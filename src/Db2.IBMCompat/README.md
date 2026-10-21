# JustyBase.Db2.IBMCompat

> Optional compatibility layer for migrating selected `IBM.Data.Db2` APIs to the managed .NET `JustyBase.Db2` driver for IBM Db2 LUW. It is not a complete replacement for the IBM provider.

> **Alpha prerelease (`0.1.0-alpha.3`). Not recommended for production use.**
> This package is for migrating selected APIs in existing `IBM.Data.Db2` applications. New applications should use `JustyBase.Db2`.

`JustyBase.Db2.IBMCompat` is an optional package in this project. It is not affiliated with, endorsed by, or sponsored by IBM. IBM and Db2 are trademarks of International Business Machines Corporation.

## Package status

This prerelease is published to NuGet.org. Install with `dotnet add package JustyBase.Db2.IBMCompat --version 0.1.0-alpha.3`; the native driver dependency is installed automatically.

This package depends on `JustyBase.Db2`, which is installed automatically. It contains only the `IBM.Data.Db2` facade; all connection, protocol and command logic lives in the core package.

Do not reference IBM's own provider package and this facade in the same compilation. Both define `IBM.Data.Db2.*` type names.

## Example

```csharp
using System.Data;
using System.Data.Common;
using IBM.Data.Db2;

var builder = new DB2ConnectionStringBuilder
{
    Server = "localhost:50000",
    Database = "TESTDB",
    UserID = Environment.GetEnvironmentVariable("DB2_USER"),
    Password = Environment.GetEnvironmentVariable("DB2_PASSWORD"),
    Pooling = true,
};

await using var connection = new DB2Connection(builder.ConnectionString);
await connection.OpenAsync(cancellationToken);

using DbCommand command = connection.CreateCommand();
command.CommandText = "SELECT NAME FROM APP.CUSTOMERS WHERE CUSTOMER_ID = ?";
DbParameter id = command.CreateParameter();
id.DbType = DbType.Int32;
id.Value = 42;
command.Parameters.Add(id);
```

Parameters bind positionally to `?` markers, in collection order. `DB2Command` and `DB2DataReader` are not concrete types here; commands and readers are standard `DbCommand` and `DbDataReader` objects. See the [migration guide](IBM-DATA-DB2-MIGRATION.md) for the full mapping.

## Bulk copy

`DB2BulkCopy` offers the IBM-shaped progress and error surface, implemented with bounded, parameterized multi-row `INSERT` statements. By default, `BatchSize` is 1024 and completed batches are committed. A failed batch is rolled back while preceding committed batches remain. `TableLock` requests an exclusive table lock and uses one transaction for the import:

```csharp
using var copy = new DB2BulkCopy(connection, DB2BulkCopyOptions.Default)
{
    DestinationTableName = "APP.CUSTOMERS_STAGE",
    NotifyAfter = 10_000,
    // Optional; defaults to 1024 rows and is further bounded by Db2 limits.
    BatchSize = 1024,
};
copy.DB2RowsCopied += (_, e) => Console.WriteLine($"Copied {e.RowsCopied:N0}");
copy.WriteToServer(sourceReader);

foreach (DB2Error error in copy.Errors)
    Console.Error.WriteLine($"row {error.RowNumber}: {error.Message}");
```

`KeepIdentity` and `Truncate` are not implemented and fail explicitly. For a configurable bounded-memory import with atomic rollback by default, cancellation and commit intervals, use the native `Db2AdoImporter.ImportAsync` from `JustyBase.Db2.AdoNet`.

## Known gaps

- Only a selected subset of `IBM.Data.Db2` is implemented. Unlisted members are absent, so code that uses them will not compile.
- The facade is source-level compatible only. It is not binary-compatible with IBM's assembly.
- Provider-specific connection keywords, client reroute, `db2dsdriver.cfg` and IBM-specific catalog controls are not implemented.
- `DB2DataAdapter`, `DB2CommandBuilder`, `DbBatch`, IBM array binding and `DB2Type` are not mapped.

See the [migration guide](IBM-DATA-DB2-MIGRATION.md) for the complete list.

## Licensing and provenance

This project is MIT-licensed and contains no IBM binaries, drivers or client libraries. Using it does not change the Db2 server licensing that applies to your deployment. IBM documents Db2 Connect licensing requirements for its own .NET provider packages; check the terms that apply to the server product you connect to. This is general project information, not legal advice.

Licensed under the MIT License.
