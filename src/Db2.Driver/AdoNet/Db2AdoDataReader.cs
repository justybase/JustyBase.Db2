using System.Buffers;
using System.Buffers.Binary;
using JustyBase.Db2.Converters;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;

namespace JustyBase.Db2.AdoNet;

/// <summary>DbDataReader over one incremental SQL result or materialized procedure result sets.</summary>
public sealed class Db2AdoDataReader : DbDataReader
{
    private static readonly Task<bool> CompletedTrue = Task.FromResult(true);
    private static readonly Task<bool> CompletedFalse = Task.FromResult(false);
    private readonly Db2AdoCommand _command;
    private readonly Db2AdoConnection _connection;
    private readonly CommandBehavior _behavior;
    private readonly Func<Exception, Exception> _translateError;
    private readonly CancellationTokenSource? _operation;
    private readonly IReadOnlyList<Db2ResultSet>? _resultSets;
    private Db2ColumnMetadata[] _currentColumns = [];
    private Dictionary<string, int>? _exactOrdinals;
    private Dictionary<string, int>? _ignoreCaseOrdinals;
    private IAsyncEnumerator<Db2StreamRow>? _stream;
    private IAsyncEnumerator<Db2ReaderBlock>? _blocks;
    private Db2BufferedRows? _bufferedRows;
    private object?[]? _rawCache;
    private int[]? _rawCacheGenerations;
    private int _rowGeneration;
    private bool _pendingBlock;
    private IAsyncEnumerator<Db2ProcedureStreamEvent>? _procedureStream;
    private Db2ProcedureStreamEvent? _procedureBoundary;
    private bool _procedureAtEnd;
    private readonly Action? _procedureOutputsOnClose;
    private int _resultSetIndex;
    private int _rowIndex = -1;
    private object?[]? _currentValues;
    private object?[]? _pendingValues;
    private bool _initialized;
    private bool _hasRows;
    private bool _streamEnded;
    private bool _singleRowReturned;
    private bool _noMoreResults;
    private int _closed;

    internal Db2AdoDataReader(
        Db2AdoCommand command,
        Db2AdoConnection connection,
        IReadOnlyList<Db2ResultSet> resultSets,
        int recordsAffected,
        CommandBehavior behavior,
        Func<Exception, Exception> translateError,
        CancellationTokenSource? operation = null)
    {
        _command = command;
        _connection = connection;
        _resultSets = resultSets;
        SetCurrentColumns(resultSets.Count > 0 ? resultSets[0].Columns : Array.Empty<Db2ColumnMetadata>());
        RecordsAffected = recordsAffected;
        _behavior = behavior;
        _translateError = translateError;
        _operation = operation;
        RecordsAffected = -1;
        _hasRows = resultSets.Count > 0 && resultSets[0].Rows.Count > 0;
        _initialized = true;
    }

    internal Db2AdoDataReader(
        Db2AdoCommand command,
        Db2AdoConnection connection,
        IAsyncEnumerator<Db2ProcedureStreamEvent> procedureStream,
        CommandBehavior behavior,
        Func<Exception, Exception> translateError,
        CancellationTokenSource operation,
        Action procedureOutputsOnClose)
    {
        _command = command;
        _connection = connection;
        _procedureStream = procedureStream;
        SetCurrentColumns(Array.Empty<Db2ColumnMetadata>());
        _behavior = behavior;
        _translateError = translateError;
        _operation = operation;
        _procedureOutputsOnClose = procedureOutputsOnClose;
        RecordsAffected = -1;
    }

    internal Db2AdoDataReader(
        Db2AdoCommand command,
        Db2AdoConnection connection,
        IReadOnlyList<Db2ColumnMetadata> columns,
        IAsyncEnumerator<Db2StreamRow> stream,
        CommandBehavior behavior,
        Func<Exception, Exception> translateError,
        CancellationTokenSource operation)
    {
        _command = command;
        _connection = connection;
        SetCurrentColumns(columns);
        _stream = stream;
        _behavior = behavior;
        _translateError = translateError;
        _operation = operation;
        RecordsAffected = -1;
    }

    internal Db2AdoDataReader(Db2AdoCommand command, Db2AdoConnection connection,
        IReadOnlyList<Db2ColumnMetadata> columns, IAsyncEnumerator<Db2ReaderBlock> blocks,
        CommandBehavior behavior, Func<Exception, Exception> translateError, CancellationTokenSource operation)
        : this(command, connection, columns, (IAsyncEnumerator<Db2StreamRow>)null!, behavior, translateError, operation)
    { _blocks = blocks; }

    internal void SetStreamMetadata(IReadOnlyList<Db2ColumnMetadata> columns) => SetCurrentColumns(columns);

    public override int Depth => 0;
    public override int FieldCount => _currentColumns.Length;
    public override int VisibleFieldCount => FieldCount;
    public override bool HasRows => _hasRows;
    public override bool IsClosed => Volatile.Read(ref _closed) != 0;
    public override int RecordsAffected { get; }
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    internal async Task InitializeAsync()
    {
        if (_initialized || IsClosed) return;
        _initialized = true;
        if (_procedureStream is not null)
        {
            try
            {
                if (!await _procedureStream.MoveNextAsync().ConfigureAwait(false))
                    throw new InvalidDataException("DB2 procedure stream ended before it exposed its first result set.");
                var first = _procedureStream.Current;
                if (first.Kind != Db2ProcedureStreamEventKind.ResultSet)
                    throw new InvalidDataException("DB2 procedure stream returned a row before its result-set metadata.");
                SetCurrentColumns(first.Columns);
                await PrimeProcedureResultSetAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception error)
            {
                await ClosePreservingReadErrorAsync(error).ConfigureAwait(false);
                throw TranslateReadError(error);
            }
        }
        if (_blocks is not null)
        {
            try
            {
                if (await _blocks.MoveNextAsync().ConfigureAwait(false))
                {
                    var block = _blocks.Current;
                    _bufferedRows = block.Rows;
                    _pendingBlock = block.Rows is not null;
                    _pendingValues = block.Materialized?.Values;
                    _hasRows = true;
                }
                else _streamEnded = true;
                return;
            }
            catch (Exception error)
            {
                await ClosePreservingReadErrorAsync(error).ConfigureAwait(false);
                throw TranslateReadError(error);
            }
        }
        if (_stream is null) return;
        try
        {
            if (await _stream.MoveNextAsync().ConfigureAwait(false))
            {
                var row = _stream.Current;
                _pendingValues = row.Values;
                _hasRows = true;
            }
            else _streamEnded = true;
        }
        catch (Exception error)
        {
            await ClosePreservingReadErrorAsync(error).ConfigureAwait(false);
            throw TranslateReadError(error);
        }
    }

    public override bool Read()
    {
        if (_initialized && !IsClosed && !_noMoreResults && _pendingBlock && _bufferedRows!.TryRead())
        {
            _pendingBlock = false; AdvanceBufferedRow(); _singleRowReturned = true;
            return true;
        }
        if (_initialized && !IsClosed && !_noMoreResults && _blocks is not null &&
            (_behavior & CommandBehavior.SingleRow) == 0 && _operation?.IsCancellationRequested != true &&
            _bufferedRows?.TryRead() == true)
        {
            AdvanceBufferedRow();
            _singleRowReturned = true;
            return true;
        }
        var read = ReadAsync(CancellationToken.None);
        return read.IsCompletedSuccessfully ? read.Result : read.GetAwaiter().GetResult();
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (IsClosed || _noMoreResults) return CompletedFalse;
        if (!_initialized) return InitializeAndReadAsync(cancellationToken);
        if (_streamEnded && _resultSets is null) return CompletedFalse;
        if (_procedureStream is not null) return ReadProcedureAsync(cancellationToken);
        if (_resultSets is not null) return ReadMaterialized();
        if (_blocks is not null) return ReadBlocks(cancellationToken);
        return ReadStreaming(cancellationToken);
    }

    private async Task<bool> InitializeAndReadAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync().ConfigureAwait(false);
        return await ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> ReadMaterialized()
    {
        if ((_behavior & CommandBehavior.SingleRow) != 0 && _singleRowReturned)
        {
            _currentValues = null;
            return FinishReadAsync();
        }
        if ((uint)_resultSetIndex >= (uint)_resultSets!.Count) return CompletedFalse;
        var rows = _resultSets[_resultSetIndex].Rows;
        var next = _rowIndex + 1;
        if (next >= rows.Count)
        {
            _currentValues = null;
            return FinishReadAsync();
        }
        _rowIndex = next;
        _currentValues = rows[next];
        _singleRowReturned = true;
        return CompletedTrue;
    }

    private Task<bool> ReadProcedureAsync(CancellationToken cancellationToken)
    {
        if ((_behavior & CommandBehavior.SingleRow) != 0 && _singleRowReturned)
        {
            _noMoreResults = true;
            return DrainProcedureAndFinishAsync();
        }
        if (_pendingValues is not null)
        {
            _currentValues = _pendingValues;
            _pendingValues = null;
            _singleRowReturned = true;
            return CompletedTrue;
        }
        if (_procedureBoundary is not null || _procedureAtEnd)
        {
            _currentValues = null;
            return CompletedFalse;
        }
        if (cancellationToken.IsCancellationRequested) _command.Cancel();

        ValueTask<bool> moveNext;
        try { moveNext = _procedureStream!.MoveNextAsync(); }
        catch (Exception error) { return CloseAfterReadErrorAsync(error); }
        if (moveNext.IsCompletedSuccessfully)
        {
            try { return ProcessProcedureMove(moveNext.Result); }
            catch (Exception error) { return CloseAfterReadErrorAsync(error); }
        }
        return ReadProcedureSlowAsync(moveNext, cancellationToken);
    }

    private Task<bool> ProcessProcedureMove(bool hasValue)
    {
        if (!hasValue)
        {
            _procedureAtEnd = true;
            _currentValues = null;
            return CompletedFalse;
        }
        var next = _procedureStream!.Current;
        if (next.Kind == Db2ProcedureStreamEventKind.ResultSet)
        {
            _procedureBoundary = next;
            _currentValues = null;
            return CompletedFalse;
        }
        _currentValues = next.Values;
        _singleRowReturned = true;
        return CompletedTrue;
    }

    private async Task<bool> ReadProcedureSlowAsync(ValueTask<bool> moveNext, CancellationToken cancellationToken)
    {
        using var cancellationRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(_command.Cancel)
            : default;
        try { return await ProcessProcedureMove(await moveNext.ConfigureAwait(false)).ConfigureAwait(false); }
        catch (Exception error)
        {
            await ClosePreservingReadErrorAsync(error).ConfigureAwait(false);
            throw TranslateReadError(error);
        }
    }

    private Task<bool> ReadStreaming(CancellationToken cancellationToken)
    {
        if ((_behavior & CommandBehavior.SingleRow) != 0 && _singleRowReturned) return FinishReadAsync();
        if (_pendingValues is not null)
        {
            _currentValues = _pendingValues;
            _pendingValues = null;
            _singleRowReturned = true;
            return CompletedTrue;
        }
        if (_stream is null) return CompletedFalse;
        if (cancellationToken.IsCancellationRequested) _command.Cancel();

        ValueTask<bool> moveNext;
        try { moveNext = _stream.MoveNextAsync(); }
        catch (Exception error) { return CloseAfterReadErrorAsync(error); }
        if (moveNext.IsCompletedSuccessfully)
        {
            if (!moveNext.Result) return CompleteStreamReadAsync();
            _currentValues = _stream.Current.Values;
            _singleRowReturned = true;
            return CompletedTrue;
        }
        return ReadStreamingSlowAsync(moveNext, cancellationToken);
    }

    private async Task<bool> ReadStreamingSlowAsync(ValueTask<bool> moveNext, CancellationToken cancellationToken)
    {
        using var cancellationRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(_command.Cancel)
            : default;
        try
        {
            if (!await moveNext.ConfigureAwait(false)) return await CompleteStreamReadAsync().ConfigureAwait(false);
            _currentValues = _stream!.Current.Values;
            _singleRowReturned = true;
            return true;
        }
        catch (Exception error)
        {
            await ClosePreservingReadErrorAsync(error).ConfigureAwait(false);
            throw TranslateReadError(error);
        }
    }

    private Task<bool> ReadBlocks(CancellationToken token)
    {
        if ((_behavior & CommandBehavior.SingleRow) != 0 && _singleRowReturned)
        { _noMoreResults = true; return FinishReadAsync(); }
        if (_pendingValues is not null)
        {
            _currentValues = _pendingValues; _pendingValues = null; _singleRowReturned = true;
            return CompletedTrue;
        }
        if (_pendingBlock)
        {
            _pendingBlock = false;
            if (!_bufferedRows!.TryRead()) return CloseAfterReadErrorAsync(new InvalidDataException("DB2 exposed an empty reader block."));
            AdvanceBufferedRow(); _singleRowReturned = true; return CompletedTrue;
        }
        if (token.IsCancellationRequested) _command.Cancel();
        if (_operation?.IsCancellationRequested == true)
            return CloseAfterReadErrorAsync(new OperationCanceledException(token.IsCancellationRequested ? token : _operation.Token));
        try
        {
            if (_bufferedRows?.TryRead() == true)
            { AdvanceBufferedRow(); _singleRowReturned = true; return CompletedTrue; }
            var next = _blocks!.MoveNextAsync();
            return next.IsCompletedSuccessfully ? ProcessBlock(next.Result) : ReadBlockSlowAsync(next, token);
        }
        catch (Exception error) { return CloseAfterReadErrorAsync(error); }
    }

    private Task<bool> ProcessBlock(bool available)
    {
        if (!available) return CompleteStreamReadAsync();
        var block = _blocks!.Current;
        _bufferedRows = block.Rows;
        _currentValues = block.Materialized?.Values;
        if (_bufferedRows is not null && !_bufferedRows.TryRead())
            throw new InvalidDataException("DB2 exposed an empty reader block.");
        AdvanceBufferedRow();
        _singleRowReturned = true;
        return CompletedTrue;
    }

    private async Task<bool> ReadBlockSlowAsync(ValueTask<bool> next, CancellationToken token)
    {
        using var registration = token.CanBeCanceled ? token.Register(static state => ((Db2AdoCommand)state!).Cancel(), _command) : default;
        try { return await ProcessBlock(await next.ConfigureAwait(false)).ConfigureAwait(false); }
        catch (Exception error) { return await CloseAfterReadErrorAsync(error).ConfigureAwait(false); }
    }

    private async Task<bool> CompleteStreamReadAsync()
    {
        _currentValues = null;
        _streamEnded = true;
        await DisposeStreamAsync().ConfigureAwait(false);
        _command.ReaderCompleted(this, _operation);
        _connection.UnregisterReader(this);
        return false;
    }

    private async Task<bool> DrainProcedureAndFinishAsync()
    {
        await DrainProcedureStreamAsync().ConfigureAwait(false);
        await FinishStreamAsync().ConfigureAwait(false);
        return false;
    }

    private Task<bool> FinishReadAsync()
    {
        var finished = FinishStreamAsync();
        return finished.IsCompletedSuccessfully ? CompletedFalse : FinishReadSlowAsync(finished);
    }

    private static async Task<bool> FinishReadSlowAsync(Task finish)
    {
        await finish.ConfigureAwait(false);
        return false;
    }

    private async Task<bool> CloseAfterReadErrorAsync(Exception error)
    {
        await ClosePreservingReadErrorAsync(error).ConfigureAwait(false);
        throw TranslateReadError(error);
    }

    private Exception TranslateReadError(Exception error)
    {
        var translated = _translateError(error);
        if (ReferenceEquals(translated, error)) return translated;
        try
        {
            if (error.Data["Db2CleanupError"] is Exception cleanup)
                translated.Data["Db2CleanupError"] = cleanup;
        }
        catch { /* Diagnostics cannot replace the primary translated error. */ }
        return translated;
    }

    private async Task ClosePreservingReadErrorAsync(Exception primary)
    {
        try { await CloseAsyncInternal().ConfigureAwait(false); }
        catch (Exception cleanup)
        {
            // Match pool cleanup reporting: keep the original SQLCODE/SQLSTATE.
            try { primary.Data["Db2CleanupError"] = cleanup; }
            catch { /* Cleanup diagnostics cannot replace the primary error. */ }
        }
    }

    public override bool NextResult() => NextResultAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        if (IsClosed) return false;
        if (_noMoreResults) return false;
        if ((_behavior & CommandBehavior.SingleRow) != 0)
        {
            _noMoreResults = true;
            _hasRows = false;
            await DrainProcedureStreamAsync().ConfigureAwait(false);
            await FinishStreamAsync().ConfigureAwait(false);
            return false;
        }
        if (_procedureStream is not null)
        {
            if ((_behavior & CommandBehavior.SingleResult) != 0)
            {
                _noMoreResults = true;
                _hasRows = false;
                await DrainProcedureStreamAsync().ConfigureAwait(false);
                await FinishStreamAsync().ConfigureAwait(false);
                return false;
            }
            if (_procedureBoundary is not null)
            {
                SetCurrentColumns(_procedureBoundary.Columns);
                _procedureBoundary = null;
                _rowIndex = -1;
                _currentValues = null;
                _singleRowReturned = false;
                await PrimeProcedureResultSetAsync().ConfigureAwait(false);
                return true;
            }
            if (_procedureAtEnd)
            {
                _noMoreResults = true;
                _hasRows = false;
                await FinishStreamAsync().ConfigureAwait(false);
                return false;
            }
            try
            {
                while (await _procedureStream.MoveNextAsync().ConfigureAwait(false))
                {
                    var next = _procedureStream.Current;
                    if (next.Kind == Db2ProcedureStreamEventKind.Row) continue;
                    SetCurrentColumns(next.Columns);
                    _rowIndex = -1;
                    _currentValues = null;
                    _singleRowReturned = false;
                    await PrimeProcedureResultSetAsync().ConfigureAwait(false);
                    return true;
                }
                _procedureAtEnd = true;
                _noMoreResults = true;
                _hasRows = false;
                // The iterator has completed the CALL, committed (when autocommit is enabled),
                // and captured OUT values. Release the session gate and command operation now;
                // ADO.NET values are still published only when the reader itself is closed.
                await FinishStreamAsync().ConfigureAwait(false);
                return false;
            }
            catch (Exception error)
            {
                await ClosePreservingReadErrorAsync(error).ConfigureAwait(false);
                throw TranslateReadError(error);
            }
        }
        if (_resultSets is not null)
        {
            if ((_behavior & CommandBehavior.SingleResult) != 0 || _resultSetIndex + 1 >= _resultSets.Count)
            {
                _noMoreResults = true;
                _hasRows = false;
                await FinishStreamAsync().ConfigureAwait(false);
                return false;
            }
            _resultSetIndex++;
            SetCurrentColumns(_resultSets[_resultSetIndex].Columns);
            _rowIndex = -1;
            _currentValues = null;
            _singleRowReturned = false;
            _hasRows = _resultSets[_resultSetIndex].Rows.Count > 0;
            return true;
        }
        _noMoreResults = true;
        _hasRows = false;
        await FinishStreamAsync().ConfigureAwait(false);
        return false;
    }

    public override string GetName(int ordinal) => GetColumn(ordinal).Name;

    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        EnsureOrdinalMaps();
        if (_exactOrdinals!.TryGetValue(name, out var ordinal)) return ordinal;
        if (_ignoreCaseOrdinals!.TryGetValue(name, out ordinal)) return ordinal;
        throw new IndexOutOfRangeException($"Column '{name}' was not found.");
    }

    public override object GetValue(int ordinal)
    {
        if (TryGetBufferedDecimal(ordinal, out var bufferedNumber)) return bufferedNumber;
        if (TryGetBufferedDateTime(ordinal, out var date) && (SqlType)((ushort)GetColumn(ordinal).SqlType & 0xfffe) != SqlType.Time) return date;
        if (_bufferedRows is not null && (SqlType)((ushort)GetColumn(ordinal).SqlType & 0xfffe) == SqlType.Time && GetColumn(ordinal).Scale <= 7 && TryGetBufferedField(ordinal, 0x22, out var timeBytes)
            && RowValueParser.TryTime(timeBytes, out var time)) return time;
        var value = GetRawValue(ordinal);
        if (value is null or DBNull) return DBNull.Value;
        if (value is Db2LobValue lob) value = lob.Materialize();
        var column = GetColumn(ordinal);
        var type = (SqlType)((ushort)column.SqlType & 0xfffe);
        try
        {
            return type switch
            {
                SqlType.Date when value is string text => DateTime.ParseExact(text.AsSpan().Trim(), "yyyy-MM-dd".AsSpan(), CultureInfo.InvariantCulture, DateTimeStyles.None),
                SqlType.Time when column.Scale <= 7 && value is string text => ParseTime(text),
                SqlType.Timestamp when column.Scale <= 7 && value is string text => ParseTimestamp(text),
                SqlType.Decimal or SqlType.Numeric when column.Precision <= 28 && value is string text && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => number,
                _ => value,
            };
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentOutOfRangeException) { return value; }
    }

    internal bool TryGetProgressiveLobImportValue(
        int ordinal,
        Db2AdoConnection destination,
        out Db2LobValue? lob,
        out SqlType type)
    {
        lob = null;
        type = default;
        if (ReferenceEquals(_connection, destination)) return false;
        if (GetRawValue(ordinal) is not Db2LobValue progressiveLob || progressiveLob.ByteLength is null)
            return false;

        type = SqlTypeNames.BaseType((SqlType)GetColumn(ordinal).SqlType);
        if (type is not (SqlType.Blob or SqlType.Clob or SqlType.DbClob or SqlType.XML)) return false;
        lob = progressiveLob;
        return true;
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        // Unsafe.As is valid only under the exact typeof(T) equality guard; each
        // local has that same CLR type, so neither size nor representation is guessed.
        if (_bufferedRows is not null)
        {
            if (typeof(T) == typeof(int) && TryGetBufferedField(ordinal, 0x02, out var i32))
            { var v = BinaryPrimitives.ReadInt32LittleEndian(i32); return Unsafe.As<int, T>(ref v); }
            if (typeof(T) == typeof(long) && TryGetBufferedField(ordinal, 0x16, out var i64))
            { var v = BinaryPrimitives.ReadInt64LittleEndian(i64); return Unsafe.As<long, T>(ref v); }
            if (typeof(T) == typeof(short) && TryGetBufferedField(ordinal, 0x04, out var i16))
            { var v = BinaryPrimitives.ReadInt16LittleEndian(i16); return Unsafe.As<short, T>(ref v); }
            if (typeof(T) == typeof(double) && TryGetBufferedField(ordinal, 0x0a, out var f64))
            { var v = BinaryPrimitives.ReadDoubleLittleEndian(f64); return Unsafe.As<double, T>(ref v); }
            if (typeof(T) == typeof(float) && TryGetBufferedField(ordinal, 0x0c, out var f32))
            { var v = BinaryPrimitives.ReadSingleLittleEndian(f32); return Unsafe.As<float, T>(ref v); }
            if (typeof(T) == typeof(sbyte) && TryGetBufferedField(ordinal, 0x06, out var signedByte))
            { var v = (sbyte)signedByte[0]; return Unsafe.As<sbyte, T>(ref v); }
            if (typeof(T) == typeof(bool) && TryGetBufferedField(ordinal, 0xbe, out var boolean))
            { var v = !boolean.IsEmpty && boolean[^1] != 0; return Unsafe.As<bool, T>(ref v); }
            if (typeof(T) == typeof(decimal) && TryGetBufferedDecimal(ordinal, out var number)) return Unsafe.As<decimal, T>(ref number);
            if (typeof(T) == typeof(DateTime) && (SqlType)((ushort)GetColumn(ordinal).SqlType & 0xfffe) != SqlType.Time && TryGetBufferedDateTime(ordinal, out var date)) return Unsafe.As<DateTime, T>(ref date);
            if (typeof(T) == typeof(TimeSpan) && (SqlType)((ushort)GetColumn(ordinal).SqlType & 0xfffe) == SqlType.Time && GetColumn(ordinal).Scale <= 7 && TryGetBufferedField(ordinal, 0x22, out var timeBytes)
                && RowValueParser.TryTime(timeBytes, out var time)) return Unsafe.As<TimeSpan, T>(ref time);
        }
        var value = GetRawValue(ordinal);
        var column = GetColumn(ordinal);
        var sqlType = (SqlType)((ushort)column.SqlType & 0xfffe);

        // Match GetValue's provider conversions without boxing their value-type results.
        if (value is string text && typeof(T) == typeof(DateTime))
        {
            if (sqlType == SqlType.Date)
            {
                try
                {
                    var date = DateTime.ParseExact(text.AsSpan().Trim(), "yyyy-MM-dd".AsSpan(), CultureInfo.InvariantCulture, DateTimeStyles.None);
                    return Unsafe.As<DateTime, T>(ref date);
                }
                catch (FormatException) { }
            }
            else if (sqlType == SqlType.Timestamp && column.Scale <= 7)
            {
                try
                {
                    var timestamp = ParseTimestamp(text);
                    return Unsafe.As<DateTime, T>(ref timestamp);
                }
                catch (FormatException) { }
            }
        }
        if (value is string timeText && typeof(T) == typeof(TimeSpan)
            && sqlType == SqlType.Time && column.Scale <= 7)
        {
            try
            {
                var time = ParseTime(timeText);
                return Unsafe.As<TimeSpan, T>(ref time);
            }
            catch (FormatException) { }
        }
        if (value is string decimalText && typeof(T) == typeof(decimal)
            && (sqlType is SqlType.Decimal or SqlType.Numeric) && column.Precision <= 28
            && decimal.TryParse(decimalText, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue))
            return Unsafe.As<decimal, T>(ref decimalValue);

        // GetValue has normalization/materialization semantics for these cases. In particular,
        // GetFieldValue<object> must agree with GetValue, and a LOB must not leak its locator wrapper.
        if (typeof(T) == typeof(object) || value is Db2LobValue
            || value is string && (sqlType == SqlType.Date
                || sqlType == SqlType.Time && column.Scale <= 7
                || sqlType == SqlType.Timestamp && column.Scale <= 7
                || (sqlType is SqlType.Decimal or SqlType.Numeric) && column.Precision <= 28))
            return (T)GetValue(ordinal);

        if (value is T typed) return typed;
        return (T)GetValue(ordinal);
    }

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, FieldCount);
        for (var index = 0; index < count; index++) values[index] = GetValue(index);
        return count;
    }

    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal)
    {
        var column = GetColumn(ordinal);
        var type = (SqlType)((ushort)column.SqlType & 0xfffe);
        return type switch
        {
            SqlType.Small => typeof(short),
            SqlType.Integer => typeof(int),
            SqlType.BigInt => typeof(long),
            SqlType.Float => column.Length == 4 ? typeof(float) : typeof(double),
            SqlType.Decimal or SqlType.Numeric when column.Precision > 28 => typeof(string),
            SqlType.Decimal or SqlType.Numeric => typeof(decimal),
            SqlType.Timestamp when column.Scale > 7 => typeof(string),
            SqlType.Date or SqlType.Timestamp => typeof(DateTime),
            SqlType.Time when column.Scale > 7 => typeof(string),
            SqlType.Time => typeof(TimeSpan),
            SqlType.Boolean => typeof(bool),
            SqlType.Binary or SqlType.VarBinary or SqlType.Blob => typeof(byte[]),
            _ => typeof(string),
        };
    }

    public override string GetDataTypeName(int ordinal)
    {
        var name = SqlTypeNames.Get((SqlType)GetColumn(ordinal).SqlType);
        return name.EndsWith(" (NULLABLE)", StringComparison.Ordinal) ? name[..^11] : name;
    }

    public override bool GetBoolean(int ordinal)
    {
        if (TryGetBufferedField(ordinal, 0xbe, out var data)) return !data.IsEmpty && data[^1] != 0;
        return GetRawValue(ordinal) is bool value ? value : Convert.ToBoolean(GetValue(ordinal), CultureInfo.InvariantCulture);
    }
    public override byte GetByte(int ordinal)
    {
        if (TryGetBufferedField(ordinal, 0x06, out var data)) return checked((byte)(sbyte)data[0]);
        return GetRawValue(ordinal) is byte value ? value : Convert.ToByte(GetValue(ordinal), CultureInfo.InvariantCulture);
    }
    public override char GetChar(int ordinal)
    {
        var value = GetValue(ordinal);
        return value is char character ? character : Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text
            ? text[0]
            : throw new InvalidCastException("The Db2 value cannot be converted to Char.");
    }
    public override DateTime GetDateTime(int ordinal)
    {
        if (TryGetBufferedDateTime(ordinal, out var date)) return date;
        var value = GetRawValue(ordinal);
        if (value is DateTime dateTime) return dateTime;
        if (value is TimeSpan time) return DateTime.MinValue.Add(time);

        if (value is string text)
        {
            var column = GetColumn(ordinal);
            var type = (SqlType)((ushort)column.SqlType & 0xfffe);
            try
            {
                if (type == SqlType.Date)
                    return DateTime.ParseExact(text.AsSpan().Trim(), "yyyy-MM-dd".AsSpan(), CultureInfo.InvariantCulture, DateTimeStyles.None);
                if (type == SqlType.Time && column.Scale <= 7)
                    return DateTime.MinValue.Add(ParseTime(text));
                if (type == SqlType.Timestamp && column.Scale <= 7)
                    return ParseTimestamp(text);
            }
            catch (FormatException) { /* Retain the legacy fallback for noncanonical temporal text. */ }
        }

        // Date/time GetValue already returns the provider's typed CLR value. Keep a fallback
        // for callers asking GetDateTime on a textual column, without formatting that value
        // back to text and parsing it again.
        var converted = GetValue(ordinal);
        return converted switch
        {
            DateTime convertedDateTime => convertedDateTime,
            TimeSpan convertedTime => DateTime.MinValue.Add(convertedTime),
            _ => DateTime.Parse(Convert.ToString(converted, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
        };
    }
    public override decimal GetDecimal(int ordinal)
    {
        if (TryGetBufferedDecimal(ordinal, out var bufferedNumber)) return bufferedNumber;
        var value = GetRawValue(ordinal);
        if (value is decimal decimalValue) return decimalValue;
        if (value is string text)
        {
            var column = GetColumn(ordinal);
            var type = (SqlType)((ushort)column.SqlType & 0xfffe);
            if ((type is SqlType.Decimal or SqlType.Numeric) && column.Precision <= 28
                && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimalValue))
                return decimalValue;
        }
        return Convert.ToDecimal(GetValue(ordinal), CultureInfo.InvariantCulture);
    }
    public override double GetDouble(int ordinal)
    {
        if (TryGetBufferedField(ordinal, 0x0a, out var data)) return BinaryPrimitives.ReadDoubleLittleEndian(data);
        return GetRawValue(ordinal) is double value ? value : Convert.ToDouble(GetValue(ordinal), CultureInfo.InvariantCulture);
    }
    public override float GetFloat(int ordinal)
    {
        if (TryGetBufferedField(ordinal, 0x0c, out var data)) return BinaryPrimitives.ReadSingleLittleEndian(data);
        return GetRawValue(ordinal) is float value ? value : Convert.ToSingle(GetValue(ordinal), CultureInfo.InvariantCulture);
    }
    public override Guid GetGuid(int ordinal)
    {
        var value = GetRawValue(ordinal);
        return value is Guid guid ? guid : Guid.Parse(Convert.ToString(GetValue(ordinal), CultureInfo.InvariantCulture)!);
    }
    public override short GetInt16(int ordinal)
    {
        if (TryGetBufferedField(ordinal, 0x04, out var data)) return BinaryPrimitives.ReadInt16LittleEndian(data);
        return GetRawValue(ordinal) is short value ? value : Convert.ToInt16(GetValue(ordinal), CultureInfo.InvariantCulture);
    }
    public override int GetInt32(int ordinal)
    {
        if (TryGetBufferedField(ordinal, 0x02, out var data)) return BinaryPrimitives.ReadInt32LittleEndian(data);
        return GetRawValue(ordinal) is int value ? value : Convert.ToInt32(GetValue(ordinal), CultureInfo.InvariantCulture);
    }
    public override long GetInt64(int ordinal)
    {
        if (TryGetBufferedField(ordinal, 0x16, out var data)) return BinaryPrimitives.ReadInt64LittleEndian(data);
        return GetRawValue(ordinal) is long value ? value : Convert.ToInt64(GetValue(ordinal), CultureInfo.InvariantCulture);
    }
    public override string GetString(int ordinal) => GetRawValue(ordinal) is string value
        ? value
        : Convert.ToString(GetValue(ordinal), CultureInfo.InvariantCulture)!;
    public override bool IsDBNull(int ordinal) => _bufferedRows is not null ? _bufferedRows.IsNull(ordinal) : GetRawValue(ordinal) is null or DBNull;
    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) =>
        IsDBNull(ordinal) ? CompletedTrue : CompletedFalse;

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        if (_bufferedRows is not null)
        {
            GetColumn(ordinal);
            var wireType = (byte)(_bufferedRows.Decoders[ordinal].Field.Type & 0xfe);
            if (wireType is 0x26 or 0x1e or 0xc0 or 0x28 or 0x2a or 0xc2 && TryGetBufferedField(ordinal, wireType, out var bytes))
                return CopySlice(bytes, dataOffset, buffer, bufferOffset, length);
        }
        var value = GetRawValue(ordinal);
        if (value is Db2LobValue lob) value = lob.Materialize();
        return value switch
        {
            byte[] raw => CopySlice(raw.AsSpan(), dataOffset, buffer, bufferOffset, length),
            ReadOnlyMemory<byte> memory => CopySlice(memory.Span, dataOffset, buffer, bufferOffset, length),
            Memory<byte> memory => CopySlice(memory.Span, dataOffset, buffer, bufferOffset, length),
            _ => throw new InvalidCastException("The Db2 value is not binary data."),
        };
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = GetString(ordinal);
        if (dataOffset < 0 || dataOffset > text.Length) throw new ArgumentOutOfRangeException(nameof(dataOffset));
        if (buffer is null) return text.Length;
        if (bufferOffset < 0 || length < 0 || bufferOffset + length > buffer.Length) throw new ArgumentOutOfRangeException(nameof(bufferOffset));
        var count = Math.Min(length, text.Length - (int)dataOffset);
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override Stream GetStream(int ordinal)
    {
        var value = GetRawValue(ordinal);
        if (value is Db2LobValue lob)
            return lob.IsText
                ? throw new InvalidCastException("The Db2 value is text data; use GetTextReader.")
                : lob.OpenReadStream();
        return value switch
        {
            byte[] bytes => new MemoryStream(bytes, writable: false),
            ReadOnlyMemory<byte> memory => CreateMemoryStream(memory),
            Memory<byte> memory => CreateMemoryStream(memory),
            _ => throw new InvalidCastException("The Db2 value is not binary data."),
        };
    }

    public override TextReader GetTextReader(int ordinal) => GetRawValue(ordinal) is Db2LobValue lob
        ? lob.OpenTextReader()
        : new StringReader(GetString(ordinal));

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming", "IL2111",
        Justification = "The schema table contains only framework primitive Type values selected by the driver; no consumer type is discovered or instantiated by reflection.")]
    public override DataTable? GetSchemaTable()
    {
        if (IsClosed) throw new InvalidOperationException("The data reader is closed.");
        if (_noMoreResults || FieldCount == 0) return null;
        var table = new DataTable("SchemaTable") { Locale = CultureInfo.InvariantCulture };
        table.Columns.Add("ColumnName", typeof(string));
        table.Columns.Add("ColumnOrdinal", typeof(int));
        table.Columns.Add("ColumnSize", typeof(int));
        table.Columns.Add("NumericPrecision", typeof(short));
        table.Columns.Add("NumericScale", typeof(short));
        table.Columns.Add("DataType", typeof(Type));
        table.Columns.Add("AllowDBNull", typeof(bool));
        table.Columns.Add("IsUnique", typeof(bool));
        table.Columns.Add("IsKey", typeof(bool));
        table.Columns.Add("IsAutoIncrement", typeof(bool));
        table.Columns.Add("BaseCatalogName", typeof(string));
        table.Columns.Add("BaseSchemaName", typeof(string));
        table.Columns.Add("BaseTableName", typeof(string));
        table.Columns.Add("BaseColumnName", typeof(string));
        table.Columns.Add("IsLong", typeof(bool));
        table.Columns.Add("ProviderType", typeof(int));
        table.Columns.Add("IsAliased", typeof(bool));
        table.Columns.Add("IsExpression", typeof(bool));
        table.Columns.Add("IsHidden", typeof(bool));
        table.Columns.Add("IsReadOnly", typeof(bool));
        for (var ordinal = 0; ordinal < FieldCount; ordinal++)
        {
            var column = GetColumn(ordinal);
            table.Rows.Add(
                column.Name,
                ordinal,
                (int)Math.Min(column.Length, int.MaxValue),
                (short)column.Precision,
                (short)column.Scale,
                GetFieldType(ordinal),
                column.Nullable,
                false,
                false,
                false,
                "",
                "",
                "",
                column.Name,
                GetDataTypeName(ordinal) is "BLOB" or "CLOB" or "DBCLOB" or "LONG VARCHAR" or "XML",
                column.SqlType,
                false,
                false,
                false,
                true);
        }
        return table;
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    public override void Close() => CloseAsyncInternal().GetAwaiter().GetResult();

    public override Task CloseAsync() => CloseAsyncInternal();

    internal Task CloseFromConnectionAsync() => CloseAsyncInternal(closeConnection: false);

    private async Task CloseAsyncInternal(bool closeConnection = true)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Exception? failure = null;
        try { await DisposeStreamAsync().ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        _currentValues = null;
        _pendingValues = null;
        if (failure is null && _procedureOutputsOnClose is not null)
        {
            try { _procedureOutputsOnClose(); }
            catch (Exception error) { failure ??= error; }
        }
        _connection.UnregisterReader(this);
        _command.ReaderCompleted(this, _operation);
        if (closeConnection && (_behavior & CommandBehavior.CloseConnection) != 0)
        {
            try { await _connection.CloseAsync().ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
        }
        if (failure is not null) throw _translateError(failure);
    }

    private async Task FinishStreamAsync()
    {
        try { await DisposeStreamAsync().ConfigureAwait(false); }
        catch (Exception error) { throw _translateError(error); }
        _streamEnded = true;
        _currentValues = null;
        _pendingValues = null;
        _connection.UnregisterReader(this);
        _command.ReaderCompleted(this, _operation);
    }

    private async Task DisposeStreamAsync()
    {
        var blocks = Interlocked.Exchange(ref _blocks, null);
        _bufferedRows = null;
        _rawCache = null;
        _rawCacheGenerations = null;
        if (blocks is not null) await blocks.DisposeAsync().ConfigureAwait(false);
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        var procedureStream = Interlocked.Exchange(ref _procedureStream, null);
        if (procedureStream is not null) await procedureStream.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DrainProcedureStreamAsync()
    {
        if (_procedureStream is null || _procedureAtEnd) return;
        _pendingValues = null;
        _procedureBoundary = null;
        try
        {
            while (await _procedureStream.MoveNextAsync().ConfigureAwait(false)) { }
            _procedureAtEnd = true;
        }
        catch (Exception error)
        {
            await ClosePreservingReadErrorAsync(error).ConfigureAwait(false);
            throw TranslateReadError(error);
        }
    }

    private async Task PrimeProcedureResultSetAsync()
    {
        _hasRows = false;
        _pendingValues = null;
        if (_procedureStream is null || _procedureAtEnd) return;
        if (!await _procedureStream.MoveNextAsync().ConfigureAwait(false))
        {
            _procedureAtEnd = true;
            return;
        }
        var next = _procedureStream.Current;
        if (next.Kind == Db2ProcedureStreamEventKind.ResultSet)
        {
            _procedureBoundary = next;
            return;
        }
        _pendingValues = next.Values;
        _hasRows = true;
    }

    private Db2ColumnMetadata GetColumn(int ordinal)
    {
        if ((uint)ordinal >= (uint)_currentColumns.Length) throw new IndexOutOfRangeException($"Column ordinal {ordinal} is outside the current result set.");
        return _currentColumns[ordinal];
    }

    private void AdvanceBufferedRow()
    {
        if (_rawCache is not null) Array.Clear(_rawCache);
        if (_rowGeneration == int.MaxValue)
        {
            _rowGeneration = 1;
            if (_rawCacheGenerations is not null) Array.Clear(_rawCacheGenerations);
        }
        else _rowGeneration++;
    }

    private bool TryGetBufferedField(int ordinal, byte type, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (_bufferedRows is null) return false;
        if ((uint)ordinal >= (uint)_currentColumns.Length) throw new IndexOutOfRangeException($"Column ordinal {ordinal} is outside the current result set.");
        if (_rowGeneration == 0) throw new InvalidOperationException("Read must be called before accessing a row.");
        if ((_bufferedRows.Decoders[ordinal].Field.Type & 0xfe) != type || _bufferedRows.IsNull(ordinal)) return false;
        bytes = _bufferedRows.GetSpan(ordinal);
        return true;
    }

    private bool TryGetBufferedDecimal(int ordinal, out decimal value)
    {
        value = default;
        return _bufferedRows is not null && (SqlType)((ushort)GetColumn(ordinal).SqlType & 0xfffe) is SqlType.Decimal or SqlType.Numeric
            && GetColumn(ordinal).Precision <= 28 &&
            TryGetBufferedField(ordinal, 0x0e, out var bytes) && bytes.Length <= 15 &&
            RowValueParser.TryDecimal(bytes, _bufferedRows.Decoders[ordinal].Field.Scale, out value);
    }

    private bool TryGetBufferedDateTime(int ordinal, out DateTime value)
    {
        value = default;
        if (_bufferedRows is null) return false;
        var type = (SqlType)((ushort)GetColumn(ordinal).SqlType & 0xfffe);
        if (type == SqlType.Date && TryGetBufferedField(ordinal, 0x20, out var date)) return RowValueParser.TryDate(date, out value);
        if (GetColumn(ordinal).Scale > 7) return false;
        if (type == SqlType.Timestamp && TryGetBufferedField(ordinal, 0x24, out var timestamp)) return RowValueParser.TryTimestamp(timestamp, out value);
        if (type == SqlType.Time && TryGetBufferedField(ordinal, 0x22, out var timeBytes) && RowValueParser.TryTime(timeBytes, out var time))
        { value = DateTime.MinValue.Add(time); return true; }
        return false;
    }

    private object? GetRawValue(int ordinal)
    {
        if ((uint)ordinal >= (uint)_currentColumns.Length) throw new IndexOutOfRangeException($"Column ordinal {ordinal} is outside the current result set.");
        if (_bufferedRows is not null)
        {
            // Compatibility getters may box/materialize. Cache only when requested;
            // typed getters below never create this per-reader compatibility cache.
            _rawCache ??= new object?[FieldCount];
            _rawCacheGenerations ??= new int[FieldCount];
            if (_rawCacheGenerations[ordinal] != _rowGeneration || _rowGeneration == 0)
            {
                _rawCache[ordinal] = _bufferedRows.GetRawValue(ordinal);
                _rawCacheGenerations[ordinal] = _rowGeneration;
            }
            return _rawCache[ordinal];
        }
        if (_currentValues is null) throw new InvalidOperationException("Read must be called before accessing a row.");
        return _currentValues[ordinal];
    }

    private void SetCurrentColumns(IReadOnlyList<Db2ColumnMetadata> columns)
    {
        _currentColumns = columns as Db2ColumnMetadata[] ?? columns.ToArray();
        _exactOrdinals = null;
        _ignoreCaseOrdinals = null;
    }

    private void EnsureOrdinalMaps()
    {
        if (_exactOrdinals is not null) return;
        var exact = new Dictionary<string, int>(_currentColumns.Length, StringComparer.Ordinal);
        var ignoreCase = new Dictionary<string, int>(_currentColumns.Length, StringComparer.OrdinalIgnoreCase);
        for (var ordinal = 0; ordinal < _currentColumns.Length; ordinal++)
        {
            var name = _currentColumns[ordinal].Name;
            exact.TryAdd(name, ordinal);
            ignoreCase.TryAdd(name, ordinal);
        }
        _exactOrdinals = exact;
        _ignoreCaseOrdinals = ignoreCase;
    }

    private static long CopySlice(ReadOnlySpan<byte> bytes, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        if (dataOffset < 0 || dataOffset > bytes.Length) throw new ArgumentOutOfRangeException(nameof(dataOffset));
        if (buffer is null) return bytes.Length;
        if (bufferOffset < 0 || length < 0 || bufferOffset + length > buffer.Length) throw new ArgumentOutOfRangeException(nameof(bufferOffset));
        var count = (int)Math.Min(length, bytes.Length - dataOffset);
        bytes.Slice((int)dataOffset, count).CopyTo(buffer.AsSpan(bufferOffset, count));
        return count;
    }

    private static Stream CreateMemoryStream(ReadOnlyMemory<byte> memory)
    {
        if (MemoryMarshal.TryGetArray(memory, out var segment) && segment.Array is { } array)
            return new MemoryStream(array, segment.Offset, segment.Count, writable: false, publiclyVisible: false);
        return new MemoryStream(memory.ToArray(), writable: false);
    }

    private static DateTime ParseTimestamp(string value)
    {
        var text = value.AsSpan().Trim();
        if (text.Length < 19) return DateTime.Parse(text, CultureInfo.InvariantCulture);
        var time = text[11..];
        var firstSeparator = time.IndexOf('.');
        var secondRelative = firstSeparator < 0 ? -1 : time[(firstSeparator + 1)..].IndexOf('.');
        var secondSeparator = secondRelative < 0 ? -1 : secondRelative + firstSeparator + 1;
        if (firstSeparator < 0 || secondSeparator < 0) return DateTime.Parse(text, CultureInfo.InvariantCulture);

        if (text.Length <= 128)
        {
            Span<char> normalized = stackalloc char[text.Length];
            return TryParseNormalizedTimestamp(text, time, firstSeparator, secondSeparator, normalized, out var timestamp)
                ? timestamp
                : DateTime.Parse(text, CultureInfo.InvariantCulture);
        }

        var rented = ArrayPool<char>.Shared.Rent(text.Length);
        try
        {
            var normalized = rented.AsSpan(0, text.Length);
            return TryParseNormalizedTimestamp(text, time, firstSeparator, secondSeparator, normalized, out var timestamp)
                ? timestamp
                : DateTime.Parse(text, CultureInfo.InvariantCulture);
        }
        finally { ArrayPool<char>.Shared.Return(rented); }
    }

    private static bool TryParseNormalizedTimestamp(
        ReadOnlySpan<char> text,
        ReadOnlySpan<char> time,
        int firstSeparator,
        int secondSeparator,
        Span<char> normalized,
        out DateTime timestamp)
    {
        text[..10].CopyTo(normalized);
        normalized[10] = 'T';
        time.CopyTo(normalized[11..]);
        normalized[11 + firstSeparator] = ':';
        normalized[11 + secondSeparator] = ':';
        if (DateTime.TryParse(normalized[..text.Length], CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp))
            return true;
        if (!IsDb2EndOfDay(time)) return false;

        normalized[11] = '0';
        normalized[12] = '0';
        if (!DateTime.TryParse(normalized[..text.Length], CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp))
            return false;
        try
        {
            timestamp = timestamp.AddDays(1);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            timestamp = default;
            return false;
        }
    }

    private static bool IsDb2EndOfDay(ReadOnlySpan<char> time)
    {
        if (time.Length < 8 || !time[..2].SequenceEqual("24".AsSpan()) || time[2] != '.'
            || !time.Slice(3, 2).SequenceEqual("00".AsSpan()) || time[5] != '.'
            || !time.Slice(6, 2).SequenceEqual("00".AsSpan()))
            return false;
        if (time.Length == 8) return true;
        if (time[8] != '.') return false;
        foreach (var digit in time[9..])
            if (digit != '0') return false;
        return true;
    }

    private static TimeSpan ParseTime(string value)
    {
        var text = value.AsSpan().Trim();
        if (TryParseDb2Time(text, out var db2Time)) return db2Time;
        if (text.Length <= 128)
        {
            Span<char> normalized = stackalloc char[text.Length];
            NormalizeTimeSeparators(text, normalized);
            return TimeSpan.Parse(normalized, CultureInfo.InvariantCulture);
        }

        var rented = ArrayPool<char>.Shared.Rent(text.Length);
        try
        {
            var normalized = rented.AsSpan(0, text.Length);
            NormalizeTimeSeparators(text, normalized);
            return TimeSpan.Parse(normalized, CultureInfo.InvariantCulture);
        }
        finally { ArrayPool<char>.Shared.Return(rented); }
    }

    private static bool TryParseDb2Time(ReadOnlySpan<char> text, out TimeSpan value)
    {
        value = default;
        if (text.Length < 8 || text[2] != '.' || text[5] != '.') return false;
        var hours = ParseTwoDigits(text);
        var minutes = ParseTwoDigits(text[3..]);
        var seconds = ParseTwoDigits(text[6..]);
        if (hours < 0 || minutes is < 0 or > 59 || seconds is < 0 or > 59 || hours > 24) return false;

        var fractionTicks = 0;
        if (text.Length > 8)
        {
            if (text[8] != '.') return false;
            var fraction = text[9..];
            if (fraction.Length is < 1 or > 7) return false;
            foreach (var digit in fraction)
            {
                if (digit is < '0' or > '9') return false;
                fractionTicks = fractionTicks * 10 + digit - '0';
            }
            for (var index = fraction.Length; index < 7; index++) fractionTicks *= 10;
        }

        if (hours == 24 && (minutes != 0 || seconds != 0 || fractionTicks != 0)) return false;
        var totalSeconds = hours * 3600L + minutes * 60L + seconds;
        value = TimeSpan.FromTicks(totalSeconds * TimeSpan.TicksPerSecond + fractionTicks);
        return true;
    }

    private static int ParseTwoDigits(ReadOnlySpan<char> value)
    {
        if (value.Length < 2 || value[0] is < '0' or > '9' || value[1] is < '0' or > '9') return -1;
        return (value[0] - '0') * 10 + value[1] - '0';
    }

    private static void NormalizeTimeSeparators(ReadOnlySpan<char> source, Span<char> destination)
    {
        source.CopyTo(destination);
        for (var index = 0; index < source.Length; index++)
            if (destination[index] == '.') destination[index] = ':';
    }
}
