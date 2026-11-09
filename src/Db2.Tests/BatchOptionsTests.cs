using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class BatchOptionsTests
{
    [Fact]
    public void DefaultsMatchJdc()
    {
        var options = new Db2BatchOptions();
        Assert.Equal(256, options.MaxRows);
        Assert.Equal(8 * 1024 * 1024, options.MaxEncodedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(32768)]
    public void MaxRowsOutsideTheDb2LimitIsRejected(int maxRows)
    {
        var options = new Db2BatchOptions { MaxRows = maxRows };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveByteLimitIsRejected(long maxEncodedBytes)
    {
        var options = new Db2BatchOptions { MaxEncodedBytes = maxEncodedBytes };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }
}
