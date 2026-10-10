using System.Buffers.Binary;
using JustyBase.Db2.Protocol;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class DssWriterPerformanceTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(32760)]
    [InlineData(32761)]
    [InlineData(32762)]
    [InlineData(65526)]
    [InlineData(65527)]
    [InlineData(200000)]
    public void DssSegmentBoundariesMatchIndependentWriter(int size)
    {
        var payload = new byte[size];
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), (ushort)CodePoint.SQLDTA);
        for (var i = 4; i < size; i++) payload[i] = (byte)i;
        foreach (var sameId in new[] { false, true })
        foreach (var last in new[] { false, true })
        {
            var encoded = DssCodec.EncodeRequest(payload, ushort.MaxValue, sameId, last);
            Assert.Equal(sameId ? ushort.MaxValue : (ushort)0, encoded.NextCorrelationId);
            Assert.Equal(Reference(payload, (byte)(3 | (last ? 0 : 0x40) | (sameId ? 0x10 : 0)), ushort.MaxValue), encoded.Bytes);
        }
        Assert.Equal(Reference(payload, 0x42, 7), DssCodec.EncodeReply(payload, true, 7));
    }

    private static byte[] Reference(byte[] payload, byte flags, ushort id)
    {
        using var output = new MemoryStream();
        var offset = 0;
        var first = true;
        while (offset < payload.Length)
        {
            var count = Math.Min(first ? 32761 : 32765, payload.Length - offset);
            var length = count + (first ? 6 : 2);
            if (offset + count != payload.Length) length |= 0x8000;
            output.WriteByte((byte)(length >> 8)); output.WriteByte((byte)length);
            if (first)
            {
                output.WriteByte(0xd0); output.WriteByte(flags);
                output.WriteByte((byte)(id >> 8)); output.WriteByte((byte)id);
            }
            output.Write(payload, offset, count); offset += count; first = false;
        }
        return output.ToArray();
    }
}
