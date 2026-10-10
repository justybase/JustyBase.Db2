extern alias IBM;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using IBMConnection = IBM::IBM.Data.Db2.DB2Connection;
using IBMCommand = IBM::IBM.Data.Db2.DB2Command;

const string Sql = "SELECT a.COLNO, a.COLNAME, b.COLNO FROM SYSCAT.COLUMNS a CROSS JOIN SYSCAT.COLUMNS b FETCH FIRST 10000 ROWS ONLY";
const int ExpectedRows = 10_000;
const int Repetitions = 7;
var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN") ?? throw new InvalidOperationException("Set DB2_TEST_DSN.");
var config = Db2Config.Parse(dsn);
await using var native = await Db2Session.ConnectAsync(config);
await using var adoNet = new Db2AdoConnection(dsn);
await adoNet.OpenAsync();
using var ibm = new IBMConnection($"Server={config.Host}:{config.Port};Database={config.Database};UID={config.User};PWD={config.Password};Pooling=false;");
ibm.Open();

var modes = new (string Name, Func<Task<FetchResult>> Run)[]
{
    ("C# native materialized", FetchNativeMaterializedAsync),
    ("C# native row stream", FetchNativeStreamAsync),
    ("C# ADO.NET DbDataReader", FetchAdoNetStreamAsync),
    ("IBM DB2DataReader", FetchIbmAsync),
};

var baseline = await FetchIbmAsync();
ValidateResult("IBM baseline", baseline, ExpectedRows, baseline.Checksum);
foreach (var (name, run) in modes)
{
    var warmup = await run();
    ValidateResult(name + " warmup", warmup, baseline.Rows, baseline.Checksum);
}

var samples = modes.ToDictionary(mode => mode.Name, _ => new List<FetchSample>(), StringComparer.Ordinal);
for (var iteration = 0; iteration < Repetitions; iteration++)
{
    for (var offset = 0; offset < modes.Length; offset++)
    {
        var mode = modes[(iteration + offset) % modes.Length];
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        var result = await mode.Run();
        timer.Stop();
        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        ValidateResult(mode.Name, result, baseline.Rows, baseline.Checksum);
        samples[mode.Name].Add(new FetchSample(timer.Elapsed.TotalMilliseconds, allocated));
        Console.WriteLine($"{mode.Name}: iteration={iteration + 1}; rows={result.Rows}; checksum={Convert.ToHexString(result.Checksum)}; ms={timer.Elapsed.TotalMilliseconds:F2}; managed-allocated-MiB={allocated / 1024d / 1024d:F2}");
    }
}

Console.WriteLine();
Console.WriteLine($"All modes matched {baseline.Rows} rows and SHA-256 {Convert.ToHexString(baseline.Checksum)}.");
Console.WriteLine("Managed allocations are process-wide GC counters sampled around each isolated fetch; they include driver, row and checksum allocations, but exclude native IBM CLI memory.");
Console.WriteLine();
Console.WriteLine("| Fetch mode | Median ms | Median rows/s | Median managed MiB allocated | Median / IBM time |");
Console.WriteLine("| --- | ---: | ---: | ---: | ---: |");
foreach (var mode in modes)
{
    var values = samples[mode.Name];
    var median = values.OrderBy(value => value.ElapsedMilliseconds).ElementAt(values.Count / 2);
    var ibmMedianMs = samples["IBM DB2DataReader"].OrderBy(value => value.ElapsedMilliseconds).ElementAt(values.Count / 2).ElapsedMilliseconds;
    Console.WriteLine($"| {mode.Name} | {median.ElapsedMilliseconds:F2} | {ExpectedRows * 1_000d / median.ElapsedMilliseconds:F0} | {median.ManagedAllocatedBytes / 1024d / 1024d:F2} | {median.ElapsedMilliseconds / ibmMedianMs:F2}× |");
}

try { await adoNet.CloseAsync(); }
finally { Db2AdoConnection.ClearAllPools(); }

async Task<FetchResult> FetchNativeMaterializedAsync()
{
    var result = await native.QueryAsync(Sql);
    var hash = new RowMultisetHash();
    foreach (var row in result.Rows) AppendRow(hash, row);
    return new FetchResult(result.Rows.Count, hash.Finish());
}

async Task<FetchResult> FetchNativeStreamAsync()
{
    var hash = new RowMultisetHash();
    var count = 0;
    await foreach (var row in native.QueryStreamAsync(Sql))
    {
        AppendRow(hash, row.Values);
        count++;
    }
    return new FetchResult(count, hash.Finish());
}

async Task<FetchResult> FetchAdoNetStreamAsync()
{
    await using var command = adoNet.CreateCommand();
    command.CommandText = Sql;
    await using var reader = await command.ExecuteReaderAsync();
    var hash = new RowMultisetHash();
    var count = 0;
    while (await reader.ReadAsync())
    {
        AppendRow(hash, [reader.GetValue(0), reader.GetValue(1), reader.GetValue(2)]);
        count++;
    }
    return new FetchResult(count, hash.Finish());
}

Task<FetchResult> FetchIbmAsync()
{
    using var command = new IBMCommand(Sql, ibm);
    using var reader = command.ExecuteReader();
    var hash = new RowMultisetHash();
    var count = 0;
    var values = new object?[reader.FieldCount];
    while (reader.Read())
    {
        reader.GetValues(values);
        AppendRow(hash, values);
        count++;
    }
    return Task.FromResult(new FetchResult(count, hash.Finish()));
}

static void AppendRow(RowMultisetHash hash, IReadOnlyList<object?> values) => hash.Add(values);

static void ValidateResult(string mode, FetchResult actual, int expectedRows, byte[] expectedChecksum)
{
    if (actual.Rows != expectedRows || !CryptographicOperations.FixedTimeEquals(actual.Checksum, expectedChecksum))
        throw new InvalidOperationException($"{mode} mismatch: rows={actual.Rows}, checksum={Convert.ToHexString(actual.Checksum)}; expected rows={expectedRows}, checksum={Convert.ToHexString(expectedChecksum)}.");
}

sealed class RowMultisetHash
{
    private readonly ulong[] _wordSums = new ulong[4];
    private int _count;

    public void Add(IReadOnlyList<object?> values)
    {
        var normalized = new object?[values.Count];
        for (var index = 0; index < values.Count; index++) normalized[index] = values[index] is DBNull ? null : values[index];
        var rowBytes = JsonSerializer.SerializeToUtf8Bytes(normalized);
        var rowDigest = SHA256.HashData(rowBytes);
        for (var word = 0; word < _wordSums.Length; word++)
        {
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(rowDigest.AsSpan(word * sizeof(ulong), sizeof(ulong)));
            _wordSums[word] = unchecked(_wordSums[word] + value);
        }
        _count++;
    }

    public byte[] Finish()
    {
        Span<byte> digestInput = stackalloc byte[sizeof(int) + sizeof(ulong) * 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(digestInput, _count);
        for (var word = 0; word < _wordSums.Length; word++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(digestInput.Slice(sizeof(int) + word * sizeof(ulong), sizeof(ulong)), _wordSums[word]);
        return SHA256.HashData(digestInput);
    }
}

sealed record FetchResult(int Rows, byte[] Checksum);
sealed record FetchSample(double ElapsedMilliseconds, long ManagedAllocatedBytes);
