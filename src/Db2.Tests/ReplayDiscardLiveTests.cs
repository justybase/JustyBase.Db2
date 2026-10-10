using System.Data;
using JustyBase.Db2.AdoNet;
using Xunit;

namespace JustyBase.Db2.Tests;

[Trait("Category", "Live")]
public sealed class ReplayDiscardLiveTests
{
    private const string RowsSql = "WITH T(ID) AS (VALUES 1 UNION ALL SELECT ID + 1 FROM T WHERE ID < 20000) SELECT ID FROM T ORDER BY ID";

    [Db2LiveFact]
    public async Task EarlyLobTerminationKeepsTheCompatibilityDrainAndSessionReusable()
    {
        const string sql = "WITH T(ID) AS (VALUES 1 UNION ALL SELECT ID + 1 FROM T WHERE ID < 20) " +
            "SELECT ID, CAST(REPEAT('x',30000) AS CLOB(100K)) || CAST(REPEAT('y',30000) AS CLOB(100K)), " +
            "BLOB(CAST(REPEAT('a',30000) AS VARCHAR(30000) FOR BIT DATA)) || " +
            "BLOB(CAST(REPEAT('b',30000) AS VARCHAR(30000) FOR BIT DATA)) FROM T ORDER BY ID";
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        await using (var session = await Db2Session.ConnectAsync(dsn))
        {
            await foreach (var row in session.QueryStreamAsync(sql))
            { Assert.Equal(1, row.Values[0]); break; }
            Assert.Equal(42, Assert.Single((await session.QueryAsync("VALUES 42")).Rows)[0]);
        }
        await using var connection = new Db2AdoConnection(dsn);
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
            Assert.True(await reader.ReadAsync()); Assert.Equal(1, reader.GetInt32(0));
            using var text = reader.GetTextReader(1);
            Assert.Equal(1, await text.ReadAsync(new char[1]));
            await using var bytes = reader.GetStream(2);
            Assert.Equal(1, await bytes.ReadAsync(new byte[1]));
        }
        await AssertNextCommand(connection);
    }

    [Db2LiveFact]
    public async Task NativeOwnedRowsMatchMaterializedResultsAcrossSupportedScalarTypes()
    {
        const string sql = "VALUES (CAST(NULL AS INTEGER), CAST(-32768 AS SMALLINT), " +
            "CAST(2147483647 AS INTEGER), CAST(-9223372036854775808 AS BIGINT), " +
            "CAST(1.25 AS REAL), CAST(-2.5 AS DOUBLE), CAST(12.34 AS DECIMAL(12,2)), " +
            "DATE('2026-02-28'), TIME('12:34:56'), TIMESTAMP('2026-02-28-12.34.56.123456'), " +
            "CAST('Zażółć 東京' AS VARCHAR(64)), CAST(X'007F80FF' AS VARBINARY(8)))";
        await using var session = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var expected = Assert.Single((await session.QueryAsync(sql)).Rows);
        var retained = new List<Db2StreamRow>();
        await foreach (var row in session.QueryStreamAsync(sql)) retained.Add(row);
        Assert.Equal(expected, Assert.Single(retained).Values);
        // A subsequent query cannot change previously yielded scalar row storage.
        await foreach (var _ in session.QueryStreamAsync("VALUES 42")) { }
        Assert.Equal(expected, retained[0].Values);
    }

    [Db2LiveFact]
    public async Task EarlyTerminationPreservesPoolReuseAndTransactionState()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var taggedDsn = dsn.StartsWith("db2://", StringComparison.OrdinalIgnoreCase)
            ? dsn + (dsn.Contains('?') ? "&" : "?") + "application_name=replay-discard-live"
            : dsn.TrimEnd(';') + ";application_name=replay-discard-live";
        await using var connection = new Db2AdoConnection(taggedDsn);
        try
        {
            await connection.OpenAsync();
            var session = connection.CurrentSession;
            foreach (var take in new[] { 1, 10 })
            {
                await using var command = connection.CreateCommand(); command.CommandText = RowsSql;
                await using (var reader = await command.ExecuteReaderAsync())
                    for (var row = 1; row <= take; row++) { Assert.True(await reader.ReadAsync()); Assert.Equal(row, reader.GetInt32(0)); }
                await AssertNextCommand(connection);
            }
            await using (var scalar = connection.CreateCommand())
            { scalar.CommandText = RowsSql; Assert.Equal(1, await scalar.ExecuteScalarAsync()); }
            await AssertNextCommand(connection);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = RowsSql;
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
                Assert.True(await reader.ReadAsync()); Assert.Equal(1, reader.GetInt32(0)); Assert.False(await reader.ReadAsync());
            }
            await connection.CloseAsync();
            await connection.OpenAsync();
            Assert.Same(session, connection.CurrentSession);
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var command = connection.CreateCommand(); command.CommandText = RowsSql; command.Transaction = transaction;
                await using (var reader = await command.ExecuteReaderAsync()) Assert.True(await reader.ReadAsync());
                Assert.False(connection.CurrentSession!.AutoCommit);
                await transaction.RollbackAsync();
            }
            Assert.True(connection.CurrentSession!.AutoCommit);
            await AssertNextCommand(connection);
        }
        finally { await connection.CloseAsync(); Db2AdoConnection.ClearAllPools(); }
    }

    private static async Task AssertNextCommand(Db2AdoConnection connection)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "VALUES 42";
        Assert.Equal(42, await command.ExecuteScalarAsync());
    }
}
