using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

/// <summary>
/// Minimal live smoke tests for <c>scripts/test-db2-integration.sh</c>
/// (<c>dotnet test --filter "Category=Db2Integration"</c>).
/// The script starts a throwaway Db2 container and exports <c>DB2_TEST_DSN</c> plus
/// <c>DB2_IT_SCHEMA</c>, a schema created for that run. Without <c>DB2_TEST_DSN</c>
/// every test is skipped, like the other live tests.
/// </summary>
[Trait("Category", "Live")]
[Trait("Category", "Db2Integration")]
public sealed class Db2IntegrationSmokeTests
{
    private static string Dsn => Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;

    /// <summary>The run's schema from the script, or the session's current schema for manual runs.</summary>
    private static async Task<string> SchemaAsync(Db2Session db)
    {
        var name = Environment.GetEnvironmentVariable("DB2_IT_SCHEMA");
        return string.IsNullOrEmpty(name)
            ? await LiveFixtureSchema.ForSessionAsync(db)
            : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string UniqueTable(string prefix) =>
        $"{prefix}_{Environment.ProcessId}_{Guid.NewGuid().ToString("N")[..8]}";

    private static async Task<int> CountAsync(Db2Session db, string table) =>
        Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync($"SELECT COUNT(*) FROM {table}")).Rows)));

    [Db2LiveFact]
    public async Task ConnectQueryClose()
    {
        await using var db = await Db2Session.ConnectAsync(Dsn);
        var row = Assert.Single((await db.QueryAsync("VALUES 1")).Rows);
        Assert.Equal(new object?[] { 1 }, row);
    }

    [Db2LiveFact]
    public async Task CreateInsertSelectUpdateDeleteWithTypesAndNulls()
    {
        await using var db = await Db2Session.ConnectAsync(Dsn);
        var table = $"{await SchemaAsync(db)}.{UniqueTable("SMOKE_DML")}";
        await db.ExecuteAsync(
            $"CREATE TABLE {table} (ID INTEGER NOT NULL PRIMARY KEY, BIG BIGINT, NAME VARCHAR(50), " +
            "CODE CHAR(4), AMOUNT DECIMAL(12,3), BORN DATE, STAMP TIMESTAMP, FLAG BOOLEAN)");

        var inserted = await db.ExecuteAsync(
            $"INSERT INTO {table} VALUES " +
            "(1, 9223372036854775807, 'alpha', 'AB', 123.456, '2024-02-29', '2024-02-29 23:59:58.123456', TRUE), " +
            "(2, -5, 'Zażółć 東京', 'CD', -0.001, '1999-12-31', '2000-01-01 00:00:00', FALSE), " +
            "(3, NULL, NULL, NULL, NULL, NULL, NULL, NULL)");
        Assert.Equal(3, inserted);

        var rows = (await db.QueryAsync($"SELECT ID, BIG, NAME, CODE, AMOUNT, BORN, STAMP, FLAG FROM {table} ORDER BY ID")).Rows;
        Assert.Equal(3, rows.Count);
        Assert.Equal(new object?[] { 1, long.MaxValue, "alpha", "AB  ", "123.456", "2024-02-29", "2024-02-29-23.59.58.123456", true }, rows[0]);
        Assert.Equal(-5L, rows[1][1]);
        Assert.Equal("Zażółć 東京", rows[1][2]);
        Assert.Equal("-0.001", rows[1][4]);
        Assert.Equal(false, rows[1][7]);
        Assert.Equal(new object?[] { 3, null, null, null, null, null, null, null }, rows[2]);

        Assert.Equal(2, await db.ExecuteAsync($"UPDATE {table} SET NAME = 'changed' WHERE ID IN (1, 2)"));
        var names = (await db.QueryAsync($"SELECT NAME FROM {table} WHERE NAME IS NOT NULL ORDER BY ID")).Rows;
        Assert.Equal(new[] { "changed", "changed" }, names.Select(row => (string)row[0]!).ToArray());

        Assert.Equal(1, await db.ExecuteAsync($"DELETE FROM {table} WHERE ID = 3"));
        Assert.Equal(2, await CountAsync(db, table));

        await db.ExecuteAsync($"DROP TABLE {table}");
    }

    [Db2LiveFact]
    public async Task ParameterizedAndPreparedStatements()
    {
        await using var db = await Db2Session.ConnectAsync(Dsn);
        var table = $"{await SchemaAsync(db)}.{UniqueTable("SMOKE_PARAM")}";
        await db.ExecuteAsync($"CREATE TABLE {table} (ID INTEGER NOT NULL PRIMARY KEY, NAME VARCHAR(30))");

        Assert.Equal(1, await db.ExecuteAsync($"INSERT INTO {table} VALUES (?, ?)", new object?[] { 1, "one" }));
        Assert.Equal(1, await db.ExecuteAsync($"INSERT INTO {table} VALUES (?, ?)", new object?[] { 2, null }));

        var select = $"SELECT NAME FROM {table} WHERE ID = ?";
        Assert.Equal(new object?[] { "one" }, Assert.Single((await db.QueryAsync(select, new object?[] { 1 })).Rows));
        Assert.Equal(new object?[] { null }, Assert.Single((await db.QueryAsync(select, new object?[] { 2 })).Rows));
        Assert.Empty((await db.QueryAsync(select, new object?[] { 99 })).Rows);

        var statement = await db.PrepareAsync(select);
        for (var i = 0; i < 3; i++)
            Assert.Equal(new object?[] { "one" }, Assert.Single((await db.QueryAsync(statement, new object?[] { 1 })).Rows));

        await db.ExecuteAsync($"DROP TABLE {table}");
    }

    [Db2LiveFact]
    public async Task TransactionRollbackThenCommit()
    {
        await using var db = await Db2Session.ConnectAsync(Dsn);
        await using var other = await Db2Session.ConnectAsync(Dsn);
        var table = $"{await SchemaAsync(db)}.{UniqueTable("SMOKE_TX")}";
        await db.ExecuteAsync($"CREATE TABLE {table} (ID INTEGER NOT NULL PRIMARY KEY)");

        await using (var transaction = await db.BeginTransactionAsync())
        {
            await transaction.ExecuteAsync($"INSERT INTO {table} VALUES (1)");
            await transaction.RollbackAsync();
        }
        Assert.Equal(0, await CountAsync(db, table));

        await using (var transaction = await db.BeginTransactionAsync())
        {
            await transaction.ExecuteAsync($"INSERT INTO {table} VALUES (2)");
            await transaction.CommitAsync();
        }
        Assert.Equal(1, await CountAsync(db, table));
        Assert.Equal(1, await CountAsync(other, table));

        await db.ExecuteAsync($"DROP TABLE {table}");
    }

    [Db2LiveFact]
    public async Task ResultShapeColumnNamesAndCount()
    {
        await using var db = await Db2Session.ConnectAsync(Dsn);
        var result = await db.QueryAsync("SELECT 7 AS SEVEN, CAST('x' AS VARCHAR(5)) AS LETTER, CAST(NULL AS INTEGER) AS NOTHING FROM SYSIBM.SYSDUMMY1");
        Assert.Equal(new[] { "SEVEN", "LETTER", "NOTHING" }, result.Columns.Select(column => column.Name).ToArray());
        Assert.Equal(new object?[] { 7, "x", null }, Assert.Single(result.Rows));
    }

    [Db2LiveFact]
    public async Task InvalidSqlIsReportedAndSessionStaysUsable()
    {
        await using var db = await Db2Session.ConnectAsync(Dsn);

        var syntax = await Assert.ThrowsAnyAsync<Exception>(() => db.QueryAsync("SELEC 1 FROM SYSIBM.SYSDUMMY1"));
        Assert.Equal(-104, Assert.IsAssignableFrom<IDb2Error>(syntax).SqlCode);

        var missing = await Assert.ThrowsAnyAsync<Exception>(() => db.QueryAsync("SELECT * FROM IT_SMOKE_NO_SUCH_TABLE"));
        Assert.Equal(-204, Assert.IsAssignableFrom<IDb2Error>(missing).SqlCode);

        Assert.Equal(new object?[] { 2 }, Assert.Single((await db.QueryAsync("VALUES 2")).Rows));
    }
}
