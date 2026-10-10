using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using JustyBase.Db2.Converters;

namespace JustyBase.Db2.AdoNet;

/// <summary>A DbConnection backed by the native DRDA Db2 session.</summary>
public class Db2AdoConnection : DbConnection
{
    private static readonly ConcurrentDictionary<string, Db2Pool> Pools = new(StringComparer.Ordinal);
    // These templates are private and never handed to public callers. Session code
    // reads them without mutation. Bound DSN retention (including credentials).
    private static readonly ConcurrentDictionary<string, ParsedConnection> ParsedConnections = new(StringComparer.Ordinal);
    private sealed record ParsedConnection(Db2Config Config, bool Pooling, int MaxPoolSize, string PoolKey, Db2PoolOptions PoolOptions);
    private string _connectionString = "";
    private ConnectionState _state;
    private Db2Config? _config;
    private Db2Session? _session;
    private Db2SessionLease? _lease;
    private Db2Session? _warningSession;
    private Db2AdoTransaction? _transaction;
    private readonly object _readerGate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly HashSet<Db2AdoDataReader> _readers = [];
    private readonly HashSet<Db2AdoCommand> _activeOperations = [];
    private int _maxPoolSize = 10;
    private bool _pooling = true;

    /// <summary>Provider-specific event raised when Db2 returns a successful SQL warning.</summary>
    public event EventHandler<Db2AdoInfoMessageEventArgs>? InfoMessage;

    public Db2AdoConnection() { }
    public Db2AdoConnection(string connectionString) => ConnectionString = connectionString;

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (_state != ConnectionState.Closed) throw new InvalidOperationException("ConnectionString cannot be changed while the connection is open.");
            _connectionString = value ?? "";
        }
    }

    public override string Database => _config?.Database ?? ParseConfiguredValue()?.Database ?? "";
    public override string DataSource => _config?.Host ?? ParseConfiguredValue()?.Host ?? "";
    public override string ServerVersion => "Db2";
    public override int ConnectionTimeout
    {
        get
        {
            var timeout = _config?.Timeout ?? ParseConfiguredValue()?.Timeout ?? TimeSpan.FromSeconds(30);
            return timeout <= TimeSpan.Zero ? 0 : (int)Math.Min(timeout.TotalSeconds, int.MaxValue);
        }
    }
    public override ConnectionState State => _state == ConnectionState.Open && CurrentSession?.IsClosed == true
        ? ConnectionState.Broken
        : _state;

    internal Db2Session? CurrentSession => _session ?? _lease?.Session;
    internal Db2AdoTransaction? CurrentTransaction => _transaction;

    public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != ConnectionState.Closed) throw new InvalidOperationException("The connection is already open or opening.");
            if (string.IsNullOrWhiteSpace(_connectionString)) throw new Db2AdoException("A Db2 connection string is required.", Db2ErrorKind.Configuration);
            await OpenConfiguredAsync(_connectionString, cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task OpenConfiguredAsync(string connectionString, CancellationToken cancellationToken)
    {
        if (_state != ConnectionState.Closed) throw new InvalidOperationException("The connection is already open or opening.");
        _state = ConnectionState.Connecting;
        try
        {
            var parsed = GetParsedConnection(connectionString);
            _config = parsed.Config;
            _pooling = parsed.Pooling;
            _maxPoolSize = parsed.MaxPoolSize;
            if (_pooling)
            {
                var key = parsed.PoolKey;
                while (true)
                {
                    var pool = Pools.GetOrAdd(key, _ => new Db2Pool(_config, parsed.PoolOptions));
                    try
                    {
                        _lease = await pool.AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (ObjectDisposedException) when (pool.IsClosed)
                    {
                        Pools.TryRemove(new KeyValuePair<string, Db2Pool>(key, pool));
                    }
                }
            }
            else _session = await Db2Session.ConnectAsync(_config, cancellationToken).ConfigureAwait(false);
            AttachWarningHandler();
            _state = ConnectionState.Open;
        }
        catch (OperationCanceledException)
        {
            _config = null;
            _state = ConnectionState.Closed;
            throw;
        }
        catch (Exception error)
        {
            _config = null;
            _state = ConnectionState.Closed;
            throw TranslateProviderError(error);
        }
    }

    public override void Close() => CloseAsync().GetAwaiter().GetResult();

    public override Task CloseAsync() => CloseAsync(cancelActiveOperations: true);

    /// <summary>
    /// Closes the connection. When <paramref name="cancelActiveOperations"/> is false, in-flight commands are
    /// allowed to finish instead of being cancelled. Intended for provider-specific dispose semantics.
    /// </summary>
    protected async Task CloseAsync(bool cancelActiveOperations)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Db2AdoCommand[] operations;
            Db2AdoDataReader[] readers;
            lock (_readerGate)
            {
                if (_state == ConnectionState.Closed) return;
                _state = ConnectionState.Closed;
                readers = [.. _readers];
                operations = [.. _activeOperations];
            }
            Exception? failure = null;
            foreach (var reader in readers)
            {
                try { await reader.CloseFromConnectionAsync().ConfigureAwait(false); }
                catch (Exception error) { failure ??= error; }
            }

            if (cancelActiveOperations)
                foreach (var operation in operations) operation.CancelFromConnectionClose();
            foreach (var operation in operations)
            {
                try { await operation.WaitForOperationCompletionAsync().ConfigureAwait(false); }
                catch (Exception error) { failure ??= error; }
            }

            var transaction = _transaction;
            if (transaction is not null)
            {
                try { await transaction.RollbackFromConnectionCloseAsync().ConfigureAwait(false); }
                catch (Exception error) { failure = error; }
            }

            var lease = _lease;
            var session = _session;
            var warningSession = _warningSession;
            _warningSession = null;
            if (warningSession is not null) warningSession.Warning -= ForwardWarning;
            _lease = null;
            _session = null;
            _config = null;
            _transaction = null;
            _state = ConnectionState.Closed;
            try
            {
                if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
                else if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) { failure ??= error; }

            if (failure is not null) throw TranslateProviderError(failure);
        }
        finally { _lifecycleGate.Release(); }
    }

    public override void ChangeDatabase(string databaseName) =>
        ChangeDatabaseAsync(databaseName, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task ChangeDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        var requestedDatabase = ValidateDatabaseName(databaseName);
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {

        Db2SessionLease? lease;
        Db2Session? session;
        Db2Session? warningSession;
        string replacementConnectionString;
        lock (_readerGate)
        {
            EnsureOpen();
            if (_activeOperations.Count > 0)
                throw new InvalidOperationException("The database cannot be changed while a command operation is active.");
            if (_readers.Count > 0)
                throw new InvalidOperationException("The database cannot be changed while a data reader is active.");
            if (_transaction is not null)
                throw new InvalidOperationException("The database cannot be changed while a transaction is active.");

            var currentDatabase = _config?.Database ?? ParseConfiguredValue()?.Database ?? "";
            if (StringComparer.Ordinal.Equals(currentDatabase, requestedDatabase)) return;

            replacementConnectionString = WithDatabase(_connectionString, requestedDatabase);
            lease = _lease;
            session = _session;
            warningSession = _warningSession;

            _state = ConnectionState.Connecting;
            _lease = null;
            _session = null;
            _config = null;
            _warningSession = null;
            if (warningSession is not null) warningSession.Warning -= ForwardWarning;
        }

        try
        {
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
            else if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _state = ConnectionState.Closed;
            throw TranslateProviderError(error);
        }

        _state = ConnectionState.Closed;
        try
        {
            await OpenConfiguredAsync(replacementConnectionString, cancellationToken).ConfigureAwait(false);
            _connectionString = replacementConnectionString;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw TranslateProviderError(error); }
        }
        finally { _lifecycleGate.Release(); }
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        BeginDbTransactionAsync(isolationLevel, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        EnsureOpen();
        var nativeIsolation = isolationLevel switch
        {
            IsolationLevel.Unspecified => Db2IsolationLevel.Unspecified,
            IsolationLevel.ReadUncommitted => Db2IsolationLevel.UncommittedRead,
            IsolationLevel.ReadCommitted => Db2IsolationLevel.CursorStability,
            IsolationLevel.RepeatableRead => Db2IsolationLevel.ReadStability,
            IsolationLevel.Serializable => Db2IsolationLevel.RepeatableRead,
            _ => throw new NotSupportedException($"Db2 LUW does not support isolation level {isolationLevel}."),
        };
        if (_transaction is not null) throw new InvalidOperationException("A transaction is already active on this connection.");
        try
        {
            var transaction = await CurrentSession!.BeginTransactionAsync(nativeIsolation, cancellationToken).ConfigureAwait(false);
            var adoTransaction = new Db2AdoTransaction(this, transaction, isolationLevel);
            _transaction = adoTransaction;
            return adoTransaction;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw TranslateProviderError(error); }
        }
        finally { _lifecycleGate.Release(); }
    }

    protected override DbCommand CreateDbCommand() => new Db2AdoCommand { Connection = this };

    public override DataTable GetSchema() => GetSchema("MetaDataCollections");

    public override DataTable GetSchema(string collectionName) => GetSchema(collectionName, null);

    public override DataTable GetSchema(string collectionName, string?[]? restrictionValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        var table = collectionName.ToUpperInvariant() switch
        {
            "DATATYPES" => Db2AdoSchema.CreateDataTypes(),
            "METADATACOLLECTIONS" => Db2AdoSchema.CreateMetaDataCollections(),
            _ => throw new ArgumentException($"Schema collection '{collectionName}' is not supported by the native Db2 provider.", nameof(collectionName)),
        };
        if (restrictionValues is { Length: > 0 } && restrictionValues[0] is { Length: > 0 } restriction && table.Columns.Count > 0)
        {
            var typeColumn = table.Columns.Contains("PROVIDER_TYPE_NAME") ? "PROVIDER_TYPE_NAME" : null;
            if (typeColumn is not null)
            {
                foreach (DataRow row in table.Rows.Cast<DataRow>().ToArray())
                    if (!Convert.ToString(row[typeColumn], System.Globalization.CultureInfo.InvariantCulture)!.Equals(restriction, StringComparison.OrdinalIgnoreCase)) table.Rows.Remove(row);
            }
        }
        return table;
    }

    internal void RegisterReader(Db2AdoDataReader reader)
    {
        lock (_readerGate) _readers.Add(reader);
    }

    internal void UnregisterReader(Db2AdoDataReader reader)
    {
        lock (_readerGate) _readers.Remove(reader);
    }

    internal void RegisterOperation(Db2AdoCommand command)
    {
        lock (_readerGate)
        {
            EnsureOpen();
            _activeOperations.Add(command);
        }
    }

    internal void UnregisterOperation(Db2AdoCommand command)
    {
        lock (_readerGate) _activeOperations.Remove(command);
    }

    internal void CompleteTransaction(Db2AdoTransaction transaction)
    {
        if (ReferenceEquals(_transaction, transaction)) _transaction = null;
    }

    private void AttachWarningHandler()
    {
        var session = CurrentSession;
        if (session is null) return;
        session.Warning += ForwardWarning;
        _warningSession = session;
    }

    private void ForwardWarning(object? sender, Db2WarningEventArgs args) =>
        OnInfoMessage(new Db2AdoInfoMessageEventArgs([args.Warning]));

    protected virtual void OnInfoMessage(Db2AdoInfoMessageEventArgs args) => InfoMessage?.Invoke(this, args);

    internal void ValidateCommandTransaction(DbTransaction? transaction)
    {
        if (_transaction is not null && !ReferenceEquals(_transaction, transaction))
            throw new InvalidOperationException("The command must use the active transaction on its connection.");
        if (transaction is not null && !ReferenceEquals(_transaction, transaction))
            throw new InvalidOperationException("The command transaction belongs to a different or completed connection transaction.");
    }

    internal Db2Session RequireSession()
    {
        EnsureOpen();
        var session = CurrentSession;
        if (session is null || session.IsClosed) throw new Db2AdoException("DB2 connection is closed.", Db2ErrorKind.ConnectionClosed);
        return session;
    }

    /// <summary>
    /// Converts a driver exception into the exception type thrown by this connection. Derived providers
    /// (for example the IBM compatibility layer) override this to change the exception surface.
    /// </summary>
    protected internal virtual Exception TranslateProviderError(Exception error) => Db2AdoException.From(error);

    /// <summary>
    /// Converts an operation-cancelled exception into the exception type thrown by this connection.
    /// </summary>
    protected internal virtual Exception TranslateOperationCancellation(Exception error) => error;

    internal static async ValueTask ClearAllPoolsAsync()
    {
        ParsedConnections.Clear();
        var pools = Pools.ToArray();
        foreach (var item in pools)
            if (Pools.TryRemove(new KeyValuePair<string, Db2Pool>(item.Key, item.Value))) await item.Value.DisposeAsync().ConfigureAwait(false);
    }

    public static void ClearAllPools() => ClearAllPoolsAsync().AsTask().GetAwaiter().GetResult();

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();
        base.Dispose(disposing);
    }

    private void EnsureOpen()
    {
        if (_state != ConnectionState.Open) throw new Db2AdoException("DB2 connection is not open.", Db2ErrorKind.ConnectionClosed);
    }

    private static ParsedConnection GetParsedConnection(string connectionString)
    {
        if (ParsedConnections.TryGetValue(connectionString, out var cached)) return cached;
        var parsed = TryParseConnectionString(connectionString);
        var entry = new ParsedConnection(parsed.Config, parsed.Pooling, parsed.MaxPoolSize,
            parsed.Pooling ? CreatePoolKey(connectionString, parsed.MaxPoolSize) : "", ReadPoolOptions(parsed.Config, parsed.MaxPoolSize));
        // Eviction changes only parsing cost; pool identity remains the same hash.
        if (ParsedConnections.Count >= 256) ParsedConnections.Clear();
        return ParsedConnections.GetOrAdd(connectionString, entry);
    }

    private static Db2PoolOptions ReadPoolOptions(Db2Config config, int maxConnections)
    {
        int Read(string key, int fallback)
        {
            if (!config.Parameters.TryGetValue(key, out var raw) && !config.Parameters.TryGetValue(key.Replace("_", "", StringComparison.Ordinal), out raw)) return fallback;
            if (!int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 1)
                throw new Db2ConfigurationException($"Invalid pool option {key}.");
            return value;
        }
        var options = new Db2PoolOptions(maxConnections)
        {
            MaxWaiters = Read("max_waiters", 100),
            AcquireTimeout = TimeSpan.FromSeconds(Read("pool_acquire_timeout", 30)),
            ConnectTimeout = TimeSpan.FromSeconds(Read("pool_connect_timeout", 10)),
            IdleTimeout = TimeSpan.FromSeconds(Read("pool_idle_timeout", 300)),
            MaxLifetime = TimeSpan.FromSeconds(Read("pool_max_lifetime", 1800)),
        };
        options.Validate();
        return options;
    }

    internal static string CreatePoolKey(string connectionString, int maxPoolSize) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{connectionString}\0{maxPoolSize}")));

    private static (Db2Config Config, bool Pooling, int MaxPoolSize) TryParseConnectionString(string connectionString)
    {
        try
        {
            if (connectionString.StartsWith("db2://", StringComparison.OrdinalIgnoreCase)
                || connectionString.StartsWith("db2s://", StringComparison.OrdinalIgnoreCase))
            {
                var urlConfig = Db2Config.Parse(connectionString);
                var urlPoolingEnabled = !urlConfig.Parameters.TryGetValue("pooling", out var urlPooling) || ParseBoolean(urlPooling);
                var urlPoolLimit = urlConfig.Parameters.TryGetValue("max_pool_size", out var urlMaxPoolSize)
                    ? int.Parse(urlMaxPoolSize, System.Globalization.CultureInfo.InvariantCulture)
                    : 10;
                if (urlPoolLimit < 1) throw new Db2ConfigurationException("MaxPoolSize must be a positive integer.");
                return (urlConfig, urlPoolingEnabled, urlPoolLimit);
            }
            var values = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var config = Db2Config.Parse(connectionString);
            if (Get(values, "Server", "Host", "HostName") is { Length: > 0 } server)
            {
                var (host, endpointPort) = ParseServer(server);
                config.Host = host;
                if (endpointPort.HasValue) config.Port = endpointPort.Value;
            }
            if (TryGet(values, out var explicitPort, "Port") && int.TryParse(Convert.ToString(explicitPort, System.Globalization.CultureInfo.InvariantCulture), out var port)) config.Port = port;
            var pooling = TryGet(values, out var poolingValue, "Pooling") ? ParseBoolean(poolingValue) : true;
            var maxPoolSize = TryGet(values, out var maxPoolSizeValue, "MaxPoolSize", "Max Pool Size")
                ? Convert.ToInt32(maxPoolSizeValue, System.Globalization.CultureInfo.InvariantCulture)
                : 10;
            if (maxPoolSize < 1) throw new Db2ConfigurationException("MaxPoolSize must be a positive integer.");
            return (config, pooling, maxPoolSize);
        }
        catch (Db2ConfigurationException) { throw; }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            throw new Db2ConfigurationException("Invalid ADO.NET Db2 connection string.", error);
        }
    }

    private Db2Config? ParseConfiguredValue() => string.IsNullOrWhiteSpace(_connectionString)
        ? null
        : TryParseConnectionString(_connectionString).Config;

    internal static string ValidateDatabaseName(string databaseName)
    {
        ArgumentNullException.ThrowIfNull(databaseName);
        var normalized = databaseName.Trim();
        if (normalized.Length == 0 || normalized.IndexOf('\0') >= 0)
            throw new ArgumentException("A non-empty Db2 database name without NUL characters is required.", nameof(databaseName));

        byte[] encoded;
        try { encoded = Ebcdic.EncodeCp500(normalized); }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("The Db2 database name contains characters that cannot be encoded as CP500.", nameof(databaseName), error);
        }
        if (encoded.Length > 18)
            throw new ArgumentException("The Db2 database name exceeds the 18-byte DRDA RDB name field.", nameof(databaseName));
        return normalized;
    }

    internal static string WithDatabase(string connectionString, string databaseName)
    {
        if (connectionString.StartsWith("db2://", StringComparison.OrdinalIgnoreCase)
            || connectionString.StartsWith("db2s://", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new UriBuilder(connectionString) { Path = "/" + Uri.EscapeDataString(databaseName) };
            return builder.Uri.AbsoluteUri;
        }

        var values = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (string key in values.Keys.Cast<string>().ToArray())
            if (key.Equals("Database", StringComparison.OrdinalIgnoreCase)
                || key.Equals("DB", StringComparison.OrdinalIgnoreCase)
                || key.Equals("DBName", StringComparison.OrdinalIgnoreCase))
                values.Remove(key);
        values["Database"] = databaseName;
        return values.ConnectionString;
    }

    private static string? Get(DbConnectionStringBuilder values, params string[] keys) =>
        TryGet(values, out var value, keys) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static bool TryGet(DbConnectionStringBuilder values, out object? value, params string[] keys)
    {
        foreach (var key in keys)
            if (values.TryGetValue(key, out value)) return true;
        value = null;
        return false;
    }

    private static bool ParseBoolean(object? value) => value switch
    {
        bool boolean => boolean,
        string text when bool.TryParse(text, out var boolean) => boolean,
        "1" or "yes" or "YES" => true,
        "0" or "no" or "NO" => false,
        _ => throw new Db2ConfigurationException($"Invalid Boolean connection string value '{value}'."),
    };

    private static (string Host, int? Port) ParseServer(string server)
    {
        var text = server.Trim();
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            var closing = text.IndexOf(']');
            if (closing < 0) throw new Db2ConfigurationException("Invalid bracketed IPv6 Server value.");
            var host = text[1..closing];
            var suffix = text[(closing + 1)..];
            if (suffix.Length == 0) return (host, null);
            if (suffix[0] != ':' || !int.TryParse(suffix[1..], out var ipv6Port) || ipv6Port is < 1 or > 65535)
                throw new Db2ConfigurationException("Invalid port in Server value.");
            return (host, ipv6Port);
        }
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out var port))
        {
            if (port is < 1 or > 65535) throw new Db2ConfigurationException("Server port must be between 1 and 65535.");
            return (text[..colon], port);
        }
        return (text, null);
    }
}

internal static class Db2AdoSchema
{
    internal static DataTable CreateMetaDataCollections()
    {
        var table = new DataTable("MetaDataCollections") { Locale = System.Globalization.CultureInfo.InvariantCulture };
        table.Columns.Add("CollectionName", typeof(string));
        table.Columns.Add("NumberOfRestrictions", typeof(int));
        table.Columns.Add("NumberOfIdentifierParts", typeof(int));
        table.Rows.Add("MetaDataCollections", 0, 0);
        table.Rows.Add("DataTypes", 1, 0);
        return table;
    }

    internal static DataTable CreateDataTypes()
    {
        var table = new DataTable("DataTypes") { Locale = System.Globalization.CultureInfo.InvariantCulture };
        table.Columns.Add("PROVIDER_TYPE", typeof(int));
        table.Columns.Add("PROVIDER_TYPE_NAME", typeof(string));
        table.Columns.Add("CREATE_PARAMS", typeof(string));
        table.Columns.Add("SQL_TYPE_NAME", typeof(string));
        foreach (var item in Types)
            table.Rows.Add(item.ProviderType, item.Name, item.CreateParams, item.SqlTypeName);
        return table;
    }

    internal static readonly (int ProviderType, string Name, string CreateParams, string SqlTypeName)[] Types =
    [
        (500, "SMALLINT", "", "SMALLINT"), (496, "INTEGER", "", "INTEGER"), (492, "BIGINT", "", "BIGINT"),
        (480, "REAL/DOUBLE", "", "DOUBLE"), (484, "DECIMAL", "precision,scale", "DECIMAL"), (996, "DECFLOAT", "precision", "DECFLOAT"),
        (384, "DATE", "", "DATE"), (388, "TIME", "", "TIME"), (392, "TIMESTAMP", "precision", "TIMESTAMP"),
        (452, "CHAR", "length", "CHAR"), (448, "VARCHAR", "length", "VARCHAR"), (456, "LONG VARCHAR", "", "LONG VARCHAR"),
        (468, "GRAPHIC", "length", "GRAPHIC"), (464, "VARGRAPHIC", "length", "VARGRAPHIC"), (472, "LONG VARGRAPHIC", "", "LONG VARGRAPHIC"),
        (912, "BINARY", "length", "BINARY"), (908, "VARBINARY", "length", "VARBINARY"), (404, "BLOB", "length", "BLOB"),
        (408, "CLOB", "length", "CLOB"), (412, "DBCLOB", "length", "DBCLOB"), (988, "XML", "", "XML"), (2436, "BOOLEAN", "", "BOOLEAN"),
    ];
}
