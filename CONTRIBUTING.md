# Contributing

Thanks for contributing to `JustyBase.Db2`.

## Development principles

Changes should preserve these project properties unless the change intentionally revises the contract:

- fail closed when protocol/session state is uncertain,
- do not automatically replay mutating SQL or transaction control after uncertain transport outcomes,
- keep pool admission, materialization and importer staging bounded,
- preserve structured SQLCODE/SQLSTATE/error classification,
- avoid exposing credentials in logs/errors,
- preserve ADO.NET lifecycle semantics,
- keep public API changes intentional and reviewable.

## Development baseline

The current alpha targets .NET 10 and CI uses the pinned SDK version recorded in the workflow.

## Before submitting a change

Run:

```bash
dotnet build src/Db2.Tests/Db2.Tests.csproj --nologo
dotnet test src/Db2.Tests/Db2.Tests.csproj --no-build --nologo
dotnet pack src/Db2.Driver/Db2.Driver.csproj -c Release -o /tmp/db2-packages
dotnet pack src/Db2.IBMCompat/Db2.IBMCompat.csproj -c Release -o /tmp/db2-packages
python3 tools/sbom/check_package_contents.py /tmp/db2-packages
python3 tests/PackageConsumer/run_package_consumer_smoke.py /tmp/db2-packages
python3 tools/sbom/generate_cs_sbom.py /tmp/db2-packages
```

Live Db2 tests are separate and require `DB2_TEST_DSN`.

## Public API changes

Read [API-COMPATIBILITY.md](API-COMPATIBILITY.md).

Do not regenerate the API snapshot simply to make CI green. First decide whether the public contract is intentionally changing. Update README/changelog/migration documentation in the same change when behavior changes.

Be especially careful with the ADO.NET and `IBM.Data.Db2` compatibility surfaces: adding a public member can create a long-lived compatibility expectation.

## Tests

Prefer deterministic offline coverage for protocol framing, malformed replies, resource limits, cancellation ordering and ADO.NET lifecycle behavior. Use live tests for server-dependent behavior.

Do not weaken, skip or delete a failing test merely to make CI pass.

## Performance and allocations

For hot paths:

- avoid unnecessary array/string allocations and copies,
- preserve streaming and bounded buffers,
- preserve trim/AOT analyzer compatibility,
- measure meaningful changes,
- do not trade correctness or session synchronization for micro-optimizations.

## Secrets and test data

Never commit:

- real Db2 passwords or credential-bearing connection strings,
- GitHub/cloud tokens,
- private TLS keys,
- production database dumps or confidential rows,
- private qualification environment files.

Use environment variables and ignored local files for live tests.

## Commit scope

Prefer focused commits with a clear reason and tests. Documentation-only and test-only changes should not alter runtime behavior implicitly.
