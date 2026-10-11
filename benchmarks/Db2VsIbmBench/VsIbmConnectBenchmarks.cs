extern alias IBM;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using IBMConnection = IBM::IBM.Data.Db2.DB2Connection;

namespace Db2VsIbmBench;

/// <summary>
/// Connection establishment cost with pooling disabled on both sides.
/// No fixture tables needed, so this is also the fast smoke test:
/// --filter '*VsIbmConnect*'.
/// </summary>
[MemoryDiagnoser]
[RankColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class VsIbmConnectBenchmarks
{
    private string _dsn = "";
    private string _dsnNoPooling = "";
    private string _ibmConnectionString = "";

    [GlobalSetup]
    public void Setup()
    {
        _dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")
            ?? throw new InvalidOperationException("Set DB2_TEST_DSN, e.g. db2://db2inst1:password@localhost:50000/TESTDB.");
        var config = Db2Config.Parse(_dsn);
        // Pooling off on both sides so every iteration pays a real TCP+auth handshake.
        _dsnNoPooling = _dsn.Contains('=', StringComparison.Ordinal)
            ? _dsn.TrimEnd(';') + ";Pooling=false"
            : _dsn.Contains('?') ? _dsn + "&pooling=false" : _dsn + "?pooling=false";
        _ibmConnectionString = $"Server={config.Host}:{config.Port};Database={config.Database};UID={config.User};PWD={config.Password};Pooling=false;";
    }

    [GlobalCleanup]
    public void Cleanup() => Db2AdoConnection.ClearAllPools();

    [Benchmark(Description = "Connect + close, pooling off: JustyBase")]
    public async Task ConnectJustyBase()
    {
        await using var connection = new Db2AdoConnection(_dsnNoPooling);
        await connection.OpenAsync().ConfigureAwait(false);
        await connection.CloseAsync().ConfigureAwait(false);
    }

    [Benchmark(Description = "Connect + close, pooling off: IBM")]
    public void ConnectIbm()
    {
        using var connection = new IBMConnection(_ibmConnectionString);
        connection.Open();
        connection.Close();
    }
}
