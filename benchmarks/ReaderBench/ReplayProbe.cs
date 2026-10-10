using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text.Json;
#if DB2_PERF_COUNTERS
using JustyBase.Db2.Protocol;
#endif

namespace ReaderBench;

/// <summary>Diagnostic samples and allocation-trace entry point; BDN remains the acceptance benchmark.</summary>
internal static class ReplayProbe
{
    internal static async Task RunAsync(string[] args)
    {
        if (args.Length is < 2 or > 4)
            throw new ArgumentException("--replay-probe Fixture Method [BlockSize=65535] [Samples=9]");
        var benchmark = new Db2ReplayBenchmarks
        {
            Fixture = args[0], BlockSize = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 65535
        };
        var samples = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 9;
        Func<Task> run = args[1] switch
        {
            "ReadAll_GetInt32" => async () => { await benchmark.ReadAll_GetInt32(); },
            "ReadAll_GetValue" => async () => { await benchmark.ReadAll_GetValue(); },
            "ReadFirstColumn" => async () => { await benchmark.ReadFirstColumn(); },
            "ReadTwoColumns" => async () => { await benchmark.ReadTwoColumns(); },
            "ReadAllColumns" => async () => { await benchmark.ReadAllColumns(); },
            "Read1_Dispose" => async () => { await benchmark.Read1_Dispose(); },
            "Read10_Dispose" => async () => { await benchmark.Read10_Dispose(); },
            "ExecuteScalar" => async () => { await benchmark.ExecuteScalar(); },
            "SingleRow" => async () => { await benchmark.SingleRow(); },
            "NativeStream_ReadAll" => async () => { await benchmark.NativeStream_ReadAll(); },
            "NativeStream_FirstColumnEquivalent" => async () => { await benchmark.NativeStream_FirstColumnEquivalent(); },
            "NativeStream_Read1_Dispose" => async () => { await benchmark.NativeStream_Read1_Dispose(); },
            _ => throw new ArgumentException("Unknown replay method.")
        };
        try
        {
            await benchmark.Setup();
            for (var warmup = 0; warmup < 3; warmup++) { await run(); benchmark.Check(); }
            Console.WriteLine("sample,elapsedMs,clientCpuMs,allocatedBytes,gen0,gen1,gen2,peerCounters");
            using var process = Process.GetCurrentProcess();
            ReplayProbeEvents.Log.MeasureStart();
            for (var sample = 0; sample < samples; sample++)
            {
                var before = benchmark.Counters();
#if DB2_PERF_COUNTERS
                Db2PerformanceCounters.Take();
#endif
                var gen0 = GC.CollectionCount(0); var gen1 = GC.CollectionCount(1); var gen2 = GC.CollectionCount(2);
                var cpu = process.TotalProcessorTime;
                var allocated = GC.GetTotalAllocatedBytes(precise: true);
                var start = Stopwatch.GetTimestamp();
                await run();
                var elapsed = Stopwatch.GetElapsedTime(start);
                var bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                var cpuUsed = process.TotalProcessorTime - cpu;
                var gc0 = GC.CollectionCount(0) - gen0; var gc1 = GC.CollectionCount(1) - gen1; var gc2 = GC.CollectionCount(2) - gen2;
                benchmark.Check();
                var after = benchmark.Counters();
                var delta = new Db2Replay.ReplayCounters(after.Queries - before.Queries, after.Fetches - before.Fetches,
                    after.BytesSent - before.BytesSent, after.DssPackets - before.DssPackets,
                    after.QrydtaPackets - before.QrydtaPackets, after.ContinuedPackets - before.ContinuedPackets,
                    after.Commits - before.Commits, after.ActiveQueries);
                Console.WriteLine(FormattableString.Invariant($"{sample},{elapsed.TotalMilliseconds:F6},{cpuUsed.TotalMilliseconds:F6},{bytes},{gc0},{gc1},{gc2},\"{JsonSerializer.Serialize(delta).Replace("\"", "\"\"")}\""));
#if DB2_PERF_COUNTERS
                Console.WriteLine("clientCounters=" + JsonSerializer.Serialize(Db2PerformanceCounters.Take()));
#endif
            }
            ReplayProbeEvents.Log.MeasureStop();
        }
        finally { await benchmark.Cleanup(); }
    }
}

[EventSource(Name = "JustyBase.Db2.ReplayProbe")]
internal sealed class ReplayProbeEvents : EventSource
{
    internal static readonly ReplayProbeEvents Log = new();
    [Event(1)] public void MeasureStart() => WriteEvent(1);
    [Event(2)] public void MeasureStop() => WriteEvent(2);
}
