// Go2Parity: C# port of go-db2/benchmarks/suite/{conformance,perf,report}.go.
// Baseline in the report is go-db2; this harness emits the csharp column.
// JSON-lines contract shared with ts-db2/benchmarks/go2-parity.cjs and
// rust-db2/examples/go2_parity.rs; aggregated by run-db2-go2-benchmark.cjs.
//
// Usage: DB2_TEST_DSN=... dotnet run --project benchmarks/Go2Parity \
//   -- [--warmups 3] [--samples 7] [--fast] [--seed-only] [--no-seed]
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JustyBase.Db2;

var options = Go2Options.Parse(args);
var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")
    ?? throw new InvalidOperationException("Set DB2_TEST_DSN");
await using var session = await Db2Session.ConnectAsync(dsn);

if (!options.NoSeed) await SeedAsync(session);
if (options.SeedOnly) return 0;

Emit(new Dictionary<string, object?> { ["language"] = "csharp", ["suite"] = "hello", ["dsn"] = "REDACTED" });

// ---- 1. Semantic conformance (8 scenarios, same SQL as conformance.go) ----
var conformance = new List<(string Name, string Output, bool Passed, string? Diff)>
{
    await ConformanceRowAsync(session, "Basic Scalar (Int, String, Float)",
        "SELECT 42, 'Pure Go Db2 Driver', 123.45 FROM SYSIBM.SYSDUMMY1",
        row => $"i={ToInt(row[0])}, s=\"{row[1]?.ToString()}\", f={ToDouble(row[2]):0.00}"),
    await ConformanceRowAsync(session, "Null Handling (sql.Null*)",
        "SELECT CAST(NULL AS INTEGER), CAST(NULL AS VARCHAR(50)) FROM SYSIBM.SYSDUMMY1",
        row => $"ni.Valid={(row[0] is not null).ToString().ToLowerInvariant()}, ns.Valid={(row[1] is not null).ToString().ToLowerInvariant()}"),
    await ConformanceRowAsync(session, "Temporal (Current Date Format)",
        "SELECT VARCHAR_FORMAT(CURRENT DATE, 'YYYY-MM-DD') FROM SYSIBM.SYSDUMMY1",
        row => $"{row[0]?.ToString()?.Trim()}"),
    await ConformanceRowAsync(session, "Numeric Precision (Smallint, Int, Bigint)",
        "SELECT C_SMALLINT, C_INT, C_BIGINT FROM BENCH_CONF_TYPES WHERE ID = 1",
        row => $"s={ToInt(row[0])}, i={ToInt(row[1])}, b={ToInt(row[2])}"),
    await ConformanceRowAsync(session, "High Precision Decimal & DECFLOAT",
        "SELECT VARCHAR(C_DECIMAL), VARCHAR(C_DECFLOAT) FROM BENCH_CONF_TYPES WHERE ID = 1",
        row => $"dec={row[0]?.ToString()?.Trim()}, decfloat={row[1]?.ToString()?.Trim()}"),
    await ConformanceBlobAsync(session),
    await ConformanceRowAsync(session, "CLOB Text Streaming",
        "SELECT LENGTH(C_CLOB), SUBSTR(C_CLOB, 1, 30) FROM BENCH_CONF_TYPES WHERE ID = 1",
        row => $"len={ToInt(row[0])}, prefix={row[1]?.ToString()}"),
    await ConformanceRowAsync(session, "VARGRAPHIC International Strings",
        "SELECT C_VARGRAPHIC FROM BENCH_CONF_TYPES WHERE ID = 1",
        row => $"{row[0]?.ToString()?.Trim()}"),
};

foreach (var (name, output, passed, diff) in conformance)
    Emit(new Dictionary<string, object?> { ["language"] = "csharp", ["suite"] = "conformance", ["scenario"] = name, ["output"] = output, ["passed"] = passed, ["diff"] = diff });

// ---- 2. Performance (5 scenarios, same iterations as perf.go) ----
await PerfAsync(session, "Ping (Round-trip Latency)", options.PingIters, options, s => s.PingAsync());
await PerfAsync(session, "Simple Single-Row SELECT", options.SimpleIters, options, async s =>
{
    var r = await s.QueryAsync("SELECT 1 FROM SYSIBM.SYSDUMMY1");
    if (ToInt(r.Rows[0][0]) != 1) throw new InvalidOperationException("expected 1");
});
await PerfAsync(session, "Fetch 1,000 Rows Scan", options.FetchIters, options, async s =>
{
    var r = await s.QueryAsync("SELECT ID, NAME, VAL FROM BENCH_PERF_ROWS");
    var n = 0;
    foreach (var row in r.Rows) { _ = ToInt(row[0]); _ = row[1]?.ToString(); _ = ToDouble(row[2]); n++; }
    if (n != 1000) throw new InvalidOperationException($"expected 1000 rows, got {n}");
});
await PerfAsync(session, "Fetch 100,000 Rows Scan", options.Fetch100kIters, options, async s =>
{
    var r = await s.QueryAsync("SELECT ID, NAME, VAL FROM BENCH_PERF_ROWS_100K");
    var n = 0;
    foreach (var row in r.Rows) { _ = ToInt(row[0]); _ = row[1]?.ToString(); _ = ToDouble(row[2]); n++; }
    if (n != 100000) throw new InvalidOperationException($"expected 100000 rows, got {n}");
});
await PerfAsync(session, "Prepared Statement INSERT", options.InsertIters, options, async s =>
{
    await s.ExecuteAsync("DELETE FROM BENCH_PERF_INSERT");
    var stmt = await s.PrepareAsync("INSERT INTO BENCH_PERF_INSERT (ID, NAME, VAL) VALUES (?, ?, ?)");
    for (var i = 1; i <= 10; i++)
        await s.ExecuteAsync(stmt, new object?[] { i, "test_name", (double)i * 2.5 });
});
await PerfAsync(session, "Read 500KB BLOB Payload", options.BlobIters, options, async s =>
{
    var r = await s.QueryAsync("SELECT DATA FROM BENCH_PERF_LOB WHERE ID = 1");
    _ = (byte[])r.Rows[0][0]!;
});

return conformance.All(c => c.Passed) ? 0 : 1;

// ---- helpers ----
static async Task SeedAsync(Db2Session s)
{
    foreach (var ddl in new[] { "DROP TABLE BENCH_CONF_TYPES", "DROP TABLE BENCH_PERF_ROWS", "DROP TABLE BENCH_PERF_ROWS_100K", "DROP TABLE BENCH_PERF_INSERT", "DROP TABLE BENCH_PERF_LOB" })
        try { await s.ExecuteAsync(ddl); } catch { }
    await s.ExecuteAsync(Go2Sql.CreateConfTypes);
    await s.ExecuteAsync("CREATE TABLE BENCH_PERF_ROWS (ID INT NOT NULL PRIMARY KEY, NAME VARCHAR(50), VAL DOUBLE)");
    await s.ExecuteAsync("CREATE TABLE BENCH_PERF_ROWS_100K (ID INT NOT NULL PRIMARY KEY, NAME VARCHAR(50), VAL DOUBLE)");
    await s.ExecuteAsync("CREATE TABLE BENCH_PERF_INSERT (ID INT NOT NULL PRIMARY KEY, NAME VARCHAR(50), VAL DOUBLE)");
    await s.ExecuteAsync("CREATE TABLE BENCH_PERF_LOB (ID INT NOT NULL PRIMARY KEY, DATA BLOB(1M))");
    await s.ExecuteAsync(Go2Sql.InsertConfTypes, new object?[] { Go2Sql.BlobSeed(), Go2Sql.ClobSeed });
    var stmt = await s.PrepareAsync("INSERT INTO BENCH_PERF_ROWS (ID, NAME, VAL) VALUES (?, ?, ?)");
    for (var i = 1; i <= 1000; i++)
        await s.ExecuteAsync(stmt, new object?[] { i, $"Customer-Record-{i:00000}", (double)i * 1.5 });
    // 100k rows via recursive CTE (server-side, ~0.6s); cross-join ROW_NUMBER takes ~2min.
    await s.ExecuteAsync(@"INSERT INTO BENCH_PERF_ROWS_100K (ID, NAME, VAL)
        WITH n(i) AS (VALUES (1) UNION ALL SELECT i+1 FROM n WHERE i < 100000)
        SELECT i, 'Customer-Record-' || CHAR(i), DOUBLE(i)*1.5 FROM n");
    await s.ExecuteAsync("INSERT INTO BENCH_PERF_LOB (ID, DATA) VALUES (1, ?)", new object?[] { Go2Sql.PerfBlob() });
}

static async Task<(string Name, string Output, bool Passed, string? Diff)> ConformanceRowAsync(Db2Session s, string name, string sql, Func<object?[], string> fmt)
{
    try
    {
        var r = await s.QueryAsync(sql);
        var output = fmt(r.Rows[0]);
        return (name, output, true, null);
    }
    catch (Exception ex) { return (name, $"ERR: {ex.GetType().Name}", false, ex.Message); }
}

static async Task<(string Name, string Output, bool Passed, string? Diff)> ConformanceBlobAsync(Db2Session s)
{
    const string name = "BLOB Binary Integrity (SHA-256)";
    try
    {
        var expected = Convert.ToHexString(SHA256.HashData(Go2Sql.BlobSeed())).ToLowerInvariant();
        var r = await s.QueryAsync("SELECT C_BLOB FROM BENCH_CONF_TYPES WHERE ID = 1");
        var bytes = (byte[])r.Rows[0][0]!;
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return actual == expected
            ? (name, actual, true, null)
            : (name, actual, false, $"Expected hash {expected}, got {actual}");
    }
    catch (Exception ex) { return (name, $"ERR: {ex.GetType().Name}", false, ex.Message); }
}

static async Task PerfAsync(Db2Session s, string scenario, int iters, Go2Options o, Func<Db2Session, Task> fn)
{
    for (var w = 0; w < o.Warmups; w++) await fn(s);
    var samples = new List<(double Ms, long Alloc)>();
    for (var i = 0; i < o.Samples; i++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        for (var k = 0; k < iters; k++) await fn(s);
        sw.Stop();
        var alloc = GC.GetTotalAllocatedBytes(true) - before;
        var perOpMs = sw.Elapsed.TotalMilliseconds / iters;
        samples.Add((perOpMs, alloc / iters));
        Emit(new Dictionary<string, object?> { ["language"] = "csharp", ["suite"] = "perf", ["scenario"] = scenario, ["mode"] = "native-materialized", ["warmup"] = false, ["iteration"] = i + 1, ["ops"] = iters, ["elapsedMs"] = perOpMs, ["allocatedBytes"] = alloc / iters });
    }
    var ordered = samples.OrderBy(v => v.Ms).ToList();
    var peak = Process.GetCurrentProcess().PeakWorkingSet64;
    Emit(new Dictionary<string, object?> { ["language"] = "csharp", ["suite"] = "perf", ["scenario"] = scenario, ["mode"] = "native-materialized", ["summary"] = true, ["ops"] = iters, ["medianMs"] = Round(ordered[ordered.Count / 2].Ms), ["minMs"] = Round(ordered.Min(v => v.Ms)), ["maxMs"] = Round(ordered.Max(v => v.Ms)), ["medianAllocatedBytes"] = ordered.Select(v => v.Alloc).OrderBy(v => v).ElementAt(ordered.Count / 2), ["peakRssBytes"] = peak });
}

static void Emit(Dictionary<string, object?> payload) =>
    Console.WriteLine(JsonSerializer.Serialize(payload));

static long ToInt(object? v) => v switch
{
    null => 0, short x => x, int x => x, long x => x, decimal x => (long)x,
    double x => (long)x, string x => long.Parse(x.Trim()), _ => Convert.ToInt64(v),
};
static double ToDouble(object? v) => v switch
{
    null => 0, double x => x, float x => x, decimal x => (double)x,
    short x => x, int x => x, long x => x, string x => double.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture), _ => Convert.ToDouble(v),
};
static double Round(double v) => Math.Round(v, 6);

sealed record Go2Options(int Warmups = 3, int Samples = 7, bool Fast = false, bool SeedOnly = false, bool NoSeed = false)
{
    public static Go2Options Parse(string[] args)
    {
        var o = new Go2Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--warmups": o = o with { Warmups = int.Parse(args[++i]) }; break;
                case "--samples": o = o with { Samples = int.Parse(args[++i]) }; break;
                case "--fast": o = o with { Fast = true }; break;
                case "--seed-only": o = o with { SeedOnly = true }; break;
                case "--no-seed": o = o with { NoSeed = true }; break;
            }
        }
        if (o.Fast) o = o with { Warmups = Math.Min(o.Warmups, 1), Samples = Math.Min(o.Samples, 3) };
        return o;
    }

    public int PingIters => Fast ? 5 : 500;
    public int SimpleIters => Fast ? 5 : 1000;
    public int FetchIters => Fast ? 2 : 50;
    public int Fetch100kIters => Fast ? 2 : 10;
    public int InsertIters => Fast ? 3 : 300;
    public int BlobIters => Fast ? 2 : 50;
}

static class Go2Sql
{
    public const string CreateConfTypes = @"
        CREATE TABLE BENCH_CONF_TYPES (
            ID INT NOT NULL PRIMARY KEY,
            C_SMALLINT SMALLINT,
            C_INT INTEGER,
            C_BIGINT BIGINT,
            C_REAL REAL,
            C_DOUBLE DOUBLE,
            C_DECIMAL DECIMAL(15,2),
            C_VARCHAR VARCHAR(100),
            C_DECFLOAT DECFLOAT(34),
            C_BLOB BLOB(64K),
            C_CLOB CLOB(64K),
            C_VARGRAPHIC VARGRAPHIC(100)
        )";
    public const string InsertConfTypes = @"
        INSERT INTO BENCH_CONF_TYPES (
            ID, C_SMALLINT, C_INT, C_BIGINT, C_REAL, C_DOUBLE, C_DECIMAL,
            C_VARCHAR, C_DECFLOAT, C_BLOB, C_CLOB, C_VARGRAPHIC
        ) VALUES (
            1, 32767, 2147483647, 9223372036854775807, 3.1415, 2.718281828459, 1234567.89,
            'Universal Db2 Conformance Suite', DECFLOAT('12345678901234567890.12345678901234', 34),
            ?, ?, 'Unicode / 日本語 / Português'
        )";
    public static string ClobSeed => string.Concat(Enumerable.Repeat("Pure-Go-Db2-CLOB-Test-Payload-", 40));
    public static byte[] BlobSeed()
    {
        var chunk = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var buf = new byte[1024];
        for (var i = 0; i < 256; i++) Buffer.BlockCopy(chunk, 0, buf, i * 4, 4);
        return buf;
    }
    public static byte[] PerfBlob()
    {
        var unit = Encoding.ASCII.GetBytes("PureGoDb2DriverBenchmarkPayload1234567890!");
        var buf = new byte[unit.Length * 12000];
        for (var i = 0; i < 12000; i++) Buffer.BlockCopy(unit, 0, buf, i * unit.Length, unit.Length);
        return buf;
    }
}
