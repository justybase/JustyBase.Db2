using BenchmarkDotNet.Running;

if (args.Length > 0 && args[0] == "--replay-probe")
{
    await ReaderBench.ReplayProbe.RunAsync(args[1..]);
    return;
}
#if DB2_PERF_COUNTERS
if (args.Length > 0 && args[0] == "--live-block-probe")
{
    await ReaderBench.LiveBlockProbe.RunAsync(args[1..]);
    return;
}
#endif
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
