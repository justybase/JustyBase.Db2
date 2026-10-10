using System.Buffers.Binary;
using JustyBase.Db2.Protocol;
using Xunit;
namespace JustyBase.Db2.Tests;
public sealed class DssStreamTests
{
    [Fact]
    public async Task ContinuedDdmMatchesPayloadWithBoundedChunks()
    {
        var payload = Enumerable.Repeat((byte)0xab, 100_000).ToArray();
        var ddm = new byte[payload.Length + 8];
        BinaryPrimitives.WriteUInt16BigEndian(ddm, 0x8008); BinaryPrimitives.WriteUInt16BigEndian(ddm.AsSpan(2), 0x146c);
        BinaryPrimitives.WriteUInt32BigEndian(ddm.AsSpan(4), (uint)payload.Length); payload.CopyTo(ddm, 8);
        var wire = DssCodec.EncodeRequest(ddm, 42, false, true).Bytes;
        using var input = new MemoryStream(wire); using var result = new MemoryStream();
        var packet = await Db2DssStream.OpenAsync(input);
        Assert.Equal(0x146c, packet.CodePoint); Assert.Equal(42, packet.Header.CorrelationId);
        while (await packet.NextChunkAsync() is { } chunk) { Assert.True(chunk.Length <= 64 * 1024); result.Write(chunk); }
        Assert.Equal(payload, result.ToArray()); Assert.Equal(input.Length, input.Position);
    }
    [Fact]
    public async Task DeclaredOversizeIsRejectedBeforePayloadRead()
    {
        using var input = new MemoryStream(new byte[] { 0,14,0xd0,2,0,1,0x80,8,0x14,0x6c,0,0,1,0 });
        await Assert.ThrowsAsync<Db2ResourceLimitException>(() => Db2DssStream.OpenAsync(input, 255));
        Assert.Equal(14, input.Position);
    }
}
