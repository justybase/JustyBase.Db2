using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class WideParameterLiveTests
{
    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task WideParameterDescriptorsPreserveValuesAcrossCptContinuationGroups()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);

        foreach (var count in new[] { 83, 84, 85 })
        {
            var sql = "VALUES (" + string.Join(" + ", Enumerable.Range(0, count)
                .Select(_ => "COALESCE(CAST(? AS INTEGER), 0)")) + ")";
            var parameters = Enumerable.Range(0, count)
                .Select(index => index % 11 == 0 ? null : (object?)index)
                .ToArray();
            var expected = parameters.Where(value => value is not null).Sum(value => (int)value!);
            var row = Assert.Single((await db.QueryAsync(sql, parameters)).Rows);
            Assert.Equal(expected, Assert.IsType<int>(row[0]));
        }

        var table = $"WB_CS_{Guid.NewGuid():N}".ToUpperInvariant();
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var created = false;
        try
        {
            await db.ExecuteAsync($"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, V1 INTEGER, V2 VARCHAR(32), V3 SMALLINT)");
            created = true;

            var expectedRows = Enumerable.Range(0, 37)
                .Select(index => new object?[]
                {
                    90_000 + index,
                    index % 8 == 0 ? null : index * 13,
                    index % 5 == 0 ? null : $"row-{index}",
                    index % 7 == 0 ? null : (short)(index - 18),
                })
                .ToArray();
            var sql = $"INSERT INTO {table} VALUES " + string.Join(", ", Enumerable.Range(0, expectedRows.Length).Select(_ => "(?, ?, ?, ?)"));
            var parameters = expectedRows.SelectMany(row => row).ToArray();
            Assert.Equal(148, parameters.Length);
            await db.ExecuteAsync(sql, parameters);

            var actualRows = (await db.QueryAsync($"SELECT ID, V1, V2, V3 FROM {table} ORDER BY ID")).Rows;
            Assert.Equal(expectedRows.Length, actualRows.Count);
            for (var index = 0; index < expectedRows.Length; index++)
                Assert.Equal(expectedRows[index], actualRows[index]);
        }
        finally
        {
            if (created)
            {
                try { await db.ExecuteAsync($"DROP TABLE {schema}.{table}"); }
                catch (Db2SqlException error) when (error.SqlCode == -204 && error.SqlState == "42704") { }
            }
        }
    }
}
