using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

if (args.Length is < 1 or > 2 || args.Length == 2 && args[1] != "--measured-only")
    throw new ArgumentException("Usage: AllocationTrace <trace.nettrace> [--measured-only]");

var etlxPath = TraceLog.CreateFromEventPipeDataFile(args[0]);
using var trace = new TraceLog(etlxPath);
var measuredOnly = args.Length == 2;
var measuring = !measuredOnly;
var markerFound = false;
var allocations = new Dictionary<(string Type, string Stack), (long Bytes, int Samples)>();
var allocationEventNames = new HashSet<string>(StringComparer.Ordinal);
var events = 0;
foreach (var item in trace.Events)
{
    if (item.ProviderName == "JustyBase.Db2.ReplayProbe")
    {
        if (item.ID == (TraceEventID)1) { measuring = true; markerFound = true; }
        if (item.ID == (TraceEventID)2) measuring = false;
        continue;
    }
    if (!measuring) continue;
    if (item.EventName.Contains("Alloc", StringComparison.OrdinalIgnoreCase))
        allocationEventNames.Add($"{item.ProviderName}/{item.EventName}");
    if (!item.EventName.Contains("AllocationTick", StringComparison.Ordinal)) continue;
    var rawSize = item.PayloadByName("AllocationAmount64") ?? item.PayloadByName("AllocationAmount");
    if (rawSize is null)
    {
        Console.WriteLine($"Allocation event payloads: {string.Join(", ", item.PayloadNames)}");
        continue;
    }
    var size = Convert.ToInt64(rawSize, System.Globalization.CultureInfo.InvariantCulture);
    if (size <= 0) continue;

    var type = Convert.ToString(item.PayloadByName("TypeName"), System.Globalization.CultureInfo.InvariantCulture) ?? "<unknown type>";
    var stack = FormatStack(item.CallStack());
    var key = (type, stack);
    var prior = allocations.GetValueOrDefault(key);
    allocations[key] = (prior.Bytes + size, prior.Samples + 1);
    events++;
}

if (measuredOnly && !markerFound) throw new InvalidDataException("Trace contains no ReplayProbe measurement markers.");

Console.WriteLine($"Allocation-related events: {string.Join(", ", allocationEventNames.Order(StringComparer.Ordinal))}");
Console.WriteLine($"Allocation tick samples: {events}");
var sampledTotal = allocations.Values.Sum(value => value.Bytes);
Console.WriteLine($"Sampled allocation bytes: {sampledTotal}");
Console.WriteLine("| Allocated type | Sampled bytes | Sampled weight | Samples |");
Console.WriteLine("| --- | ---: | ---: | ---: |");
foreach (var group in allocations.GroupBy(item => item.Key.Type)
    .Select(group => new { Type = group.Key, Bytes = group.Sum(item => item.Value.Bytes), Samples = group.Sum(item => item.Value.Samples) })
    .OrderByDescending(group => group.Bytes).Take(20))
    Console.WriteLine(FormattableString.Invariant($"| `{group.Type}` | {group.Bytes} | {(double)group.Bytes / sampledTotal:P2} | {group.Samples} |"));
Console.WriteLine();
Console.WriteLine("| Rank | Sampled bytes | Samples | Allocated type | Top stack frames |");
Console.WriteLine("| ---: | ---: | ---: | --- | --- |");
var rank = 0;
foreach (var (key, value) in allocations.OrderByDescending(item => item.Value.Bytes).Take(20))
    Console.WriteLine($"| {++rank} | {value.Bytes:N0} | {value.Samples:N0} | `{key.Type}` | `{key.Stack}` |");

static string FormatStack(TraceCallStack? stack)
{
    if (stack is null) return "<no stack>";
    var frames = new List<string>(8);
    for (var depth = 0; stack is not null && depth < 8; depth++, stack = stack.Caller)
    {
        var name = stack.CodeAddress.FullMethodName;
        if (!string.IsNullOrWhiteSpace(name)) frames.Add(name);
    }
    return frames.Count == 0 ? "<no symbols>" : string.Join(" ← ", frames);
}
