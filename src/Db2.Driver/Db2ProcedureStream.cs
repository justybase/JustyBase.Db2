using System.Runtime.CompilerServices;
using JustyBase.Db2.Converters;
using JustyBase.Db2.Protocol;

namespace JustyBase.Db2;

internal enum Db2ProcedureStreamEventKind { ResultSet, Row }

internal sealed record Db2ProcedureStreamEvent(
    Db2ProcedureStreamEventKind Kind,
    IReadOnlyList<Db2ColumnMetadata> Columns,
    object?[]? Values = null);

public sealed partial class Db2Session
{
    /// <summary>
    /// Streams procedure cursor metadata and rows while retaining the session gate. The callback
    /// runs only after all cursors have been consumed or safely drained and the procedure commit
    /// has completed. ADO.NET uses it to publish output values when its reader is closed.
    /// </summary>
    internal IAsyncEnumerable<Db2ProcedureStreamEvent> QueryProcedureStreamAsync(
        Db2Statement statement,
        IReadOnlyList<object?> parameters,
        IReadOnlyList<Db2ParameterDirection> outputDirections,
        CancellationToken cancellationToken,
        Action<IReadOnlyList<object?>> outputsReady,
        Action<string>? protocolTrace = null) =>
        QueryProcedureStreamCoreAsync(statement, parameters, outputDirections, cancellationToken, outputsReady, protocolTrace);

    private async IAsyncEnumerable<Db2ProcedureStreamEvent> QueryProcedureStreamCoreAsync(
        Db2Statement statement,
        IReadOnlyList<object?> parameters,
        IReadOnlyList<Db2ParameterDirection> outputDirections,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Action<IReadOnlyList<object?>> outputsReady,
        Action<string>? protocolTrace)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var states = new List<ProcedureResultSetState>();
        Task<bool>? cancellationInterrupt = null;
        CancellationTokenRegistration cancellationRegistration = default;
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var commandToken = commandCancellation.Token;
        using var operationTimeout = new CancellationTokenSource();
        var cleanupTimeout = TimeSpan.FromSeconds(30);
        var outputParameters = new object?[parameters.Count];
        var completedCursorCount = 0;
        var completed = false;
        long rowsAffected = 0;

        try
        {
            Task<bool>? pendingInterrupt;
            lock (_interruptGate) pendingInterrupt = _interruptPending;
            if (pendingInterrupt is not null)
            {
                try { await pendingInterrupt.WaitAsync(commandToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (commandToken.IsCancellationRequested) { throw; }
                catch { }
            }

            Volatile.Write(ref _operationActive, 1);
            cancellationRegistration = commandToken.Register(() =>
            {
                if (Volatile.Read(ref _operationActive) == 0) return;
                operationTimeout.CancelAfter(cleanupTimeout);
                try { cancellationInterrupt = InterruptAsync(cleanupTimeout); }
                catch { }
            });

            EnsureStatement(statement);
            if (parameters.Count != statement.ParameterColumns.Count)
                throw new ArgumentException($"DB2 expected {statement.ParameterColumns.Count} parameters, got {parameters.Count}.", nameof(parameters));
            if (outputDirections.Count != parameters.Count)
                throw new ArgumentException("Output parameter directions must match the prepared statement parameter count.", nameof(outputDirections));

            var encoded = parameters.Count == 0 ? null : SqlDtaEncoder.BuildWithExternalData(statement.ParameterColumns, parameters);
            _correlationId = 1;
            await SendAsync(DdmCodec.PackExecuteSqlStatement(
                PackageId, ConsistencyToken, SectionNumber, _database,
                maximumResultSets: ushort.MaxValue, queryBlockSize: _blockSize,
                queryRowsetSize: null,
                outputExpected: outputDirections.Any(direction => direction != Db2ParameterDirection.Input)),
                encoded is not null, encoded is null, commandToken).ConfigureAwait(false);
            if (encoded is not null)
            {
                await SendAsync(encoded.SqlDta, encoded.ExternalData.Count > 0, encoded.ExternalData.Count == 0, commandToken).ConfigureAwait(false);
                for (var index = 0; index < encoded.ExternalData.Count; index++)
                    await SendExternalDataAsync(encoded.ExternalData[index], index < encoded.ExternalData.Count - 1, index == encoded.ExternalData.Count - 1, commandToken).ConfigureAwait(false);
            }

            byte[]? outputSqlDtaReply = null;
            var outputExternalData = new List<byte[]>();
            var sections = Array.Empty<ResultSetPackageSection>();
            var declaredCount = -1;
            ProcedureResultSetState? current = null;
            IReadOnlyList<Db2ColumnMetadata>? pendingColumns = null;
            var seenInitialQrydta = false;
            var initialReplies = await ReadReplyChainAsync(commandToken).ConfigureAwait(false);
            foreach (var reply in initialReplies)
            {
                var replyCode = (CodePoint)reply.CodePoint;
                var replyCursor = replyCode is CodePoint.RSLSETRM or CodePoint.SQLRSLRD or CodePoint.SQLDTARD
                    ? 0
                    : replyCode == CodePoint.OPNQRYRM || current is null
                        ? states.Count + 1
                        : states.Count;
                TraceProcedureReply(protocolTrace, "initial", reply, replyCursor);
                switch ((CodePoint)reply.CodePoint)
                {
                    case CodePoint.RSLSETRM:
                        sections = DdmCodec.ParseResultSetSections(reply.Payload);
                        break;
                    case CodePoint.SQLRSLRD:
                        declaredCount = DdmCodec.ParseSqlResultSetCount(reply.Payload);
                        break;
                    case CodePoint.SQLDTARD:
                        outputSqlDtaReply = reply.Payload;
                        break;
                    case CodePoint.OPNQRYRM:
                        if (states.Count >= sections.Length)
                            throw new InvalidDataException("DB2 returned more procedure cursors than RSLSETRM declared.");
                        current = new ProcedureResultSetState(sections[states.Count]) { NextCorrelation = reply.Header.CorrelationId };
                        if (pendingColumns is not null)
                        {
                            current.Columns = pendingColumns;
                            pendingColumns = null;
                        }
                        var openValues = DdmCodec.ParseParameters(reply.Payload);
                        if (openValues.TryGetValue((ushort)CodePoint.QRYINSID, out var queryId) && queryId.Length >= 8)
                            current.QueryId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(queryId);
                        states.Add(current);
                        break;
                    case CodePoint.SQLCINRD:
                        if (current is null) throw new InvalidDataException("DB2 returned SQLCINRD without an open procedure cursor.");
                        var cursorColumns = ParseSqlCursorInfoWithTrace(protocolTrace, "initial", states.Count, reply.Payload);
                        current.Columns = DdmCodec.MergeCursorColumnNames(cursorColumns, current.Columns);
                        TraceProcedureColumns(protocolTrace, "SQLCINRD", states.Count, current.Columns);
                        break;
                    case CodePoint.SQLDARD:
                        var described = DdmCodec.ParseSqlDard(reply.Payload, littleEndian: true, synthesizeUnnamedNames: false);
                        if (described.Length > 0)
                        {
                            if (current is null) pendingColumns = described;
                            else current.Columns = DdmCodec.MergeCursorColumnNames(current.Columns, described);
                        }
                        TraceProcedureColumns(protocolTrace, "SQLDARD", current is null ? states.Count + 1 : states.Count, described);
                        break;
                    case CodePoint.QRYDSC:
                        if (current is null) throw new InvalidDataException("DB2 returned QRYDSC without an open procedure cursor.");
                        current.Fields = DdmCodec.ParseQueryDescriptor(reply.Payload);
                        protocolTrace?.Invoke($"procedure descriptor QRYDSC cursor={states.Count} columns={current.Fields.Length}");
                        break;
                    case CodePoint.QRYDTA:
                        if (current is null) throw new InvalidDataException("DB2 returned QRYDTA without an open procedure cursor.");
                        current.RowData.Write(reply.Payload);
                        current.SeenQueryData = true;
                        seenInitialQrydta = true;
                        break;
                    case CodePoint.EXTDTA:
                        if (outputDirections.Any(direction => direction != Db2ParameterDirection.Input) && !seenInitialQrydta)
                            outputExternalData.Add(reply.Payload);
                        else if (current is null)
                            throw new InvalidDataException("DB2 returned EXTDTA without an open procedure cursor or output parameters.");
                        else current.ExternalData.Add(reply.Payload);
                        break;
                    case CodePoint.ENDQRYRM:
                        if (current is not null) current.Ended = true;
                        break;
                    case CodePoint.SQLCARD:
                        var card = ParseSqlCardAndReportWarning(reply.Payload);
                        if (card.SqlCode < 0 || card.SqlCode == 464) throw SqlError(card);
                        rowsAffected += card.RowsAffected;
                        if (card.SqlCode == 100 && current is not null) current.Ended = true;
                        break;
                    case CodePoint.SQLERRRM:
                        throw ServerError(reply.Payload);
                }
            }

            if (outputDirections.Any(direction => direction != Db2ParameterDirection.Input))
            {
                if (outputSqlDtaReply is not null)
                    ApplyOutputParameterReply(outputSqlDtaReply, outputDirections, outputParameters, outputExternalData);
                else if (outputExternalData.Count != 0)
                    throw new InvalidDataException("DB2 returned output-parameter EXTDTA without SQLDTARD metadata.");
                else
                    EnsureOutputParameterDataReturned(outputDirections, dataReturned: false);
            }
            if (sections.Length > 0 && declaredCount != sections.Length || states.Count != sections.Length)
                throw new InvalidDataException($"DB2 declared {declaredCount} procedure result sets, returned {sections.Length} package sections and opened {states.Count} cursors.");

            // A procedure with no returned cursor is exposed as one empty ADO result.
            if (states.Count == 0)
                yield return new Db2ProcedureStreamEvent(Db2ProcedureStreamEventKind.ResultSet, Array.Empty<Db2ColumnMetadata>());

            for (var index = 0; index < states.Count; index++)
            {
                var state = states[index];
                if (state.QueryId == 0) throw new InvalidDataException("DB2 left a procedure cursor open without its query instance ID.");
                if (state.Fields.Length > 0 && state.Columns.Count != state.Fields.Length)
                    throw new InvalidDataException($"DB2 returned {state.Fields.Length} procedure result columns in QRYDSC but {state.Columns.Count} in SQLCINRD metadata.");
                if (state.Columns.Any(column => column.Name.Length == 0) && state.Fields.Length > 0)
                    await DescribeProcedureCursorColumnsAsync(state, index + 1, commandToken, protocolTrace).ConfigureAwait(false);

                yield return new Db2ProcedureStreamEvent(Db2ProcedureStreamEventKind.ResultSet, state.Columns);

                foreach (var row in await DecodeProcedureCursorBlockAsync(state, commandToken).ConfigureAwait(false))
                    yield return new Db2ProcedureStreamEvent(Db2ProcedureStreamEventKind.Row, state.Columns, row);

                var fetchCount = 0;
                var shortFinalBlock = false;
                while (!state.Ended && !shortFinalBlock)
                {
                    if (++fetchCount > 65_535) throw new InvalidDataException("DB2 procedure cursor exceeded the maximum supported number of fetch blocks.");
                    await SendAsync(DdmCodec.PackContinueProcedureResultSet(state.PackageSection, _blockSize, state.QueryId), false, true, commandToken, state.NextCorrelation).ConfigureAwait(false);
                    var replies = await ReadReplyChainAsync(commandToken).ConfigureAwait(false);
                    TraceProcedureReplies(protocolTrace, "fetch", replies, index + 1);
                    ConsumeProcedureCursorReplies(replies, state);
                    var blockRows = await DecodeProcedureCursorBlockAsync(state, commandToken).ConfigureAwait(false);
                    foreach (var row in blockRows)
                        yield return new Db2ProcedureStreamEvent(Db2ProcedureStreamEventKind.Row, state.Columns, row);
                    if (state.Ended) break;
                    var lastData = replies.LastOrDefault(reply => (CodePoint)reply.CodePoint == CodePoint.QRYDTA);
                    if (lastData is null) throw new InvalidDataException("DB2 CNTQRY returned no query data or end-of-query reply for a procedure cursor.");
                    shortFinalBlock = lastData.Payload.Length < MaximumProcedureQueryDataPayload(_blockSize);
                }

                // Even after a short final block, the procedure cursor is explicitly closed before
                // the next package section is fetched, matching observed Db2 LUW CLI behavior.
                await SendAsync(DdmCodec.PackCloseProcedureResultSet(state.PackageSection, state.QueryId), false, true, commandToken, state.NextCorrelation).ConfigureAwait(false);
                var closeReplies = await ReadReplyChainAsync(commandToken).ConfigureAwait(false);
                TraceProcedureReplies(protocolTrace, "close", closeReplies, index + 1);
                ConsumeProcedureCursorReplies(closeReplies, state);
                foreach (var row in await DecodeProcedureCursorBlockAsync(state, commandToken).ConfigureAwait(false))
                    yield return new Db2ProcedureStreamEvent(Db2ProcedureStreamEventKind.Row, state.Columns, row);
                state.Ended = true;
                state.Closed = true;
                completedCursorCount = index + 1;
            }

            if (AutoCommit) await CommitProcedureCallLockedAsync(commandToken).ConfigureAwait(false);
            outputsReady(outputParameters);
            completed = true;
        }
        finally
        {
            cancellationRegistration.Dispose();
            try
            {
                if (!completed && commandToken.IsCancellationRequested)
                {
                    _closed = true;
                    _client.Dispose();
                }
                else if (!completed)
                {
                    using var drainTimeout = new CancellationTokenSource(cleanupTimeout);
                    for (var index = completedCursorCount; index < states.Count; index++)
                        await DrainProcedureCursorAsync(states[index], drainTimeout.Token).ConfigureAwait(false);
                    if (AutoCommit) await CommitProcedureCallLockedAsync(drainTimeout.Token).ConfigureAwait(false);
                    outputsReady(outputParameters);
                }

                if (cancellationInterrupt is not null)
                {
                    try
                    {
                        if (!await cancellationInterrupt.WaitAsync(cleanupTimeout).ConfigureAwait(false))
                            throw new InvalidDataException("DB2 did not acknowledge SQLINTR.");
                    }
                    catch (Exception error)
                    {
                        _closed = true;
                        _client.Dispose();
                        throw new Db2ConnectionClosedException($"DB2 SQLINTR could not be confirmed; the session was discarded. {error.Message}");
                    }
                }
                // Cancellation that arrives after Db2 has completed the CALL is a lost race.
                // Do not turn a successful cursor drain/commit into a cancellation error while
                // the consumer is disposing the already-completed async iterator.
                if (!completed && commandToken.IsCancellationRequested)
                    throw new OperationCanceledException("The DB2 procedure read was canceled.", null, cancellationToken);
            }
            catch (Exception error)
            {
                _closed = true;
                _client.Dispose();
                if (completed) throw;
                if (error is OperationCanceledException) throw;
                throw new Db2ConnectionClosedException($"Procedure cursor cleanup failed; the session was discarded. {error.Message}");
            }
            finally
            {
                foreach (var state in states) state.RowData.Dispose();
                Volatile.Write(ref _operationActive, 0);
                _gate.Release();
            }
        }
    }

    private void TraceProcedureReplies(Action<string>? trace, string phase, IReadOnlyList<DssPacket> replies, int cursor)
    {
        if (trace is null) return;
        foreach (var reply in replies)
        {
            TraceProcedureReply(trace, phase, reply, cursor);
            switch ((CodePoint)reply.CodePoint)
            {
                case CodePoint.SQLCINRD:
                    _ = ParseSqlCursorInfoWithTrace(trace, phase, cursor, reply.Payload);
                    break;
                case CodePoint.SQLDARD:
                    var diagnostics = new List<(int Name, int Label, bool NameMatch, bool LabelMatch)>();
                    var described = DdmCodec.ParseSqlDard(reply.Payload, littleEndian: true, synthesizeUnnamedNames: false,
                        expectedAlias: "PAYLOAD", nameDiagnostics: (nameLength, labelLength, nameMatch, labelMatch) =>
                            diagnostics.Add((nameLength, labelLength, nameMatch, labelMatch)));
                    TraceProcedureColumns(trace, "SQLDARD", cursor, described);
                    var lengths = string.Join(',', diagnostics.Select(item => $"{item.Name}/{item.Label}"));
                    var matches = string.Join(',', diagnostics.Select(item => $"{item.NameMatch}/{item.LabelMatch}"));
                    trace($"procedure descriptor source=SQLDARD phase={phase} cursor={cursor} nameLabelChars=[{lengths}] expectedPAYLOADNameLabelMatches=[{matches}]");
                    TraceExpectedAlias(trace, "SQLDARD", cursor, reply.Payload, described);
                    break;
                case CodePoint.QRYDSC:
                    trace($"procedure descriptor phase={phase} codepoint=QRYDSC cursor={cursor} columns={DdmCodec.ParseQueryDescriptor(reply.Payload).Length}");
                    break;
            }
        }
    }

    private static void TraceProcedureReply(Action<string>? trace, string phase, DssPacket reply, int cursor)
    {
        trace?.Invoke($"procedure reply phase={phase} cursor={cursor} codepoint={(CodePoint)reply.CodePoint} payloadBytes={reply.Payload.Length} correlation={reply.Header.CorrelationId}");
    }

    private static void TraceProcedureColumns(Action<string>? trace, string source, int cursor, IReadOnlyList<Db2ColumnMetadata> columns)
    {
        if (trace is null) return;
        var nameLengths = string.Join(',', columns.Select(column => column.Name.Length));
        trace($"procedure descriptor source={source} cursor={cursor} columns={columns.Count} nameChars=[{nameLengths}]");
    }

    private Db2ColumnMetadata[] ParseSqlCursorInfoWithTrace(Action<string>? trace, string phase, int cursor, byte[] payload)
    {
        if (trace is null) return DdmCodec.ParseSqlCursorInfo(payload, _sqlamLevel);
        var diagnostics = new List<(int Name, int Label, bool NameMatch, bool LabelMatch)>();
        var offsets = "legacy-parser";
        var columns = DdmCodec.ParseSqlCursorInfo(payload, _sqlamLevel, "PAYLOAD", (nameLength, labelLength, nameMatch, labelMatch) =>
            diagnostics.Add((nameLength, labelLength, nameMatch, labelMatch)), value => offsets = value);
        var lengths = string.Join(',', diagnostics.Select(item => $"{item.Name}/{item.Label}"));
        var matches = string.Join(',', diagnostics.Select(item => $"{item.NameMatch}/{item.LabelMatch}"));
        trace($"procedure descriptor source=SQLCINRD phase={phase} cursor={cursor} columns={columns.Length} nameLabelChars=[{lengths}] expectedPAYLOADNameLabelMatches=[{matches}]");
        trace($"procedure SQLCINRD parser offsets phase={phase} cursor={cursor} {offsets}");
        TraceExpectedAlias(trace, "SQLCINRD", cursor, payload, columns);
        return columns;
    }

    private static void TraceExpectedAlias(
        Action<string>? trace,
        string source,
        int cursor,
        byte[] payload,
        IReadOnlyList<Db2ColumnMetadata> columns)
    {
        if (trace is null) return;
        var aliasBytes = System.Text.Encoding.UTF8.GetBytes("PAYLOAD");
        var offset = payload.AsSpan().IndexOf(aliasBytes);
        var matches = string.Join(',', columns.Select(column => StringComparer.Ordinal.Equals(column.Name, "PAYLOAD")));
        trace($"procedure alias diagnostic source={source} cursor={cursor} expected=PAYLOAD bytesPresent={offset >= 0} byteOffset={offset} parsedNameMatches=[{matches}]");
        if (source == "SQLCINRD")
        {
            var window = DdmCodec.FormatRedactedDescriptorWindow(payload, "PAYLOAD", 112, 176);
            trace($"procedure alias redactedWindow source={source} cursor={cursor} bytes=[112..176) tokens={window}");
        }
    }

    private async Task DescribeProcedureCursorColumnsAsync(
        ProcedureResultSetState state,
        int cursor,
        CancellationToken cancellationToken,
        Action<string>? protocolTrace)
    {
        protocolTrace?.Invoke($"procedure request phase=describe codepoint=DSCSQLSTT cursor={cursor} section={state.PackageSection.SectionNumber}");
        await SendAsync(DdmCodec.PackDescribeSqlStatement(state.PackageSection), false, true, cancellationToken, state.NextCorrelation).ConfigureAwait(false);
        var replies = await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false);
        TraceProcedureReplies(protocolTrace, "describe", replies, cursor);
        foreach (var reply in replies)
        {
            switch ((CodePoint)reply.CodePoint)
            {
                case CodePoint.SQLDARD:
                    var described = DdmCodec.ParseSqlDard(reply.Payload, littleEndian: true, synthesizeUnnamedNames: false);
                    if (described.Length > 0)
                        state.Columns = DdmCodec.MergeCursorColumnNames(state.Columns, described);
                    break;
                case CodePoint.SQLCARD:
                    var card = ParseSqlCardAndReportWarning(reply.Payload);
                    if (card.SqlCode < 0) throw SqlError(card);
                    break;
                case CodePoint.SQLERRRM:
                    throw ServerError(reply.Payload);
            }
        }
    }

    private async Task<List<object?[]>> DecodeProcedureCursorBlockAsync(ProcedureResultSetState state, CancellationToken cancellationToken)
    {
        var pending = state.RowData;
        var (decoded, consumed) = DecodeRowsWithWarnings(state.Fields, pending.Data);
        pending.Consume(consumed);
        var rows = decoded.Select(row => row.Values).ToList();
        foreach (var row in decoded) ReportWarning(row.Warning);
        var used = StitchExtdta(state.Fields, rows, state.ExternalData);
        if (used > 0) state.ExternalData.RemoveRange(0, used);
        await ResolveProgressiveLobsAsync(rows, state.Fields, state.QueryId, state.NextCorrelation, cancellationToken).ConfigureAwait(false);
        if ((state.Ended || state.Closed) && (pending.PendingBytes != 0 || state.ExternalData.Count != 0 || HasUnresolvedExternalLocator(state.Fields, rows)))
            throw new InvalidDataException("DB2 ended a procedure cursor with unresolved row or LOB data.");
        if (state.Ended || state.Closed) pending.Dispose();
        return rows;
    }

    private async Task DrainProcedureCursorAsync(ProcedureResultSetState state, CancellationToken cancellationToken)
    {
        if (state.Closed) return;
        var fetchCount = 0;
        var shortFinalBlock = false;
        while (!state.Ended && !shortFinalBlock)
        {
            if (++fetchCount > 65_535) throw new InvalidDataException("DB2 procedure cursor exceeded the maximum supported number of drain blocks.");
            await SendAsync(DdmCodec.PackContinueProcedureResultSet(state.PackageSection, _blockSize, state.QueryId), false, true, cancellationToken, state.NextCorrelation).ConfigureAwait(false);
            var replies = await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false);
            var lastData = replies.LastOrDefault(reply => (CodePoint)reply.CodePoint == CodePoint.QRYDTA);
            ConsumeProcedureCursorRepliesForDrain(replies, state);
            if (state.Ended) break;
            if (lastData is null) throw new InvalidDataException("DB2 procedure cursor drain received neither rows nor a terminal response.");
            shortFinalBlock = lastData.Payload.Length < MaximumProcedureQueryDataPayload(_blockSize);
        }
        await SendAsync(DdmCodec.PackCloseProcedureResultSet(state.PackageSection, state.QueryId), false, true, cancellationToken, state.NextCorrelation).ConfigureAwait(false);
        ConsumeProcedureCursorRepliesForDrain(await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false), state);
        state.Ended = true;
        state.Closed = true;
    }

    private void ConsumeProcedureCursorRepliesForDrain(IReadOnlyList<DssPacket> replies, ProcedureResultSetState state)
    {
        foreach (var reply in replies)
        {
            switch ((CodePoint)reply.CodePoint)
            {
                case CodePoint.OPNQRYRM:
                    state.NextCorrelation = reply.Header.CorrelationId;
                    break;
                case CodePoint.ENDQRYRM:
                    state.Ended = true;
                    break;
                case CodePoint.SQLCARD:
                    var card = ParseSqlCardAndReportWarning(reply.Payload);
                    if (card.SqlCode < 0 || card.SqlCode == 464) throw SqlError(card);
                    if (card.SqlCode == 100) state.Ended = true;
                    break;
                case CodePoint.SQLERRRM:
                    throw ServerError(reply.Payload);
            }
        }
    }
}
