using JustyBase.Db2.Protocol;
using JustyBase.Db2.Converters;
using JustyBase.Db2.Types;
using Xunit;
namespace JustyBase.Db2.Tests;

public sealed class DdmBufferWriterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(32755)]
    [InlineData(32759)]
    [InlineData(32763)]
    [InlineData(32764)]
    [InlineData(65535)]
    public void NestedBackpatchesMatchOwnedEncoder(int length)
    {
        var data = new byte[length]; for (var i = 0; i < data.Length; i++) data[i] = (byte)i;
        using var writer = new DdmBufferWriter();
        var outer = writer.BeginObject(CodePoint.SQLDTA);
        var inner = writer.BeginObject(CodePoint.FDODTA); writer.Write(data); writer.EndObject(inner);
        writer.EndObject(outer);
        Assert.Equal(DdmCodec.PackObject(CodePoint.SQLDTA, DdmCodec.PackObject(CodePoint.FDODTA, data)), writer.ToArray());
    }
    [Theory]
    [InlineData("-0.00", 8, 2)]
    [InlineData("123.4567", 8, 2)]
    [InlineData("-123.45", 31, 20)]
    [InlineData("0.0000000000000000000000000001", 31, 31)]
    [InlineData("79228162514264337593543950335", 31, 0)]
    public void DecimalBitsPathMatchesTextCompatibilityEncoder(string text, ushort precision, ushort scale)
    {
        var number = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        Db2ColumnMetadata[] columns = [new("D", SqlType.Decimal, 16, precision, scale, false)];
        // Decimal formatting's signed-zero normalization is the historical typed-input behavior.
        Assert.Equal(SqlDtaEncoder.Build(columns, [number.ToString(System.Globalization.CultureInfo.InvariantCulture)]), SqlDtaEncoder.Build(columns, [number]));
    }
    [Fact]
    public void DateTimeSpanFormattingMatchesHistoricalInvariantText()
    {
        Db2ColumnMetadata[] columns = [new("D", SqlType.Date, 10, 0, 0, false),
            new("T", SqlType.Time, 8, 0, 0, false), new("TS", SqlType.Timestamp, 32, 0, 6, false)];
        var date = new DateTime(2026, 2, 28, 12, 34, 56).AddTicks(1234567);
        Assert.Equal(SqlDtaEncoder.Build(columns, ["2026-02-28", "12:34:56", "2026-02-28-12.34.56.123456"]), SqlDtaEncoder.Build(columns, [date, date, date]));
    }
}
