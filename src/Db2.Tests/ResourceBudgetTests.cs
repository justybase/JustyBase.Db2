using Xunit;
namespace JustyBase.Db2.Tests;
public sealed class ResourceBudgetTests
{
    [Fact]
    public async Task SpoolsShareQuotaAndReleaseOnDisposal()
    {
        var budget = new Db2ResourceBudget(10);
        var first = new Db2LobSpool(budget); var second = new Db2LobSpool(budget);
        try
        {
            await first.AppendAsync(new byte[7]); await second.AppendAsync(new byte[3]);
            await Assert.ThrowsAsync<Db2ResourceLimitException>(() => second.AppendAsync(new byte[1]));
            Assert.Equal(10, budget.UsedBytes);
            Assert.Equal(3, second.File.Length);
            await first.DisposeAsync(); await first.DisposeAsync();
            Assert.Equal(3, budget.UsedBytes);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => first.AppendAsync(new byte[1]));
            Assert.Equal(3, budget.UsedBytes);
        }
        finally { await first.DisposeAsync(); await second.DisposeAsync(); }
        Assert.Equal(0, budget.UsedBytes);
    }
}
