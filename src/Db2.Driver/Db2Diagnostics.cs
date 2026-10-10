using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace JustyBase.Db2;

/// <summary>Metadata-only telemetry; exporters are owned by the application.</summary>
public static class Db2Diagnostics
{
    public static ActivitySource Activities { get; } = new("JustyBase.Db2");
    public static Meter Meter { get; } = new("JustyBase.Db2");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("db2.operation.duration", "ms");
    private static readonly Counter<long> Discards = Meter.CreateCounter<long>("db2.pool.discarded");
    internal static IDisposable? Begin(string operation)
    {
        try { return Activities.StartActivity(operation, ActivityKind.Client) is { } activity ? new SafeActivity(activity) : null; }
        catch { return null; }
    }
    private sealed class SafeActivity(Activity activity) : IDisposable
    {
        public void Dispose() { try { activity.Dispose(); } catch { } }
    }
    internal static void Record(string operation, long started, bool success)
    {
        try {
            if (Activity.Current is { } activity && activity.Source == Activities && activity.OperationName == operation)
                activity.SetStatus(success ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("success", success)); }
        catch { /* Observers must not change driver behavior. */ }
    }
    internal static void Discard() { try { Discards.Add(1); } catch { } }
}
