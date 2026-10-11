# JustyBase.Db2 vs IBM provider — BenchmarkDotNet comparison

Head-to-head live comparison of this driver against the official IBM provider
(`Net.IBM.Data.Db2-lnx` 10.0.0.300) with BenchmarkDotNet 0.15.8
(latest stable) and its `MemoryDiagnoser` (managed allocations per operation).

Workloads use everyday column types only — no LOB extremes:

| Suite | Workload |
| --- | --- |
| `VsIbmNarrow500kBenchmarks` | 500k rows × `INT, VARCHAR(50), DECIMAL(12,2), TIMESTAMP`, `GetValues` and typed getters |
| `VsIbmMixed100kBenchmarks` | 100k rows × `INT, VARCHAR, DECIMAL, DATE, DOUBLE` with NULLs, plus scalar `COUNT(*)` |
| `VsIbmConnectBenchmarks` | connect + close with pooling off (no tables needed; fast smoke test) |
| `VsIbmWriteBenchmarks` | 10k-row `INSERT` (`INT, DECIMAL, TIMESTAMP`) in a rolled-back transaction: JustyBase importer (batch 500) vs IBM prepared single-row loop |
| `VsIbmBulkCopyBenchmarks` | 10k-row `DB2BulkCopy.WriteToServer(DataTable)`, `Default` and `TableLock`, JustyBase vs IBM; JustyBase sweeps 128/256/512/1024; row count checked and table emptied outside measurement |

## Requirements

- Linux x64, .NET 10 SDK, live Db2 LUW reachable over TCP.
- `DB2_TEST_DSN`, e.g. `db2://db2inst1:password@localhost:50000/TESTDB`.
- Fixture tables `JBBENCH_NARROW_500K` / `JBBENCH_MIXED_100K` are created and
  seeded automatically on first run (idempotent — complete tables are reused).
  They are kept afterwards so reruns are fast; drop `JBBENCH_*` manually to reseed.
- The bulk-copy fixture table is unique per run and removed automatically when
  the BenchmarkDotNet runner exits, including after a failed run.

## Run

```bash
dotnet build benchmarks/Db2VsIbmBench/Db2VsIbmBench.csproj -c Release
LD_LIBRARY_PATH="$PWD/benchmarks/Db2VsIbmBench/bin/Release/net10.0/clidriver/lib:$PWD/benchmarks/Db2VsIbmBench/bin/Release/net10.0/clidriver/lib/icc" \
DB2_TEST_DSN='db2://db2inst1:password@localhost:50000/TESTDB' \
  dotnet run --project benchmarks/Db2VsIbmBench/Db2VsIbmBench.csproj -c Release --no-build -- \
  --filter '*VsIbm*' --job short --artifacts /tmp/db2-vs-ibm-bdn
```

Fast smoke test first (no seeding):

```bash
# same env as above, filter '*VsIbmConnect*' instead of '*VsIbm*'
```

## Fairness notes

- Each driver uses its own dedicated connection with pooling disabled.
- Fetch benchmarks stream through `DbDataReader`; no `ORDER BY`
  (order-independent checksum verified in setup, outside measurement).
- JustyBase calls are `async`, IBM calls are sync — each driver's fastest
  idiomatic path.
- `MemoryDiagnoser` counts managed allocations only; IBM's native CLI memory
  is invisible to it (same caveat as in `../FetchComparison/README.md`).

## Results

Result tables live in [`../ReaderBench/BASELINE.md`](../ReaderBench/BASELINE.md),
not here — this README only documents how to reproduce the run.
