using JustyBase.Db2.Protocol;
using Xunit;
namespace JustyBase.Db2.Tests;

public sealed class DssReadBufferTests
{
    [Theory]
    [InlineData("open-query.bin", 1)]
    [InlineData("open-query.bin", 5)]
    [InlineData("open-query.bin", 65536)]
    [InlineData("continued-dss.bin", 1)]
    [InlineData("continued-dss.bin", 2)]
    [InlineData("continued-dss.bin", 65536)]
    [InlineData("extended-extdta.bin", 1)]
    [InlineData("null-warning.bin", 3)]
    public async Task BufferedReaderMatchesOwnedPacketAdapterForGoldenFrames(string name, int fragment)
    {
        var data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Drda", name));
        using var reference = new MemoryStream(data);
        using var stream = new FragmentedStream(data, fragment);
        using var reader = new DssReadBuffer(stream);
        while (reference.Position < reference.Length)
        {
            var expected = await DssCodec.ReadAsync(reference);
            var actual = await reader.ReadAsync();
            Assert.Equal(expected.Header, actual.Header);
            Assert.Equal(expected.CodePoint, actual.CodePoint);
            Assert.Equal(expected.Payload, actual.Payload);
        }
        if (fragment == 65536) Assert.Equal(1, stream.Reads);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadAsync());
    }

    [Theory]
    [InlineData("000AD001000100022412")]
    [InlineData("0004D0010001")]
    [InlineData("000AAA010001")]
    [InlineData("800AD0010001000424120001")]
    public async Task BufferedReaderKeepsMalformedFrameValidation(string bytes)
    {
        using var stream = new MemoryStream(Convert.FromHexString(bytes));
        using var reader = new DssReadBuffer(stream);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadAsync());
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public async Task LargeOwnedContinuationPreservesFollowingBufferedPacket(int fragment)
    {
        var payload = new byte[200000];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        var first = DssCodec.EncodeReply(DdmCodec.PackObject(CodePoint.EXTDTA, payload), true, 1);
        var last = DssCodec.EncodeReply(DdmCodec.PackObject(CodePoint.SQLCARD, new byte[] { 1, 2, 3 }), false, 2);
        byte[] wire = [..first, ..last];
        using var stream = new FragmentedStream(wire, fragment);
        using var reader = new DssReadBuffer(stream);
        var packet = await reader.ReadAsync();
        Assert.Equal((ushort)CodePoint.EXTDTA, packet.CodePoint);
        Assert.Equal(payload, packet.Payload);
        var following = await reader.ReadAsync();
        Assert.Equal((ushort)CodePoint.SQLCARD, following.CodePoint);
        Assert.Equal(new byte[] { 1, 2, 3 }, following.Payload);
        // Owned bytes remain stable after the reusable session buffer advances.
        Assert.Equal(payload, packet.Payload);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadAsync());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(100000000)]
    public async Task ContinuedExtendedLengthKeepsOwnedCodecValidation(int lengthDelta)
    {
        var ddm = DdmCodec.PackObject(CodePoint.EXTDTA, new byte[200000]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ddm.AsSpan(4), (uint)(200000 + lengthDelta));
        var wire = DssCodec.EncodeReply(ddm, false, 1);
        using var original = new MemoryStream(wire);
        using var stream = new FragmentedStream(wire, 4096);
        using var reader = new DssReadBuffer(stream);
        if (lengthDelta < 0)
        {
            var expected = await DssCodec.ReadAsync(original);
            var actual = await reader.ReadAsync();
            Assert.Equal(expected.Payload, actual.Payload);
        }
        else
        {
            var expected = await Assert.ThrowsAsync<InvalidDataException>(async () => await DssCodec.ReadAsync(original));
            var actual = await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadAsync());
            Assert.Equal(expected.Message, actual.Message);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task OwnedContinuationSupportsEveryExistingExtendedLengthWidth(int extension)
    {
        var payload = new byte[200000];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        var ddm = new byte[4 + extension + payload.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(ddm, (ushort)(0x8000 | (4 + extension)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(ddm.AsSpan(2), (ushort)CodePoint.EXTDTA);
        var length = extension == 1 ? 32UL : extension == 2 ? 60000UL : 200000UL;
        for (var i = 0; i < extension; i++) ddm[4 + extension - 1 - i] = (byte)(length >> (i * 8));
        payload.CopyTo(ddm, 4 + extension);
        var wire = DssCodec.EncodeReply(ddm, false, 1);
        using var reference = new MemoryStream(wire);
        using var stream = new FragmentedStream(wire, 997);
        using var reader = new DssReadBuffer(stream);
        var expected = await DssCodec.ReadAsync(reference);
        var actual = await reader.ReadAsync();
        Assert.Equal(expected.Header, actual.Header);
        Assert.Equal(expected.Payload, actual.Payload);
    }

    private sealed class FragmentedStream(byte[] bytes, int size) : MemoryStream(bytes)
    {
        internal int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { Reads++; return base.ReadAsync(buffer[..Math.Min(buffer.Length, size)], token); }
    }
}
