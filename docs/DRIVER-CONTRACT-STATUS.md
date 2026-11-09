# JDC v1 status — C# (.NET)

Contract: [`docs/DRIVER-CONTRACT.md`](DRIVER-CONTRACT.md) (JDC v1).
Last reviewed: 2026-11-09.

This file is the repo-specific conformance record. It is not part of the
normative text. `compliant` means every `MUST` in the group is met; `partial`
means some are met and the rest are listed; `gap` means the group is not met.
`gap` and `partial` entries are implementation work for the next conformance
pass; this file records them, it does not waive the requirement.

## Summary

| Status | Requirements |
| --- | --- |
| compliant | ERR-02-09, OUT-01, OUT-06-09, DSN-01-06, DSN-08-10, LIM-01-08, POOL-01-07, SES-01-10, VAL-01-10, CAN-01-09, BAT-01-09 |
| partial | ERR-01, OUT-02-05, DSN-07 |
| gap | — |

## Details

| Requirements | Status | Notes |
| --- | --- | --- |
| ERR-01 | partial | Most failures implement `IDb2Error`; a command timeout, batch failure and batch cancellation now expose kind/outcome too. Raw socket/stream exceptions are still plain `IOException`/`InvalidDataException` without a kind. |
| ERR-02 | compliant | `Db2ErrorKind` covers all canonical categories, including `OperationCancelled`, `Timeout` and `Other`. |
| ERR-03–04 | compliant | SQL/auth/authz/conversion/transport/protocol/timeout/cancellation map to stable kinds; confirmed `-952` maps to `OperationCancelled` in both SQLCARD and SQLERRRM paths. |
| ERR-05–08 | compliant | SQLCODE/SQLSTATE and batch progress are preserved; messages use `SQLCODE=… SQLSTATE=…`; secrets are covered by tests. |
| ERR-09 | compliant | No public serialized error form; enum spellings are idiomatic. |
| OUT-01 | compliant | `Db2OperationOutcome` is `NotStarted`/`Confirmed`/`Unknown`. |
| OUT-02–05 | partial | `IDb2Error.OutcomeOverride` is set from the operation phase for timeouts and batch failures; the session records the first request flush. Raw transport exceptions still carry no outcome by themselves. |
| OUT-06–07 | compliant | No automatic replay of mutating SQL; a session with an uncertain reset/transport is discarded, and the pool now also discards a lease whose classified outcome is `Unknown`. |
| OUT-08 | compliant | A batch wrapper reports `confirmed` for a server-confirmed cause, `not-started` for a pre-write cause and `unknown` when the failing set may have executed. |
| OUT-09 | compliant | A confirmed `-952` produces `OperationCancelled` with `Confirmed`; unconfirmed settlement is `Unknown`. |
| DSN-01 | compliant | Case-insensitive key/value and `db2://` forms; last value wins. |
| DSN-02–03 | compliant | Every JDC canonical key and alias is accepted, including `currentschema`/`current_schema`, `kerberos_spn`, the principal aliases and the Kerberos file keys. The schema is validated before I/O, applied with a quoted identifier after connect, exposed by `Db2Session.CurrentSchema`, and restored by the pool reset path. |
| DSN-04–06 | compliant | Security mechanisms 0/3/7/9/11, TLS keys and `authentication=` shorthand behave as required. |
| DSN-07 | partial | No protocol-limit keys; the status text documents that they are not exposed. The SHOULD for LOB/streamed limits is not met. |
| DSN-08–10 | compliant | Unknown Kerberos keys fail closed; required identity values are checked before I/O; configuration dumps, if any, do not include the password. |
| LIM-01 | compliant | Protocol limits are fixed JDC defaults with documented hard ceilings in `Db2ProtocolLimits`; invalid `block_size` values are rejected with a configuration error instead of ignored. |
| LIM-02 | compliant | DSS 64 MiB, DDM object 64 MiB, reply chain 256 MiB / 4096 packets, LOB 16 MiB, result 32 MiB, streamed 1 GiB and 64 KiB chunks match JDC. |
| LIM-03 | compliant | The fixed values are within the hard ceilings; there are no configurable values to clamp. |
| LIM-04-06 | compliant | Materialized LOB/result limits fail closed; DSS/DDM/reply-chain caps reject the chain and discard the session. |
| LIM-07 | compliant | `Db2BatchOptions` bounds a batch chunk by rows and estimated encoded bytes; import staging is bounded by the same defaults. |
| LIM-08 | compliant | The process-wide 10 GiB spool budget is reserved before use and released on success, error and cancellation. |
| POOL-01-06 | compliant | Defaults, admission errors, reset/discard and drain/shutdown match the contract; `maxWaiters` accepts zero. |
| POOL-07 | compliant | `SessionCount`, `IdleSessionCount`, `ActiveSessionCount`, `WaitingCount`, `DiscardedSessionCount` and `AcquireTimeoutCount` are monotonic for the pool lifetime. |
| SES-01–03 | compliant | Connect/close, prepare/execute and commit/rollback behave as required; close does not commit. |
| SES-04–06 | compliant | The native `Db2Session` exposes `ChangeDatabaseAsync`, `SetCurrentSchemaAsync` and `CurrentSchema`. Name/schema validation happens before I/O, a change requires an open session and is refused during a transaction or on a pooled lease even for the current name, and a failed reconnect leaves the session closed. |
| SES-07 | compliant | Isolation is transaction-scoped (`BeginTransactionAsync(isolation)`), which restores the previous register at the end. |
| SES-08–10 | compliant | Transaction state is per session; batch order/progress and cancellation tokens are supported. |
| VAL-01–10 | compliant | Native rows use exact strings; the ADO adapter may return `decimal`/`DateTime` for supported precisions. NULL, binary, CP500 and metadata rules are met. |
| CAN-01–02 | compliant | Two-step interrupt and original-operation settlement are implemented. |
| CAN-03 | compliant | A confirmed `-952`/`57014` is `OperationCancelled` with `Confirmed` and preserves SQLCODE/SQLSTATE. |
| CAN-04–07 | compliant | Race handling, unconfirmed-session discard, idle cancellation and idempotent cancel are covered. |
| CAN-08 | compliant | A command/connect deadline raises `Timeout`; the outcome is `Confirmed` when SQLINTR produced `-952`, `Unknown` after a flush and `NotStarted` before any request was written. |
| CAN-09 | compliant | Cancellation is callable while the operation lock is held; confirmed `-952` leaves the session reusable. |
| BAT-01 | compliant | Ordered execution, stop-on-first-failure and completed/affected counts. |
| BAT-02 | compliant | `Db2BatchOptions` defaults to 256 rows / 8 MiB, validates `MaxRows` in 1..32767 and treats the byte limit as soft. The native path sends one set per request, which is stricter than the chunk bound. |
| BAT-03-04 | compliant | Batch cause/progress preservation; ADO import supports atomic vs commit-per-batch and atomic-only table lock. |
| BAT-05 | compliant | Import defaults are 256 rows, 8 MiB staged bytes, progress every 10,000 rows, atomic policy and table lock off. |
| BAT-06–09 | compliant | 1 GiB streamed budget, 64 KiB chunks, progressive LOB cancel, fail-closed materialization and the 1 GiB checksum workload are implemented. |
