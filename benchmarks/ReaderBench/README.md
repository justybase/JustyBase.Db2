# DbDataReader benchmarks

`ReaderBench` uses BenchmarkDotNet's `MemoryDiagnoser` for the seven live reader cases, an offline DSS-frame codec case, and an offline QRYDTA row-decoder fixture. The live benchmark expects the shared `P1BENCH` tables and `DB2_TEST_DSN`; use `localhost` for the DB2 host.

```sh
DB2_TEST_DSN='db2://db2inst1:password@localhost:50000/TESTDB' \
  dotnet run --project benchmarks/ReaderBench/ReaderBench.csproj -c Release -- \
  --filter '*Db2ReaderBenchmarks*' --job short --artifacts /tmp/db2-reader-bdn
```

Scenarios cover: one `INT` column × 1M (`GetInt32`), five narrow columns × 100K, 33 columns with NULLs × 100K, VARCHAR/CLOB/BLOB × 1K, DECIMAL/TIMESTAMP × 10K, `SequentialAccess` BLOB × 8M, and repeated small-result `ExecuteReader`/close. The wide/narrow projections use existing immutable `P1BENCH` tables. The offline codec and row-decoder fixtures are deterministic and QRYDTA-shaped; they are **synthetic, not packet captures from DB2**.

Additional value-only narrow/wide projections isolate typed getters from string
allocations. `Db2PoolBenchmarks` covers both a reused logical connection and a
new logical connection leasing a warmed pool. `Db2WriteBenchmarks` covers a
repeated no-match DELETE and 1,000-row importer batches of 128 with an explicit
transaction and rollback. Its source DataTable is created outside measurement;
reader creation, transaction control and rollback are included. The temporary
table is unique to the run and dropped at cleanup. These benchmarks require DDL
permissions and never modify the immutable P1BENCH tables.

To run the pool and write workloads against a Db2 instance, set the DSN in the
environment and select those benchmark classes:

```sh
DB2_TEST_DSN='db2://db2inst1:password@localhost:50000/TESTDB' \
  dotnet run --project benchmarks/ReaderBench/ReaderBench.csproj -c Release -- \
  --filter '*Db2PoolBenchmarks*' '*Db2WriteBenchmarks*' --job short \
  --artifacts /tmp/db2-operations-bdn
```

For a restricted environment without socket access, only synthetic offline
benchmarks can run. Use `--inProcess`; label those results separately from live
measurements and the normal out-of-process toolchain.

To collect allocation ticks for the typed INT reader and print the top sampled allocation types/stacks:

```sh
DB2_TEST_DSN='db2://db2inst1:password@localhost:50000/TESTDB' \
LD_LIBRARY_PATH='benchmarks/P1Fetch/bin/Release/net10.0/clidriver/lib:benchmarks/P1Fetch/bin/Release/net10.0/clidriver/lib/icc' \
dotnet-trace collect --profile gc-verbose --output /tmp/int1m-alloc.nettrace -- \
  dotnet benchmarks/P1Fetch/bin/Release/net10.0/P1Fetch.dll \
  --dataset INT_1M --modes ado-typed --warmups 0 --samples 1
dotnet run --project benchmarks/AllocationTrace/AllocationTrace.csproj -c Release -- \
  /tmp/int1m-alloc.nettrace
```

`P1Fetch` performs one untimed checksum verification pass and one timed pass in the trace command. Allocation tick totals therefore include both passes and are sampled/weighted by the runtime; use the P1 `allocatedBytes` and BDN `Allocated` columns for exact per-run allocation totals.

## Offline DRDA replay

The deterministic loopback suite, its fixtures, validation, profiling and
block-size diagnostics are described in [Db2ReplayServer](../Db2ReplayServer/README.md).
