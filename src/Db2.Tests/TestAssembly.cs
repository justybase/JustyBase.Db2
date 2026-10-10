using Xunit;

// The live integration tests share one Db2 server and use transactional state,
// SQLINTR, catalog scans, and temporary DDL. Serialize them to avoid test-runner
// concurrency changing server behavior or causing unrelated cleanup races.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
