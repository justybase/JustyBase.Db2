# Reader benchmark reference measurements

These measurements describe one Linux x64 environment: .NET 10.0.12, Ubuntu 24.04.5, AMD Ryzen 7 7840HS, and Db2 LUW 11.5.8.0 at `localhost:50000`. Live BenchmarkDotNet values use `ShortRun` with three measured iterations. Treat them as reference measurements for this environment, not as performance guarantees.

## Paired fetch workloads

The cross-driver P1 workloads validate row counts and a frozen multiset checksum before timing. The aggregate report is in [P1Fetch results](../P1Fetch/RESULTS.md). Per-sample machine output is not included in the repository.

| Dataset / mode | Median time | Allocated / operation | Bytes / row |
| --- | ---: | ---: | ---: |
| INT_1M / C# ADO `GetValue` | 264.43 ms | 150,249,816 B | 150.2 |
| INT_1M / C# ADO `GetInt32` | 246.05 ms | 150,250,064 B | 150.3 |
| INT_1M / IBM reader | 216.64 ms | 120,022,496 B | 120.0 |
| NARROW_500K / C# ADO reader | 209.72 ms | 127,939,088 B | 255.9 |
| NARROW_500K / C# native stream | 185.80 ms | 127,900,192 B | 255.8 |

## Reader cases

| Scenario | Mean | Allocated / operation |
| --- | ---: | ---: |
| INT × 1M, `GetInt32` | 246.094 ms | 146,719.97 KB |
| Narrow 5 columns × 100K | 50.877 ms | 30,718.88 KB |
| Wide 33 columns with NULLs × 100K | 107.346 ms | 45,145.55 KB |
| VARCHAR/CLOB/BLOB × 1K | 399.304 ms | 25.65 MB |
| DECIMAL/TIMESTAMP × 10K | 13.764 ms | 9,502.9 KB |
| SequentialAccess BLOB × 8M | 44.085 ms | 24,631.46 KB |
| Small result, execute and close | 730.073 µs | 13.54 KB |

## Offline protocol fixtures

The synthetic 32 KiB DSS/QRYDTA-shaped frame is parsed in 3.299 µs/op with 32.15 KB allocated per operation. The synthetic offline row decoder processes 10,000 one-column INT rows in 519.244 µs/op and allocates 1.11 MB/op (about 116 B/row). These fixtures characterize local codec and decoder work; they are not Db2 packet captures.

## JustyBase vs IBM provider (BenchmarkDotNet, live Db2)

Head-to-head comparison from `benchmarks/Db2VsIbmBench/` (see its README for
reproduction steps). Everyday column types only — no LOB extremes.
Final complete run measured 2026-10-11: Linux x64, .NET 10.0.12, Db2 LUW
11.5.8.0 at `localhost:50000`, BenchmarkDotNet **0.15.8** (latest stable)
with `MemoryDiagnoser`, `--job short` (3 warmup + 3 measured iterations),
official provider `Net.IBM.Data.Db2-lnx` **10.0.0.300**. The 28 benchmarks
completed successfully. ShortRun intervals are broad for some live cases;
read small time differences as approximate.
Fixture tables `JBBENCH_NARROW_500K` (500k × `INT, VARCHAR(50), DECIMAL(12,2),
TIMESTAMP`) and `JBBENCH_MIXED_100K` (100k × `INT, VARCHAR, DECIMAL, DATE,
DOUBLE` with NULLs) were seeded once and verified by row count plus
`SUM(CAST(ID AS BIGINT))` on both drivers before timing.

### Fetch 500k narrow rows (streaming reader, no ORDER BY)

| Driver / access | Mean | Allocated / op | B / row | vs matching IBM |
| --- | ---: | ---: | ---: | ---: |
| JustyBase ADO `GetValues` | 439.5 ms | 57.61 MB | 121 | 0.67× |
| JustyBase typed getters | 424.9 ms | 19.46 MB | 41 | 0.70× |
| IBM typed getters | 603.5 ms | 358.21 MB | 751 | 1.00× |
| IBM `GetValues` | 655.8 ms | 442.14 MB | 927 | 1.00× |

### Fetch 100k mixed rows incl. NULLs + scalar

| Scenario | JustyBase | IBM | Notes |
| --- | ---: | ---: | --- |
| Fetch 100k mixed, `GetValues` | 179.0 ms / 13,580 KB (139 B/row) | 120.3 ms / 85,303 KB (873 B/row) | JustyBase slower in this run; ~6.3× fewer managed bytes |
| Scalar `COUNT(*)` over 100k | 3.85 ms / 4.64 KB | 3.31 ms / 18.63 KB | similar range; JustyBase lower allocation |
| Connect + close, pooling off | 1.172 s / 15.02 KB | 1.182 s / 12.84 KB | similar; dominated by TCP+auth handshake |

### INSERT 10k typed rows in a rolled-back transaction

| Driver / idiom | Mean | Allocated / op |
| --- | ---: | ---: |
| JustyBase importer, batch 500 | 57.53 ms | 2.94 MB |
| IBM prepared single-row loop | 2,723.64 ms | 17.94 MB |

The write comparison contrasts idioms, not the same statement shape:
bounded multi-row INSERTs vs the typical prepared-loop pattern
(10k server round trips). This is a DML idiom reference, not the main
bulk-copy comparison below.

### BulkCopy 10k rows via DB2BulkCopy.WriteToServer(DataTable)

Same 10k typed rows (`INT, DECIMAL, TIMESTAMP`) from the same `DataTable`;
the table is emptied in `IterationCleanup` (outside measurement) and the
copied row count is verified there. JustyBase batch sizes 128/256/512/1024
were swept; IBM's public class does not expose a batch-size property. The new
default is 1024 rows, bounded further by Db2 marker, SQL-text and memory limits.

| `DB2BulkCopy` mode / batch | JustyBase mean | IBM mean | JustyBase allocated | IBM allocated |
| --- | ---: | ---: | ---: | ---: |
| `Default`, 128 | 1,091.56 ms | 202.71 ms | 2.81 MB | 15.26 MB |
| `Default`, 256 | 237.78 ms | 122.45 ms | 3.00 MB | 15.26 MB |
| `Default`, 512 | 155.97 ms | 114.48 ms | 3.92 MB | 15.26 MB |
| `Default`, 1024 (new default) | 105.26 ms | 103.10 ms | 5.68 MB | 15.26 MB |
| `TableLock`, 1024 | 66.98 ms | 92.23 ms | 5.65 MB | 15.26 MB |

The new `Default` path commits bounded multi-row batches instead of every
row. At 1024 rows its mean time was comparable to IBM's native bulk path,
with ~2.7× lower managed allocation. `TableLock` was faster in this sample
and allocated ~2.7× less; use it when an exclusive table lock is acceptable.
The 128-row JustyBase default sample was a clear outlier (1.09 s), and
ShortRun confidence intervals are wide for several operations. Treat means
as reference points, not performance guarantees. 1024 is the selected
default from the tested sweep, not a universal optimum.

Allocation figures are managed memory per operation (`MemoryDiagnoser`):
IBM's native CLI memory is invisible to them, so IBM's true footprint is
larger than shown. JustyBase calls are `async`, IBM calls are sync — each
driver's fastest idiomatic path. Server cache, JIT/GC activity, network
latency and row width can move these numbers; treat them as reference
measurements for this environment, not guarantees.
