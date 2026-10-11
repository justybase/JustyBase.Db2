using BenchmarkDotNet.Running;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;

var runId = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
Environment.SetEnvironmentVariable("DB2_VS_IBM_BENCH_RUN_ID", runId);

try
{
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
finally
{
    var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN");
    if (dsn is not null)
    {
        try
        {
            await using var connection = new Db2AdoConnection(dsn);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE JBB_BC_{runId}";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is IDb2Error { SqlCode: -204 })
        {
            // The selected benchmark filter may not include the bulk-copy fixture.
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Could not clean up benchmark bulk table JBB_BC_{runId}: {error.Message}");
        }
        finally
        {
            Db2AdoConnection.ClearAllPools();
        }
    }
}
