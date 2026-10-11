using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;

namespace IBM.Data.Db2;

/// <summary>
/// IBM-style DB2 connection type for consumers migrating from IBM.Data.Db2.
/// The provider-specific namespace is intentionally distinct so both providers can
/// be referenced in the same process for value and behavior comparisons.
/// </summary>
public sealed class DB2Connection : Db2AdoConnection
{
    private int _disposingAsynchronously;

    public DB2Connection() { }
    public DB2Connection(string connectionString) => ConnectionString = connectionString;

    [AllowNull]
    public override string ConnectionString
    {
        get => base.ConnectionString;
        set => base.ConnectionString = AddIbmDefaultConnectTimeout(value ?? "");
    }

    public override int ConnectionTimeout => string.IsNullOrWhiteSpace(ConnectionString) ? 0 : base.ConnectionTimeout;

    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing)
                base.CloseAsync(cancelActiveOperations: false).GetAwaiter().GetResult();
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing) base.ConnectionString = "";
        }
    }

    public override async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposingAsynchronously, 1);
        try { await base.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            Interlocked.Exchange(ref _disposingAsynchronously, 0);
            base.ConnectionString = "";
        }
    }

    public new event DB2InfoMessageEventHandler? InfoMessage;

    protected override void OnInfoMessage(Db2AdoInfoMessageEventArgs args)
    {
        base.OnInfoMessage(args);
        InfoMessage?.Invoke(this, new DB2InfoMessageEventArgs(new DB2ErrorCollection(args.Errors.Select(DB2Error.FromWarning))));
    }

    protected internal override Exception TranslateProviderError(Exception error) => DB2Exception.From(error);

    protected internal override Exception TranslateOperationCancellation(Exception error) =>
        Volatile.Read(ref _disposingAsynchronously) != 0
            ? DB2Exception.FromConnectionDisposeCancellation(error)
            : error;

    private static string AddIbmDefaultConnectTimeout(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return connectionString;
        if (connectionString.StartsWith("db2://", StringComparison.OrdinalIgnoreCase)
            || connectionString.StartsWith("db2s://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new UriBuilder(connectionString);
            var query = uri.Query.TrimStart('?');
            if (query.Split('&', StringSplitOptions.RemoveEmptyEntries).Any(pair =>
                Uri.UnescapeDataString(pair.Split('=', 2)[0]).Equals("connect_timeout", StringComparison.OrdinalIgnoreCase)
                || Uri.UnescapeDataString(pair.Split('=', 2)[0]).Equals("timeout", StringComparison.OrdinalIgnoreCase)))
                return connectionString;
            uri.Query = query.Length == 0 ? "connect_timeout=0" : $"{query}&connect_timeout=0";
            return uri.Uri.AbsoluteUri;
        }

        var values = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (!values.Keys.Cast<string>().Any(key => key.Equals("connect_timeout", StringComparison.OrdinalIgnoreCase)
            || key.Equals("timeout", StringComparison.OrdinalIgnoreCase)))
            values["Connect_Timeout"] = 0;
        return values.ConnectionString;
    }
}

/// <summary>IBM-style connection string builder for <see cref="DB2Connection"/>.</summary>
public sealed class DB2ConnectionStringBuilder : Db2AdoConnectionStringBuilder
{
    public DB2ConnectionStringBuilder() => ConnectTimeout = 0;
    public DB2ConnectionStringBuilder(string connectionString)
    {
        ConnectionString = connectionString;
        if (!Keys.Cast<string>().Any(key => key.Equals("connect_timeout", StringComparison.OrdinalIgnoreCase)
            || key.Equals("timeout", StringComparison.OrdinalIgnoreCase)))
            ConnectTimeout = 0;
    }

    /// <summary>IBM-compatible spelling of <see cref="Db2AdoConnectionStringBuilder.ConnectTimeout"/> (the <c>Connect_Timeout</c> keyword).</summary>
    public int Connect_Timeout { get => ConnectTimeout; set => ConnectTimeout = value; }
}

public delegate void DB2InfoMessageEventHandler(object sender, DB2InfoMessageEventArgs e);

/// <summary>Information and warning messages returned by Db2 after a successful operation.</summary>
public sealed class DB2InfoMessageEventArgs(DB2ErrorCollection errors) : EventArgs
{
    public DB2ErrorCollection Errors { get; } = errors ?? throw new ArgumentNullException(nameof(errors));
}

/// <summary>An individual server warning or bulk-copy row error.</summary>
public sealed class DB2Error
{
    internal DB2Error(string message, long rowNumber, int? sqlCode, string? sqlState)
    {
        Message = message;
        RowNumber = rowNumber > int.MaxValue ? int.MaxValue : (int)rowNumber;
        SourceRowNumber = rowNumber;
        SqlCode = sqlCode;
        SqlState = sqlState;
    }

    public string Message { get; }
    public int RowNumber { get; }
    public long SourceRowNumber { get; }
    public int? SqlCode { get; }
    public string? SqlState { get; }
    public int NativeError => SqlCode ?? 0;
    public string SQLState => SqlState ?? "";
    public string Source => "JustyBase.Db2";

    internal static DB2Error FromWarning(Db2Warning warning) =>
        new(warning.Message, 0, warning.SqlCode, warning.SqlState);
}

/// <summary>Read-only set of Db2 warnings or bulk-copy row errors.</summary>
public sealed class DB2ErrorCollection : ReadOnlyCollection<DB2Error>
{
    internal DB2ErrorCollection(IEnumerable<DB2Error> errors) : base(errors.ToList()) { }
    internal static DB2ErrorCollection EmptyCollection { get; } = new(Array.Empty<DB2Error>());
}

/// <summary>Server exception compatible with the IBM provider's exception catch surface.</summary>
public sealed class DB2Exception : DbException, JustyBase.Db2.IDb2Error
{
    private DB2Exception(string message, DB2ErrorCollection errors, Exception? innerException)
        : base(message, innerException)
    {
        Errors = errors;
        var first = errors.FirstOrDefault();
        ErrorKind = innerException is JustyBase.Db2.IDb2Error db2Error ? db2Error.ErrorKind : JustyBase.Db2.Db2ErrorKind.ServerError;
        SqlCode = first?.SqlCode ?? (innerException as JustyBase.Db2.IDb2Error)?.SqlCode;
        SqlState = first?.SqlState ?? (innerException as JustyBase.Db2.IDb2Error)?.SqlState;
    }

    public DB2ErrorCollection Errors { get; }
    public JustyBase.Db2.Db2ErrorKind ErrorKind { get; }
    public int? SqlCode { get; }
    public override string? SqlState { get; }

    internal static DB2Exception From(Exception error)
    {
        if (error is DB2Exception existing) return existing;
        var errors = error is JustyBase.Db2.IDb2Error db2Error
            ? new DB2ErrorCollection([new DB2Error(error.Message, 0, db2Error.SqlCode, db2Error.SqlState)])
            : DB2ErrorCollection.EmptyCollection;
        return new DB2Exception(error.Message, errors, error);
    }

    internal static DB2Exception FromBulkCopy(DB2ErrorCollection errors, Exception innerException) =>
        new(innerException.Message, errors, innerException);

    internal static DB2Exception FromConnectionDisposeCancellation(Exception innerException) =>
        new(
            "The active Db2 command was interrupted while its connection was asynchronously disposed.",
            new DB2ErrorCollection([new DB2Error("The active Db2 command was interrupted.", 0, -952, "57014")]),
            innerException);
}

public delegate void DB2RowsCopiedEventHandler(object sender, DB2RowsCopiedEventArgs e);

/// <summary>Progress information for a <see cref="DB2BulkCopy.DB2RowsCopied"/> notification.</summary>
public sealed class DB2RowsCopiedEventArgs(long rowsCopied) : EventArgs
{
    public bool Abort { get; set; }
    public long RowsCopied { get; } = rowsCopied;
}

[Flags]
public enum DB2BulkCopyOptions
{
    Default = 0,
    KeepIdentity = 1,
    TableLock = 2,
    Truncate = 4,
}

/// <summary>
/// IBM-shaped bulk-copy API backed by bounded, parameterized multi-row INSERTs.
/// By default rows are inserted in bounded batches and each successful batch is committed.
/// A failure rolls back the batch in progress while preserving earlier committed batches. With
/// <see cref="DB2BulkCopyOptions.TableLock"/>, an exclusive SQL table lock is held for the copy
/// transaction; successful rows are committed when a row error or progress abort ends the operation.
/// </summary>
public sealed class DB2BulkCopy : IDisposable
{
    /// <summary>
    /// Default number of source rows per multi-row INSERT batch. The importer reduces this as
    /// necessary to respect Db2's parameter, SQL-text, and bounded-memory limits.
    /// </summary>
    public const int DefaultBatchSize = 1024;

    private readonly Db2AdoConnection _connection;
    private readonly DB2BulkCopyOptions _options;
    private DB2ErrorCollection _errors = DB2ErrorCollection.EmptyCollection;
    private string _destinationTableName = "";
    private int _bulkCopyTimeout = 3_600;
    private int _notifyAfter;
    private int _batchSize = DefaultBatchSize;
    private long _rowsCopied;
    private bool _isWriting;
    private bool _isClosed;

    public DB2BulkCopy(DB2Connection connection, DB2BulkCopyOptions copyOptions = DB2BulkCopyOptions.Default)
        : this((Db2AdoConnection)connection, copyOptions) { }

    public DB2BulkCopy(Db2AdoConnection connection, DB2BulkCopyOptions copyOptions = DB2BulkCopyOptions.Default)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        const DB2BulkCopyOptions knownOptions = DB2BulkCopyOptions.KeepIdentity | DB2BulkCopyOptions.TableLock | DB2BulkCopyOptions.Truncate;
        if ((copyOptions & ~knownOptions) != 0) throw new ArgumentOutOfRangeException(nameof(copyOptions));
        var unsupportedOptions = copyOptions & (DB2BulkCopyOptions.KeepIdentity | DB2BulkCopyOptions.Truncate);
        if (unsupportedOptions != 0)
            throw new NotSupportedException($"DB2BulkCopy option(s) {unsupportedOptions} are not implemented by the native provider.");
        _options = copyOptions;
    }

    public string DestinationTableName
    {
        get => _destinationTableName;
        set => _destinationTableName = value ?? throw new ArgumentNullException(nameof(value));
    }

    public int BulkCopyTimeout
    {
        get => _bulkCopyTimeout;
        set => _bulkCopyTimeout = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>Number of copied rows between progress notifications; zero disables notifications.</summary>
    public int NotifyAfter
    {
        get => _notifyAfter;
        set => _notifyAfter = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>
    /// Maximum number of rows per multi-row INSERT. For the default (unlocked) mode,
    /// successful batches are committed; TableLock mode commits the whole import or
    /// preserves completed rows on a row error/abort. Defaults to <see cref="DefaultBatchSize"/>.
    /// </summary>
    public int BatchSize
    {
        get => _batchSize;
        set => _batchSize = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public DB2ErrorCollection Errors => _errors;
    public long RowsCopied => _rowsCopied;
    public event DB2RowsCopiedEventHandler? DB2RowsCopied;

    public void WriteToServer(IDataReader source) => WriteToServerAsync(source, CancellationToken.None).GetAwaiter().GetResult();

    public void WriteToServer(DbDataReader source) => WriteToServer((IDataReader)source);

    public void WriteToServer(DataTable source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var reader = source.CreateDataReader();
        WriteToServer(reader);
    }

    public Task WriteToServerAsync(DbDataReader source, CancellationToken cancellationToken = default) =>
        WriteToServerAsync((IDataReader)source, cancellationToken);

    public async Task WriteToServerAsync(IDataReader source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureOpen();
        if (_isWriting) throw new InvalidOperationException("This DB2BulkCopy instance already has an active write.");
        if (string.IsNullOrWhiteSpace(_destinationTableName))
            throw new InvalidOperationException("DestinationTableName must be set before WriteToServer.");
        if (_connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The DB2 connection must be open before WriteToServer.");

        _isWriting = true;
        _errors = DB2ErrorCollection.EmptyCollection;
        _rowsCopied = 0;
        try
        {
            using var timeout = new CancellationTokenSource();
            if (_bulkCopyTimeout > 0) timeout.CancelAfter(TimeSpan.FromSeconds(_bulkCopyTimeout));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var interval = NotifyAfter;
            var nextNotification = interval > 0 ? interval : long.MaxValue;
            var abortRequested = false;
            try
            {
                var result = await Db2AdoImporter.ImportAsync(
                    _connection,
                    source,
                    _destinationTableName,
                    options: new Db2AdoImportOptions
                    {
                        BatchSize = BatchSize,
                        // Without an explicit row-notification boundary, commit after each
                        // batch. When NotifyAfter is set, align commits to that boundary so
                        // an Abort event never reports rows that are then silently rolled back.
                        CommitEveryRows = (_options & DB2BulkCopyOptions.TableLock) != 0
                            ? 0
                            : NotifyAfter > 0 ? NotifyAfter : BatchSize,
                        CommandTimeout = 0,
                        NotifyAfterRows = NotifyAfter > 0 ? 1 : int.MaxValue,
                        RowsUntilProgressBoundary = rowsInserted =>
                        {
                            var remaining = nextNotification - rowsInserted;
                            return remaining <= 0 ? 1 : remaining >= int.MaxValue ? int.MaxValue : (int)remaining;
                        },
                        TableLock = (_options & DB2BulkCopyOptions.TableLock) != 0,
                        PreserveSuccessfulRowsOnCancellation = (_options & DB2BulkCopyOptions.TableLock) != 0,
                    },
                    progress: progress =>
                    {
                        _rowsCopied = progress.RowsInserted;
                        while (NotifyAfter > 0 && nextNotification <= progress.RowsInserted)
                        {
                            var eventArgs = new DB2RowsCopiedEventArgs(nextNotification);
                            try { DB2RowsCopied?.Invoke(this, eventArgs); }
                            catch
                            {
                                // IBM documents that exceptions from DB2RowsCopied handlers are ignored.
                            }
                            if (eventArgs.Abort)
                            {
                                abortRequested = true;
                                linked.Cancel();
                                return;
                            }
                            interval = NotifyAfter;
                            nextNotification = interval > 0 ? checked(eventArgs.RowsCopied + interval) : long.MaxValue;
                        }
                    },
                    cancellationToken: linked.Token).ConfigureAwait(false);
                _rowsCopied = result.RowsInserted;
            }
            catch (OperationCanceledException error) when (abortRequested)
            {
                throw new SystemException("DB2BulkCopy.WriteToServer was interrupted because a DB2RowsCopied handler set Abort to true.", error);
            }
            catch (OperationCanceledException error) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"DB2 bulk copy exceeded BulkCopyTimeout ({_bulkCopyTimeout} seconds).", error);
            }
            catch (Db2AdoImportException error) when (error.FirstRowNumber > 0)
            {
                var row = error.RowNumber ?? error.FirstRowNumber;
                _errors = new DB2ErrorCollection([new DB2Error(error.Message, row, error.SqlCode, error.SqlState)]);
                _rowsCopied = error.RowsCommitted;
                if (_rowsCopied == 0) throw DB2Exception.FromBulkCopy(_errors, error);
            }
        }
        finally
        {
            _isWriting = false;
        }
    }

    public void Close()
    {
        _isClosed = true;
    }

    public void Dispose() => Close();

    private void EnsureOpen()
    {
        if (_isClosed) throw new ObjectDisposedException(nameof(DB2BulkCopy));
    }
}
