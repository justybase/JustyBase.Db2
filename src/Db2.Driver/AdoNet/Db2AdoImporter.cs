using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using JustyBase.Db2.Types;

namespace JustyBase.Db2.AdoNet;

/// <summary>
/// Imports a forward-only <see cref="IDataReader"/> using bounded multi-row INSERTs.
/// The importer owns an atomic transaction by default; set <see cref="Db2AdoImportOptions.CommitEveryRows"/>
/// to opt into durable intervals.
/// </summary>
public static class Db2AdoImporter
{
    private const int MaximumMarkersPerStatement = 32_767;
    // FDODSC groups continue with CPT after 84 fields. Live IBM readback verifies
    // multi-group writes through 4000 markers; statement and byte limits still apply.
    private const int MaximumSqlCharacters = 32_000;

    /// <summary>
    /// Copies rows into an existing Db2 table. The source remains owned by the caller.
    /// When a <see cref="DbDataReader"/> is supplied, its asynchronous read path is used.
    /// </summary>
    public static async Task<Db2AdoImportResult> ImportAsync(
        Db2AdoConnection connection,
        IDataReader source,
        string destinationTable,
        IReadOnlyList<string>? destinationColumns = null,
        Db2AdoImportOptions? options = null,
        DbTransaction? transaction = null,
        Action<Db2AdoImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationTable);
        options ??= new Db2AdoImportOptions();
        options.Validate();

        if (source.IsClosed) throw new ArgumentException("The import source reader is closed.", nameof(source));
        var fieldCount = source.FieldCount;
        if (fieldCount < 1) throw new ArgumentException("The import source must have at least one column.", nameof(source));
        var columns = destinationColumns is null
            ? Enumerable.Range(0, fieldCount).Select(source.GetName).ToArray()
            : destinationColumns.ToArray();
        if (columns.Length != fieldCount)
            throw new ArgumentException($"The source has {fieldCount} columns but {columns.Length} destination columns were supplied.", nameof(destinationColumns));
        var quotedTable = QuoteTableName(destinationTable);
        var quotedColumns = columns.Select(QuoteColumnName).ToArray();
        if (quotedColumns.Distinct(StringComparer.Ordinal).Count() != quotedColumns.Length)
            throw new ArgumentException("Destination column names must be unique.", nameof(destinationColumns));

        var existingTransaction = connection.CurrentTransaction;
        if (transaction is null && existingTransaction is not null)
            throw new InvalidOperationException("Pass the active connection transaction to the importer explicitly.");
        if (transaction is not null && !ReferenceEquals(transaction, existingTransaction))
            throw new InvalidOperationException("The supplied transaction is not the active transaction for this connection.");
        var ownsTransaction = transaction is null;
        if (!ownsTransaction && options.CommitEveryRows != 0)
            throw new ArgumentException("CommitEveryRows is only valid when the importer owns the transaction.", nameof(options));

        var requestedBatchSize = Math.Min(options.BatchSize, MaximumMarkersPerStatement / fieldCount);
        if (requestedBatchSize < 1)
            throw new ArgumentException($"The source has {fieldCount} columns, exceeding Db2's supported marker count for one INSERT.", nameof(source));
        var safeBatchSize = requestedBatchSize;
        var batchCapacity = LimitBatchBySqlLength(quotedTable, quotedColumns, safeBatchSize);
        if (batchCapacity < 1)
            throw new ArgumentException("A single-row INSERT exceeds the Db2 statement size supported by this importer.", nameof(destinationTable));

        Db2AdoTransaction? ownedTransaction = ownsTransaction
            ? (Db2AdoTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var activeTransaction = (DbTransaction?)ownedTransaction ?? transaction;
        var rowsInserted = 0L;
        var rowsCommitted = 0L;
        var rowsInTransaction = 0L;
        var rowsRead = 0L;
        var lastNotifiedRows = 0L;
        var batch = new List<object?[]>(batchCapacity);
        var reusableRows = new Stack<object?[]>();
        var activeSpools = new List<Db2LobSpool>();
        var statementCache = new Db2InsertStatementCache(quotedTable, quotedColumns);
        var batchEstimatedBytes = 0L;
        var batchStartRow = 0L;
        var executingBatch = false;
        var readingSourceRow = false;
        var activeSourceRow = 0L;

        async Task DisposeActiveSpoolsAsync(int? preserveSuffixFrom = null, bool suppressCleanupErrors = false)
        {
            Exception? cleanupError = null;
            var disposeCount = preserveSuffixFrom is null
                ? activeSpools.Count
                : Math.Clamp(preserveSuffixFrom.Value, 0, activeSpools.Count);
            var toDispose = activeSpools.GetRange(0, disposeCount);
            activeSpools.RemoveRange(0, disposeCount);
            foreach (var spool in toDispose)
            {
                try { await spool.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { cleanupError ??= error; }
            }
            if (!suppressCleanupErrors && cleanupError is not null) throw cleanupError;
        }

        try
        {
            if (options.TableLock)
            {
                await using var lockCommand = new Db2AdoCommand
                {
                    Connection = connection,
                    Transaction = activeTransaction as Db2AdoTransaction,
                    CommandTimeout = options.CommandTimeout,
                };
                lockCommand.CommandText = $"LOCK TABLE {quotedTable} IN EXCLUSIVE MODE";
                await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var command = new Db2AdoCommand
            {
                Connection = connection,
                Transaction = activeTransaction as Db2AdoTransaction,
                CommandTimeout = options.CommandTimeout,
            };
            await SetCommandShapeAsync(command, quotedTable, quotedColumns, batchCapacity, cancellationToken, statementCache).ConfigureAwait(false);

            int CurrentBatchCapacity()
            {
                var capacity = options.CommitEveryRows > 0
                    ? (int)Math.Min(batchCapacity, options.CommitEveryRows - rowsInTransaction)
                    : batchCapacity;
                var rowsUntilNotification = options.RowsUntilProgressBoundary?.Invoke(rowsInserted)
                    ?? options.NotifyAfterRows - rowsInserted % options.NotifyAfterRows;
                return (int)Math.Min(capacity, Math.Max(1, rowsUntilNotification));
            }

            async Task FlushBatchAsync(int? preserveSuffixFrom = null)
            {
                if (batch.Count == 0) return;
                if (ownsTransaction && activeTransaction is null)
                {
                    ownedTransaction = (Db2AdoTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                    activeTransaction = ownedTransaction;
                    command.Transaction = ownedTransaction;
                }

                executingBatch = true;
                await ExecuteBatchAsync(command, quotedTable, quotedColumns, batch, cancellationToken, statementCache).ConfigureAwait(false);
                executingBatch = false;
                rowsInserted += batch.Count;
                rowsInTransaction += batch.Count;
                foreach (var row in batch) { Array.Clear(row); reusableRows.Push(row); }
                batch.Clear();
                batchEstimatedBytes = 0;
                await DisposeActiveSpoolsAsync(preserveSuffixFrom).ConfigureAwait(false);

                if (ownsTransaction && options.CommitEveryRows > 0 && rowsInTransaction == options.CommitEveryRows)
                {
                    await ownedTransaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
                    await ownedTransaction.DisposeAsync().ConfigureAwait(false);
                    ownedTransaction = null;
                    activeTransaction = null;
                    rowsCommitted += rowsInTransaction;
                    rowsInTransaction = 0;
                }

                if (rowsInserted / options.NotifyAfterRows > lastNotifiedRows / options.NotifyAfterRows)
                {
                    lastNotifiedRows = rowsInserted;
                    progress?.Invoke(new Db2AdoImportProgress(rowsRead, rowsInserted, rowsCommitted));
                }
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                readingSourceRow = true;
                activeSourceRow = rowsRead + 1;
                var hasRow = await ReadNextAsync(source, cancellationToken).ConfigureAwait(false);
                if (!hasRow)
                {
                    readingSourceRow = false;
                    activeSourceRow = 0;
                    break;
                }
                rowsRead++;
                activeSourceRow = rowsRead;

                var values = reusableRows.TryPop(out var reusedRow) ? reusedRow : new object?[fieldCount];
                var rowSpoolsStart = activeSpools.Count;
                var hasReaderOwnedLob = false;
                for (var ordinal = 0; ordinal < fieldCount; ordinal++)
                {
                    if (source is Db2AdoDataReader db2Reader
                        && db2Reader.TryGetProgressiveLobImportValue(ordinal, connection, out var lob, out var lobType))
                    {
                        values[ordinal] = await SpoolProgressiveLobAsync(lob!, lobType, activeSpools, cancellationToken).ConfigureAwait(false);
                        hasReaderOwnedLob = true;
                    }
                    else
                        values[ordinal] = source.GetValue(ordinal);
                    ValidateStreamValue(values[ordinal], rowsRead, ordinal);
                }
                readingSourceRow = false;
                activeSourceRow = 0;

                var rowEstimatedBytes = EstimateEncodedBytes(values);
                var capacity = CurrentBatchCapacity();
                if (batch.Count > 0 && (batch.Count >= capacity
                    || batchEstimatedBytes >= options.MaxBatchBytes
                    || rowEstimatedBytes > options.MaxBatchBytes - batchEstimatedBytes))
                    // Keep only spools staged for this row. Older batch spools can be
                    // released immediately after their INSERT has completed.
                    await FlushBatchAsync(hasReaderOwnedLob ? rowSpoolsStart : 0).ConfigureAwait(false);

                if (batch.Count == 0) batchStartRow = rowsRead;
                batch.Add(values);
                batchEstimatedBytes += rowEstimatedBytes;

                // A progressive locator belongs to the source reader's current row. Consume it
                // in this insert before ReadAsync advances that cursor and invalidates the value.
                if (hasReaderOwnedLob || batch.Count >= CurrentBatchCapacity())
                    await FlushBatchAsync().ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await FlushBatchAsync().ConfigureAwait(false);

            if (rowsInserted > lastNotifiedRows)
            {
                lastNotifiedRows = rowsInserted;
                progress?.Invoke(new Db2AdoImportProgress(rowsRead, rowsInserted, rowsCommitted));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (ownsTransaction && ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
                ownedTransaction = null;
                activeTransaction = null;
                rowsCommitted += rowsInTransaction;
            }
            return new Db2AdoImportResult(rowsRead, rowsInserted, rowsCommitted);
        }
        catch (OperationCanceledException)
        {
            if (ownsTransaction && options.PreserveSuccessfulRowsOnCancellation && ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
                ownedTransaction = null;
                activeTransaction = null;
                rowsCommitted += rowsInTransaction;
                rowsInTransaction = 0;
            }
            else
                await RollbackQuietlyAsync(ownedTransaction).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            long? exactRow = null;
            var firstRow = executingBatch ? batchStartRow : readingSourceRow ? activeSourceRow : 0;
            var lastRow = executingBatch ? batchStartRow + batch.Count - 1 : firstRow;
            if (executingBatch && batch.Count == 1) exactRow = batchStartRow;
            else if (readingSourceRow) exactRow = activeSourceRow;
            else if (options.TableLock && ownsTransaction && executingBatch
                && IsDiagnosableRowFailure(error) && !ContainsOneShotStream(batch))
            {
                await using var replayCommand = new Db2AdoCommand
                {
                    Connection = connection,
                    Transaction = activeTransaction as Db2AdoTransaction,
                    CommandTimeout = options.CommandTimeout,
                };
                var replay = await ReplayBatchUntilFailureAsync(
                    replayCommand, quotedTable, quotedColumns, batch, CancellationToken.None).ConfigureAwait(false);
                if (replay.Error is not null)
                {
                    rowsInserted += replay.SuccessfulRows;
                    rowsInTransaction += replay.SuccessfulRows;
                    exactRow = batchStartRow + replay.SuccessfulRows;
                    firstRow = lastRow = exactRow.Value;

                    if (ownedTransaction is not null)
                    {
                        await ownedTransaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                        await ownedTransaction.DisposeAsync().ConfigureAwait(false);
                        ownedTransaction = null;
                        activeTransaction = null;
                        rowsCommitted += rowsInTransaction;
                        rowsInTransaction = 0;
                    }

                    throw new Db2AdoImportException(
                        $"Db2 import failed at row {exactRow.Value.ToString(CultureInfo.InvariantCulture)}; {rowsCommitted.ToString(CultureInfo.InvariantCulture)} rows were already committed.",
                        replay.Error,
                        firstRow,
                        lastRow,
                        exactRow,
                        rowsCommitted);
                }
            }
            else if (executingBatch && ownsTransaction && IsDiagnosableRowFailure(error) && !ContainsOneShotStream(batch))
            {
                try
                {
                    var failedTransaction = ownedTransaction;
                    ownedTransaction = null;
                    activeTransaction = null;
                    if (failedTransaction is not null)
                        await failedTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    exactRow = await FindFailingRowAsync(
                        connection, quotedTable, quotedColumns, batch, batchStartRow, options.CommandTimeout).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the original server error if rollback or the diagnostic retry fails.
                }
            }

            var diagnostic = exactRow is { } row
                ? $"row {row.ToString(CultureInfo.InvariantCulture)}"
                : executingBatch
                    ? $"rows {firstRow.ToString(CultureInfo.InvariantCulture)}–{lastRow.ToString(CultureInfo.InvariantCulture)}"
                    : firstRow == 0 ? "import operation" : $"row {firstRow.ToString(CultureInfo.InvariantCulture)}";
            throw new Db2AdoImportException(
                $"Db2 import failed at {diagnostic}; {rowsCommitted.ToString(CultureInfo.InvariantCulture)} rows were already committed.",
                error, firstRow, lastRow, exactRow, rowsCommitted);
        }
        finally
        {
            await RollbackQuietlyAsync(ownedTransaction).ConfigureAwait(false);
            await DisposeActiveSpoolsAsync(suppressCleanupErrors: true).ConfigureAwait(false);
        }
    }

    internal static string QuoteTableName(string tableName)
    {
        var parts = new List<string>(2);
        var index = 0;
        while (index < tableName.Length)
        {
            while (index < tableName.Length && char.IsWhiteSpace(tableName[index])) index++;
            if (index == tableName.Length) break;
            string part;
            if (tableName[index] == '"')
            {
                index++;
                var identifier = new StringBuilder();
                var closed = false;
                while (index < tableName.Length)
                {
                    var current = tableName[index++];
                    if (current != '"') { identifier.Append(current); continue; }
                    if (index < tableName.Length && tableName[index] == '"')
                    {
                        identifier.Append('"');
                        index++;
                        continue;
                    }
                    closed = true;
                    break;
                }
                if (!closed || identifier.Length == 0) throw new ArgumentException("The destination table contains an invalid quoted identifier.", nameof(tableName));
                part = identifier.ToString();
            }
            else
            {
                var start = index;
                while (index < tableName.Length && tableName[index] != '.') index++;
                part = tableName[start..index].Trim();
                if (!IsSimpleIdentifier(part)) throw new ArgumentException("Use a table name or schema.table identifier; SQL fragments are not accepted.", nameof(tableName));
                part = part.ToUpperInvariant();
            }

            parts.Add(QuoteColumnName(part));
            while (index < tableName.Length && char.IsWhiteSpace(tableName[index])) index++;
            if (index < tableName.Length)
            {
                if (tableName[index] != '.') throw new ArgumentException("The destination table identifier has unexpected characters.", nameof(tableName));
                index++;
                while (index < tableName.Length && char.IsWhiteSpace(tableName[index])) index++;
                if (index == tableName.Length) throw new ArgumentException("The destination table identifier ends after a qualifier separator.", nameof(tableName));
            }
        }
        if (parts.Count is < 1 or > 2) throw new ArgumentException("The destination table must be table or schema.table.", nameof(tableName));
        return string.Join('.', parts);
    }

    internal static string BuildInsertStatement(string table, IReadOnlyList<string> columns, int rows)
    {
        if (rows < 1) throw new ArgumentOutOfRangeException(nameof(rows));
        var rowValues = $"({string.Join(',', Enumerable.Repeat("?", columns.Count))})";
        var sql = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES {string.Join(",", Enumerable.Repeat(rowValues, rows))}";
        if ((long)columns.Count * rows > MaximumMarkersPerStatement || sql.Length > MaximumSqlCharacters)
            throw new ArgumentOutOfRangeException(nameof(rows), "The INSERT exceeds the supported Db2 statement or marker limit.");
        return sql;
    }

    private static async Task ExecuteBatchAsync(
        Db2AdoCommand command,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows,
        CancellationToken cancellationToken,
        Db2InsertStatementCache? statementCache = null)
    {
        var sql = statementCache?.Get(rows.Count) ?? BuildInsertStatement(table, columns, rows.Count);
        if (!StringComparer.Ordinal.Equals(command.CommandText, sql))
        {
            command.CommandText = sql;
            command.Parameters.Clear();
            AddParameters(command, checked(columns.Count * rows.Count));
            await command.PrepareAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                command.Parameters[rowIndex * columns.Count + columnIndex].Value = rows[rowIndex][columnIndex] ?? DBNull.Value;

        // Count only rows actually confirmed by SQLERRD(3). SQLCODE >= 0 alone
        // is insufficient: a malformed LOB exchange can leave the INSERT unexecuted.
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != rows.Count)
            throw new InvalidDataException($"Db2 confirmed {affected} inserted rows for an import batch of {rows.Count}; refusing to report unconfirmed rows as inserted.");
    }

    private static async Task SetCommandShapeAsync(
        Db2AdoCommand command,
        string table,
        IReadOnlyList<string> columns,
        int rowCount,
        CancellationToken cancellationToken,
        Db2InsertStatementCache? statementCache = null)
    {
        command.CommandText = statementCache?.Get(rowCount) ?? BuildInsertStatement(table, columns, rowCount);
        AddParameters(command, checked(columns.Count * rowCount));
        await command.PrepareAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameters(Db2AdoCommand command, int count)
    {
        for (var index = 0; index < count; index++)
            command.Parameters.Add(new Db2AdoParameter { ParameterName = $"p{index}", Value = DBNull.Value });
    }

    private static async Task<long?> FindFailingRowAsync(
        Db2AdoConnection connection,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows,
        long firstRow,
        int commandTimeout)
    {
        Db2AdoTransaction? diagnosticTransaction = null;
        try
        {
            diagnosticTransaction = (Db2AdoTransaction)await connection.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
            await using var command = new Db2AdoCommand
            {
                Connection = connection,
                Transaction = diagnosticTransaction,
                CommandTimeout = commandTimeout,
            };
            await SetCommandShapeAsync(command, table, columns, 1, CancellationToken.None).ConfigureAwait(false);
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                    command.Parameters[columnIndex].Value = rows[rowIndex][columnIndex] ?? DBNull.Value;
                try { await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false); }
                catch
                {
                    await diagnosticTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    diagnosticTransaction = null;
                    return firstRow + rowIndex;
                }
            }
            await diagnosticTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            diagnosticTransaction = null;
            return null;
        }
        finally
        {
            await RollbackQuietlyAsync(diagnosticTransaction).ConfigureAwait(false);
        }
    }

    private static async Task<(int SuccessfulRows, Exception? Error)> ReplayBatchUntilFailureAsync(
        Db2AdoCommand command,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows,
        CancellationToken cancellationToken)
    {
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            try
            {
                await ExecuteBatchAsync(command, table, columns, [rows[rowIndex]], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return (rowIndex, error);
            }
        }
        return (rows.Count, null);
    }

    private static void ValidateStreamValue(object? value, long row, int ordinal)
    {
        if (value is TextReader)
            throw new NotSupportedException($"TextReader values from import sources are not supported at row {row}, column {ordinal + 1}; provide a length-aware Db2ClobStream, Db2DbClobStream, or Db2XmlStream.");
        if (value is Stream stream && !stream.CanSeek)
            throw new NotSupportedException($"A non-seekable Stream at row {row}, column {ordinal + 1} must be wrapped in a length-aware Db2BlobStream, Db2ClobStream, Db2DbClobStream, or Db2XmlStream.");
    }

    private static async Task<object> SpoolProgressiveLobAsync(
        Db2LobValue lob,
        SqlType type,
        List<Db2LobSpool> ownedSpools,
        CancellationToken cancellationToken)
    {
        if (lob.ByteLength is { } sourceLength && sourceLength > Db2LobSpool.MaxValueBytes)
            throw new Db2ResourceLimitException("DB2 LOB exceeds streamed-value limit.");
        var spool = new Db2LobSpool();
        ownedSpools.Add(spool);
        await using var source = lob.OpenReadStream();
        if (type == SqlType.DbClob)
            await CopyDbClobAsUtf8Async(source, spool, cancellationToken).ConfigureAwait(false);
        else
            await CopyLobAsync(source, spool, cancellationToken).ConfigureAwait(false);

        await spool.File.FlushAsync(cancellationToken).ConfigureAwait(false);
        spool.File.Position = 0;
        var length = spool.File.Length;
        return type switch
        {
            SqlType.Blob => new Db2BlobStream(spool.File, length),
            SqlType.Clob => new Db2ClobStream(spool.File, length),
            SqlType.DbClob => new Db2DbClobStream(spool.File, length),
            SqlType.XML => new Db2XmlStream(spool.File, length),
            _ => throw new InvalidDataException($"Unsupported progressive Db2 LOB type {type}."),
        };
    }

    private static async Task CopyLobAsync(Stream source, Db2LobSpool spool, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            await spool.AppendAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyDbClobAsUtf8Async(Stream source, Db2LobSpool spool, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(source, Encoding.BigEndianUnicode, detectEncodingFromByteOrderMarks: false,
            bufferSize: 64 * 1024, leaveOpen: true);
        var encoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetEncoder();
        var characters = new char[16 * 1024];
        var bytes = new byte[64 * 1024];

        while (true)
        {
            var read = await reader.ReadAsync(characters, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var offset = 0;
            while (offset < read)
            {
                encoder.Convert(characters, offset, read - offset, bytes, 0, bytes.Length, flush: false,
                    out var charsUsed, out var bytesUsed, out _);
                if (charsUsed == 0 && bytesUsed == 0)
                    throw new InvalidDataException("The DBCLOB UTF-8 encoder made no progress.");
                if (bytesUsed > 0) await spool.AppendAsync(bytes.AsMemory(0, bytesUsed), cancellationToken).ConfigureAwait(false);
                offset += charsUsed;
            }
        }

        encoder.Convert(Array.Empty<char>(), 0, 0, bytes, 0, bytes.Length, flush: true,
            out _, out var finalBytes, out var completed);
        if (finalBytes > 0) await spool.AppendAsync(bytes.AsMemory(0, finalBytes), cancellationToken).ConfigureAwait(false);
        if (!completed) throw new InvalidDataException("The DBCLOB UTF-8 encoder did not finish its final character.");
    }

    private static bool ContainsOneShotStream(IEnumerable<object?[]> rows) => rows
        .SelectMany(row => row)
        .Any(value => value is Stream or TextReader or Db2BlobStream or Db2ClobStream or Db2DbClobStream or Db2XmlStream);

    private static bool IsDiagnosableRowFailure(Exception error) =>
        error is Db2AdoException or JustyBase.Db2.IDb2Error or ArgumentException or InvalidCastException or FormatException or OverflowException;

    private static long EstimateEncodedBytes(IEnumerable<object?> values)
    {
        long total = 0;
        foreach (var value in values)
        {
            var size = value switch
            {
                byte[] bytes => bytes.LongLength,
                ReadOnlyMemory<byte> bytes => bytes.Length,
                Memory<byte> bytes => bytes.Length,
                string text => Encoding.UTF8.GetByteCount(text),
                char[] chars => (long)chars.Length * sizeof(char),
                Stream or TextReader or Db2BlobStream or Db2ClobStream or Db2DbClobStream or Db2XmlStream => 0,
                null or DBNull => 1,
                _ => 32,
            };
            if (long.MaxValue - total < size) return long.MaxValue;
            total += size;
        }
        return total;
    }

    private static async ValueTask<bool> ReadNextAsync(IDataReader source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is DbDataReader dbDataReader)
            return await dbDataReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return source.Read();
    }

    private static async Task RollbackQuietlyAsync(Db2AdoTransaction? transaction)
    {
        if (transaction is null) return;
        try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* Keep the import/cancellation failure as the primary diagnostic. */ }
        finally { await transaction.DisposeAsync().ConfigureAwait(false); }
    }

    private static int LimitBatchBySqlLength(string table, IReadOnlyList<string> columns, int requested)
    {
        var low = 0;
        var high = requested;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            try { _ = BuildInsertStatement(table, columns, middle); low = middle; }
            catch (ArgumentOutOfRangeException) { high = middle - 1; }
        }
        return low;
    }

    private static string QuoteColumnName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0'))
            throw new ArgumentException("Destination column names must be non-empty identifiers without NUL characters.", nameof(name));
        return $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static bool IsSimpleIdentifier(string value)
    {
        if (value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_')) return false;
        for (var index = 1; index < value.Length; index++)
            if (!(char.IsLetterOrDigit(value[index]) || value[index] is '_' or '@' or '$' or '#')) return false;
        return true;
    }
}
