using JustyBase.Db2.Protocol;
using Xunit;
namespace JustyBase.Db2.Tests;

public sealed class RowLayoutTests
{
    [Fact]
    public void IncompleteRowRestoresOffsetWithoutExceptionsOrAllocations()
    {
        var plans = Db2ColumnDecoder.Create([new FieldDescriptor(0x03, 0, 4), new FieldDescriptor(0x3f, 0, 99)]);
        byte[] bytes = [0xff, 0, 0, 42, 0, 0, 0, 0, 0, 3, 65, 66, 67];
        for (var prefix = 0; prefix < bytes.Length; prefix++)
        {
            var offset = 0;
            Assert.False(Db2Session.TryMeasureRow(plans, bytes.AsSpan(0, prefix), ref offset, out _, out _));
            Assert.Equal(0, offset);
            Assert.Empty(Db2Session.DecodeRowsWithWarnings([new FieldDescriptor(0x03, 0, 4), new FieldDescriptor(0x3f, 0, 99)], bytes.AsSpan(0, prefix).ToArray()).Rows);
        }
        for (var i = 0; i < 10000; i++) { var offset = 0; Db2Session.TryMeasureRow(plans, bytes.AsSpan(0, 12), ref offset, out _, out _); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) { var offset = 0; Db2Session.TryMeasureRow(plans, bytes.AsSpan(0, 12), ref offset, out _, out _); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        var position = 0;
        Assert.True(Db2Session.TryMeasureRow(plans, bytes, ref position, out var fields, out var warning));
        Assert.Equal(2, fields); Assert.Equal(bytes.Length, position); Assert.Null(warning);
    }

    [Theory]
    [InlineData(0xff)]
    [InlineData(0x80)]
    [InlineData(0x7f)]
    public void NullIndicatorsSkipValueBytes(int indicator)
    {
        var plans = Db2ColumnDecoder.Create([new FieldDescriptor(0x03, 0, 4), new FieldDescriptor(0x3f, 0, 99)]);
        byte[] bytes = [0xff, 0, (byte)indicator, (byte)indicator];
        var offset = 0;
        Assert.True(Db2Session.TryMeasureRow(plans, bytes, ref offset, out _, out _));
        Assert.Equal(bytes.Length, offset);
    }
}
