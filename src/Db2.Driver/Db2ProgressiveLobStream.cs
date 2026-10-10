namespace JustyBase.Db2;

internal sealed class Db2ProgressiveLobStream : Stream
{
    private readonly long? length;
    private readonly Func<CancellationToken, ValueTask<(byte[] Data, bool IsFinal)>> fetch;
    private readonly Action? cancelOperation;
    private byte[] _buffer = [];
    private int _offset;
    private long _received;
    private long _position;
    private bool _completed;
    private bool _disposed;
    private readonly SemaphoreSlim _readGate = new(1, 1);

    public Db2ProgressiveLobStream(long? length, Func<CancellationToken, ValueTask<(byte[] Data, bool IsFinal)>> fetch, Action? cancelOperation = null)
    {
        this.length = length;
        this.fetch = fetch;
        this.cancelOperation = cancelOperation;
        // DB2 represents an empty locator with length zero. It is already complete and
        // does not require a GETNXTCHK exchange.
        _completed = length == 0;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length ?? throw new NotSupportedException("DB2 did not report the progressive LOB's encoded byte length.");
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("DB2 LOB streams are forward-only.");
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var destination = buffer.AsMemory(offset, count);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length == 0 || _completed && _offset == _buffer.Length) return 0;
        // Keep the same gate as asynchronous reads and draining. Only an already
        // buffered chunk bypasses AsTask; network reads retain their original path.
        if (_readGate.Wait(0))
        {
            try
            {
                var available = Math.Min(destination.Length, _buffer.Length - _offset);
                if (available > 0)
                {
                    _buffer.AsMemory(_offset, available).CopyTo(destination);
                    _offset += available; _position += available;
                    return available;
                }
            }
            finally { _readGate.Release(); }
        }
        return ReadAsync(destination).AsTask().GetAwaiter().GetResult();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ReadCoreAsync(buffer, cancellationToken);

    private async ValueTask<int> ReadCoreAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length == 0 || _completed && _offset == _buffer.Length) return 0;
        using var cancellationRegistration = cancellationToken.CanBeCanceled && cancelOperation is not null
            ? cancellationToken.Register(static state => ((Action)state!).Invoke(), cancelOperation)
            : default;
        if (cancellationToken.IsCancellationRequested)
        {
            _completed = true;
            _disposed = true;
            cancellationToken.ThrowIfCancellationRequested();
        }
        try { await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _completed = true;
            _disposed = true;
            throw;
        }
        try
        {
            if (_offset == _buffer.Length && !_completed) await FetchAsync(cancellationToken).ConfigureAwait(false);
            var count = Math.Min(destination.Length, _buffer.Length - _offset);
            if (count > 0)
            {
                _buffer.AsMemory(_offset, count).CopyTo(destination);
                _offset += count;
                _position += count;
                return count;
            }
            return 0;
        }
        finally { _readGate.Release(); }
    }

    private async ValueTask FetchAsync(CancellationToken cancellationToken)
    {
        (byte[] Data, bool IsFinal) chunk;
        try { chunk = await fetch(cancelOperation is null ? cancellationToken : CancellationToken.None).ConfigureAwait(false); }
        catch (Db2SqlException error) when (error.SqlCode == -952)
        {
            _completed = true;
            _disposed = true;
            throw;
        }
        catch (OperationCanceledException)
        {
            _completed = true;
            _disposed = true;
            throw;
        }
        if (chunk.Data.Length == 0 && !chunk.IsFinal)
            throw new InvalidDataException("DB2 GETNXTCHK made no progress before the final chunk.");
        _received = checked(_received + chunk.Data.LongLength);
        if (length is long expected && _received > expected) throw new InvalidDataException("DB2 progressive LOB exceeded the locator byte length.");
        if (chunk.IsFinal && length is long finalExpected && _received != finalExpected)
            throw new InvalidDataException($"DB2 progressive LOB ended at {_received} bytes; its locator declared {finalExpected} bytes.");
        _buffer = chunk.Data;
        _offset = 0;
        _completed = chunk.IsFinal;
    }

    internal async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (!_completed)
            {
                _offset = _buffer.Length;
                await FetchAsync(cancellationToken).ConfigureAwait(false);
            }
            _offset = _buffer.Length;
            _disposed = true;
        }
        finally { _readGate.Release(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DrainAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await DrainAsync(CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
