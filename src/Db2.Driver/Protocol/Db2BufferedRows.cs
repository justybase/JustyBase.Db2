using System.Buffers;

namespace JustyBase.Db2.Protocol;

/// <summary>Owns the current QRYDTA block and incomplete tail; a reader borrows it until the next fetch.</summary>
internal sealed class Db2BufferedRows : IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(65536);
    private int _length;
    private int _completeLength;
    private int _position;
    private int _indexedColumn;
    private int _fieldPosition;
    private bool _current;
    private readonly int[] _starts;
    private readonly int[] _valueStarts;
    private readonly int[] _lengths;
    internal Db2ColumnDecoder[] Decoders { get; }
    internal int PendingBytes => _length - _completeLength;
    internal ReadOnlySpan<byte> PendingData => _buffer.AsSpan(_completeLength, PendingBytes);
    internal bool HasRows => _completeLength > 0;

    internal Db2BufferedRows(Db2ColumnDecoder[] decoders)
    {
        Decoders = decoders;
        _starts = new int[decoders.Length];
        _valueStarts = new int[decoders.Length];
        _lengths = new int[decoders.Length];
    }

    internal void BeginFetch()
    {
        var remaining = PendingBytes;
        _buffer.AsSpan(_completeLength, remaining).CopyTo(_buffer);
        _length = remaining;
        _completeLength = _position = 0;
        _current = false;
    }

    internal void Append(ReadOnlySpan<byte> data)
    {
#if DB2_PERF_COUNTERS
        Interlocked.Add(ref Db2PerformanceCounters.RowBufferCopyBytes, data.Length);
#endif
        var needed = checked(_length + data.Length);
        if (needed > _buffer.Length)
        {
            var size = (int)Math.Min(Array.MaxLength, Math.Max((long)needed, _buffer.Length * 2L));
            var grown = ArrayPool<byte>.Shared.Rent(size);
            _buffer.AsSpan(0, _length).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = grown;
        }
        data.CopyTo(_buffer.AsSpan(_length));
        _length = needed;
    }

    internal void Validate(Action<Db2Warning?> reportWarning)
    {
        var data = _buffer.AsSpan(0, _length);
        var offset = 0;
        List<Db2Warning>? warnings = null;
        while (Decoders.Length > 0 && offset + 2 <= data.Length)
        {
            if (!Db2Session.TryMeasureRow(Decoders, data, ref offset, out _, out var warning)) break;
            if (warning is not null) (warnings ??= []).Add(warning);
            _completeLength = offset;
        }
        // The legacy decoder reports row warnings only after the complete block
        // has validated. A later malformed row must not expose earlier warnings.
        if (warnings is not null) foreach (var warning in warnings) reportWarning(warning);
    }

    internal bool TryRead()
    {
        if (_current)
        {
            for (var i = _indexedColumn + 1; i < Decoders.Length; i++)
                if (!Decoders[i].TrySkip(_buffer.AsSpan(0, _completeLength), ref _fieldPosition, out _, out _))
                    throw new InvalidDataException("A validated DB2 row became incomplete.");
            _position = _fieldPosition;
        }
        _current = false;
        if (_position >= _completeLength) return false;
        var offset = _position;
        if (!Db2Session.TryReadRowSqlca(_buffer.AsSpan(0, _completeLength), ref offset, out _))
            throw new InvalidDataException("A validated DB2 row header became incomplete.");
        _fieldPosition = offset;
        _indexedColumn = -1;
        _current = true;
        return true;
    }

    internal bool IsNull(int ordinal) { IndexColumn(ordinal); return _lengths[ordinal] < 0; }
    internal ReadOnlySpan<byte> GetSpan(int ordinal)
    {
        IndexColumn(ordinal);
        return _lengths[ordinal] < 0 ? default : _buffer.AsSpan(_valueStarts[ordinal], _lengths[ordinal]);
    }
    internal object? GetRawValue(int ordinal)
    {
        IndexColumn(ordinal);
        var offset = _starts[ordinal];
        return Db2Session.DecodeField(Decoders[ordinal].Field, _buffer.AsSpan(0, _completeLength), ref offset);
    }

    private void IndexColumn(int ordinal)
    {
        if ((uint)ordinal >= (uint)Decoders.Length) throw new IndexOutOfRangeException($"Column ordinal {ordinal} is outside the current result set.");
        if (!_current) throw new InvalidOperationException("Read must be called before accessing a row.");
        for (var i = _indexedColumn + 1; i <= ordinal; i++)
        {
            _starts[i] = _fieldPosition;
            if (!Decoders[i].TrySkip(_buffer.AsSpan(0, _completeLength), ref _fieldPosition, out _valueStarts[i], out _lengths[i]))
                throw new InvalidDataException("A validated DB2 field became incomplete.");
        }
        _indexedColumn = Math.Max(_indexedColumn, ordinal);
    }

    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = [];
        _current = false;
        _completeLength = _length = 0;
        if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
    }
}

internal readonly struct Db2ReaderBlock
{
    internal readonly Db2BufferedRows? Rows;
    internal readonly Db2StreamRow? Materialized;
    internal Db2ReaderBlock(Db2BufferedRows rows) { Rows = rows; Materialized = null; }
    internal Db2ReaderBlock(Db2StreamRow row) { Materialized = row; Rows = null; }
}
