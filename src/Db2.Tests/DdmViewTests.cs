using JustyBase.Db2.Protocol;
using Xunit;
namespace JustyBase.Db2.Tests;

public sealed class DdmViewTests
{
    [Fact]
    public void ViewsKeepLastDuplicateAndValidateTrailingObjects()
    {
        byte[] data = [..DdmCodec.PackUInt64(CodePoint.QRYINSID, 1), ..DdmCodec.PackUInt64(CodePoint.QRYINSID, 2)];
        Assert.True(DdmCodec.TryGetParameter(data, (ushort)CodePoint.QRYINSID, out var value));
        Assert.Equal(DdmCodec.ParseParameters(data)[(ushort)CodePoint.QRYINSID], value.ToArray());
        Assert.False(DdmCodec.TryGetParameter(data, (ushort)CodePoint.RDBNAM, out _));
        byte[] invalid = [..data, 0, 4];
        Assert.Throws<InvalidDataException>(() => DdmCodec.TryGetParameter(invalid, (ushort)CodePoint.QRYINSID, out _));
    }

    [Fact]
    public void NestedLengthCannotWrapUInt64()
    {
        byte[] data = [0x80, 0x0c, 0x21, 0x13, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff];
        Assert.Throws<InvalidDataException>(() => DdmCodec.ParseResultSetSections(data));
    }
}
