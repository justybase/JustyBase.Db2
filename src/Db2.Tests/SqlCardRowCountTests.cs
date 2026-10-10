using System.Buffers.Binary;
using System.Text;
using JustyBase.Db2.Protocol;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class SqlCardRowCountTests
{
    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 257)]
    [InlineData(false, 257)]
    [InlineData(false, -1)]
    public void ReadsSqlerrdThreeAfterTheNullableExtensionIndicator(bool littleEndian, int count)
    {
        var card = new byte[54];
        Encoding.ASCII.GetBytes("00000").CopyTo(card, 5);
        for (var i = 0; i < 6; i++)
        {
            var value = i == 2 ? count : 0x123456 + i;
            if (littleEndian) BinaryPrimitives.WriteInt32LittleEndian(card.AsSpan(19 + i * 4), value);
            else BinaryPrimitives.WriteInt32BigEndian(card.AsSpan(19 + i * 4), value);
        }
        Assert.Equal(count, DdmCodec.ParseSqlCard(card, littleEndian).RowsAffected);
        card[18] = 0xff;
        Assert.Equal(0, DdmCodec.ParseSqlCard(card, littleEndian).RowsAffected);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(26)]
    [InlineData(31)]
    [InlineData(53)]
    public void RejectsTruncatedPresentExtension(int length)
        => Assert.Throws<InvalidDataException>(() => DdmCodec.ParseSqlCard(new byte[length]));
}
