using JustyBase.Db2.Protocol;
using Xunit;
namespace JustyBase.Db2.Tests;

public sealed class DssReplyChainTests
{
    [Theory]
    [InlineData("open-query.bin", 1)]
    [InlineData("open-query.bin", 65536)]
    [InlineData("continued-dss.bin", 2)]
    [InlineData("extended-extdta.bin", 5)]
    [InlineData("null-warning.bin", 3)]
    public async Task BorrowedChainMatchesOwnedGoldenPackets(string name, int fragment)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Drda", name));
        using var reference = new MemoryStream(bytes);
        using var fragmented = new FragmentedStream(bytes, fragment);
        using var reader = new DssReadBuffer(fragmented);
        while (reference.Position < reference.Length)
        {
            using var chain = await reader.ReadChainAsync();
            for (var i = 0; i < chain.Count; i++)
            {
                var expected = await DssCodec.ReadAsync(reference);
                var actual = chain[i];
                Assert.Equal(expected.Header, actual.Header.ToHeader());
                Assert.Equal(expected.CodePoint, actual.CodePoint);
                Assert.True(expected.Payload.AsSpan().SequenceEqual(actual.Payload.Span));
                Assert.Equal(i < chain.Count - 1, expected.Header.Chained);
            }
        }
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadChainAsync());
    }

    [Fact]
    public async Task ReplyChainLimitsFailClosed()
    {
        const int packets = (int)DssReadBuffer.MaxReplyChainPackets + 1;
        var body = DdmCodec.PackObject(CodePoint.SQLCARD, ReadOnlySpan<byte>.Empty);
        var bytes = new MemoryStream();
        for (var index = 0; index < packets; index++)
        {
            var length = (ushort)(6 + body.Length);
            var chained = index < packets - 1;
            bytes.Write(new byte[] { (byte)(length >> 8), (byte)length, 0xD0, (byte)(chained ? DssCodec.ChainedFlag : 0x00), 0x00, 0x01 });
            bytes.Write(body);
        }
        bytes.Position = 0;
        using var reader = new DssReadBuffer(bytes);
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadChainAsync());
        Assert.Contains("reply chain exceeds configured limits", error.Message);
    }

    [Fact]
    public async Task ChainLeasePreventsReuseAndStaleDisposalCannotReleaseNextChain()
    {
        var bytes = DssCodec.EncodeReply(DdmCodec.PackObject(CodePoint.QRYDTA, [42]), false, 1);
        using var stream = new MemoryStream([..bytes, ..bytes]);
        using var reader = new DssReadBuffer(stream);
        var first = await reader.ReadChainAsync();
        Assert.Equal(42, first[0].Payload.Span[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.ReadChainAsync());
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => first[0]);
        using var second = await reader.ReadChainAsync();
        first.Dispose();
        Assert.Equal(42, second[0].Payload.Span[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.ReadAsync());
    }

    [Fact]
    public async Task BorrowedPacketsAvoidPayloadSizedWarmAllocationsOnSynchronousStream()
    {
        var bytes = DssCodec.EncodeReply(DdmCodec.PackObject(CodePoint.QRYDTA, new byte[32000]), false, 1);
        using var stream = new MemoryStream(bytes);
        using var reader = new DssReadBuffer(stream);
        for (var i = 0; i < 1000; i++) { stream.Position = 0; using var chain = await reader.ReadChainAsync(); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) { stream.Position = 0; using var chain = await reader.ReadChainAsync(); }
        // Debug builds allocate a small async state-machine/continuation object
        // per read. Keep the regression guard focused on payload copies: a
        // 32 KiB packet must not cause an allocation remotely close to its size.
        var allocatedPerRead = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
        Assert.InRange(allocatedPerRead, 0, 512);
    }

    private sealed class FragmentedStream(byte[] bytes, int size) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, size)], token);
    }
}
