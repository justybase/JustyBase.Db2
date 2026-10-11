using System.Globalization;
using System.Text;
using JustyBase.Db2.AdoNet;

namespace Db2VsIbmBench;

/// <summary>
/// Self-contained live fixture for the JustyBase vs IBM comparison.
/// Tables are created and seeded once, then reused across runs (idempotent:
/// a complete table with a matching row count and ID checksum is kept).
/// Tables are intentionally NOT dropped after the run so repeated
/// BenchmarkDotNet invocations don't reseed 600k rows every time.
/// Drop JBBENCH_* manually to force a reseed.
/// </summary>
internal static class BenchFixture
{
    public const string Narrow500kTable = "JBBENCH_NARROW_500K";
    public const long Narrow500kRows = 500_000;
    public const string Narrow500kSql = "SELECT ID, V, A, TS FROM " + Narrow500kTable;

    public const string Mixed100kTable = "JBBENCH_MIXED_100K";
    public const long Mixed100kRows = 100_000;
    public const string Mixed100kSql = "SELECT ID, V, A, D, X FROM " + Mixed100kTable;

    private const int InsertBatchRows = 500;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly DateTime NarrowBase = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime MixedDateBase = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    public static Task EnsureNarrow500kAsync(Db2AdoConnection connection, CancellationToken cancellationToken = default)
        => EnsureAsync(
            connection,
            Narrow500kTable,
            Narrow500kRows,
            $"CREATE TABLE {Narrow500kTable} (ID INTEGER NOT NULL PRIMARY KEY, V VARCHAR(50) NOT NULL, A DECIMAL(12,2) NOT NULL, TS TIMESTAMP NOT NULL)",
            AppendNarrowBatch,
            cancellationToken);

    public static Task EnsureMixed100kAsync(Db2AdoConnection connection, CancellationToken cancellationToken = default)
        => EnsureAsync(
            connection,
            Mixed100kTable,
            Mixed100kRows,
            $"CREATE TABLE {Mixed100kTable} (ID INTEGER NOT NULL PRIMARY KEY, V VARCHAR(50), A DECIMAL(12,2), D DATE, X DOUBLE)",
            AppendMixedBatch,
            cancellationToken);

    public static async Task<long> CountAsync(Db2AdoConnection connection, string table, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), Inv);
    }

    private static async Task EnsureAsync(
        Db2AdoConnection connection,
        string table,
        long expectedRows,
        string createSql,
        Action<StringBuilder, long, int> appendBatch,
        CancellationToken cancellationToken)
    {
        if (await IsCompleteAsync(connection, table, expectedRows, cancellationToken).ConfigureAwait(false))
        {
            Console.WriteLine($"[fixture] {table} already seeded ({expectedRows} rows), reusing.");
            return;
        }

        Console.WriteLine($"[fixture] seeding {table} ({expectedRows} rows)...");
        await DropQuietAsync(connection, table, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, createSql, cancellationToken).ConfigureAwait(false);

        var batch = new StringBuilder(64 * 1024);
        var batches = 0;
        for (var start = 1L; start <= expectedRows; start += InsertBatchRows)
        {
            var count = (int)Math.Min(InsertBatchRows, expectedRows - start + 1);
            batch.Clear();
            batch.Append("INSERT INTO ").Append(table).Append(" VALUES ");
            appendBatch(batch, start, count);
            await ExecuteAsync(connection, batch.ToString(), cancellationToken).ConfigureAwait(false);
            if (++batches % 200 == 0)
                Console.WriteLine($"[fixture] {table}: {Math.Min(start + InsertBatchRows - 1, expectedRows)}/{expectedRows} rows...");
        }

        if (!await IsCompleteAsync(connection, table, expectedRows, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"Seeding {table} failed verification.");

        Console.WriteLine($"[fixture] {table} seeded and verified.");
    }

    private static async Task<bool> IsCompleteAsync(Db2AdoConnection connection, string table, long expectedRows, CancellationToken cancellationToken)
    {
        long count;
        long sum;
        try
        {
            count = await CountAsync(connection, table, cancellationToken).ConfigureAwait(false);
            if (count != expectedRows)
                return false;
            await using var command = connection.CreateCommand();
            // SUM(INTEGER) overflows 32-bit INT in Db2; cast first.
            command.CommandText = $"SELECT SUM(CAST(ID AS BIGINT)) FROM {table}";
            sum = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), Inv);
        }
        catch
        {
            return false;
        }

        return sum == expectedRows * (expectedRows + 1) / 2;
    }

    private static async Task DropQuietAsync(Db2AdoConnection connection, string table, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(connection, $"DROP TABLE {table}", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Table did not exist; nothing to drop.
        }
    }

    private static async Task ExecuteAsync(Db2AdoConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AppendNarrowBatch(StringBuilder batch, long start, int count)
    {
        for (var offset = 0; offset < count; offset++)
        {
            var id = start + offset;
            if (offset != 0)
                batch.Append(',');
            batch.Append('(').Append(id).Append(",'v-").Append(id).Append("',")
                .Append(FormatCents((((ulong)id * 37) % 100_000))).Append(",'")
                .Append(NarrowBase.AddSeconds(id).ToString("yyyy-MM-dd-HH.mm.ss.ffffff", Inv)).Append("')");
        }
    }

    private static void AppendMixedBatch(StringBuilder batch, long start, int count)
    {
        for (var offset = 0; offset < count; offset++)
        {
            var id = start + offset;
            if (offset != 0)
                batch.Append(',');

            var cents = (((ulong)id * 37) % 100_000);
            var date = MixedDateBase.AddDays(id % 365).ToString("yyyy-MM-dd", Inv);
            var dbl = (id * 0.5).ToString(Inv);

            batch.Append('(').Append(id).Append(',');
            batch.Append(id % 7 == 0 ? "NULL" : $"'name-{id}'").Append(',');
            batch.Append(id % 11 == 0 ? "NULL" : FormatCents(cents)).Append(',');
            batch.Append(id % 13 == 0 ? "NULL" : $"'{date}'").Append(',');
            batch.Append(id % 17 == 0 ? "NULL" : dbl).Append(')');
        }
    }

    private static string FormatCents(ulong cents)
        => string.Create(Inv, $"{cents / 100}.{cents % 100:D2}");
}
