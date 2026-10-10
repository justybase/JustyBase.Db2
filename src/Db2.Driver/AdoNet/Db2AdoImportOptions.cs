namespace JustyBase.Db2.AdoNet;

/// <summary>Controls batching, progress, and commit boundaries for a bounded-memory import.</summary>
public sealed record Db2AdoImportOptions
{
    /// <summary>Maximum number of source rows in one multi-row INSERT. Defaults to 128.</summary>
    public int BatchSize { get; init; } = 128;

    /// <summary>Approximate maximum encoded value bytes retained in a batch. Defaults to 8 MiB.</summary>
    public long MaxBatchBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Report progress after each multiple of this row count. Defaults to 10,000.</summary>
    public int NotifyAfterRows { get; init; } = 10_000;

    /// <summary>
    /// Commit each interval when the importer owns the transaction. Zero (the default)
    /// makes the whole import atomic. Positive values intentionally allow earlier intervals
    /// to remain committed if a later interval fails.
    /// </summary>
    public int CommitEveryRows { get; init; }

    /// <summary>Acquire an exclusive DB2 table lock for the import transaction.</summary>
    public bool TableLock { get; init; }

    /// <summary>Keep completed rows when cancellation ends an IBM-compatible locked bulk copy.</summary>
    internal bool PreserveSuccessfulRowsOnCancellation { get; init; }

    internal Func<long, int>? RowsUntilProgressBoundary { get; init; }

    /// <summary>Timeout in seconds for each INSERT. Zero disables the timeout.</summary>
    public int CommandTimeout { get; init; } = 3_600;

    internal void Validate()
    {
        if (BatchSize < 1) throw new ArgumentOutOfRangeException(nameof(BatchSize));
        if (MaxBatchBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxBatchBytes));
        if (NotifyAfterRows < 1) throw new ArgumentOutOfRangeException(nameof(NotifyAfterRows));
        if (CommitEveryRows < 0) throw new ArgumentOutOfRangeException(nameof(CommitEveryRows));
        if (TableLock && CommitEveryRows > 0)
            throw new ArgumentException("TableLock requires one transaction for the duration of the import; CommitEveryRows must be zero.");
        if (CommandTimeout < 0) throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
    }
}

/// <summary>Progress snapshot reported by <see cref="Db2AdoImporter"/>.</summary>
public sealed record Db2AdoImportProgress(long RowsRead, long RowsInserted, long RowsCommitted);

/// <summary>Result for one bounded-memory import.</summary>
public sealed record Db2AdoImportResult(long RowsRead, long RowsInserted, long RowsCommitted);

/// <summary>An import failure with source row diagnostics and any earlier commit count.</summary>
public sealed class Db2AdoImportException : System.Data.Common.DbException, IDb2Error
{
    internal Db2AdoImportException(
        string message,
        Exception innerException,
        long firstRowNumber,
        long lastRowNumber,
        long? rowNumber,
        long rowsCommitted)
        : base(message, innerException)
    {
        FirstRowNumber = firstRowNumber;
        LastRowNumber = lastRowNumber;
        RowNumber = rowNumber;
        RowsCommitted = rowsCommitted;
        if (innerException is IDb2Error db2Error)
        {
            ErrorKind = db2Error.ErrorKind;
            SqlCode = db2Error.SqlCode;
            SqlState = db2Error.SqlState;
        }
        else if (innerException is ArgumentException or InvalidCastException or FormatException or OverflowException or NotSupportedException)
        {
            ErrorKind = Db2ErrorKind.ParameterConversion;
        }
        else ErrorKind = Db2ErrorKind.ServerError;
    }

    /// <summary>First source row in the failed batch; zero when failure is outside row processing.</summary>
    public long FirstRowNumber { get; }
    /// <summary>Last source row in the failed batch; zero when failure is outside row processing.</summary>
    public long LastRowNumber { get; }
    /// <summary>Exact failing source row where it was isolated; null means the batch range is known only.</summary>
    public long? RowNumber { get; }
    public long RowsCommitted { get; }
    public Db2ErrorKind ErrorKind { get; }
    public int? SqlCode { get; }
    public override string? SqlState { get; }
}
