# JDC v1 status — C# (.NET)

Contract: [`docs/DRIVER-CONTRACT.md`](DRIVER-CONTRACT.md) (JDC v1).
Last reviewed: 2026-11-08.

This file is the repo-specific conformance record. It is not part of the
normative text. `compliant` means every `MUST` in the group is met; `partial`
means some are met and the rest are listed; `gap` means the group is not met.
`gap` and `partial` entries are implementation work for the next conformance
pass; this file records them, it does not waive the requirement.

## Summary

| Status | Requirements |
| --- | --- |
| compliant | ERR-05–09, OUT-01, OUT-06–07, DSN-01, DSN-04–06, DSN-08–10, LIM-03–06, POOL-01–06, SES-01–03, SES-07–10, VAL-01–10, CAN-01–02, CAN-04–07, CAN-09, BAT-01, BAT-03–04, BAT-06–09 |
| partial | ERR-01, ERR-03–04, OUT-08–09, DSN-07, LIM-01, LIM-08 |
| gap | ERR-02, OUT-02–05, DSN-02–03, LIM-02, LIM-07, POOL-07, SES-04–06, CAN-03, CAN-08, BAT-02, BAT-05 |

## Details

| Requirements | Status | Notes |
| --- | --- | --- |
| ERR-01 | partial | Most failures implement `IDb2Error`. `Db2CommandTimeoutException` (a `TimeoutException`) and `Db2BatchCancellationException` (an `OperationCanceledException`) expose no kind/outcome. |
| ERR-02 | gap | `Db2ErrorKind` has no `OperationCancelled`, `Timeout` or `Other`; it does have `BatchExecution`. |
| ERR-03–04 | partial | SQL/auth/authz/conversion/transport/protocol classification is stable; timeout and confirmed-cancellation classification are missing. |
| ERR-05–08 | compliant | SQLCODE/SQLSTATE and batch progress are preserved; messages use `SQLCODE=… SQLSTATE=…`; secrets are covered by tests. |
| ERR-09 | compliant | No public serialized error form; enum spellings are idiomatic. |
| OUT-01 | compliant | `Db2OperationOutcome` is `NotStarted`/`Confirmed`/`Unknown`. |
| OUT-02–05 | gap | `IDb2Error.Outcome` is derived from kind plus `SqlCode`, not from the operation phase. Transport failures before and after a write share the same result. |
| OUT-06–07 | compliant | No automatic replay of mutating SQL; a session with an uncertain reset/transport is discarded. |
| OUT-08 | partial | A batch error exposes completed sets and rows, but its outcome does not distinguish an in-flight failing set from a confirmed failure. |
| OUT-09 | partial | A confirmed `-952` produces `Confirmed`, but its kind remains `SqlError`. |
| DSN-01 | compliant | Case-insensitive key/value and `db2://` forms; last value wins. |
| DSN-02–03 | gap | `currentschema`/`current_schema` is not accepted and there is no current-schema setter/getter on the native session. All other core aliases are present. |
| DSN-04–06 | compliant | Security mechanisms 0/3/7/9/11, TLS keys and `authentication=` shorthand behave as required. |
| DSN-07 | partial | No protocol-limit keys; the status text documents that they are not exposed. The SHOULD for LOB/streamed limits is not met. |
| DSN-08–10 | compliant | Unknown Kerberos keys fail closed; required identity values are checked before I/O; configuration dumps, if any, do not include the password. |
| LIM-01 | partial | Protocol limits are constants; `block_size` outside 1024..1048576 is ignored rather than rejected. |
| LIM-02 | gap | DSS 1 GiB, DDM object 512 MiB and reply chain 512 MiB exceed the JDC defaults (64 MiB / 64 MiB / 256 MiB). LOB 16 MiB, result 32 MiB, packets 4096, streamed 1 GiB and 64 KiB chunks match. |
| LIM-03 | compliant | Constants are within the hard ceilings; there are no configurable values to clamp. |
| LIM-04–06 | compliant | Materialized LOB/result limits fail closed; reply-chain caps reject the chain. |
| LIM-07 | gap | `ExecuteBatchAsync` has no row/byte bound; import staging is bounded. |
| LIM-08 | partial | A process-wide spool budget exists and is released on all paths; it is not publicly configurable. |
| POOL-01–06 | compliant | Defaults, admission errors, reset/discard and drain/shutdown match the contract. |
| POOL-07 | gap | Public counters cover total/idle/active/waiting only; discarded sessions and acquire timeouts are not exposed. |
| SES-01–03 | compliant | Connect/close, prepare/execute and commit/rollback behave as required; close does not commit. |
| SES-04–06 | gap | `ChangeDatabase` exists only on the ADO.NET connection; the native `Db2Session` has no `change_database`, current-schema setter or getter. |
| SES-07 | compliant | Isolation is transaction-scoped (`BeginTransactionAsync(isolation)`), which restores the previous register at the end. |
| SES-08–10 | compliant | Transaction state is per session; batch order/progress and cancellation tokens are supported. |
| VAL-01–10 | compliant | Native rows use exact strings; the ADO adapter may return `decimal`/`DateTime` for supported precisions. NULL, binary, CP500 and metadata rules are met. |
| CAN-01–02 | compliant | Two-step interrupt and original-operation settlement are implemented. |
| CAN-03 | gap | A confirmed `-952`/`57014` preserves SQLCODE/SQLSTATE and `Confirmed`, but the kind is `SqlError`, not `OperationCancelled`. |
| CAN-04–07 | compliant | Race handling, unconfirmed-session discard, idle cancellation and idempotent cancel are covered. |
| CAN-08 | gap | A command timeout raises `Db2CommandTimeoutException` without a JDC kind/outcome. |
| CAN-09 | compliant | Cancellation is callable while the operation lock is held; confirmed `-952` leaves the session reusable. |
| BAT-01 | compliant | Ordered execution, stop-on-first-failure and completed/affected counts. |
| BAT-02 | gap | `ExecuteBatchAsync` has no bounded option type. |
| BAT-03–04 | compliant | Batch cause/progress preservation; ADO import supports atomic vs commit-per-batch and atomic-only table lock. |
| BAT-05 | gap | Import default `BatchSize` is 128, JDC requires 256; the other import defaults match. |
| BAT-06–09 | compliant | 1 GiB streamed budget, 64 KiB chunks, progressive LOB cancel, fail-closed materialization and the 1 GiB checksum workload are implemented. |
