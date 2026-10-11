extern alias IBM;

using System.Data;
using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using IBMConnection = IBM::IBM.Data.Db2.DB2Connection;
using IBMBulkCopy = IBM::IBM.Data.Db2.DB2BulkCopy;
using IBMBulkCopyOptions = IBM::IBM.Data.Db2.DB2BulkCopyOptions;
using OurBulkCopy = global::IBM.Data.Db2.DB2BulkCopy;
using OurBulkCopyOptions = global::IBM.Data.Db2.DB2BulkCopyOptions;

namespace Db2VsIbmBench;

/// <summary>
/// Dedicated bulk-copy classes on both sides: our IBM-shaped
/// <c>DB2BulkCopy</c> (bounded multi-row INSERTs) vs the official IBM
/// <c>DB2BulkCopy</c> (native provider path). Same 10k typed rows
/// (INT, DECIMAL, TIMESTAMP) from the same DataTable.
/// The table is emptied in [IterationCleanup] (outside measurement) and the
/// copied row count is verified there, so every measured call copies exactly
/// <see cref="RowCount"/> rows into an empty table. JustyBase is tested at
/// several batch sizes to select a large default empirically; IBM's public
/// DB2BulkCopy API does not expose a batch-size property.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class VsIbmBulkCopyBenchmarks
{
    private const int RowCount = 10_000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private string _bulkTable = "";

    [Params(128, 256, 512, 1024)]
    public int BatchSize { get; set; }

    private Db2AdoConnection _jb = null!;
    private IBMConnection _ibm = null!;
    private DataTable _source = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var runId = Environment.GetEnvironmentVariable("DB2_VS_IBM_BENCH_RUN_ID")
            ?? Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        _bulkTable = "JBB_BC_" + runId;
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")
            ?? throw new InvalidOperationException("Set DB2_TEST_DSN, e.g. db2://db2inst1:password@localhost:50000/TESTDB.");
        var config = Db2Config.Parse(dsn);
        _jb = new Db2AdoConnection(dsn);
        await _jb.OpenAsync().ConfigureAwait(false);
        _ibm = new IBMConnection($"Server={config.Host}:{config.Port};Database={config.Database};UID={config.User};PWD={config.Password};Pooling=false;");
        _ibm.Open();

        try
        {
            await ExecuteJbAsync($"DROP TABLE {_bulkTable}").ConfigureAwait(false);
        }
        catch
        {
            // A benchmark run uses a unique table name; there is normally nothing to drop.
        }
        await ExecuteJbAsync($"CREATE TABLE {_bulkTable} (ID INTEGER NOT NULL, A DECIMAL(12,2), TS TIMESTAMP)").ConfigureAwait(false);
        Console.WriteLine($"[fixture] bulk table for this benchmark run: {_bulkTable}");

        _source = new DataTable();
        _source.Columns.Add("ID", typeof(int));
        _source.Columns.Add("A", typeof(decimal));
        _source.Columns.Add("TS", typeof(DateTime));
        var baseTime = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Unspecified);
        for (var row = 1; row <= RowCount; row++)
            _source.Rows.Add(row, row / 100m, baseTime.AddMinutes(row));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        // Program.cs drops the run-scoped table after all BenchmarkDotNet cases finish.
        _source.Dispose();
        _ibm.Dispose();
        await _jb.DisposeAsync().ConfigureAwait(false);
        Db2AdoConnection.ClearAllPools();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        var count = Count();
        if (count != RowCount)
            throw new InvalidOperationException($"Bulk copy left {count} rows, expected {RowCount}.");
        using var command = _jb.CreateCommand();
        command.CommandText = $"DELETE FROM {_bulkTable}";
        command.ExecuteNonQuery();
    }

    [Benchmark(Description = "BulkCopy 10k rows, Default: JustyBase DB2BulkCopy")]
    public long BulkJustyBaseDefault()
    {
        using var copy = new OurBulkCopy(_jb, OurBulkCopyOptions.Default)
        {
            DestinationTableName = _bulkTable,
            BatchSize = BatchSize,
        };
        copy.WriteToServer(_source);
        return copy.RowsCopied;
    }

    [Benchmark(Description = "BulkCopy 10k rows, Default: IBM DB2BulkCopy")]
    public int BulkIbmDefault()
    {
        using var copy = new IBMBulkCopy(_ibm, IBMBulkCopyOptions.Default)
        {
            DestinationTableName = _bulkTable,
        };
        copy.WriteToServer(_source);
        return RowCount;
    }

    [Benchmark(Description = "BulkCopy 10k rows, TableLock: JustyBase DB2BulkCopy")]
    public long BulkJustyBaseTableLock()
    {
        using var copy = new OurBulkCopy(_jb, OurBulkCopyOptions.TableLock)
        {
            DestinationTableName = _bulkTable,
            BatchSize = BatchSize,
        };
        copy.WriteToServer(_source);
        return copy.RowsCopied;
    }

    [Benchmark(Description = "BulkCopy 10k rows, TableLock: IBM DB2BulkCopy")]
    public int BulkIbmTableLock()
    {
        using var copy = new IBMBulkCopy(_ibm, IBMBulkCopyOptions.TableLock)
        {
            DestinationTableName = _bulkTable,
        };
        copy.WriteToServer(_source);
        return RowCount;
    }

    private long Count()
    {
        using var command = _jb.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {_bulkTable}";
        return Convert.ToInt64(command.ExecuteScalar(), Inv);
    }

    private async Task ExecuteJbAsync(string sql)
    {
        try
        {
            await using var command = _jb.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch when (sql.StartsWith("DROP TABLE", StringComparison.Ordinal))
        {
            // Fresh database: nothing to drop.
        }
    }
}
