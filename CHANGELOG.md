# Changelog

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
