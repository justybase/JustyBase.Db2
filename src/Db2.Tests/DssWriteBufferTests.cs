using JustyBase.Db2.Protocol;
using Xunit;
namespace JustyBase.Db2.Tests;

public sealed class DssWriteBufferTests
{
    [Fact]
    public async Task ChainUsesOneWriteAndKeepsIndependentFraming()
    {
        using var writer = new DssWriteBuffer();
        using var destination = new CountingStream();
        var first = DdmCodec.PackObject(CodePoint.PRPSQLSTT, [1, 2]);
        var second = DdmCodec.PackObject(CodePoint.SQLSTT, [3, 4]);
        var third = DdmCodec.PackObject(CodePoint.OPNQRY, [5, 6]);
        var id = writer.Append(first, 1, true, false);
        id = writer.Append(second, id, false, false);
        id = writer.Append(third, id, false, true);
        Assert.Equal((ushort)3, id);
        await writer.FlushAsync(destination, default);
        Assert.Equal(1, destination.Writes);
        Assert.Equal((byte[])[..DssCodec.EncodeRequest(first, 1, true, false).Bytes,
            ..DssCodec.EncodeRequest(second, 1, false, false).Bytes,
            ..DssCodec.EncodeRequest(third, 2, false, true).Bytes], destination.ToArray());
        writer.Append(first, id, false, true);
        await writer.FlushAsync(destination, default);
        Assert.Equal(2, destination.Writes);
    }

    [Fact]
    public async Task DisposalWaitsForOutstandingWriteBeforeReturningMemory()
    {
        var writer = new DssWriteBuffer();
        var payload = DdmCodec.PackObject(CodePoint.SECCHK, [0xaa, 0xbb]);
        var destination = new DelayedStream();
        writer.Append(payload, 1, false, true);
        var flush = writer.FlushAsync(destination, default);
        writer.Dispose();
        Assert.Equal(DssCodec.EncodeRequest(payload, 1, false, true).Bytes, destination.Pending.ToArray());
        destination.Complete.SetResult();
        await flush;
        Assert.Throws<ObjectDisposedException>(() => writer.Append(payload, 1, false, true));
    }

    private sealed class CountingStream : MemoryStream
    {
        internal int Writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Writes++; Write(buffer.Span); return ValueTask.CompletedTask; }
    }
    private sealed class DelayedStream : MemoryStream
    {
        internal ReadOnlyMemory<byte> Pending { get; private set; }
        internal TaskCompletionSource Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Pending = buffer; return new ValueTask(Complete.Task); }
    }
}
