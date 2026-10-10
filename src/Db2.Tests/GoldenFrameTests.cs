using System.Buffers.Binary;
using JustyBase.Db2;
using JustyBase.Db2.Protocol;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class GoldenFrameTests
{
    [Fact]
    public void ReplyEncodersMatchTheIndependentOpenQueryBytes()
    {
        byte[] descriptor = [6, 0x76, 0xd0, 0x02, 0, 4];
        byte[] queryId = [0, 0, 0, 0, 0, 0, 0, 1];
        var packets = new[]
        {
            DssCodec.EncodeReply(DdmCodec.PackObject(CodePoint.QRYDSC, descriptor), true, 1),
            DssCodec.EncodeReply(DdmCodec.PackObject(CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, queryId)), true, 1),
            DssCodec.EncodeReply(DdmCodec.PackObject(CodePoint.QRYDTA, [0xff, 0, 42, 0, 0, 0]), true, 1),
            DssCodec.EncodeReply(DdmCodec.PackObject(CodePoint.ENDQRYRM, []), false, 1),
        };
        Assert.Equal(Load("open-query.bin"), packets.SelectMany(packet => packet).ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(4096)]
    public async Task OpenQueryKeepsMetadataCursorAndRowAcrossTransportFragments(int fragmentSize)
    {
        await using var stream = new FragmentedStream(Load("open-query.bin"), fragmentSize);
        var descriptor = await DssCodec.ReadAsync(stream);
        Assert.Equal((ushort)CodePoint.QRYDSC, descriptor.CodePoint);
        Assert.True(descriptor.Header.Chained);
        var fields = DdmCodec.ParseQueryDescriptor(descriptor.Payload);
        Assert.Equal(new FieldDescriptor(0x02, 0, 4), Assert.Single(fields));
        var open = await DssCodec.ReadAsync(stream);
        Assert.Equal((ushort)CodePoint.OPNQRYRM, open.CodePoint);
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64BigEndian(DdmCodec.ParseParameters(open.Payload)[(ushort)CodePoint.QRYINSID]));
        var data = await DssCodec.ReadAsync(stream);
        Assert.Equal((ushort)CodePoint.QRYDTA, data.CodePoint);
        Assert.Equal(42, Assert.Single(Assert.Single(Db2Session.DecodeRowsIncremental(fields, data.Payload).Rows)));
        var end = await DssCodec.ReadAsync(stream);
        Assert.Equal((ushort)CodePoint.ENDQRYRM, end.CodePoint);
        Assert.False(end.Header.Chained);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task PartialQueryRowIsNotConsumedUntilNextBlockArrives()
    {
        FieldDescriptor[] fields = [new(0x02, 0, 4)];
        var first = await DssCodec.ReadAsync(new MemoryStream(Load("split-row-first.bin")));
        var partial = Db2Session.DecodeRowsIncremental(fields, first.Payload);
        Assert.Empty(partial.Rows);
        Assert.Equal(0, partial.Consumed);
        var last = await DssCodec.ReadAsync(new MemoryStream(Load("split-row-last.bin")));
        var decoded = Db2Session.DecodeRowsIncremental(fields, [.. first.Payload, .. last.Payload]);
        Assert.Equal(new[] { 42, 43 }, decoded.Rows.Select(row => Assert.IsType<int>(Assert.Single(row))));
        Assert.Equal(first.Payload.Length + last.Payload.Length, decoded.Consumed);
    }

    [Fact]
    public async Task EveryPrefixWithinAFieldLeavesItsRowUnconsumed()
    {
        var packet = await DssCodec.ReadAsync(new MemoryStream(Load("continued-dss.bin")));
        FieldDescriptor[] fields = [new(0x02, 0, 4)];
        for (var length = 0; length < packet.Payload.Length; length++)
        {
            var decoded = Db2Session.DecodeRowsIncremental(fields, packet.Payload[..length]);
            Assert.Equal(length / 6, decoded.Rows.Count);
            Assert.Equal(length / 6 * 6, decoded.Consumed);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task DssContinuationReassemblesTheIndependentGoldenBytes(int fragmentSize)
    {
        await using var stream = new FragmentedStream(Load("continued-dss.bin"), fragmentSize);
        var packet = await DssCodec.ReadAsync(stream);
        Assert.Equal((ushort)7, packet.Header.CorrelationId);
        Assert.Equal((ushort)CodePoint.QRYDTA, packet.CodePoint);
        Assert.Equal(Convert.FromHexString("FF002A000000FF002B000000"), packet.Payload);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task SqlcaWarningAndNullKeepTheirRowAssociation()
    {
        var packet = await DssCodec.ReadAsync(new MemoryStream(Load("null-warning.bin")));
        var decoded = Db2Session.DecodeRowsWithWarnings([new FieldDescriptor(0x03, 0, 4)], packet.Payload);
        Assert.Equal(packet.Payload.Length, decoded.Consumed);
        Assert.Equal(3, decoded.Rows.Count);
        Assert.Equal(40, Assert.Single(decoded.Rows[0].Values));
        Assert.Null(Assert.Single(decoded.Rows[1].Values));
        Assert.Equal(41, Assert.Single(decoded.Rows[2].Values));
        Assert.Equal("01003", decoded.Rows[0].Warning!.SqlState);
        Assert.Equal("W W        ", decoded.Rows[0].Warning!.WarningFlags);
        Assert.Null(decoded.Rows[1].Warning);
        Assert.Null(decoded.Rows[2].Warning);
    }

    [Fact]
    public async Task ExtendedDdmLengthPreservesExternalPayload()
    {
        var packet = await DssCodec.ReadAsync(new FragmentedStream(Load("extended-extdta.bin"), 1));
        Assert.Equal((ushort)CodePoint.EXTDTA, packet.CodePoint);
        Assert.Equal(new byte[] { 0, 0xde, 0xad, 0xbe, 0xef }, packet.Payload);
    }

    private static byte[] Load(string name) => File.ReadAllBytes(Path.Combine(
        Path.GetDirectoryName(typeof(GoldenFrameTests).Assembly.Location)!, "Fixtures", "Drda", name));

    private sealed class FragmentedStream(byte[] bytes, int fragmentSize) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, fragmentSize)], cancellationToken);
    }
}
