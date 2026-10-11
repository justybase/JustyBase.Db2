extern alias IBM;

using System.Data;
using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using IBMConnection = IBM::IBM.Data.Db2.DB2Connection;
using IBMCommand = IBM::IBM.Data.Db2.DB2Command;

namespace Db2VsIbmBench;

/// <summary>
/// Everyday write path: 10k typed rows (INT, DECIMAL, TIMESTAMP) inserted
/// inside a transaction that is always rolled back, so iterations are
/// repeatable and the fixture table stays empty.
/// JustyBase uses the bounded multi-row importer (batch 500);
/// IBM uses the typical prepared single-row INSERT loop.
/// The source DataTable is built once in setup, outside measurement.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class VsIbmWriteBenchmarks
{
    public const string WriteTable = "JBBENCH_WR_10K";
    private const int RowCount = 10_000;

    private Db2AdoConnection _jb = null!;
    private IBMConnection _ibm = null!;
    private DataTable _source = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")
            ?? throw new InvalidOperationException("Set DB2_TEST_DSN, e.g. db2://db2inst1:password@localhost:50000/TESTDB.");
        var config = Db2Config.Parse(dsn);
        _jb = new Db2AdoConnection(dsn);
        await _jb.OpenAsync().ConfigureAwait(false);
        _ibm = new IBMConnection($"Server={config.Host}:{config.Port};Database={config.Database};UID={config.User};PWD={config.Password};Pooling=false;");
        _ibm.Open();

        await ExecuteJbAsync($"DROP TABLE {WriteTable}").ConfigureAwait(false);
        await ExecuteJbAsync($"CREATE TABLE {WriteTable} (ID INTEGER NOT NULL, A DECIMAL(12,2), TS TIMESTAMP)").ConfigureAwait(false);

        _source = new DataTable();
        _source.Columns.Add("ID", typeof(int));
        _source.Columns.Add("A", typeof(decimal));
        _source.Columns.Add("TS", typeof(DateTime));
        var baseTime = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Unspecified);
        for (var row = 1; row <= RowCount; row++)
            _source.Rows.Add(row, row / 100m, baseTime.AddMinutes(row));

        // Smoke check outside measurement: one full pass per driver, then rollback.
        if (await WriteJustyBase() != RowCount)
            throw new InvalidOperationException("JustyBase write smoke check failed.");
        if (WriteIbm() != RowCount)
            throw new InvalidOperationException("IBM write smoke check failed.");
        if (await CountAsync() != 0)
            throw new InvalidOperationException("Write fixture table is not empty after rollback.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        try
        {
            await ExecuteJbAsync($"DROP TABLE {WriteTable}").ConfigureAwait(false);
        }
        finally
        {
            _source.Dispose();
            _ibm.Dispose();
            await _jb.DisposeAsync().ConfigureAwait(false);
            Db2AdoConnection.ClearAllPools();
        }
    }

    [Benchmark(Description = "INSERT 10k rows, batch 500, rollback: JustyBase importer")]
    public async Task<long> WriteJustyBase()
    {
        await using var transaction = await _jb.BeginTransactionAsync().ConfigureAwait(false);
        using var source = _source.CreateDataReader();
        var result = await Db2AdoImporter.ImportAsync(
            _jb,
            source,
            WriteTable,
            options: new Db2AdoImportOptions { BatchSize = 500 },
            transaction: transaction).ConfigureAwait(false);
        await transaction.RollbackAsync().ConfigureAwait(false);
        return result.RowsInserted;
    }

    [Benchmark(Description = "INSERT 10k rows, prepared loop, rollback: IBM")]
    public long WriteIbm()
    {
        using var transaction = _ibm.BeginTransaction();
        using var command = new IBMCommand($"INSERT INTO {WriteTable} (ID, A, TS) VALUES (@ID, @A, @TS)", _ibm);
        command.Transaction = transaction;
        var id = command.CreateParameter();
        id.ParameterName = "@ID";
        var amount = command.CreateParameter();
        amount.ParameterName = "@A";
        var stamp = command.CreateParameter();
        stamp.ParameterName = "@TS";
        command.Parameters.Add(id);
        command.Parameters.Add(amount);
        command.Parameters.Add(stamp);
        command.Prepare();

        long inserted = 0;
        foreach (DataRow row in _source.Rows)
        {
            id.Value = row["ID"];
            amount.Value = row["A"];
            stamp.Value = row["TS"];
            inserted += command.ExecuteNonQuery();
        }

        transaction.Rollback();
        return inserted;
    }

    private async Task<long> CountAsync()
    {
        await using var command = _jb.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {WriteTable}";
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
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
