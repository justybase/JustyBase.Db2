using System.Buffers;

namespace JustyBase.Db2.Protocol;

/// <summary>Owns one reusable DSS chain buffer. Its memory never escapes an active write.</summary>
internal sealed class DssWriteBuffer : IDisposable
{
    private readonly object _gate = new();
    private byte[]? _buffer;
    private int _written;
    private bool _writing;
    private bool _disposed;

    internal ushort Append(ReadOnlySpan<byte> payload, ushort id, bool sameId, bool last)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_writing) throw new InvalidOperationException("Cannot append during an active DSS write.");
            var size = DssCodec.GetEncodedLength(payload.Length);
            var needed = checked(_written + size);
            if (_buffer is null || needed > _buffer.Length)
            {
                var grown = ArrayPool<byte>.Shared.Rent(Math.Max(needed, _buffer is null ? 4096 : checked(_buffer.Length * 2)));
                if (_buffer is not null)
                {
                    _buffer.AsSpan(0, _written).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
                }
                _buffer = grown;
            }
            var next = DssCodec.WriteRequest(payload, _buffer.AsSpan(_written, size), id, sameId, last);
            _written = needed;
            return next;
        }
    }

    internal Task FlushAsync(Stream destination, CancellationToken token)
    {
        ReadOnlyMemory<byte> memory;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_written == 0) return Task.CompletedTask;
            if (_writing) throw new InvalidOperationException("A DSS write is already active.");
            _writing = true;
            memory = _buffer!.AsMemory(0, _written);
        }
        return FlushCoreAsync(destination, memory, token);
    }

    private async Task FlushCoreAsync(Stream destination, ReadOnlyMemory<byte> memory, CancellationToken token)
    {
        try { await destination.WriteAsync(memory, token).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                // Authentication frames use this buffer too. Clear their bytes immediately.
                _buffer!.AsSpan(0, _written).Clear();
                _written = 0;
                _writing = false;
                if (_disposed) ReturnBuffer();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            // Dispose may race SQLINTR/connection shutdown. The outstanding I/O owns
            // the memory until its await completes; only then may it return to the pool.
            if (!_writing) ReturnBuffer();
        }
    }

    private void ReturnBuffer()
    {
        if (_buffer is null) return;
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = null;
        _written = 0;
    }
}
