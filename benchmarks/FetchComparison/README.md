# Db2 result fetch comparison

The benchmark runs one read-only query against a live Db2 database:

```sql
SELECT a.COLNO, a.COLNAME, b.COLNO
FROM SYSCAT.COLUMNS a CROSS JOIN SYSCAT.COLUMNS b
FETCH FIRST 10000 ROWS ONLY
```

It compares four paths: `Db2Session.QueryAsync` materialization, `Db2Session.QueryStreamAsync`, ADO.NET `DbDataReader.ReadAsync`, and IBM `DB2DataReader.Read`. Each mode warms once, then the runner rotates execution order over seven measured iterations. An order-independent SHA-256 multiset digest retains duplicate rows and verifies all four modes return the same values; it does not rely on result ordering. Connection setup is excluded. C# session, ADO.NET, and IBM each use a dedicated connection; IBM pooling is disabled. The custom driver uses `TCP_NODELAY`.

The runner records elapsed time and process-wide managed bytes allocated around each fetch. This allocation counter excludes IBM's native CLI memory and is not a peak-memory measurement. Row streaming avoids retaining the complete result set by construction, but this benchmark does not assign a numerical peak-memory figure.

This Linux host uses IBM's `Net.IBM.Data.Db2-lnx` 10.0.0.200 package, the Linux AMD64 platform sibling of [`Net.IBM.Data.Db2`](https://www.nuget.org/packages/Net.IBM.Data.Db2). The listing targets .NET 10. The package's native `clidriver/lib` must be on `LD_LIBRARY_PATH`. With `DB2_TEST_DSN` set, run:

```bash
dotnet build benchmarks/FetchComparison/FetchComparison.csproj -c Release
LD_LIBRARY_PATH="$PWD/benchmarks/FetchComparison/bin/Release/net10.0/clidriver/lib:$PWD/benchmarks/FetchComparison/bin/Release/net10.0/clidriver/lib/icc" \
  dotnet run --project benchmarks/FetchComparison/FetchComparison.csproj -c Release --no-build
```

## Sample run on 2026-09-30

Environment: local Db2 11.5.8.0 over `localhost:50000`, .NET 10 Release, Linux x64. Seven samples, 10,000 rows, all modes matched the same SHA-256 multiset digest:

| Fetch mode | Seven samples (ms) | Median (ms) | Median managed allocation |
| --- | --- | ---: | ---: |
| C# native materialized | 28.32, 29.10, 28.88, 29.40, 27.31, 28.30, 31.15 | 28.88 | 5.44 MiB |
| C# native row stream | 27.61, 28.45, 29.41, 30.10, 27.97, 30.42, 34.43 | 29.41 | 5.66 MiB |
| C# ADO.NET `DbDataReader` | 27.74, 28.71, 32.48, 29.71, 28.17, 29.40, 28.73 | 28.73 | 6.36 MiB |
| IBM `Net.IBM.Data.Db2-lnx` `DB2DataReader` | 31.46, 31.91, 33.95, 30.33, 30.47, 29.38, 30.72 | 30.72 | 4.93 MiB |

The three native C# medians were within 2.4% of one another and between 0.94× and 0.96× the IBM median time. This is comparable performance on this run, not evidence that the native driver is faster. The public row stream allocated 5.66 MiB cumulatively versus 5.44 MiB for materialization while avoiding retention of the full result. The ADO.NET wrapper allocated 6.36 MiB. IBM's 4.93 MiB excludes native CLI allocation. Query shape, catalog contents, server cache, JIT/GC activity, network latency, row width and result size can change the comparison materially.

An attempted `ORDER BY` over the catalog cross-join made Db2 sort the large intermediate relation and IBM returned SQLSTATE `57014`; it was removed so the benchmark measures the reader paths rather than an avoidable server sort. The current unordered query is checked with an order-independent multiset digest. An earlier 2026-09-28 materialized-only run measured C# at 12.95 ms and IBM at 17.58 ms with a weaker additive checksum and different warm-cache conditions; treat it as historical context, not a direct comparison. TS and Rust were not included in this new four-mode C# run.
