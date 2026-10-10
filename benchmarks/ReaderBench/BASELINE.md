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
