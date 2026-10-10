using System.Data;
using IBM.Data.Db2;
using JustyBase.Db2;

if (args is ["--live"])
{
    var connectionString = Environment.GetEnvironmentVariable("DB2_TEST_DSN");
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new InvalidOperationException("DB2_TEST_DSN is required for --live.");

    using var liveConnection = new DB2Connection(connectionString);
    liveConnection.Open();
    using var liveCommand = liveConnection.CreateCommand();
    liveCommand.CommandText = "VALUES CAST(? AS INTEGER)";
    var liveParameter = liveCommand.CreateParameter();
    liveParameter.DbType = DbType.Int32;
    liveParameter.Value = 42;
    liveCommand.Parameters.Add(liveParameter);
    var result = liveCommand.ExecuteScalar();
    if (result is not int value || value != 42)
        throw new InvalidOperationException($"Unexpected AOT live scalar result: {result ?? "<null>"}.");

    Console.WriteLine($"AOT live DB2 smoke PASS; scalar={value}");
    return;
}

if (args.Length != 0)
    throw new ArgumentException("Usage: AotSmoke [--live]");

// The default mode stays offline; --live runs one read-only scalar query against
// the DB2 connection string supplied by the isolated test-command wrapper.
var builder = new DB2ConnectionStringBuilder
{
    Server = "127.0.0.1:50000",
    Database = "TESTDB",
    UserID = "DB2INST1",
    Password = "not-used",
    Pooling = false,
    Connect_Timeout = 1,
};

using var connection = new DB2Connection(builder.ConnectionString);
using var command = connection.CreateCommand();
command.CommandText = "VALUES ?";
var parameter = command.CreateParameter();
parameter.DbType = DbType.Int32;
parameter.Value = 42;
command.Parameters.Add(parameter);

var parsed = Db2Config.Parse("db2://DB2INST1:not-used@127.0.0.1:50000/TESTDB");
Console.WriteLine($"AOT API smoke: {connection.GetType().FullName}; {parsed.Database}; parameters={command.Parameters.Count}");
