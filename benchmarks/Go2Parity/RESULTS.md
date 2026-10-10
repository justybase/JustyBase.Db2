# Db2 Driver Go2 Benchmark & Conformance Report

> Generated at: 2026-10-05T17:01:32.395Z

Historical cross-driver reference measurement. The orchestration spanned multiple repositories and is not included here; this repository contains the aggregate report only.

Baseline is **`go-db2`** (Pure Go DRDA harness `go-db2/go2parity`). Columns `JustyBase.Db2`, `ts-db2`, and `rust-db2` are the three native drivers under test. The method follows `go-db2/benchmarks/suite/{conformance,perf}.go`: identical `BENCH_*` seed and iteration counts, per-operation latency = wall time / operations, memory = allocator delta / operations. This file contains aggregate results; per-harness machine output is not included in the repository.

## 1. Semantic Conformance (Result Parity vs go-db2)

| Test Scenario | go-db2 | JustyBase.Db2 | ts-db2 | rust-db2 | Parity |
| :--- | :--- | :--- | :--- | :--- | :---: |
| **Basic Scalar (Int, String, Float)** | `i=42, s="Pure Go Db2 Driver", f=123.45` | `i=42, s="Pure Go Db2 Driver", f=123.45` | `i=42, s="Pure Go Db2 Driver", f=123.45` | `i=42, s="Pure Go Db2 Driver", f=123.45` | ✅ PASS |
| **Null Handling (sql.Null*)** | `ni.Valid=false, ns.Valid=false` | `ni.Valid=false, ns.Valid=false` | `ni.Valid=false, ns.Valid=false` | `ni.Valid=false, ns.Valid=false` | ✅ PASS |
| **Temporal (Current Date Format)** | `2026-10-05` | `2026-10-05` | `2026-10-05` | `2026-10-05` | ✅ PASS |
| **Numeric Precision (Smallint, Int, Bigint)** | `s=32767, i=2147483647, b=9223372036854775807` | `s=32767, i=2147483647, b=9223372036854775807` | `s=32767, i=2147483647, b=9223372036854775807` | `s=32767, i=2147483647, b=9223372036854775807` | ✅ PASS |
| **High Precision Decimal & DECFLOAT** | `dec=1234567.89, decfloat=12345678901234567890.1234567890` | `dec=1234567.89, decfloat=12345678901234567890.1234567890` | `dec=1234567.89, decfloat=12345678901234567890.1234567890` | `dec=1234567.89, decfloat=12345678901234567890.1234567890` | ✅ PASS |
| **BLOB Binary Integrity (SHA-256)** | `326e228882b798cecdd5fb44fddc9a7f843014a517611a71bcc6b3c8` | `326e228882b798cecdd5fb44fddc9a7f843014a517611a71bcc6b3c8` | `326e228882b798cecdd5fb44fddc9a7f843014a517611a71bcc6b3c8` | `326e228882b798cecdd5fb44fddc9a7f843014a517611a71bcc6b3c8` | ✅ PASS |
| **CLOB Text Streaming** | `len=1200, prefix=Pure-Go-Db2-CLOB-Test-Payload-` | `len=1200, prefix=Pure-Go-Db2-CLOB-Test-Payload-` | `len=1200, prefix=Pure-Go-Db2-CLOB-Test-Payload-` | `len=1200, prefix=Pure-Go-Db2-CLOB-Test-Payload-` | ✅ PASS |
| **VARGRAPHIC International Strings** | `Unicode / 日本語 / Português` | `Unicode / 日本語 / Português` | `Unicode / 日本語 / Português` | `Unicode / 日本語 / Português` | ✅ PASS |

Conformance: 8/8 scenarios in parity.

## 2. Performance & Resource Allocation (avg latency per op; ratio vs go-db2)

| Scenario | go-db2 | JustyBase.Db2 | ts-db2 | rust-db2 | Fastest native (vs go-db2) |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **Ping (Round-trip Latency)** | 0.396 ms | 0.349 ms | 0.309 ms | 0.286 ms | rust 1.39x faster vs go-db2 |
| **Simple Single-Row SELECT** | 0.767 ms | 0.340 ms | 0.415 ms | 0.340 ms | csharp 2.26x faster vs go-db2 |
| **Fetch 1,000 Rows Scan** | 2.227 ms | 0.854 ms | 3.111 ms | 0.870 ms | csharp 2.61x faster vs go-db2 |
| **Fetch 100,000 Rows Scan** | 162.515 ms | 75.429 ms | 122.397 ms | 59.291 ms | rust 2.74x faster vs go-db2 |
| **Prepared Statement INSERT** | 17.560 ms | 15.509 ms | 16.869 ms | 17.303 ms | csharp 1.13x faster vs go-db2 |
| **Read 500KB BLOB Payload** | 4.054 ms | 4.263 ms | 3.351 ms | 3.229 ms | rust 1.26x faster vs go-db2 |

Memory allocation is measured per harness: Go reports `runtime.MemStats` deltas, C# `GC.GetTotalAllocatedBytes`, TypeScript `heapUsed` deltas, and Rust counting-allocator bytes.

## 3. Key Observations

- The cross-driver harnesses were built before timing, and measurements used a single live Db2 instance.
- Runs were serial, with warmed single-session pools and setup excluded from timing, following the methodology in `go-db2/benchmarks/suite/perf.go`.
