using System.Data;
using System.Security.Cryptography;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using IBM.Data.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class AdoNetLiveDb2Tests
{
    private static string Dsn => Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetProgressiveLobStreamsReadBinaryAndTextAndKeepConnectionUsable()
    {
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var table = $"CS_ADO_PLOB_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (B BLOB(10M))");
            created = true;
            var blob = Enumerable.Range(0, 2_500_003).Select(index => (byte)(index * 31)).ToArray();
            await using (var source = new MemoryStream(blob, writable: false))
            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = $"INSERT INTO {table} (B) VALUES (?)";
                insert.Parameters.Add(new Db2AdoParameter { Value = new Db2BlobStream(source, blob.Length) });
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }

            await using (var select = connection.CreateCommand())
            {
                select.CommandText = $"SELECT B FROM {table}";
                await using var reader = await select.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                await using var stream = reader.GetStream(0);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long length = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    length += read;
                }
                Assert.Equal(blob.LongLength, length);
                Assert.Equal(SHA256.HashData(blob), hash.GetHashAndReset());
                Assert.False(await reader.ReadAsync());
            }

            // Parameter-expression results may use EXTDTA and therefore materialize before
            // GetTextReader; this checks text API content without requiring a progressive locator.
            var text = string.Concat(Enumerable.Repeat("CLOB café 東京 🚀 ", 5_000));
            var encoded = System.Text.Encoding.UTF8.GetBytes(text);
            await using (var source = new MemoryStream(encoded, writable: false))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "VALUES CAST(? AS CLOB(2M))";
                command.Parameters.Add(new Db2AdoParameter { Value = new Db2ClobStream(source, encoded.Length) });
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                using var textReader = reader.GetTextReader(0);
                Assert.Equal(text, await textReader.ReadToEndAsync());
                Assert.False(await reader.ReadAsync());
            }

            Assert.Equal(29, await ScalarIntAsync(connection, "VALUES 29"));
        }
        finally { if (created) await ExecuteAsync(connection, $"DROP TABLE {schema}.{table}"); }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetChangeDatabaseRejectsActiveReaderAndTransactionWithoutClosingConnection()
    {
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var database = connection.Database;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "VALUES 29";
            await using var reader = await command.ExecuteReaderAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => connection.ChangeDatabaseAsync("OTHER"));
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(database, connection.Database);
            Assert.True(await reader.ReadAsync());
            Assert.Equal(29, reader.GetInt32(0));
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => connection.ChangeDatabaseAsync("OTHER"));
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(database, connection.Database);
            await transaction.RollbackAsync();
        }

        Assert.Equal(29, await ScalarIntAsync(connection, "VALUES 29"));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetChangeDatabaseRejectsAnActiveNonReaderCommandBeforeTouchingItsSession()
    {
        var table = $"CS_ADO_CHG_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var commandConnection = new Db2AdoConnection(Dsn);
        await using var blockerConnection = new Db2AdoConnection(Dsn);
        await commandConnection.OpenAsync();
        await blockerConnection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(commandConnection);
        var originalDatabase = commandConnection.Database;
        var created = false;
        Db2AdoTransaction? blockerTransaction = null;
        Task<int>? blockedUpdate = null;
        Task? changeDatabase = null;
        try
        {
            await ExecuteAsync(commandConnection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, VALUE INTEGER NOT NULL)");
            created = true;
            await ExecuteAsync(commandConnection, $"INSERT INTO {table} VALUES (1, 0)");

            blockerTransaction = (Db2AdoTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var holdLock = blockerConnection.CreateCommand())
            {
                holdLock.Transaction = blockerTransaction;
                holdLock.CommandText = $"UPDATE {table} SET VALUE = VALUE + 1 WHERE ID = 1";
                Assert.Equal(1, await holdLock.ExecuteNonQueryAsync());
            }

            await using var update = commandConnection.CreateCommand();
            update.CommandTimeout = 30;
            update.CommandText = $"UPDATE {table} SET VALUE = VALUE + 1 WHERE ID = 1";
            blockedUpdate = update.ExecuteNonQueryAsync();
            await Task.Delay(400);
            Assert.False(blockedUpdate.IsCompleted, "The update should be waiting on the other connection's row lock.");

            var missingDatabase = $"X{Guid.NewGuid():N}"[..18].ToUpperInvariant();
            changeDatabase = commandConnection.ChangeDatabaseAsync(missingDatabase);
            await Task.Delay(250);
            var rejectedBeforeUnlock = changeDatabase.IsCompleted;

            await blockerTransaction.RollbackAsync();
            blockerTransaction = null;
            Assert.Equal(1, await blockedUpdate.WaitAsync(TimeSpan.FromSeconds(15)));

            Exception? changeError = null;
            try { await changeDatabase.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (Exception error) { changeError = error; }

            Assert.True(rejectedBeforeUnlock, "ChangeDatabase must reject the in-flight command without waiting for or closing its session.");
            Assert.IsType<InvalidOperationException>(changeError);
            Assert.Equal(ConnectionState.Open, commandConnection.State);
            Assert.Equal(originalDatabase, commandConnection.Database);
            Assert.Equal(1, await ScalarIntAsync(commandConnection, $"SELECT VALUE FROM {table} WHERE ID = 1"));
        }
        finally
        {
            if (blockerTransaction is not null)
            {
                try { await blockerTransaction.RollbackAsync(); }
                catch { }
            }
            if (blockedUpdate is not null)
            {
                try { await blockedUpdate.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch { }
            }
            if (changeDatabase is not null)
            {
                try { await changeDatabase.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch { }
            }
            try { await commandConnection.CloseAsync(); }
            catch { }
            try { await blockerConnection.CloseAsync(); }
            catch { }
            if (created)
            {
                await using var cleanup = new Db2AdoConnection(Dsn);
                await cleanup.OpenAsync();
                await TryDropAsync(cleanup, table);
            }
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetCloseInterruptsAndWaitsForAnActiveNonReaderCommand()
    {
        var table = $"CS_ADO_CLS_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var commandConnection = new Db2AdoConnection(Dsn);
        await using var blockerConnection = new Db2AdoConnection(Dsn);
        await commandConnection.OpenAsync();
        await blockerConnection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(commandConnection);
        var created = false;
        Db2AdoTransaction? blockerTransaction = null;
        Task<int>? blockedUpdate = null;
        Task? closeTask = null;
        try
        {
            await ExecuteAsync(commandConnection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, VALUE INTEGER NOT NULL)");
            created = true;
            await ExecuteAsync(commandConnection, $"INSERT INTO {table} VALUES (1, 0)");

            blockerTransaction = (Db2AdoTransaction)await blockerConnection.BeginTransactionAsync();
            await using (var holdLock = blockerConnection.CreateCommand())
            {
                holdLock.Transaction = blockerTransaction;
                holdLock.CommandText = $"UPDATE {table} SET VALUE = VALUE + 1 WHERE ID = 1";
                Assert.Equal(1, await holdLock.ExecuteNonQueryAsync());
            }

            await using var update = commandConnection.CreateCommand();
            update.CommandTimeout = 30;
            update.CommandText = $"UPDATE {table} SET VALUE = VALUE + 1 WHERE ID = 1";
            blockedUpdate = update.ExecuteNonQueryAsync();
            await Task.Delay(400);
            Assert.False(blockedUpdate.IsCompleted, "The update must still be waiting on the held row lock.");

            closeTask = commandConnection.CloseAsync();
            await closeTask.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(ConnectionState.Closed, commandConnection.State);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedUpdate);

            await blockerTransaction.RollbackAsync();
            blockerTransaction = null;
            await using var reopened = new Db2AdoConnection(Dsn);
            await reopened.OpenAsync();
            Assert.Equal(0, await ScalarIntAsync(reopened, $"SELECT VALUE FROM {table} WHERE ID = 1"));
        }
        finally
        {
            if (blockerTransaction is not null) await blockerTransaction.RollbackAsync();
            if (closeTask is not null) await closeTask;
            if (blockedUpdate is not null && !blockedUpdate.IsCompleted)
            {
                try { await blockedUpdate; }
                catch (OperationCanceledException) { }
            }
            if (created)
            {
                await using var cleanup = new Db2AdoConnection(Dsn);
                await cleanup.OpenAsync();
                await TryDropAsync(cleanup, table);
            }
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetChangeDatabaseFailedReconnectLeavesConnectionClosedAndKeepsOriginalDsn()
    {
        var originalDsn = Dsn;
        await using var connection = new Db2AdoConnection(originalDsn);
        await connection.OpenAsync();
        var originalDatabase = connection.Database;
        var missingDatabase = "X" + Guid.NewGuid().ToString("N")[..17].ToUpperInvariant();

        await Assert.ThrowsAsync<Db2AdoException>(() => connection.ChangeDatabaseAsync(missingDatabase));

        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(originalDsn, connection.ConnectionString);
        Assert.Equal(originalDatabase, connection.Database);
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetPrepareRebindsParametersSqlAndConnectionSessions()
    {
        await using var firstConnection = new Db2AdoConnection(Dsn);
        await using var secondConnection = new Db2AdoConnection(Dsn);
        await firstConnection.OpenAsync();
        await secondConnection.OpenAsync();
        await using var command = new Db2AdoCommand
        {
            Connection = firstConnection,
            CommandText = "VALUES CAST(? AS INTEGER)",
        };
        var parameter = new Db2AdoParameter { ParameterName = "value", DbType = DbType.Int32, Value = 13 };
        command.Parameters.Add(parameter);

        await command.PrepareAsync();
        Assert.Equal(13, Assert.IsType<int>(await command.ExecuteScalarAsync()));
        parameter.Value = 29;
        Assert.Equal(29, Assert.IsType<int>(await command.ExecuteScalarAsync()));

        command.CommandText = "VALUES CAST(? AS VARCHAR(20))";
        parameter.DbType = DbType.String;
        parameter.Value = "rebound-sql";
        await command.PrepareAsync();
        Assert.Equal("rebound-sql", Assert.IsType<string>(await command.ExecuteScalarAsync()));

        command.Connection = secondConnection;
        parameter.Value = "second-session";
        Assert.Equal("second-session", Assert.IsType<string>(await command.ExecuteScalarAsync()));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetCloseConnectionCommandBehaviorClosesAfterReaderDisposal()
    {
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "VALUES 29";
        var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(29, reader.GetInt32(0));
        await reader.DisposeAsync();
        Assert.Equal(ConnectionState.Closed, connection.State);

        await connection.OpenAsync();
        Assert.Equal(29, await ScalarIntAsync(connection, "VALUES 29"));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetFixtureCleanupAcceptsAnAlreadyMissingTable()
    {
        var table = $"CS_IMP_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();

        await TryDropAsync(connection, table);

        Assert.Equal(29, await ScalarIntAsync(connection, "VALUES 29"));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetDecodesAggregateRowsThatCarryDb2Warnings()
    {
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var warnings = new List<Db2Warning>();
        connection.InfoMessage += (_, args) => warnings.AddRange(args.Errors);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), CAST(AVG(X) AS DOUBLE) FROM (VALUES (1), (CAST(NULL AS INTEGER)), (3)) AS T(X)";
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.GetInt32(0));
        Assert.Equal(2d, reader.GetDouble(1));
        Assert.False(await reader.ReadAsync());
        var warning = Assert.Single(warnings);
        Assert.Equal("01003", warning.SqlState);
        Assert.StartsWith("W", warning.WarningFlags);
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task IbmStyleInfoMessageEventDeliversSuccessfulDb2Warnings()
    {
        await using var connection = new DB2Connection(Dsn);
        await connection.OpenAsync();
        var warnings = new List<DB2Error>();
        connection.InfoMessage += (_, args) => warnings.AddRange(args.Errors);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), CAST(AVG(X) AS DOUBLE) FROM (VALUES (1), (CAST(NULL AS INTEGER)), (3)) AS T(X)";
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.GetInt32(0));
        Assert.Equal(2d, reader.GetDouble(1));
        Assert.False(await reader.ReadAsync());
        var warning = Assert.Single(warnings);
        Assert.Equal("01003", warning.SqlState);
        Assert.False(string.IsNullOrWhiteSpace(warning.Message));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task IbmStyleConnectionBuilderOpensLiveDb2AndRaisesInfoMessage()
    {
        var config = Db2Config.Parse(Dsn);
        var builder = new DB2ConnectionStringBuilder
        {
            UserID = config.User,
            Password = config.Password,
            Server = $"{config.Host}:{config.Port}",
            Database = config.Database,
            Connect_Timeout = 30,
            Pooling = false,
        };
        await using var connection = new DB2Connection(builder.ConnectionString);
        await connection.OpenAsync();
        var warnings = new List<DB2Error>();
        connection.InfoMessage += (_, args) => warnings.AddRange(args.Errors);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), CAST(AVG(X) AS DOUBLE) FROM (VALUES (1), (CAST(NULL AS INTEGER)), (3)) AS T(X)";
        await using var reader = await command.ExecuteReaderAsync();

        Assert.Equal(config.Database, connection.Database);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(3, reader.GetInt32(0));
        Assert.Equal(2d, reader.GetDouble(1));
        Assert.False(await reader.ReadAsync());
        Assert.Equal("01003", Assert.Single(warnings).SqlState);
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task IbmStyleCommandFailureUsesDb2ExceptionAndPreservesDb2ErrorFields()
    {
        var table = $"CS_DB2EX_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new DB2Connection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY)");
            created = true;
            await ExecuteAsync(connection, $"INSERT INTO {table} VALUES (1)");
            await using var duplicate = connection.CreateCommand();
            duplicate.CommandText = $"INSERT INTO {table} VALUES (1)";

            var error = await Assert.ThrowsAsync<DB2Exception>(() => duplicate.ExecuteNonQueryAsync());
            Assert.Equal("23505", error.SqlState);
            Assert.Equal("23505", Assert.Single(error.Errors).SQLState);
            Assert.NotEqual(0, error.Errors[0].NativeError);
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(1, await ScalarIntAsync(connection, $"SELECT COUNT(*) FROM {table}"));
            Assert.Equal(29, await ScalarIntAsync(connection, "VALUES 29"));

            await connection.CloseAsync();
            Assert.Equal(ConnectionState.Closed, connection.State);
            await connection.OpenAsync();
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(1, await ScalarIntAsync(connection, $"SELECT COUNT(*) FROM {table}"));
            Assert.Equal(29, await ScalarIntAsync(connection, "VALUES 29"));
        }
        finally
        {
            if (created) await TryDropAsync(connection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task IbmStyleBulkCopyUsesTableLockReportsProgressAndCopiesTypedValues()
    {
        var table = $"CS_BCP_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new DB2Connection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        Exception? testFailure = null;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, LABEL VARCHAR(40), AMOUNT DECIMAL(12,2))");
            created = true;

            var source = new DataTable();
            source.Columns.Add("ID", typeof(int));
            source.Columns.Add("LABEL", typeof(string));
            source.Columns.Add("AMOUNT", typeof(decimal));
            for (var id = 1; id <= 25; id++)
                source.Rows.Add(id, id % 4 == 0 ? DBNull.Value : $"row-{id}", id % 4 == 0 ? DBNull.Value : id * 1.25m);

            var notifications = new List<long>();
            using (var bulkCopy = new DB2BulkCopy(connection, DB2BulkCopyOptions.TableLock)
            {
                DestinationTableName = table,
                BulkCopyTimeout = 60,
                NotifyAfter = 10,
            })
            {
                bulkCopy.DB2RowsCopied += (_, args) =>
                {
                    notifications.Add(args.RowsCopied);
                    if (args.RowsCopied == 10) bulkCopy.NotifyAfter = 5;
                };
                bulkCopy.DB2RowsCopied += (_, _) => throw new InvalidOperationException("IBM DB2BulkCopy ignores progress-handler exceptions.");
                using var reader = source.CreateDataReader();
                bulkCopy.WriteToServer(reader);
                Assert.Empty(bulkCopy.Errors);
                Assert.Equal(25, bulkCopy.RowsCopied);
            }

            Assert.Equal(new long[] { 10, 15, 20, 25 }, notifications);
            await using var query = connection.CreateCommand();
            query.CommandText = $"SELECT ID, LABEL, AMOUNT FROM {table} ORDER BY ID";
            await using var result = await query.ExecuteReaderAsync();
            for (var id = 1; id <= 25; id++)
            {
                Assert.True(await result.ReadAsync());
                Assert.Equal(id, result.GetInt32(0));
                if (id % 4 == 0)
                {
                    Assert.True(result.IsDBNull(1));
                    Assert.True(result.IsDBNull(2));
                }
                else
                {
                    Assert.Equal($"row-{id}", result.GetString(1));
                    Assert.Equal(id * 1.25m, result.GetDecimal(2));
                }
            }
            Assert.False(await result.ReadAsync());
        }
        catch (Exception error)
        {
            testFailure = error;
            throw;
        }
        finally
        {
            if (created)
                await LiveTestCleanup.RunPreservingFailureAsync(testFailure, () => TryDropAsync(connection, table));
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task IbmStyleBulkCopyTableLockBlocksConcurrentUpdatesUntilCopyCompletes()
    {
        var table = $"CS_BCP_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var copyConnection = new DB2Connection(Dsn);
        await using var updateConnection = new Db2AdoConnection(Dsn);
        await copyConnection.OpenAsync();
        await updateConnection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(copyConnection);
        var created = false;
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        Task? copyTask = null;
        Task<int>? updateTask = null;
        try
        {
            await ExecuteAsync(copyConnection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, VALUE INTEGER NOT NULL)");
            created = true;
            await ExecuteAsync(copyConnection, $"INSERT INTO {table} VALUES (1, 0)");

            var source = new DataTable();
            source.Columns.Add("ID", typeof(int));
            source.Columns.Add("VALUE", typeof(int));
            source.Rows.Add(2, 0);
            using var reader = source.CreateDataReader();
            using var bulkCopy = new DB2BulkCopy(copyConnection, DB2BulkCopyOptions.TableLock)
            {
                DestinationTableName = table,
                NotifyAfter = 1,
            };
            bulkCopy.DB2RowsCopied += (_, _) =>
            {
                callbackEntered.Set();
                releaseCallback.Wait(TimeSpan.FromSeconds(15));
            };

            copyTask = Task.Run(() => bulkCopy.WriteToServer(reader));
            Assert.True(await Task.Run(() => callbackEntered.Wait(TimeSpan.FromSeconds(10))), "The bulk-copy progress callback did not run.");

            await using var update = updateConnection.CreateCommand();
            update.CommandText = $"UPDATE {table} SET VALUE = VALUE + 1 WHERE ID = 1";
            updateTask = update.ExecuteNonQueryAsync();
            await Task.Delay(350);
            Assert.False(updateTask.IsCompleted, "An exclusive TableLock must prevent another session from updating during the copy.");

            releaseCallback.Set();
            await copyTask.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, await updateTask.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(1, await ScalarIntAsync(copyConnection, $"SELECT VALUE FROM {table} WHERE ID = 1"));
            Assert.Equal(2, await ScalarIntAsync(copyConnection, $"SELECT COUNT(*) FROM {table}"));
        }
        finally
        {
            releaseCallback.Set();
            if (copyTask is not null) await copyTask;
            if (updateTask is not null) await updateTask;
            if (created) await TryDropAsync(copyConnection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task IbmStyleBulkCopyCollectsDuplicateRowErrorsAndKeepsEarlierCommittedRows()
    {
        var table = $"CS_BCP_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new DB2Connection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, LABEL VARCHAR(40))");
            created = true;

            var source = new DataTable();
            source.Columns.Add("ID", typeof(int));
            source.Columns.Add("LABEL", typeof(string));
            source.Rows.Add(1, "first");
            source.Rows.Add(2, "second");
            source.Rows.Add(2, "duplicate");

            using (var bulkCopy = new DB2BulkCopy(connection)
            {
                DestinationTableName = table,
                BulkCopyTimeout = 60,
            })
            {
                using var reader = source.CreateDataReader();
                bulkCopy.WriteToServer(reader);
                var error = Assert.Single(bulkCopy.Errors);
                Assert.Equal(3, error.RowNumber);
                Assert.Equal("23505", error.SqlState);
                Assert.Equal("23505", error.SQLState);
            }

            Assert.Equal(2, await ScalarIntAsync(connection, $"SELECT COUNT(*) FROM {table}"));
            Assert.Equal(29, await ScalarIntAsync(connection, "VALUES 29"));
        }
        finally
        {
            if (created) await TryDropAsync(connection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task IbmStyleBulkCopyThrowsDb2ExceptionWhenNoSourceRowsWereCopied()
    {
        var table = $"CS_BCP0_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new DB2Connection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, LABEL VARCHAR(40))");
            created = true;
            await ExecuteAsync(connection, $"INSERT INTO {table} VALUES (1, 'existing')");

            var source = new DataTable();
            source.Columns.Add("ID", typeof(int));
            source.Columns.Add("LABEL", typeof(string));
            source.Rows.Add(1, "duplicate-first-row");
            using var bulkCopy = new DB2BulkCopy(connection) { DestinationTableName = table };
            using var reader = source.CreateDataReader();

            var error = Assert.Throws<DB2Exception>(() => bulkCopy.WriteToServer(reader));
            Assert.Equal("23505", error.SqlState);
            Assert.Equal("23505", Assert.Single(bulkCopy.Errors).SQLState);
            Assert.Equal(0, bulkCopy.RowsCopied);
            Assert.Equal(1, await ScalarIntAsync(connection, $"SELECT COUNT(*) FROM {table}"));
            Assert.Equal(29, await ScalarIntAsync(connection, "VALUES 29"));
        }
        finally
        {
            if (created) await TryDropAsync(connection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task IbmStyleBulkCopyTableLockReportsRowErrorAndPreservesEarlierRows()
    {
        var table = $"CS_BCP_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new DB2Connection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, LABEL VARCHAR(40))");
            created = true;
            var source = new DataTable();
            source.Columns.Add("ID", typeof(int));
            source.Columns.Add("LABEL", typeof(string));
            source.Rows.Add(1, "first");
            source.Rows.Add(2, "second");
            source.Rows.Add(2, "duplicate");

            using (var bulkCopy = new DB2BulkCopy(connection, DB2BulkCopyOptions.TableLock)
            {
                DestinationTableName = table,
            })
            {
                using var reader = source.CreateDataReader();
                bulkCopy.WriteToServer(reader);
                var error = Assert.Single(bulkCopy.Errors);
                Assert.Equal(3, error.RowNumber);
                Assert.Equal("23505", error.SQLState);
                Assert.Equal(2, bulkCopy.RowsCopied);
            }

            Assert.Equal(2, await ScalarIntAsync(connection, $"SELECT COUNT(*) FROM {table}"));
            Assert.Equal(29, await ScalarIntAsync(connection, "VALUES 29"));
        }
        finally
        {
            if (created) await TryDropAsync(connection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task IbmStyleBulkCopyAbortEventStopsAndKeepsCompletedRows()
    {
        var table = $"CS_BCP_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new DB2Connection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, LABEL VARCHAR(40))");
            created = true;

            var source = new DataTable();
            source.Columns.Add("ID", typeof(int));
            source.Columns.Add("LABEL", typeof(string));
            for (var id = 1; id <= 25; id++) source.Rows.Add(id, $"row-{id}");

            using (var bulkCopy = new DB2BulkCopy(connection)
            {
                DestinationTableName = table,
                NotifyAfter = 5,
            })
            {
                var notifications = new List<long>();
                bulkCopy.DB2RowsCopied += (_, args) =>
                {
                    notifications.Add(args.RowsCopied);
                    args.Abort = true;
                };
                using var reader = source.CreateDataReader();
                Assert.ThrowsAny<SystemException>(() => bulkCopy.WriteToServer(reader));
                Assert.Empty(bulkCopy.Errors);
                Assert.Equal(new long[] { 5 }, notifications);
            }

            Assert.Equal(5, await ScalarIntAsync(connection, $"SELECT COUNT(*) FROM {table}"));
            Assert.Equal(29, await ScalarIntAsync(connection, "VALUES 29"));
        }
        finally
        {
            if (created) await TryDropAsync(connection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetParameterSourceConversionsMatchIbmCliAndRejectedBindKeepsConnectionUsable()
    {
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();

        await using (var textCommand = connection.CreateCommand())
        {
            textCommand.CommandText = "VALUES CAST(? AS VARCHAR(4))";
            var boolean = textCommand.CreateParameter();
            boolean.Value = true;
            textCommand.Parameters.Add(boolean);
            Assert.Equal("1", Assert.IsType<string>(await textCommand.ExecuteScalarAsync()));
        }

        await using (var numericCommand = connection.CreateCommand())
        {
            numericCommand.CommandText = "VALUES CAST(? AS SMALLINT)";
            var bytes = numericCommand.CreateParameter();
            bytes.Value = new byte[] { 49, 50, 51 };
            numericCommand.Parameters.Add(bytes);
            await Assert.ThrowsAsync<InvalidCastException>(() => numericCommand.ExecuteScalarAsync());
        }

        await using var reuseCommand = connection.CreateCommand();
        reuseCommand.CommandText = "VALUES 29";
        Assert.Equal(29, Assert.IsType<int>(await reuseCommand.ExecuteScalarAsync()));
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetImporterStreamsTypedRowsNullsAndBlobsInBoundedBatches()
    {
        var table = $"CS_IMP_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, TXT VARCHAR(80), AMOUNT DECIMAL(12,2), PAYLOAD BLOB(1M))");
            created = true;

            using var source = CreateImportSource(275);
            using var reader = source.CreateDataReader();
            var notifications = new List<Db2AdoImportProgress>();
            var result = await Db2AdoImporter.ImportAsync(
                connection,
                reader,
                table,
                options: new Db2AdoImportOptions { BatchSize = 37, MaxBatchBytes = 512, NotifyAfterRows = 50 },
                progress: notifications.Add);

            Assert.Equal(275, result.RowsRead);
            Assert.Equal(275, result.RowsInserted);
            Assert.Equal(275, result.RowsCommitted);
            Assert.NotEmpty(notifications);
            Assert.Equal(275, notifications[^1].RowsInserted);
            var ids = new List<int>();
            await using (var idQuery = connection.CreateCommand())
            {
                idQuery.CommandText = $"SELECT ID FROM {table} ORDER BY ID";
                await using var idReader = await idQuery.ExecuteReaderAsync();
                while (await idReader.ReadAsync()) ids.Add(idReader.GetInt32(0));
            }
            Assert.True(ids.SequenceEqual(Enumerable.Range(1, 275)), $"Imported {ids.Count} ids: {string.Join(',', ids)}");

            await using (var check = connection.CreateCommand())
            {
                check.CommandText = $"SELECT COUNT(*), COUNT(TXT), COUNT(AMOUNT), COUNT(PAYLOAD), SUM(ID), SUM(AMOUNT), SUM(LENGTH(PAYLOAD)) FROM {table}";
                await using var rows = await check.ExecuteReaderAsync();
                Assert.True(await rows.ReadAsync());
                Assert.Equal(275, rows.GetInt32(0));
                Assert.Equal(236, rows.GetInt32(1));
                Assert.Equal(220, rows.GetInt32(2));
                Assert.Equal(250, rows.GetInt32(3));
                Assert.Equal(37_950, rows.GetInt32(4));
                Assert.Equal(60_500.00m, rows.GetDecimal(5));
                Assert.Equal(ExpectedPayloadLengthSum(275), rows.GetInt64(6));
            }

            await using var select = connection.CreateCommand();
            select.CommandText = $"SELECT ID, TXT, AMOUNT, PAYLOAD FROM {table} ORDER BY ID";
            await using var imported = await select.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
            for (var id = 1; id <= 275; id++)
            {
                Assert.True(await imported.ReadAsync());
                Assert.Equal(id, imported.GetInt32(0));
                if (id % 7 == 0) Assert.True(await imported.IsDBNullAsync(1));
                else Assert.Equal($"row-{id:D4}", imported.GetString(1));
                if (id % 5 == 0) Assert.True(await imported.IsDBNullAsync(2));
                else Assert.Equal(id * 2m, imported.GetDecimal(2));
                if (id % 11 == 0) Assert.True(await imported.IsDBNullAsync(3));
                else
                {
                    var expected = PayloadFor(id);
                    var actual = new byte[expected.Length];
                    Assert.Equal(expected.Length, imported.GetBytes(3, 0, actual, 0, actual.Length));
                    Assert.Equal(SHA256.HashData(expected), SHA256.HashData(actual));
                }
            }
            Assert.False(await imported.ReadAsync());
        }
        finally
        {
            if (created) await TryDropAsync(connection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetImporterStreamsLargeBlobClobDbClobAndXmlInputs()
    {
        var table = $"CS_IMP_LOB_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, B BLOB(2M), C CLOB(2M), D DBCLOB(2M), X XML)");
            created = true;

            var blobs = Enumerable.Range(0, 2).Select(_ => new byte[512 * 1024 + 17]).ToArray();
            foreach (var blob in blobs) RandomNumberGenerator.Fill(blob);
            var clobText = string.Concat(Enumerable.Repeat("CLOB café 東京 🚀 ", 5_000));
            var dbClobText = string.Concat(Enumerable.Repeat("DBCLOB łódź 東京 🚀 ", 5_000));
            var xmlText = $"<root><message>XML café 東京 🚀</message><body>{new string('x', 100_000)}</body></root>";
            var clobBytes = System.Text.Encoding.UTF8.GetBytes(clobText);
            var dbClobBytes = System.Text.Encoding.UTF8.GetBytes(dbClobText);
            var xmlBytes = System.Text.Encoding.UTF8.GetBytes(xmlText);
            var blobSources = blobs.Select(bytes => new MemoryStream(bytes, writable: false)).ToArray();
            var clobSources = Enumerable.Range(0, 2).Select(_ => new MemoryStream(clobBytes, writable: false)).ToArray();
            var dbClobSources = Enumerable.Range(0, 2).Select(_ => new MemoryStream(dbClobBytes, writable: false)).ToArray();
            var xmlSources = Enumerable.Range(0, 2).Select(_ => new MemoryStream(xmlBytes, writable: false)).ToArray();

            using var source = new DataTable();
            source.Columns.Add("ID", typeof(int));
            source.Columns.Add("B", typeof(object));
            source.Columns.Add("C", typeof(object));
            source.Columns.Add("D", typeof(object));
            source.Columns.Add("X", typeof(object));
            for (var index = 0; index < 2; index++)
                source.Rows.Add(
                    index + 1,
                    new Db2BlobStream(blobSources[index], blobs[index].Length),
                    new Db2ClobStream(clobSources[index], clobBytes.Length),
                    new Db2DbClobStream(dbClobSources[index], dbClobBytes.Length),
                    new Db2XmlStream(xmlSources[index], xmlBytes.Length));

            using var reader = source.CreateDataReader();
            var result = await Db2AdoImporter.ImportAsync(
                connection, reader, table,
                options: new Db2AdoImportOptions { BatchSize = 8, NotifyAfterRows = 10 });
            Assert.Equal(2, result.RowsInserted);
            Assert.Equal(2, result.RowsCommitted);
            Assert.All(blobSources, stream => Assert.Equal(stream.Length, stream.Position));
            Assert.All(clobSources, stream => Assert.Equal(stream.Length, stream.Position));
            Assert.All(dbClobSources, stream => Assert.Equal(stream.Length, stream.Position));
            Assert.All(xmlSources, stream => Assert.Equal(stream.Length, stream.Position));

            await using (var select = connection.CreateCommand())
            {
                select.CommandText = $"SELECT ID, B, C, D, XMLSERIALIZE(X AS CLOB(2M)) FROM {table} ORDER BY ID";
                await using var imported = await select.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                for (var index = 0; index < 2; index++)
                {
                    Assert.True(await imported.ReadAsync());
                    Assert.Equal(index + 1, imported.GetInt32(0));
                    var bytes = new byte[blobs[index].Length];
                    Assert.Equal(bytes.Length, imported.GetBytes(1, 0, bytes, 0, bytes.Length));
                    Assert.Equal(SHA256.HashData(blobs[index]), SHA256.HashData(bytes));
                    Assert.Equal(clobText, imported.GetString(2));
                    Assert.Equal(dbClobText, imported.GetString(3));
                    Assert.Equal(xmlText, imported.GetString(4));
                }
                Assert.False(await imported.ReadAsync());
            }
        }
        finally
        {
            if (created) await TryDropAsync(connection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetImporterStreamsProgressiveSourceLobBeforeAdvancingTheReader()
    {
        var suffix = Guid.NewGuid().ToString("N")[..18].ToUpperInvariant();
        var sourceTable = $"CS_IMP_SRC_{suffix}";
        var targetTable = $"CS_IMP_DST_{suffix}";
        await using var sourceConnection = new Db2AdoConnection(Dsn);
        await using var destinationConnection = new Db2AdoConnection(Dsn);
        await sourceConnection.OpenAsync();
        await destinationConnection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(sourceConnection);
        var sourceCreated = false;
        var targetCreated = false;
        try
        {
            await ExecuteAsync(sourceConnection, $"CREATE TABLE {schema}.{sourceTable} (ID INTEGER NOT NULL PRIMARY KEY, B BLOB(4M), C CLOB(4M), D DBCLOB(4M), X XML)");
            sourceCreated = true;
            await ExecuteAsync(destinationConnection, $"CREATE TABLE {schema}.{targetTable} (ID INTEGER NOT NULL PRIMARY KEY, B BLOB(4M), C CLOB(4M), D DBCLOB(4M), X XML)");
            targetCreated = true;
            var checkpointBlob = new byte[] { 1, 2, 3, 0xff };
            await using (var payload = new MemoryStream(checkpointBlob, writable: false))
            await using (var insert = sourceConnection.CreateCommand())
            {
                insert.CommandText = $"INSERT INTO {sourceTable} VALUES (1, ?, CAST('checkpoint clob café 東京 🚀' AS CLOB(64)), CAST('checkpoint dbclob łódź 東京 🚀' AS DBCLOB(64)), XMLPARSE(DOCUMENT '<root>checkpoint café 東京 🚀</root>'))";
                insert.Parameters.Add(new Db2AdoParameter { Value = new Db2BlobStream(payload, checkpointBlob.LongLength) });
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }

            var expected = new byte[3_000_000];
            for (var index = 0; index < expected.Length; index++) expected[index] = (byte)(index * 31);
            await using (var payload = new MemoryStream(expected, writable: false))
            await using (var insert = sourceConnection.CreateCommand())
            {
                insert.CommandText = $"INSERT INTO {sourceTable} VALUES (2, ?, CAST('later clob café 東京 🚀' AS CLOB(64)), CAST('later dbclob łódź 東京 🚀' AS DBCLOB(64)), XMLPARSE(DOCUMENT '<root>later café 東京 🚀</root>'))";
                insert.Parameters.Add(new Db2AdoParameter { Value = new Db2BlobStream(payload, expected.LongLength) });
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }

            // GetValue/GetFieldValue<object> intentionally materialize a LOB. GetStream is
            // the ADO contract that can prove the locator remains progressive.
            await using (var probeCommand = sourceConnection.CreateCommand())
            {
                probeCommand.CommandText = $"SELECT ID, B, C, D, X FROM {sourceTable} WHERE ID = 2";
                await using var probe = await probeCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                Assert.True(await probe.ReadAsync());
                Assert.Equal(2, probe.GetInt32(0));
                await using var locatorStream = probe.GetStream(1);
                Assert.IsType<Db2ProgressiveLobStream>(locatorStream);
                var probeBuffer = new byte[64 * 1024];
                while (await locatorStream.ReadAsync(probeBuffer) > 0) { }
                Assert.False(await probe.ReadAsync());
            }

            await using (var select = sourceConnection.CreateCommand())
            {
                select.CommandText = $"SELECT ID, B, C, D, X FROM {sourceTable} ORDER BY ID";
                await using var sourceReader = await select.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                var imported = await Db2AdoImporter.ImportAsync(
                    destinationConnection,
                    sourceReader,
                    targetTable,
                    options: new Db2AdoImportOptions { BatchSize = 8, MaxBatchBytes = 8 * 1024 * 1024 });
                Assert.Equal(2, imported.RowsRead);
                Assert.Equal(2, imported.RowsInserted);
                Assert.Equal(2, imported.RowsCommitted);
            }

            await using (var verify = destinationConnection.CreateCommand())
            {
                verify.CommandText = $"SELECT ID, B, C, D, XMLSERIALIZE(X AS CLOB(4M)) FROM {targetTable} ORDER BY ID";
                await using var result = await verify.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                Assert.True(await result.ReadAsync());
                Assert.Equal(1, result.GetInt32(0));
                var checkpointReadback = new byte[checkpointBlob.Length];
                Assert.Equal(checkpointReadback.Length, result.GetBytes(1, 0, checkpointReadback, 0, checkpointReadback.Length));
                Assert.Equal(checkpointBlob, checkpointReadback);
                Assert.Equal("checkpoint clob café 東京 🚀", result.GetString(2));
                Assert.Equal("checkpoint dbclob łódź 東京 🚀", result.GetString(3));
                Assert.Equal("<root>checkpoint café 東京 🚀</root>", result.GetString(4));
                Assert.True(await result.ReadAsync());
                Assert.Equal(2, result.GetInt32(0));
                Assert.Equal("later clob café 東京 🚀", result.GetString(2));
                Assert.Equal("later dbclob łódź 東京 🚀", result.GetString(3));
                Assert.Equal("<root>later café 東京 🚀</root>", result.GetString(4));
                await using var stream = result.GetStream(1);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long length = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    length += read;
                }
                Assert.Equal(expected.LongLength, length);
                Assert.Equal(SHA256.HashData(expected), hash.GetHashAndReset());
                Assert.Equal("<root>later café 東京 🚀</root>", result.GetString(4));
                Assert.False(await result.ReadAsync());
            }
        }
        finally
        {
            if (targetCreated) await TryDropAsync(destinationConnection, targetTable);
            if (sourceCreated) await TryDropAsync(sourceConnection, sourceTable);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetImporterCancellationDuringProgressiveSourceLocatorPreservesEarlierCheckpoint()
    {
        var suffix = Guid.NewGuid().ToString("N")[..18].ToUpperInvariant();
        var targetTable = $"CS_IMP_DST_CANCEL_{suffix}";
        await using var sourceConnection = new Db2AdoConnection(Dsn);
        await using var destinationConnection = new Db2AdoConnection(Dsn);
        await sourceConnection.OpenAsync();
        await destinationConnection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(destinationConnection);
        using var cancellation = new CancellationTokenSource();
        var targetCreated = false;
        Db2AdoDataReader? sourceReader = null;
        try
        {
            await ExecuteAsync(destinationConnection, $"CREATE TABLE {schema}.{targetTable} (ID INTEGER NOT NULL PRIMARY KEY, B BLOB(4M))");
            targetCreated = true;

            var checkpointBlob = new byte[] { 1, 2, 3, 0xff };
            const int secondBlobLength = 3_000_000;
            var secondBlob = new byte[secondBlobLength];
            for (var index = 0; index < secondBlob.Length; index++) secondBlob[index] = (byte)(index * 31);

            await using (var select = sourceConnection.CreateCommand())
            await using (var firstPayload = new MemoryStream(checkpointBlob, writable: false))
            await using (var secondPayload = new MemoryStream(secondBlob, writable: false))
            {
                select.CommandText = "SELECT ID, B FROM (VALUES (1, CAST(? AS BLOB(4M))), (2, CAST(? AS BLOB(4M)))) AS S(ID, B)";
                select.Parameters.Add(new Db2AdoParameter { Value = new Db2BlobStream(firstPayload, checkpointBlob.LongLength) });
                select.Parameters.Add(new Db2AdoParameter { Value = new Db2BlobStream(secondPayload, secondBlob.LongLength) });
                sourceReader = (Db2AdoDataReader)await select.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                var instrumentedSource = new ProgressiveSourceDataReader(sourceReader, cancellation);
                var progress = new List<Db2AdoImportProgress>();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Db2AdoImporter.ImportAsync(
                    destinationConnection,
                    instrumentedSource,
                    targetTable,
                    destinationColumns: ["ID", "B"],
                    options: new Db2AdoImportOptions
                    {
                        BatchSize = 1,
                        CommitEveryRows = 1,
                        NotifyAfterRows = 1,
                        MaxBatchBytes = 64 * 1024,
                    },
                    progress: progress.Add,
                    cancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(45)));

                Assert.True(instrumentedSource.SecondLocatorReadStarted.IsCompleted,
                    "cancellation must happen while the second progressive source LOB is being consumed");
                Assert.InRange(instrumentedSource.SecondLocatorBytesRead, 1_048_576, secondBlobLength - 1);
                Assert.Single(progress);
                Assert.Equal(1, progress[0].RowsCommitted);
                Assert.Equal(1, progress[0].RowsInserted);
                try { await sourceReader.DisposeAsync(); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                sourceReader = null;
            }

            // This wrapper is a generic IDataReader and explicitly returns a caller-owned
            // Db2BlobStream, so it exercises direct EXTDTA cancellation rather than the
            // Db2AdoDataReader progressive-locator staging path.
            if (!(sourceConnection.CurrentSession?.IsClosed ?? true))
            {
                // A confirmed SQLCODE -952 ends the source query cleanly, so the
                // session may remain open. Prove it is reusable before accepting it.
                Assert.Equal(1, await ScalarIntAsync(sourceConnection, "VALUES 1"));
            }
            Assert.True(destinationConnection.CurrentSession?.IsClosed ?? true,
                "a destination session with a partially sent EXTDTA value must be discarded");

            await using var verifier = new Db2AdoConnection(Dsn);
            await verifier.OpenAsync();
            Assert.Equal(1, await ScalarIntAsync(verifier, $"SELECT COUNT(*) FROM {targetTable}"));
            Assert.Equal(1, await ScalarIntAsync(verifier, $"SELECT ID FROM {targetTable}"));
            await using var verify = verifier.CreateCommand();
            verify.CommandText = $"SELECT B FROM {targetTable} WHERE ID = 1";
            await using var result = await verify.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
            Assert.True(await result.ReadAsync());
            var readback = new byte[checkpointBlob.Length];
            Assert.Equal(readback.Length, result.GetBytes(0, 0, readback, 0, readback.Length));
            Assert.Equal(checkpointBlob, readback);
        }
        finally
        {
            if (sourceReader is not null)
            {
                try { await sourceReader.DisposeAsync(); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            if (targetCreated)
            {
                await using var cleanup = new Db2AdoConnection(Dsn);
                await cleanup.OpenAsync();
                if (targetCreated) await TryDropAsync(cleanup, targetTable);
            }
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetImporterRollsBackAtomicallyAndPinpointsTheBadSourceRow()
    {
        var table = $"CS_IMP_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY)");
            created = true;
            using var source = CreateIdSource(1, 2, 3, 3, 4);
            using var reader = source.CreateDataReader();

            var error = await Assert.ThrowsAsync<Db2AdoImportException>(() => Db2AdoImporter.ImportAsync(
                connection, reader, table,
                options: new Db2AdoImportOptions { BatchSize = 4, NotifyAfterRows = 100 }));

            Assert.Equal(4, error.RowNumber);
            Assert.Equal(1, error.FirstRowNumber);
            Assert.Equal(4, error.LastRowNumber);
            Assert.Equal(0, error.RowsCommitted);
            Assert.Equal("23505", error.SqlState);
            Assert.Equal(0, await ScalarIntAsync(connection, $"SELECT COUNT(*) FROM {table}"));
        }
        finally
        {
            if (created) await TryDropAsync(connection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetImporterPinpointsLocalTypeConversionFailuresBeforeWireExecution()
    {
        var table = $"CS_IMP_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY)");
            created = true;
            using var source = new DataTable();
            source.Columns.Add("ID", typeof(object));
            source.Rows.Add(1);
            source.Rows.Add(2);
            source.Rows.Add("not-an-integer");
            source.Rows.Add(4);
            using var reader = source.CreateDataReader();

            var error = await Assert.ThrowsAsync<Db2AdoImportException>(() => Db2AdoImporter.ImportAsync(
                connection, reader, table,
                options: new Db2AdoImportOptions { BatchSize = 4, NotifyAfterRows = 100 }));

            Assert.Equal(3, error.RowNumber);
            Assert.Equal(Db2ErrorKind.ParameterConversion, error.ErrorKind);
            Assert.Null(error.SqlState);
            Assert.Equal(0, error.RowsCommitted);
            Assert.Equal(0, await ScalarIntAsync(connection, $"SELECT COUNT(*) FROM {table}"));
        }
        finally
        {
            if (created) await TryDropAsync(connection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetImporterReportsDurableCommitIntervalsAfterALaterRowFailure()
    {
        var table = $"CS_IMP_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY)");
            created = true;
            using var source = CreateIdSource(1, 2, 3, 3, 4);
            using var reader = source.CreateDataReader();

            var error = await Assert.ThrowsAsync<Db2AdoImportException>(() => Db2AdoImporter.ImportAsync(
                connection, reader, table,
                options: new Db2AdoImportOptions { BatchSize = 4, CommitEveryRows = 2, NotifyAfterRows = 100 }));

            Assert.Equal(4, error.RowNumber);
            Assert.Equal(3, error.FirstRowNumber);
            Assert.Equal(4, error.LastRowNumber);
            Assert.Equal(2, error.RowsCommitted);
            Assert.Equal(2, await ScalarIntAsync(connection, $"SELECT COUNT(*) FROM {table}"));
        }
        finally
        {
            if (created) await TryDropAsync(connection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetImporterCancellationPreservesOnlyCompletedCommitIntervals()
    {
        var table = $"CS_IMP_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY)");
            created = true;
            using var source = CreateIdSource(Enumerable.Range(1, 100).ToArray());
            using var reader = source.CreateDataReader();
            using var cancellation = new CancellationTokenSource();

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Db2AdoImporter.ImportAsync(
                connection,
                reader,
                table,
                options: new Db2AdoImportOptions { BatchSize = 16, CommitEveryRows = 16, NotifyAfterRows = 16 },
                progress: update => { if (update.RowsCommitted >= 32) cancellation.Cancel(); },
                cancellationToken: cancellation.Token));

            Assert.NotNull(error);
            Assert.Equal(32, await ScalarIntAsync(connection, $"SELECT COUNT(*) FROM {table}"));
        }
        finally
        {
            if (created) await TryDropAsync(connection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetImporterCancellationDuringStreamedBlobPreservesEarlierCheckpoint()
    {
        var table = $"CS_IMP_LOBCANCEL_{Guid.NewGuid():N}"[..30].ToUpperInvariant();
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        using var cancellation = new CancellationTokenSource();
        using var blockedPayload = new CancellationGateStream(4_000_000);
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, B BLOB(4M))");
            created = true;
            using var firstPayload = new MemoryStream(new byte[] { 0x01, 0x02, 0x03, 0xff }, writable: false);
            using var source = new DataTable();
            source.Columns.Add("ID", typeof(int));
            source.Columns.Add("B", typeof(object));
            source.Rows.Add(1, new Db2BlobStream(firstPayload, firstPayload.Length));
            source.Rows.Add(2, new Db2BlobStream(blockedPayload, blockedPayload.Length));
            using var reader = source.CreateDataReader();
            var progress = new List<Db2AdoImportProgress>();

            var cancelAfterPartialLob = Task.Run(async () =>
            {
                try
                {
                    await blockedPayload.SecondReadStarted.WaitAsync(TimeSpan.FromSeconds(15));
                    cancellation.Cancel();
                    return true;
                }
                catch (TimeoutException)
                {
                    cancellation.Cancel();
                    return false;
                }
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Db2AdoImporter.ImportAsync(
                connection,
                reader,
                table,
                options: new Db2AdoImportOptions { BatchSize = 1, CommitEveryRows = 1, NotifyAfterRows = 1 },
                progress: progress.Add,
                cancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.True(await cancelAfterPartialLob.WaitAsync(TimeSpan.FromSeconds(5)), "the second streamed BLOB must be in flight before cancellation");
            Assert.InRange(blockedPayload.BytesReturned, 1, checked((int)blockedPayload.Length - 1));
            Assert.Single(progress);
            Assert.Equal(1, progress[0].RowsCommitted);
            await using var verifier = new Db2AdoConnection(Dsn);
            await verifier.OpenAsync();
            Assert.Equal(1, await ScalarIntAsync(verifier, $"SELECT COUNT(*) FROM {table}"));
            Assert.Equal(1, await ScalarIntAsync(verifier, $"SELECT ID FROM {table}"));
            Assert.Equal(29, await ScalarIntAsync(verifier, "VALUES 29"));
            await TryDropAsync(verifier, table);
            created = false;
        }
        finally
        {
            cancellation.Cancel();
            if (created)
            {
                if (connection.State == ConnectionState.Open)
                    await TryDropAsync(connection, table);
                else
                {
                    await using var cleanup = new Db2AdoConnection(Dsn);
                    await cleanup.OpenAsync();
                    await TryDropAsync(cleanup, table);
                }
            }
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetImporterCancellationInterruptsAnActiveInsertAndRollsBackItsTransaction()
    {
        var table = $"CS_IMP_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var locker = new Db2AdoConnection(Dsn);
        await using var importer = new Db2AdoConnection(Dsn);
        await locker.OpenAsync();
        await importer.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(locker);
        var created = false;
        try
        {
            await ExecuteAsync(locker, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, V INTEGER NOT NULL)");
            created = true;
            await ExecuteAsync(locker, $"INSERT INTO {table} VALUES (1, 0)");
            await using var lockTransaction = (Db2AdoTransaction)await locker.BeginTransactionAsync();
            await using (var update = locker.CreateCommand())
            {
                update.Transaction = lockTransaction;
                update.CommandText = $"UPDATE {table} SET V = 1 WHERE ID = 1";
                Assert.Equal(1, await update.ExecuteNonQueryAsync());
            }

            using var source = new DataTable();
            source.Columns.Add("ID", typeof(int));
            source.Columns.Add("V", typeof(int));
            source.Rows.Add(1, 2);
            using var reader = source.CreateDataReader();
            using var cancellation = new CancellationTokenSource();
            var import = Db2AdoImporter.ImportAsync(
                importer, reader, table,
                options: new Db2AdoImportOptions { BatchSize = 1 },
                cancellationToken: cancellation.Token);

            await Task.Delay(500);
            Assert.False(import.IsCompleted, "The duplicate-key insert should be waiting for the lock transaction.");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import.WaitAsync(TimeSpan.FromSeconds(20)));
            await lockTransaction.RollbackAsync();

            Assert.Equal(1, await ScalarIntAsync(importer, $"SELECT COUNT(*) FROM {table}"));
            Assert.Equal(0, await ScalarIntAsync(importer, $"SELECT V FROM {table} WHERE ID = 1"));
        }
        finally
        {
            if (created) await TryDropAsync(locker, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetImporterCanParticipateInAndLeaveTheCallerTransactionOpen()
    {
        var table = $"CS_IMP_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY)");
            created = true;
            await using var transaction = (Db2AdoTransaction)await connection.BeginTransactionAsync();
            using var source = CreateIdSource(10, 20, 30);
            using var reader = source.CreateDataReader();

            var result = await Db2AdoImporter.ImportAsync(
                connection,
                reader,
                table,
                options: new Db2AdoImportOptions { BatchSize = 2 },
                transaction: transaction);

            Assert.Equal(3, result.RowsInserted);
            Assert.Equal(0, result.RowsCommitted);
            Assert.Equal(3, await ScalarIntAsync(connection, $"SELECT COUNT(*) FROM {table}", transaction));
            await transaction.RollbackAsync();
            Assert.Equal(0, await ScalarIntAsync(connection, $"SELECT COUNT(*) FROM {table}"));
        }
        finally
        {
            if (created) await TryDropAsync(connection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetReadsParameterizedRowsAndStreamsMultipleReplyBlocks()
    {
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        Assert.Equal(ConnectionState.Open, connection.State);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "VALUES (CAST(? AS INTEGER), CAST(? AS VARCHAR(20)), CAST(? AS INTEGER))";
            command.Parameters.Add(new Db2AdoParameter { ParameterName = "@id", DbType = DbType.Int32, Value = 73 });
            command.Parameters.Add(new Db2AdoParameter { ParameterName = "@text", DbType = DbType.String, Value = "ADO.NET" });
            command.Parameters.Add(new Db2AdoParameter { ParameterName = "@nullable", DbType = DbType.Int32, Value = DBNull.Value });
            await using var reader = await command.ExecuteReaderAsync();
            Assert.Equal(3, reader.FieldCount);
            Assert.True(reader.HasRows);
            Assert.True(await reader.ReadAsync());
            Assert.Equal(73, reader.GetInt32(0));
            Assert.Equal("ADO.NET", reader.GetString(1));
            Assert.True(await reader.IsDBNullAsync(2));
            Assert.False(await reader.ReadAsync());
        }

        var values = string.Join(',', Enumerable.Range(1, 130).Select(value => $"({value})"));
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"VALUES {values}";
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
            var count = 0;
            var sum = 0;
            while (await reader.ReadAsync())
            {
                count++;
                sum += reader.GetInt32(0);
            }
            Assert.Equal(130, count);
            Assert.Equal(8515, sum);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "VALUES 74";
            Assert.Equal(74, await command.ExecuteScalarAsync());
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "VALUES HEX(CAST(? AS VARCHAR(20)))";
            command.Parameters.Add(new Db2AdoParameter { DbType = DbType.Binary, Value = new byte[] { 0xe9 } });
            Assert.Equal("E9", await command.ExecuteScalarAsync());
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetSchemaOnlyDescribesSqlWithoutExecutingDml()
    {
        var table = $"CS_ADO_SCH_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, VALUE VARCHAR(32))");
            created = true;

            await using (var insert = connection.CreateCommand())
            {
                insert.CommandText = $"INSERT INTO {table} (ID, VALUE) VALUES (1, 'must not be inserted')";
                await using var reader = await insert.ExecuteReaderAsync(CommandBehavior.SchemaOnly);
                Assert.Equal(0, reader.FieldCount);
                Assert.False(reader.HasRows);
                Assert.Null(reader.GetSchemaTable());
                Assert.False(await reader.ReadAsync());
                Assert.False(await reader.NextResultAsync());
                Assert.Null(reader.GetSchemaTable());
            }

            Assert.Equal(0, await ScalarIntAsync(connection, $"SELECT COUNT(*) FROM {table}"));

            await using (var select = connection.CreateCommand())
            {
                select.CommandText = $"SELECT ID, VALUE FROM {table}";
                await using var reader = await select.ExecuteReaderAsync(CommandBehavior.SchemaOnly);
                Assert.Equal(2, reader.FieldCount);
                Assert.Equal("ID", reader.GetName(0));
                Assert.Equal("VALUE", reader.GetName(1));
                Assert.Equal(2, reader.GetSchemaTable()!.Rows.Count);
                Assert.False(reader.HasRows);
                Assert.False(await reader.ReadAsync());
            }
        }
        finally
        {
            if (created) await TryDropAsync(connection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetExecutesDmlTransactionsAndStreamedBlobParameters()
    {
        var table = $"CS_ADO_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var created = false;
        try
        {
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, TXT VARCHAR(80), B BLOB(2M))";
                await create.ExecuteNonQueryAsync();
                created = true;
            }

            var payload = new byte[512 * 1024 + 17];
            RandomNumberGenerator.Fill(payload);
            await using (var transaction = (Db2AdoTransaction)await connection.BeginTransactionAsync())
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = $"INSERT INTO {table}(ID, TXT, B) VALUES(?, ?, ?)";
                insert.Parameters.Add(new Db2AdoParameter { DbType = DbType.Int32, Value = 100 });
                insert.Parameters.Add(new Db2AdoParameter { DbType = DbType.String, Size = 20, Value = "streamed" });
                var source = new MemoryStream(payload, writable: false);
                insert.Parameters.Add(new Db2AdoParameter { DbType = DbType.Binary, Size = payload.Length, Value = source, LeaveOpen = true });
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
                Assert.True(source.CanRead);
                await transaction.CommitAsync();
            }

            await using (var transaction = (Db2AdoTransaction)await connection.BeginTransactionAsync())
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = $"INSERT INTO {table}(ID, TXT) VALUES(?, ?)";
                insert.Parameters.Add(new Db2AdoParameter { DbType = DbType.Int32, Value = 101 });
                insert.Parameters.Add(new Db2AdoParameter { DbType = DbType.String, Value = "rolled back" });
                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
                await transaction.RollbackAsync();
            }

            await using (var select = connection.CreateCommand())
            {
                select.CommandText = $"SELECT ID, TXT, B FROM {table} WHERE ID = ?";
                select.Parameters.Add(new Db2AdoParameter { DbType = DbType.Int32, Value = 100 });
                await using var reader = await select.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                Assert.True(await reader.ReadAsync());
                Assert.Equal(100, reader.GetInt32(0));
                Assert.Equal("streamed", reader.GetString(1));
                var bytes = new byte[payload.Length];
                Assert.Equal(payload.Length, reader.GetBytes(2, 0, bytes, 0, bytes.Length));
                Assert.Equal(SHA256.HashData(payload), SHA256.HashData(bytes));
                Assert.False(await reader.ReadAsync());
            }

            await using (var count = connection.CreateCommand())
            {
                count.CommandText = $"SELECT COUNT(*) FROM {table} WHERE ID = 101";
                Assert.Equal(0, await count.ExecuteScalarAsync());
            }

            await using (var transaction = (Db2AdoTransaction)await connection.BeginTransactionAsync())
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table} WHERE ID = ?";
                delete.Parameters.Add(new Db2AdoParameter { DbType = DbType.Int32, Value = 100 });
                Assert.Equal(1, await delete.ExecuteNonQueryAsync());
                await transaction.CommitAsync();
            }
        }
        finally
        {
            if (created)
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = $"DROP TABLE {schema}.{table}";
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetExecuteScalarDistinguishesSqlNullFromNoRows()
    {
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = "VALUES CAST(NULL AS INTEGER)";
        Assert.Same(DBNull.Value, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT 1 FROM SYSIBM.SYSDUMMY1 WHERE 1 = 0";
        Assert.Null(await command.ExecuteScalarAsync());
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetExecuteNonQuerySupportsNoArgumentCallWithoutOutputParameters()
    {
        var table = $"CS_ADO_CALL_{Guid.NewGuid():N}"[..28].ToUpperInvariant();
        var procedure = $"CS_ADO_NOP_{Guid.NewGuid():N}"[..27].ToUpperInvariant();
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        var tableCreated = false;
        var procedureCreated = false;
        try
        {
            await ExecuteAsync(connection, $"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY, VALUE INTEGER NOT NULL)");
            tableCreated = true;
            await ExecuteAsync(connection, $"INSERT INTO {table} VALUES (1, 0)");
            await ExecuteAsync(connection, $"CREATE PROCEDURE {schema}.{procedure}() LANGUAGE SQL MODIFIES SQL DATA BEGIN ATOMIC UPDATE {table} SET VALUE = VALUE + 1 WHERE ID = 1; END");
            procedureCreated = true;

            await using (var call = connection.CreateCommand())
            {
                call.CommandText = $"CALL {procedure}()";
                await call.ExecuteNonQueryAsync();
            }

            Assert.Equal(1, await ScalarIntAsync(connection, $"SELECT VALUE FROM {table} WHERE ID = 1"));
        }
        finally
        {
            if (procedureCreated)
            {
                await using var drop = connection.CreateCommand();
                drop.CommandText = $"DROP PROCEDURE {schema}.{procedure}";
                await drop.ExecuteNonQueryAsync();
            }
            if (tableCreated) await TryDropAsync(connection, table);
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetStoredProcedureReturnsOutputParametersAndMultipleResultSets()
    {
        var procedure = $"CS_ADO_OUT_{Guid.NewGuid():N}"[..27].ToUpperInvariant();
        var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        Db2AdoCommand? command = null;
        var created = false;
        Exception? primaryFailure = null;
        try
        {
            await using (var verifyLiteral = connection.CreateCommand())
            {
                verifyLiteral.CommandText = "SELECT HEX(BLOB(X'007F80FF')) FROM SYSIBM.SYSDUMMY1";
                Assert.Equal("007F80FF", await verifyLiteral.ExecuteScalarAsync());
            }

            await using (var create = connection.CreateCommand())
            {
                create.CommandText = $$"""
                    CREATE PROCEDURE {{schema}}.{{procedure}} (IN P INTEGER, OUT DOUBLED INTEGER, INOUT LABEL VARCHAR(40), OUT CONTENT BLOB(100))
                    LANGUAGE SQL DYNAMIC RESULT SETS 3
                    BEGIN
                        DECLARE C1 CURSOR WITH RETURN TO CALLER FOR SELECT P AS VALUE FROM SYSIBM.SYSDUMMY1;
                        DECLARE C2 CURSOR WITH RETURN TO CALLER FOR SELECT N, BLOB(X'007F80FF') AS PAYLOAD FROM (VALUES (P), (P + 1)) AS T(N);
                        DECLARE C3 CURSOR WITH RETURN TO CALLER FOR SELECT CAST(0 AS INTEGER) AS EMPTY_VALUE FROM SYSIBM.SYSDUMMY1 WHERE 1 = 0;
                        SET DOUBLED = P * 2;
                        SET LABEL = 'DONE: ' || LABEL;
                        SET CONTENT = BLOB(X'007F80FF');
                        OPEN C1;
                        OPEN C2;
                        OPEN C3;
                    END
                    """;
                await create.ExecuteNonQueryAsync();
                created = true;
            }

            command = (Db2AdoCommand)connection.CreateCommand();
            command.ProcedureProtocolTrace = message => Console.WriteLine($"D2 protocol: {message}");
            command.CommandTimeout = 10;
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = procedure;
            command.Parameters.Add(new Db2AdoParameter { ParameterName = "P", DbType = DbType.Int32, Value = 21 });
            command.Parameters.Add(new Db2AdoParameter { ParameterName = "DOUBLED", DbType = DbType.Int32, Direction = ParameterDirection.Output });
            command.Parameters.Add(new Db2AdoParameter { ParameterName = "LABEL", DbType = DbType.String, Size = 40, Direction = ParameterDirection.InputOutput, Value = "source" });
            command.Parameters.Add(new Db2AdoParameter { ParameterName = "CONTENT", DbType = DbType.Binary, Size = 100, Direction = ParameterDirection.Output });
            Console.WriteLine("D2 live: execute CALL");
            var reader = await command.ExecuteReaderAsync();
            Assert.Null(command.Parameters["DOUBLED"].Value);
            Assert.Equal("source", command.Parameters["LABEL"].Value);
            Assert.Null(command.Parameters["CONTENT"].Value);
            Assert.Equal(1, reader.FieldCount);
            Assert.Equal("VALUE", reader.GetName(0));
            Console.WriteLine("D2 live: read cursor 1");
            Assert.True(await reader.ReadAsync());
            Assert.Equal(21, reader.GetInt32(0));
            Console.WriteLine("D2 live: advance to cursor 2");
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal(21, reader.GetInt32(0));
            Assert.Equal(2, reader.FieldCount);
            Assert.Equal("PAYLOAD", reader.GetName(1));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(22, reader.GetInt32(0));
            Assert.Equal(new byte[] { 0, 0x7f, 0x80, 0xff }, Assert.IsType<byte[]>(reader.GetValue(1)));
            Console.WriteLine("D2 live: advance to cursor 3");
            Assert.True(await reader.NextResultAsync());
            Assert.Equal(1, reader.FieldCount);
            Assert.False(await reader.ReadAsync());
            Console.WriteLine("D2 live: reach terminal result set");
            Assert.False(await reader.NextResultAsync());
            Assert.Null(command.Parameters["DOUBLED"].Value);
            Assert.Equal("source", command.Parameters["LABEL"].Value);
            Console.WriteLine("D2 live: dispose reader");
            await reader.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(12));
            Console.WriteLine("D2 live: reader disposed");
            Assert.True(reader.IsClosed);
            Assert.False(((Db2AdoCommand)command).HasActiveReader);
            Assert.False(((Db2AdoCommand)command).HasActiveOperation);
            Assert.Equal(42, command.Parameters["DOUBLED"].Value);
            Assert.Equal("DONE: source", command.Parameters["LABEL"].Value);
            var outputBlob = command.Parameters["CONTENT"].Value;
            if (outputBlob is byte[] bytes)
            {
                Assert.Equal(new byte[] { 0, 0x7f, 0x80, 0xff }, bytes);
            }
            else
            {
                var locator = Assert.IsType<Db2LobValue>(outputBlob);
                Assert.False(locator.IsText);
                Assert.Equal(4, locator.ByteLength);
                using var stream = locator.OpenReadStream();
                using var value = new MemoryStream();
                stream.CopyTo(value);
                Assert.Equal(new byte[] { 0, 0x7f, 0x80, 0xff }, value.ToArray());
            }

            command.Parameters["P"].Value = 22;
            command.Parameters["DOUBLED"].Value = null;
            command.Parameters["LABEL"].Value = "single-result";
            command.Parameters["CONTENT"].Value = null;
            await using (var singleResult = await command.ExecuteReaderAsync(CommandBehavior.SingleResult))
            {
                Assert.True(await singleResult.ReadAsync());
                Assert.Equal(22, singleResult.GetInt32(0));
                Assert.False(await singleResult.NextResultAsync());
                Assert.Null(command.Parameters["DOUBLED"].Value);
            }
            Assert.Equal(44, command.Parameters["DOUBLED"].Value);
            Assert.Equal("DONE: single-result", command.Parameters["LABEL"].Value);

            command.Parameters["P"].Value = 23;
            command.Parameters["DOUBLED"].Value = null;
            command.Parameters["LABEL"].Value = "single-row";
            command.Parameters["CONTENT"].Value = null;
            await using (var singleRow = await command.ExecuteReaderAsync(CommandBehavior.SingleRow))
            {
                Assert.True(await singleRow.ReadAsync());
                Assert.Equal(23, singleRow.GetInt32(0));
                Assert.False(await singleRow.ReadAsync());
                Assert.Null(command.Parameters["DOUBLED"].Value);
            }
            Assert.Equal(46, command.Parameters["DOUBLED"].Value);
            Assert.Equal("DONE: single-row", command.Parameters["LABEL"].Value);
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            if (command is not null)
            {
                Console.WriteLine($"D2 cleanup: dispose command for {procedure}");
                try { await command.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"D2 cleanup command disposal failed for {procedure}: {error}");
                    cleanupFailures.Add(error);
                }
            }

            var originalSessionClosed = false;
            Console.WriteLine($"D2 cleanup: close original session for {procedure}");
            try
            {
                await connection.CloseAsync().WaitAsync(TimeSpan.FromSeconds(20));
                originalSessionClosed = true;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"D2 cleanup original session close failed for {procedure}: {error}");
                cleanupFailures.Add(error);
            }

            // Do not DROP from a second connection until the session that created and called
            // the procedure is known to be closed; otherwise a live cursor can block cleanup.
            if (created && originalSessionClosed)
            {
                Console.WriteLine($"D2 cleanup: drop procedure {procedure} on a fresh session");
                try
                {
                    await using var cleanup = new Db2AdoConnection(Dsn);
                    await cleanup.OpenAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    await using var drop = cleanup.CreateCommand();
                    drop.CommandTimeout = 10;
                    drop.CommandText = $"DROP PROCEDURE {schema}.{procedure}";
                    await drop.ExecuteNonQueryAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"D2 cleanup DROP failed for {procedure}: {error}");
                    cleanupFailures.Add(error);
                }
            }

            if (cleanupFailures.Count > 0)
            {
                var cleanupFailure = cleanupFailures.Count == 1
                    ? cleanupFailures[0]
                    : new AggregateException($"Cleanup of {procedure} encountered multiple failures.", cleanupFailures);
                if (primaryFailure is null) throw cleanupFailure;
                primaryFailure.Data["D2CleanupFailure"] = cleanupFailure;
            }
        }
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetPreservesDb2DecimalAndTimestampPrecisionBeyondClrLimits()
    {
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "VALUES (CAST('123456789012345678901.123456789' AS DECIMAL(31,9)), CAST('2024-02-29 23:59:58.123456789012' AS TIMESTAMP(12)))";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.Equal(2, reader.FieldCount);
        Assert.Equal(typeof(string), reader.GetFieldType(0));
        Assert.Equal(typeof(string), reader.GetFieldType(1));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("123456789012345678901.123456789", reader.GetString(0));
        Assert.Equal("2024-02-29-23.59.58.123456789012", reader.GetString(1));
        Assert.False(await reader.ReadAsync());
    }

    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AdoNetCancelAndCancellationTokenInterruptDb2AndKeepTheConnectionUsable()
    {
        await using var connection = new Db2AdoConnection(Dsn);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SYSCAT.COLUMNS a, SYSCAT.COLUMNS b, SYSCAT.COLUMNS c";
        var canceledByCommand = command.ExecuteScalarAsync();
        await Task.Delay(500);
        command.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledByCommand);
        Assert.Equal(ConnectionState.Open, connection.State);

        using var cancellation = new CancellationTokenSource();
        var canceledByToken = command.ExecuteScalarAsync(cancellation.Token);
        await Task.Delay(500);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledByToken);

        command.CommandText = "SELECT COUNT(*) FROM SYSCAT.COLUMNS a, SYSCAT.COLUMNS b, SYSCAT.COLUMNS c";
        command.CommandTimeout = 1;
        await Assert.ThrowsAsync<TimeoutException>(() => command.ExecuteScalarAsync());

        command.CommandTimeout = 30;
        command.CommandText = "VALUES 75";
        Assert.Equal(75, await command.ExecuteScalarAsync());
        await connection.CloseAsync();

        await using var reopened = new Db2AdoConnection(Dsn);
        await reopened.OpenAsync();
        await using var freshCommand = reopened.CreateCommand();
        freshCommand.CommandText = "VALUES 76";
        Assert.Equal(76, await freshCommand.ExecuteScalarAsync());
    }

    private static DataTable CreateImportSource(int rowCount)
    {
        var table = new DataTable();
        table.Columns.Add("ID", typeof(int));
        table.Columns.Add("TXT", typeof(string));
        table.Columns.Add("AMOUNT", typeof(decimal));
        table.Columns.Add("PAYLOAD", typeof(byte[]));
        for (var id = 1; id <= rowCount; id++)
            table.Rows.Add(
                id,
                id % 7 == 0 ? DBNull.Value : $"row-{id:D4}",
                id % 5 == 0 ? DBNull.Value : id * 2m,
                id % 11 == 0 ? DBNull.Value : PayloadFor(id));
        return table;
    }

    private static DataTable CreateIdSource(params int[] ids)
    {
        var table = new DataTable();
        table.Columns.Add("ID", typeof(int));
        foreach (var id in ids) table.Rows.Add(id);
        return table;
    }

    private static byte[] PayloadFor(int id)
    {
        var length = 17 + id % 23;
        return Enumerable.Range(0, length).Select(index => (byte)((id * 31 + index * 17) % 256)).ToArray();
    }

    private static long ExpectedPayloadLengthSum(int rowCount) => Enumerable.Range(1, rowCount)
        .Where(id => id % 11 != 0)
        .Sum(id => (long)PayloadFor(id).Length);

    private static async Task ExecuteAsync(Db2AdoConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarIntAsync(Db2AdoConnection connection, string sql, Db2AdoTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task TryDropAsync(Db2AdoConnection connection, string table)
    {
        var schema = await LiveFixtureSchema.ForAdoConnectionAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE {schema}.{table}";
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception error) when (LiveTestCleanup.IsMissingObjectException(error))
        {
            // Cleanup is idempotent if the failed operation already rolled the fixture back.
        }
    }

    private sealed class CancellationGateStream(long length) : Stream
    {
        private readonly TaskCompletionSource _secondReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;
        private long _position;

        public Task SecondReadStarted => _secondReadStarted.Task;
        public int BytesReturned { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (Interlocked.Increment(ref _readCount) != 1) throw new InvalidOperationException("The gate stream must be read asynchronously once before it blocks.");
            var count = Math.Min(buffer.Length, 64 * 1024);
            buffer[..count].Fill(0x5a);
            _position += count;
            BytesReturned += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) != 1)
            {
                _secondReadStarted.TrySetResult();
                return WaitForCancellationAsync(cancellationToken);
            }

            var count = Math.Min(buffer.Length, 64 * 1024);
            buffer.Span[..count].Fill(0x5a);
            _position += count;
            BytesReturned += count;
            return ValueTask.FromResult(count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private static async ValueTask<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class ProgressiveSourceDataReader(Db2AdoDataReader reader, CancellationTokenSource cancellation) : IDataReader
    {
        private readonly TaskCompletionSource _secondLocatorReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _secondLocatorBytesRead;

        public Task SecondLocatorReadStarted => _secondLocatorReadStarted.Task;
        public long SecondLocatorBytesRead => Interlocked.Read(ref _secondLocatorBytesRead);
        public int Depth => reader.Depth;
        public bool IsClosed => reader.IsClosed;
        public int RecordsAffected => reader.RecordsAffected;
        public int FieldCount => reader.FieldCount;
        public object this[int i] => GetValue(i);
        public object this[string name] => GetValue(GetOrdinal(name));

        public void Close() => reader.Close();
        public DataTable? GetSchemaTable() => reader.GetSchemaTable();
        public bool NextResult() => reader.NextResult();
        public bool Read() => reader.Read();
        public string GetName(int i) => reader.GetName(i);
        public string GetDataTypeName(int i) => reader.GetDataTypeName(i);
        public Type GetFieldType(int i) => reader.GetFieldType(i);
        public int GetOrdinal(string name) => reader.GetOrdinal(name);
        public bool GetBoolean(int i) => reader.GetBoolean(i);
        public byte GetByte(int i) => reader.GetByte(i);
        public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) => reader.GetBytes(i, fieldOffset, buffer, bufferoffset, length);
        public char GetChar(int i) => reader.GetChar(i);
        public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) => reader.GetChars(i, fieldoffset, buffer, bufferoffset, length);
        public Guid GetGuid(int i) => reader.GetGuid(i);
        public short GetInt16(int i) => reader.GetInt16(i);
        public int GetInt32(int i) => reader.GetInt32(i);
        public long GetInt64(int i) => reader.GetInt64(i);
        public float GetFloat(int i) => reader.GetFloat(i);
        public double GetDouble(int i) => reader.GetDouble(i);
        public string GetString(int i) => reader.GetString(i);
        public decimal GetDecimal(int i) => reader.GetDecimal(i);
        public DateTime GetDateTime(int i) => reader.GetDateTime(i);
        public IDataReader GetData(int i) => reader.GetData(i);
        public bool IsDBNull(int i) => reader.IsDBNull(i);

        public object GetValue(int i)
        {
            if (i == 1 && reader.GetInt32(0) == 2)
            {
                var stream = reader.GetStream(i);
                if (stream is not Db2ProgressiveLobStream)
                    throw new InvalidDataException("The source test query did not return a progressive DB2 LOB locator.");
                return new Db2BlobStream(new CancelOnSecondProgressiveChunkReadStream(
                    stream, cancellation, () => _secondLocatorReadStarted.TrySetResult(),
                    count => Interlocked.Add(ref _secondLocatorBytesRead, count)), stream.Length);
            }
            return reader.GetValue(i);
        }

        public int GetValues(object[] values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var count = Math.Min(values.Length, FieldCount);
            for (var index = 0; index < count; index++) values[index] = GetValue(index);
            return count;
        }

        public void Dispose() => reader.Dispose();
    }

    private sealed class CancelOnSecondProgressiveChunkReadStream(
        Stream source,
        CancellationTokenSource cancellation,
        Action secondChunkStarted,
        Action<int> bytesRead) : Stream
    {
        private const long FirstProgressiveChunkLength = 1024 * 1024;
        private int _cancelIssued;

        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ReadMonitoredAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadMonitoredAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private async ValueTask<int> ReadMonitoredAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var cancel = source.Position >= FirstProgressiveChunkLength
                && Interlocked.CompareExchange(ref _cancelIssued, 1, 0) == 0;
            var read = source.ReadAsync(buffer, cancellationToken);
            if (cancel)
            {
                secondChunkStarted();
                cancellation.Cancel();
            }
            var count = await read.ConfigureAwait(false);
            bytesRead(count);
            return count;
        }
    }
}
