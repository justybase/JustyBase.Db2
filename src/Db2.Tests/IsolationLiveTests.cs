using System.Data;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using Xunit;
namespace JustyBase.Db2.Tests;

public sealed class IsolationLiveTests
{
    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task TwoSessionsObserveDirtyReadsOnlyAtUrAndWaitAtRsRr()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        await using var writer = await Db2Session.ConnectAsync(dsn);
        await using var reader = await Db2Session.ConnectAsync(dsn);
        var table = "ISO_" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        var schema = await LiveFixtureSchema.ForSessionAsync(writer);
        await writer.ExecuteAsync($"CREATE TABLE {schema}.{table} (ID INT NOT NULL PRIMARY KEY, V INT)");
        try
        {
            await writer.ExecuteAsync($"INSERT INTO {table} VALUES (1, 0)");
            foreach (var level in new[] { Db2IsolationLevel.UncommittedRead, Db2IsolationLevel.ReadStability, Db2IsolationLevel.RepeatableRead })
            {
                await using var write = await writer.BeginTransactionAsync();
                await write.ExecuteAsync($"UPDATE {table} SET V=99 WHERE ID=1");
                await using var read = await reader.BeginTransactionAsync(level);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var pending = read.QueryAsync($"SELECT V FROM {table} WHERE ID=1", deadline.Token);
                if (level == Db2IsolationLevel.UncommittedRead)
                    Assert.Equal(99, (await pending).Rows[0][0]);
                else
                {
                    await Task.Delay(200);
                    Assert.False(pending.IsCompleted, "RS/RR must wait for the uncommitted writer.");
                }
                await write.RollbackAsync();
                if (level != Db2IsolationLevel.UncommittedRead) Assert.Equal(0, (await pending).Rows[0][0]);
                await read.RollbackAsync();
                Assert.Equal(0, (await reader.QueryAsync($"SELECT V FROM {table}")).Rows[0][0]);
            }
        }
        finally { await writer.ExecuteAsync($"DROP TABLE {schema}.{table}"); }
    }
    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task NativeIsolationScopesRestoreRegisterAfterCommitAndRollback()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var baseline = await Register(db);
        foreach (var (level, code) in new[] { (Db2IsolationLevel.UncommittedRead, "UR"), (Db2IsolationLevel.CursorStability, "CS"), (Db2IsolationLevel.ReadStability, "RS"), (Db2IsolationLevel.RepeatableRead, "RR") })
        foreach (var commit in new[] { false, true })
        {
            await using var tx = await db.BeginTransactionAsync(level);
            Assert.Equal(code, ((string)(await tx.QueryAsync("VALUES CURRENT ISOLATION")).Rows[0][0]!).Trim());
            if (commit) await tx.CommitAsync(); else await tx.RollbackAsync();
            Assert.Equal(baseline, await Register(db));
        }
        await db.ExecuteAsync("SET CURRENT ISOLATION UR");
        await using (var tx = await db.BeginTransactionAsync(Db2IsolationLevel.CursorStability)) await tx.RollbackAsync();
        Assert.Equal("UR", await Register(db));
        await db.ExecuteAsync("SET CURRENT ISOLATION RESET");
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoIsolationReportsRequestedLevelAndRestoresUnspecifiedBaseline()
    {
        await using var db = new Db2AdoConnection(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        await db.OpenAsync();
        async Task<string> Iso() { await using var cmd = db.CreateCommand(); cmd.CommandText = "VALUES CURRENT ISOLATION"; return ((string)(await cmd.ExecuteScalarAsync())!).Trim(); }
        var baseline = await Iso();
        foreach (var (level, code) in new[] { (IsolationLevel.ReadUncommitted, "UR"), (IsolationLevel.ReadCommitted, "CS"), (IsolationLevel.RepeatableRead, "RS"), (IsolationLevel.Serializable, "RR") })
        {
            await using var tx = await db.BeginTransactionAsync(level);
            Assert.Equal(level, tx.IsolationLevel);
            await using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "VALUES CURRENT ISOLATION";
            Assert.Equal(code, ((string)(await cmd.ExecuteScalarAsync())!).Trim());
            await tx.RollbackAsync();
            Assert.Equal(baseline, await Iso());
        }
        await using (var tx = await db.BeginTransactionAsync()) { Assert.Equal(IsolationLevel.Unspecified, tx.IsolationLevel); await tx.RollbackAsync(); }
        await Assert.ThrowsAsync<NotSupportedException>(() => db.BeginTransactionAsync(IsolationLevel.Snapshot).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => db.BeginTransactionAsync(IsolationLevel.Chaos).AsTask());
    }
    private static async Task<string> Register(Db2Session db) => ((string)(await db.QueryAsync("VALUES CURRENT ISOLATION")).Rows[0][0]!).Trim();
}
