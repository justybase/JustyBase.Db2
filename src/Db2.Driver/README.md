# JustyBase.Db2

> Experimental managed .NET 10 driver for **IBM Db2 LUW**. Speaks DRDA/DDM directly over TCP/TLS, with no IBM CLI or ODBC runtime dependency.

> **Alpha prerelease (`0.1.0-alpha.3`). Not recommended for production use.**
> APIs and protocol coverage may change between prereleases. Validate against your own Db2 environment and workload before relying on it.

`JustyBase.Db2` is an independent open-source project. It is not affiliated with, endorsed by, or sponsored by IBM. IBM and Db2 are trademarks of International Business Machines Corporation.

## Package status

`JustyBase.Db2` is not currently published to NuGet.org or another package registry. `dotnet pack` produces a local validation artifact; a public install command will be documented after publication is enabled.

New applications should use this package's native API and ADO.NET provider. For migrations from selected `IBM.Data.Db2` APIs, the optional `JustyBase.Db2.IBMCompat` package is separate; it is not a complete replacement for the IBM provider.

## Requirements

- .NET 10 SDK or runtime (`net10.0` is the only target framework in this alpha).
- Reachability to an IBM Db2 LUW server over TCP, optionally TLS.
- Kerberos (SECMEC 11) support is limited to Linux x86_64 with the system MIT GSS-API library.

Maintainer qualification currently covers **Linux x64** with **Db2 LUW 12.1.5** over TCP and TLS, using .NET SDK 10.0.112. Earlier live verification also covered Db2 LUW 11.5.8. Other platforms and Db2 versions are not regularly tested. Db2 for z/OS and IBM i are not supported targets.

## Features

- Native async `Db2Session` and bounded `Db2Pool` APIs.
- ADO.NET provider: `Db2AdoConnection`, `Db2AdoCommand`, `Db2AdoDataReader`, `Db2AdoTransaction`, `Db2AdoProviderFactory`.
- Positional parameters, batch execution and prepared statements.
- Stored procedures with OUT/INOUT parameters and dynamic result sets.
- Transactions with explicit isolation levels.
- Password (SECMEC 3), encrypted password (SECMEC 7 `USRENCPWD`, SECMEC 9 `EUSRIDPWD`; an unset mechanism follows `AUTHENTICATION=SERVER_ENCRYPT`) and Kerberos (SECMEC 11, Linux x86_64) authentication.
- TLS 1.2/1.3 with certificate and hostname validation, and mutual TLS.
- Query cancellation and command timeouts through Db2's SQL interrupt.
- Row streaming, progressive BLOB/CLOB/DBCLOB/XML streams, and bounded materialization.
- Bounded import (`Db2AdoImporter`) with progress and commit policies.
- Metadata-only telemetry through `Db2Diagnostics` (`ActivitySource` and `Meter`).

## Quick start: ADO.NET

```csharp
using System.Data.Common;
using JustyBase.Db2.AdoNet;

await using var connection = new Db2AdoConnection(
    "Server=localhost:50000;Database=TESTDB;UID=app;PWD=<secret>;Pooling=true");
await connection.OpenAsync(cancellationToken);

await using DbCommand command = connection.CreateCommand();
command.CommandText = "SELECT NAME FROM APP.CUSTOMERS WHERE CUSTOMER_ID = ?";

DbParameter id = command.CreateParameter();
id.Value = 42;
command.Parameters.Add(id);

await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
while (await reader.ReadAsync(cancellationToken))
    Console.WriteLine(reader.GetString(0));
```

Parameters are positional: each `?` binds to the matching entry in `Parameters`, in collection order. Read real credentials from environment variables or a secret store.

## Quick start: native API

```csharp
using JustyBase.Db2;

await using var session = await Db2Session.ConnectAsync(
    "db2://app:<secret>@localhost:50000/TESTDB", cancellationToken);

Db2QueryResult result = await session.QueryAsync(
    "SELECT CUSTOMER_ID, NAME FROM APP.CUSTOMERS FETCH FIRST 10 ROWS ONLY", cancellationToken);

foreach (Db2ColumnMetadata column in result.Columns)
    Console.WriteLine($"{column.Name}: {column.SqlType}");
```

`Db2QueryResult.Columns` and `Db2StreamRow.Columns` describe each column with `Db2ColumnMetadata` (name, `SqlType`, length, precision, scale and nullability).

## TLS

```csharp
await using var connection = new Db2AdoConnection(
    "Server=db2.example.com:50001;Database=TESTDB;UID=app;PWD=<secret>;" +
    "Security=SSL;SSLServerCertificate=/etc/db2/db2-ca.pem");
```

Hostname validation stays enabled. Unsupported security values are rejected instead of silently falling back to plaintext.

## Known limitations

- Single target framework: `net10.0`. No earlier .NET or .NET Framework targets.
- Protocol coverage is the subset exercised by the qualification suite. Unlisted SQL types or server features may fail or return less precise metadata.
- LOB materialization is bounded: 16 MiB per materialized LOB and 32 MiB per materialized query result. Larger values must be streamed.
- The native API's result types may change before 1.0.
- Kerberos is verified only on Linux x86_64 with MIT GSS-API.
- Bulk import uses bounded prepared multi-row INSERT statements. It does not use the Db2 LOAD utility.
- No connection-string compatibility guarantee with other Db2 providers beyond the documented keywords.

## More information

- [Repository and documentation](https://github.com/justybase/JustyBase.Db2)
- [Driver guide](https://github.com/justybase/JustyBase.Db2/blob/main/docs/DRIVER-GUIDE.md)
- [Qualification summary](https://github.com/justybase/JustyBase.Db2/blob/main/docs/QUALIFICATION.md)
- [Kerberos setup](https://github.com/justybase/JustyBase.Db2/blob/main/docs/KERBEROS.md)
- [CHANGELOG](https://github.com/justybase/JustyBase.Db2/blob/main/CHANGELOG.md)
- [API compatibility policy](https://github.com/justybase/JustyBase.Db2/blob/main/API-COMPATIBILITY.md)
- [Security policy](https://github.com/justybase/JustyBase.Db2/blob/main/SECURITY.md)

Licensed under the MIT License.
