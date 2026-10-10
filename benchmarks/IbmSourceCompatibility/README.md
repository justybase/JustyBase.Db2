# IBM.Data.Db2 source compatibility harness

This project intentionally references only `JustyBase.Db2`, not the official IBM NuGet package. It compiles the consumer-shaped API from `DB2ConnectionStringBuilder`/`DB2Connection.InfoMessage` through standard `DbCommand` and `IDataReader` to `DB2BulkCopy(TableLock)`, progress events, `Errors`, and `Close`. It also executes a null-elimination aggregate and verifies that `InfoMessage` delivers SQLSTATE `01003` with the expected values.

Run it against the configured live DB2 with `DB2_TEST_DSN` set:

```bash
dotnet run --project benchmarks/IbmSourceCompatibility/IbmSourceCompatibility.csproj -c Release
```

The harness creates one uniquely named compressed row-organized table, copies two rows including NULL, checks progress and readback, then drops the table in `finally`. It does not read or write JBL objects.
