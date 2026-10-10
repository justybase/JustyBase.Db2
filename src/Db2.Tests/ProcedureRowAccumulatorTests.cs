using JustyBase.Db2.Protocol;
using Xunit;
namespace JustyBase.Db2.Tests;
public sealed class ProcedureRowAccumulatorTests
{
    [Fact]
    public void SplitRowsKeepTheirTailInTheSameOwnedBuffer()
    {
        FieldDescriptor[] fields = [new(2, 0, 4)];
        byte[] data = [0xff, 0, 1, 0, 0, 0, 0xff, 0, 2, 0, 0, 0];
        for (var split = 1; split < data.Length; split++)
        {
            using var pending = new Db2RowAccumulator(); pending.Append(data.AsSpan(0, split));
            var first = Db2Session.DecodeRowsWithWarnings(fields, pending.Data); pending.Consume(first.Consumed);
            Assert.Equal(split - first.Consumed, pending.PendingBytes);
            pending.Append(data.AsSpan(split));
            var last = Db2Session.DecodeRowsWithWarnings(fields, pending.Data); pending.Consume(last.Consumed);
            Assert.Equal(new[] { 1, 2 }, first.Rows.Concat(last.Rows).Select(row => (int)row.Values[0]!));
            Assert.Equal(0, pending.PendingBytes);
        }
    }
    [Fact]
    public void GrowthAndCompactionPreserveBytesAndRejectInvalidConsumption()
    {
        using var pending = new Db2RowAccumulator(); var bytes = new byte[100000];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;
        pending.Append(bytes); pending.Consume(90000);
        Assert.Equal(bytes.AsSpan(90000).ToArray(), pending.Data.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => pending.Consume(10001));
        Assert.Throws<ArgumentOutOfRangeException>(() => pending.Consume(-1));
        pending.Append(bytes); Assert.Equal(110000, pending.PendingBytes);
    }
}
