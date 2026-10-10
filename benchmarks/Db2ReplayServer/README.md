# Deterministic DRDA replay

This executable is a small synthetic DRDA peer for the existing ReaderBench
suite and offline regression tests. It accepts loopback connections only. It
does not require Db2, credentials, captured authentication exchanges, or mutable
database state. It is not a general Db2 emulator or a protocol reference.

The peer validates negotiation, initialization, direct prepare/open, cursor ID,
continue-query requests and transaction completion. Unexpected requests fail the
run. Each query has its own fetch position; reusing a connection starts the same
immutable fixture again. The benchmark launches the peer in another process,
so its fixture generation, writes and allocations are excluded from client
allocation and CPU measurements. TCP scheduling still contributes variance.

Fixtures use IDs starting at 1 with checksum `N * (N + 1) / 2`:

| Name | Rows | Columns |
| --- | ---: | --- |
| Int | 1,000,000 | INT |
| Mixed | 100,000 | INT, BIGINT, DECIMAL(12,2), TIMESTAMP(6), VARCHAR, nullable VARCHAR |
| Wide | 100,000 | 40 columns repeating those types |
| Discard | 250,000 | Six mixed columns |
| Small | 1 | INT |

Values and NULL patterns are defined by `ReplayFixture.Value`; native decimal
and timestamp text expectations are defined by `NativeValue`. SQLDARD metadata
and QRYDSC descriptors are generated separately from the row payload. All data
is synthetic. Existing hand-authored golden vectors remain independent checks
on the production codec used to frame generated responses.

Fetch shapes are precomputed at 16384, 32767 and 65535 bytes. Splitting a byte
stream deliberately exercises partial rows and continued DSS objects. This is
a client stress model: a requested QRYBLKSZ alone does not prove Db2 emits this
exact shape or that MAXBLKEXT has these synthetic semantics. Live traces and
live correctness gates are required before changing any production default.

```sh
dotnet run --project benchmarks/ReaderBench -c Release -- \
  --filter '*Db2ReplayBenchmarks.*' '*Db2ReplayBlockSizeBenchmarks.*' \
  --launchCount 3 --warmupCount 5 --iterationCount 15 --outliers DontRemove \
  --artifacts /tmp/db2-replay
python3 benchmarks/ReaderBench/summarize-replay.py \
  /tmp/db2-replay /tmp/db2-replay/metrics.csv
```

Use the normal out-of-process BDN toolchain when possible. If `--inProcess` is
required, label it explicitly and use the identical toolchain for comparison.
Iteration cleanup validates the returned checksum and peer completion outside
measurement; it intentionally fixes invocation count at one. Small-result
iteration times can therefore be short: inspect variance instead of treating
small differences as reliable improvements. Setup independently checks every
ADO value and the native full-result count/checksum. A peer failure or mismatch
fails the benchmark. Gen0/1/2 in the derived CSV are collections per 1000 ops,
including explicit zeros. B/row and rows/sec use consumed rows; early-dispose
results must additionally be read with operation time/allocation and peer wire
bytes/fetch counts, not interpreted as full-result throughput.

For client CPU, exact process allocation totals, peer counts, or an allocation
trace entry point:

```sh
dotnet run --project benchmarks/ReaderBench -c Release -- \
  --replay-probe Int NativeStream_ReadAll 65535 9
dotnet-trace collect --profile gc-verbose --providers JustyBase.Db2.ReplayProbe \
  --output /tmp/native.nettrace -- \
  dotnet benchmarks/ReaderBench/bin/Release/net10.0/ReaderBench.dll \
  --replay-probe Int NativeStream_ReadAll 65535 9
dotnet run --project benchmarks/AllocationTrace -c Release -- \
  /tmp/native.nettrace --measured-only
```

Measurement markers exclude setup and warmup when `--measured-only` is used.
The child peer does not inherit the client's diagnostic startup port. Type
weights are sampled, not exact per-type allocation totals. Per-operation allocation
totals exclude setup, checks, counter serialization and peer work. BDN remains
the primary performance comparison. Fixture objects holding payloads and peer
processes are released at cleanup; the client benchmark retains no row corpus.

## Block-size diagnostics

An optional build contains internal counters; normal builds compile them out.
Use a separate output snapshot and rebuild normally afterwards. Instrumented
times include counter overhead and are diagnostic evidence, not BDN acceptance
measurements. Run these experiments serially.

```sh
dotnet build benchmarks/ReaderBench -c Release -p:Db2PerformanceCounters=true
# Supply DB2_TEST_DSN via the local environment; do not put it on a command line.
mkdir -p /tmp/db2-block-diagnostics
for size in 16384 32767 65535; do
  dotnet benchmarks/ReaderBench/bin/Release/net10.0/ReaderBench.dll \
    --replay-probe Int ReadFirstColumn "$size" 9 \
    > "/tmp/db2-block-diagnostics/replay-Int-${size}.csv"
  dotnet benchmarks/ReaderBench/bin/Release/net10.0/ReaderBench.dll \
    --live-block-probe Int "$size" 9 \
    > "/tmp/db2-block-diagnostics/live-Int-${size}.jsonl"
done
python3 benchmarks/ReaderBench/summarize-block-counters.py \
  /tmp/db2-block-diagnostics \
  /tmp/db2-block-counters.csv
dotnet build benchmarks/ReaderBench -c Release -p:Db2PerformanceCounters=false
```

The example uses the Int fixture. Live diagnostics also support Wide, Small and
Lob against the existing P1BENCH corpus. The typed first-column loop checks
row count, ID checksum, metadata and absence of warnings outside timing.
The LOB case exercises automatic safe draining rather than LOB consumption.
Socket counters count calls at the driver's stream boundary, so live socket
diagnostics reject TLS. Read call counts can vary with TCP scheduling.

`DssFrames` includes continuation segments; `QueryDataPackets` counts logical
QRYDTA objects; `Continuations` counts additional DSS segments. Peer
`ContinuedPackets` counts QRYDTA objects having at least one continuation.
Transport copy counters include buffer growth, compaction, continuation removal,
trimming and compatibility read-ahead. Row-buffer bytes count QRYDTA appends;
discard bytes count incomplete-row tail appends. They do not count every CPU
memory access, array clearing, or decoded object allocation.

The peer implements one QRYDTA block per fetch. It deliberately does not model
Db2 LUW MAXBLKEXT extension scheduling, SQLINTR's secondary connection, LOB
locators, prepared parameter execution or procedure result sets. Existing
protocol tests and the live suite cover those behaviors. Never infer a new
production block-size default from replay alone.
