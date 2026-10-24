# JustyBase.Db2

<p align="center">
  <img src="assets/justybase-db2-banner.png" alt="JustyBase.Db2 managed Db2 driver" width="100%" />
</p>


> Native managed **.NET / ADO.NET driver for IBM Db2 LUW**, speaking DRDA/DDM directly over TCP/TLS without IBM CLI runtime dependencies.

[![CI](https://github.com/justybase/JustyBase.Db2/actions/workflows/ci.yml/badge.svg)](https://github.com/justybase/JustyBase.Db2/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10%20%7C%2011-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Status: alpha](https://img.shields.io/badge/status-alpha-orange.svg)](CHANGELOG.md)

> **Highly experimental alpha.** The driver is under active development and is not production-ready. APIs and protocol coverage may still change. Validate it against your own Db2 environment and workload before using it for production-critical data.

## Overview

`JustyBase.Db2` is a managed .NET Db2 LUW client that implements DRDA/DDM directly over .NET TCP/TLS. It does not require IBM CLI, ODBC or native Db2 client libraries at runtime.

The repository provides:

- a native async `Db2Session` API,
- an ADO.NET provider (`DbConnection`, `DbCommand`, `DbParameter`, `DbTransaction`, `DbDataReader`),
- bounded pooling, cancellation, row streaming, large-object streaming, stored procedures and bounded import.

> **Independent project:** this project is not maintained, endorsed, sponsored or certified by IBM. IBM and Db2 are trademarks of International Business Machines Corporation.

## Why JustyBase.Db2?

- **Managed .NET transport** — no IBM CLI runtime dependency.
- **ADO.NET integration** — standard `DbConnection` / `DbCommand` workflows.
- **Bounded resources** — bounded pools, materialization limits and incremental protocol handling.
- **Streaming LOB support** — progressive reads through `GetStream` / `GetTextReader` where Db2 returns progressive locators.
- **Explicit cancellation semantics** — `CancellationToken`, `Cancel()` and `CommandTimeout` use Db2 SQL interrupt handling.
- **NativeAOT/trim analysis** — compatibility analyzers are enabled in the driver project.

## Feature matrix

| Feature | Status | Notes |
| --- | :---: | --- |
| TCP connections | ✅ | Managed .NET sockets |
| TLS 1.2 / 1.3 | ✅ | Certificate/hostname validation |
| Password authentication | ✅ | DRDA server authentication (SECMEC 3) |
| SECMEC 7 | ✅ | Clear user ID and DES-encrypted password (`USRENCPWD`); verified on Db2 11.5.8; AES selection fails closed |
| SECMEC 9 | ✅ | Encrypted user/password exchange |
| Kerberos / GSSAPI (SECMEC 11) | 🧪 | `krb5_keytab` / `krb5_ccache` on Linux x86_64; see [docs/KERBEROS.md](docs/KERBEROS.md) |
| Native async session API | ✅ | `Db2Session` |
| ADO.NET provider | ✅ | `DbConnection`, commands, readers, transactions |
| Prepared statements | ✅ | Positional parameter binding |
| Transactions | ✅ | ADO.NET and native APIs |
| Connection pooling | ✅ | Bounded pool with reset validation |
| Row streaming | ✅ | Async row enumeration / `DbDataReader` |
| BLOB / CLOB / DBCLOB / XML | ✅ | Materialized and streamed paths |
| Progressive LOB streaming | ✅ | `GetStream` / `GetTextReader` |
| Stored procedures | ✅ | OUT/INOUT and dynamic result sets |
| Batch DML | ✅ | Native bounded batch execution |
| Cancellation / timeout | ✅ | SQL interrupt path |
| Bulk/import path | ✅ | Bounded prepared INSERT implementation |
| IBM CLI dependency | ❌ | Not required at runtime |

Use `security_mechanism=7` (also `secmec=7`, `usrencpwd` or `encrypted_password_security`) to request DRDA `USRENCPWD` against a server with `AUTHENTICATION=SERVER_ENCRYPT`; `Authentication=SERVER_ENCRYPT` continues to request mechanism 9. When `security_mechanism` is not set, the driver requests mechanism 3 and, if the server refuses it and lists an encrypting mechanism, retries the key exchange with 9 (or 7) on the same connection, as IBM CLI does; no credentials are sent before that. An explicitly configured mechanism is never changed: if the server rejects it, the connection fails with the list of mechanisms the server accepts. Mechanism 7 uses DES (`ENCALG=1`); a server selecting AES (`ENCALG=2`) is rejected before any credentials are sent. DRDA password encryption uses an unauthenticated Diffie–Hellman exchange, so use TLS when the network is not trusted.

## Status

- **Experimental / alpha** (`0.1.0-alpha.4`). NuGet packages are prereleases; validate against your own Db2 environment and workload before production use.
- **APIs may change** before 1.0, including the native API and result types.
- **Not yet recommended for production use.** Validate against your own Db2 environment and workload first.

## Requirements

- **.NET 10 or 11** (`net10.0`, `net11.0`) for the current alpha. Building the `net11.0` target requires the .NET 11 SDK (11.0.100-rc.2 or newer).
- An IBM Db2 LUW server reachable over TCP, optionally TLS.
- Kerberos (SECMEC 11) only on Linux x86_64 with the system MIT GSS-API library.

## Supported and tested environments

The maintained live qualification profile is **Linux x64** with **Db2 LUW 12.1.5** over TCP and TLS, using .NET SDK 10.0.112 (`net10.0`) and 11.0.100-rc.2 (`net11.0`) (see [Qualification and compatibility](docs/QUALIFICATION.md)). Compatibility has also been verified against **Db2 LUW 11.5.8** over TCP. Windows and macOS receive continuous managed protocol and API smoke coverage, but are not currently live-qualified against Db2. Db2 for z/OS and IBM i are outside the supported scope.

## Packages and local validation

Install the prerelease packages from NuGet.org with an explicit version:

```bash
dotnet add package JustyBase.Db2 --version 0.1.0-alpha.4
dotnet add package JustyBase.Db2.IBMCompat --version 0.1.0-alpha.4
```

The compatibility package depends on `JustyBase.Db2`; install it directly only when migrating selected `IBM.Data.Db2` APIs.

For development against the source projects:

```bash
dotnet add reference src/Db2.Driver/Db2.Driver.csproj
```

Use `JustyBase.Db2` for new applications. For applications migrating selected `IBM.Data.Db2` APIs, see [Migrating from IBM.Data.Db2](#migrating-from-ibmdata-db2) and the [migration guide](src/Db2.IBMCompat/IBM-DATA-DB2-MIGRATION.md).

## Known limitations

- Protocol coverage is the subset exercised by the qualification suite. Unlisted SQL types or server features may fail or return less precise metadata.
- LOB materialization is bounded to 16 MiB per value and 32 MiB per query result; larger values must be streamed.
- The `IBM.Data.Db2` facade is a selected, source-level subset. It is not binary-compatible with IBM's assembly.
- Bulk import uses bounded multi-row INSERT statements, not the Db2 LOAD utility.
- Kerberos is verified only on Linux x86_64 with MIT GSS-API.
- Public XML documentation is incomplete for the alpha surface.

## Quick start — standard ADO.NET

```csharp
using System.Data.Common;
using JustyBase.Db2.AdoNet;

await using DbConnection connection = new Db2AdoConnection(
    "Server=localhost:50000;Database=TESTDB;UID=app;PWD=secret;Pooling=true");

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

Use environment variables or a secret store for real credentials.

## Quick start — native session API

```csharp
await using var db = await Db2Session.ConnectAsync(
    "db2://user:password@localhost:50000/TESTDB");

var result = await db.QueryAsync("VALUES 1");
Console.WriteLine(result.Rows[0][0]);
```

## Migrating from IBM.Data.Db2

`JustyBase.Db2.IBMCompat` is an optional compatibility layer for migrating selected `IBM.Data.Db2` APIs to `JustyBase.Db2`. It is not a complete replacement for the IBM provider. The package is built for validation and is not currently published.

```csharp
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
```

This is **source-level compatibility for a selected subset**, not binary compatibility with IBM assemblies and not a claim of complete IBM API parity. See [IBM provider migration guide](src/Db2.IBMCompat/IBM-DATA-DB2-MIGRATION.md).

## TLS

ADO.NET and IBM-shaped connection strings support the documented SSL subset, including `Security=SSL` and `SSLServerCertificate`.

```csharp
await using var connection = new Db2AdoConnection(
    "Server=db2.example.com:50001;Database=TESTDB;UID=app;PWD=secret;" +
    "Security=SSL;SSLServerCertificate=/etc/db2/db2-ca.pem");

await connection.OpenAsync(cancellationToken);
```

Unsupported security settings fail during parsing instead of silently falling back to plaintext.

## Transactions

```csharp
await using DbTransaction tx = await connection.BeginTransactionAsync(
    IsolationLevel.ReadCommitted, cancellationToken);

try
{
    await using DbCommand command = connection.CreateCommand();
    command.Transaction = tx;
    command.CommandText =
        "UPDATE APP.ACCOUNTS SET BALANCE = BALANCE - ? WHERE ACCOUNT_ID = ?";

    AddParameter(command, DbType.Decimal, 25.00m);
    AddParameter(command, DbType.Int32, 7);

    await command.ExecuteNonQueryAsync(cancellationToken);
    await tx.CommitAsync(cancellationToken);
}
catch
{
    await tx.RollbackAsync(CancellationToken.None);
    throw;
}
```

Failed or uncertain transaction-control state is not returned to a pool as a clean reusable session.

## Streaming rows and large objects

ADO.NET readers support forward-only async consumption. For progressive BLOB locators use `GetStream`; for progressive text/XML locators use `GetTextReader`.

```csharp
await using DbCommand command = connection.CreateCommand();
command.CommandText = "SELECT PAYLOAD FROM APP.DOCUMENTS WHERE DOCUMENT_ID = ?";
AddParameter(command, DbType.Int64, documentId);

await using DbDataReader reader = await command.ExecuteReaderAsync(
    CommandBehavior.SequentialAccess, cancellationToken);

if (await reader.ReadAsync(cancellationToken))
{
    await using Stream payload = reader.GetStream(0);
    await using var output = File.Create("document.bin");
    await payload.CopyToAsync(output, cancellationToken);
}
```

The current alpha limits a single materialized LOB to **16 MiB** and a materialized query result to **32 MiB**. Some inline/EXTDTA representations are materialized even when progressive locator streaming is supported elsewhere.

## Cancellation and command timeouts

`CancellationToken`, `DbCommand.Cancel()` and positive `CommandTimeout` values use Db2's SQL interrupt path.

An interrupt acknowledgment does **not** by itself prove that the original command has stopped. Await the original operation before assuming the connection is reusable. If cleanup cannot be confirmed, the driver discards the session instead of risking reuse with uncertain protocol state.

See [Advanced driver guide](docs/DRIVER-GUIDE.md#cancellation-and-command-timeouts).

## Stored procedures

`CommandType.StoredProcedure` supports ordered input/output/input-output parameters and dynamic result sets. Output parameters become final only after result cursors are consumed or safely drained.

## Bulk copy and import

The compatibility package (`JustyBase.Db2.IBMCompat`) includes a selected `DB2BulkCopy` workflow. The implementation uses bounded prepared INSERT execution; it does **not** invoke IBM CLI LOAD and does not claim identical throughput or behavior to IBM's native bulk loader.

For application migration details and current gaps, see [IBM-DATA-DB2-MIGRATION.md](src/Db2.IBMCompat/IBM-DATA-DB2-MIGRATION.md).

## Errors and warnings

Classified failures implement `IDb2Error` and expose `Db2ErrorKind`. Server SQL failures preserve SQLCODE and SQLSTATE when supplied by Db2.

ADO.NET exposes provider-specific successful SQL warnings through `InfoMessage`.

Authentication errors do not echo configured passwords.

## Testing

All tests live in `src/Db2.Tests/Db2.Tests.csproj` (xUnit). Categories: offline (no category filter needed), `Live`, `Db2Integration` (live smoke subset), `Secmec9Integration`, `Secmec7Integration`, `KerberosIntegration`.

```bash
# 1. Offline unit + protocol tests (fast, no Docker / Db2 needed)
dotnet build src/Db2.Tests/Db2.Tests.csproj --nologo
dotnet test src/Db2.Tests/Db2.Tests.csproj --no-build --nologo

# 1b. Same offline scope as CI (explicit filter, no live/Kerberos tests)
dotnet test src/Db2.Tests/Db2.Tests.csproj --no-build --nologo -v q \
  --filter 'FullyQualifiedName!~Live&Category!=Live&Category!=Db2Integration&Category!=KerberosIntegration&Category!=Secmec9Integration&Category!=Secmec7Integration'

# 2. Live Db2 smoke suite (starts throwaway Db2 12.1.5 container, removes it afterwards)
./scripts/test-db2-integration.sh           # smoke suite (Category=Db2Integration): connect, DML/types/NULLs, parameters, transactions, errors

# 3. Full live suite (same container setup, all Category=Live; slow, several minutes)
./scripts/test-db2-integration.sh --full

# 4. Kerberos end-to-end (fresh MIT KDC + Db2 container per run, SECMEC 11)
./scripts/test-db2-kerberos.sh
./scripts/test-db2-kerberos.sh --verbose                # extra provisioning detail
./scripts/test-db2-kerberos.sh --keep-on-failure        # keep containers/network/workdir on failure for diagnosis

# 5. Full local qualification: full live suite + 3x fresh Kerberos E2E (same as manual Live Db2 Qualification workflow)
./scripts/test-db2-qualification.sh
./scripts/test-db2-qualification.sh --kerberos-repeats 1 --verbose-kerberos   # faster variant

# 6. Pre-submit / CI parity checks: pack both packages, then validate them
dotnet pack src/Db2.Driver/Db2.Driver.csproj -c Release -o /tmp/db2-packages
dotnet pack src/Db2.IBMCompat/Db2.IBMCompat.csproj -c Release -o /tmp/db2-packages
python3 tools/sbom/check_package_contents.py /tmp/db2-packages
python3 tests/PackageConsumer/run_package_consumer_smoke.py /tmp/db2-packages
python3 tools/sbom/generate_cs_sbom.py /tmp/db2-packages
python3 scripts/check-trx-no-skips.py <results>.trx   # CI rejects skipped offline/smoke tests
```

Live tests require `DB2_TEST_DSN` and are opt-in in a plain `dotnet test` run. CI uses an explicit offline filter and rejects skipped tests, then runs the isolated Db2 smoke suite against a temporary container on every pull request.

### Local Db2 integration tests

```bash
./scripts/test-db2-integration.sh           # smoke suite (Category=Db2Integration): connect, DML/types/NULLs, parameters, transactions, errors
./scripts/test-db2-integration.sh --full    # all live tests (Category=Live; slow)
```

The command starts a temporary IBM Db2 12.1.5 Community container, runs the live integration tests against it, and removes the container, its volumes and temporary files afterwards, also when a test fails or the run is interrupted. It never uses an existing database: `DB2_*` variables from your environment are cleared and the connection string is built from the container that this invocation started. A plain `dotnet test` stays offline: the live tests are skipped without `DB2_TEST_DSN`.

The `CI` workflow runs this smoke suite for pushes and pull requests. The manual [Live Db2 Qualification workflow](.github/workflows/live-db2-manual.yml) runs the full live suite and three fresh Kerberos end-to-end environments. Both workflows publish short-lived, redacted TRX and console results for diagnosis.

Requirements: Linux x86_64, Docker with a running daemon (the Db2 container runs in privileged mode), about 4 GB of free memory, 10 GB of free disk space and the .NET 10 SDK (or .NET 11 SDK 11.0.100-rc.2+ for the `net11.0` target). The first run is slower because Docker has to download the Db2 image (several GB); starting Db2 usually takes two to four minutes. The script passes `LICENSE=accept` to the IBM image, so running it means accepting the Db2 Community Edition license terms.

CI also verifies the public API snapshot, SBOM generation, package contents, trim/AOT analyzer compatibility and offline protocol behavior. After the first successful run, configure `CI / CI gate` as the required branch protection check; it passes only after offline, platform, live Db2 and IBM provider parity lanes all pass. A green offline job alone does not claim live Db2 coverage.

## Public API policy

See [API-COMPATIBILITY.md](API-COMPATIBILITY.md). Application code should use the native session/pool APIs or the documented ADO.NET surface. Protocol and DRDA/DDM types are internal implementation details and are not part of the public API.

## Documentation

- [Native package guide (JustyBase.Db2)](src/Db2.Driver/README.md)
- [Compatibility package guide (JustyBase.Db2.IBMCompat)](src/Db2.IBMCompat/README.md)
- [IBM provider migration guide](src/Db2.IBMCompat/IBM-DATA-DB2-MIGRATION.md)
- [Advanced driver guide](docs/DRIVER-GUIDE.md)
- [Qualification and compatibility](docs/QUALIFICATION.md)
- [API compatibility policy](API-COMPATIBILITY.md)
- [Changelog](CHANGELOG.md)
- [Contributing](CONTRIBUTING.md)
- [Security policy](SECURITY.md)

## Known limitations

- Highly experimental alpha; not production-ready.
- Current package targets .NET 10 and .NET 11.
- Maintained compatibility testing targets Db2 LUW, not Db2 for z/OS or IBM i.
- `JustyBase.Db2.IBMCompat` supports selected `IBM.Data.Db2` APIs. It is not a complete replacement for IBM's provider.
- Some IBM provider APIs such as full column mappings / complete bulk-copy feature parity are not implemented.
- Some inline/EXTDTA LOB representations remain materialized.
- Trusted-context switching requires matching server configuration.
- Kerberos with explicit `krb5_keytab` / `krb5_ccache` is verified on Linux x86_64 with an MIT KDC and Db2 LUW 12.1.5 only; other platforms support only `gssapi_token_file`. See [docs/KERBEROS.md](docs/KERBEROS.md).

## License and provenance

The project is licensed under the [MIT License](LICENSE).

The implementation uses the MIT-licensed [`go-db2`](https://github.com/go-db2/go-db2) project as a primary protocol and implementation reference. Required upstream notices for derived or adapted portions are retained in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

This repository does not bundle IBM CLI libraries or IBM driver binaries. Application and server licensing requirements remain the responsibility of the user.
