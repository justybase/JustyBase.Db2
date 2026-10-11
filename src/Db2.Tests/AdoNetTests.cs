using System.Data;
using System.Data.Common;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;
using IBM.Data.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class AdoNetTests
{
    [Fact]
    public void FactoryCreatesProviderObjectsAndConnectionParsesStandardAndUrlStrings()
    {
        var factory = Db2AdoProviderFactory.Instance;
        Assert.IsType<Db2AdoConnection>(factory.CreateConnection());
        Assert.IsType<Db2AdoCommand>(factory.CreateCommand());
        Assert.IsType<Db2AdoParameter>(factory.CreateParameter());
        Assert.IsType<Db2AdoConnectionStringBuilder>(factory.CreateConnectionStringBuilder());

        using var connection = new Db2AdoConnection("Server=db.example.test:50001;Database=SAMPLE;UID=app;PWD=secret;Pooling=false");
        Assert.Equal("db.example.test", connection.DataSource);
        Assert.Equal("SAMPLE", connection.Database);
        Assert.Equal(ConnectionState.Closed, connection.State);

        using var urlConnection = new Db2AdoConnection("db2://app:secret@db.example.test:50000/sample?pooling=false");
        Assert.Equal("db.example.test", urlConnection.DataSource);
        Assert.Equal("sample", urlConnection.Database);
    }

    [Fact]
    public void ConnectionStringBuilderRetainsDb2AndPoolOptions()
    {
        var builder = new Db2AdoConnectionStringBuilder
        {
            Server = "localhost:50000",
            Database = "SAMPLE",
            UserID = "app",
            Password = "pw",
            Pooling = false,
            MaxPoolSize = 3,
            ConnectTimeout = 17,
            UseSSL = false,
        };
        var connection = new Db2AdoConnection(builder.ConnectionString);
        Assert.Equal("localhost", connection.DataSource);
        Assert.Equal("SAMPLE", connection.Database);
        Assert.Contains("MaxPoolSize=3", builder.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IbmStyleConnectionBuilderSurfaceParsesTheSameConnectionSettings()
    {
        var builder = new DB2ConnectionStringBuilder
        {
            UserID = "app",
            Password = "pw",
            Server = "localhost:50001",
            Database = "SAMPLE",
            Connect_Timeout = 23,
            Pooling = false,
        };

        using var connection = new DB2Connection(builder.ConnectionString);
        Assert.Equal("app", builder.UserID);
        Assert.Equal("pw", builder.Password);
        Assert.Equal(23, builder.ConnectTimeout);
        Assert.Equal("localhost", connection.DataSource);
        Assert.Equal("SAMPLE", connection.Database);
        Assert.Equal(23, connection.ConnectionTimeout);
        Assert.False(builder.Pooling);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Connect_Timeout = 32_768);
    }

    [Fact]
    public void IbmStyleConnectTimeoutMatchesIbmDefaultAndExplicitZeroSemantics()
    {
        var builder = new DB2ConnectionStringBuilder
        {
            Server = "db.example.test:50001",
            Database = "SAMPLE",
        };
        Assert.Equal(0, builder.Connect_Timeout);

        using var empty = new DB2Connection();
        using var fromBuilder = new DB2Connection(builder.ConnectionString);
        using var fromRawKeyValue = new DB2Connection("Server=db.example.test:50001;Database=SAMPLE;UID=app;PWD=secret");
        using var fromRawUrl = new DB2Connection("db2://app:secret@db.example.test:50001/SAMPLE");
        using var explicitlyBounded = new DB2Connection("Server=db.example.test:50001;Database=SAMPLE;UID=app;PWD=secret;Connect_Timeout=7");

        Assert.Equal(0, empty.ConnectionTimeout);
        Assert.Equal(0, fromBuilder.ConnectionTimeout);
        Assert.Equal(0, fromRawKeyValue.ConnectionTimeout);
        Assert.Equal(0, fromRawUrl.ConnectionTimeout);
        Assert.Equal(7, explicitlyBounded.ConnectionTimeout);
        Assert.Contains("Connect_Timeout=0", fromRawKeyValue.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connect_timeout=0", fromRawUrl.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IbmStyleClosePreservesConnectionStringWhileSyncAndAsyncDisposeClearIt()
    {
        var connectionString = new DB2ConnectionStringBuilder
        {
            Server = "db.example.test:50001",
            Database = "SAMPLE",
            UserID = "app",
            Password = "secret",
        }.ConnectionString;

        var closed = new DB2Connection(connectionString);
        var retainedConnectionString = closed.ConnectionString;
        closed.Close();
        Assert.Equal(retainedConnectionString, closed.ConnectionString);
        closed.Dispose();
        Assert.Equal("", closed.ConnectionString);
        Assert.Equal(0, closed.ConnectionTimeout);

        var synchronouslyDisposed = new DB2Connection(connectionString);
        synchronouslyDisposed.Dispose();
        Assert.Equal("", synchronouslyDisposed.ConnectionString);

        var asynchronouslyDisposed = new DB2Connection(connectionString);
        await asynchronouslyDisposed.DisposeAsync();
        Assert.Equal("", asynchronouslyDisposed.ConnectionString);
    }

    [Fact]
    public void Db2BulkCopyCompatibilitySurfaceValidatesOptionsAndNotificationSettings()
    {
        using var connection = new DB2Connection();
        using var bulkCopy = new DB2BulkCopy(connection, DB2BulkCopyOptions.TableLock)
        {
            DestinationTableName = "APP.IMPORT_TARGET",
            BulkCopyTimeout = 42,
            NotifyAfter = 10_000,
            BatchSize = 256,
        };
        var notifications = new List<long>();
        bulkCopy.DB2RowsCopied += (_, args) => notifications.Add(args.RowsCopied);

        Assert.Equal("APP.IMPORT_TARGET", bulkCopy.DestinationTableName);
        Assert.Equal(42, bulkCopy.BulkCopyTimeout);
        Assert.Equal(10_000, bulkCopy.NotifyAfter);
        Assert.Equal(256, bulkCopy.BatchSize);
        Assert.Empty(bulkCopy.Errors);
        Assert.Throws<ArgumentOutOfRangeException>(() => bulkCopy.NotifyAfter = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => bulkCopy.BatchSize = 0);
        using var defaults = new DB2BulkCopy(connection);
        Assert.Equal(1024, defaults.BatchSize);
        Assert.Throws<NotSupportedException>(() => new DB2BulkCopy(connection, DB2BulkCopyOptions.KeepIdentity));
        Assert.Empty(notifications);

        var eventArgs = new DB2RowsCopiedEventArgs(10_000) { Abort = true };
        Assert.True(eventArgs.Abort);
        Assert.Equal(10_000, eventArgs.RowsCopied);
    }

    [Fact]
    public void ChangeDatabaseConnectionStringPreservesConnectionOptionsForKeyValueAndUrlDsn()
    {
        var keyValue = Db2AdoConnection.WithDatabase(
            "Server=db.example.test:50001;Database=OLDDB;UID=app;PWD=secret;Pooling=false;MaxPoolSize=3;SSL=true;Block_Size=32768;Client_ApplName=editor",
            "NEWDB");
        var parsed = Db2Config.Parse(keyValue);

        Assert.Equal("NEWDB", parsed.Database);
        Assert.Equal("db.example.test", parsed.Host);
        Assert.Equal(50001, parsed.Port);
        Assert.Equal("app", parsed.User);
        Assert.Equal("secret", parsed.Password);
        Assert.True(parsed.UseSsl);
        Assert.Equal(32768, parsed.BlockSize);
        Assert.Equal("editor", parsed.ClientApplName);
        var builder = new DbConnectionStringBuilder { ConnectionString = keyValue };
        Assert.Equal("false", Convert.ToString(builder["Pooling"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("3", Convert.ToString(builder["MaxPoolSize"], System.Globalization.CultureInfo.InvariantCulture));

        var url = Db2AdoConnection.WithDatabase("db2s://app:secret@db.example.test:50001/OLDDB?pooling=false&max_pool_size=3&block_size=32768", "NEW DB");
        var urlConfig = Db2Config.Parse(url);
        Assert.Equal("NEW DB", urlConfig.Database);
        Assert.Equal("app", urlConfig.User);
        Assert.Equal("secret", urlConfig.Password);
        Assert.True(urlConfig.UseSsl);
        Assert.Equal(32768, urlConfig.BlockSize);
        Assert.False(bool.Parse(urlConfig.Parameters["pooling"]));
        Assert.Equal("3", urlConfig.Parameters["max_pool_size"]);
    }

    [Fact]
    public void ChangeDatabaseValidatesEncodedRdbNameLengthAndCharacters()
    {
        Assert.Equal("DB2TEST", Db2AdoConnection.ValidateDatabaseName(" DB2TEST "));
        Assert.Equal("é", Db2AdoConnection.ValidateDatabaseName("é"));
        Assert.Throws<ArgumentNullException>(() => Db2AdoConnection.ValidateDatabaseName(null!));
        Assert.Throws<ArgumentException>(() => Db2AdoConnection.ValidateDatabaseName("  "));
        Assert.Throws<ArgumentException>(() => Db2AdoConnection.ValidateDatabaseName("DB\0NAME"));
        Assert.Throws<ArgumentException>(() => Db2AdoConnection.ValidateDatabaseName(new string('A', 19)));
        Assert.Throws<ArgumentException>(() => Db2AdoConnection.ValidateDatabaseName("😀"));
    }

    [Fact]
    public async Task ChangeDatabaseRequiresAnOpenConnection()
    {
        using var connection = new Db2AdoConnection("Server=db.example.test;Database=SAMPLE;UID=app;PWD=secret;Pooling=false");

        await Assert.ThrowsAsync<Db2AdoException>(() => connection.ChangeDatabaseAsync("OTHER"));
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal("SAMPLE", connection.Database);
    }

    [Fact]
    public void ParameterCollectionKeepsBindOrderAndSupportsPrefixInsensitiveLookup()
    {
        var command = new Db2AdoCommand();
        var first = new Db2AdoParameter { ParameterName = "@id", DbType = DbType.Int32, Value = 41 };
        var second = new Db2AdoParameter { ParameterName = ":label", DbType = DbType.String, Value = "row" };
        command.Parameters.Add(first);
        command.Parameters.Add(second);

        Assert.Equal(2, command.Parameters.Count);
        Assert.Same(first, command.Parameters["id"]);
        Assert.Same(second, command.Parameters["label"]);
        Assert.Same(second, command.Parameters[1]);
        Assert.Throws<ArgumentException>(() => command.Parameters.Add(new object()));
    }

    [Fact]
    public void SchemaDataTypesExposeTheColumnsUsedByTheJustyBasePlugin()
    {
        using var connection = new Db2AdoConnection();
        var dataTypes = connection.GetSchema("DataTypes");

        Assert.Equal(typeof(int), dataTypes.Columns["PROVIDER_TYPE"]!.DataType);
        Assert.Equal(typeof(string), dataTypes.Columns["PROVIDER_TYPE_NAME"]!.DataType);
        Assert.Equal(typeof(string), dataTypes.Columns["CREATE_PARAMS"]!.DataType);
        Assert.Equal(typeof(string), dataTypes.Columns["SQL_TYPE_NAME"]!.DataType);
        Assert.Contains(dataTypes.Rows.Cast<DataRow>(), row => (string)row["SQL_TYPE_NAME"] == "XML");
        Assert.Contains(dataTypes.Rows.Cast<DataRow>(), row => (string)row["SQL_TYPE_NAME"] == "DECFLOAT");
        Assert.Single(connection.GetSchema("DataTypes", ["INTEGER"]).Rows.Cast<DataRow>());
    }

    [Fact]
    public async Task DataReaderExposesTypedValuesSchemaAndSegmentedLobAccess()
    {
        Db2ColumnMetadata[] columns =
        [
            new("ID", SqlType.NInteger, 4, 10, 0, false),
            new("AMOUNT", SqlType.NDecimal, 12, 8, 2, true),
            new("CREATED", SqlType.NDate, 10, 0, 0, true),
            new("TEXT_VALUE", SqlType.NVarChar, 32, 0, 0, true),
            new("BINARY_VALUE", SqlType.NBlob, 32, 0, 0, true),
        ];
        object?[][] rows =
        [
            [7, "12.50", "2026-08-26", "hello", new byte[] { 1, 2, 3, 4 }],
            [8, null, null, null, null],
        ];
        var command = new Db2AdoCommand();
        using var connection = new Db2AdoConnection();
        await using var reader = new Db2AdoDataReader(
            command,
            connection,
            [new Db2ResultSet(columns, rows)],
            recordsAffected: -1,
            CommandBehavior.Default,
            error => error);
        await reader.InitializeAsync();

        Assert.True(reader.HasRows);
        Assert.Equal(5, reader.FieldCount);
        Assert.Equal(-1, reader.RecordsAffected);
        Assert.Equal(2, reader.GetOrdinal("created"));
        Assert.Equal("INTEGER", reader.GetDataTypeName(0));
        Assert.Equal(typeof(int), reader.GetFieldType(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(7, reader.GetInt32(0));
        Assert.Equal(7, reader.GetFieldValue<int>(0));
        Assert.Equal(12.50m, reader.GetDecimal(1));
        Assert.Equal(12.50m, reader.GetFieldValue<decimal>(1));
        Assert.Equal(new DateTime(2026, 8, 26), reader.GetDateTime(2));
        Assert.Equal(new DateTime(2026, 8, 26), reader.GetFieldValue<DateTime>(2));
        Assert.Equal(reader.GetValue(2), reader.GetFieldValue<object>(2));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<string>(2));
        Assert.Equal("hello", reader.GetString(3));
        var chunk = new byte[2];
        Assert.Equal(2, reader.GetBytes(4, 1, chunk, 0, 2));
        Assert.Equal(new byte[] { 2, 3 }, chunk);
        Assert.Equal(5, reader.GetSchemaTable()!.Rows.Count);

        Assert.True(await reader.ReadAsync());
        Assert.True(await reader.IsDBNullAsync(1));
        Assert.Equal(DBNull.Value, reader.GetValue(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task DataReaderParsesDb2TimeAndTimestampWithoutIntermediateStrings()
    {
        Db2ColumnMetadata[] columns =
        [
            new("CLOCK", SqlType.NTime, 15, 0, 6, true),
            new("MOMENT", SqlType.NTimestamp, 26, 0, 6, true),
        ];
        var reader = new Db2AdoDataReader(
            new Db2AdoCommand(), new Db2AdoConnection(),
            [new Db2ResultSet(columns, [["12.34.56.123456", "2026-08-26-12.34.56.123456"], ["24.00.00", "2026-08-26-24.00.00"]])],
            -1, CommandBehavior.Default, error => error);
        await using (reader)
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(new TimeSpan(12, 34, 56).Add(TimeSpan.FromTicks(1_234_560)), reader.GetValue(0));
            Assert.Equal(new DateTime(2026, 8, 26, 12, 34, 56).AddTicks(1_234_560), reader.GetDateTime(1));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(TimeSpan.FromHours(24), reader.GetFieldValue<TimeSpan>(0));
            Assert.Equal(new DateTime(2026, 8, 27), reader.GetFieldValue<DateTime>(1));
        }
    }

    [Fact]
    public async Task DataReaderOrdinalLookupKeepsExactPriorityAndMemorySlicesAvoidWholeValueCopies()
    {
        Db2ColumnMetadata[] columns =
        [
            new("VALUE", SqlType.NInteger, 4, 10, 0, false),
            new("value", SqlType.NInteger, 4, 10, 0, false),
            new("DATA", SqlType.NVarBinary, 4, 0, 0, true),
        ];
        var bytes = new byte[] { 1, 2, 3, 4 };
        var reader = new Db2AdoDataReader(
            new Db2AdoCommand(), new Db2AdoConnection(),
            [new Db2ResultSet(columns, [[10, 20, new ReadOnlyMemory<byte>(bytes)]])],
            -1, CommandBehavior.Default, error => error);
        await using (reader)
        {
            Assert.Equal(1, reader.GetOrdinal("value"));
            Assert.Equal(0, reader.GetOrdinal("VaLuE"));
            Assert.True(await reader.ReadAsync());

            var firstSlice = new byte[2];
            var secondSlice = new byte[2];
            Assert.Equal(2, reader.GetBytes(2, 0, firstSlice, 0, firstSlice.Length));
            Assert.Equal(2, reader.GetBytes(2, 2, secondSlice, 0, secondSlice.Length));
            Assert.Equal(new byte[] { 1, 2 }, firstSlice);
            Assert.Equal(new byte[] { 3, 4 }, secondSlice);

            await using var stream = reader.GetStream(2);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }
    }

    [Fact]
    public async Task EmptyDataReaderKeepsPreparedMetadataAndReportsNoRows()
    {
        var columns = new Db2ColumnMetadata[] { new("VALUE", SqlType.NInteger, 4, 10, 0, true) };
        var reader = new Db2AdoDataReader(
            new Db2AdoCommand(), new Db2AdoConnection(), [new Db2ResultSet(columns, Array.Empty<object?[]>())],
            0, CommandBehavior.Default, error => error);
        await using (reader)
        {
            Assert.Equal(1, reader.FieldCount);
            Assert.False(reader.HasRows);
            Assert.False(await reader.ReadAsync());
        }
    }

    [Fact]
    public async Task DataReaderSchemaTableTracksCurrentResultAndReaderLifetime()
    {
        var firstColumns = new Db2ColumnMetadata[] { new("FIRST_VALUE", SqlType.NInteger, 4, 10, 0, false) };
        var secondColumns = new Db2ColumnMetadata[] { new("SECOND_VALUE", SqlType.NVarChar, 20, 0, 0, true) };
        var reader = new Db2AdoDataReader(
            new Db2AdoCommand(), new Db2AdoConnection(),
            [new Db2ResultSet(firstColumns, [[1]]), new Db2ResultSet(secondColumns, [["two"]])],
            -1, CommandBehavior.Default, error => error);

        Assert.Equal("FIRST_VALUE", reader.GetSchemaTable()!.Rows[0]["ColumnName"]);
        Assert.True(await reader.NextResultAsync());
        Assert.Equal("SECOND_VALUE", reader.GetSchemaTable()!.Rows[0]["ColumnName"]);
        Assert.True(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        Assert.Null(reader.GetSchemaTable());

        await reader.DisposeAsync();
        Assert.Throws<InvalidOperationException>(() => reader.GetSchemaTable());
    }

    [Fact]
    public async Task SingleRowBehaviorSuppressesEveryLaterResultSet()
    {
        var firstColumns = new Db2ColumnMetadata[] { new("FIRST_VALUE", SqlType.NInteger, 4, 10, 0, false) };
        var secondColumns = new Db2ColumnMetadata[] { new("SECOND_VALUE", SqlType.NInteger, 4, 10, 0, false) };
        await using var reader = new Db2AdoDataReader(
            new Db2AdoCommand(), new Db2AdoConnection(),
            [new Db2ResultSet(firstColumns, [[1], [2]]), new Db2ResultSet(secondColumns, [[3]])],
            -1, CommandBehavior.SingleRow, error => error);

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
        Assert.Null(reader.GetSchemaTable());
    }

    [Fact]
    public void UnsupportedCommandBehaviorFailsClearly()
    {
        using var connection = new Db2AdoConnection();
        var command = new Db2AdoCommand { CommandText = "VALUES 1", Connection = connection };
        Assert.Throws<NotSupportedException>(() => command.ExecuteReader(CommandBehavior.KeyInfo));
        Assert.Throws<ArgumentOutOfRangeException>(() => command.ExecuteReader((CommandBehavior)128));
    }

    [Fact]
    public async Task DataReaderKeepsValuesAsTextWhenClrTypesCannotPreserveDb2Precision()
    {
        Db2ColumnMetadata[] columns =
        [
            new("WIDE_DECIMAL", SqlType.NDecimal, 17, 31, 9, true),
            new("WIDE_TIMESTAMP", SqlType.NTimestamp, 32, 0, 12, true),
        ];
        object?[][] rows =
        [["123456789012345678901.123456789", "2024-02-29-23.59.58.123456789012"]];
        var reader = new Db2AdoDataReader(
            new Db2AdoCommand(), new Db2AdoConnection(), [new Db2ResultSet(columns, rows)],
            -1, CommandBehavior.Default, error => error);
        await using (reader)
        {
            Assert.True(await reader.ReadAsync());
            for (var ordinal = 0; ordinal < columns.Length; ordinal++)
            {
                Assert.Equal(typeof(string), reader.GetFieldType(ordinal));
                Assert.IsType<string>(reader.GetValue(ordinal));
            }
            Assert.Equal("123456789012345678901.123456789", reader.GetString(0));
            Assert.Equal("2024-02-29-23.59.58.123456789012", reader.GetString(1));
        }
    }
}
