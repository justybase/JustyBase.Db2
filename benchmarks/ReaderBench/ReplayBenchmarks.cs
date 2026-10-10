using System.Data;
using BenchmarkDotNet.Attributes;
using Db2Replay;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;

namespace ReaderBench;

[MemoryDiagnoser]
[MedianColumn]
[MinColumn, MaxColumn]
[JsonExporterAttribute.Full]
public class Db2ReplayBenchmarks
{
    [Params("Int", "Mixed", "Wide", "Discard", "Small")]
    public string Fixture { get; set; } = "Int";
    [Params(65535)] public int BlockSize { get; set; } = 65535;
    private ReplayProcess _server = null!;
    private ReplayFixture _fixture = null!;
    private Db2AdoConnection _connection = null!;
    private Db2Session _session = null!;
    private long _actual, _expected;
    internal ReplayCounters Counters() => _server.Check();

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = new ReplayFixture(Fixture, generateData: false);
        _server = new ReplayProcess(Fixture, BlockSize);
        _connection = new Db2AdoConnection(_server.Dsn(BlockSize));
        await _connection.OpenAsync();
        _session = await Db2Session.ConnectAsync(_server.Dsn(BlockSize));
        // Verify all values independently of timed methods, including fields unused by lazy cases.
        await using var command = Command();
        await using var reader = await command.ExecuteReaderAsync();
        if (reader.FieldCount != _fixture.Columns) throw new InvalidDataException("Replay column count mismatch.");
        var count = 0;
        while (await reader.ReadAsync())
        {
            count++;
            for (var c = 0; c < _fixture.Columns; c++)
                if (!Equals(reader.GetValue(c), _fixture.Value(count, c) ?? DBNull.Value))
                    throw new InvalidDataException($"Replay value mismatch at {count}/{c}.");
        }
        if (count != _fixture.Rows) throw new InvalidDataException("Replay row count mismatch.");
        long nativeSum = 0; var nativeCount = 0;
        await foreach (var row in _session.QueryStreamAsync(_fixture.Sql))
        { nativeCount++; nativeSum += (int)row.Values[0]!; }
        if (nativeCount != _fixture.Rows || nativeSum != _fixture.IdChecksum)
            throw new InvalidDataException("Native replay checksum mismatch.");
        _server.Check();
    }

    // Assertions and peer health checks execute outside measurement.
    [IterationCleanup]
    public void Check()
    {
        if (_actual != _expected) throw new InvalidDataException($"Replay checksum mismatch: {_actual}, expected {_expected}.");
        _server.Check();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        if (_session is not null) await _session.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
    }

    private Db2AdoCommand Command()
    {
        var command = (Db2AdoCommand)_connection.CreateCommand();
        command.CommandText = _fixture.Sql;
        return command;
    }

    private async Task<long> Read(int columns, int limit = int.MaxValue, bool boxed = false,
        CommandBehavior behavior = CommandBehavior.Default)
    {
        await using var command = Command();
        await using var reader = await command.ExecuteReaderAsync(behavior).ConfigureAwait(false);
        long sum = 0; var count = 0; long consumed = 0;
        while (count < limit && await reader.ReadAsync().ConfigureAwait(false))
        {
            count++; sum += reader.GetInt32(0);
            if (boxed)
                for (var c = 0; c < Math.Min(columns, reader.FieldCount); c++)
                    consumed += reader.GetValue(c) switch { int n => n, long n => n, string s => s.Length, DBNull => 0, _ => 1 };
            else if (columns > 1 && reader.FieldCount > 1) consumed += reader.GetInt64(1);
        }
        var expectedCount = Math.Min(limit, _fixture.Rows);
        _actual = count == expectedCount ? sum : -1;
        _expected = (long)expectedCount * (expectedCount + 1) / 2;
        return sum + consumed;
    }

    [Benchmark] public Task<long> ReadAll_GetValue() => Read(int.MaxValue, boxed: true);
    [Benchmark] public Task<long> ReadAll_GetInt32() => Read(1);
    [Benchmark] public Task<long> ReadFirstColumn() => Read(1);
    [Benchmark] public Task<long> ReadTwoColumns() => Read(2);
    [Benchmark] public Task<long> ReadAllColumns() => Read(int.MaxValue, boxed: true);
    [Benchmark] public Task<long> Read1_Dispose() => Read(1, 1);
    [Benchmark] public Task<long> Read10_Dispose() => Read(1, 10);
    [Benchmark] public Task<long> SingleRow() => Read(1, 1, behavior: CommandBehavior.SingleRow);
    [Benchmark]
    public async Task<object?> ExecuteScalar()
    {
        await using var command = Command();
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        _actual = value is int n ? n : -1; _expected = 1;
        return value;
    }
    [Benchmark] public Task<long> NativeStream_ReadAll() => Native(int.MaxValue, true);
    [Benchmark] public Task<long> NativeStream_FirstColumnEquivalent() => Native(int.MaxValue, false);
    [Benchmark] public Task<long> NativeStream_Read1_Dispose() => Native(1, false);
    private async Task<long> Native(int limit, bool allColumns)
    {
        long sum = 0; var count = 0; long consumed = 0;
        await foreach (var row in _session.QueryStreamAsync(_fixture.Sql).ConfigureAwait(false))
        {
            count++; sum += (int)row.Values[0]!;
            if (allColumns)
                foreach (var value in row.Values) consumed += value switch { string s => s.Length, null => 0, _ => 1 };
            if (count == limit) break;
        }
        var expectedCount = Math.Min(limit, _fixture.Rows);
        _actual = count == expectedCount ? sum : -1;
        _expected = (long)expectedCount * (expectedCount + 1) / 2;
        return sum + consumed;
    }
}

// The same benchmark matrix at the three supported experiment sizes.
[MemoryDiagnoser, MedianColumn, MinColumn, MaxColumn, JsonExporterAttribute.Full]
public class Db2ReplayBlockSizeBenchmarks
{
    [Params(16384, 32767, 65535)] public int BlockSize { get; set; }
    [Params("Int", "Mixed", "Wide", "Small")] public string Fixture { get; set; } = "Int";
    private Db2ReplayBenchmarks _benchmark = null!;
    [GlobalSetup] public Task Setup()
    {
        _benchmark = new() { Fixture = Fixture, BlockSize = BlockSize };
        return _benchmark.Setup();
    }
    [Benchmark] public Task<long> ReadFirstColumn() => _benchmark.ReadFirstColumn();
    [IterationCleanup] public void Check() => _benchmark.Check();
    [GlobalCleanup] public Task Cleanup() => _benchmark.Cleanup();
}
