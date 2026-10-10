extern alias IBM;

using System.Buffers.Binary;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using IBMConnection = IBM::IBM.Data.Db2.DB2Connection;
using IBMCommand = IBM::IBM.Data.Db2.DB2Command;

var options = P1Options.Parse(args);
var manifest = P1Manifest.Load(options.ManifestPath);
var dataset = manifest.Datasets.FirstOrDefault(d => d.Name == options.Dataset)
    ?? throw new InvalidOperationException($"dataset {options.Dataset} is not in the manifest");

var config = Db2Config.Parse(options.Dsn);
await using var native = await Db2Session.ConnectAsync(config);
await using var adoNet = new Db2AdoConnection(options.Dsn);
await adoNet.OpenAsync();
using var ibm = new IBMConnection($"Server={config.Host}:{config.Port};Database={config.Database};UID={config.User};PWD={config.Password};Pooling=false;");
ibm.Open();

var modes = new List<(string Name, Func<bool, Task<Outcome>> Run)>
{
    ("native-materialized", NativeMaterializedAsync),
    ("native-stream", NativeStreamAsync),
    ("ado-reader", AdoReaderAsync),
    ("ibm-reader", IbmReaderAsync),
};
if (dataset.Name == "INT_1M") modes.Add(("ado-typed", AdoTypedAsync));
modes = modes.Where(m => options.Modes.Contains(m.Name)).ToList();
if (modes.Count == 0) throw new InvalidOperationException("no modes selected");

// Untimed verification pass: compute the canonical checksum once per mode and
// validate it. Timed passes skip hashing so the measurement reflects the driver.
var verified = new Dictionary<string, Outcome>(StringComparer.Ordinal);
foreach (var (name, run) in modes)
{
    var outcome = await run(true);
    Validate(name, outcome, dataset);
    verified[name] = outcome;
}

var samples = modes.ToDictionary(m => m.Name, _ => new List<Sample>(), StringComparer.Ordinal);
foreach (var (name, run) in modes)
    for (var warmup = 0; warmup < options.Warmups; warmup++) await run(false);

for (var iteration = 0; iteration < options.Samples; iteration++)
{
    for (var offset = 0; offset < modes.Count; offset++)
    {
        var (name, run) = modes[(iteration + offset) % modes.Count];
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var outcome = await run(false);
        if (outcome.Rows != dataset.Rows) throw new InvalidOperationException($"{name}: expected {dataset.Rows} rows, got {outcome.Rows}");
        samples[name].Add(new Sample(outcome.ElapsedMs, outcome.FirstRowMs, outcome.Rows, verified[name].Bytes, outcome.AllocatedBytes, verified[name].Checksum));
        Emit(new Dictionary<string, object?> { ["language"] = "csharp", ["dataset"] = dataset.Name, ["mode"] = name, ["warmup"] = false, ["iteration"] = iteration + 1, ["rows"] = outcome.Rows, ["bytes"] = verified[name].Bytes, ["checksum"] = Convert.ToHexString(verified[name].Checksum).ToLowerInvariant(), ["elapsedMs"] = outcome.ElapsedMs, ["firstRowMs"] = outcome.FirstRowMs, ["allocatedBytes"] = outcome.AllocatedBytes });
    }
}

var peakRss = Process.GetCurrentProcess().PeakWorkingSet64;
foreach (var (name, _) in modes)
{
    var values = samples[name];
    var median = values.OrderBy(v => v.ElapsedMs).ElementAt(values.Count / 2);
    Emit(new Dictionary<string, object?> { ["language"] = "csharp", ["dataset"] = dataset.Name, ["mode"] = name, ["summary"] = true, ["rows"] = median.Rows, ["bytes"] = verified[name].Bytes, ["checksum"] = Convert.ToHexString(verified[name].Checksum).ToLowerInvariant(), ["medianMs"] = Math.Round(median.ElapsedMs, 3), ["minMs"] = Math.Round(values.Min(v => v.ElapsedMs), 3), ["maxMs"] = Math.Round(values.Max(v => v.ElapsedMs), 3), ["medianFirstRowMs"] = median.FirstRowMs, ["medianAllocatedBytes"] = (long)values.Select(v => v.AllocatedBytes).OrderBy(v => v).ElementAt(values.Count / 2), ["peakRssBytes"] = peakRss });
}

try { await adoNet.CloseAsync(); } finally { Db2AdoConnection.ClearAllPools(); }
return 0;

async Task<Outcome> NativeMaterializedAsync(bool checksum)
{
    var sw = Stopwatch.StartNew();
    var before = GC.GetTotalAllocatedBytes(true);
    var result = await native.QueryAsync(dataset.Select);
    sw.Stop();
    var allocated = GC.GetTotalAllocatedBytes(true) - before;
    long bytes = 0;
    var digest = Array.Empty<byte>();
    if (checksum)
    {
        var hash = new CanonicalHash(dataset.Columns);
        foreach (var row in result.Rows) hash.Add(row);
        bytes = hash.Bytes;
        digest = hash.Finish();
    }
    return new Outcome(result.Rows.Count, bytes, digest, sw.Elapsed.TotalMilliseconds, null, allocated);
}

async Task<Outcome> NativeStreamAsync(bool checksum)
{
    var sw = Stopwatch.StartNew();
    var before = GC.GetTotalAllocatedBytes(true);
    var hash = checksum ? new CanonicalHash(dataset.Columns) : null;
    var rows = 0;
    double? firstRow = null;
    await foreach (var row in native.QueryStreamAsync(dataset.Select))
    {
        firstRow ??= sw.Elapsed.TotalMilliseconds;
        hash?.Add(row.Values);
        rows++;
    }
    sw.Stop();
    return new Outcome(rows, hash?.Bytes ?? 0, hash?.Finish() ?? Array.Empty<byte>(), sw.Elapsed.TotalMilliseconds, firstRow, GC.GetTotalAllocatedBytes(true) - before);
}

async Task<Outcome> AdoReaderAsync(bool checksum)
{
    await using var command = adoNet.CreateCommand();
    command.CommandText = dataset.Select;
    var sw = Stopwatch.StartNew();
    var before = GC.GetTotalAllocatedBytes(true);
    await using var reader = await command.ExecuteReaderAsync();
    var hash = checksum ? new CanonicalHash(dataset.Columns) : null;
    var rows = 0;
    double? firstRow = null;
    var values = new object?[reader.FieldCount];
    while (await reader.ReadAsync())
    {
        firstRow ??= sw.Elapsed.TotalMilliseconds;
        for (var i = 0; i < values.Length; i++) values[i] = reader.GetValue(i);
        hash?.Add(values);
        rows++;
    }
    sw.Stop();
    return new Outcome(rows, hash?.Bytes ?? 0, hash?.Finish() ?? Array.Empty<byte>(), sw.Elapsed.TotalMilliseconds, firstRow, GC.GetTotalAllocatedBytes(true) - before);
}

async Task<Outcome> AdoTypedAsync(bool checksum)
{
    await using var command = adoNet.CreateCommand();
    command.CommandText = dataset.Select;
    var sw = Stopwatch.StartNew();
    var before = GC.GetTotalAllocatedBytes(true);
    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
    var hash = checksum ? new CanonicalHash(dataset.Columns) : null;
    var rows = 0;
    double? firstRow = null;
    while (await reader.ReadAsync())
    {
        firstRow ??= sw.Elapsed.TotalMilliseconds;
        var value = reader.GetInt32(0);
        hash?.AddInt32(value);
        rows++;
    }
    sw.Stop();
    return new Outcome(rows, hash?.Bytes ?? 0, hash?.Finish() ?? Array.Empty<byte>(), sw.Elapsed.TotalMilliseconds, firstRow, GC.GetTotalAllocatedBytes(true) - before);
}

Task<Outcome> IbmReaderAsync(bool checksum)
{
    using var command = new IBMCommand(dataset.Select, ibm);
    var sw = Stopwatch.StartNew();
    var before = GC.GetTotalAllocatedBytes(true);
    using var reader = command.ExecuteReader();
    var hash = checksum ? new CanonicalHash(dataset.Columns) : null;
    var rows = 0;
    double? firstRow = null;
    var values = new object?[reader.FieldCount];
    while (reader.Read())
    {
        firstRow ??= sw.Elapsed.TotalMilliseconds;
        reader.GetValues(values);
        hash?.Add(values);
        rows++;
    }
    sw.Stop();
    return Task.FromResult(new Outcome(rows, hash?.Bytes ?? 0, hash?.Finish() ?? Array.Empty<byte>(), sw.Elapsed.TotalMilliseconds, firstRow, GC.GetTotalAllocatedBytes(true) - before));
}

void Validate(string mode, Outcome outcome, P1Dataset dataset)
{
    if (outcome.Rows != dataset.Rows)
        throw new InvalidOperationException($"{mode}: expected {dataset.Rows} rows, got {outcome.Rows}");
    var actual = Convert.ToHexString(outcome.Checksum).ToLowerInvariant();
    if (manifest.Checksum.Expected.TryGetValue(dataset.Name, out var expected) && expected != actual)
        throw new InvalidOperationException($"{mode}: checksum {actual} does not match manifest {expected}");
}

void Emit(Dictionary<string, object?> payload) => Console.WriteLine(JsonSerializer.Serialize(payload));

sealed record Outcome(long Rows, long Bytes, byte[] Checksum, double ElapsedMs, double? FirstRowMs, long AllocatedBytes);
sealed record Sample(double ElapsedMs, double? FirstRowMs, long Rows, long Bytes, long AllocatedBytes, byte[] Checksum);

sealed class CanonicalHash(IReadOnlyList<P1Column> columns)
{
    private readonly ulong[] _sums = new ulong[4];
    private long _count;
    public long Bytes { get; private set; }

    public void Add(IReadOnlyList<object?> row)
    {
        using var buffer = new MemoryStream();
        for (var index = 0; index < columns.Count; index++)
            Append(buffer, columns[index], index < row.Count ? row[index] : null);
        Bytes += buffer.Length;
        var digest = SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        for (var word = 0; word < 4; word++)
            _sums[word] = unchecked(_sums[word] + BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(word * 8, 8)));
        _count++;
    }

    public void AddInt32(int value)
    {
        Span<byte> input = stackalloc byte[9];
        input[0] = 0x02;
        BinaryPrimitives.WriteInt64BigEndian(input[1..], value);
        Bytes += input.Length;
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        for (var word = 0; word < 4; word++)
            _sums[word] = unchecked(_sums[word] + BinaryPrimitives.ReadUInt64LittleEndian(digest.Slice(word * 8, 8)));
        _count++;
    }

    private static void Append(MemoryStream buffer, P1Column column, object? value)
    {
        if (value is null || value is DBNull) { buffer.WriteByte(0x00); return; }
        switch (column.Type)
        {
            case "integer": buffer.WriteByte(0x02); WriteI64(buffer, Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case "decimal":
                buffer.WriteByte(0x03);
                WriteString(buffer, value is string text ? text : ((decimal)value).ToString("F" + column.Scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                break;
            case "double":
                buffer.WriteByte(0x04);
                var bits = value is double d ? d : Convert.ToDouble(value, CultureInfo.InvariantCulture);
                Span<byte> doubleBytes = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64BigEndian(doubleBytes, BitConverter.DoubleToUInt64Bits(bits));
                buffer.Write(doubleBytes);
                break;
            case "varchar": case "text": buffer.WriteByte(0x01); WriteString(buffer, ResolveText(value)); break;
            case "date": buffer.WriteByte(0x05); WriteString(buffer, value is DateTime date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""); break;
            case "timestamp": buffer.WriteByte(0x05); WriteString(buffer, value is DateTime stamp ? stamp.ToString("yyyy-MM-dd-HH.mm.ss.ffffff", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""); break;
            case "boolean":
                buffer.WriteByte(0x06);
                buffer.WriteByte(value is bool flag ? (byte)(flag ? 1 : 0) : (byte)(Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0 ? 1 : 0));
                break;
            case "binary":
                var bytes = ResolveBytes(value);
                buffer.WriteByte(0x07);
                WriteLength(buffer, bytes.Length);
                buffer.Write(bytes);
                break;
            default: throw new InvalidOperationException($"unknown canonical type {column.Type}");
        }
    }

    public byte[] Finish()
    {
        Span<byte> input = stackalloc byte[40];
        BinaryPrimitives.WriteUInt64BigEndian(input, (ulong)_count);
        for (var word = 0; word < 4; word++)
            BinaryPrimitives.WriteUInt64BigEndian(input.Slice(8 + word * 8, 8), _sums[word]);
        return SHA256.HashData(input);
    }

    private static void WriteI64(MemoryStream buffer, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        buffer.Write(bytes);
    }

    private static void WriteLength(MemoryStream buffer, int length)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)length);
        buffer.Write(bytes);
    }

    private static void WriteString(MemoryStream buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteLength(buffer, bytes.Length);
        buffer.Write(bytes);
    }

    private static byte[] ResolveBytes(object value) => value switch
    {
        byte[] array => array,
        ReadOnlyMemory<byte> memory => memory.ToArray(),
        Db2LobValue lob => ReadAll(lob.OpenReadStream()),
        _ => throw new InvalidOperationException($"cannot encode {value.GetType()} as binary"),
    };

    private static string ResolveText(object value) => value switch
    {
        string text => text,
        Db2LobValue { IsText: true } lob => ReadAllText(lob.OpenTextReader()),
        char[] chars => new string(chars),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    private static string ReadAllText(TextReader reader)
    {
        using (reader) return reader.ReadToEnd();
    }
}

sealed record P1Options(string Dataset, HashSet<string> Modes, int Warmups, int Samples, string ManifestPath, string Dsn, string? Out)
{
    public static P1Options Parse(string[] args)
    {
        string? dataset = null, modes = null, manifest = null, output = null;
        var warmups = 3;
        var samples = 7;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dataset": dataset = args[++index]; break;
                case "--modes": modes = args[++index]; break;
                case "--warmups": warmups = int.Parse(args[++index], CultureInfo.InvariantCulture); break;
                case "--samples": samples = int.Parse(args[++index], CultureInfo.InvariantCulture); break;
                case "--manifest": manifest = args[++index]; break;
                case "--out": output = args[++index]; break;
                default: throw new ArgumentException($"unknown argument {args[index]}");
            }
        }
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN") ?? throw new InvalidOperationException("Set DB2_TEST_DSN.");
        return new P1Options(
            dataset ?? throw new ArgumentException("--dataset is required"),
            new HashSet<string>((modes ?? "native-materialized,native-stream,ado-reader,ado-typed,ibm-reader").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal),
            warmups, samples,
            manifest ?? throw new ArgumentException("--manifest <dataset manifest.json> is required"),
            dsn, output);
    }
}

sealed class P1Manifest
{
    public List<P1Dataset> Datasets { get; init; } = [];
    public P1Checksum Checksum { get; init; } = new();
    public static P1Manifest Load(string path) =>
        JsonSerializer.Deserialize<P1Manifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
}

sealed class P1Checksum
{
    public Dictionary<string, string> Expected { get; init; } = new(StringComparer.Ordinal);
}

sealed class P1Dataset
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public long Rows { get; init; }
    public string Select { get; init; } = "";
    public List<P1Column> Columns { get; init; } = [];
}

sealed class P1Column
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public int Scale { get; init; }
}
