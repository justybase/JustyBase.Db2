using System.Data;
using BenchmarkDotNet.Attributes;
using JustyBase.Db2.AdoNet;

namespace ReaderBench;

/// <summary>Warm pool leases; physical connection creation is excluded by setup.</summary>
[MemoryDiagnoser]
public class Db2PoolBenchmarks
{
    private string _dsn = null!;
    private Db2AdoConnection _reused = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")
            ?? throw new InvalidOperationException("Set DB2_TEST_DSN before running live pool benchmarks.");
        _reused = new Db2AdoConnection(_dsn);
        await _reused.OpenAsync().ConfigureAwait(false);
        await _reused.CloseAsync().ConfigureAwait(false);
    }

    [Benchmark(Description = "Warm pool: same connection Open + Close")]
    public async Task ReopenConnection()
    {
        await _reused.OpenAsync().ConfigureAwait(false);
        await _reused.CloseAsync().ConfigureAwait(false);
    }

    [Benchmark(Description = "Warm pool: new connection Open + Close")]
    public async Task NewConnection()
    {
        await using var connection = new Db2AdoConnection(_dsn);
        await connection.OpenAsync().ConfigureAwait(false);
        await connection.CloseAsync().ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _reused.DisposeAsync().ConfigureAwait(false);
        Db2AdoConnection.ClearAllPools();
    }
}

/// <summary>Dedicated disposable table; never modifies the shared P1BENCH corpus.</summary>
[MemoryDiagnoser]
public class Db2WriteBenchmarks
{
    private Db2AdoConnection _connection = null!;
    private Db2AdoCommand _nonQuery = null!;
    private DataTable _source = null!;
    private string _table = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")
            ?? throw new InvalidOperationException("Set DB2_TEST_DSN before running live write benchmarks.");
        _connection = new Db2AdoConnection(dsn);
        await _connection.OpenAsync().ConfigureAwait(false);
        _table = "JBP_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        await ExecuteAsync($"CREATE TABLE {_table} (ID INTEGER NOT NULL, AMOUNT DECIMAL(12,2), MOMENT TIMESTAMP)").ConfigureAwait(false);
        _nonQuery = (Db2AdoCommand)_connection.CreateCommand();
        _nonQuery.CommandText = $"DELETE FROM {_table} WHERE ID = -1";
        _source = new DataTable();
        _source.Columns.Add("ID", typeof(int));
        _source.Columns.Add("AMOUNT", typeof(decimal));
        _source.Columns.Add("MOMENT", typeof(DateTime));
        for (var row = 0; row < 1_000; row++)
            _source.Rows.Add(row, row / 100m, new DateTime(2026, 1, 1).AddTicks(row * 10L));
    }

    [Benchmark(Description = "ExecuteNonQuery: repeated no-match DELETE")]
    public Task<int> ExecuteNonQuery() => _nonQuery.ExecuteNonQueryAsync();

    [Benchmark(Description = "Importer: 1K typed rows, batch 128, rollback")]
    public async Task<long> ImportBatch()
    {
        await using var transaction = await _connection.BeginTransactionAsync().ConfigureAwait(false);
        using var source = _source.CreateDataReader();
        var result = await Db2AdoImporter.ImportAsync(_connection, source, _table,
            options: new Db2AdoImportOptions { BatchSize = 128 }, transaction: transaction).ConfigureAwait(false);
        await transaction.RollbackAsync().ConfigureAwait(false);
        return result.RowsInserted;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        try { if (_connection is not null && _connection.State == ConnectionState.Open) await ExecuteAsync($"DROP TABLE {_table}").ConfigureAwait(false); }
        finally
        {
            if (_nonQuery is not null) await _nonQuery.DisposeAsync().ConfigureAwait(false);
            _source?.Dispose();
            if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
            Db2AdoConnection.ClearAllPools();
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = (Db2AdoCommand)_connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}

[MemoryDiagnoser]
public class Db2PoolStreamBenchmarks
{
    private JustyBase.Db2.Db2Pool _pool = null!;
    [GlobalSetup]
    public async Task Setup()
    {
        _pool = new JustyBase.Db2.Db2Pool(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!, new JustyBase.Db2.Db2PoolOptions(1));
        await _pool.QueryAsync("VALUES 1");
    }
    [GlobalCleanup] public async Task Cleanup() => await _pool.DisposeAsync();
    [Benchmark]
    public async Task<long> ReadPublicRows()
    {
        long sum = 0;
        await foreach (var row in _pool.QueryStreamAsync("SELECT ID FROM P1BENCH.MIXED_10K")) sum += (int)row.Values[0]!;
        return sum;
    }
}
