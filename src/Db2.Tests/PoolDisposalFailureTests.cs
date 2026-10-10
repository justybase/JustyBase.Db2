using System.Net.Sockets;
using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class PoolDisposalFailureTests
{
    [Fact]
    public async Task ReturnDisposalFailurePreservesOperationErrorAndReleasesPoolSlot()
    {
        var disposeError = new IOException("stream dispose failed");
        var session = CreateSession(new DisposalFailureStream(null, disposeError), baselineCaptured: true);
        var factoryCalls = 0;
        var factoryError = new InvalidOperationException("replacement factory called");
        await using var pool = new Db2Pool(_ =>
        {
            if (Interlocked.Increment(ref factoryCalls) == 1) return Task.FromResult(session);
            return Task.FromException<Db2Session>(factoryError);
        }, new Db2PoolOptions(1));
        var operationError = new InvalidOperationException("operation failed");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.WithSessionAsync<int>((leased, _) =>
        {
            leased.MarkTransactionAbandoned();
            return Task.FromException<int>(operationError);
        }));

        Assert.Same(operationError, thrown);
        Assert.Same(disposeError, thrown.Data["Db2CleanupError"]);
        Assert.Equal(0, pool.SessionCount);
        Assert.Equal(0, pool.IdleSessionCount);

        var replacementError = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.WithSessionAsync((_, _) => Task.FromResult(1)));
        Assert.Same(factoryError, replacementError);
        Assert.Equal(0, pool.SessionCount);
    }

    [Fact]
    public async Task AcquireDisposalFailurePreservesBaselineErrorAndReleasesPoolSlot()
    {
        var baselineError = new IOException("baseline read failed");
        var disposeError = new IOException("stream dispose failed");
        var session = CreateSession(new DisposalFailureStream(baselineError, disposeError));
        var factoryCalls = 0;
        var factoryError = new InvalidOperationException("replacement factory called");
        await using var pool = new Db2Pool(_ =>
        {
            if (Interlocked.Increment(ref factoryCalls) == 1) return Task.FromResult(session);
            return Task.FromException<Db2Session>(factoryError);
        }, new Db2PoolOptions(1));

        var thrown = await Assert.ThrowsAsync<IOException>(() => pool.WithSessionAsync((_, _) => Task.FromResult(1)));

        Assert.Same(baselineError, thrown);
        Assert.Same(disposeError, thrown.Data["Db2CleanupError"]);
        Assert.Equal(0, pool.SessionCount);
        Assert.Equal(0, pool.IdleSessionCount);

        var replacementError = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.WithSessionAsync((_, _) => Task.FromResult(1)));
        Assert.Same(factoryError, replacementError);
        Assert.Equal(0, pool.SessionCount);
    }

    private static Db2Session CreateSession(Stream stream, bool baselineCaptured = false) =>
        new(new TcpClient(), stream, new Db2Config { Database = "TESTDB", User = "db2inst1" }, baselineCaptured);

    private sealed class DisposalFailureStream(Exception? readError, Exception disposeError) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw readError ?? new NotSupportedException();
        public override int Read(Span<byte> buffer) => throw readError ?? new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(readError ?? new NotSupportedException());
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(readError ?? new NotSupportedException());
        public override void Write(byte[] buffer, int offset, int count) { }
        public override void Write(ReadOnlySpan<byte> buffer) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() => ValueTask.FromException(disposeError);
    }
}
