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
/// Everyday mixed workload: 100k rows with NULLs across
/// INT, VARCHAR, DECIMAL, DATE and DOUBLE, plus a scalar aggregate.
/// Same fairness rules as the 500k narrow suite.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class VsIbmMixed100kBenchmarks
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
        await BenchFixture.EnsureMixed100kAsync(_jb).ConfigureAwait(false);

        var jbCount = await BenchFixture.CountAsync(_jb, BenchFixture.Mixed100kTable).ConfigureAwait(false);
        using var countCommand = new IBMCommand($"SELECT COUNT(*) FROM {BenchFixture.Mixed100kTable}", _ibm);
        var ibmCount = Convert.ToInt64(countCommand.ExecuteScalar(), Inv);
        if (jbCount != BenchFixture.Mixed100kRows || ibmCount != BenchFixture.Mixed100kRows)
            throw new InvalidOperationException($"Fixture row-count mismatch: JustyBase={jbCount}, IBM={ibmCount}.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _ibm.Dispose();
        await _jb.DisposeAsync().ConfigureAwait(false);
        Db2AdoConnection.ClearAllPools();
    }

    [Benchmark(Description = "Fetch 100k mixed incl. NULLs: JustyBase ADO")]
    public async Task<long> Fetch100kJustyBase()
    {
        await using var command = _jb.CreateCommand();
        command.CommandText = BenchFixture.Mixed100kSql;
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var values = new object[reader.FieldCount];
        long sum = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            reader.GetValues(values);
            sum += Convert.ToInt64(values[0], Inv);
            if (values[1] is string text)
                sum += text.Length;
            if (values[2] is decimal amount)
                sum += (long)(amount * 100);
            if (values[4] is double factor)
                sum += (long)factor;
        }
        return sum;
    }

    [Benchmark(Description = "Fetch 100k mixed incl. NULLs: IBM")]
    public long Fetch100kIbm()
    {
        using var command = new IBMCommand(BenchFixture.Mixed100kSql, _ibm);
        using var reader = command.ExecuteReader();
        var values = new object[reader.FieldCount];
        long sum = 0;
        while (reader.Read())
        {
            reader.GetValues(values);
            sum += Convert.ToInt64(values[0], Inv);
            if (values[1] is string text)
                sum += text.Length;
            if (values[2] is decimal amount)
                sum += (long)(amount * 100);
            if (values[4] is double factor)
                sum += (long)factor;
        }
        return sum;
    }

    [Benchmark(Description = "Scalar COUNT(*) over 100k: JustyBase")]
    public async Task<long> ScalarJustyBase()
    {
        await using var command = _jb.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {BenchFixture.Mixed100kTable}";
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), Inv);
    }

    [Benchmark(Description = "Scalar COUNT(*) over 100k: IBM")]
    public long ScalarIbm()
    {
        using var command = new IBMCommand($"SELECT COUNT(*) FROM {BenchFixture.Mixed100kTable}", _ibm);
        return Convert.ToInt64(command.ExecuteScalar(), Inv);
    }
}
