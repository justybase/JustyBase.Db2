# Migrate from `Net.IBM.Data.Db2`

This guide targets applications using IBM's ADO.NET provider in the `IBM.Data.Db2` namespace. The driver in this repository contains a compatibility layer for selected APIs of that namespace and implements the common ADO.NET contract. It is not a replacement assembly for `IBM.Data.Db2.dll`, and it does not provide binary compatibility.

Read the [driver README](../Db2.Driver/README.md) for complete examples of connection setup, commands, transactions, streaming, cancellation, and bulk import.

## 1. Replace the provider reference

Remove the IBM package from the application project. Do not reference both packages in one compilation: both define names such as `IBM.Data.Db2.DB2Connection`.

The prerelease packages are published to NuGet.org. Install the compatibility package with `dotnet add package JustyBase.Db2.IBMCompat --version 0.1.0-alpha.3`; its `JustyBase.Db2` dependency is installed automatically. Before migrating an application, validate the prerelease against your environment and workload.

`JustyBase.Db2.IBMCompat` depends on `JustyBase.Db2`, which is pulled in automatically. Both packages target `net10.0` and share the prerelease version `0.1.0-alpha.3`. **This package is highly experimental and not recommended for production use.** See the [compatibility package README](README.md) for its status. New applications should use the native `JustyBase.Db2` API instead.

Keep `using IBM.Data.Db2;` for the compatibility facade, or use `JustyBase.Db2.AdoNet` and instantiate `Db2AdoConnection` directly. Both use the same native driver implementation.

## 2. Keep common ADO.NET code and bind parameters positionally

These common patterns remain similar in shape:

| IBM provider code | This driver |
| --- | --- |
| `new DB2Connection(connectionString)` | Supported |
| `new DB2ConnectionStringBuilder()` | Supported for common connection properties |
| `connection.Open()` / `OpenAsync()` / `Close()` | Supported |
| `connection.InfoMessage += ...` | Supported; warning entries expose SQLSTATE and SQLCODE |
| `connection.CreateCommand()` | Supported; returns a standard `DbCommand` |
| `ExecuteReader`, `ExecuteScalar`, `ExecuteNonQuery`, sync and async | Supported |
| `DbCommand.Prepare`, `CommandTimeout`, `Cancel`, `CommandBehavior` | Supported within the limits in this guide |
| `DbTransaction`, commit, rollback, isolation | Supported for Db2 LUW UR/CS/RS/RR |
| `DB2Exception` / `DB2Error` | Compatible subset with `SqlCode`, `SqlState`, `NativeError`, `SQLState`, and `Errors` |
| `DB2BulkCopy` | Supported subset documented below |

Commands and readers are obtained as `DbCommand` and `DbDataReader`; there are no concrete IBM `DB2Command` or `DB2DataReader` facade types. Use standard ADO.NET APIs instead of casting to IBM-specific classes.

Parameters are bound in collection order to `?` markers. Change SQL that depends on named IBM parameter markers to positional markers, and add parameters in the same order:

```csharp
command.CommandText = "UPDATE APP.CUSTOMERS SET STATUS = ? WHERE CUSTOMER_ID = ?";
Add(command, DbType.String, status);       // marker 1
Add(command, DbType.Int32, customerId);    // marker 2

static void Add(DbCommand command, DbType type, object? value)
{
    DbParameter parameter = command.CreateParameter();
    parameter.DbType = type;
    parameter.Value = value ?? DBNull.Value;
    command.Parameters.Add(parameter);
}
```

Parameter names can still be assigned for application-side collection access, but they do not reorder or resolve SQL markers. `DB2Type`, provider-specific parameter constructors, arrays, and `ArrayBindCount` are not mapped. Use `DbType` and one value per `?` marker.

## 3. Update stored procedure calls where needed

IBM documents both `CommandType.StoredProcedure` and SQL text `CALL` forms. This driver supports:

```csharp
command.CommandType = CommandType.StoredProcedure;
command.CommandText = "APP.RECALCULATE_TOTAL";
```

or:

```csharp
command.CommandType = CommandType.Text;
command.CommandText = "CALL APP.RECALCULATE_TOTAL(?)";
```

Add `Input`, `Output`, and `InputOutput` parameters in procedure declaration order. `ReturnValue`, IBM array-valued parameters, cursor-valued OUT parameters, named marker resolution, and IBM's `{call ...}` escape form are not implemented. Procedures that return dynamic result cursors can be read through `ExecuteReader` and `NextResult`; output values become available after the reader has reached the end or been closed and drained. A no-argument, no-output `CALL` can use `ExecuteNonQuery`.

If the IBM code expects a `DB2DataReader` variable, change the declared type to `DbDataReader`:

```csharp
await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
do
{
    while (await reader.ReadAsync(cancellationToken))
        Consume(reader);
}
while (await reader.NextResultAsync(cancellationToken));
```

## 4. Replace IBM bulk-copy features

The common pattern `DB2BulkCopy(connection, DB2BulkCopyOptions.TableLock)`, `DestinationTableName`, `BulkCopyTimeout`, `NotifyAfter`, `DB2RowsCopied`, `WriteToServer(IDataReader)`, `Errors`, `RowsCopied`, `Close`, and `Dispose` is available. `IDataReader`, `DbDataReader`, and `DataTable` sources are supported. The destination table must already exist, and the connection must be open and outside another active transaction.

```csharp
using DB2BulkCopy copy = new DB2BulkCopy(connection, DB2BulkCopyOptions.TableLock)
{
    DestinationTableName = "APP.IMPORT_ROWS",
    BulkCopyTimeout = 600,
    NotifyAfter = 10_000,
};

copy.DB2RowsCopied += (_, e) => progress?.Invoke($"Copied {e.RowsCopied:N0}");
copy.WriteToServer(sourceReader);

foreach (DB2Error error in copy.Errors)
    Console.Error.WriteLine($"row {error.RowNumber}: {error.Message}");

if (copy.Errors.Count != 0)
    throw new InvalidOperationException($"Import reported {copy.Errors.Count} row error(s).");
```

IBM's `KeepIdentity`, `Truncate`, and `DB2BulkCopyColumnMapping` APIs are not implemented. `KeepIdentity` and `Truncate` options fail explicitly. To import columns in a different order, pass an explicit `destinationColumns` list to `Db2AdoImporter.ImportAsync`; each name in the list corresponds to the same ordinal in the source reader. If the target must be cleared, issue an explicit, application-reviewed Db2 statement before starting the copy.

`DB2BulkCopy` is implemented with bounded, parameterized multi-row `INSERT` statements rather than the IBM CLI `LOAD` path. The common progress/error surface is compatible, but do not assume the same throughput, locking internals, identity handling, or advanced load options. Successful rows before a later row error remain inserted and are reflected in `RowsCopied`; the failing row appears in `Errors`. `TableLock` requests an exclusive lock for the operation. Progress-handler exceptions are ignored, as IBM documents; setting `e.Abort` stops the copy after preserving rows reported as copied.

The native implementation defaults to a bounded 1024-row batch (`BatchSize` is configurable). In the default unlocked mode, each completed batch is committed; if a row in the next batch fails, that batch is rolled back and earlier committed batches remain. `TableLock` holds an exclusive lock and a single transaction, preserving successfully copied rows on a row error or progress abort. The default commit boundary is therefore a batch, not an individual row.

For a configurable bounded-memory import with atomic rollback by default, cancellation, explicit commit intervals, and row diagnostics, use `Db2AdoImporter.ImportAsync`. It accepts an existing `IDataReader`; see [the compatibility README bulk-copy example](README.md#bulk-copy) and the `Db2AdoImportOptions` XML docs. For a parameter-set batch without an ADO bulk-copy source reader, the lower-level `Db2Session.ExecuteBatchAsync` API is available; ADO `DbCommand` itself does not expose a batch method.

## 5. Audit connection and lifecycle options

Common connection string properties supported by the facade include `Server`, `Port`, `Database`/`DB`, `UID`/`UserID`, `PWD`/`Password`, `Connect_Timeout`, `Pooling`, `MaxPoolSize`, and `SSL` plus the documented certificate-path options. `Connect_Timeout` defaults to zero on `IBM.Data.Db2.DB2Connection`, matching IBM's no-deadline default; set it explicitly if an application expects a bounded login.

Review code that depends on provider-specific connection keywords, `db2dsdriver.cfg`, client reroute, alternate servers, IBM package/cache controls, credentials from external catalogs, ambient transaction enlistment, or distributed transactions. They are not implied by the common builder facade. See [`Config.cs`](../Db2.Driver/Config.cs) and [`Db2AdoConnection`](../Db2.Driver/AdoNet/Db2AdoConnection.cs) for the accepted settings.

`GetSchema("DataTypes")` and `GetSchema("MetaDataCollections")` are implemented. Broader catalog schema collections, `DB2DataAdapter`, `DB2CommandBuilder`, `DbBatch`, `DB2ResultSet`, `DB2XmlAdapter`, provider-specific XML readers, `ExecutePageReader`, `ExecuteRow`, and `ExecuteResultSet` are not mapped.

On a SQL `NULL` scalar, `ExecuteScalar` returns `DBNull.Value`; if there is no row, it returns `null`, following ADO.NET. `GetValue` also returns `DBNull.Value`. Large progressive LOBs can be read through `GetStream` / `GetTextReader`; `GetBytes`/`GetChars` use the reader's materialized-value behavior rather than IBM-specific locator semantics for every LOB encoding.

IBM's reference says `DB2Connection` instance members are not guaranteed thread safe ([IBM class documentation](https://www.ibm.com/docs/en/db2/12.1.x?topic=ibmdatadb2-db2connection-class)). Keep command, reader, transaction, and lifecycle operations serialized on one connection. In a live concurrent `ChangeDatabase`/`CALL` probe, IBM waited for the call and then returned a communication error while preserving the old open database; this driver rejected the concurrent lifecycle operation immediately. Neither behavior is promised for concurrent use of one connection. A failed non-concurrent reconnect in this driver leaves the connection closed and preserves the original connection string.

For synchronous `Dispose`, both providers wait for an active command to finish. The live comparison found IBM and this facade's `DisposeAsync` interrupt an active command and report SQLCODE `-952` / SQLSTATE `57014`; the cancellation task still must be awaited. This behavior is separate from ordinary `CancellationToken` cancellation, which surfaces cancellation to the caller.

## 6. Verify a migration

Run your normal application query and write workflows against a test database first. The package-consumer smoke test builds without IBM's assembly and exercises `DB2ConnectionStringBuilder`, `InfoMessage`, `DbCommand`, `IDataReader`, `DB2BulkCopy`, progress, NULL import, and readback. To compare the selected compatibility APIs with IBM's provider on an isolated Db2 instance, run the parity suite on Linux x64 with Docker available:

```sh
./scripts/test-ibm-parity.sh
```

For the driver's Db2 integration suite, use `./scripts/test-db2-integration.sh` for the smoke suite or `./scripts/test-db2-integration.sh --full` for all supported live tests using its isolated, pinned Db2 container. The complete live and Kerberos qualification is available with `./scripts/test-db2-qualification.sh`; it runs the full live suite and three fresh Kerberos environments by default. These scripts create temporary credentials for their disposable containers and remove their test resources when they finish. They require Linux x64, Docker, and access to the container and package registries.

## Licensing and deployment rights

The `JustyBase.Db2` package declares MIT in its project metadata. This C# driver uses [`go-db2`](https://github.com/go-db2/go-db2) as its primary protocol and implementation reference; the upstream MIT copyright and permission notice is preserved in the repository's [third-party notice](https://github.com/justybase/JustyBase.Db2/blob/main/THIRD-PARTY-NOTICES.md) for portions derived or adapted from it. The project [LICENSE](https://github.com/justybase/JustyBase.Db2/blob/main/LICENSE) and third-party notice are included in the NuGet package. This repository's MIT declaration does not grant rights to IBM binaries or to code from dependencies whose terms differ.

The production driver project does not depend on IBM CLI. IBM documents Db2 Connect licensing requirements for its .NET NuGet deployment to Db2 for z/OS and IBM i ([IBM deployment requirements](https://www.ibm.com/docs/en/db2/12.1.x?topic=adonet-deploying-net-core-applications-db2-net-nuget-packages)). Check the license terms that apply to the actual server product and deployment; switching client libraries does not by itself establish server entitlements. See the [compatibility README licensing notes](README.md#licensing-and-provenance). This is general project information, not legal advice.

## IBM and .NET references

- [IBM .NET provider and platform-specific NuGet packages](https://www.ibm.com/docs/en/db2/12.1.x?topic=adonet-data-server-provider-net-core)
- [IBM connection-string examples](https://www.ibm.com/docs/en/db2/12.1.0?topic=net-connecting-database)
- [IBM stored procedure patterns](https://www.ibm.com/docs/en/db2/12.1.x?topic=net-calling-stored-procedures-from-applications)
- [IBM `DB2BulkCopy` class](https://www.ibm.com/docs/SSEPGG_11.5.0/com.ibm.swg.im.dbclient.adonet.ref.doc/doc/DB2BulkCopyClass.html)
- [IBM `DB2BulkCopyOptions`](https://www.ibm.com/docs/en/db2/11.1?topic=ibmdatadb2-db2bulkcopyoptions-enumeration)
- [Microsoft `DbCommand.ExecuteScalar` NULL behavior](https://learn.microsoft.com/en-us/dotnet/api/system.data.common.dbcommand.executescalar?view=net-10.0)
