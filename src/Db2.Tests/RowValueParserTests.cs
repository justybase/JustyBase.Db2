using System.Globalization;
using System.Text;
using JustyBase.Db2.Converters;
using Xunit;
namespace JustyBase.Db2.Tests;

public sealed class RowValueParserTests
{
    [Theory]
    [InlineData("000000000D", 2, "-0.00")]
    [InlineData("000012345C", 2, "123.45")]
    [InlineData("000012345D", 2, "-123.45")]
    [InlineData("000012345B", 2, "-123.45")]
    [InlineData("000012345F", 0, "12345")]
    [InlineData("10000000000000000000000000000C", 28, "1.0000000000000000000000000000")]
    public void PackedDecimalPreservesScaleAndSignedZero(string hex, int scale, string text)
    {
        Assert.True(RowValueParser.TryDecimal(Convert.FromHexString(hex), scale, out var value));
        Assert.Equal(decimal.GetBits(decimal.Parse(text, CultureInfo.InvariantCulture)), decimal.GetBits(value));
    }
    [Theory]
    [InlineData("2026-01-01-12.34.56.123456", "2026-01-01T12:34:56.1234560")]
    [InlineData("2026-01-01-12.34.56.1234567", "2026-01-01T12:34:56.1234567")]
    [InlineData("2026-01-01-24.00.00.000000", "2026-01-02T00:00:00")]
    public void TimestampReadsDigitsAndEndOfDayWithoutStrings(string bytes, string expected)
    {
        Assert.True(RowValueParser.TryTimestamp(Encoding.ASCII.GetBytes(bytes), out var value));
        Assert.Equal(DateTime.Parse(expected, CultureInfo.InvariantCulture), value);
    }
    [Theory]
    [InlineData("9999-12-31-24.00.00")]
    [InlineData("2026-02-30-12.00.00")]
    [InlineData("2026-01-01-24.00.01")]
    [InlineData("2026-01-01-12.00.00.12345678")]
    public void NoncanonicalOrUnrepresentableTimestampUsesCompatibilityFallback(string text)
        => Assert.False(RowValueParser.TryTimestamp(Encoding.ASCII.GetBytes(text), out _));
}
