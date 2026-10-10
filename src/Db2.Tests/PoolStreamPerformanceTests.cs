using Xunit;
namespace JustyBase.Db2.Tests;

[Trait("Category", "Live")]
public sealed class PoolStreamPerformanceTests
{
    [Db2LiveFact, Trait("Category", "Live")]
    public async Task EnumerationHoldsLeaseAndReturnsItOnCompletion()
    {
        await using var pool = new Db2Pool(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!, new Db2PoolOptions(1));
        await using (var rows = pool.QueryStreamAsync("VALUES 1,2,3").GetAsyncEnumerator())
        {
            Assert.True(await rows.MoveNextAsync());
            Assert.Equal(0, pool.IdleSessionCount);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.QueryAsync("VALUES 4", cancelled.Token));
            var count = 1; while (await rows.MoveNextAsync()) count++;
            Assert.Equal(3, count);
        }
        Assert.Equal(1, pool.IdleSessionCount);
        Assert.Equal(4, Assert.Single((await pool.QueryAsync("VALUES 4")).Rows)[0]);
    }
    [Db2LiveFact, Trait("Category", "Live")]
    public async Task EarlyBreakCancelsCursorWithoutSurfacingItsCleanupCancellation()
    {
        await using var pool = new Db2Pool(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!, new Db2PoolOptions(1));
        await foreach (var row in pool.QueryStreamAsync("SELECT TABNAME FROM SYSCAT.TABLES")) { Assert.NotNull(row.Values[0]); break; }
        Assert.Equal(1, Assert.Single((await pool.QueryAsync("VALUES 1")).Rows)[0]);
    }
    [Db2LiveFact, Trait("Category", "Live")]
    public async Task InvalidOptionsAndSqlErrorsDoNotLeakLease()
    {
        await using var pool = new Db2Pool(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!, new Db2PoolOptions(1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var row in pool.QueryStreamAsync("VALUES 1", new Db2CommandOptions(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1)))) { }
        });
        await Assert.ThrowsAsync<Db2SqlException>(async () => { await foreach (var row in pool.QueryStreamAsync("SELECT FROM SYSIBM.SYSDUMMY1")) { } });
        Assert.Equal(1, Assert.Single((await pool.QueryAsync("VALUES 1")).Rows)[0]);
    }
}
