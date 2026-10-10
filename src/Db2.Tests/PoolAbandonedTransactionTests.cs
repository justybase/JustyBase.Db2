using System.Net.Sockets;
using JustyBase.Db2;
using JustyBase.Db2.Protocol;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class PoolAbandonedTransactionTests
{
    [Fact]
    public async Task RollbackFailureIsAttachedToPrimaryOperationErrorAndPoolSlotIsReleased()
    {
        var session = CreateSession();
        var factoryCalls = 0;
        var replacementError = new InvalidOperationException("replacement factory called");
        await using var pool = new Db2Pool(_ =>
        {
            if (Interlocked.Increment(ref factoryCalls) == 1) return Task.FromResult(session);
            return Task.FromException<Db2Session>(replacementError);
        }, new Db2PoolOptions(1));
        var operationError = new InvalidOperationException("operation failed");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.WithSessionAsync<int>(async (leased, token) =>
        {
            _ = await leased.BeginTransactionAsync(token);
            throw operationError;
        }));

        Assert.Same(operationError, thrown);
        var cleanup = Assert.IsType<Db2AbandonedTransactionException>(thrown.Data["Db2CleanupError"]);
        Assert.Equal(-911, Assert.IsType<Db2SqlException>(cleanup.RollbackError).SqlCode);
        Assert.Same(cleanup.RollbackError, cleanup.InnerException);
        Assert.Equal(0, pool.SessionCount);
        Assert.Equal(0, pool.IdleSessionCount);

        var next = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.WithSessionAsync((_, _) => Task.FromResult(1)));
        Assert.Same(replacementError, next);
        Assert.Equal(0, pool.SessionCount);
    }

    [Fact]
    public async Task SuccessfulCallbackWithUnfinishedTransactionFailsClearlyWhenRollbackFails()
    {
        var session = CreateSession();
        var factoryCalls = 0;
        var replacementError = new InvalidOperationException("replacement factory called");
        await using var pool = new Db2Pool(_ =>
        {
            if (Interlocked.Increment(ref factoryCalls) == 1) return Task.FromResult(session);
            return Task.FromException<Db2Session>(replacementError);
        }, new Db2PoolOptions(1));

        var thrown = await Assert.ThrowsAsync<Db2AbandonedTransactionException>(() => pool.WithSessionAsync(async (leased, token) =>
        {
            _ = await leased.BeginTransactionAsync(token);
            return 73;
        }));

        Assert.Equal(-911, Assert.IsType<Db2SqlException>(thrown.RollbackError).SqlCode);
        Assert.Same(thrown.RollbackError, thrown.InnerException);
        Assert.Equal(0, pool.SessionCount);
        Assert.Equal(0, pool.IdleSessionCount);

        var next = await Assert.ThrowsAsync<InvalidOperationException>(() => pool.WithSessionAsync((_, _) => Task.FromResult(1)));
        Assert.Same(replacementError, next);
        Assert.Equal(0, pool.SessionCount);
    }

    private static Db2Session CreateSession()
    {
        var card = new byte[54];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(card.AsSpan(1), -911);
        System.Text.Encoding.ASCII.GetBytes("40001").CopyTo(card, 5);
        var ddm = DdmCodec.PackObject(CodePoint.SQLCARD, card);
        var response = DssCodec.EncodeReply(ddm, chained: false, correlationId: 1);
        return new Db2Session(
            new TcpClient(),
            new RollbackResponseStream(response),
            new Db2Config { Database = "TESTDB", User = "db2inst1" },
            poolBaselineCaptured: true);
    }

    private sealed class RollbackResponseStream(byte[] response) : Stream
    {
        private int _readOffset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => response.Length;
        public override long Position { get => _readOffset; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, response.Length - _readOffset);
            response.AsSpan(_readOffset, count).CopyTo(buffer);
            _readOffset += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));
        public override void Write(byte[] buffer, int offset, int count) { }
        public override void Write(ReadOnlySpan<byte> buffer) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
