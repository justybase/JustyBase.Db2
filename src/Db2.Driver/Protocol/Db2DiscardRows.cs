using System.Buffers;

namespace JustyBase.Db2.Protocol;

/// <summary>Validates unobserved rows without materializing fields or retaining complete blocks.</summary>
internal sealed class Db2DiscardRows(Db2ColumnDecoder[] decoders) : IDisposable
{
    private byte[] _tail = [];
    private int _pending;
    private List<Db2Warning>? _warnings;
    internal int PendingBytes => _pending;

    internal void BeginFetch() => _warnings?.Clear();

    internal void Append(ReadOnlySpan<byte> data)
    {
        // Only a row crossing a payload boundary needs owned storage. Grow the
        // incomplete prefix in small steps so the rest of the block stays borrowed.
        while (_pending > 0 && !data.IsEmpty)
        {
            var take = Math.Min(data.Length, Math.Max(16, _pending));
            AppendTail(data[..take]); data = data[take..];
            var consumed = Scan(_tail.AsSpan(0, _pending));
            if (consumed == 0) continue;
            _pending -= consumed;
            _tail.AsSpan(consumed, _pending).CopyTo(_tail);
        }
        if (data.IsEmpty) return;
        var complete = Scan(data);
        if (complete < data.Length) AppendTail(data[complete..]);
    }

    private int Scan(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset + 2 <= data.Length)
        {
            if (!Db2Session.TryMeasureRow(decoders, data, ref offset, out _, out var warning)) break;
            if (warning is not null) (_warnings ??= []).Add(warning);
        }
        return offset;
    }

    private void AppendTail(ReadOnlySpan<byte> data)
    {
#if DB2_PERF_COUNTERS
        Interlocked.Add(ref Db2PerformanceCounters.DiscardCopyBytes, data.Length);
#endif
        var needed = checked(_pending + data.Length);
        if (needed > _tail.Length)
        {
            var size = (int)Math.Min(Array.MaxLength, Math.Max((long)needed, Math.Max(128L, _tail.Length * 2L)));
            var grown = ArrayPool<byte>.Shared.Rent(size);
            _tail.AsSpan(0, _pending).CopyTo(grown);
            if (_tail.Length > 0) ArrayPool<byte>.Shared.Return(_tail, clearArray: true);
            _tail = grown;
        }
        data.CopyTo(_tail.AsSpan(_pending));
        _pending = needed;
    }

    internal void CompleteFetch(Action<Db2Warning?> reportWarning)
    {
        // Match buffered reading: no earlier row warnings escape a malformed block.
        if (_warnings is not null)
            foreach (var warning in _warnings) reportWarning(warning);
    }

    public void Dispose()
    {
        var tail = _tail;
        _tail = []; _pending = 0; _warnings = null;
        if (tail.Length > 0) ArrayPool<byte>.Shared.Return(tail, clearArray: true);
    }
}
