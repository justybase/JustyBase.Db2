# Advanced driver guide

This guide collects operational details that are intentionally kept out of the main README.

## Native session API and ADO.NET

The repository exposes two primary application surfaces:

- `Db2Session` / `Db2Pool` for the native driver API,
- `JustyBase.Db2.AdoNet` for standard ADO.NET integration.

A selected source-compatible facade also exists under `IBM.Data.Db2` for migration scenarios. It is not binary-compatible with IBM assemblies and does not aim to reproduce every IBM-specific type or behavior.

## Connection strings and authentication

The driver accepts native URL DSNs and ADO.NET/IBM-style key/value connection strings for the documented subset. Unsupported security/authentication modes fail during validation rather than silently changing behavior.

Use TLS for credentials in transit. Hostname/IP verification remains enabled when TLS is configured.

Kerberos (`Authentication=KERBEROS`, DRDA SECMEC 11) with `krb5_keytab` or `krb5_ccache` is described in [KERBEROS.md](KERBEROS.md). A failed Kerberos attempt never falls back to a password mechanism.

## Pooling and reset

The pool bounds physical sessions and pending admission. It supports acquire/connect deadlines, idle/lifetime controls and explicit shutdown.

When a lease returns, the driver validates relevant server session state and restores observed changes where supported. This prevents schema/isolation/client identity state from leaking between callers.

If reset cannot be proven successful, the session is discarded instead of reused.

## ADO.NET reader lifecycle

`DbDataReader` exposes forward-only result consumption. Result-set and procedure lifecycles matter because Db2 can return cursor metadata, row data, output parameters and external LOB payloads across several DRDA reply objects.

Close/dispose readers normally so the driver can drain/close server cursor state before the connection is reused.

## Large objects

### Materialized values

The current alpha limits:

- one materialized LOB to 16 MiB,
- one materialized query result to 32 MiB.

Values beyond those limits fail instead of allocating without bound.

### Progressive locator reads

For progressive BLOB locators, use `GetStream`. For progressive CLOB/DBCLOB/XML values, use `GetTextReader`.

Advancing or closing the owning reader drains unread progressive locator data before reuse when safe. Only one progressive locator is active at a time on the relevant reader path.

Some inline/EXTDTA representations remain materialized and are therefore subject to the limits above.

### Streaming writes

ADO.NET parameters can use `Stream` for large values. The native API also exposes typed BLOB/CLOB/DBCLOB/XML stream inputs. Known/declared lengths are part of the protocol contract.

## Cancellation and command timeouts

`CancellationToken`, `DbCommand.Cancel()` and positive `CommandTimeout` values use Db2's SQL interrupt path.

The important distinction is:

1. the interrupt request can be acknowledged,
2. the original operation is still in flight until it settles,
3. only the original operation result establishes the final command state.

An interrupt acknowledgment alone does not prove that the command has stopped.

A confirmed cancellation commonly surfaces as SQLCODE `-952` / SQLSTATE `57014`. If cleanup cannot be confirmed, the driver discards the session rather than risking reuse with uncertain protocol state.

## Transactions

ADO.NET transactions must be assigned to commands explicitly. Native transaction scopes provide the same underlying session ownership.

Supported LUW isolation mappings include UR/CS/RS/RR through the corresponding ADO.NET isolation values.

Failed commit/rollback exchanges invalidate the session. An unfinished transaction is not treated as clean reusable pool state.

## Stored procedures

`CommandType.StoredProcedure` supports ordered input/output/input-output parameters and dynamic result sets.

Output values become final only after all result sets are consumed or safely drained. This is necessary because Db2 can interleave output metadata/data with cursor exchanges.

## Bulk copy and importer behavior

The selected `DB2BulkCopy` facade and native importer use bounded prepared INSERT execution. They do not call IBM CLI LOAD.

The importer bounds rows, statement markers and estimated encoded bytes. Large values can use streaming/staging paths instead of materializing the entire payload.

Compatibility with common `DB2BulkCopy` workflows does not imply identical implementation strategy or throughput to IBM's provider.

## IBM-shaped compatibility scope

The compatibility namespace exists to ease migration of common application code. It deliberately does not claim complete IBM provider parity.

Known omissions/partial areas are documented in `src/Db2.IBMCompat/IBM-DATA-DB2-MIGRATION.md` and include IBM-specific APIs that have no equivalent in the current alpha.

## Warnings and errors

Classified failures implement `IDb2Error` and expose `Db2ErrorKind`. SQLCODE and SQLSTATE are preserved when Db2 supplies them.

Successful server warnings are exposed through the provider-specific `InfoMessage` event rather than converted into command errors.

Early reader `Close`/`Dispose`, `ExecuteScalar` and `SingleRow` drain the remaining
protocol exchange before session reuse. Errors discovered during that drain are
reported to the caller, and an uncertain or failed drain invalidates the session.
If reading already failed, its original SQLCODE/SQLSTATE remains the primary
error; a secondary cleanup failure is attached as `Exception.Data["Db2CleanupError"]`.

Authentication errors do not echo configured passwords.

## Fail-closed behavior

The driver intentionally disposes uncertain sessions instead of speculating that protocol state is reusable. Examples include:

- incomplete cursor cleanup,
- transport failure during an active request/reply exchange,
- unconfirmed interrupt cleanup,
- failed transaction control,
- malformed/oversized reply chains,
- failed session reset,
- contradictory or incomplete LOB/procedure state.

This is an intentional part of the safety/resource model.
