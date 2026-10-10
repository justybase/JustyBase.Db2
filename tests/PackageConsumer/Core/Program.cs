// Offline smoke test of the packed native API. No Db2 server is contacted.
using System.Data;
using System.Data.Common;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Types;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException($"Consumer smoke check failed: {message}");
}

var config = Db2Config.Parse("db2://app:not-used@localhost:50000/TESTDB");
Check(config.Host == "localhost" && config.Port == 50000 && config.Database == "TESTDB", "Db2Config.Parse");

var builder = new Db2AdoConnectionStringBuilder
{
    Server = "localhost:50000",
    Database = "TESTDB",
    UserID = "app",
    Password = "not-used",
    Pooling = false,
};
Check(builder.ConnectTimeout == 30, "native builder default connect timeout");

using var connection = new Db2AdoConnection(builder.ConnectionString);
Check(connection.State == ConnectionState.Closed, "connection is closed before open");
Check(connection.Database == "TESTDB", "connection string parsed into Database");

DbConnection fromFactory = Db2AdoProviderFactory.Instance.CreateConnection();
Check(fromFactory is Db2AdoConnection, "native factory creates native connections");
await fromFactory.DisposeAsync();

using DbCommand command = connection.CreateCommand();
command.CommandText = "SELECT ID FROM APP.ITEMS WHERE ID = ?";
DbParameter parameter = command.CreateParameter();
parameter.ParameterName = "@id";
parameter.DbType = DbType.Int32;
parameter.Value = 42;
command.Parameters.Add(parameter);
Check(command.Parameters.Count == 1, "parameter added");

Db2ColumnMetadata column = new("ID", SqlType.NInteger, 4, 10, 0, true);
Check(SqlTypeNames.BaseType(column.SqlType) == SqlType.Integer, "metadata SqlType base type");
Check(column.Nullable, "metadata nullability");

Console.WriteLine("PASS JustyBase.Db2 package consumer smoke (native API)");
return 0;
