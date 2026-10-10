# Allocation tick report

This small helper reads an EventPipe `.nettrace` file and ranks `GC/AllocationTick` samples by allocated type and full sampled call stack:

```sh
dotnet run --project benchmarks/AllocationTrace/AllocationTrace.csproj -c Release -- /tmp/int1m-alloc.nettrace
```

The reported byte amounts are runtime allocation-tick weights (sampled), not an exact allocation census. Use `P1Fetch` or BenchmarkDotNet `MemoryDiagnoser` for operation totals.
