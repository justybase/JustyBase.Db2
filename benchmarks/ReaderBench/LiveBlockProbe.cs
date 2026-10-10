#if DB2_PERF_COUNTERS
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Protocol;

namespace ReaderBench;

internal static class LiveBlockProbe
{
    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("--live-block-probe Int|Wide|Small|Lob BlockSize Samples");
        var size = int.Parse(args[1], CultureInfo.InvariantCulture);
        if (size is not (16384 or 32767 or 65535)) throw new ArgumentOutOfRangeException(nameof(size));
        var samples = int.Parse(args[2], CultureInfo.InvariantCulture);
        var (sql, expectedRows, columns) = args[0] switch
        {
            "Int" => ("SELECT ID FROM P1BENCH.NARROW_1M", 1_000_000, 1),
            "Wide" => ("SELECT ID, " + string.Join(",", Enumerable.Range(0, 39).Select(i =>
                i % 3 == 0 ? "CAST(NULL AS INTEGER)" : "A")) + " FROM P1BENCH.NARROW_100K", 100_000, 40),
            "Small" => ("SELECT 1 FROM SYSIBM.SYSDUMMY1", 1, 1),
            "Lob" => ("SELECT ID,C,B FROM P1BENCH.LOB_4K", 1_000, 3),
            _ => throw new ArgumentException("Unknown live fixture.")
        };
        var config = Db2Config.Parse(Environment.GetEnvironmentVariable("DB2_TEST_DSN")
            ?? throw new InvalidOperationException("DB2_TEST_DSN is required."));
        if (config.UseSsl) throw new NotSupportedException("This socket-read diagnostic requires a plaintext loopback Db2 endpoint.");
        var builder = new Db2AdoConnectionStringBuilder
        { Server = config.Host + ":" + config.Port, Database = config.Database, UserID = config.User, Password = config.Password, Pooling = false };
        builder["BlockSize"] = size;
        await using var connection = new Db2AdoConnection(builder.ConnectionString);
        await connection.OpenAsync();
        var warnings = new List<string>();
        connection.InfoMessage += (_, e) => warnings.AddRange(e.Errors.Select(error => error.SqlState));
        (int Rows, long Sum, int Columns) actual = default;
        async Task Run()
        {
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            var count = 0; long sum = 0;
            while (await reader.ReadAsync()) { count++; sum += reader.GetInt32(0); }
            actual = (count, sum, reader.FieldCount);
        }
        void Check()
        {
            if (actual != (expectedRows, (long)expectedRows * (expectedRows + 1) / 2, columns) || warnings.Count != 0)
                throw new InvalidDataException("Live block-size result completeness, metadata, checksum or SQLSTATE mismatch.");
        }
        for (var i = 0; i < 3; i++) { await Run(); Check(); }
        using var process = Process.GetCurrentProcess();
        for (var sample = 0; sample < samples; sample++)
        {
            Db2PerformanceCounters.Take();
            var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
            var cpu = process.TotalProcessorTime;
            var bytes = GC.GetTotalAllocatedBytes(true);
            var start = Stopwatch.GetTimestamp();
            await Run();
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var allocation = GC.GetTotalAllocatedBytes(true) - bytes;
            var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            var generations = new[] { GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2 };
            Check();
            Console.WriteLine(JsonSerializer.Serialize(new { Fixture = args[0], BlockSize = size, Sample = sample,
                Milliseconds = ms, ClientCpuMs = cpuMs, AllocatedBytes = allocation,
                RowsPerSecond = expectedRows * 1000 / ms, BytesPerRow = (double)allocation / expectedRows,
                Generations = generations, actual.Rows, actual.Sum, actual.Columns, SqlStates = warnings,
                Counters = Db2PerformanceCounters.Take() }));
        }
    }
}
#endif
