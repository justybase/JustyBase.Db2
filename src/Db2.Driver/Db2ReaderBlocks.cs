using System.Buffers.Binary;
using JustyBase.Db2.Protocol;

namespace JustyBase.Db2;

public sealed partial class Db2Session
{
    private string? _lastReaderSql;
    private Db2ResultMetadataCache? _lastReaderMetadata;
    private Db2ResultMetadataCache GetReaderMetadataCache(string sql, Db2Statement? statement)
    {
        if (statement is not null) return statement.ResultMetadataCache ??= new Db2ResultMetadataCache();
        if (_lastReaderMetadata is null || _lastReaderSql != sql)
        { _lastReaderSql = sql; _lastReaderMetadata = new Db2ResultMetadataCache(); }
        return _lastReaderMetadata;
    }

    private async ValueTask<(List<object?[]> Rows, bool GotData)> ReadStreamingRowsAsync(QueryStreamState state, CancellationToken token)
    {
        if (!state.UseBufferedRows || state.HasLobFields)
        {
            var owned = await ReadReplyChainAsync(token).ConfigureAwait(false);
            var gotData = false;
            foreach (var reply in owned) if ((CodePoint)reply.CodePoint is CodePoint.QRYDTA or CodePoint.EXTDTA) gotData = true;
            return (ConsumeStreamingReplies(owned, state), gotData);
        }
        var chain = await ReadBorrowedRepliesAsync(token).ConfigureAwait(false);
        using (chain)
        {
            FieldDescriptor[]? preview = null;
            var useCompatibility = state.HasLobFields;
            if (!useCompatibility)
            {
                try
                {
                    for (var i = 0; i < chain.Count; i++)
                        if ((CodePoint)chain[i].CodePoint == CodePoint.QRYDSC)
                            preview = state.MetadataCache?.ReadFields(chain[i].Payload.Span) ?? DdmCodec.ParseQueryDescriptor(chain[i].Payload.Span);
                    if (preview is not null)
                        foreach (var field in preview) if (IsLobType(field.Type)) useCompatibility = true;
                }
                catch (InvalidDataException)
                {
                    // Preserve warning/error order for malformed descriptor chains.
                    useCompatibility = true;
                }
            }
            if (useCompatibility || (preview?.Length ?? state.Fields.Length) == 0)
            {
                var owned = new DssPacket[chain.Count];
                var gotData = false;
                for (var i = 0; i < chain.Count; i++)
                {
                    owned[i] = chain[i].ToOwned();
                    if ((CodePoint)owned[i].CodePoint is CodePoint.QRYDTA or CodePoint.EXTDTA) gotData = true;
                }
                chain.Dispose(); // Owned packets no longer borrow the transport buffer.
                return (ConsumeStreamingReplies(owned, state), gotData);
            }
            return ConsumeBufferedReplies(chain, state, preview);
        }
    }

    private async ValueTask<DssReplyChain> ReadBorrowedRepliesAsync(CancellationToken token)
    {
        DssReplyChain chain;
        try
        {
            chain = await (_readBuffer ??= new DssReadBuffer(_stream)).ReadChainAsync(token).ConfigureAwait(false);
            try
            {
                for (var i = 0; i < chain.Count; i++)
                    if ((CodePoint)chain[i].CodePoint == CodePoint.DTAMCHRM)
                        throw new InvalidOperationException("DB2 server returned DTAMCHRM (SQLDTA descriptor and parameter data do not match).");
            }
            catch { chain.Dispose(); throw; }
        }
        catch { _closed = true; throw; }
        return chain;
    }

    private async ValueTask ReadDiscardRowsAsync(QueryStreamState state, CancellationToken token)
    {
        using var chain = await ReadBorrowedRepliesAsync(token).ConfigureAwait(false);
        var gotData = ConsumeBufferedStatus(chain, state, state.Fields, discarding: true);
        var discard = state.DiscardRows!;
        discard.BeginFetch();
        for (var i = 0; i < chain.Count; i++)
            if ((CodePoint)chain[i].CodePoint == CodePoint.QRYDTA)
                discard.Append(chain[i].Payload.Span);
        discard.CompleteFetch(_rowWarningReporter ??= ReportWarning);
        if (!gotData && !state.Ended)
            throw new InvalidDataException("DB2 CNTQRY returned neither query data nor an end-of-query reply.");
    }

    private (List<object?[]> Rows, bool GotData) ConsumeBufferedReplies(DssReplyChain chain, QueryStreamState state, FieldDescriptor[]? preview)
    {
        var gotData = ConsumeBufferedStatus(chain, state, preview);
        state.BufferedRows ??= new Db2BufferedRows(state.Decoders);
        state.BufferedRows.BeginFetch();
        if (state.PendingRowData.Length > 0)
        {
            state.BufferedRows.Append(state.PendingRowData);
            state.PendingRowData = [];
        }
        for (var i = 0; i < chain.Count; i++)
            if ((CodePoint)chain[i].CodePoint == CodePoint.QRYDTA) state.BufferedRows.Append(chain[i].Payload.Span);
        state.BufferedRows.Validate(_rowWarningReporter ??= ReportWarning);
        return (EmptyDecodedRows, gotData);
    }

    private bool ConsumeBufferedStatus(DssReplyChain chain, QueryStreamState state, FieldDescriptor[]? preview, bool discarding = false)
    {
        var gotData = false;
        for (var i = 0; i < chain.Count; i++)
        {
            var reply = chain[i];
            var payload = reply.Payload.Span;
            switch ((CodePoint)reply.CodePoint)
            {
                case CodePoint.SQLDARD: state.Columns = state.MetadataCache?.ReadColumns(payload) ?? DdmCodec.ParseSqlDard(payload); break;
                case CodePoint.QRYDSC:
                    if (discarding && !DdmCodec.ParseQueryDescriptor(payload).AsSpan().SequenceEqual(state.Fields))
                        throw new InvalidDataException("DB2 changed the row descriptor while discarding a query.");
                    state.Fields = preview!;
                    state.Decoders = state.MetadataCache?.Decoders ?? Db2ColumnDecoder.Create(state.Fields);
                    break;
                case CodePoint.QRYDTA: gotData = true; break;
                case CodePoint.OPNQRYRM:
                    if (DdmCodec.TryGetParameter(payload, (ushort)CodePoint.QRYINSID, out var id) && id.Length >= 8)
                        state.QueryId = BinaryPrimitives.ReadUInt64BigEndian(id);
                    state.IsOpen = true;
                    state.NextCorrelation = reply.Header.CorrelationId;
                    break;
                case CodePoint.ENDQRYRM: state.Ended = true; break;
                case CodePoint.SQLCARD:
                    var card = ParseSqlCardAndReportWarning(payload);
                    if (card.SqlCode < 0) { state.Ended = true; state.Failed = true; throw SqlError(card); }
                    if (card.SqlCode == 100) state.Ended = true;
                    break;
                case CodePoint.EXTDTA:
                    if (discarding) throw new InvalidDataException("DB2 returned external data while discarding a non-LOB query.");
                    state.ExternalData.Add(payload.ToArray()); gotData = true; break;
                case CodePoint.SQLERRRM: state.Ended = true; state.Failed = true; throw ServerError(payload.ToArray());
            }
        }
        return gotData;
    }
}
