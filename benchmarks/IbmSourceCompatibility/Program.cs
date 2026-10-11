using System.Data;
using System.Data.Common;
using IBM.Data.Db2;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;

using var connection = GetConnection(null) as DB2Connection;
if (connection is null) throw new InvalidOperationException("The provider factory did not create an IBM-namespaced DB2Connection.");
var warningStates = new List<string>();
connection.InfoMessage += (_, args) =>
{
    for (var index = 0; index < args.Errors.Count; index++)
        warningStates.Add(args.Errors[index].SQLState);
};
connection.Open();

using (DbCommand warningCommand = connection.CreateCommand())
{
    warningCommand.CommandText = "SELECT COUNT(*), CAST(AVG(X) AS DOUBLE) FROM (VALUES (1), (CAST(NULL AS INTEGER)), (3)) AS T(X)";
    using var warningReader = warningCommand.ExecuteReader();
    if (!warningReader.Read() || warningReader.GetInt32(0) != 3 || warningReader.GetDouble(1) != 2d)
        throw new InvalidOperationException("The warning-producing aggregate returned unexpected values.");
    if (warningReader.Read()) throw new InvalidOperationException("The warning-producing aggregate returned extra rows.");
}
if (!warningStates.Contains("01003", StringComparer.Ordinal))
    throw new InvalidOperationException("InfoMessage did not deliver the expected SQLSTATE 01003 warning.");

var table = $"CS_COMPAT_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
var tableCreated = false;
try
{
    var headers = new[] { "ID INTEGER NOT NULL PRIMARY KEY", "LABEL VARCHAR(40)" };
    var createSql = $"CREATE TABLE {table} ({string.Join(',', headers)}){Environment.NewLine}ORGANIZE BY ROW{Environment.NewLine}COMPRESS YES";
    using (DbCommand command = connection.CreateCommand())
    {
        command.CommandText = createSql;
        command.ExecuteNonQuery();
        tableCreated = true;
    }

    var importJob = new DataTable();
    importJob.Columns.Add("ID", typeof(int));
    importJob.Columns.Add("LABEL", typeof(string));
    importJob.Rows.Add(1, "source-compatible");
    importJob.Rows.Add(2, DBNull.Value);

    var progress = new List<string>();
    using (DB2BulkCopy copy = new DB2BulkCopy(connection, DB2BulkCopyOptions.TableLock))
    {
        copy.BulkCopyTimeout = 60;
        copy.DestinationTableName = table;
        copy.NotifyAfter = 1;
        copy.DB2RowsCopied += (_, args) => progress.Add($"Copied {args.RowsCopied:N0}");
        using IDataReader sourceReader = importJob.CreateDataReader();
        copy.WriteToServer(sourceReader);
        if (copy.Errors.Count > 0)
            throw new InvalidOperationException($"DB2BulkCopy returned {copy.Errors.Count} errors: {copy.Errors[0].Message}");
        if (copy.RowsCopied != 2) throw new InvalidOperationException($"Expected 2 rows, got {copy.RowsCopied}.");
        copy.Close();
    }

    if (!progress.SequenceEqual(["Copied 1", "Copied 2"]))
        throw new InvalidOperationException($"Unexpected DB2RowsCopied notifications: {string.Join(", ", progress)}.");

    using (DbCommand verify = connection.CreateCommand())
    {
        verify.CommandText = $"SELECT ID, LABEL FROM {table} ORDER BY ID";
        using var reader = verify.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != 1 || reader.GetString(1) != "source-compatible")
            throw new InvalidOperationException("The first imported row did not match the source.");
        if (!reader.Read() || reader.GetInt32(0) != 2 || !reader.IsDBNull(1))
            throw new InvalidOperationException("The second imported NULL row did not match the source.");
        if (reader.Read()) throw new InvalidOperationException("The target contains an unexpected extra row.");
    }

    Console.WriteLine("PASS IBM.Data.Db2 source-shaped connection builder, InfoMessage SQLSTATE 01003, DbCommand, IDataReader, DB2BulkCopy and progress callbacks.");
}
finally
{
    if (tableCreated)
    {
        try
        {
            using DbCommand cleanup = connection.CreateCommand();
            cleanup.CommandText = $"DROP TABLE {table}";
            cleanup.ExecuteNonQuery();
        }
        catch (DB2Exception error) when (error.SqlState == "42704") { }
    }
    connection.Close();
}

static DbConnection? GetConnection(string? _)
{
    var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN");
    if (string.IsNullOrWhiteSpace(dsn)) return null;
    var config = Db2Config.Parse(dsn);
    var builder = new DB2ConnectionStringBuilder
    {
        UserID = config.User,
        Password = config.Password,
        Server = $"{config.Host}:{config.Port}",
        Database = config.Database,
        Connect_Timeout = 30,
        Pooling = false,
    };
    var connection = new DB2Connection();
    connection.ConnectionString = builder.ConnectionString;
    return connection;
}
