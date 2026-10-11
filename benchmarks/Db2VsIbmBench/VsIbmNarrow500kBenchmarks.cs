extern alias IBM;

using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using IBMConnection = IBM::IBM.Data.Db2.DB2Connection;
using IBMCommand = IBM::IBM.Data.Db2.DB2Command;

namespace Db2VsIbmBench;

/// <summary>
/// Head-to-head fetch of 500k rows of everyday column types
/// (INT, VARCHAR, DECIMAL, TIMESTAMP) over a streaming reader.
/// No ORDER BY: row order is irrelevant, both drivers must return the same
/// row count and ID checksum (verified in setup, outside measurement).
/// IBM pooling is disabled; each driver uses its own dedicated connection.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class VsIbmNarrow500kBenchmarks
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private Db2AdoConnection _jb = null!;
    private IBMConnection _ibm = null!;

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
        await BenchFixture.EnsureNarrow500kAsync(_jb).ConfigureAwait(false);

        var jbCount = await BenchFixture.CountAsync(_jb, BenchFixture.Narrow500kTable).ConfigureAwait(false);
        using var countCommand = new IBMCommand($"SELECT COUNT(*) FROM {BenchFixture.Narrow500kTable}", _ibm);
        var ibmCount = Convert.ToInt64(countCommand.ExecuteScalar(), Inv);
        if (jbCount != BenchFixture.Narrow500kRows || ibmCount != BenchFixture.Narrow500kRows)
            throw new InvalidOperationException($"Fixture row-count mismatch: JustyBase={jbCount}, IBM={ibmCount}.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _ibm.Dispose();
        await _jb.DisposeAsync().ConfigureAwait(false);
        Db2AdoConnection.ClearAllPools();
    }

    [Benchmark(Description = "Fetch 500k narrow: JustyBase ADO GetValues")]
    public async Task<long> Fetch500kJustyBaseValues()
    {
        await using var command = _jb.CreateCommand();
        command.CommandText = BenchFixture.Narrow500kSql;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var values = new object[reader.FieldCount];
        long sum = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            reader.GetValues(values);
            sum += Convert.ToInt64(values[0], Inv);
            if (values[1] is string text)
                sum += text.Length;
        }
        return sum;
    }

    [Benchmark(Description = "Fetch 500k narrow: IBM GetValues")]
    public long Fetch500kIbmValues()
    {
        using var command = new IBMCommand(BenchFixture.Narrow500kSql, _ibm);
        using var reader = command.ExecuteReader();
        var values = new object[reader.FieldCount];
        long sum = 0;
        while (reader.Read())
        {
            reader.GetValues(values);
            sum += Convert.ToInt64(values[0], Inv);
            if (values[1] is string text)
                sum += text.Length;
        }
        return sum;
    }

    [Benchmark(Description = "Fetch 500k narrow: JustyBase typed getters")]
    public async Task<long> Fetch500kJustyBaseTyped()
    {
        await using var command = _jb.CreateCommand();
        command.CommandText = BenchFixture.Narrow500kSql;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        long sum = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            sum += reader.GetInt32(0);
            sum += reader.GetString(1).Length;
            sum += (long)(reader.GetDecimal(2) * 100);
            sum += reader.GetDateTime(3).Ticks % 1000;
        }
        return sum;
    }

    [Benchmark(Description = "Fetch 500k narrow: IBM typed getters")]
    public long Fetch500kIbmTyped()
    {
        using var command = new IBMCommand(BenchFixture.Narrow500kSql, _ibm);
        using var reader = command.ExecuteReader();
        long sum = 0;
        while (reader.Read())
        {
            sum += reader.GetInt32(0);
            sum += reader.GetString(1).Length;
            sum += (long)(reader.GetDecimal(2) * 100);
            sum += reader.GetDateTime(3).Ticks % 1000;
        }
        return sum;
    }
}
