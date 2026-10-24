# Qualification and compatibility

This document summarizes the compatibility baseline and automated test lanes for the current alpha, along with the additional checks used for release qualification.

## Current tested baseline

Maintainer qualification currently targets **Linux x64** with:

- **Db2 LUW 12.1.5** over TCP and TLS.
- **.NET SDK 10.0.112** (`net10.0`) and **11.0.100-rc.2** (`net11.0`).

## Qualification gates

A release-candidate revision is expected to pass:

- the complete offline .NET/unit/protocol suite,
- live TCP protocol checks against Db2,
- TLS behavior,
- IBM-provider parity checks for selected values and operations,
- cancellation / SQL interrupt scenarios,
- **1 GiB** streamed BLOB checksum verification,
- bounded RSS and spool usage,
- a measured **15-minute soak** after warm-up,
- package/SBOM/content validation,
- exact source revision/runtime recording.

Any source change after a successful qualification invalidates that qualification for release purposes until the changed revision is tested again.

## Automated test lanes

- Every pull request runs the offline unit/protocol suite, package validation, consumer smoke and SBOM generation.
- Every pull request runs platform smoke tests on Linux, Windows and macOS. These exercise the managed client build and protocol/API surface without claiming live Db2 qualification on each operating system.
- Every pull request runs an isolated Db2 LUW live smoke suite and IBM-provider parity checks. Both use pinned Db2 Community images; IBM's provider is used only by the parity test project.
- The manual `Live Db2 Qualification (C#)` workflow runs the full Db2 live suite followed by three fresh MIT KDC/Kerberos end-to-end environments.
- Extended release qualification includes TLS behavior, the 1 GiB LOB workload, resource budgets and a measured soak. These checks are separate from the pull-request CI lanes.

The offline workflow excludes the live categories and fails if its TRX report contains skipped or non-runnable tests. Live workflow artifacts contain redacted test output and expire after a short retention period.

## Qualification budgets

The maintained cross-driver qualification currently uses:

- up to **10 concurrent sessions**,
- **512 MiB** maximum process RSS,
- **1 GiB** large streamed LOB workload,
- process-wide **10 GiB** spool budget,
- **15 minutes** measured soak after warm-up.

These are release gates, not universal application defaults.

## Current public resource limits

The current alpha deliberately bounds materialization:

- **16 MiB** maximum materialized LOB,
- **32 MiB** maximum materialized query result,
- bounded progressive locator reads,
- bounded importer staging,
- fail-closed session disposal when protocol cleanup cannot be proven.

## Scope limitations

Passing the maintained qualification does not imply verification of every Db2 environment.

Notably:

- Db2 for z/OS and IBM i are outside the maintained LUW qualification baseline,
- trusted-context switching depends on server configuration/privileges,
- Kerberos explicit keytab/ccache support is verified only on Linux x86_64 against an MIT KDC and Db2 LUW 12.1.5 (`scripts/test-db2-kerberos.sh`),
- exact LOB representations can vary by Db2 version and query shape,
- the `IBM.Data.Db2` facade is a selected compatibility subset,
- the project is independent and not IBM-certified.

Validate materially different deployments before relying on the driver.
