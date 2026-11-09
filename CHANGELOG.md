# Changelog

## Unreleased

### Changed

- Added the self-contained JDC v1 contract (`docs/DRIVER-CONTRACT.md`) and the
  repo-specific conformance record (`docs/DRIVER-CONTRACT-STATUS.md`). No API or
  behaviour changes.
- Added `Db2ErrorKind.OperationCancelled`, `Db2ErrorKind.Timeout` and
  `Db2ErrorKind.Other`; `Db2SqlException`/`Db2ServerErrorException` report
  `-952` as `OperationCancelled`, and command/batch timeouts and batch
  cancellations now expose a phase-derived `IDb2Error.Outcome`.
- `Db2CommandTimeoutException` and `Db2BatchCancellationException` implement
  `IDb2Error`; `Db2BatchExecutionException` reports the outcome of its failing
  set. The pool discards a lease whose classified outcome is `Unknown`.
  API snapshot regenerated for the additive enum/interface members.
- Added `currentschema`/`current_schema` DSN support and the native
  `Db2Session.CurrentSchema`, `SetCurrentSchemaAsync` and `ChangeDatabaseAsync`
  verbs. Names and schemas are validated before I/O, a database switch is
  refused during a transaction or on a pooled lease, a failed reconnect leaves
  the session closed, and the pool reset restores the configured schema. An
  explicit empty schema is rejected. API snapshot regenerated for the additive
  members.
- Aligned resource limits with JDC: abuse caps are now 64 MiB DSS, 64 MiB DDM,
  256 MiB / 4096-packet reply chains, and `block_size` outside 1024..1048576 is
  rejected with a configuration error. Added `Db2BatchOptions` (256 rows / 8 MiB
  defaults), changed the ADO import default batch size to 256, and exposed
  `DiscardedSessionCount`/`AcquireTimeoutCount`; `maxWaiters` now accepts zero
  from both the native options and ADO connection strings. Reply-chain limits
  are enforced on the owned-packet reader too and report
  `Db2ResourceLimitException` on overflow. API snapshot regenerated for the
  additive members.
- `Db2Session.ExecuteBatchAsync`/`Db2Pool.ExecuteBatchAsync` now expose one
  batch-options overload with an optional command-options argument. The removed
  `(batchOptions, CancellationToken)` overload made an explicit `null` options
  argument ambiguous; the combined overload keeps the same behaviour and the
  API snapshot records the intentional shape. Callers that previously passed a
  token positionally with batch options can use `cancellationToken: token`.
- `ChangeDatabaseAsync` applies the open-session, active-transaction and pooled
  lease checks before treating a same-name switch as a no-op.
- Mutual TLS with a PEM client certificate and key now works on Windows.
  Schannel rejects the ephemeral key produced by PEM loading, so the client
  certificate is imported into a temporary user key container for the
  handshake and that container is removed afterwards. Connect, interrupt and
  change-database reconnects share the same loading and cleanup path.

## 0.1.0-alpha.4 - 2026-10-24

- Both packages now target `net10.0` and `net11.0` (driver, IBMCompat and tests; CI, publish and dry-run workflows install .NET 10.0.112 and 11.0.100-rc.2; SBOM/package-contents allowlist and docs updated). Building `net11.0` requires the .NET 11 SDK.
- `DecodePackedDecimal` now builds the decimal string with a single allocation via `stackalloc` instead of `StringBuilder`/intermediate strings (measured ~5x faster micro, ~15-18% faster Mixed 100k-row native replay, ~36% less allocation; buffered `RowValueParser` path unchanged).

- DRDA `USRENCPWD` (SECMEC 7) authentication: `security_mechanism=7` (`usrencpwd`, `encrypted_password_security`, `secmec7`) sends a clear user ID and the DES-CBC-encrypted password in `SECCHK/SECTKN`, with the client DH key in `ACCSEC` (`ENCALG=1`) and the IV taken from the CP500 user ID. AES (`ENCALG=2`) and other algorithms fail closed before credentials are sent. Opt-in live test `Secmec7LiveTests` (`DB2_SECMEC7_TEST_DSN`, category `Secmec7Integration`).
- A connection without a configured security mechanism now follows a server that refuses the plaintext default: when `ACCSECRD` lists `9` or `7` (Db2 `AUTHENTICATION=SERVER_ENCRYPT`), the driver sends a second `ACCSEC` on the same connection with `9`, else `7`, before any credentials are sent. An explicitly configured mechanism, including `3`, is never changed, and a rejection of the retried mechanism ends the handshake.
- `ACCSECRD`'s SECMEC and ENCALG parameters are decoded as lists. When the server rejects the requested mechanism the `Db2UnsupportedFeatureException` (previously a plain `NotSupportedException`) names every mechanism the server accepts plus `SECCHKCD` (`DB2 rejected security mechanism 3 (SECCHKCD=1); the server accepts 7, 9, 10`). A non-zero `SECCHKCD` for the requested mechanism now fails with `Db2AuthenticationException` before `SECCHK`.
- The `SwitchUserAsync` SECCHK fallback checks the mechanism actually negotiated, so it stays refused after an automatic switch to an encrypting mechanism.
- Verified live against Db2 LUW 11.5.8.0 with `AUTHENTICATION=SERVER_ENCRYPT` (SECMEC 7, SECMEC 9, unset mechanism, explicit SECMEC 3 rejection, wrong password).

## 0.1.0-alpha.2 - 2026-10-11

- `DB2BulkCopy` now batches up to 1024 rows by default and commits completed batches instead of committing each row; batch size is configurable and respects Db2 limits.
- Added IBM-vs-JustyBase BenchmarkDotNet comparisons for 500k/100k row fetches, bulk copy, writes, scalar commands, connection setup and managed allocations.
- Bulk-copy error handling preserves earlier completed batches and rolls back the batch containing a failing row.
- Published both packages as NuGet prereleases.
