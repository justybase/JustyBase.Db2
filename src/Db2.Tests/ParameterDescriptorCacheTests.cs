using JustyBase.Db2.Converters;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;
using Xunit;
namespace JustyBase.Db2.Tests;
public sealed class ParameterDescriptorCacheTests
{
    [Fact]
    public void CachedDescriptorTracksColumnReplacementAndBinaryCharacterBinding()
    {
        Db2ColumnMetadata[] columns = [new("C", SqlType.NVarChar, 100, 0, 0, true)];
        var cache = new SqlDtaEncoder.DescriptorCache();
        foreach (object? value in new object?[] { "hello", "world", new byte[] { 1, 2 }, null, DBNull.Value, "text" })
            Assert.Equal(SqlDtaEncoder.Build(columns, [value]), SqlDtaEncoder.BuildWithExternalData(columns, [value], cache).SqlDta);
        columns[0] = new("I", SqlType.NInteger, 4, 10, 0, true);
        Assert.Equal(SqlDtaEncoder.Build(columns, [42]), SqlDtaEncoder.BuildWithExternalData(columns, [42], cache).SqlDta);
    }
    [Fact]
    public void CachedBlobDescriptorTracksNullAndExternalBindings()
    {
        Db2ColumnMetadata[] columns = [new("B", SqlType.NBlob, 1000, 0, 0, true)];
        var cache = new SqlDtaEncoder.DescriptorCache();
        foreach (object? value in new object?[] { null, new byte[] { 1, 2 }, new byte[] { 3 }, DBNull.Value, null })
        {
            var expected = SqlDtaEncoder.BuildWithExternalData(columns, [value]);
            var actual = SqlDtaEncoder.BuildWithExternalData(columns, [value], cache);
            Assert.Equal(expected.SqlDta, actual.SqlDta); Assert.Equal(expected.ExternalData.Count, actual.ExternalData.Count);
            for (var i = 0; i < expected.ExternalData.Count; i++) Assert.Equal(expected.ExternalData[i].DdmObject, actual.ExternalData[i].DdmObject);
        }
    }
    [Theory]
    [InlineData(1)]
    [InlineData(84)]
    [InlineData(256)]
    [InlineData(6000)]
    public void CachedMultiGroupAndExtendedDescriptorsRemainByteIdentical(int count)
    {
        var columns = Enumerable.Repeat(new Db2ColumnMetadata("I", SqlType.NInteger, 4, 10, 0, true), count).ToArray();
        var values = Enumerable.Repeat<object?>(42, count).ToArray(); var cache = new SqlDtaEncoder.DescriptorCache();
        var expected = SqlDtaEncoder.Build(columns, values);
        Assert.Equal(expected, SqlDtaEncoder.BuildWithExternalData(columns, values, cache).SqlDta);
        Assert.Equal(expected, SqlDtaEncoder.BuildWithExternalData(columns, values, cache).SqlDta);
    }
    [Theory]
    [InlineData(409)]
    [InlineData(413)]
    [InlineData(989)]
    public void LargeMaterializedTextLobUsesUtf8ExternalDataAndCacheTracksTransitions(int sqlType)
    {
        Db2ColumnMetadata[] columns = [new("L", (SqlType)sqlType, 2_000_000, 0, 0, true)];
        var cache = new SqlDtaEncoder.DescriptorCache();
        foreach (var value in new string?[] { "", new string('x', 65535), "東京🚀" + new string('x', 65536), null, "small" })
        {
            var encoded = SqlDtaEncoder.BuildWithExternalData(columns, [value], cache);
            var fresh = SqlDtaEncoder.BuildWithExternalData(columns, [value]);
            Assert.Equal(fresh.SqlDta, encoded.SqlDta);
            Assert.Equal(value is { Length: > 65535 } ? 1 : 0, encoded.ExternalData.Count);
            if (encoded.ExternalData.Count == 1)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(value!);
                var ddm = encoded.ExternalData[0].DdmObject!;
                Assert.Equal(bytes, ddm.AsSpan(ddm.Length - bytes.Length).ToArray());
                Assert.True(encoded.SqlDta.Length < 256);
            }
        }
    }

}
