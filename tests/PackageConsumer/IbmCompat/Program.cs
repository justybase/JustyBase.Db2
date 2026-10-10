// Offline smoke test of the packed IBM compatibility facade. No Db2 server is contacted.
using System.Data;
using System.Data.Common;
using IBM.Data.Db2;
using JustyBase.Db2.AdoNet;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException($"Consumer smoke check failed: {message}");
}

var builder = new DB2ConnectionStringBuilder
{
    Server = "localhost:50000",
    Database = "TESTDB",
    UserID = "app",
    Password = "not-used",
};
Check(builder.Connect_Timeout == 0, "IBM-style builder defaults Connect_Timeout to zero");

using var connection = new DB2Connection(builder.ConnectionString);
Check(connection is Db2AdoConnection, "IBM facade connection derives from the native connection");
Check(connection.Database == "TESTDB", "connection string parsed through the facade");

using DbCommand command = connection.CreateCommand();
command.CommandText = "SELECT ID FROM APP.ITEMS WHERE ID = ?";
DbParameter parameter = command.CreateParameter();
parameter.DbType = DbType.Int32;
parameter.Value = 7;
command.Parameters.Add(parameter);
Check(command.Parameters.Count == 1, "parameter added through the facade");

Check(DB2BulkCopyOptions.Default == 0, "bulk copy options default");
Check(typeof(DB2Exception).IsSubclassOf(typeof(DbException)), "DB2Exception is a DbException");

Console.WriteLine("PASS JustyBase.Db2.IBMCompat package consumer smoke (IBM.Data.Db2 facade)");
return 0;
