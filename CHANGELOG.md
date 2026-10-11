# Changelog

## 0.1.0-alpha.2 - 2026-10-11

- `DB2BulkCopy` now batches up to 1024 rows by default and commits completed batches instead of committing each row; batch size is configurable and respects Db2 limits.
- Added IBM-vs-JustyBase BenchmarkDotNet comparisons for 500k/100k row fetches, bulk copy, writes, scalar commands, connection setup and managed allocations.
- Bulk-copy error handling preserves earlier completed batches and rolls back the batch containing a failing row.
- Published both packages as NuGet prereleases.
