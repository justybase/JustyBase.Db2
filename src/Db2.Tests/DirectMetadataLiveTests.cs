using Xunit;
namespace JustyBase.Db2.Tests;

public sealed class DirectMetadataLiveTests
{
    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task DirectOpenDocumentsAvailabilityOfNamedResultMetadata()
    {
        await using var session = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var prepared = await session.PrepareAsync("SELECT 42 AS NAMED_VALUE FROM SYSIBM.SYSDUMMY1");
        Assert.Equal("NAMED_VALUE", Assert.Single(prepared.OutputColumns).Name);
        await foreach (var row in session.QueryStreamAsync("SELECT 42 AS NAMED_VALUE FROM SYSIBM.SYSDUMMY1"))
        {
            Assert.Equal(42, row.Values[0]);
            Assert.Equal("NAMED_VALUE", Assert.Single(row.Columns).Name);
        }
    }
}
