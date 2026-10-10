using System.Security.Cryptography;
using System.Text;
using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class Db2LiveFactAttribute : FactAttribute
{
    public Db2LiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DB2_TEST_DSN")))
            Skip = "Set DB2_TEST_DSN to enable live Db2 integration tests.";
    }
}

public sealed class LiveDb2Tests
{
    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveEmptyAndNullLobValuesRemainDistinct()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var expressions = new[]
        {
            "CAST(NULL AS BLOB(1M))", "CAST(X'' AS BLOB(1M))",
            "CAST(NULL AS CLOB(1M))", "CAST('' AS CLOB(1M))",
            "CAST(NULL AS DBCLOB(1M))", "CAST('' AS DBCLOB(1M))",
            "CAST('DBCLOB 東京' AS DBCLOB(1M))", "CAST(NULL AS XML)",
        };
        foreach (var expression in expressions)
        {
            try { await db.QueryAsync($"VALUES {expression}"); }
            catch (Exception error) { throw new InvalidOperationException($"Db2 rejected or the driver failed to decode {expression}.", error); }
        }

        var row = Assert.Single((await db.QueryAsync($"VALUES ({string.Join(", ", expressions)})")).Rows);

        Assert.Null(row[0]);
        Assert.Empty(Assert.IsType<byte[]>(row[1]));
        Assert.Null(row[2]);
        Assert.Equal(string.Empty, Assert.IsType<string>(row[3]));
        Assert.Null(row[4]);
        Assert.Equal(string.Empty, Assert.IsType<string>(row[5]));
        Assert.Equal("DBCLOB 東京", Assert.IsType<string>(row[6]));
        Assert.Null(row[7]);

        var streamedRows = new List<object?[]>();
        await foreach (var streamedRow in db.QueryStreamAsync($"VALUES ({string.Join(", ", expressions)})"))
            streamedRows.Add(streamedRow.Values);
        var streamed = Assert.Single(streamedRows);
        Assert.Null(streamed[0]);
        Assert.Empty(Assert.IsType<byte[]>(streamed[1]));
        Assert.Null(streamed[2]);
        Assert.Equal(string.Empty, Assert.IsType<string>(streamed[3]));
        Assert.Null(streamed[4]);
        Assert.Equal(string.Empty, Assert.IsType<string>(streamed[5]));
        Assert.Equal("DBCLOB 東京", Assert.IsType<string>(streamed[6]));
        Assert.Null(streamed[7]);

        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 29")).Rows))));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveTypedNullsIntegerBoundariesAndFloatingPointValues()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        Assert.Equal(new object?[] { null, null, null, null }, Assert.Single((await db.QueryAsync("VALUES (CAST(NULL AS INTEGER), CAST(NULL AS VARCHAR(30)), CAST(NULL AS DATE), CAST(NULL AS BLOB(100)))")).Rows));
        Assert.Equal(new object?[] { int.MinValue, long.MaxValue, short.MinValue }, Assert.Single((await db.QueryAsync("VALUES (CAST(-2147483648 AS INTEGER), CAST(9223372036854775807 AS BIGINT), CAST(-32768 AS SMALLINT))")).Rows));
        Assert.Equal(new object?[] { 1.25f, -2.5d, true, false }, Assert.Single((await db.QueryAsync("VALUES (CAST(1.25 AS REAL), CAST(-2.5 AS DOUBLE), CAST(TRUE AS BOOLEAN), CAST(FALSE AS BOOLEAN))")).Rows));
        Assert.Equal(new object?[] { (short)-3, (short)-2, (short)-1, (short)0, (short)0, (short)1, (short)2, (short)3 }, Assert.Single((await db.QueryAsync(
            "VALUES (CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT))",
            [-3.75d, -2.5d, -1.5d, -0.5d, 0.5d, 1.5d, 2.5d, 3.75d])).Rows));
        Assert.Equal(new object?[] { (short)-3, (short)-1, (short)0, (short)0, (short)1, (short)3 }, Assert.Single((await db.QueryAsync(
            "VALUES (CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT))",
            [-3.75m, -1.5m, -0.5m, 0.5m, 1.5m, 3.75m])).Rows));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveAdminCommandRunsParameterizedRunstatsThroughPool()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var table = $"CS_ADMIN_{Guid.NewGuid():N}".ToUpperInvariant();
        await using var db = await Db2Session.ConnectAsync(dsn);
        await using var pool = new Db2Pool(dsn, new Db2PoolOptions(1));
        var created = false;
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => db.ExecuteAdminCommandAsync(" \t\r\n"));
            await Assert.ThrowsAsync<ArgumentException>(() => pool.ExecuteAdminCommandAsync("RUNSTATS\0 ON TABLE X"));
            var schema = Assert.IsType<string>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES CURRENT SCHEMA")).Rows)));
            Assert.Matches("^[A-Z][A-Z0-9_]*$", schema);
            await db.ExecuteAsync($"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL)");
            created = true;

            await pool.ExecuteAdminCommandAsync($"RUNSTATS ON TABLE {schema}.{table}");
            var stats = await db.QueryAsync("SELECT STATS_TIME FROM SYSCAT.TABLES WHERE TABSCHEMA = ? AND TABNAME = ?", [schema, table]);
            Assert.Single(stats.Rows);
            Assert.NotNull(Assert.Single(stats.Rows[0]));
        }
        finally
        {
            if (created) await db.ExecuteAsync($"DROP TABLE {await LiveFixtureSchema.ForSessionAsync(db)}.{table}").ConfigureAwait(false);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveDateTimeUnicodeAndBinaryValues()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        Assert.Equal(new object?[] { "2024-02-29", "23.59.58", "2024-02-29-23.59.58.123456" }, Assert.Single((await db.QueryAsync("VALUES (CAST('2024-02-29' AS DATE), CAST('23:59:58' AS TIME), CAST('2024-02-29 23:59:58.123456' AS TIMESTAMP))")).Rows));
        var row = Assert.Single((await db.QueryAsync("VALUES (CAST('Zażółć 東京' AS VARCHAR(50)), CAST(X'00FF7F' AS VARBINARY(10)))")).Rows);
        Assert.Equal("Zażółć 東京", row[0]);
        Assert.Equal(new byte[] { 0, 255, 127 }, Assert.IsType<byte[]>(row[1]));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveTemporalTextParametersMatchIbmProvider()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        const string sql = "VALUES (CAST(? AS DATE), CAST(? AS DATE), CAST(? AS TIME), CAST(? AS TIME), CAST(? AS TIMESTAMP), CAST(? AS TIMESTAMP))";
        object?[] values = ["2024-2-9", "02/29/2024", "12:34", "23.59.58", "2024-02-29T23:59:58.123456", "2024-02-29"];
        object?[] expected = ["2024-02-09", "2024-02-29", "12.34.00", "23.59.58", "2024-02-29-23.59.58.123456", "2024-02-29-00.00.00.000000"];

        Assert.Equal(expected, Assert.Single((await db.QueryAsync(sql, values)).Rows));
        var statement = await db.PrepareAsync(sql);
        try { Assert.Equal(expected, Assert.Single((await statement.QueryAsync(values)).Rows)); }
        finally { statement.Close(); }

        var streamed = new List<IReadOnlyList<object?>>();
        await foreach (var row in db.QueryStreamAsync(sql, values)) streamed.Add(row.Values);
        Assert.Equal(new[] { expected }, streamed);

        const string edgeSql = "VALUES (CAST(? AS TIME), CAST(? AS DATE), CAST(? AS TIME))";
        object?[] edgeValues = ["24:00:00", "2024-02-29T23:59:58.123456", "2024-02-29 23:59:58.123456"];
        object?[] edgeExpected = ["24.00.00", "2024-02-29", "23.59.58"];
        Assert.Equal(edgeExpected, Assert.Single((await db.QueryAsync(edgeSql, edgeValues)).Rows));

        const string timestampPrecision12Sql = "VALUES CAST(CAST(? AS TIMESTAMP(12)) AS VARCHAR(32))";
        object?[] timestampPrecision12Values = ["2024-02-29T23:59:58.123456789012"];
        object?[] timestampPrecision12Expected = ["2024-02-29-23.59.58.123456789012"];
        Assert.Equal(timestampPrecision12Expected,
            Assert.Single((await db.QueryAsync(timestampPrecision12Sql, timestampPrecision12Values)).Rows));
        var precision12Statement = await db.PrepareAsync(timestampPrecision12Sql);
        try
        {
            Assert.Equal(timestampPrecision12Expected,
                Assert.Single((await precision12Statement.QueryAsync(timestampPrecision12Values)).Rows));
        }
        finally { precision12Statement.Close(); }
        var precision12Stream = new List<IReadOnlyList<object?>>();
        await foreach (var row in db.QueryStreamAsync(timestampPrecision12Sql, timestampPrecision12Values))
            precision12Stream.Add(row.Values);
        Assert.Equal(new[] { timestampPrecision12Expected }, precision12Stream);

        foreach (var (type, value) in new[]
        {
            ("DATE", "2024-02-30"),
            ("TIME", "23:59:58.123456"),
            ("TIME", "24:00:01"),
            ("TIMESTAMP", "2024-02-30 23:59:58"),
            ("TIMESTAMP", "2024-02-29 23:59:58+02:00"),
        })
            await Assert.ThrowsAsync<FormatException>(() => db.QueryAsync($"VALUES CAST(? AS {type})", [value]));

        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 29")).Rows))));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveOrderedMultirowResultSet()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var rows = (await db.QueryAsync("SELECT N FROM (VALUES 1, 2, 3, 4, 5) AS T(N) ORDER BY N DESC")).Rows;
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, rows.Select(row => Assert.IsType<int>(Assert.Single(row))));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveProcedureCallReturnsOrderedCursorSetsAndDrainsEachCursor()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var config = Db2Config.Parse(dsn);
        var procedure = $"{config.User}.CS_DB2_RS_{Guid.NewGuid():N}"[..(config.User.Length + 23)].ToUpperInvariant();
        config.Timeout = TimeSpan.FromSeconds(8);
        var values = string.Join(", ", Enumerable.Range(1, 130).Select(value => $"({value})"));
        var digits = string.Join(", ", Enumerable.Range(0, 10).Select(value => $"({value})"));
        var dimensions = string.Join(", ", Enumerable.Range(0, 5).Select(index => $"(VALUES {digits}) AS D{index}(N)"));
        var orderBy = string.Join(", ", Enumerable.Range(0, 5).Select(index => $"D{index}.N"));
        var largeQuery = $"SELECT ROW_NUMBER() OVER (ORDER BY {orderBy}) AS N FROM {dimensions} FETCH FIRST 12000 ROWS ONLY";
        var create = $"CREATE PROCEDURE {procedure} (IN P INTEGER) LANGUAGE SQL DYNAMIC RESULT SETS 4 BEGIN DECLARE C1 CURSOR WITH RETURN TO CALLER FOR SELECT P AS VALUE FROM SYSIBM.SYSDUMMY1; DECLARE C2 CURSOR WITH RETURN TO CALLER FOR SELECT N FROM (VALUES {values}) AS T(N); DECLARE C3 CURSOR WITH RETURN TO CALLER FOR {largeQuery}; DECLARE C4 CURSOR WITH RETURN TO CALLER FOR SELECT P AS VALUE FROM SYSIBM.SYSDUMMY1 WHERE 1 = 0; OPEN C1; OPEN C2; OPEN C3; OPEN C4; END";
        var created = false;
        try
        {
            await using var db = await Db2Session.ConnectAsync(config);
            await db.ExecuteAsync(create);
            created = true;

            var result = await db.QueryAsync($"/* result-set path */ -- exercise leading SQL trivia\nCALL {procedure}(?)", [42]);
            Assert.Equal(4, result.ResultSets.Count);
            Assert.Equal(42, Assert.IsType<int>(Assert.Single(Assert.Single(result.ResultSets[0].Rows))));
            Assert.Equal(130, result.ResultSets[1].Rows.Count);
            Assert.Equal(1, Assert.IsType<int>(Assert.Single(result.ResultSets[1].Rows[0])));
            Assert.Equal(130, Assert.IsType<int>(Assert.Single(result.ResultSets[1].Rows[^1])));
            Assert.Equal(12_000, result.ResultSets[2].Rows.Count);
            Assert.Equal(1L, Assert.IsType<long>(Assert.Single(result.ResultSets[2].Rows[0])));
            Assert.Equal(12_000L, Assert.IsType<long>(Assert.Single(result.ResultSets[2].Rows[^1])));
            Assert.Empty(result.ResultSets[3].Rows);
            Assert.Same(result.ResultSets[0].Rows, result.Rows);
            Assert.Equal(0, await db.ExecuteAsync($"CALL {procedure}(?)", [42]));
            Assert.Equal(23, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 23")).Rows))));
        }
        finally
        {
            if (created)
            {
                await using var cleanup = await Db2Session.ConnectAsync(dsn);
                await cleanup.ExecuteAsync($"DROP PROCEDURE {procedure}");
            }
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveProcedureCallReturnsOutputParametersAndCursorSetsTogether()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var procedure = $"CS_OUT_RS_{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        await using var db = await Db2Session.ConnectAsync(dsn);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var created = false;
        try
        {
            await db.ExecuteAsync($$"""
                CREATE PROCEDURE {{schema}}.{{procedure}} (IN P INTEGER, OUT DOUBLED INTEGER)
                LANGUAGE SQL DYNAMIC RESULT SETS 2
                BEGIN
                    DECLARE C1 CURSOR WITH RETURN TO CALLER FOR SELECT P AS VALUE FROM SYSIBM.SYSDUMMY1;
                    DECLARE C2 CURSOR WITH RETURN TO CALLER FOR SELECT N FROM (VALUES (P), (P + 1)) AS T(N);
                    SET DOUBLED = P * 2;
                    OPEN C1;
                    OPEN C2;
                END
                """);
            created = true;

            var statement = await db.PrepareAsync($"CALL {procedure}(?, ?)");
            var result = await statement.ExecuteWithOutputsAsync([
                new Db2Parameter(21),
                new Db2Parameter(0, Db2ParameterDirection.Output),
            ]);

            Assert.Equal(0, result.RowsAffected);
            Assert.Null(result.OutputParameters[0]);
            Assert.Equal(42, result.OutputParameters[1]);
            Assert.Equal(2, result.ResultSets.Count);
            Assert.Equal(21, Assert.IsType<int>(Assert.Single(Assert.Single(result.ResultSets[0].Rows))));
            Assert.Equal(new[] { 21, 22 }, result.ResultSets[1].Rows.Select(row => Assert.IsType<int>(Assert.Single(row))));
            Assert.Equal(5, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 5")).Rows))));
        }
        finally
        {
            if (created) await db.ExecuteAsync($"DROP PROCEDURE {schema}.{procedure}");
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveProcedureResultSetCursorsReturnLargeBlobClobAndXmlValues()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var procedure = $"CS_RS_LOB_{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        await using var db = await Db2Session.ConnectAsync(dsn);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var created = false;
        try
        {
            await db.ExecuteAsync($$"""
                CREATE PROCEDURE {{schema}}.{{procedure}} () LANGUAGE SQL DYNAMIC RESULT SETS 3
                BEGIN
                    DECLARE C1 CURSOR WITH RETURN TO CALLER FOR
                        SELECT CAST(REPEAT('c', 30000) AS CLOB(100K)) || CAST(REPEAT('d', 30000) AS CLOB(100K)) || CAST(REPEAT('e', 10000) AS CLOB(100K)) AS VALUE FROM SYSIBM.SYSDUMMY1;
                    DECLARE C2 CURSOR WITH RETURN TO CALLER FOR
                        SELECT BLOB(CAST(REPEAT('x', 30000) AS VARCHAR(30000) FOR BIT DATA)) || BLOB(CAST(REPEAT('y', 30000) AS VARCHAR(30000) FOR BIT DATA)) || BLOB(CAST(REPEAT('z', 12000) AS VARCHAR(12000) FOR BIT DATA)) AS VALUE FROM SYSIBM.SYSDUMMY1;
                    DECLARE C3 CURSOR WITH RETURN TO CALLER FOR
                        SELECT XMLELEMENT(NAME "r", XMLAGG(XMLELEMENT(NAME "c", COLNAME))) AS VALUE FROM SYSCAT.COLUMNS;
                    OPEN C1;
                    OPEN C2;
                    OPEN C3;
                END
                """);
            created = true;

            var result = await db.QueryAsync($"CALL {procedure}()");
            Assert.Equal(3, result.ResultSets.Count);
            var clob = Assert.IsType<string>(Assert.Single(Assert.Single(result.ResultSets[0].Rows)));
            Assert.Equal(new string('c', 30_000) + new string('d', 30_000) + new string('e', 10_000), clob);
            var blob = Assert.IsType<byte[]>(Assert.Single(Assert.Single(result.ResultSets[1].Rows)));
            Assert.Equal(72_000, blob.Length);
            Assert.Equal(new string('x', 30_000) + new string('y', 30_000) + new string('z', 12_000), Encoding.ASCII.GetString(blob));
            var xml = Assert.IsType<string>(Assert.Single(Assert.Single(result.ResultSets[2].Rows)));
            Assert.True(xml.Length >= 100_000, $"procedure XML result is {xml.Length} characters");
            Assert.StartsWith("<r><c>", xml);
            Assert.EndsWith("</c></r>", xml);
            Assert.Equal(31, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 31")).Rows))));
        }
        finally
        {
            if (created) await db.ExecuteAsync($"DROP PROCEDURE {schema}.{procedure}");
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveNullableAndNotNullLobDescriptorsPreserveLeadingZeroBytes()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var config = Db2Config.Parse(dsn);
        var table = $"CS_LOBNULL_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        var procedure = $"CS_LOBNULL_{Guid.NewGuid():N}_P"[..30].ToUpperInvariant();
        string? schema = null;
        var created = false;
        var procedureCreated = false;
        try
        {
            await using var db = await Db2Session.ConnectAsync(config);
            schema = await LiveFixtureSchema.ForSessionAsync(db);
            await db.ExecuteAsync($"CREATE TABLE {schema}.{table} (ID INTEGER, BN BLOB(2M) NOT NULL, B BLOB(2M), CN CLOB(2M) NOT NULL, C CLOB(2M), DN DBCLOB(2M) NOT NULL, D DBCLOB(2M))");
            created = true;
            const int size = 70_000;
            var bn = new byte[size];
            for (var index = 1; index < size; index++) bn[index] = (byte)index;
            var b = new byte[size];
            for (var index = 1; index < size; index++) b[index] = (byte)(index * 3);
            var cn = "\u0000" + new string('a', size - 1);
            var c = "\u0000" + new string('b', size - 1);
            var dn = "\u0000" + new string('漢', size - 1);
            var d = "\u0000" + new string('字', size - 1);
            Assert.Equal(1, await db.ExecuteAsync($"INSERT INTO {table} VALUES (1, ?, ?, ?, ?, ?, ?)", [bn, b, cn, c, dn, d]));
            Assert.Equal(1, await db.ExecuteAsync($"INSERT INTO {table} VALUES (2, CAST('' AS BLOB(2M)), NULL, CAST('' AS CLOB(2M)), NULL, CAST('' AS DBCLOB(2M)), NULL)"));
            await db.ExecuteAsync($"CREATE PROCEDURE {schema}.{procedure} () LANGUAGE SQL DYNAMIC RESULT SETS 1 BEGIN DECLARE CUR CURSOR WITH RETURN TO CALLER FOR SELECT ID, BN, B, CN, C, DN, D FROM {table} ORDER BY ID; OPEN CUR; END");
            procedureCreated = true;

            void AssertFirstRow(IReadOnlyList<object?[]> rows, string label)
            {
                var row = rows[0];
                Assert.Equal(1, Assert.IsType<int>(row[0]));
                Assert.Equal(bn, Assert.IsType<byte[]>(row[1]));
                Assert.Equal(b, Assert.IsType<byte[]>(row[2]));
                Assert.Equal(cn, Assert.IsType<string>(row[3]));
                Assert.Equal(c, Assert.IsType<string>(row[4]));
                Assert.Equal(dn, Assert.IsType<string>(row[5]));
                Assert.Equal(d, Assert.IsType<string>(row[6]));
                var empty = rows[1];
                Assert.Equal(2, Assert.IsType<int>(empty[0]));
                Assert.Empty(Assert.IsType<byte[]>(empty[1]));
                Assert.Null(empty[2]);
                Assert.Equal(string.Empty, Assert.IsType<string>(empty[3]));
                Assert.Null(empty[4]);
                Assert.Equal(string.Empty, Assert.IsType<string>(empty[5]));
                Assert.Null(empty[6]);
            }

            var materialized = await db.QueryAsync($"SELECT ID, BN, B, CN, C, DN, D FROM {table} ORDER BY ID");
            AssertFirstRow(materialized.Rows, "query");

            var call = await db.QueryAsync($"CALL {procedure}()");
            AssertFirstRow(call.ResultSets[0].Rows, "procedure");
            Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 29")).Rows))));
        }
        finally
        {
            await using var cleanup = await Db2Session.ConnectAsync(dsn);
            if (procedureCreated) await cleanup.ExecuteAsync($"DROP PROCEDURE {schema}.{procedure}");
            if (created) await cleanup.ExecuteAsync($"DROP TABLE {schema}.{table}");
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveCancellationTokenInterruptsProcedureCursorFetchAndDiscardsUncertainCursor()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var procedure = $"CS_RS_CANCEL_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        var dimensions = string.Join(", ", Enumerable.Range(0, 6).Select(index => $"(VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS D{index}(N)"));
        var projection = string.Join(" + ", Enumerable.Range(0, 6).Select(index => $"D{index}.N"));
        var config = Db2Config.Parse(dsn);
        config.Timeout = TimeSpan.FromSeconds(12);
        await using var db = await Db2Session.ConnectAsync(config);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var created = false;
        try
        {
            // Sort one million generated rows on the server, then return one. This
            // avoids timing cancellation against client-side transfer of completed rows.
            await db.ExecuteAsync($"CREATE PROCEDURE {schema}.{procedure} () LANGUAGE SQL DYNAMIC RESULT SETS 1 BEGIN DECLARE C CURSOR WITH RETURN TO CALLER FOR SELECT {projection} AS N FROM {dimensions} ORDER BY RAND() FETCH FIRST 1 ROW ONLY; OPEN C; END");
            created = true;

            using var cancellation = new CancellationTokenSource();
            var call = db.QueryAsync($"CALL {procedure}()", cancellation.Token);
            await Task.Delay(100);
            Assert.False(call.IsCompleted, "the result cursor should still be fetching when cancellation is requested");
            var cancelLatency = System.Diagnostics.Stopwatch.StartNew();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            cancelLatency.Stop();
            Console.WriteLine($"procedure cursor cancellation completion: {cancelLatency.Elapsed.TotalMilliseconds:F1} ms; session closed: {db.IsClosed}");
            if (!db.IsClosed)
                Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 29")).Rows))));
        }
        finally
        {
            await db.DisposeAsync();
            if (created)
            {
                var cleanupConfig = Db2Config.Parse(dsn);
                cleanupConfig.Timeout = TimeSpan.FromSeconds(5);
                await using var cleanup = await Db2Session.ConnectAsync(cleanupConfig);
                var remaining = await cleanup.QueryAsync($"SELECT COUNT(*) FROM SYSCAT.ROUTINES WHERE ROUTINESCHEMA = '{cleanupConfig.User}' AND ROUTINENAME = '{procedure}'");
                if (Assert.IsType<int>(Assert.Single(Assert.Single(remaining.Rows))) > 0)
                    await cleanup.ExecuteAsync($"DROP PROCEDURE {schema}.{procedure}");
            }
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveCancellationTokenInterruptsSecondProcedureCursorAndCleansEveryCursor()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var procedure = $"CS_RS_CANCEL2_{Guid.NewGuid():N}"[..30].ToUpperInvariant();
        var dimensions = string.Join(", ", Enumerable.Range(0, 7).Select(index => $"(VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS D{index}(N)"));
        var projection = string.Join(" + ", Enumerable.Range(0, 7).Select(index => $"D{index}.N"));
        var config = Db2Config.Parse(dsn);
        config.Timeout = TimeSpan.FromSeconds(12);
        await using var db = await Db2Session.ConnectAsync(config);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var created = false;
        try
        {
            // The first cursor completes immediately; the second sorts ten million rows
            // server-side before returning one, avoiding client-transfer timing races.
            await db.ExecuteAsync($"CREATE PROCEDURE {schema}.{procedure} () LANGUAGE SQL DYNAMIC RESULT SETS 2 BEGIN DECLARE C1 CURSOR WITH RETURN TO CALLER FOR SELECT 41 AS N FROM SYSIBM.SYSDUMMY1; DECLARE C2 CURSOR WITH RETURN TO CALLER FOR SELECT {projection} AS N FROM {dimensions} ORDER BY RAND() FETCH FIRST 1 ROW ONLY; OPEN C1; OPEN C2; END");
            created = true;

            using var cancellation = new CancellationTokenSource();
            var call = db.QueryAsync($"CALL {procedure}()", cancellation.Token);
            await Task.Delay(200);
            Assert.False(call.IsCompleted, "the second result cursor should still be fetching when cancellation is requested");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            // The server confirmed the cancellation with SQLCODE -952, so every cursor is
            // cleaned up and the session remains usable.
            Assert.False(db.IsClosed, "a server-confirmed cancellation must preserve the session.");
            Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 29")).Rows))));
        }
        finally
        {
            await db.DisposeAsync();
            if (created)
            {
                var cleanupConfig = Db2Config.Parse(dsn);
                cleanupConfig.Timeout = TimeSpan.FromSeconds(5);
                await using var cleanup = await Db2Session.ConnectAsync(cleanupConfig);
                var remaining = await cleanup.QueryAsync($"SELECT COUNT(*) FROM SYSCAT.ROUTINES WHERE ROUTINESCHEMA = '{cleanupConfig.User}' AND ROUTINENAME = '{procedure}'");
                if (Assert.IsType<int>(Assert.Single(Assert.Single(remaining.Rows))) > 0)
                    await cleanup.ExecuteAsync($"DROP PROCEDURE {schema}.{procedure}");
            }
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveQueryStreamReadsAcrossBlocksAndDrainsAfterEarlyExit()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        const string query = "SELECT ROW_NUMBER() OVER() AS N FROM (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) A(X), (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) B(X), (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) C(X), (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) D(X), (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) E(X) FETCH FIRST 30000 ROWS ONLY";

        long count = 0;
        long checksum = 0;
        await foreach (var row in db.QueryStreamAsync(query))
        {
            Assert.Single(row.Columns);
            count++;
            checksum += Assert.IsType<long>(row.Values[0]);
        }
        Assert.Equal(30_000, count);
        Assert.Equal(450_015_000, checksum);

        await using (var reader = db.QueryStreamAsync(query).GetAsyncEnumerator())
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.Single(reader.Current.Columns);
        }
        Assert.Equal(17, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 17")).Rows))));

        var boundRows = new List<object?[]>();
        await foreach (var row in db.QueryStreamAsync("VALUES CAST(? AS INTEGER)", [42])) boundRows.Add(row.Values);
        Assert.Equal(new object?[] { 42 }, Assert.Single(boundRows));
        var statement = await db.PrepareAsync("VALUES CAST(? AS INTEGER)");
        var preparedRows = new List<object?[]>();
        await foreach (var row in statement.QueryStreamAsync([43])) preparedRows.Add(row.Values);
        Assert.Equal(new object?[] { 43 }, Assert.Single(preparedRows));

        var blob = Enumerable.Range(0, 70_003).Select(index => (byte)(index * 31)).ToArray();
        byte[]? actualBlob = null;
        await foreach (var row in db.QueryStreamAsync("VALUES CAST(? AS BLOB(1M))", [blob]))
        {
            var value = Assert.Single(row.Values);
            if (value is Db2LobValue lob)
            {
                Assert.Equal(blob.Length, lob.ByteLength);
                await using var stream = lob.OpenReadStream();
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes);
                actualBlob = bytes.ToArray();
            }
            else actualBlob = Assert.IsType<byte[]>(value);
        }
        Assert.Equal(blob, actualBlob);

        var clobText = string.Concat(Enumerable.Repeat("CLOB café 東京 🚀 ", 5_000));
        var clobBytes = Encoding.UTF8.GetBytes(clobText);
        await using (var clobSource = new MemoryStream(clobBytes, writable: false))
        {
            string? streamedText = null;
            await foreach (var row in db.QueryStreamAsync("VALUES CAST(? AS CLOB(2M))", [new Db2ClobStream(clobSource, clobBytes.Length)]))
            {
                var value = Assert.Single(row.Values);
                if (value is Db2LobValue lob)
                {
                    Assert.True(lob.IsText);
                    using var text = lob.OpenTextReader();
                    streamedText = await text.ReadToEndAsync();
                }
                else streamedText = Assert.IsType<string>(value);
            }
            Assert.Equal(clobText, streamedText);
            Assert.Equal(clobBytes.Length, clobSource.Position);
        }

        var dbClobText = string.Concat(Enumerable.Repeat("DBCLOB 東京 🚀 ", 5_000));
        var dbClobBytes = Encoding.UTF8.GetBytes(dbClobText);
        await using (var dbClobSource = new MemoryStream(dbClobBytes, writable: false))
        {
            string? streamedText = null;
            await foreach (var row in db.QueryStreamAsync("VALUES CAST(? AS DBCLOB(2M))", [new Db2DbClobStream(dbClobSource, dbClobBytes.Length)]))
            {
                var value = Assert.Single(row.Values);
                if (value is Db2LobValue lob)
                {
                    Assert.True(lob.IsText);
                    using var text = lob.OpenTextReader();
                    streamedText = await text.ReadToEndAsync();
                }
                else streamedText = Assert.IsType<string>(value);
            }
            Assert.Equal(dbClobText, streamedText);
            Assert.Equal(dbClobBytes.Length, dbClobSource.Position);
        }

        var xmlText = $"<root><message>XML café 東京 🚀</message><body>{new string('x', 100_000)}</body></root>";
        var xmlBytes = Encoding.UTF8.GetBytes(xmlText);
        await using (var xmlSource = new MemoryStream(xmlBytes, writable: false))
        {
            string? streamedText = null;
            await foreach (var row in db.QueryStreamAsync("VALUES XMLSERIALIZE(CAST(? AS XML) AS CLOB(2M))", [new Db2XmlStream(xmlSource, xmlBytes.Length)]))
            {
                var value = Assert.Single(row.Values);
                if (value is Db2LobValue lob)
                {
                    Assert.True(lob.IsText);
                    using var text = lob.OpenTextReader();
                    streamedText = await text.ReadToEndAsync();
                }
                else streamedText = Assert.IsType<string>(value);
            }
            Assert.Equal(xmlText, streamedText);
            Assert.Equal(xmlBytes.Length, xmlSource.Position);
        }

        await using var pool = new Db2Pool(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!, new Db2PoolOptions(1));
        await foreach (var row in pool.QueryStreamAsync(query)) { Assert.Single(row.Columns); break; }
        var pooledRows = new List<object?[]>();
        await foreach (var row in pool.QueryStreamAsync("VALUES CAST(? AS INTEGER)", [44])) pooledRows.Add(row.Values);
        Assert.Equal(new object?[] { 44 }, Assert.Single(pooledRows));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveQueryStreamCancellationWaitsForDb2AndPreservesTheSession()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        using var cancellation = new CancellationTokenSource();
        await using var reader = db.QueryStreamAsync(
            "SELECT COUNT(*) FROM SYSCAT.COLUMNS a, SYSCAT.COLUMNS b, SYSCAT.COLUMNS c",
            cancellation.Token).GetAsyncEnumerator();
        var firstRow = reader.MoveNextAsync().AsTask();
        await Task.Delay(500);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRow);
        Assert.Equal(17, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 17")).Rows))));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveInterruptReturnsMinus952AndPreservesSession()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        Assert.False(await db.InterruptAsync());
        var query = db.QueryAsync("SELECT COUNT(*) FROM SYSCAT.COLUMNS a, SYSCAT.COLUMNS b, SYSCAT.COLUMNS c");
        await Task.Delay(500);
        var interruptStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.True(await db.InterruptAsync());
        var interruptAcknowledgmentMs = System.Diagnostics.Stopwatch.GetElapsedTime(interruptStarted).TotalMilliseconds;
        var error = await Assert.ThrowsAsync<Db2SqlException>(() => query);
        var operationCompletionMs = System.Diagnostics.Stopwatch.GetElapsedTime(interruptStarted).TotalMilliseconds;
        Assert.Equal(-952, error.SqlCode);
        Assert.Equal("57014", error.SqlState);
        Assert.Contains("SQLCODE=-952 SQLSTATE=57014", error.Message);
        Console.WriteLine($"Live SQLINTR: interrupt acknowledgment {interruptAcknowledgmentMs:F1} ms; operation completion {operationCompletionMs:F1} ms");
        Assert.Equal(17, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 17")).Rows))));
        Assert.False(await db.InterruptAsync());
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveCancellationTokenInterruptsServerAndPreservesSession()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        using var cancellation = new CancellationTokenSource();
        var query = db.QueryAsync("SELECT COUNT(*) FROM SYSCAT.COLUMNS a, SYSCAT.COLUMNS b, SYSCAT.COLUMNS c", cancellation.Token);
        await Task.Delay(500);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.False(db.IsClosed);
        Assert.Equal(17, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 17")).Rows))));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveCommandDeadlineInterruptsServerAndPreservesSession()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var options = new Db2CommandOptions(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(15));
        var error = await Assert.ThrowsAsync<Db2CommandTimeoutException>(() => db.QueryAsync(
            "SELECT COUNT(*) FROM SYSCAT.COLUMNS a, SYSCAT.COLUMNS b, SYSCAT.COLUMNS c, SYSCAT.COLUMNS d", options));
        Assert.Contains("100", error.Message);
        Assert.False(db.IsClosed, "DB2-confirmed -952 should leave the session ready for another command.");
        Assert.Equal(17, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 17")).Rows))));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveCancellationTokenInterruptsPreparedAndPooledQueries()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        await using var db = await Db2Session.ConnectAsync(dsn);
        var constant = await db.PrepareAsync("VALUES 17");
        Assert.Equal(17, Assert.IsType<int>(Assert.Single(Assert.Single((await constant.QueryAsync([])).Rows))));
        var statement = await db.PrepareAsync("SELECT COUNT(*) FROM SYSCAT.COLUMNS a, SYSCAT.COLUMNS b, SYSCAT.COLUMNS c");
        using (var cancellation = new CancellationTokenSource())
        {
            var query = statement.QueryAsync([], cancellation.Token);
            await Task.Delay(500);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        }
        Assert.Equal(18, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 18")).Rows))));

        await using var pool = new Db2Pool(dsn, new Db2PoolOptions(MaxConnections: 1));
        using (var cancellation = new CancellationTokenSource())
        {
            var query = pool.QueryAsync("SELECT COUNT(*) FROM SYSCAT.COLUMNS a, SYSCAT.COLUMNS b, SYSCAT.COLUMNS c", cancellation.Token);
            await Task.Delay(500);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        }
        Assert.Equal(19, Assert.IsType<int>(Assert.Single(Assert.Single((await pool.QueryAsync("VALUES 19")).Rows))));
        Assert.Equal(1, pool.SessionCount);
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveQueuedPoolCancellationDoesNotInterruptActiveLease()
    {
        await using var pool = new Db2Pool(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!, new Db2PoolOptions(MaxConnections: 1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = pool.WithSessionAsync(async (session, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await session.QueryAsync("VALUES 17", token);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            var queued = pool.QueryAsync("VALUES 41", cancellation.Token);
            Assert.False(queued.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, pool.SessionCount);
            Assert.Equal(0, pool.IdleSessionCount);
            Assert.False(active.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(17, Assert.IsType<int>(Assert.Single(Assert.Single((await active).Rows))));
        Assert.Equal(19, Assert.IsType<int>(Assert.Single(Assert.Single((await pool.QueryAsync("VALUES 19")).Rows))));
        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(1, pool.IdleSessionCount);
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveInterruptCancelsLockWaitAndDecodesServerDiagnostic()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var table = $"CS_DB2_CANCEL_{Guid.NewGuid():N}"[..30].ToUpperInvariant();
        await using var locker = await Db2Session.ConnectAsync(dsn);
        await using var waiter = await Db2Session.ConnectAsync(dsn);
        var schema = await LiveFixtureSchema.ForSessionAsync(locker);
        Db2Transaction? transaction = null;
        var created = false;
        try
        {
            await locker.ExecuteAsync($"CREATE TABLE {schema}.{table} (ID INT NOT NULL, V INT NOT NULL, PRIMARY KEY (ID))");
            created = true;
            await locker.ExecuteAsync($"INSERT INTO {table} VALUES (1, 0)");
            transaction = await locker.BeginTransactionAsync();
            await transaction.ExecuteAsync($"UPDATE {table} SET V = V + 1 WHERE ID = 1");

            var blockedUpdate = waiter.ExecuteAsync($"UPDATE {table} SET V = V + 1 WHERE ID = 1");
            await Task.Delay(500);
            Assert.True(await waiter.InterruptAsync());
            var error = await Assert.ThrowsAsync<Db2ServerErrorException>(() => blockedUpdate);
            Assert.Equal(Db2ErrorKind.SqlError, error.ErrorKind);
            Assert.Equal(-952, error.SqlCode);
            Assert.Null(error.SqlState);
            Assert.Contains("SQLCODE:-952", error.Message);

            await transaction.RollbackAsync();
            transaction = null;
            Assert.Equal(21, Assert.IsType<int>(Assert.Single(Assert.Single((await waiter.QueryAsync("VALUES 21")).Rows))));
        }
        finally
        {
            if (transaction is not null) await transaction.RollbackAsync();
            if (created)
            {
                try { await locker.ExecuteAsync($"DROP TABLE {schema}.{table}"); }
                catch (InvalidOperationException error) when (error.Message.Contains("SQLCODE=-204 SQLSTATE=42704", StringComparison.Ordinal)) { }
            }
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveCancellationTokenInterruptsLockWaitAndKeepsSessionUsable()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var table = $"CS_DB2_CANCEL_{Guid.NewGuid():N}"[..30].ToUpperInvariant();
        await using var locker = await Db2Session.ConnectAsync(dsn);
        await using var waiter = await Db2Session.ConnectAsync(dsn);
        var schema = Assert.IsType<string>((await locker.QueryAsync("VALUES CURRENT SCHEMA")).Rows[0][0]).TrimEnd();
        var qualifiedTable = "\"" + schema.Replace("\"", "\"\"") + "\".\"" + table + "\"";
        Db2Transaction? transaction = null;
        var created = false;
        try
        {
            await locker.ExecuteAsync($"CREATE TABLE {schema}.{table} (ID INT NOT NULL, V INT NOT NULL, PRIMARY KEY (ID))");
            created = true;
            await locker.ExecuteAsync($"INSERT INTO {table} VALUES (1, 0)");
            transaction = await locker.BeginTransactionAsync();
            await transaction.ExecuteAsync($"UPDATE {table} SET V = V + 1 WHERE ID = 1");

            using var cancellation = new CancellationTokenSource();
            var blockedUpdate = waiter.ExecuteAsync($"UPDATE {table} SET V = V + 1 WHERE ID = 1", cancellation.Token);
            await Task.Delay(500);
            Assert.False(blockedUpdate.IsCompleted, "The second UPDATE should still be waiting on the first transaction's row lock.");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedUpdate.WaitAsync(TimeSpan.FromSeconds(20)));

            await transaction.RollbackAsync();
            transaction = null;
            Assert.False(waiter.IsClosed);
            Assert.Equal(22, Assert.IsType<int>(Assert.Single(Assert.Single((await waiter.QueryAsync("VALUES 22")).Rows))));
        }
        finally
        {
            if (transaction is not null) await transaction.RollbackAsync();
            if (created) await locker.ExecuteAsync($"DROP TABLE {qualifiedTable}");
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveCancellationTokenInterruptsSecondBatchSetAndPreservesConfirmedWrites()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var table = $"CS_DB2_BATCH_CANCEL_{Guid.NewGuid():N}"[..30].ToUpperInvariant();
        await using var locker = await Db2Session.ConnectAsync(dsn);
        await using var waiter = await Db2Session.ConnectAsync(dsn);
        var schema = await LiveFixtureSchema.ForSessionAsync(locker);
        Db2Transaction? transaction = null;
        var created = false;
        try
        {
            await locker.ExecuteAsync($"CREATE TABLE {schema}.{table} (ID INT NOT NULL, V INT NOT NULL, PRIMARY KEY (ID))");
            created = true;
            await locker.ExecuteAsync($"INSERT INTO {table} VALUES (1, 0), (2, 0)");
            transaction = await locker.BeginTransactionAsync();
            await transaction.ExecuteAsync($"UPDATE {table} SET V = V + 1 WHERE ID = 2");

            IReadOnlyList<IReadOnlyList<object?>> parameterSets = [new object?[] { 1 }, new object?[] { 2 }];
            using var cancellation = new CancellationTokenSource();
            var batch = waiter.ExecuteBatchAsync($"UPDATE {table} SET V = V + 1 WHERE ID = ?", parameterSets, cancellation.Token);
            await Task.Delay(500);
            Assert.False(batch.IsCompleted, "The second batch set should still be waiting on the first transaction's row lock.");
            cancellation.Cancel();
            var cancellationError = await Assert.ThrowsAsync<Db2BatchCancellationException>(() => batch.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Equal(1, cancellationError.CompletedParameterSets);
            Assert.Equal(1, cancellationError.InFlightParameterSetIndex);
            Assert.Equal(1, cancellationError.RowsAffected);

            await transaction.RollbackAsync();
            transaction = null;
            Assert.Equal(1, Assert.IsType<int>(Assert.Single(Assert.Single((await locker.QueryAsync($"SELECT V FROM {table} WHERE ID = 1")).Rows))));
            Assert.Equal(0, Assert.IsType<int>(Assert.Single(Assert.Single((await locker.QueryAsync($"SELECT V FROM {table} WHERE ID = 2")).Rows))));
            Assert.False(waiter.IsClosed);
            Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await waiter.QueryAsync("VALUES 29")).Rows))));
        }
        finally
        {
            if (transaction is not null) await transaction.RollbackAsync();
            if (created)
            {
                try { await locker.ExecuteAsync($"DROP TABLE {schema}.{table}"); }
                catch (InvalidOperationException error) when (error.Message.Contains("SQLCODE=-204 SQLSTATE=42704", StringComparison.Ordinal)) { }
            }
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveGraphicAndTimestampParametersPreservePaddingAndMicroseconds()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        Assert.Equal("abc  ", Assert.IsType<string>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES CAST(? AS GRAPHIC(5))", ["abc"])).Rows))));
        Assert.Equal("東京", Assert.IsType<string>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES CAST(? AS VARGRAPHIC(10))", ["東京"])).Rows))));
        Assert.Equal("2026-08-26-12.34.56.123456", Assert.IsType<string>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES CAST(? AS TIMESTAMP)", ["2026-08-26 12:34:56.123456"])).Rows))));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveSqlStatementsAndParameterBlocksSpanMultipleDssSegments()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        foreach (var size in new[] { 16, 32, 64, 128, 1024 }.Select(kib => kib * 1024))
        {
            var query = new StringBuilder("SELECT COUNT(*) FROM SYSIBM.SYSDUMMY1 WHERE 1 IN (1");
            for (var value = 2; query.Length < size; value++) query.Append($", {value}");
            query.Append(')');
            Assert.Equal(1, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync(query.ToString())).Rows))));
            Assert.Equal(1, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync(query + " AND 1 = ?", [1])).Rows))));
            await db.ExecuteAsync("SET CURRENT QUERY OPTIMIZATION = 5" + new string(' ', size));
        }

        foreach (var count in new[] { 1, 2, 4 })
        {
            var query = "SELECT " + string.Join(" + ", Enumerable.Repeat("LENGTH(CAST(? AS VARCHAR(16000)))", count)) + " FROM SYSIBM.SYSDUMMY1";
            object?[] values = Enumerable.Range(0, count).Select(index => (object?)new string((char)('a' + index), 16_000)).ToArray();
            Assert.Equal(16_000 * count, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync(query, values)).Rows))));
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveConnectionDecodesScalarsReportsSqlErrorsAndPings()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var integers = await db.QueryAsync("VALUES (CAST(42 AS INTEGER), CAST(7 AS SMALLINT), CAST(9 AS BIGINT))");
        Assert.Equal(new object?[] { 42, (short)7, 9L }, Assert.Single(integers.Rows));

        var decimalResult = await db.QueryAsync("VALUES CAST(125 AS DECIMAL(5,2))");
        Assert.Equal("125.00", Assert.Single(Assert.Single(decimalResult.Rows)));

        var decfloat = await db.QueryAsync("VALUES (CAST('123.456' AS DECFLOAT(16)), CAST('-987.654' AS DECFLOAT(16)), CAST('Infinity' AS DECFLOAT(16)), CAST('NaN' AS DECFLOAT(16)))");
        Assert.Equal(new object?[] { "123.456", "-987.654", "Infinity", "NaN" }, Assert.Single(decfloat.Rows));
        var wideDecfloat = await db.QueryAsync("VALUES CAST('12345678901234567890.12345678901234' AS DECFLOAT(34))");
        Assert.Equal("12345678901234567890.12345678901234", Assert.Single(Assert.Single(wideDecfloat.Rows)));
        var nullDecfloat = await db.QueryAsync("VALUES CAST(NULL AS DECFLOAT(16))");
        Assert.Null(Assert.Single(Assert.Single(nullDecfloat.Rows)));

        var text = await db.QueryAsync("VALUES CAST(CURRENT SERVER AS VARCHAR(128))");
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(Assert.Single(Assert.Single(text.Rows)))));

        var error = await Assert.ThrowsAsync<Db2SqlException>(() => db.QueryAsync("SELECT FROM SYSIBM.SYSDUMMY1"));
        Assert.NotNull(error.SqlState);
        Assert.Contains("SQLCODE=", error.Message);
        await db.PingAsync();
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LivePreparedStatementsBindQueriesAndDmlParameters()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var reusable = await db.PrepareAsync("VALUES CAST(? AS INTEGER)");
        Assert.Single(reusable.ParameterColumns);
        Assert.Equal(42, Assert.IsType<int>(Assert.Single(Assert.Single((await reusable.QueryAsync([42])).Rows))));
        var otherStatement = await db.PrepareAsync("VALUES CAST(? AS BIGINT)");
        Assert.Equal(73L, Assert.IsType<long>(Assert.Single(Assert.Single((await otherStatement.QueryAsync([73L])).Rows))));
        Assert.Equal(73, Assert.IsType<int>(Assert.Single(Assert.Single((await reusable.QueryAsync([73])).Rows))));
        var typed = await db.QueryAsync("VALUES (CAST(? AS DECIMAL(7,2)), CAST(? AS VARCHAR(32)), CAST(? AS DATE), CAST(? AS BOOLEAN))", [125.50m, "Teclado Mecânico", new DateOnly(2026, 8, 26), true]);
        Assert.Equal(new object?[] { "125.50", "Teclado Mecânico", "2026-08-26", true }, Assert.Single(typed.Rows));
        var typedNulls = await db.QueryAsync("VALUES (CAST(? AS INTEGER), CAST(? AS VARCHAR(30)), CAST(? AS DECIMAL(9,3)), CAST(? AS DATE), CAST(? AS BOOLEAN), CAST(? AS XML))", [null, null, null, null, null, null]);
        Assert.All(Assert.Single(typedNulls.Rows), Assert.Null);
        var typedNullTypes = new[] { "SMALLINT", "INTEGER", "BIGINT", "REAL", "DOUBLE", "DECIMAL(9,3)", "DECFLOAT(16)", "DATE", "TIME", "TIMESTAMP", "CHAR(5)", "VARCHAR(12)", "GRAPHIC(5)", "VARGRAPHIC(10)", "BINARY(4)", "VARBINARY(10)", "BLOB(100)", "CLOB(100)", "DBCLOB(100)", "XML", "BOOLEAN" };
        var typedNullMatrix = await db.QueryAsync($"VALUES ({string.Join(", ", typedNullTypes.Select(type => $"CAST(? AS {type})"))})", new object?[typedNullTypes.Length]);
        var nullMatrixRow = Assert.Single(typedNullMatrix.Rows);
        Assert.Equal(typedNullTypes.Length, nullMatrixRow.Length);
        Assert.All(nullMatrixRow, Assert.Null);
        var varbinary = new byte[] { 0, 0x7f, 0x80, 0xff };
        var boundVarbinary = await db.QueryAsync("VALUES CAST(? AS VARBINARY(10))", [varbinary]);
        Assert.Equal(varbinary, Assert.IsType<byte[]>(Assert.Single(Assert.Single(boundVarbinary.Rows))));
        var decimalPrecisions = await db.QueryAsync("VALUES (CAST(? AS DECIMAL(4,2)), CAST(? AS DECIMAL(5,2)), CAST(? AS DECIMAL(8,3)), CAST(? AS DECIMAL(9,3)))", [1.25m, 1.25m, 12345.678m, 123456.789m]);
        Assert.Equal(new object?[] { "1.25", "1.25", "12345.678", "123456.789" }, Assert.Single(decimalPrecisions.Rows));
        Assert.Equal("1.23", Assert.Single(Assert.Single((await db.QueryAsync("VALUES CAST(? AS DECIMAL(5,2))", ["1.236"])).Rows)));
        await Assert.ThrowsAsync<OverflowException>(() => db.QueryAsync("VALUES CAST(? AS DECIMAL(2,2))", ["1.25"]));
        await Assert.ThrowsAsync<OverflowException>(() => db.QueryAsync("VALUES CAST(? AS SMALLINT)", [32768]));
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 29")).Rows))));
        var parameterBoundaries = await db.QueryAsync("VALUES (CAST(? AS SMALLINT), CAST(? AS INTEGER), CAST(? AS BIGINT), CAST(? AS DECIMAL(9,3)), CAST(? AS REAL), CAST(? AS DOUBLE))", [short.MinValue, int.MinValue, long.MaxValue, -123456.789m, 1.25f, -2.5d]);
        Assert.Equal(new object?[] { short.MinValue, int.MinValue, long.MaxValue, "-123456.789", 1.25f, -2.5d }, Assert.Single(parameterBoundaries.Rows));
        var typedDecfloat = await db.QueryAsync("VALUES (CAST(? AS DECFLOAT(16)), CAST(? AS DECFLOAT(16)), CAST(? AS DECFLOAT(16)), CAST(? AS DECFLOAT(34)), CAST(? AS DECFLOAT(16)))", ["123.456", "-987.654", "Infinity", "12345678901234567890.12345678901234", "NaN"]);
        Assert.Equal(new object?[] { "123.456", "-987.654", "Infinity", "12345678901234567890.12345678901234", "NaN" }, Assert.Single(typedDecfloat.Rows));
        var lobBytes = Enumerable.Range(0, 70_003).Select(index => (byte)(index % 251)).ToArray();
        foreach (var size in new[] { 32_766, 32_767, 32_768, lobBytes.Length })
        {
            var boundBytes = lobBytes.AsMemory(0, size).ToArray();
            Db2QueryResult boundLength;
            try { boundLength = await db.QueryAsync("VALUES LENGTH(CAST(? AS BLOB(1M)))", [boundBytes]); }
            catch (Exception error) { throw new InvalidOperationException($"BLOB query parameter size {size} failed.", error); }
            Assert.Equal(size, Assert.IsType<int>(Assert.Single(Assert.Single(boundLength.Rows))));
        }
        var clobText = string.Concat(Enumerable.Repeat("CLOB café ", 80));
        var dbclobText = string.Concat(Enumerable.Repeat("DBCLOB 東京 ", 80));
        const string xmlText = "<root><message>XML café</message></root>";
        var lobTable = $"CS_DB2_LOB_{Guid.NewGuid():N}";
        var lobTableCreated = false;
        try
        {
            await db.ExecuteAsync($"CREATE TABLE {schema}.{lobTable} (B BLOB(1M), C CLOB(1M), D DBCLOB(1M), X XML)");
            lobTableCreated = true;
            await db.ExecuteAsync($"INSERT INTO {lobTable} (B) VALUES (?)", [lobBytes]);
            Assert.Single((await db.QueryAsync($"SELECT 1 FROM {lobTable} WHERE B IS NOT NULL")).Rows);
            await db.ExecuteAsync($"DELETE FROM {lobTable}");
            await db.ExecuteAsync($"INSERT INTO {lobTable} (C) VALUES (?)", [clobText]);
            Assert.Single((await db.QueryAsync($"SELECT 1 FROM {lobTable} WHERE C IS NOT NULL")).Rows);
            await db.ExecuteAsync($"DELETE FROM {lobTable}");
            await db.ExecuteAsync($"INSERT INTO {lobTable} (D) VALUES (?)", [dbclobText]);
            Assert.Single((await db.QueryAsync($"SELECT 1 FROM {lobTable} WHERE D IS NOT NULL")).Rows);
            await db.ExecuteAsync($"DELETE FROM {lobTable}");
            await db.ExecuteAsync($"INSERT INTO {lobTable} (X) VALUES (?)", [xmlText]);
            Assert.Single((await db.QueryAsync($"SELECT 1 FROM {lobTable} WHERE X IS NOT NULL")).Rows);
            await db.ExecuteAsync($"DELETE FROM {lobTable}");
            Assert.Equal(1, await db.ExecuteAsync($"INSERT INTO {lobTable} VALUES (?, ?, ?, ?)", [lobBytes, clobText, dbclobText, xmlText]));
            var lobInputs = await db.QueryAsync($"SELECT B, C, D, XMLSERIALIZE(X AS VARCHAR(1024)) FROM {lobTable}");
            var row = Assert.Single(lobInputs.Rows);
            var returnedBytes = Assert.IsType<byte[]>(row[0]);
            Assert.Equal(lobBytes.Length, returnedBytes.Length);
            Assert.True(lobBytes.AsSpan().SequenceEqual(returnedBytes));
            Assert.Equal(clobText, row[1]);
            Assert.Equal(dbclobText, row[2]);
            Assert.Equal(xmlText, row[3]);
        }
        catch
        {
            if (lobTableCreated)
            {
                try { await db.ExecuteAsync($"DROP TABLE {schema}.{lobTable}"); }
                catch { /* Keep the failure from the DB2 operation under test. */ }
            }
            throw;
        }
        if (lobTableCreated) await db.ExecuteAsync($"DROP TABLE {schema}.{lobTable}");
        otherStatement.Close();
        reusable.Close();

        var table = $"CS_DB2_PARAMS_{Guid.NewGuid():N}";
        var created = false;
        try
        {
            await db.ExecuteAsync($"CREATE TABLE {schema}.{table} (V INTEGER NOT NULL UNIQUE)");
            created = true;
            var insert = await db.PrepareAsync($"INSERT INTO {table} (V) VALUES (?)");
            Assert.Equal(1, await insert.ExecuteAsync([91]));
            insert.Close();
            Assert.Equal(1, await db.ExecuteAsync($"INSERT INTO {table} (V) VALUES (?)", [92]));
            IReadOnlyList<object?>[] batch = [new object?[] { 93 }, new object?[] { 94 }];
            Assert.Equal(2, await db.ExecuteBatchAsync($"INSERT INTO {table} (V) VALUES (?)", batch));
            Assert.Equal(0, await db.ExecuteBatchAsync($"INSERT INTO {table} (V) VALUES (?)", Array.Empty<IReadOnlyList<object?>>()));
            var batchError = await Assert.ThrowsAsync<Db2BatchExecutionException>(() => db.ExecuteBatchAsync($"INSERT INTO {table} (V) VALUES (?)", [new object?[] { 95 }, new object?[] { 95 }]));
            Assert.Equal(1, batchError.ParameterSetIndex);
            Assert.Equal(1, batchError.CompletedParameterSets);
            Assert.Equal(1, batchError.RowsAffected);
            Assert.Equal(-803, batchError.SqlCode);
            Assert.Equal("23505", batchError.SqlState);
            Assert.IsType<Db2SqlException>(batchError.InnerException);
            Assert.Equal(91, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync($"SELECT V FROM {table} WHERE V = ?", [91])).Rows))));
            Assert.Equal(92, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync($"SELECT V FROM {table} WHERE V = ?", [92])).Rows))));
            var batchRows = await db.QueryAsync($"SELECT V FROM {table} WHERE V > ? ORDER BY V", [92]);
            Assert.Equal(new object?[] { 93 }, batchRows.Rows[0]);
            Assert.Equal(new object?[] { 94 }, batchRows.Rows[1]);
            Assert.Equal(new object?[] { 95 }, Assert.Single((await db.QueryAsync($"SELECT V FROM {table} WHERE V = 95")).Rows));
        }
        finally
        {
            if (created)
            {
                try { await db.ExecuteAsync($"DROP TABLE {schema}.{table}"); }
                catch (InvalidOperationException error) when (error.Message.Contains("SQLCODE=-204 SQLSTATE=42704", StringComparison.Ordinal)) { }
            }
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveInvalidDmlAndBatchParameterConversionPreservesCommittedRowsAndSession()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var table = $"CS_DB2_INVALID_BATCH_{Guid.NewGuid():N}".ToUpperInvariant();
        var created = false;
        try
        {
            await db.ExecuteAsync($"CREATE TABLE {schema}.{table} (V INTEGER NOT NULL)");
            created = true;
            var insertSql = $"INSERT INTO {table} VALUES (?)";
            object?[] invalid = [new byte[] { 49, 50, 51 }];

            await Assert.ThrowsAsync<InvalidCastException>(() => db.ExecuteAsync(insertSql, invalid));
            var prepared = await db.PrepareAsync(insertSql);
            try { await Assert.ThrowsAsync<InvalidCastException>(() => prepared.ExecuteAsync(invalid)); }
            finally { prepared.Close(); }

            IReadOnlyList<object?>[] batch = [new object?[] { 10 }, invalid];
            var batchError = await Assert.ThrowsAsync<Db2BatchExecutionException>(() => db.ExecuteBatchAsync(insertSql, batch));
            Assert.Equal(1, batchError.ParameterSetIndex);
            Assert.Equal(1, batchError.CompletedParameterSets);
            Assert.Equal(1, batchError.RowsAffected);
            Assert.IsType<InvalidCastException>(batchError.InnerException);
            Assert.Equal(new object?[] { 10 }, Assert.Single((await db.QueryAsync($"SELECT V FROM {table}")).Rows));
            Assert.False(db.IsClosed);
            Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 29")).Rows))));

            // B1: every query-path invalid pair must also reject locally in DML.
            (string Name, string Type, object Invalid, object Valid, object ExpectedValid)[] invalidDmlCases =
            [
                ("SMALLINT", "SMALLINT", new byte[] { 49, 50, 51 }, (short)1, (short)1),
                ("BLOB", "BLOB(20)", true, new byte[] { 1, 2 }, new byte[] { 1, 2 }),
                ("VARBINARY", "VARBINARY(20)", 123, new byte[] { 1 }, new byte[] { 1 }),
                ("CLOB_BOOL", "CLOB(20)", true, "hi", "hi"),
                ("CLOB_DEC", "CLOB(20)", 12.50m, "hi", "hi"),
                ("DBCLOB", "DBCLOB(20)", false, "hi", "hi"),
                ("XML", "XML", true, "<r/>", "<r/>"),
            ];
            foreach (var (name, type, invalidValue, valid, expectedValid) in invalidDmlCases)
            {
                var dmlTable = $"{table}_{name}";
                await db.ExecuteAsync($"CREATE TABLE {schema}.{dmlTable} (V {type})");
                try
                {
                    var dmlInsert = $"INSERT INTO {dmlTable} VALUES (?)";
                    await Assert.ThrowsAsync<InvalidCastException>(() => db.ExecuteAsync(dmlInsert, [invalidValue]));
                    var dmlPrepared = await db.PrepareAsync(dmlInsert);
                    try { await Assert.ThrowsAsync<InvalidCastException>(() => dmlPrepared.ExecuteAsync([invalidValue])); }
                    finally { dmlPrepared.Close(); }
                    var dmlBatchError = await Assert.ThrowsAsync<Db2BatchExecutionException>(() =>
                        db.ExecuteBatchAsync(dmlInsert, [new object?[] { valid }, new object?[] { invalidValue }]));
                    Assert.Equal(1, dmlBatchError.ParameterSetIndex);
                    Assert.Equal(1, dmlBatchError.CompletedParameterSets);
                    Assert.Equal(1, dmlBatchError.RowsAffected);
                    Assert.IsType<InvalidCastException>(dmlBatchError.InnerException);
                    var stored = Assert.Single((await db.QueryAsync($"SELECT V FROM {dmlTable}")).Rows);
                    Assert.Equal(expectedValid, Assert.Single(stored));
                    Assert.False(db.IsClosed);
                    Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 29")).Rows))));
                }
                finally
                {
                    await db.ExecuteAsync($"DROP TABLE {schema}.{dmlTable}");
                }
            }
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

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveMixedNullAndValueParametersKeepTheirPositionsAcrossExecutionModes()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var table = $"CS_MIXED_PARAMS_{Guid.NewGuid():N}".ToUpperInvariant();
        var created = false;
        var binary = new byte[] { 0, 0x7f, 0x80, 0xff };
        const string mixedQuery = "VALUES (CAST(? AS SMALLINT), CAST(? AS VARCHAR(20)), CAST(? AS DECIMAL(9,3)), CAST(? AS BOOLEAN), CAST(? AS VARBINARY(10)), CAST(? AS INTEGER))";
        object?[] mixedParameters = [12, null, 42.125m, true, binary, null];

        void AssertMixedQuery(Db2QueryResult result)
        {
            var row = Assert.Single(result.Rows);
            Assert.Equal((short)12, Assert.IsType<short>(row[0]));
            Assert.Null(row[1]);
            Assert.Equal("42.125", Assert.IsType<string>(row[2]));
            Assert.True(Assert.IsType<bool>(row[3]));
            Assert.Equal(binary, Assert.IsType<byte[]>(row[4]));
            Assert.Null(row[5]);
        }

        try
        {
            await db.ExecuteAsync($"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, LABEL VARCHAR(30), AMOUNT DECIMAL(9,3), ENABLED BOOLEAN)");
            created = true;

            AssertMixedQuery(await db.QueryAsync(mixedQuery, mixedParameters));
            var queryStatement = await db.PrepareAsync(mixedQuery);
            try { AssertMixedQuery(await queryStatement.QueryAsync(mixedParameters)); }
            finally { queryStatement.Close(); }

            const string insertSql = "INSERT INTO {0} (ID, LABEL, AMOUNT, ENABLED) VALUES (?, ?, ?, ?)";
            var qualifiedInsert = string.Format(System.Globalization.CultureInfo.InvariantCulture, insertSql, table);
            var preparedInsert = await db.PrepareAsync(qualifiedInsert);
            try { Assert.Equal(1, await preparedInsert.ExecuteAsync([1, null, 12.375m, true])); }
            finally { preparedInsert.Close(); }

            Assert.Equal(1, await db.ExecuteAsync(qualifiedInsert, [2, "direct", null, false]));
            IReadOnlyList<object?>[] batch =
            [
                new object?[] { 3, null, null, null },
                new object?[] { 4, "batch", 4.25m, true },
            ];
            Assert.Equal(2, await db.ExecuteBatchAsync(qualifiedInsert, batch));

            var rows = await db.QueryAsync($"SELECT ID, LABEL, AMOUNT, ENABLED FROM {table} ORDER BY ID");
            Assert.Collection(rows.Rows,
                row => Assert.Equal(new object?[] { 1, null, "12.375", true }, row),
                row => Assert.Equal(new object?[] { 2, "direct", null, false }, row),
                row => Assert.Equal(new object?[] { 3, null, null, null }, row),
                row => Assert.Equal(new object?[] { 4, "batch", "4.250", true }, row));
        }
        finally
        {
            if (created) await db.ExecuteAsync($"DROP TABLE {schema}.{table}");
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveMixedScalarParameterMatrixMatchesValuesAcrossQueryAndStreamPaths()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var binary = new byte[] { 0, 0x7f, 0x80, 0xff };
        (string Expression, object? FirstParameter, object? SecondParameter, object? FirstValue, object? SecondValue)[] cases =
        [
            ("CAST(? AS SMALLINT)", short.MinValue, null, short.MinValue, null),
            ("CAST(? AS INTEGER)", null, int.MaxValue, null, int.MaxValue),
            ("CAST(? AS BIGINT)", long.MaxValue, null, long.MaxValue, null),
            ("CAST(? AS REAL)", null, 1.25f, null, 1.25f),
            ("CAST(? AS DOUBLE)", -2.5d, null, -2.5d, null),
            ("CAST(? AS DECIMAL(9,3))", null, 42.125m, null, "42.125"),
            ("CAST(? AS DECFLOAT(16))", "Infinity", null, "Infinity", null),
            ("CAST(? AS DECFLOAT(34))", null, "1234567890123456789012345678901234", null, "1234567890123456789012345678901234"),
            ("CAST(? AS DATE)", new DateOnly(2026, 8, 26), null, "2026-08-26", null),
            ("CAST(? AS TIME)", null, "12:34:56", null, "12.34.56"),
            ("CAST(? AS TIMESTAMP)", "2026-08-26 12:34:56.123456", null, "2026-08-26-12.34.56.123456", null),
            ("CAST(? AS CHAR(5))", "ab", null, "ab   ", null),
            ("CAST(? AS VARCHAR(20))", null, "café", null, "café"),
            ("CAST(? AS GRAPHIC(5))", "東", null, "東    ", null),
            ("CAST(? AS VARGRAPHIC(10))", null, "東京", null, "東京"),
            ("CAST(? AS BINARY(4))", binary, null, binary, null),
            ("CAST(? AS VARBINARY(10))", null, binary, null, binary),
            ("CAST(? AS BLOB(100))", binary, null, binary, null),
            ("CAST(? AS CLOB(100))", null, "CLOB café", null, "CLOB café"),
            ("CAST(? AS DBCLOB(100))", "DBCLOB 東京", null, "DBCLOB 東京", null),
            ("CAST(? AS XML)", null, "<root/>", null, "<root/>"),
            ("CAST(? AS BOOLEAN)", true, null, true, null),
        ];

        var expressions = new List<string>(cases.Length * 2);
        var parameters = new List<object?>(cases.Length * 2);
        var expected = new List<object?>(cases.Length * 2);
        foreach (var item in cases)
        {
            expressions.Add(item.Expression);
            expressions.Add(item.Expression);
            parameters.Add(item.FirstParameter);
            parameters.Add(item.SecondParameter);
            expected.Add(item.FirstValue);
            expected.Add(item.SecondValue);
        }
        var sql = $"VALUES ({string.Join(", ", expressions)})";

        void AssertValues(IReadOnlyList<object?> row, IReadOnlyList<object?> expectedValues)
        {
            Assert.Equal(expectedValues.Count, row.Count);
            for (var index = 0; index < expectedValues.Count; index++)
            {
                if (expectedValues[index] is byte[] expectedBytes)
                    Assert.Equal(expectedBytes, Assert.IsType<byte[]>(row[index]));
                else Assert.Equal(expectedValues[index], row[index]);
            }
        }

        AssertValues(Assert.Single((await db.QueryAsync(sql, parameters)).Rows), expected);
        var statement = await db.PrepareAsync(sql);
        try { AssertValues(Assert.Single((await statement.QueryAsync(parameters)).Rows), expected); }
        finally { statement.Close(); }

        var streamed = new List<IReadOnlyList<object?>>();
        await foreach (var row in db.QueryStreamAsync(sql, parameters)) streamed.Add(row.Values);
        AssertValues(Assert.Single(streamed), expected);

        string SqlType(string expression)
        {
            const string prefix = "CAST(? AS ";
            return expression[prefix.Length..^1];
        }

        object?[] InputRow(int id, bool first)
        {
            var row = new object?[cases.Length + 1];
            row[0] = id;
            for (var index = 0; index < cases.Length; index++)
                row[index + 1] = first ? cases[index].FirstParameter : cases[index].SecondParameter;
            return row;
        }

        object?[] ExpectedRow(int id, bool first)
        {
            var row = new object?[cases.Length + 1];
            row[0] = id;
            for (var index = 0; index < cases.Length; index++)
                row[index + 1] = first ? cases[index].FirstValue : cases[index].SecondValue;
            return row;
        }

        var table = $"CS_TYPE_MATRIX_{Guid.NewGuid():N}".ToUpperInvariant();
        var columns = cases.Select((item, index) => $"C{index} {SqlType(item.Expression)}");
        var created = false;
        Exception? testFailure = null;
        try
        {
            await db.ExecuteAsync($"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, {string.Join(", ", columns)})");
            created = true;
            var insertSql = $"INSERT INTO {table} VALUES ({string.Join(", ", Enumerable.Repeat("?", cases.Length + 1))})";
            var insert = await db.PrepareAsync(insertSql);
            try { Assert.Equal(1, await insert.ExecuteAsync(InputRow(1, first: true))); }
            finally { insert.Close(); }

            Assert.Equal(1, await db.ExecuteAsync(insertSql, InputRow(2, first: false)));
            IReadOnlyList<object?>[] batch = [InputRow(3, first: true), InputRow(4, first: false)];
            Assert.Equal(2, await db.ExecuteBatchAsync(insertSql, batch));

            var persistedRows = (await db.QueryAsync($"SELECT * FROM {table} ORDER BY ID")).Rows;
            var expectedRows = new[]
            {
                ExpectedRow(1, first: true), ExpectedRow(2, first: false),
                ExpectedRow(3, first: true), ExpectedRow(4, first: false),
            };
            Assert.Equal(expectedRows.Length, persistedRows.Count);
            for (var index = 0; index < expectedRows.Length; index++)
            {
                Assert.Equal(expectedRows[index][0], persistedRows[index][0]);
                AssertValues(persistedRows[index].Skip(1).ToArray(), expectedRows[index].Skip(1).ToArray());
            }
        }
        catch (Exception error)
        {
            testFailure = error;
            throw;
        }
        finally
        {
            if (created)
            {
                await LiveTestCleanup.RunPreservingFailureAsync(testFailure, () =>
                    LiveTestCleanup.TryDropMissingObjectAsync(() => db.ExecuteAsync($"DROP TABLE {schema}.{table}")));
            }
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveBooleanParameterConversionsMatchDb2AndIbmProvider()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        const string sql = "VALUES (CAST(? AS BOOLEAN), CAST(? AS BOOLEAN), CAST(? AS BOOLEAN), CAST(? AS BOOLEAN), CAST(? AS BOOLEAN), CAST(? AS BOOLEAN), CAST(? AS BOOLEAN), CAST(? AS BOOLEAN), CAST(? AS BOOLEAN), CAST(? AS BOOLEAN), CAST(? AS BOOLEAN), CAST(? AS BOOLEAN))";
        object?[] values = ["true", " FALSE ", "YES", "no", "y", "N", "On", "OFF", "0", "2", "-0", " +2 "];
        object?[] expected = [true, false, true, false, true, false, true, false, false, true, false, true];

        static void AssertBooleanRow(IReadOnlyList<object?> row, IReadOnlyList<object?> expectedValues) =>
            Assert.Equal(expectedValues, row);

        AssertBooleanRow(Assert.Single((await db.QueryAsync(sql, values)).Rows), expected);
        var statement = await db.PrepareAsync(sql);
        try { AssertBooleanRow(Assert.Single((await statement.QueryAsync(values)).Rows), expected); }
        finally { statement.Close(); }

        var streamed = new List<IReadOnlyList<object?>>();
        await foreach (var row in db.QueryStreamAsync(sql, values)) streamed.Add(row.Values);
        AssertBooleanRow(Assert.Single(streamed), expected);

        var integerValues = await db.QueryAsync("VALUES (CAST(? AS BOOLEAN), CAST(? AS BOOLEAN), CAST(? AS BOOLEAN))", [0, 2, -1]);
        Assert.Equal(new object?[] { false, true, true }, Assert.Single(integerValues.Rows));
        foreach (var invalid in new object[] { "", " ", "1.0", "maybe", 1.5d, 1.5f, 1.5m })
            await Assert.ThrowsAsync<FormatException>(() => db.QueryAsync("VALUES CAST(? AS BOOLEAN)", [invalid]));

        Assert.False(db.IsClosed);
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 29")).Rows))));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveIntegerParameterNumericTextAndBooleanConversionsMatchIbmProvider()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        const string sql = "VALUES (CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT), CAST(? AS SMALLINT))";
        object?[] values = [-3.75d, -1.5m, " 1.5 ", "1e0", ".5", "1.", "+0001.50", true, false];
        object?[] expected = [(short)-3, (short)-1, (short)1, (short)1, (short)0, (short)1, (short)1, (short)1, (short)0];

        Assert.Equal(expected, Assert.Single((await db.QueryAsync(sql, values)).Rows));
        var statement = await db.PrepareAsync(sql);
        try { Assert.Equal(expected, Assert.Single((await statement.QueryAsync(values)).Rows)); }
        finally { statement.Close(); }

        var streamed = new List<IReadOnlyList<object?>>();
        await foreach (var row in db.QueryStreamAsync(sql, values)) streamed.Add(row.Values);
        Assert.Equal(new[] { expected }, streamed);

        foreach (var invalid in new object[] { "1,5", "abc", "" })
            await Assert.ThrowsAsync<FormatException>(() => db.QueryAsync("VALUES CAST(? AS SMALLINT)", [invalid]));
        await Assert.ThrowsAsync<OverflowException>(() => db.QueryAsync("VALUES CAST(? AS SMALLINT)", ["32768.0"]));
        Assert.False(db.IsClosed);
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 29")).Rows))));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveParameterSourceTypeConversionsMatchIbmCli()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        const string textSql = "VALUES (CAST(? AS VARCHAR(4)), CAST(? AS CHAR(4)), CAST(? AS GRAPHIC(4)), CAST(? AS VARGRAPHIC(4)))";
        object?[] textValues = [true, false, true, false];
        string[] expectedText = ["1", "0   ", "1   ", "0"];

        void AssertTextValues(Db2QueryResult result)
        {
            var row = Assert.Single(result.Rows);
            Assert.Equal(expectedText, row.Select(value => Assert.IsType<string>(value)).ToArray());
        }

        AssertTextValues(await db.QueryAsync(textSql, textValues));
        var textStatement = await db.PrepareAsync(textSql);
        try { AssertTextValues(await textStatement.QueryAsync(textValues)); }
        finally { textStatement.Close(); }
        var textStream = new List<IReadOnlyList<object?>>();
        await foreach (var row in db.QueryStreamAsync(textSql, textValues)) textStream.Add(row.Values);
        Assert.Equal(expectedText, Assert.Single(textStream).Select(value => Assert.IsType<string>(value)).ToArray());

        const string binarySql = "VALUES (CAST(? AS VARCHAR(4)), CAST(? AS CHAR(4)), CAST(? AS VARBINARY(3)), CAST(? AS BLOB(3)), CAST(? AS CLOB(20)), CAST(? AS DBCLOB(20)), CAST(? AS XML))";
        object?[] binaryValues =
        [
            new byte[] { 0xe6, 0x9d, 0xb1 },
            new byte[] { 49, 50, 51 },
            new byte[] { 49, 50, 51 },
            new byte[] { 49, 50, 51 },
            new byte[] { 0xc3, 0xa9 },
            new byte[] { 0x67, 0x71 },
            Encoding.UTF8.GetBytes("<r/>"),
        ];
        void AssertBinaryValues(IReadOnlyList<object?> row)
        {
            Assert.Equal("東", Assert.IsType<string>(row[0]));
            Assert.Equal("123 ", Assert.IsType<string>(row[1]));
            Assert.Equal(new byte[] { 49, 50, 51 }, Assert.IsType<byte[]>(row[2]));
            Assert.Equal(new byte[] { 49, 50, 51 }, Assert.IsType<byte[]>(row[3]));
            Assert.Equal("é", Assert.IsType<string>(row[4]));
            Assert.Equal("東", Assert.IsType<string>(row[5]));
            Assert.Equal("<r/>", Assert.IsType<string>(row[6]));
        }
        AssertBinaryValues(Assert.Single((await db.QueryAsync(binarySql, binaryValues)).Rows));
        var binaryStatement = await db.PrepareAsync(binarySql);
        try { AssertBinaryValues(Assert.Single((await binaryStatement.QueryAsync(binaryValues)).Rows)); }
        finally { binaryStatement.Close(); }
        var binaryStream = new List<IReadOnlyList<object?>>();
        await foreach (var row in db.QueryStreamAsync(binarySql, binaryValues)) binaryStream.Add(row.Values);
        AssertBinaryValues(Assert.Single(binaryStream));

        const string hexBinarySql = "VALUES (CAST(? AS BINARY(4)), CAST(? AS VARBINARY(4)), CAST(? AS BLOB(4)), CAST(? AS VARBINARY(4)) )";
        object?[] hexBinaryValues = ["313233", "313233", "313233", "aBcD"];
        void AssertHexBinaryRow(IReadOnlyList<object?> row)
        {
            Assert.Equal(new byte[] { 49, 50, 51, 0 }, Assert.IsType<byte[]>(row[0]));
            Assert.Equal(new byte[] { 49, 50, 51 }, Assert.IsType<byte[]>(row[1]));
            Assert.Equal(new byte[] { 49, 50, 51 }, Assert.IsType<byte[]>(row[2]));
            Assert.Equal(new byte[] { 0xab, 0xcd }, Assert.IsType<byte[]>(row[3]));
        }
        AssertHexBinaryRow(Assert.Single((await db.QueryAsync(hexBinarySql, hexBinaryValues)).Rows));
        var hexBinaryStatement = await db.PrepareAsync(hexBinarySql);
        try { AssertHexBinaryRow(Assert.Single((await hexBinaryStatement.QueryAsync(hexBinaryValues)).Rows)); }
        finally { hexBinaryStatement.Close(); }
        var hexBinaryRows = new List<IReadOnlyList<object?>>();
        await foreach (var row in db.QueryStreamAsync(hexBinarySql, hexBinaryValues)) hexBinaryRows.Add(row.Values);
        AssertHexBinaryRow(Assert.Single(hexBinaryRows));
        var emptyBinary = Assert.Single((await db.QueryAsync("VALUES (CAST(? AS VARBINARY(4)), CAST(? AS BLOB(4)))", ["", ""])).Rows);
        Assert.Empty(Assert.IsType<byte[]>(emptyBinary[0]));
        Assert.Empty(Assert.IsType<byte[]>(emptyBinary[1]));

        var binaryTable = $"CS_BINSTR_{Guid.NewGuid():N}"[..30].ToUpperInvariant();
        var binaryTableCreated = false;
        try
        {
            await db.ExecuteAsync($"CREATE TABLE {schema}.{binaryTable} (ID INT NOT NULL PRIMARY KEY, F BINARY(4), V VARBINARY(4), B BLOB(4))");
            binaryTableCreated = true;
            var insertSql = $"INSERT INTO {binaryTable} VALUES (?, ?, ?, ?)";
            object?[] insertValues(int id, string value) => [id, value, value, value];
            Assert.Equal(1, await db.ExecuteAsync(insertSql, insertValues(1, "313233")));
            var insert = await db.PrepareAsync(insertSql);
            try { Assert.Equal(1, await insert.ExecuteAsync(insertValues(2, "313233"))); }
            finally { insert.Close(); }
            IReadOnlyList<object?>[] batch = [insertValues(3, "313233"), insertValues(4, "")];
            Assert.Equal(2, await db.ExecuteBatchAsync(insertSql, batch));
            var rows = (await db.QueryAsync($"SELECT ID, F, V, B FROM {binaryTable} ORDER BY ID")).Rows;
            Assert.Equal(4, rows.Count);
            for (var index = 0; index < 3; index++)
            {
                Assert.Equal(index + 1, Assert.IsType<int>(rows[index][0]));
                Assert.Equal(new byte[] { 49, 50, 51, 0 }, Assert.IsType<byte[]>(rows[index][1]));
                Assert.Equal(new byte[] { 49, 50, 51 }, Assert.IsType<byte[]>(rows[index][2]));
                Assert.Equal(new byte[] { 49, 50, 51 }, Assert.IsType<byte[]>(rows[index][3]));
            }
            Assert.Equal(new byte[4], Assert.IsType<byte[]>(rows[3][1]));
            Assert.Empty(Assert.IsType<byte[]>(rows[3][2]));
            Assert.Empty(Assert.IsType<byte[]>(rows[3][3]));
        }
        finally { if (binaryTableCreated) await db.ExecuteAsync($"DROP TABLE {schema}.{binaryTable}"); }

        const string numericSql = "VALUES (CAST(? AS SMALLINT), CAST(? AS INTEGER), CAST(? AS BIGINT), CAST(? AS REAL), CAST(? AS DOUBLE), CAST(? AS DECIMAL(9,3)), CAST(? AS DECFLOAT(16)))";
        object?[] numericValues = [true, true, true, true, true, true, true];
        string[] expectedNumeric = ["1", "1", "1", "1", "1", "1.000", "1"];
        void AssertNumericValues(Db2QueryResult result) =>
            Assert.Equal(expectedNumeric, Assert.Single(result.Rows)
                .Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!)
                .ToArray());

        AssertNumericValues(await db.QueryAsync(numericSql, numericValues));
        var numericStatement = await db.PrepareAsync(numericSql);
        try { AssertNumericValues(await numericStatement.QueryAsync(numericValues)); }
        finally { numericStatement.Close(); }
        var numericStream = new List<IReadOnlyList<object?>>();
        await foreach (var row in db.QueryStreamAsync(numericSql, numericValues)) numericStream.Add(row.Values);
        Assert.Equal(expectedNumeric, Assert.Single(numericStream)
            .Select(value => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!)
            .ToArray());

        (string Sql, object Value)[] unsupported =
        [
            ("VALUES CAST(? AS SMALLINT)", new byte[] { 49, 50, 51 }),
            ("VALUES CAST(? AS BLOB(20))", true),
            ("VALUES CAST(? AS VARBINARY(20))", 123),
            ("VALUES CAST(? AS CLOB(20))", true),
            ("VALUES CAST(? AS CLOB(20))", 12.50m),
            ("VALUES CAST(? AS DBCLOB(20))", false),
            ("VALUES CAST(? AS XML)", true),
        ];
        foreach (var (sql, value) in unsupported)
            await Assert.ThrowsAsync<InvalidCastException>(() => db.QueryAsync(sql, [value]));
        await Assert.ThrowsAsync<InvalidCastException>(() => db.QueryAsync("VALUES CAST(? AS VARBINARY(20))", ["123"]));
        var invalidHexStatement = await db.PrepareAsync("VALUES CAST(? AS VARBINARY(20))");
        try { await Assert.ThrowsAsync<InvalidCastException>(() => invalidHexStatement.QueryAsync(["12G4"])); }
        finally { invalidHexStatement.Close(); }
        await Assert.ThrowsAsync<InvalidCastException>(async () =>
        {
            await foreach (var _ in db.QueryStreamAsync("VALUES CAST(? AS VARBINARY(2))", ["313233"])) { }
        });
        const string rejectedNumericSql = "VALUES CAST(? AS SMALLINT)";
        object?[] rejectedBinaryValue = [new byte[] { 49, 50, 51 }];
        var rejectedNumericStatement = await db.PrepareAsync(rejectedNumericSql);
        try
        {
            await Assert.ThrowsAsync<InvalidCastException>(
                () => rejectedNumericStatement.QueryAsync(rejectedBinaryValue));
        }
        finally { rejectedNumericStatement.Close(); }
        await Assert.ThrowsAsync<InvalidCastException>(async () =>
        {
            await foreach (var _ in db.QueryStreamAsync(rejectedNumericSql, rejectedBinaryValue)) { }
        });
        Assert.False(db.IsClosed);
        Assert.Equal(
            29,
            Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 29")).Rows))));
        const string invalidUtf8HexSql = "VALUES HEX(CAST(? AS VARCHAR(20)))";
        object?[] invalidUtf8Bytes = [new byte[] { 0xe9 }];
        Assert.Equal("E9", Assert.IsType<string>(Assert.Single(Assert.Single((await db.QueryAsync(invalidUtf8HexSql, invalidUtf8Bytes)).Rows))));
        var invalidUtf8Statement = await db.PrepareAsync(invalidUtf8HexSql);
        try
        {
            Assert.Equal("E9", Assert.IsType<string>(Assert.Single(Assert.Single((await invalidUtf8Statement.QueryAsync(invalidUtf8Bytes)).Rows))));
        }
        finally { invalidUtf8Statement.Close(); }
        var invalidUtf8Stream = new List<IReadOnlyList<object?>>();
        await foreach (var row in db.QueryStreamAsync(invalidUtf8HexSql, invalidUtf8Bytes)) invalidUtf8Stream.Add(row.Values);
        Assert.Equal("E9", Assert.IsType<string>(Assert.Single(Assert.Single(invalidUtf8Stream))));

        Assert.False(db.IsClosed);
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 29")).Rows))));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveQueriesMaterializeExtdtaXmlAndLargeObjects()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var xml = await db.QueryAsync("SELECT XMLPARSE(DOCUMENT '<root><item id=\"1\">x</item></root>') FROM SYSIBM.SYSDUMMY1");
        Assert.Equal("<root><item id=\"1\">x</item></root>", Assert.IsType<string>(Assert.Single(Assert.Single(xml.Rows))));

        var nullXml = await db.QueryAsync("SELECT CAST(NULL AS XML) FROM SYSIBM.SYSDUMMY1");
        Assert.Null(Assert.Single(Assert.Single(nullXml.Rows)));

        var largeXml = await db.QueryAsync("SELECT XMLELEMENT(NAME \"r\", XMLAGG(XMLELEMENT(NAME \"c\", COLNAME))) FROM SYSCAT.COLUMNS");
        var xmlText = Assert.IsType<string>(Assert.Single(Assert.Single(largeXml.Rows)));
        Assert.True(xmlText.Length >= 100_000, $"large XML is {xmlText.Length} characters");
        Assert.StartsWith("<r><c>", xmlText);
        Assert.EndsWith("</c></r>", xmlText);

        var blob = await db.QueryAsync("SELECT BLOB(CAST(REPEAT('z', 30000) AS VARCHAR(30000) FOR BIT DATA)) || BLOB(CAST(REPEAT('y', 30000) AS VARCHAR(30000) FOR BIT DATA)) FROM SYSIBM.SYSDUMMY1");
        var bytes = Assert.IsType<byte[]>(Assert.Single(Assert.Single(blob.Rows)));
        Assert.Equal(60_000, bytes.Length);
        Assert.Equal(new string('z', 30_000) + new string('y', 30_000), Encoding.ASCII.GetString(bytes));

        var xmlRows = await db.QueryAsync("SELECT XMLELEMENT(NAME \"t\", TABNAME) FROM SYSCAT.TABLES FETCH FIRST 5 ROWS ONLY");
        Assert.Equal(5, xmlRows.Rows.Count);
        Assert.All(xmlRows.Rows, row => Assert.StartsWith("<t>", Assert.IsType<string>(Assert.Single(row))));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveLargeClobAndDbClobLocatorsAdvertiseByteBudgets()
    {
        // Db2 returns progressive locators for these large text values on the verified
        // profile. A progressive DBCLOB locator advertises UTF-16 code units while its
        // GETNXTCHK chunks are UTF-16BE bytes, so the byte budget must be doubled.
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var table = $"CS_BIG_TEXT_LOB_{Guid.NewGuid():N}";
        await db.ExecuteAsync($"CREATE TABLE {schema}.{table} (C CLOB(32M), D DBCLOB(32M))");
        try
        {
            var clobText = new string('é', 4_000_000);
            var dbclobText = new string('漢', 4_000_000);
            var clobBytes = Encoding.UTF8.GetBytes(clobText);
            var dbclobBytes = Encoding.UTF8.GetBytes(dbclobText);
            await using (var clobSource = new MemoryStream(clobBytes, writable: false))
            await using (var dbclobSource = new MemoryStream(dbclobBytes, writable: false))
            {
                Assert.Equal(1, await db.ExecuteAsync(
                    $"INSERT INTO {table} VALUES (?, ?)",
                    [new Db2ClobStream(clobSource, clobBytes.Length), new Db2DbClobStream(dbclobSource, dbclobBytes.Length)]));
            }

            await using var reader = db.QueryStreamAsync($"SELECT C, D FROM {table}").GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync());
            var row = reader.Current;
            var clobLob = Assert.IsType<Db2LobValue>(row.Values[0]);
            var dbclobLob = Assert.IsType<Db2LobValue>(row.Values[1]);
            Assert.Equal(clobBytes.Length, clobLob.ByteLength);
            Assert.Equal((long)dbclobText.Length * 2, dbclobLob.ByteLength);
            using (var text = clobLob.OpenTextReader())
                Assert.Equal(clobText, await text.ReadToEndAsync());
            using (var text = dbclobLob.OpenTextReader())
                Assert.Equal(dbclobText, await text.ReadToEndAsync());
            Assert.False(await reader.MoveNextAsync());
        }
        finally
        {
            await db.ExecuteAsync($"DROP TABLE {schema}.{table}");
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveStreamedBlobParametersSendInBoundedChunksAndPreserveOwnership()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);

        const int roundTripLength = 80_003;
        var roundTripSource = new PatternReadStream(roundTripLength);
        var roundTrip = await db.QueryAsync("VALUES CAST(? AS BLOB(1M))", [new Db2BlobStream(roundTripSource, roundTripLength)]);
        var expected = Enumerable.Range(0, roundTripLength).Select(index => (byte)((index * 37 + 11) % 251)).ToArray();
        Assert.Equal(expected, Assert.IsType<byte[]>(Assert.Single(Assert.Single(roundTrip.Rows))));
        Assert.Equal(roundTripLength, roundTripSource.BytesRead);
        Assert.False(roundTripSource.IsDisposed);

        const int largeLength = 2_000_123;
        var largeSource = new PatternReadStream(largeLength);
        var largeLengthResult = await db.QueryAsync("VALUES LENGTH(CAST(? AS BLOB(10M)))", [new Db2BlobStream(largeSource, largeLength, LeaveOpen: false)]);
        Assert.Equal(largeLength, Assert.IsType<int>(Assert.Single(Assert.Single(largeLengthResult.Rows))));
        Assert.Equal(largeLength, largeSource.BytesRead);
        Assert.True(largeSource.MaxRequestedRead <= 32_765, $"driver requested {largeSource.MaxRequestedRead} bytes from one source read");
        Assert.True(largeSource.IsDisposed);

        var emptySource = new PatternReadStream(0);
        var emptyResult = await db.QueryAsync("VALUES LENGTH(CAST(? AS BLOB(1M)))", [new Db2BlobStream(emptySource, 0)]);
        Assert.Equal(0, Assert.IsType<int>(Assert.Single(Assert.Single(emptyResult.Rows))));
        Assert.Equal(0, emptySource.BytesRead);

        var prepared = await db.PrepareAsync("VALUES CAST(? AS BLOB(1M))");
        var preparedSource = new PatternReadStream(70_001);
        var preparedResult = await prepared.QueryAsync([new Db2BlobStream(preparedSource, 70_001)]);
        Assert.Equal(
            Enumerable.Range(0, 70_001).Select(index => (byte)((index * 37 + 11) % 251)).ToArray(),
            Assert.IsType<byte[]>(Assert.Single(Assert.Single(preparedResult.Rows))));

        var streamedRows = new List<object?[]>();
        var queryStreamSource = new PatternReadStream(90_007);
        await foreach (var row in db.QueryStreamAsync(
            "VALUES LENGTH(CAST(? AS BLOB(1M)))",
            [new Db2BlobStream(queryStreamSource, 90_007)]))
            streamedRows.Add(row.Values);
        Assert.Equal(new object?[] { 90_007 }, Assert.Single(streamedRows));

        var table = $"CS_DB2_STREAM_{Guid.NewGuid():N}";
        await db.ExecuteAsync($"CREATE TABLE {schema}.{table} (ID INT NOT NULL PRIMARY KEY, B BLOB(10M))");
        try
        {
            var insertSource = new PatternReadStream(80_003);
            Assert.Equal(1, await db.ExecuteAsync(
                $"INSERT INTO {table} VALUES (?, ?)",
                [1, new Db2BlobStream(insertSource, 80_003)]));
            var stored = await db.QueryAsync($"SELECT B FROM {table} WHERE ID = 1");
            Assert.Equal(
                Enumerable.Range(0, 80_003).Select(index => (byte)((index * 37 + 11) % 251)).ToArray(),
                Assert.IsType<byte[]>(Assert.Single(Assert.Single(stored.Rows))));

            IReadOnlyList<object?>[] batch =
            [
                new object?[] { 2, new Db2BlobStream(new PatternReadStream(90_007), 90_007) },
                new object?[] { 3, new Db2BlobStream(new PatternReadStream(100_009), 100_009) },
            ];
            Assert.Equal(2, await db.ExecuteBatchAsync($"INSERT INTO {table} VALUES (?, ?)", batch));

            await using (var transaction = await db.BeginTransactionAsync())
            {
                await transaction.ExecuteAsync(
                    $"UPDATE {table} SET B = ? WHERE ID = ?",
                    [new Db2BlobStream(new PatternReadStream(200_011), 200_011), 1]);
                await transaction.RollbackAsync();
            }
            Assert.Equal(80_003, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync($"SELECT LENGTH(B) FROM {table} WHERE ID = 1")).Rows))));

            await using var pool = new Db2Pool(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!, new Db2PoolOptions(1));
            var pooled = await pool.QueryAsync("VALUES LENGTH(CAST(? AS BLOB(1M)))", [new Db2BlobStream(new PatternReadStream(77_777), 77_777)]);
            Assert.Equal(77_777, Assert.IsType<int>(Assert.Single(Assert.Single(pooled.Rows))));
        }
        finally { await db.ExecuteAsync($"DROP TABLE {schema}.{table}"); }

        var shortSource = new PatternReadStream(40_000);
        await Assert.ThrowsAsync<EndOfStreamException>(() => db.QueryAsync(
            "VALUES LENGTH(CAST(? AS BLOB(1M)))",
            [new Db2BlobStream(shortSource, 100_000)]));
        await Assert.ThrowsAsync<Db2ConnectionClosedException>(() => db.QueryAsync("VALUES 1"));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LivePreparedParameterizedQueryMaterializesLargeBlobWithMatchingChecksum()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        const int blobLength = 2_500_123;
        var table = $"CS_DB2_BLOB_{Guid.NewGuid():N}".ToUpperInvariant();
        var expectedBytes = new byte[blobLength];
        for (var index = 0; index < expectedBytes.Length; index++)
            expectedBytes[index] = (byte)((index * 37 + 11) % 251);
        var expectedChecksum = SHA256.HashData(expectedBytes);
        var created = false;

        try
        {
            await db.ExecuteAsync($"CREATE TABLE {schema}.{table} (ID INT NOT NULL PRIMARY KEY, B BLOB(10M))");
            created = true;
            await db.ExecuteAsync($"INSERT INTO {table} VALUES (?, ?)", [1, new Db2BlobStream(new PatternReadStream(blobLength), blobLength)]);

            var statement = await db.PrepareAsync($"SELECT B FROM {table} WHERE ID = ?");
            var result = await statement.QueryAsync([1]);
            var blob = Assert.IsType<byte[]>(Assert.Single(Assert.Single(result.Rows)));

            Assert.Equal(blobLength, blob.Length);
            Assert.Equal(expectedChecksum, SHA256.HashData(blob));

            void AssertMatchingBlob(object? value)
            {
                if (value is byte[] materialized)
                {
                    Assert.Equal(blobLength, materialized.Length);
                    Assert.Equal(expectedChecksum, SHA256.HashData(materialized));
                    return;
                }

                var locator = Assert.IsType<Db2LobValue>(value);
                Assert.False(locator.IsText);
                Assert.Equal(blobLength, locator.ByteLength);
                using var stream = locator.OpenReadStream();
                Assert.Equal(expectedChecksum, SHA256.HashData(stream));
            }

            await foreach (var row in db.QueryStreamAsync($"SELECT B FROM {table} WHERE ID = ?", [1]))
                AssertMatchingBlob(Assert.Single(row.Values));
            await foreach (var row in statement.QueryStreamAsync([1]))
                AssertMatchingBlob(Assert.Single(row.Values));
        }
        finally
        {
            if (created) await db.ExecuteAsync($"DROP TABLE {schema}.{table}");
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveStreamedClobParameterPreservesUtf16Text()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var expected = string.Concat(Enumerable.Repeat("CLOB café 東京 🚀 ", 5_000));
        var encoded = Encoding.UTF8.GetBytes(expected);
        await using var source = new MemoryStream(encoded, writable: false);
        var result = await db.QueryAsync("VALUES CAST(? AS CLOB(1M))", [new Db2ClobStream(source, encoded.Length)]);
        Assert.Equal(expected, Assert.IsType<string>(Assert.Single(Assert.Single(result.Rows))));
        Assert.Equal(encoded.Length, source.Position);
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveStreamedDbClobAndXmlParametersPreserveUtf8Text()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var dbClobText = string.Concat(Enumerable.Repeat("DBCLOB 東京 🚀 ", 5_000));
        var dbClobBytes = Encoding.UTF8.GetBytes(dbClobText);
        var xmlText = $"<root><message>XML café 東京 🚀</message><body>{new string('x', 100_000)}</body></root>";
        var xmlBytes = Encoding.UTF8.GetBytes(xmlText);
        var table = $"CS_DB2_TEXTLOB_{Guid.NewGuid():N}"[..30].ToUpperInvariant();
        await db.ExecuteAsync($"CREATE TABLE {schema}.{table} (D DBCLOB(1M), X XML)");
        try
        {
            await using (var dbClobSource = new MemoryStream(dbClobBytes, writable: false))
            {
                Assert.Equal(1, await db.ExecuteAsync($"INSERT INTO {table} (D) VALUES (?)", [new Db2DbClobStream(dbClobSource, dbClobBytes.Length)]));
                Assert.Equal(dbClobBytes.Length, dbClobSource.Position);
            }
            var dbClob = await db.QueryAsync($"SELECT LENGTH(D), SUBSTR(D, 1, 100), SUBSTR(D, LENGTH(D) - 99, 100) FROM {table} WHERE D IS NOT NULL");
            var dbClobRow = Assert.Single(dbClob.Rows);
            Assert.Equal(dbClobText.Length, Assert.IsType<int>(dbClobRow[0]));
            Assert.Equal(dbClobText[..100], Assert.IsType<string>(dbClobRow[1]));
            Assert.Equal(dbClobText[^100..], Assert.IsType<string>(dbClobRow[2]));

            await using var xmlSource = new MemoryStream(xmlBytes, writable: false);
            Assert.Equal(1, await db.ExecuteAsync($"INSERT INTO {table} (X) VALUES (?)", [new Db2XmlStream(xmlSource, xmlBytes.Length)]));
            var stored = await db.QueryAsync($"SELECT XMLSERIALIZE(X AS CLOB(1M)) FROM {table} WHERE X IS NOT NULL");
            Assert.Equal(xmlText, Assert.IsType<string>(Assert.Single(Assert.Single(stored.Rows))));
            Assert.Equal(xmlBytes.Length, xmlSource.Position);
        }
        finally { await db.ExecuteAsync($"DROP TABLE {schema}.{table}"); }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveShortTextLobStreamsInvalidateTheirSessions()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;

        await using (var db = await Db2Session.ConnectAsync(dsn))
        {
            var source = new PatternReadStream(40_000);
            await Assert.ThrowsAsync<EndOfStreamException>(() => db.QueryAsync(
                "VALUES LENGTH(CAST(? AS CLOB(1M)))",
                [new Db2ClobStream(source, 100_000)]));
            Assert.True(db.IsClosed);
        }

        await using (var db = await Db2Session.ConnectAsync(dsn))
        {
            var source = new PatternReadStream(40_000);
            await Assert.ThrowsAsync<EndOfStreamException>(() => db.QueryAsync(
                "VALUES LENGTH(CAST(? AS DBCLOB(1M)))",
                [new Db2DbClobStream(source, 100_000)]));
            Assert.True(db.IsClosed);
        }

        await using (var db = await Db2Session.ConnectAsync(dsn))
        {
            var source = new PatternReadStream(40_000);
            await Assert.ThrowsAsync<EndOfStreamException>(() => db.QueryAsync(
                "VALUES XMLSERIALIZE(CAST(? AS XML) AS CLOB(1M))",
                [new Db2XmlStream(source, 100_000)]));
            Assert.True(db.IsClosed);
        }

        await using (var db = await Db2Session.ConnectAsync(dsn))
        {
            var firstBytes = Encoding.UTF8.GetBytes("first text LOB 🚀".PadRight(40_000, 'a'));
            await using var firstSource = new MemoryStream(firstBytes, writable: false);
            var secondSource = new PatternReadStream(40_000);
            await Assert.ThrowsAsync<EndOfStreamException>(() => db.QueryAsync(
                "VALUES (LENGTH(CAST(? AS CLOB(1M))), LENGTH(CAST(? AS DBCLOB(1M))))",
                [new Db2ClobStream(firstSource, firstBytes.Length), new Db2DbClobStream(secondSource, 100_000)]));
            Assert.Equal(firstBytes.Length, firstSource.Position);
            Assert.True(db.IsClosed);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveTransactionHandlesCommitAndRollbackAtomically()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var table = $"CS_DB2_TX_{Guid.NewGuid():N}";
        var created = false;
        try
        {
            await db.ExecuteAsync($"CREATE TABLE {schema}.{table} (V INTEGER)");
            created = true;

            await using (var transaction = await db.BeginTransactionAsync())
            {
                await transaction.ExecuteAsync($"INSERT INTO {table} VALUES (1)");
                await transaction.RollbackAsync();
            }
            Assert.True(db.AutoCommit);
            Assert.Equal(0, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync($"SELECT COUNT(*) FROM {table}")).Rows))));

            await using (var transaction = await db.BeginTransactionAsync())
            {
                await transaction.ExecuteAsync($"INSERT INTO {table} VALUES (2)");
                await transaction.CommitAsync();
            }
            Assert.True(db.AutoCommit);
            Assert.Equal(2, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync($"SELECT V FROM {table}")).Rows))));
        }
        finally
        {
            if (created) await db.ExecuteAsync($"DROP TABLE {schema}.{table}");
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveInsertResultsReturnGeneratedIdentityValues()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var table = $"CS_DB2_ID_{Guid.NewGuid():N}";
        var created = false;
        try
        {
            await db.ExecuteAsync($"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL GENERATED ALWAYS AS IDENTITY, V VARCHAR(32))");
            created = true;
            var direct = await db.ExecuteInsertAsync($"INSERT INTO {table} (V) VALUES (?)", ["direct"]);
            Assert.Equal(new Db2InsertResult(1, 1), direct);

            var statement = await db.PrepareAsync($"INSERT INTO {table} (V) VALUES (?)");
            var prepared = await statement.ExecuteInsertAsync(["prepared"]);
            Assert.Equal(new Db2InsertResult(1, 2), prepared);
            statement.Close();

            var rows = await db.QueryAsync($"SELECT ID, V FROM {table} ORDER BY ID");
            Assert.Equal(new object?[] { 1, "direct" }, rows.Rows[0]);
            Assert.Equal(new object?[] { 2, "prepared" }, rows.Rows[1]);
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

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveClientInfoUpdatesAndRestoresAccountingRegisters()
    {
        await using var db = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var baseline = db.GetClientInfo();
        await Assert.ThrowsAsync<ArgumentException>(() => db.SwitchUserAsync("alice\"; DROP TABLE T"));
        Assert.Equal(Db2Config.Parse(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!).User, db.CurrentSessionUser);
        await db.SetClientInfoAsync(new Db2ClientInfo("audit'service", "JustyBase.Db2-worker", "audit_user", "cost_center_7"));
        Assert.Equal("audit'service", db.GetClientInfo().ApplicationName);
        Assert.Equal("cost_center_7", db.GetClientInfo().Accounting);
        var current = await db.QueryAsync("SELECT COALESCE(CURRENT CLIENT_APPLNAME, ''), COALESCE(CURRENT CLIENT_WRKSTNNAME, ''), COALESCE(CURRENT CLIENT_USERID, ''), COALESCE(CURRENT CLIENT_ACCTNG, '') FROM SYSIBM.SYSDUMMY1");
        Assert.Equal(new object?[] { "audit'service", "JustyBase.Db2-worker", "audit_user", "cost_center_7" }, Assert.Single(current.Rows));

        await db.ResetClientInfoAsync(baseline);
        Assert.True(db.ClientInfoMatches(baseline));
        var restored = await db.QueryAsync("SELECT COALESCE(CURRENT CLIENT_APPLNAME, ''), COALESCE(CURRENT CLIENT_WRKSTNNAME, ''), COALESCE(CURRENT CLIENT_USERID, ''), COALESCE(CURRENT CLIENT_ACCTNG, '') FROM SYSIBM.SYSDUMMY1");
        Assert.Equal(new object?[] { baseline.ApplicationName, baseline.WorkstationName, baseline.UserId, baseline.Accounting }, Assert.Single(restored.Rows));

        var config = Db2Config.Parse(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        config.ClientApplName = "configured_app";
        config.ClientWrkstnName = "configured_worker";
        config.ClientUserid = "configured_user";
        config.ClientAcctng = "configured_acct";
        await using var configured = await Db2Session.ConnectAsync(config);
        var initial = await configured.QueryAsync("SELECT COALESCE(CURRENT CLIENT_APPLNAME, ''), COALESCE(CURRENT CLIENT_WRKSTNNAME, ''), COALESCE(CURRENT CLIENT_USERID, ''), COALESCE(CURRENT CLIENT_ACCTNG, '') FROM SYSIBM.SYSDUMMY1");
        Assert.Equal(new object?[] { "configured_app", "configured_worker", "configured_user", "configured_acct" }, Assert.Single(initial.Rows));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LivePoolReusesOneSessionAndClosesIt()
    {
        await using var pool = new Db2Pool(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!, new Db2PoolOptions(1));
        var first = await pool.QueryAsync("VALUES CAST(42 AS INTEGER)");
        Assert.Equal(42, Assert.IsType<int>(Assert.Single(Assert.Single(first.Rows))));
        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(1, pool.IdleSessionCount);

        var second = await pool.QueryAsync("VALUES CAST(? AS INTEGER)", [73]);
        Assert.Equal(73, Assert.IsType<int>(Assert.Single(Assert.Single(second.Rows))));
        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(1, pool.IdleSessionCount);

        var baseline = await pool.WithSessionAsync((session, _) => Task.FromResult(session.GetClientInfo()));
        await pool.WithSessionAsync(async (session, token) =>
        {
            await session.SetClientInfoAsync(new Db2ClientInfo("pooled_tenant", UserId: "tenant_user", Accounting: "tenant_batch"), token);
            return true;
        });
        var registers = await pool.WithSessionAsync(async (session, token) =>
            await session.QueryAsync("SELECT COALESCE(CURRENT CLIENT_APPLNAME, ''), COALESCE(CURRENT CLIENT_WRKSTNNAME, ''), COALESCE(CURRENT CLIENT_USERID, ''), COALESCE(CURRENT CLIENT_ACCTNG, '') FROM SYSIBM.SYSDUMMY1", token));
        Assert.Equal(new object?[] { baseline.ApplicationName, baseline.WorkstationName, baseline.UserId, baseline.Accounting }, Assert.Single(registers.Rows));

        await pool.DisposeAsync();
        Assert.Equal(0, pool.SessionCount);
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LivePoolRestoresSchemaAndIsolationRegistersAfterRawSet()
    {
        await using var pool = new Db2Pool(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!, new Db2PoolOptions(1));
        var baseline = await pool.WithSessionAsync(async (session, token) => (
            Schema: Assert.Single((await session.QueryAsync("VALUES CURRENT SCHEMA", token)).Rows),
            Isolation: Assert.Single((await session.QueryAsync("VALUES CURRENT ISOLATION", token)).Rows)));

        // Borrower A changes session state through raw SQL, outside any transaction scope.
        await pool.WithSessionAsync(async (session, token) =>
        {
            await session.ExecuteAsync("SET CURRENT SCHEMA JBL_LIVE", token);
            await session.ExecuteAsync("SET CURRENT ISOLATION UR", token);
            Assert.Equal("JBL_LIVE", Assert.IsType<string>(Assert.Single(Assert.Single((await session.QueryAsync("VALUES CURRENT SCHEMA", token)).Rows))));
            Assert.Equal("UR", Assert.IsType<string>(Assert.Single(Assert.Single((await session.QueryAsync("VALUES CURRENT ISOLATION", token)).Rows))));
            return true;
        });
        Assert.Equal(1, pool.SessionCount);

        // Borrower B observes the exact baseline and runs a prepared query on the same lease.
        await pool.WithSessionAsync(async (session, token) =>
        {
            Assert.Equal(baseline.Schema, Assert.Single((await session.QueryAsync("VALUES CURRENT SCHEMA", token)).Rows));
            Assert.Equal(baseline.Isolation, Assert.Single((await session.QueryAsync("VALUES CURRENT ISOLATION", token)).Rows));
            var statement = await session.PrepareAsync("VALUES 29", token);
            try { Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await statement.QueryAsync([], token)).Rows)))); }
            finally { statement.Close(); }
            return true;
        });
        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(1, pool.IdleSessionCount);
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LivePoolRestoresClientRegistersAfterRawSetAndWlmCall()
    {
        await using var pool = new Db2Pool(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!, new Db2PoolOptions(1));
        var baseline = await pool.WithSessionAsync(async (session, token) =>
        {
            var result = await session.QueryAsync(
                "SELECT COALESCE(CURRENT CLIENT_APPLNAME, ''), COALESCE(CURRENT CLIENT_WRKSTNNAME, ''), COALESCE(CURRENT CLIENT_USERID, ''), COALESCE(CURRENT CLIENT_ACCTNG, '') FROM SYSIBM.SYSDUMMY1",
                token);
            return Assert.Single(result.Rows);
        });

        await pool.WithSessionAsync(async (session, token) =>
        {
            await session.ExecuteAsync("CALL SYSPROC.WLM_SET_CLIENT_INFO('raw_pool_user', 'raw_pool_workstation', 'raw_pool_application', 'raw_pool_accounting', NULL)", token);
            var changed = await session.QueryAsync(
                "SELECT COALESCE(CURRENT CLIENT_APPLNAME, ''), COALESCE(CURRENT CLIENT_WRKSTNNAME, ''), COALESCE(CURRENT CLIENT_USERID, ''), COALESCE(CURRENT CLIENT_ACCTNG, '') FROM SYSIBM.SYSDUMMY1",
                token);
            Assert.Equal(4, Assert.Single(changed.Rows).Length);
            Assert.Equal(new object?[] { "raw_pool_application", "raw_pool_workstation", "raw_pool_user", "raw_pool_accounting" }, Assert.Single(changed.Rows));
            return true;
        });

        var restored = await pool.WithSessionAsync(async (session, token) =>
        {
            var result = await session.QueryAsync(
                "SELECT COALESCE(CURRENT CLIENT_APPLNAME, ''), COALESCE(CURRENT CLIENT_WRKSTNNAME, ''), COALESCE(CURRENT CLIENT_USERID, ''), COALESCE(CURRENT CLIENT_ACCTNG, '') FROM SYSIBM.SYSDUMMY1",
                token);
            return Assert.Single(result.Rows);
        });
        Assert.Equal(baseline, restored);
        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(1, pool.IdleSessionCount);
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LivePoolDiscardsAbandonedTransactionBeforeReuse()
    {
        await using var pool = new Db2Pool(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!, new Db2PoolOptions(1));
        var schema = await pool.WithSessionAsync((session, _) => LiveFixtureSchema.ForSessionAsync(session));
        var table = $"CS_POOL_ABANDON_{Guid.NewGuid():N}";
        await pool.ExecuteAsync($"CREATE TABLE {schema}.{table} (V INTEGER)");
        try
        {
            Db2Transaction? abandoned = null;
            await Assert.ThrowsAsync<Db2AbandonedTransactionException>(() => pool.WithSessionAsync(async (session, token) =>
            {
                abandoned = await session.BeginTransactionAsync(token);
                await abandoned.ExecuteAsync($"INSERT INTO {table} VALUES (1)", token);
                return true;
            }));
            Assert.NotNull(abandoned);
            await Assert.ThrowsAsync<InvalidOperationException>(() => abandoned!.ExecuteAsync($"INSERT INTO {table} VALUES (2)"));
            Assert.Equal(0, pool.SessionCount);
            Assert.Equal(0, pool.IdleSessionCount);

            Assert.Equal(0, Assert.IsType<int>(Assert.Single(Assert.Single((await pool.QueryAsync($"SELECT COUNT(*) FROM {table}")).Rows))));
            Assert.Equal(1, pool.SessionCount);
            Assert.Equal(1, pool.IdleSessionCount);
        }
        finally
        {
            try { await pool.ExecuteAsync($"DROP TABLE {schema}.{table}"); }
            catch (Db2SqlException error) when (error.SqlCode == -204 && error.SqlState == "42704") { }
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveAbandonedTransactionScopeDiscardsItsSession()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var table = $"CS_DB2_ABANDON_{Guid.NewGuid():N}";
        await using var setup = await Db2Session.ConnectAsync(dsn);
        var schema = await LiveFixtureSchema.ForSessionAsync(setup);
        await setup.ExecuteAsync($"CREATE TABLE {schema}.{table} (V INTEGER)");
        try
        {
            await using (var db = await Db2Session.ConnectAsync(dsn))
            {
                await using (var transaction = await db.BeginTransactionAsync())
                {
                    await transaction.ExecuteAsync($"INSERT INTO {table} VALUES (1)");
                }
                Assert.True(db.IsClosed, "An unfinished transaction scope must discard its session.");
                await Assert.ThrowsAsync<Db2ConnectionClosedException>(() => db.QueryAsync("VALUES 1"));
            }

            await using var verifier = await Db2Session.ConnectAsync(dsn);
            Assert.Equal(0, Assert.IsType<int>(Assert.Single(Assert.Single((await verifier.QueryAsync($"SELECT COUNT(*) FROM {table}")).Rows))));
        }
        finally
        {
            await setup.ExecuteAsync($"DROP TABLE {schema}.{table}");
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LivePoolConcurrentCloseCallersWaitForShutdownAndRemainIdempotent()
    {
        var pool = new Db2Pool(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!, new Db2PoolOptions(1));
        Assert.Equal(1, Assert.IsType<int>(Assert.Single(Assert.Single((await pool.QueryAsync("VALUES 1")).Rows))));
        Assert.Equal(1, pool.IdleSessionCount);

        var closeTasks = Enumerable.Range(0, 8).Select(_ => pool.DisposeAsync().AsTask()).ToArray();
        await Task.WhenAll(closeTasks);

        Assert.True(pool.IsClosed);
        Assert.Equal(0, pool.SessionCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.QueryAsync("VALUES 2"));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LivePoolReturnsSessionAfterServerSqlError()
    {
        await using var pool = new Db2Pool(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!, new Db2PoolOptions(1));
        var error = await Assert.ThrowsAsync<Db2SqlException>(() => pool.QueryAsync("SELECT FROM SYSIBM.SYSDUMMY1"));
        Assert.NotNull(error.SqlState);
        Assert.Contains("SQLCODE=", error.Message);
        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(1, pool.IdleSessionCount);

        var result = await pool.QueryAsync("VALUES 91");
        Assert.Equal(91, Assert.IsType<int>(Assert.Single(Assert.Single(result.Rows))));
        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(1, pool.IdleSessionCount);
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveProcedureReturnsOutAndInOutParameters()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var procedure = $"CS_OUT_{Guid.NewGuid():N}"[..24].ToUpperInvariant();
        await using var db = await Db2Session.ConnectAsync(dsn);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var created = false;
        try
        {
            await db.ExecuteAsync($$"""
                CREATE OR REPLACE PROCEDURE {{schema}}.{{procedure}} (
                    IN a INT, IN b INT, OUT sum_val INT, OUT prod_val INT, INOUT msg VARCHAR(100)
                )
                LANGUAGE SQL
                BEGIN
                    SET sum_val = a + b;
                    SET prod_val = a * b;
                    SET msg = 'OK: ' || msg;
                END
                """);
            created = true;
            var statement = await db.PrepareAsync($"CALL {procedure}(?, ?, ?, ?, ?)");
            var result = await statement.ExecuteWithOutputsAsync([
                new Db2Parameter(15),
                new Db2Parameter(4),
                new Db2Parameter(0, Db2ParameterDirection.Output),
                new Db2Parameter(0, Db2ParameterDirection.Output),
                new Db2Parameter("Alpha", Db2ParameterDirection.InputOutput),
            ]);
            Assert.Equal(0, result.RowsAffected);
            Assert.Null(result.OutputParameters[0]);
            Assert.Null(result.OutputParameters[1]);
            Assert.Equal(19, result.OutputParameters[2]);
            Assert.Equal(60, result.OutputParameters[3]);
            Assert.Equal("OK: Alpha", result.OutputParameters[4]);
        }
        finally
        {
            if (created) await db.ExecuteAsync($"DROP PROCEDURE {schema}.{procedure}");
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveInvalidInputOutputValueIsRejectedBeforeTheServerSeesIt()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var procedure = $"CS_INOUT_RANGE_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var db = await Db2Session.ConnectAsync(dsn);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var created = false;
        try
        {
            await db.ExecuteAsync($$"""
                CREATE PROCEDURE {{schema}}.{{procedure}} (IN a INT, INOUT b SMALLINT, OUT c INT)
                LANGUAGE SQL
                BEGIN
                    SET c = a + b;
                    SET b = b + 1;
                END
                """);
            created = true;
            var statement = await db.PrepareAsync($"CALL {procedure}(?, ?, ?)");
            await Assert.ThrowsAsync<OverflowException>(() => statement.ExecuteWithOutputsAsync([
                new Db2Parameter(1),
                new Db2Parameter(32768, Db2ParameterDirection.InputOutput),
                new Db2Parameter(0, Db2ParameterDirection.Output),
            ]));
            // The encoder rejects the value before any SQLDTA is written, so the
            // session must remain usable rather than desynchronized.
            Assert.Equal(7, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 7")).Rows))));
        }
        finally
        {
            if (created) await db.ExecuteAsync($"DROP PROCEDURE {schema}.{procedure}");
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveProcedureErrorAfterOutputAssignmentKeepsTheSessionUsable()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var procedure = $"CS_OUT_FAIL_{Guid.NewGuid():N}"[..26].ToUpperInvariant();
        await using var db = await Db2Session.ConnectAsync(dsn);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var created = false;
        try
        {
            await db.ExecuteAsync($$"""
                CREATE PROCEDURE {{schema}}.{{procedure}} (OUT a INT, OUT b VARCHAR(20))
                LANGUAGE SQL
                BEGIN
                    SET a = 11;
                    SET b = 'set-before-failure';
                    SIGNAL SQLSTATE '75001' SET MESSAGE_TEXT = 'invalid output value';
                END
                """);
            created = true;
            var statement = await db.PrepareAsync($"CALL {procedure}(?, ?)");
            var error = await Assert.ThrowsAnyAsync<Exception>(() => statement.ExecuteWithOutputsAsync([
                new Db2Parameter(0, Db2ParameterDirection.Output),
                new Db2Parameter("", Db2ParameterDirection.Output),
            ]));
            Assert.Contains("SQLCODE", error.Message);
            Assert.Contains("75001", error.Message);
            Assert.Equal(7, Assert.IsType<int>(Assert.Single(Assert.Single((await db.QueryAsync("VALUES 7")).Rows))));
        }
        finally
        {
            if (created) await db.ExecuteAsync($"DROP PROCEDURE {schema}.{procedure}");
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveTypedProcedureOutParametersPreserveDb2Values()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var procedure = $"CS_TYPED_OUT_{Guid.NewGuid():N}"[..27].ToUpperInvariant();
        await using var db = await Db2Session.ConnectAsync(dsn);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var created = false;
        Db2Statement? statement = null;
        var binary = new byte[] { 0, 0x7f, 0x80, 0xff };
        try
        {
            await db.ExecuteAsync($$"""
                CREATE PROCEDURE {{schema}}.{{procedure}} (
                    OUT small_value SMALLINT,
                    OUT decimal_value DECIMAL(9,3),
                    OUT decfloat_value DECFLOAT(16),
                    OUT date_value DATE,
                    OUT timestamp_value TIMESTAMP,
                    OUT text_value VARCHAR(30),
                    OUT varbinary_value VARBINARY(10),
                    OUT blob_value BLOB(100),
                    OUT clob_value CLOB(100),
                    OUT dbclob_value DBCLOB(100),
                    OUT xml_value XML,
                    OUT boolean_value BOOLEAN
                )
                LANGUAGE SQL
                BEGIN
                    SET small_value = -32768;
                    SET decimal_value = 42.125;
                    SET decfloat_value = DECFLOAT('Infinity');
                    SET date_value = DATE('2026-08-26');
                    SET timestamp_value = TIMESTAMP('2026-08-26 12:34:56.123456');
                    SET text_value = 'Zażółć 東京';
                    SET varbinary_value = CAST(X'007F80FF' AS VARBINARY(10));
                    SET blob_value = BLOB(X'007F80FF');
                    SET clob_value = 'CLOB café';
                    SET dbclob_value = CAST('DBCLOB 東京' AS DBCLOB(100));
                    SET xml_value = XMLPARSE(DOCUMENT '<root/>');
                    SET boolean_value = TRUE;
                END
                """);
            created = true;

            statement = await db.PrepareAsync($"CALL {procedure}({string.Join(", ", Enumerable.Repeat("?", 12))})");
            var result = await statement.ExecuteWithOutputsAsync([
                new Db2Parameter((short)0, Db2ParameterDirection.Output),
                new Db2Parameter(0m, Db2ParameterDirection.Output),
                new Db2Parameter("0", Db2ParameterDirection.Output),
                new Db2Parameter(new DateOnly(2000, 1, 1), Db2ParameterDirection.Output),
                new Db2Parameter("2000-01-01 00:00:00", Db2ParameterDirection.Output),
                new Db2Parameter("", Db2ParameterDirection.Output),
                new Db2Parameter(Array.Empty<byte>(), Db2ParameterDirection.Output),
                new Db2Parameter(Array.Empty<byte>(), Db2ParameterDirection.Output),
                new Db2Parameter("", Db2ParameterDirection.Output),
                new Db2Parameter("", Db2ParameterDirection.Output),
                new Db2Parameter("<empty/>", Db2ParameterDirection.Output),
                new Db2Parameter(false, Db2ParameterDirection.Output),
            ]);

            Assert.Equal((short)-32768, result.OutputParameters[0]);
            Assert.Equal("42.125", result.OutputParameters[1]);
            Assert.Equal("Infinity", result.OutputParameters[2]);
            Assert.Equal("2026-08-26", result.OutputParameters[3]);
            Assert.Equal("2026-08-26-12.34.56.123456", result.OutputParameters[4]);
            Assert.Equal("Zażółć 東京", result.OutputParameters[5]);
            Assert.Equal(binary, Assert.IsType<byte[]>(result.OutputParameters[6]));
            Assert.Equal(binary, Assert.IsType<byte[]>(result.OutputParameters[7]));
            Assert.Equal("CLOB café", result.OutputParameters[8]);
            Assert.Equal("DBCLOB 東京", result.OutputParameters[9]);
            Assert.Equal("<root/>", result.OutputParameters[10]);
            Assert.Equal(true, result.OutputParameters[11]);
        }
        finally
        {
            statement?.Close();
            if (created) await db.ExecuteAsync($"DROP PROCEDURE {schema}.{procedure}");
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveTypedProcedureOutNullsPreserveDb2Nulls()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var procedure = $"CS_NULL_OUT_{Guid.NewGuid():N}"[..27].ToUpperInvariant();
        await using var db = await Db2Session.ConnectAsync(dsn);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var created = false;
        Db2Statement? statement = null;
        try
        {
            await db.ExecuteAsync($$"""
                CREATE PROCEDURE {{schema}}.{{procedure}} (
                    OUT small_value SMALLINT,
                    OUT decimal_value DECIMAL(9,3),
                    OUT decfloat_value DECFLOAT(16),
                    OUT date_value DATE,
                    OUT timestamp_value TIMESTAMP,
                    OUT text_value VARCHAR(30),
                    OUT varbinary_value VARBINARY(10),
                    OUT blob_value BLOB(100),
                    OUT clob_value CLOB(100),
                    OUT dbclob_value DBCLOB(100),
                    OUT xml_value XML,
                    OUT boolean_value BOOLEAN
                )
                LANGUAGE SQL
                BEGIN
                    SET small_value = NULL;
                    SET decimal_value = NULL;
                    SET decfloat_value = NULL;
                    SET date_value = NULL;
                    SET timestamp_value = NULL;
                    SET text_value = NULL;
                    SET varbinary_value = NULL;
                    SET blob_value = NULL;
                    SET clob_value = NULL;
                    SET dbclob_value = NULL;
                    SET xml_value = NULL;
                    SET boolean_value = NULL;
                END
                """);
            created = true;

            statement = await db.PrepareAsync($"CALL {procedure}({string.Join(", ", Enumerable.Repeat("?", 12))})");
            var result = await statement.ExecuteWithOutputsAsync([
                new Db2Parameter((short)0, Db2ParameterDirection.Output),
                new Db2Parameter(0m, Db2ParameterDirection.Output),
                new Db2Parameter("0", Db2ParameterDirection.Output),
                new Db2Parameter(new DateOnly(2000, 1, 1), Db2ParameterDirection.Output),
                new Db2Parameter("2000-01-01 00:00:00", Db2ParameterDirection.Output),
                new Db2Parameter("", Db2ParameterDirection.Output),
                new Db2Parameter(Array.Empty<byte>(), Db2ParameterDirection.Output),
                new Db2Parameter(Array.Empty<byte>(), Db2ParameterDirection.Output),
                new Db2Parameter("", Db2ParameterDirection.Output),
                new Db2Parameter("", Db2ParameterDirection.Output),
                new Db2Parameter("<empty/>", Db2ParameterDirection.Output),
                new Db2Parameter(false, Db2ParameterDirection.Output),
            ]);

            Assert.Equal(12, result.OutputParameters.Count);
            foreach (var value in result.OutputParameters)
                Assert.Null(value);
        }
        finally
        {
            statement?.Close();
            if (created) await db.ExecuteAsync($"DROP PROCEDURE {schema}.{procedure}");
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task LiveProcedureOutLobWithResultCursorRoutesExtdtaToOwner()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        var procedure = $"CS_OUTLOB_{Guid.NewGuid():N}"[..27].ToUpperInvariant();
        await using var db = await Db2Session.ConnectAsync(dsn);
        var schema = await LiveFixtureSchema.ForSessionAsync(db);
        var created = false;
        Db2Statement? statement = null;
        var expectedBlob = new byte[] { 0, 0x7f, 0x80, 0xff };
        try
        {
            await db.ExecuteAsync($$"""
                CREATE PROCEDURE {{schema}}.{{procedure}} (
                    OUT out_blob BLOB(100),
                    OUT out_text VARCHAR(20)
                )
                LANGUAGE SQL DYNAMIC RESULT SETS 1 BEGIN
                    DECLARE C CURSOR WITH RETURN TO CALLER FOR SELECT 42 AS V FROM SYSIBM.SYSDUMMY1;
                    SET out_blob = BLOB(X'007F80FF');
                    SET out_text = 'hello-世界';
                    OPEN C;
                END
                """);
            created = true;
            statement = await db.PrepareAsync($"CALL {procedure}(?, ?)");
            var result = await statement.ExecuteWithOutputsAsync([
                new Db2Parameter(Array.Empty<byte>(), Db2ParameterDirection.Output),
                new Db2Parameter("", Db2ParameterDirection.Output),
            ]);
            Assert.Equal(expectedBlob, Assert.IsType<byte[]>(result.OutputParameters[0]));
            Assert.Equal("hello-世界", result.OutputParameters[1]);
            var resultSet = Assert.Single(result.ResultSets);
            Assert.Equal(42, Assert.IsType<int>(Assert.Single(Assert.Single(resultSet.Rows))));
            var reuse = await db.QueryAsync("VALUES 91");
            Assert.Equal(91, Assert.IsType<int>(Assert.Single(Assert.Single(reuse.Rows))));
        }
        finally
        {
            statement?.Close();
            if (created)
            {
                try { await db.ExecuteAsync($"DROP PROCEDURE {schema}.{procedure}"); }
                catch (InvalidOperationException) when (db.IsClosed)
                {
                    await using var cleanup = await Db2Session.ConnectAsync(dsn);
                    await cleanup.ExecuteAsync($"DROP PROCEDURE {schema}.{procedure}");
                }
            }
        }
    }

    private sealed class PatternReadStream(int length) : Stream
    {
        private int _position;
        public int BytesRead => _position;
        public int MaxRequestedRead { get; private set; }
        public bool IsDisposed { get; private set; }
        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            MaxRequestedRead = Math.Max(MaxRequestedRead, buffer.Length);
            var count = Math.Min(buffer.Length, length - _position);
            for (var index = 0; index < count; index++) buffer[index] = (byte)(((_position + index) * 37 + 11) % 251);
            _position += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
