using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net.Security;
using System.Numerics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using JustyBase.Db2.Converters;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Security;
using JustyBase.Db2.Security.Kerberos;

namespace JustyBase.Db2;

public sealed record Db2ResultSet(IReadOnlyList<Db2ColumnMetadata> Columns, IReadOnlyList<object?[]> Rows);
public sealed record Db2QueryResult(IReadOnlyList<Db2ColumnMetadata> Columns, IReadOnlyList<object?[]> Rows)
{
    /// <summary>All dynamic result sets returned by a stored procedure.</summary>
    public IReadOnlyList<Db2ResultSet> ResultSets { get; init; } = Array.Empty<Db2ResultSet>();
}
/// <summary>A single row yielded by <see cref="Db2Session.QueryStreamAsync(string, System.Threading.CancellationToken)"/>.</summary>
public sealed record Db2StreamRow(IReadOnlyList<Db2ColumnMetadata> Columns, object?[] Values);
internal readonly record struct Db2DecodedRow(object?[] Values, Db2Warning? Warning);
internal sealed record Db2ProgressiveLobReference(ulong Length, ulong Reference);
/// <summary>A one-shot BLOB parameter whose bytes are sent through EXTDTA without buffering the payload.</summary>
public sealed record Db2BlobStream(Stream Source, long Length, bool LeaveOpen = true);
/// <summary>A one-shot CLOB parameter whose UTF-8 bytes are sent through EXTDTA without buffering the payload.</summary>
public sealed record Db2ClobStream(Stream Source, long Length, bool LeaveOpen = true);
/// <summary>A one-shot DBCLOB parameter whose UTF-8 bytes are converted by Db2 and sent through EXTDTA without buffering the payload.</summary>
public sealed record Db2DbClobStream(Stream Source, long Length, bool LeaveOpen = true);
/// <summary>A one-shot XML parameter whose serialized UTF-8 bytes are sent through EXTDTA without buffering the payload.</summary>
public sealed record Db2XmlStream(Stream Source, long Length, bool LeaveOpen = true);
public sealed record Db2InsertResult(long RowsAffected, long LastInsertId);
public enum Db2ParameterDirection { Input, Output, InputOutput }
/// <summary>A positional procedure parameter. For Output parameters, Value is a typed placeholder for SQLDTA binding.</summary>
public sealed record Db2Parameter(object? Value, Db2ParameterDirection Direction = Db2ParameterDirection.Input);
/// <summary>A prepared execution result with values aligned to the supplied parameter positions; input-only positions are null.</summary>
public sealed record Db2ExecutionResult(long RowsAffected, IReadOnlyList<object?> OutputParameters)
{
    /// <summary>Dynamic result sets returned by a stored procedure.</summary>
    public IReadOnlyList<Db2ResultSet> ResultSets { get; init; } = Array.Empty<Db2ResultSet>();
}
public sealed record Db2ClientInfo(string ApplicationName = "", string WorkstationName = "", string UserId = "", string Accounting = "", string CorrelationToken = "");

/// <summary>Per-command deadline and the maximum time allowed to confirm and drain SQLINTR cancellation.</summary>
public sealed record Db2CommandOptions(TimeSpan Timeout, TimeSpan CancellationCleanupTimeout)
{
    /// <summary>Applies this session user before the command (go-db2's context WithUser).</summary>
    public string? User { get; init; }

    /// <summary>Applies these client-info registers before the command (go-db2's context WithClientInfo).
    /// Empty fields keep their current session value.</summary>
    public Db2ClientInfo? ClientInfo { get; init; }

    public static Db2CommandOptions Default { get; } = new(TimeSpan.Zero, TimeSpan.FromSeconds(30));
    internal void Validate()
    {
        if (Timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Timeout));
        if (CancellationCleanupTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(CancellationCleanupTimeout));
    }
}

/// <summary>A prepared statement and the server-described input and result columns.</summary>
public sealed class Db2Statement
{
    private bool _closed;

    internal Db2Statement(Db2Session session, string sql, IReadOnlyList<Db2ColumnMetadata> outputColumns, IReadOnlyList<Db2ColumnMetadata> parameterColumns)
    {
        Session = session; Sql = sql; OutputColumns = outputColumns; ParameterColumns = parameterColumns;
    }

    internal Db2ResultMetadataCache? ResultMetadataCache;
    internal SqlDtaEncoder.DescriptorCache? ParameterDescriptorCache;
    internal Db2Session Session { get; }
    public string Sql { get; }
    public IReadOnlyList<Db2ColumnMetadata> OutputColumns { get; }
    public IReadOnlyList<Db2ColumnMetadata> ParameterColumns { get; }
    public bool IsClosed => _closed;

    public Task<long> ExecuteAsync(IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default)
    {
        EnsureOpen(); return Session.ExecuteAsync(this, parameters, cancellationToken);
    }

    public Task<long> ExecuteAsync(IReadOnlyList<object?> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        EnsureOpen(); return Session.ExecuteAsync(this, parameters, options, cancellationToken);
    }

    public Task<Db2InsertResult> ExecuteInsertAsync(IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default)
    {
        EnsureOpen(); return Session.ExecuteInsertAsync(this, parameters, cancellationToken);
    }

    public Task<Db2QueryResult> QueryAsync(IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default)
    {
        EnsureOpen(); return Session.QueryAsync(this, parameters, cancellationToken);
    }

    public Task<Db2QueryResult> QueryAsync(IReadOnlyList<object?> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        EnsureOpen(); return Session.QueryAsync(this, parameters, options, cancellationToken);
    }

    public IAsyncEnumerable<Db2StreamRow> QueryStreamAsync(IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default)
    {
        EnsureOpen(); return Session.QueryStreamAsync(this, parameters, cancellationToken);
    }

    public IAsyncEnumerable<Db2StreamRow> QueryStreamAsync(IReadOnlyList<object?> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        EnsureOpen(); return Session.QueryStreamAsync(this, parameters, options, cancellationToken);
    }

    public Task<Db2ExecutionResult> ExecuteWithOutputsAsync(IReadOnlyList<Db2Parameter> parameters, CancellationToken cancellationToken = default)
    {
        EnsureOpen(); return Session.ExecuteWithOutputsAsync(this, parameters, cancellationToken);
    }

    public Task<Db2ExecutionResult> ExecuteWithOutputsAsync(IReadOnlyList<Db2Parameter> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        EnsureOpen(); return Session.ExecuteWithOutputsAsync(this, parameters, options, cancellationToken);
    }

    public void Close() => _closed = true;
    internal void EnsureOpen() { if (_closed) throw new InvalidOperationException("DB2 statement is closed."); }
}

/// <summary>A transaction scope over one Db2 session. Dispose an unfinished scope to roll it back.</summary>
public sealed class Db2Transaction : IAsyncDisposable
{
    private bool _completed;
    private readonly long _generation;

    internal Db2Transaction(Db2Session session, long generation) { Session = session; _generation = generation; }
    internal Db2Session Session { get; }

    public Task<Db2QueryResult> QueryAsync(string sql, CancellationToken cancellationToken = default)
    {
        EnsureActive(); return Session.QueryAsync(sql, cancellationToken);
    }

    public Task<Db2QueryResult> QueryAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default)
    {
        EnsureActive(); return Session.QueryAsync(sql, parameters, cancellationToken);
    }

    public Task<long> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        EnsureActive(); return Session.ExecuteAsync(sql, cancellationToken);
    }

    public Task<long> ExecuteAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default)
    {
        EnsureActive(); return Session.ExecuteAsync(sql, parameters, cancellationToken);
    }

    public Task<Db2InsertResult> ExecuteInsertAsync(string sql, CancellationToken cancellationToken = default)
    {
        EnsureActive(); return Session.ExecuteInsertAsync(sql, cancellationToken);
    }

    public Task<Db2InsertResult> ExecuteInsertAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default)
    {
        EnsureActive(); return Session.ExecuteInsertAsync(sql, parameters, cancellationToken);
    }

    public Task CommitAsync(CancellationToken cancellationToken = default) => FinishAsync(true, cancellationToken);
    public Task RollbackAsync(CancellationToken cancellationToken = default) => FinishAsync(false, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_completed) return;
        await RollbackAsync().ConfigureAwait(false);
        // Implicit rollback means the caller abandoned the scope without an explicit
        // commit/rollback (Rust Drop invalidates in this case). Discard the session
        // so a pool cannot reuse it; the server rolls back on disconnect.
        Session.MarkTransactionAbandoned();
    }

    private async Task FinishAsync(bool commit, CancellationToken cancellationToken)
    {
        EnsureActive();
        try
        {
            await Session.CompleteTransactionAsync(commit, _generation, cancellationToken).ConfigureAwait(false);
            _completed = true;
        }
        catch
        {
            // Cancellation before the session gate is acquired did not send a
            // transaction-control request; keep the scope available for retry/rollback.
            // Once the session advances the generation, the outcome is uncertain.
            if (!Session.IsTransactionActive(_generation)) _completed = true;
            throw;
        }
    }

    private void EnsureActive()
    {
        if (_completed) throw new InvalidOperationException("DB2 transaction is already complete.");
        if (!Session.IsTransactionActive(_generation)) throw new InvalidOperationException("DB2 transaction is no longer active.");
    }
}

/// <summary>A serialized native DRDA session over TCP or TLS.</summary>
public sealed partial class Db2Session : IAsyncDisposable
{
    private const int MaxMaterializedLobBytes = 16 * 1024 * 1024;
    private const long MaxMaterializedResultBytes = 32L * 1024 * 1024;
    private const string PackageId = "SYSSH200";
    private const string ConsistencyToken = "SYSLVL01";
    private const ushort SectionNumber = 65;
    private const int MaximumSecurityTokenLength = 32000;

    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly Db2Config _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _interruptGate = new();
    private byte[]? _rdbInterruptToken;
    private ushort _sqlamLevel = 11;
    private Task<bool>? _interruptPending;
    private int _operationActive;
    private int _interruptUsed;
    private readonly string _database;
    private readonly ushort _securityMechanism;
    private readonly KerberosCredentialPlan? _kerberosPlan;
    private readonly Func<IKerberosTokenProvider>? _kerberosProviderFactory;
    private readonly uint _blockSize;

    /// <summary>Raised when Db2 returns a successful SQL warning on this session.</summary>
    public event EventHandler<Db2WarningEventArgs>? Warning;
    private ushort _correlationId = 1;
    private bool _closed;
    private int _disposeStarted;
    private string? _transactionIsolationBaseline;
    private bool _transactionActive;
    private long _transactionGeneration;
    private string? _preparedSql;
    private string _currentUser;
    private string _baseUser;
    private Db2ClientInfo _clientInfo = new();
    private Db2ClientInfo _baseClientInfo = new();
    private string? _baseSchema;
    private string? _baseIsolation;
    private bool _poolBaselineCaptured;
    // Conservatively dirty until an actual baseline/reset completes. Any exchange
    // may run server-side code; a SQL keyword is not evidence of register safety.
    private bool _poolStateDirty = true;
    private readonly DssWriteBuffer _writeBuffer = new();
    private DssReadBuffer? _readBuffer;

    internal Db2Session(TcpClient client, Stream stream, Db2Config config, bool poolBaselineCaptured = false, KerberosCredentialPlan? kerberosPlan = null,
        Func<IKerberosTokenProvider>? kerberosProviderFactory = null)
    {
        _kerberosPlan = kerberosPlan;
        _kerberosProviderFactory = kerberosProviderFactory;
        _client = client;
        _stream = stream;
        _config = config;
        _database = config.Database.PadRight(18)[..18];
        _securityMechanism = config.SecurityMechanism == 0 ? (ushort)3 : config.SecurityMechanism;
        _blockSize = config.BlockSize >= 1024 ? (uint)config.BlockSize : 65535;
        _currentUser = config.User;
        _baseUser = config.User;
        _poolBaselineCaptured = poolBaselineCaptured;
    }

    public bool AutoCommit { get; set; } = true;
    public bool IsClosed => _closed;
    public string CurrentSessionUser => _currentUser;
    public Db2ClientInfo GetClientInfo() => _clientInfo;
    public bool ClientInfoMatches(Db2ClientInfo info) => _clientInfo == info;

    /// <summary>Sends a cancellation request for the active command. A true result means Db2 acknowledged the request, not that the command stopped. Await the original command; it can return SQLCODE -952 or complete normally if cancellation lost a race.</summary>
    public Task<bool> InterruptAsync(CancellationToken cancellationToken = default)
    {
        lock (_interruptGate)
        {
            if (_interruptPending is not null) return _interruptPending;
            if (Volatile.Read(ref _operationActive) == 0 || IsClosed) return Task.FromResult(false);
            if (_rdbInterruptToken is null) throw new InvalidOperationException("DB2 did not provide an interrupt token.");
            Volatile.Write(ref _interruptUsed, 1);
            _interruptPending = SendInterruptTrackedAsync(_rdbInterruptToken, cancellationToken);
            return _interruptPending;
        }
    }

    private async Task<bool> InterruptAsync(TimeSpan acknowledgmentTimeout)
    {
        using var source = new CancellationTokenSource(acknowledgmentTimeout);
        return await InterruptAsync(source.Token).ConfigureAwait(false);
    }

    private async Task<bool> SendInterruptTrackedAsync(byte[] token, CancellationToken cancellationToken)
    {
        try { return await SendInterruptAsync(token, cancellationToken).ConfigureAwait(false); }
        finally { lock (_interruptGate) _interruptPending = null; }
    }

    private async Task<bool> SendInterruptAsync(byte[] token, CancellationToken cancellationToken)
    {
        var timeout = _config.Timeout > TimeSpan.Zero ? _config.Timeout : TimeSpan.FromSeconds(30);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var auxiliary = new TcpClient();
        auxiliary.NoDelay = true;
        await auxiliary.ConnectAsync(_config.Host, _config.Port, timeoutSource.Token).ConfigureAwait(false);
        Stream stream = auxiliary.GetStream();
        if (_config.UseSsl)
        {
            var certificates = new X509CertificateCollection();
            if (!string.IsNullOrEmpty(_config.SslClientCertPath))
            {
                var certificate = string.IsNullOrEmpty(_config.SslClientKeyPath)
                    ? X509CertificateLoader.LoadCertificateFromFile(_config.SslClientCertPath)
                    : X509Certificate2.CreateFromPemFile(_config.SslClientCertPath, _config.SslClientKeyPath);
                certificates.Add(certificate);
            }
            var ssl = new SslStream(stream, false, CreateCertificateValidator(_config.SslRootCaPath));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = _config.Host,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                ClientCertificates = certificates,
            }, timeoutSource.Token).ConfigureAwait(false);
            stream = ssl;
        }
        var excsat = DssCodec.EncodeRequest(DdmCodec.PackExcsat("JustyBase.Db2-interrupt", Environment.MachineName, "SQL12010", "JustyBase.Db2"), 1, false, true);
        await stream.WriteAsync(excsat.Bytes, timeoutSource.Token).ConfigureAwait(false);
        var negotiation = await DssCodec.ReadAsync(stream, timeoutSource.Token).ConfigureAwait(false);
        if (negotiation.CodePoint != (ushort)CodePoint.EXCSATRD) throw new InvalidDataException($"DB2 interrupt negotiation failed: 0x{negotiation.CodePoint:X4}.");
        var request = DssCodec.EncodeRequest(DdmCodec.PackInterruptRdbRequest(token), excsat.NextCorrelationId, false, true);
        await stream.WriteAsync(request.Bytes, timeoutSource.Token).ConfigureAwait(false);
        var reply = await DssCodec.ReadAsync(stream, timeoutSource.Token).ConfigureAwait(false);
        if (reply.CodePoint != (ushort)CodePoint.CMDCMPRM) throw new InvalidDataException($"DB2 interrupt rejected: 0x{reply.CodePoint:X4}.");
        var fields = DdmCodec.ParseParameters(reply.Payload);
        if (fields.TryGetValue((ushort)CodePoint.SVRCOD, out var severity) && severity.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(severity) != 0)
            throw new InvalidDataException("DB2 interrupt returned an error severity.");
        return true;
    }

    public static async Task<Db2Session> ConnectAsync(string dsn, CancellationToken cancellationToken = default) =>
        await ConnectAsync(Db2Config.Parse(dsn), cancellationToken).ConfigureAwait(false);

    public static Task<Db2Session> ConnectAsync(Db2Config config, CancellationToken cancellationToken = default) =>
        ConnectAsync(config, null, cancellationToken);

    /// <summary>Test seam: <paramref name="kerberosProviderFactory"/> replaces credential resolution and the GSS-API layer.</summary>
    internal static async Task<Db2Session> ConnectAsync(Db2Config config, Func<IKerberosTokenProvider>? kerberosProviderFactory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.Host)) throw new Db2ConfigurationException("DB2 host is required.");
        if (config.Port is < 1 or > 65535) throw new Db2ConfigurationException($"Invalid DB2 port {config.Port}; expected a value from 1 through 65535.");
        if (string.IsNullOrWhiteSpace(config.Database)) throw new Db2ConfigurationException("DB2 database name is required.");
        // Kerberos establishes the identity from the ticket, so a user name is optional there.
        if (string.IsNullOrWhiteSpace(config.User) && config.SecurityMechanism != 11) throw new Db2ConfigurationException("DB2 user name is required.");
        if (config.SecurityMechanism is not (0 or 3 or 9 or 11))
            throw new Db2ConfigurationException($"Unsupported DB2 authentication mechanism {config.SecurityMechanism}. Supported mechanisms are 3 (USRIDPWD), 9 (EUSRIDPWD), and 11 (Kerberos/GSSAPI).");
        // Resolved before any socket is opened so configuration mistakes never reach the wire.
        var kerberosPlan = config.SecurityMechanism == 11 && kerberosProviderFactory is null ? KerberosCredentialPlan.Resolve(config) : null;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (config.Timeout > TimeSpan.Zero) timeoutSource.CancelAfter(config.Timeout);
        var client = new TcpClient();
        client.NoDelay = true;
        try
        {
            await client.ConnectAsync(config.Host, config.Port, timeoutSource.Token).ConfigureAwait(false);
            Stream stream = client.GetStream();
            if (config.UseSsl)
            {
                var certificates = new X509CertificateCollection();
                if (!string.IsNullOrEmpty(config.SslClientCertPath))
                {
                    var certificate = string.IsNullOrEmpty(config.SslClientKeyPath)
                        ? X509CertificateLoader.LoadCertificateFromFile(config.SslClientCertPath)
                        : X509Certificate2.CreateFromPemFile(config.SslClientCertPath, config.SslClientKeyPath);
                    certificates.Add(certificate);
                }
                var ssl = new SslStream(stream, false, CreateCertificateValidator(config.SslRootCaPath));
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = config.Host,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                    ClientCertificates = certificates,
                }, timeoutSource.Token).ConfigureAwait(false);
                stream = ssl;
            }
#if DB2_PERF_COUNTERS
            stream = new Db2PerformanceCounters.CountingStream(stream);
#endif
            var session = new Db2Session(client, stream, config, kerberosPlan: kerberosPlan, kerberosProviderFactory: kerberosProviderFactory);
            await session.HandshakeAsync(timeoutSource.Token).ConfigureAwait(false);
            return session;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Switches the active SQL authorization identifier within a Db2 trusted context.</summary>
    public Task SwitchUserAsync(string user, CancellationToken cancellationToken = default) =>
        SwitchUserAsync(user, password: null, cancellationToken);

    /// <summary>
    /// Switches the active SQL authorization identifier. <paramref name="password"/>
    /// enables the DRDA SECCHK re-authentication fallback when the SQL
    /// session-authorization statements are rejected, mirroring go-db2.
    /// </summary>
    public async Task SwitchUserAsync(string user, string? password, CancellationToken cancellationToken = default)
    {
        var target = ValidateUserIdentifier(user);
        await WithGateAsync(async token =>
        {
            EnsureOpen();
            await SwitchUserLockedAsync(target, token, password: password).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<Db2QueryResult> QueryAsync(string sql, CancellationToken cancellationToken = default) =>
        WithGateAsync(token => QueryLockedAsync(sql, token), cancellationToken);

    /// <summary>Runs a query with a command deadline. A zero timeout disables the deadline.</summary>
    public Task<Db2QueryResult> QueryAsync(string sql, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return WithGateAsync(token => QueryLockedAsync(sql, token), cancellationToken, options);
    }

    private async Task StartDirectQueryStreamAsync(string sql, CancellationToken cancellationToken)
    {
        _correlationId = 1;
        _preparedSql = null;
        await SendAsync(DdmCodec.PackPrepareSqlStatement(PackageId, ConsistencyToken, SectionNumber, _database), true, false, cancellationToken).ConfigureAwait(false);
        await SendAsync(DdmCodec.PackSqlStatement(sql), false, false, cancellationToken).ConfigureAwait(false);
        _preparedSql = sql;
        await SendAsync(DdmCodec.PackOpenQuery(PackageId, ConsistencyToken, SectionNumber, _database, _blockSize), false, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads query rows incrementally, retaining at most the current Db2 reply block and one partial row.</summary>
    public IAsyncEnumerable<Db2StreamRow> QueryStreamAsync(string sql, CancellationToken cancellationToken = default) =>
        QueryRowsCoreAsync(sql, null, null, cancellationToken);

    public IAsyncEnumerable<Db2StreamRow> QueryStreamAsync(string sql, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return QueryRowsCoreAsync(sql, null, null, cancellationToken, options);
    }

    public IAsyncEnumerable<Db2StreamRow> QueryStreamAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default) =>
        QueryRowsCoreAsync(sql, parameters, null, cancellationToken);

    public IAsyncEnumerable<Db2StreamRow> QueryStreamAsync(string sql, IReadOnlyList<object?> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return QueryRowsCoreAsync(sql, parameters, null, cancellationToken, options);
    }

    public IAsyncEnumerable<Db2StreamRow> QueryStreamAsync(Db2Statement statement, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default) =>
        QueryRowsCoreAsync(statement.Sql, parameters, statement, cancellationToken);

    public IAsyncEnumerable<Db2StreamRow> QueryStreamAsync(Db2Statement statement, IReadOnlyList<object?> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return QueryRowsCoreAsync(statement.Sql, parameters, statement, cancellationToken, options);
    }

    internal IAsyncEnumerable<Db2StreamRow> QueryReaderStreamAsync(string sql, Action<IReadOnlyList<Db2ColumnMetadata>> metadata, CancellationToken cancellationToken) =>
        QueryRowsCoreAsync(sql, null, null, cancellationToken, metadata: metadata);

    private async IAsyncEnumerable<Db2StreamRow> QueryRowsCoreAsync(string sql, IReadOnlyList<object?>? parameters, Db2Statement? statement,
        [EnumeratorCancellation] CancellationToken token, Db2CommandOptions? options = null, Action<IReadOnlyList<Db2ColumnMetadata>>? metadata = null)
    {
        await foreach (var block in QueryStreamCoreAsync(sql, parameters, statement, token, options, metadata).ConfigureAwait(false))
            yield return block.Materialized!;
    }

    internal IAsyncEnumerable<Db2ReaderBlock> QueryReaderBlocksAsync(string sql, Db2Statement? statement, IReadOnlyList<object?>? parameters,
        Action<IReadOnlyList<Db2ColumnMetadata>> metadata, CancellationToken token) =>
        QueryStreamCoreAsync(sql, parameters, statement, token, metadata: metadata, buffered: true);

    private async IAsyncEnumerable<Db2ReaderBlock> QueryStreamCoreAsync(string sql, IReadOnlyList<object?>? parameters, Db2Statement? suppliedStatement, [EnumeratorCancellation] CancellationToken cancellationToken, Db2CommandOptions? options = null, Action<IReadOnlyList<Db2ColumnMetadata>>? metadata = null, bool buffered = false)
    {
        options?.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        QueryStreamState? state = null;
        Task<bool>? cancellationInterrupt = null;
        CancellationTokenRegistration cancellationRegistration = default;
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options?.Timeout > TimeSpan.Zero) commandCancellation.CancelAfter(options.Timeout);
        var commandToken = commandCancellation.Token;
        var cleanupTimeout = options?.CancellationCleanupTimeout ?? TimeSpan.FromSeconds(30);
        var commitAfterQuery = false;
        try
        {
            Task<bool>? pending;
            lock (_interruptGate) pending = _interruptPending;
            if (pending is not null)
            {
                try { await pending.WaitAsync(commandToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (commandToken.IsCancellationRequested) { throw; }
                catch { /* A completed interrupt does not poison the next request. */ }
            }

            // Db2Config.Timeout bounds connection establishment and interrupt transport setup.
            // Commands have no deadline unless Db2CommandOptions specifies one.
            using var operationTimeout = new CancellationTokenSource();
            Volatile.Write(ref _operationActive, 1);
            cancellationRegistration = commandToken.Register(() =>
            {
                if (Volatile.Read(ref _operationActive) == 0) return;
                operationTimeout.CancelAfter(cleanupTimeout);
                try { cancellationInterrupt = InterruptAsync(cleanupTimeout); }
                catch { /* The cleanup deadline aborts and discards the unresponsive primary exchange. */ }
            });

            EnsureOpen();
            IReadOnlyList<Db2ColumnMetadata>? describedColumns = null;
            Db2Statement? effectiveStatement = null;
            if (parameters is not null)
            {
                var statement = suppliedStatement;
                if (statement is null) statement = await PrepareLockedAsync(sql, operationTimeout.Token).ConfigureAwait(false);
                else
                {
                    EnsureStatement(statement);
                    // All prepared commands share section 65. A retained statement can
                    // outlive another PREPARE, so restore its SQL and descriptors before
                    // encoding parameters and opening that section's cursor.
                    if (_preparedSql != statement.Sql)
                        statement = await PrepareLockedAsync(statement.Sql, operationTimeout.Token).ConfigureAwait(false);
                }
                effectiveStatement = statement;
                if (!ReferenceEquals(statement.Session, this)) throw new ArgumentException("The prepared statement belongs to another Db2 session.", nameof(suppliedStatement));
                if (parameters.Count != statement.ParameterColumns.Count)
                    throw new ArgumentException($"DB2 expected {statement.ParameterColumns.Count} parameters, got {parameters.Count}.", nameof(parameters));
                if (statement.ParameterColumns.Count > 0)
                {
                    var encoded = SqlDtaEncoder.BuildWithExternalData(statement.ParameterColumns, parameters, statement.ParameterDescriptorCache ??= new());
                    _correlationId = 1;
                    await SendAsync(DdmCodec.PackOpenQueryWithParams(PackageId, ConsistencyToken, SectionNumber, _database, _blockSize), true, false, operationTimeout.Token).ConfigureAwait(false);
                    await SendAsync(encoded.SqlDta, encoded.ExternalData.Count > 0, encoded.ExternalData.Count == 0, operationTimeout.Token).ConfigureAwait(false);
                    for (var index = 0; index < encoded.ExternalData.Count; index++)
                        await SendExternalDataAsync(encoded.ExternalData[index], index < encoded.ExternalData.Count - 1, index == encoded.ExternalData.Count - 1, operationTimeout.Token, commandToken).ConfigureAwait(false);
                    describedColumns = statement.OutputColumns;
                    commitAfterQuery = AutoCommit;
                }
                else await StartDirectQueryStreamAsync(sql, operationTimeout.Token).ConfigureAwait(false);
            }
            else await StartDirectQueryStreamAsync(sql, operationTimeout.Token).ConfigureAwait(false);

            state = new QueryStreamState
            {
                Columns = describedColumns?.ToArray() ?? Array.Empty<Db2ColumnMetadata>(),
                UseBufferedRows = buffered,
                MetadataCache = buffered ? GetReaderMetadataCache(sql, effectiveStatement) : null,
                CancelCommand = () =>
                {
                    try { commandCancellation.Cancel(); }
                    catch (ObjectDisposedException) { }
                },
            };
            List<object?[]> rows;
            try
            {
                rows = (await ReadStreamingRowsAsync(state, operationTimeout.Token).ConfigureAwait(false)).Rows;
                // A locator-bearing read keeps a Db2 unit of work alive even
                // after ENDQRYRM. Release it before a pooled reader returns its
                // connection; ordinary non-LOB reads retain their fast path.
                if (state.HasLobFields && AutoCommit) commitAfterQuery = true;
                metadata?.Invoke(state.Columns);
            }
            catch (Exception error)
            {
                state.Failed = true;
                if ((error as IDb2Error)?.SqlCode == -952) state.Ended = true;
                if (commandToken.IsCancellationRequested) ThrowCommandCancellation(cancellationToken, commandToken, options, error);
                throw;
            }
            if (state.BufferedRows is { HasRows: true } firstBlock)
            {
                if (commandToken.IsCancellationRequested) ThrowCommandCancellation(cancellationToken, commandToken, options);
                yield return new Db2ReaderBlock(firstBlock);
            }
            if (state.HasLobFields)
                foreach (var row in rows) state.PendingRows.Enqueue(row);
            foreach (var row in rows)
            {
                if (state.HasLobFields)
                {
                    state.PendingRows.Dequeue();
                    state.CurrentYieldedRow = row;
                    state.CurrentRowGeneration++;
                    ExposeProgressiveLobs(row, state, operationTimeout.Token);
                }
                if (commandToken.IsCancellationRequested) ThrowCommandCancellation(cancellationToken, commandToken, options);
                yield return new Db2ReaderBlock(new Db2StreamRow(state.Columns, row));
                if (state.HasLobFields)
                {
                    await DrainProgressiveLobsAsync(row).ConfigureAwait(false);
                    state.CurrentYieldedRow = null;
                }
            }

            while (state.IsOpen && !state.Ended)
            {
                var priorData = state.PendingByteCount;
                var priorExternal = state.ExternalData.Count;
                await SendAsync(DdmCodec.PackContinueQuery(PackageId, ConsistencyToken, SectionNumber, _database, _blockSize, state.QueryId), false, true, operationTimeout.Token, state.NextCorrelation).ConfigureAwait(false);
                bool gotData;
                List<object?[]> gotRows;
                try
                {
                    (gotRows, gotData) = await ReadStreamingRowsAsync(state, operationTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    state.Failed = true;
                    if ((error as IDb2Error)?.SqlCode == -952) state.Ended = true;
                    if (commandToken.IsCancellationRequested) ThrowCommandCancellation(cancellationToken, commandToken, options, error);
                    throw;
                }
                if (state.HasLobFields)
                    foreach (var row in gotRows) state.PendingRows.Enqueue(row);
                if (!gotData && state.PendingByteCount == priorData && state.ExternalData.Count == priorExternal && !state.Ended)
                {
                    state.Failed = true;
                    throw new InvalidDataException("DB2 CNTQRY returned neither query data nor an end-of-query reply.");
                }
                if (state.BufferedRows is { HasRows: true } nextBlock)
                {
                    if (commandToken.IsCancellationRequested) ThrowCommandCancellation(cancellationToken, commandToken, options);
                    yield return new Db2ReaderBlock(nextBlock);
                }
                foreach (var row in gotRows)
                {
                    if (state.HasLobFields)
                    {
                        state.PendingRows.Dequeue();
                        state.CurrentYieldedRow = row;
                        state.CurrentRowGeneration++;
                        ExposeProgressiveLobs(row, state, operationTimeout.Token);
                    }
                    if (commandToken.IsCancellationRequested) ThrowCommandCancellation(cancellationToken, commandToken, options);
                    yield return new Db2ReaderBlock(new Db2StreamRow(state.Columns, row));
                    if (state.HasLobFields)
                    {
                        await DrainProgressiveLobsAsync(row).ConfigureAwait(false);
                        state.CurrentYieldedRow = null;
                    }
                }
            }
            if (state.PendingByteCount != 0)
                throw new InvalidDataException("DB2 ended a streamed query in the middle of a row.");
            if (commandToken.IsCancellationRequested) ThrowCommandCancellation(cancellationToken, commandToken, options);
        }
        finally
        {
            cancellationRegistration.Dispose();
            Exception? cleanupFailure = null;
            var failedBeforeCleanup = state?.Failed == true;
            try
            {
                if (!commandToken.IsCancellationRequested && state is { HasLobFields: true, Ended: false, Failed: false, CurrentYieldedRow: { } yieldedRow })
                {
                    await DrainProgressiveLobsAsync(yieldedRow).ConfigureAwait(false);
                    state.CurrentYieldedRow = null;
                }
                if (state is not null)
                {
                    if (commandToken.IsCancellationRequested)
                    {
                        state.CurrentYieldedRow = null;
                        state.PendingRows.Clear();
                    }
                    else
                    {
                        while (state.PendingRows.TryDequeue(out var pendingRow))
                        {
                            state.CurrentRowGeneration++;
                            ExposeProgressiveLobs(pendingRow, state, CancellationToken.None);
                            await DrainProgressiveLobsAsync(pendingRow).ConfigureAwait(false);
                        }
                    }
                }
                if (state is { IsOpen: true, Ended: false } && commandToken.IsCancellationRequested)
                {
                    await DisposeAsync().ConfigureAwait(false);
                }
                else if (state is { IsOpen: true, Ended: false })
                {
                    // An early consumer exit must not return a session with an open query to the pool.
                    // Drain without retaining rows; if the server cannot finish, invalidate the session.
                    try
                    {
                        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        if (!state.HasLobFields && state.Decoders.Length > 0 &&
                            state.Decoders.All(static decoder => decoder.Layout is not (Db2FieldLayout.Lob or Db2FieldLayout.Unsupported)))
                        {
                            state.DiscardRows = new Db2DiscardRows(state.Decoders);
                            if (state.BufferedRows is { } bufferedRows) state.DiscardRows.Append(bufferedRows.PendingData);
                            else state.DiscardRows.Append(state.PendingRowData);
                            state.PendingRowData = [];
                        }
                        while (state.IsOpen && !state.Ended)
                        {
                            await SendAsync(DdmCodec.PackContinueQuery(PackageId, ConsistencyToken, SectionNumber, _database, _blockSize, state.QueryId), false, true, drainTimeout.Token, state.NextCorrelation).ConfigureAwait(false);
                            if (state.DiscardRows is not null)
                                await ReadDiscardRowsAsync(state, drainTimeout.Token).ConfigureAwait(false);
                            else
                            {
                                var drainedRows = ConsumeStreamingReplies(await ReadReplyChainAsync(drainTimeout.Token).ConfigureAwait(false), state);
                                await ResolveProgressiveLobsAsync(drainedRows, state.Fields, state.QueryId, state.NextCorrelation, drainTimeout.Token).ConfigureAwait(false);
                            }
                            if (state.Ended && state.PendingByteCount != 0)
                                throw new InvalidDataException("DB2 ended a streamed query in the middle of a row.");
                        }
                    }
                    catch (Exception error)
                    {
                        // A late server/row error is observable on Close/Dispose.
                        // Never return a failed or uncertain cleanup to the pool.
                        state.Failed = true;
                        _closed = true;
                        _client.Dispose();
                        if (!failedBeforeCleanup) cleanupFailure = error;
                    }
                }
                if (state is { Ended: true, Failed: false } && state.PendingByteCount != 0)
                {
                    state.Failed = true;
                    _closed = true;
                    _client.Dispose();
                    cleanupFailure = new InvalidDataException("DB2 ended a streamed query in the middle of a row.");
                }
                if (commitAfterQuery && state is { Ended: true, Failed: false, PendingByteCount: 0 } && !_closed)
                {
                    try
                    {
                        using var commitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        _correlationId = 1;
                        await SendAsync(DdmCodec.PackCommit(), false, true, commitTimeout.Token).ConfigureAwait(false);
                        foreach (var reply in await ReadReplyChainAsync(commitTimeout.Token).ConfigureAwait(false))
                            ThrowIfServerError(reply);
                    }
                    catch
                    {
                        _closed = true;
                        _client.Dispose();
                        throw;
                    }
                }
                if (cancellationInterrupt is not null)
                {
                    try
                    {
                        if (!await cancellationInterrupt.WaitAsync(cleanupTimeout).ConfigureAwait(false))
                            throw new InvalidDataException("DB2 did not acknowledge SQLINTR.");
                    }
                    catch (Exception error)
                    {
                        await DisposeAsync().ConfigureAwait(false);
                        throw new Db2ConnectionClosedException($"DB2 SQLINTR could not be confirmed; the session was discarded. {error.Message}");
                    }
                }
                if (commandToken.IsCancellationRequested)
                    ThrowCommandCancellation(cancellationToken, commandToken, options);
                if (cleanupFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
            finally
            {
                state?.DiscardRows?.Dispose(); state?.BufferedRows?.Dispose();
                if (state is not null)
                {
                    state.DecodedRows?.Clear(); state.DecodedRows = null;
                    state.MaterializedRows?.Clear(); state.MaterializedRows = null;
                }
                Volatile.Write(ref _operationActive, 0); _gate.Release();
            }
        }
    }

    public Task<Db2QueryResult> QueryAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default) =>
        WithGateAsync(async (token, sourceReadToken) =>
        {
            EnsureOpen();
            var statement = await PrepareLockedAsync(sql, token).ConfigureAwait(false);
            return await QueryPreparedLockedAsync(statement, parameters, token, alreadyPrepared: true, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<Db2QueryResult> QueryAsync(string sql, IReadOnlyList<object?> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return WithGateAsync(async (token, sourceReadToken) =>
        {
            EnsureOpen(); var statement = await PrepareLockedAsync(sql, token).ConfigureAwait(false);
            return await QueryPreparedLockedAsync(statement, parameters, token, alreadyPrepared: true, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false);
        }, cancellationToken, options);
    }

    public Task<Db2QueryResult> QueryAsync(Db2Statement statement, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default) =>
        WithGateAsync(async (token, sourceReadToken) =>
        {
            EnsureStatement(statement);
            return await QueryPreparedLockedAsync(statement, parameters, token, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<Db2QueryResult> QueryAsync(Db2Statement statement, IReadOnlyList<object?> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return WithGateAsync(async (token, sourceReadToken) => { EnsureStatement(statement); return await QueryPreparedLockedAsync(statement, parameters, token, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false); }, cancellationToken, options);
    }

    public Task<Db2Statement> PrepareAsync(string sql, CancellationToken cancellationToken = default) =>
        WithGateAsync(async token =>
        {
            EnsureOpen();
            return await PrepareLockedAsync(sql, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<long> ExecuteAsync(string sql, CancellationToken cancellationToken = default) =>
        WithGateAsync(token => ExecuteLockedAsync(sql, token), cancellationToken);

    public Task<long> ExecuteAsync(string sql, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return WithGateAsync(token => ExecuteLockedAsync(sql, token), cancellationToken, options);
    }

    public Task<long> ExecuteAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default) =>
        WithGateAsync(async (token, sourceReadToken) =>
        {
            EnsureOpen();
            var statement = await PrepareLockedAsync(sql, token).ConfigureAwait(false);
            return await ExecutePreparedLockedAsync(statement, parameters, token, alreadyPrepared: true, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<long> ExecuteAsync(string sql, IReadOnlyList<object?> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return WithGateAsync(async (token, sourceReadToken) => { EnsureOpen(); var statement = await PrepareLockedAsync(sql, token).ConfigureAwait(false); return await ExecutePreparedLockedAsync(statement, parameters, token, alreadyPrepared: true, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false); }, cancellationToken, options);
    }

    /// <summary>Executes one Db2 administrative command through SYSPROC.ADMIN_CMD.</summary>
    public async Task<long> ExecuteAdminCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        var normalized = Db2AdminCommand.Normalize(command);
        return await ExecuteAsync(Db2AdminCommand.CallSql, [normalized], cancellationToken).ConfigureAwait(false);
    }

    public Task<long> ExecuteAsync(Db2Statement statement, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default) =>
        WithGateAsync(async (token, sourceReadToken) =>
        {
            EnsureStatement(statement);
            return await ExecutePreparedLockedAsync(statement, parameters, token, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<long> ExecuteAsync(Db2Statement statement, IReadOnlyList<object?> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return WithGateAsync(async (token, sourceReadToken) => { EnsureStatement(statement); return await ExecutePreparedLockedAsync(statement, parameters, token, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false); }, cancellationToken, options);
    }

    public Task<Db2ExecutionResult> ExecuteWithOutputsAsync(string sql, IReadOnlyList<Db2Parameter> parameters, CancellationToken cancellationToken = default) =>
        WithGateAsync(async (token, sourceReadToken) =>
        {
            EnsureOpen();
            var statement = await PrepareLockedAsync(sql, token).ConfigureAwait(false);
            return await ExecutePreparedWithOutputsLockedAsync(statement, parameters, token, alreadyPrepared: true, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<Db2ExecutionResult> ExecuteWithOutputsAsync(string sql, IReadOnlyList<Db2Parameter> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return WithGateAsync(async (token, sourceReadToken) => { EnsureOpen(); var statement = await PrepareLockedAsync(sql, token).ConfigureAwait(false); return await ExecutePreparedWithOutputsLockedAsync(statement, parameters, token, alreadyPrepared: true, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false); }, cancellationToken, options);
    }

    public Task<Db2ExecutionResult> ExecuteWithOutputsAsync(Db2Statement statement, IReadOnlyList<Db2Parameter> parameters, CancellationToken cancellationToken = default) =>
        WithGateAsync(async (token, sourceReadToken) =>
        {
            EnsureStatement(statement);
            return await ExecutePreparedWithOutputsLockedAsync(statement, parameters, token, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<Db2ExecutionResult> ExecuteWithOutputsAsync(Db2Statement statement, IReadOnlyList<Db2Parameter> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return WithGateAsync(async (token, sourceReadToken) => { EnsureStatement(statement); return await ExecutePreparedWithOutputsLockedAsync(statement, parameters, token, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false); }, cancellationToken, options);
    }

    public Task<Db2InsertResult> ExecuteInsertAsync(string sql, CancellationToken cancellationToken = default) =>
        WithGateAsync(async token =>
        {
            var affected = await ExecuteLockedAsync(sql, token).ConfigureAwait(false);
            return new Db2InsertResult(affected, await ReadIdentityLockedAsync(token).ConfigureAwait(false));
        }, cancellationToken);

    public Task<Db2InsertResult> ExecuteInsertAsync(string sql, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return WithGateAsync(async token => { var affected = await ExecuteLockedAsync(sql, token).ConfigureAwait(false); return new Db2InsertResult(affected, await ReadIdentityLockedAsync(token).ConfigureAwait(false)); }, cancellationToken, options);
    }

    public Task<Db2InsertResult> ExecuteInsertAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default) =>
        WithGateAsync(async (token, sourceReadToken) =>
        {
            EnsureOpen();
            var statement = await PrepareLockedAsync(sql, token).ConfigureAwait(false);
            var affected = await ExecutePreparedLockedAsync(statement, parameters, token, alreadyPrepared: true, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false);
            return new Db2InsertResult(affected, await ReadIdentityLockedAsync(token).ConfigureAwait(false));
        }, cancellationToken);

    public Task<Db2InsertResult> ExecuteInsertAsync(string sql, IReadOnlyList<object?> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return WithGateAsync(async (token, sourceReadToken) => { EnsureOpen(); var statement = await PrepareLockedAsync(sql, token).ConfigureAwait(false); var affected = await ExecutePreparedLockedAsync(statement, parameters, token, alreadyPrepared: true, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false); return new Db2InsertResult(affected, await ReadIdentityLockedAsync(token).ConfigureAwait(false)); }, cancellationToken, options);
    }

    public Task<Db2InsertResult> ExecuteInsertAsync(Db2Statement statement, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default) =>
        WithGateAsync(async (token, sourceReadToken) =>
        {
            EnsureStatement(statement);
            var affected = await ExecutePreparedLockedAsync(statement, parameters, token, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false);
            return new Db2InsertResult(affected, await ReadIdentityLockedAsync(token).ConfigureAwait(false));
        }, cancellationToken);

    public Task<Db2InsertResult> ExecuteInsertAsync(Db2Statement statement, IReadOnlyList<object?> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return WithGateAsync(async (token, sourceReadToken) => { EnsureStatement(statement); var affected = await ExecutePreparedLockedAsync(statement, parameters, token, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false); return new Db2InsertResult(affected, await ReadIdentityLockedAsync(token).ConfigureAwait(false)); }, cancellationToken, options);
    }

    /// <summary>Executes parameter sets sequentially and reports confirmed partial progress if a set fails.</summary>
    public async Task<long> ExecuteBatchAsync(string sql, IReadOnlyList<IReadOnlyList<object?>> parameterSets, CancellationToken cancellationToken = default)
        => await ExecuteBatchCoreAsync(sql, parameterSets, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Executes a batch under a command deadline while retaining confirmed partial-progress diagnostics.</summary>
    public Task<long> ExecuteBatchAsync(string sql, IReadOnlyList<IReadOnlyList<object?>> parameterSets, Db2CommandOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        return ExecuteBatchCoreAsync(sql, parameterSets, options, cancellationToken);
    }

    private async Task<long> ExecuteBatchCoreAsync(string sql, IReadOnlyList<IReadOnlyList<object?>> parameterSets, Db2CommandOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameterSets);
        if (parameterSets.Count == 0) return 0;
        long affected = 0;
        var completed = 0;
        int? inFlight = null;
        try
        {
            return await WithGateAsync(async (token, sourceReadToken) =>
            {
                EnsureOpen();
                Db2Statement statement;
                try { statement = await PrepareLockedAsync(sql, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { throw new Db2BatchExecutionException(null, 0, 0, error); }

                foreach (var parameters in parameterSets)
                {
                    inFlight = completed;
                    long currentAffected;
                    try { currentAffected = await ExecutePreparedLockedAsync(statement, parameters, token, alreadyPrepared: true, sourceReadCancellationToken: sourceReadToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { throw new Db2BatchExecutionException(completed, completed, affected, error); }
                    affected += currentAffected;
                    completed++;
                    inFlight = null;
                }
                return affected;
            }, cancellationToken, options).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new Db2BatchCancellationException(cancellationToken, completed, inFlight, affected);
        }
    }

    /// <summary>Updates non-empty client registers on this physical session.</summary>
    public Task SetClientInfoAsync(Db2ClientInfo info, CancellationToken cancellationToken = default) =>
        WithGateAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(info);
            EnsureOpen();
            var target = new Db2ClientInfo(
                string.IsNullOrEmpty(info.ApplicationName) ? _clientInfo.ApplicationName : info.ApplicationName,
                string.IsNullOrEmpty(info.WorkstationName) ? _clientInfo.WorkstationName : info.WorkstationName,
                string.IsNullOrEmpty(info.UserId) ? _clientInfo.UserId : info.UserId,
                string.IsNullOrEmpty(info.Accounting) ? _clientInfo.Accounting : info.Accounting,
                string.IsNullOrEmpty(info.CorrelationToken) ? _clientInfo.CorrelationToken : info.CorrelationToken);
            await ApplyClientInfoLockedAsync(target, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>Replaces all tracked client registers, including resetting values to empty.</summary>
    public Task ResetClientInfoAsync(Db2ClientInfo info, CancellationToken cancellationToken = default) =>
        WithGateAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(info);
            EnsureOpen();
            await ApplyClientInfoLockedAsync(info, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    internal Task ResetForPoolAsync() =>
        WithGateAsync(async token =>
        {
            EnsureOpen();
            if (_transactionActive || !AutoCommit)
            {
                // An unfinished transaction at pool-return time means the caller abandoned
                // its unit of work (Rust drops it and discards the lease). Attempt a
                // rollback to clean server state, then discard anyway so the pool cannot
                // reuse this session; the server rolls back on disconnect if rollback fails.
                _transactionGeneration++;
                Exception? rollbackError = null;
                try { await ControlLockedAsync(DdmCodec.PackRollback(), token).ConfigureAwait(false); }
                catch (Exception error) { rollbackError = error; }
                finally
                {
                    AutoCommit = true;
                    _transactionActive = false;
                    _closed = true;
                }
                throw new Db2AbandonedTransactionException(rollbackError);
            }
            if (Volatile.Read(ref _interruptUsed) != 0)
            {
                // The current DbConnection remains usable after SQLCODE -952, but
                // Db2 can still deliver cancellation state across later work on a
                // pooled logical connection. Do not lease an interrupted session
                // to another DbConnection; closing it gives Db2 a clean boundary.
                await DisposeAsync().ConfigureAwait(false);
                return true;
            }
            if (_poolBaselineCaptured && !_poolStateDirty)
            {
                Warning = null;
                return true;
            }
            try
            {
                if (_poolBaselineCaptured)
                {
                    var current = await ReadPoolSessionStateLockedAsync(token).ConfigureAwait(false);
                    if (!string.Equals(current.SessionUser, _baseUser, StringComparison.Ordinal))
                        await SwitchUserLockedAsync(_baseUser, token, force: true, allowFallback: false).ConfigureAwait(false);
                    else
                        _currentUser = _baseUser;

                    // A successful identity restore is required before caches are reseeded
                    // from the state observed on the server.
                    _clientInfo = current.ClientInfo with { CorrelationToken = _clientInfo.CorrelationToken };
                    await RestorePoolRegistersLockedAsync(current.Schema, current.Isolation, token).ConfigureAwait(false);
                    await ApplyClientInfoLockedAsync(_baseClientInfo, token).ConfigureAwait(false);
                }
            }
            catch
            {
                // A failed register restore leaves server-side session state uncertain.
                // The pool discards this transport instead of handing it to the next borrower.
                _closed = true;
                throw;
            }
            // Lease-specific warning subscriptions must not leak into the next borrower.
            _poolStateDirty = false;
            Warning = null;
            return true;
        }, CancellationToken.None);

    internal bool HasPoolBaseline => _poolBaselineCaptured;

    /// <summary>Captures the server's actual pooled-session baseline before the first caller.</summary>
    internal Task CapturePoolBaselineAsync(CancellationToken cancellationToken = default) =>
        WithGateAsync(async token =>
        {
            EnsureOpen();
            if (_poolBaselineCaptured) return true;
            try
            {
                var baseline = await ReadPoolSessionStateLockedAsync(token).ConfigureAwait(false);
                _baseSchema = baseline.Schema;
                _baseIsolation = baseline.Isolation;
                // The server is the source of truth for client registers and identity.
                _clientInfo = baseline.ClientInfo with { CorrelationToken = _clientInfo.CorrelationToken };
                _baseClientInfo = _clientInfo;
                _baseUser = baseline.SessionUser;
                _currentUser = _baseUser;
                _preparedSql = null;
                _poolBaselineCaptured = true;
                _poolStateDirty = false;
                return true;
            }
            catch
            {
                _closed = true;
                throw;
            }
        }, cancellationToken);

    private sealed record PoolSessionState(string SessionUser, string Schema, string Isolation, Db2ClientInfo ClientInfo);

    private async Task<PoolSessionState> ReadPoolSessionStateLockedAsync(CancellationToken cancellationToken)
    {
        const string probe = "VALUES (" +
            "CAST(RTRIM(SESSION_USER) AS VARCHAR(255)), " +
            "CAST(CURRENT SCHEMA AS VARCHAR(255)), " +
            "CAST(CURRENT ISOLATION AS VARCHAR(255)), " +
            "CAST(COALESCE(CURRENT CLIENT_APPLNAME, '') AS VARCHAR(255)), " +
            "CAST(COALESCE(CURRENT CLIENT_WRKSTNNAME, '') AS VARCHAR(255)), " +
            "CAST(COALESCE(CURRENT CLIENT_USERID, '') AS VARCHAR(255)), " +
            "CAST(COALESCE(CURRENT CLIENT_ACCTNG, '') AS VARCHAR(255)))";
        var rows = (await QueryLockedAsync(probe, cancellationToken).ConfigureAwait(false)).Rows;
        if (rows.Count != 1 || rows[0].Length != 7 || rows[0].Any(value => value is not string))
            throw new InvalidDataException("DB2 returned an invalid pooled-session state probe row.");
        var values = rows[0].Cast<string>().ToArray();
        var sessionUser = values[0].Trim();
        var schema = values[1].Trim();
        var isolation = values[2].Trim();
        if (string.IsNullOrWhiteSpace(sessionUser) || sessionUser.Length > 128)
            throw new InvalidDataException("DB2 returned an invalid SESSION_USER register.");
        if (string.IsNullOrWhiteSpace(schema) || schema.Length > 128)
            throw new InvalidDataException("DB2 returned an invalid CURRENT SCHEMA register.");
        if (isolation is not ("" or "UR" or "CS" or "RS" or "RR"))
            throw new InvalidDataException("DB2 returned an invalid CURRENT ISOLATION register.");
        return new PoolSessionState(sessionUser, schema, isolation,
            new Db2ClientInfo(values[3], values[4], values[5], values[6]));
    }

    private async Task RestorePoolRegistersLockedAsync(string currentSchema, string currentIsolation, CancellationToken cancellationToken)
    {
        if (currentSchema != _baseSchema)
        {
            await ExecuteSqlSetLockedAsync($"SET CURRENT SCHEMA {QuoteIdentifier(_baseSchema ?? "")}", cancellationToken).ConfigureAwait(false);
            _preparedSql = null;
        }
        if (currentIsolation != _baseIsolation)
        {
            await ExecuteSqlSetLockedAsync($"SET CURRENT ISOLATION {(_baseIsolation!.Length == 0 ? "RESET" : _baseIsolation)}", cancellationToken).ConfigureAwait(false);
            _preparedSql = null;
        }
        if (!AutoCommit) AutoCommit = true;
    }

    private static string QuoteIdentifier(string value)
    {
        var target = value.Trim();
        if (target.Length == 0 || target.Length > 128) throw new ArgumentException("Invalid DB2 identifier.", nameof(value));
        return $"\"{target.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    internal void MarkTransactionAbandoned()
    {
        _closed = true;
    }

    public Task PingAsync(CancellationToken cancellationToken = default) => ControlAsync(DdmCodec.PackCommit(), cancellationToken);
    public Task CommitAsync(CancellationToken cancellationToken = default) => ControlAsync(DdmCodec.PackCommit(), cancellationToken);
    public Task RollbackAsync(CancellationToken cancellationToken = default) => ControlAsync(DdmCodec.PackRollback(), cancellationToken);

    public Task<Db2Transaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        WithGateAsync(token =>
        {
            EnsureOpen();
            if (_transactionActive || !AutoCommit) throw new InvalidOperationException("A DB2 transaction is already active.");
            _transactionActive = true;
            AutoCommit = false;
            return Task.FromResult(new Db2Transaction(this, ++_transactionGeneration));
        }, cancellationToken);

    /// <summary>Applies a scoped LUW isolation override and restores CURRENT ISOLATION after completion.</summary>
    public Task<Db2Transaction> BeginTransactionAsync(Db2IsolationLevel isolation, CancellationToken cancellationToken = default)
    {
        var code = isolation switch
        {
            Db2IsolationLevel.Unspecified => null,
            Db2IsolationLevel.UncommittedRead => "UR",
            Db2IsolationLevel.CursorStability => "CS",
            Db2IsolationLevel.ReadStability => "RS",
            Db2IsolationLevel.RepeatableRead => "RR",
            _ => throw new ArgumentOutOfRangeException(nameof(isolation)),
        };
        if (code is null) return BeginTransactionAsync(cancellationToken);
        return WithGateAsync(async token =>
        {
            EnsureOpen();
            if (_transactionActive || !AutoCommit) throw new InvalidOperationException("A DB2 transaction is already active.");
            var baselineRows = (await QueryLockedAsync("VALUES CURRENT ISOLATION", token).ConfigureAwait(false)).Rows;
            var baseline = baselineRows.Count == 1 && baselineRows[0].Length == 1 ? (baselineRows[0][0] as string)?.Trim() : null;
            if (baseline is not ("" or "UR" or "CS" or "RS" or "RR")) throw new InvalidDataException("DB2 returned an invalid CURRENT ISOLATION register.");
            try
            {
                // SET ISOLATION is not transaction controlled; capture it before overriding.
                // https://www.ibm.com/docs/en/db2/11.5.x?topic=statements-set-current-isolation
                await ExecuteSqlSetLockedAsync($"SET CURRENT ISOLATION {code}", token).ConfigureAwait(false);
                _preparedSql = null;
                _transactionIsolationBaseline = baseline;
                _transactionActive = true;
                AutoCommit = false;
                return new Db2Transaction(this, ++_transactionGeneration);
            }
            catch { _closed = true; throw; }
        }, cancellationToken);
    }

    internal bool IsTransactionActive(long generation) => _transactionActive && _transactionGeneration == generation;

    internal Task CompleteTransactionAsync(bool commit, long generation, CancellationToken cancellationToken) =>
        WithGateAsync(async token =>
        {
            EnsureOpen();
            if (!IsTransactionActive(generation)) throw new InvalidOperationException("DB2 transaction is no longer active.");
            try
            {
                await ControlLockedAsync(commit ? DdmCodec.PackCommit() : DdmCodec.PackRollback(), token).ConfigureAwait(false);
                if (_transactionIsolationBaseline is { } baseline)
                {
                    await ExecuteSqlSetLockedAsync($"SET CURRENT ISOLATION {(baseline.Length == 0 ? "RESET" : baseline)}", token).ConfigureAwait(false);
                    _preparedSql = null;
                }
                return true;
            }
            catch
            {
                // The server may have processed neither, part, or all of a failed
                // transaction-control exchange. Do not let a pool reuse uncertain state.
                _closed = true;
                throw;
            }
            finally
            {
                AutoCommit = true;
                _transactionActive = false;
                _transactionIsolationBaseline = null;
                _transactionGeneration++;
            }
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _closed = true;
        try { await _stream.DisposeAsync().ConfigureAwait(false); }
        // Keep the managed gate available: operations started after disposal should
        // acquire it and fail with Db2ConnectionClosedException, not ObjectDisposedException.
        finally { _readBuffer?.Dispose(); _writeBuffer.Dispose(); _client.Dispose(); }
    }

    private async Task<Db2QueryResult> QueryLockedAsync(string sql, CancellationToken cancellationToken)
    {
        EnsureOpen();
        if (IsCallStatement(sql))
        {
            var call = await PrepareLockedAsync(sql, cancellationToken).ConfigureAwait(false);
            return (await QueryProcedurePreparedLockedAsync(call, Array.Empty<object?>(), cancellationToken).ConfigureAwait(false)).QueryResult;
        }
        _correlationId = 1;
        _preparedSql = null;
        await SendAsync(DdmCodec.PackPrepareSqlStatement(PackageId, ConsistencyToken, SectionNumber, _database), true, false, cancellationToken).ConfigureAwait(false);
        await SendAsync(DdmCodec.PackSqlStatement(sql), false, false, cancellationToken).ConfigureAwait(false);
        _preparedSql = sql;
        await SendAsync(DdmCodec.PackOpenQuery(PackageId, ConsistencyToken, SectionNumber, _database, _blockSize), false, true, cancellationToken).ConfigureAwait(false);
        return await FetchQueryAsync(await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ExecuteLockedAsync(string sql, CancellationToken cancellationToken)
    {
        EnsureOpen();
        if (IsCallStatement(sql))
        {
            var statement = await PrepareLockedAsync(sql, cancellationToken).ConfigureAwait(false);
            var procedure = await QueryProcedurePreparedLockedAsync(statement, Array.Empty<object?>(), cancellationToken).ConfigureAwait(false);
            return procedure.RowsAffected;
        }
        _correlationId = 1;
        await SendAsync(DdmCodec.PackExecSqlImmediate(PackageId, ConsistencyToken, SectionNumber, _database), true, false, cancellationToken).ConfigureAwait(false);
        await SendAsync(DdmCodec.PackSqlStatement(sql), false, !AutoCommit, cancellationToken).ConfigureAwait(false);
        if (AutoCommit) await SendAsync(DdmCodec.PackCommit(), false, true, cancellationToken).ConfigureAwait(false);
        long affected = 0;
        foreach (var reply in await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false))
        {
            if ((CodePoint)reply.CodePoint == CodePoint.SQLCARD)
            {
                var card = ParseSqlCardAndReportWarning(reply.Payload);
                if (card.SqlCode < 0) throw SqlError(card);
                affected += card.RowsAffected;
            }
            else if ((CodePoint)reply.CodePoint == CodePoint.SQLERRRM) throw ServerError(reply.Payload);
        }
        return affected;
    }

    private async Task<long> ReadIdentityLockedAsync(CancellationToken cancellationToken)
    {
        var result = await QueryLockedAsync("SELECT IDENTITY_VAL_LOCAL() AS LAST_ID FROM SYSIBM.SYSDUMMY1", cancellationToken).ConfigureAwait(false);
        if (result.Rows.Count == 0 || result.Rows[0].Length == 0 || result.Rows[0][0] is null)
            throw new InvalidOperationException("DB2 did not return an identity value.");
        var value = result.Rows[0][0];
        if (value is string text)
        {
            var integral = text.Trim().Split('.', 2)[0];
            return long.Parse(integral, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
        if (value is byte[] bytes)
        {
            var textValue = Encoding.UTF8.GetString(bytes).Trim().Split('.', 2)[0];
            return long.Parse(textValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task<Db2Statement> PrepareLockedAsync(string sql, CancellationToken cancellationToken)
    {
        _correlationId = 1;
        _preparedSql = null;
        await SendAsync(DdmCodec.PackPrepareSqlStatement(PackageId, ConsistencyToken, SectionNumber, _database), true, false, cancellationToken).ConfigureAwait(false);
        await SendAsync(DdmCodec.PackSqlStatement(sql), false, false, cancellationToken).ConfigureAwait(false);
        await SendAsync(DdmCodec.PackDescribeSqlStatement(PackageId, ConsistencyToken, SectionNumber, _database), false, true, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Db2ColumnMetadata> outputColumns = Array.Empty<Db2ColumnMetadata>();
        IReadOnlyList<Db2ColumnMetadata> parameterColumns = Array.Empty<Db2ColumnMetadata>();
        foreach (var reply in await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false))
        {
            switch ((CodePoint)reply.CodePoint)
            {
                case CodePoint.SQLDARD:
                    var columns = DdmCodec.ParseSqlDard(reply.Payload);
                    if (reply.Payload.Length > 0 && reply.Payload[0] == 0xff) parameterColumns = columns;
                    else outputColumns = columns;
                    break;
                case CodePoint.SQLCARD:
                    var card = ParseSqlCardAndReportWarning(reply.Payload);
                    if (card.SqlCode < 0) throw SqlError(card);
                    break;
                case CodePoint.SQLERRRM: throw ServerError(reply.Payload);
            }
        }
        _preparedSql = sql;
        return new Db2Statement(this, sql, outputColumns, parameterColumns);
    }

    private async Task<long> ExecutePreparedLockedAsync(Db2Statement statement, IReadOnlyList<object?> parameters, CancellationToken cancellationToken, bool alreadyPrepared = false, IReadOnlyList<Db2ParameterDirection>? outputDirections = null, object?[]? outputParameters = null, CancellationToken? sourceReadCancellationToken = null)
    {
        EnsureStatement(statement);
        if (!alreadyPrepared && _preparedSql != statement.Sql) statement = await PrepareLockedAsync(statement.Sql, cancellationToken).ConfigureAwait(false);
        if (parameters.Count != statement.ParameterColumns.Count)
            throw new ArgumentException($"DB2 expected {statement.ParameterColumns.Count} parameters, got {parameters.Count}.", nameof(parameters));
        if (IsCallStatement(statement.Sql))
        {
            var procedure = await QueryProcedurePreparedLockedAsync(statement, parameters, cancellationToken, outputDirections, sourceReadCancellationToken).ConfigureAwait(false);
            if (outputParameters is not null)
                Array.Copy(procedure.OutputParameters.ToArray(), outputParameters, Math.Min(procedure.OutputParameters.Count, outputParameters.Length));
            return procedure.RowsAffected;
        }
        if (statement.ParameterColumns.Count == 0)
            return await ExecuteLockedAsync(statement.Sql, cancellationToken).ConfigureAwait(false);
        var encoded = SqlDtaEncoder.BuildWithExternalData(statement.ParameterColumns, parameters, statement.ParameterDescriptorCache ??= new());
        _correlationId = 1;
        await SendAsync(DdmCodec.PackExecuteSqlStatement(PackageId, ConsistencyToken, SectionNumber, _database), true, false, cancellationToken).ConfigureAwait(false);
        await SendAsync(encoded.SqlDta, encoded.ExternalData.Count > 0, encoded.ExternalData.Count == 0 && !AutoCommit, cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < encoded.ExternalData.Count; index++)
            await SendExternalDataAsync(encoded.ExternalData[index], index < encoded.ExternalData.Count - 1, index == encoded.ExternalData.Count - 1 && !AutoCommit, cancellationToken, sourceReadCancellationToken ?? cancellationToken).ConfigureAwait(false);
        if (AutoCommit) await SendAsync(DdmCodec.PackCommit(), false, true, cancellationToken, 1).ConfigureAwait(false);
        var replies = (await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false)).ToList();
        if (replies.Count > 0 && replies.All(reply => (CodePoint)reply.CodePoint == CodePoint.SQLDARD))
            replies.AddRange(await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false));
        long affected = 0;
        var outputDataReceived = false;
        foreach (var reply in replies)
        {
            if ((CodePoint)reply.CodePoint == CodePoint.SQLCARD)
            {
                var card = ParseSqlCardAndReportWarning(reply.Payload);
                if (card.SqlCode < 0) throw SqlError(card);
                affected += card.RowsAffected;
            }
            else if ((CodePoint)reply.CodePoint == CodePoint.SQLDTARD && outputDirections is not null && outputParameters is not null)
            {
                ApplyOutputParameterReply(reply.Payload, outputDirections, outputParameters, []);
                outputDataReceived = true;
            }
            else if ((CodePoint)reply.CodePoint == CodePoint.SQLERRRM) throw ServerError(reply.Payload);
        }
        if (outputDirections is not null && outputParameters is not null)
            EnsureOutputParameterDataReturned(outputDirections, outputDataReceived);
        return affected;
    }

    private async Task<Db2ExecutionResult> ExecutePreparedWithOutputsLockedAsync(Db2Statement statement, IReadOnlyList<Db2Parameter> parameters, CancellationToken cancellationToken, bool alreadyPrepared = false, CancellationToken? sourceReadCancellationToken = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!alreadyPrepared && _preparedSql != statement.Sql) statement = await PrepareLockedAsync(statement.Sql, cancellationToken).ConfigureAwait(false);
        if (parameters.Count != statement.ParameterColumns.Count)
            throw new ArgumentException($"DB2 expected {statement.ParameterColumns.Count} parameters, got {parameters.Count}.", nameof(parameters));
        var directions = parameters.Select(parameter => parameter.Direction).ToArray();
        if (!directions.Any(direction => direction != Db2ParameterDirection.Input))
            throw new ArgumentException("At least one parameter must have Output or InputOutput direction.", nameof(parameters));
        var values = parameters.Select(parameter => parameter.Value).ToArray();
        if (IsCallStatement(statement.Sql))
        {
            var procedure = await QueryProcedurePreparedLockedAsync(statement, values, cancellationToken, directions, sourceReadCancellationToken).ConfigureAwait(false);
            return new Db2ExecutionResult(procedure.RowsAffected, procedure.OutputParameters) { ResultSets = procedure.QueryResult.ResultSets };
        }
        var outputs = new object?[parameters.Count];
        var affected = await ExecutePreparedLockedAsync(statement, values, cancellationToken, alreadyPrepared: true, outputDirections: directions, outputParameters: outputs, sourceReadCancellationToken: sourceReadCancellationToken).ConfigureAwait(false);
        return new Db2ExecutionResult(affected, outputs);
    }

    private async Task<Db2QueryResult> QueryPreparedLockedAsync(Db2Statement statement, IReadOnlyList<object?> parameters, CancellationToken cancellationToken, bool alreadyPrepared = false, CancellationToken? sourceReadCancellationToken = null)
    {
        EnsureStatement(statement);
        if (!alreadyPrepared && _preparedSql != statement.Sql) statement = await PrepareLockedAsync(statement.Sql, cancellationToken).ConfigureAwait(false);
        if (parameters.Count != statement.ParameterColumns.Count)
            throw new ArgumentException($"DB2 expected {statement.ParameterColumns.Count} parameters, got {parameters.Count}.", nameof(parameters));
        if (IsCallStatement(statement.Sql))
            return (await QueryProcedurePreparedLockedAsync(statement, parameters, cancellationToken, sourceReadCancellationToken: sourceReadCancellationToken).ConfigureAwait(false)).QueryResult;
        if (statement.ParameterColumns.Count == 0)
            return await QueryLockedAsync(statement.Sql, cancellationToken).ConfigureAwait(false);
        var encoded = SqlDtaEncoder.BuildWithExternalData(statement.ParameterColumns, parameters, statement.ParameterDescriptorCache ??= new());
        _correlationId = 1;
        await SendAsync(DdmCodec.PackOpenQueryWithParams(PackageId, ConsistencyToken, SectionNumber, _database, _blockSize), true, false, cancellationToken).ConfigureAwait(false);
        await SendAsync(encoded.SqlDta, encoded.ExternalData.Count > 0, encoded.ExternalData.Count == 0, cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < encoded.ExternalData.Count; index++)
            await SendExternalDataAsync(encoded.ExternalData[index], index < encoded.ExternalData.Count - 1, index == encoded.ExternalData.Count - 1, cancellationToken, sourceReadCancellationToken ?? cancellationToken).ConfigureAwait(false);
        var result = await FetchQueryAsync(await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false), cancellationToken, statement.OutputColumns).ConfigureAwait(false);
        if (AutoCommit)
        {
            _correlationId = 1;
            await SendAsync(DdmCodec.PackCommit(), false, true, cancellationToken).ConfigureAwait(false);
            foreach (var reply in await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false))
            {
                if ((CodePoint)reply.CodePoint == CodePoint.SQLCARD)
                {
                    var card = ParseSqlCardAndReportWarning(reply.Payload);
                    if (card.SqlCode < 0) throw SqlError(card);
                }
                else if ((CodePoint)reply.CodePoint == CodePoint.SQLERRRM) throw ServerError(reply.Payload);
            }
        }
        return result;
    }

    private sealed class ProcedureResultSetState(ResultSetPackageSection packageSection)
    {
        internal ResultSetPackageSection PackageSection { get; } = packageSection;
        internal IReadOnlyList<Db2ColumnMetadata> Columns = Array.Empty<Db2ColumnMetadata>();
        internal FieldDescriptor[] Fields = [];
        internal readonly Db2RowAccumulator RowData = new();
        internal readonly List<byte[]> ExternalData = [];
        internal bool Ended;
        internal bool Closed;
        internal bool SeenQueryData;
        internal ulong QueryId;
        internal ushort NextCorrelation = 1;
    }

    private sealed record ProcedureCallResult(Db2QueryResult QueryResult, IReadOnlyList<object?> OutputParameters, long RowsAffected);

    private async Task<ProcedureCallResult> QueryProcedurePreparedLockedAsync(Db2Statement statement, IReadOnlyList<object?> parameters, CancellationToken cancellationToken, IReadOnlyList<Db2ParameterDirection>? outputDirections = null, CancellationToken? sourceReadCancellationToken = null)
    {
        EnsureStatement(statement);
        if (parameters.Count != statement.ParameterColumns.Count)
            throw new ArgumentException($"DB2 expected {statement.ParameterColumns.Count} parameters, got {parameters.Count}.", nameof(parameters));
        if (outputDirections is not null && outputDirections.Count != parameters.Count)
            throw new ArgumentException("Output parameter directions must match the prepared statement parameter count.", nameof(outputDirections));
        var outputParameters = new object?[parameters.Count];

        var encoded = parameters.Count == 0 ? null : SqlDtaEncoder.BuildWithExternalData(statement.ParameterColumns, parameters, statement.ParameterDescriptorCache ??= new());
        _correlationId = 1;
        await SendAsync(DdmCodec.PackExecuteSqlStatement(
            PackageId, ConsistencyToken, SectionNumber, _database,
            maximumResultSets: ushort.MaxValue, queryBlockSize: _blockSize,
            queryRowsetSize: null, outputExpected: outputDirections?.Any(direction => direction != Db2ParameterDirection.Input) ?? false), encoded is not null, encoded is null, cancellationToken).ConfigureAwait(false);
        if (encoded is not null)
        {
            await SendAsync(encoded.SqlDta, encoded.ExternalData.Count > 0, encoded.ExternalData.Count == 0, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < encoded.ExternalData.Count; index++)
                await SendExternalDataAsync(encoded.ExternalData[index], index < encoded.ExternalData.Count - 1, index == encoded.ExternalData.Count - 1, cancellationToken, sourceReadCancellationToken ?? cancellationToken).ConfigureAwait(false);
        }

        var sections = Array.Empty<ResultSetPackageSection>();
        var declaredCount = -1;
        long rowsAffected = 0;
        byte[]? outputSqlDtaReply = null;
        var outputExternalData = new List<byte[]>();
        var states = new List<ProcedureResultSetState>();
        ProcedureResultSetState? current = null;
        IReadOnlyList<Db2ColumnMetadata>? pendingColumns = null;
        var seenInitialQrydta = false;
        try
        {
            var initialReplies = await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false);
            foreach (var reply in initialReplies)
            {
                switch ((CodePoint)reply.CodePoint)
                {
                    case CodePoint.RSLSETRM:
                        sections = DdmCodec.ParseResultSetSections(reply.Payload);
                        break;
                    case CodePoint.SQLRSLRD:
                        declaredCount = DdmCodec.ParseSqlResultSetCount(reply.Payload);
                        break;
                    case CodePoint.SQLDTARD when outputDirections is not null:
                        outputSqlDtaReply = reply.Payload;
                        break;
                    case CodePoint.OPNQRYRM:
                        if (states.Count >= sections.Length)
                            throw new InvalidDataException("DB2 returned more procedure cursors than RSLSETRM declared.");
                        current = new ProcedureResultSetState(sections[states.Count]) { NextCorrelation = reply.Header.CorrelationId };
                        if (pendingColumns is not null)
                        {
                            current.Columns = pendingColumns;
                            pendingColumns = null;
                        }
                        var openValues = DdmCodec.ParseParameters(reply.Payload);
                        if (openValues.TryGetValue((ushort)CodePoint.QRYINSID, out var id) && id.Length >= 8)
                            current.QueryId = BinaryPrimitives.ReadUInt64BigEndian(id);
                        states.Add(current);
                        break;
                    case CodePoint.SQLCINRD:
                        if (current is null) throw new InvalidDataException("DB2 returned SQLCINRD without an open procedure cursor.");
                        current.Columns = DdmCodec.MergeCursorColumnNames(
                            DdmCodec.ParseSqlCursorInfo(reply.Payload, _sqlamLevel),
                            current.Columns);
                        break;
                    case CodePoint.SQLDARD:
                        var describedColumns = DdmCodec.ParseSqlDard(reply.Payload, littleEndian: true, synthesizeUnnamedNames: false);
                        if (describedColumns.Length > 0)
                        {
                            if (current is null) pendingColumns = describedColumns;
                            else current.Columns = DdmCodec.MergeCursorColumnNames(current.Columns, describedColumns);
                        }
                        break;
                    case CodePoint.QRYDSC:
                        if (current is null) throw new InvalidDataException("DB2 returned QRYDSC without an open procedure cursor.");
                        current.Fields = DdmCodec.ParseQueryDescriptor(reply.Payload);
                        break;
                    case CodePoint.QRYDTA:
                        if (current is null) throw new InvalidDataException("DB2 returned QRYDTA without an open procedure cursor.");
                        current.RowData.Write(reply.Payload);
                        seenInitialQrydta = true;
                        break;
                    case CodePoint.EXTDTA:
                        if (current is null)
                        {
                            if (outputDirections is null)
                                throw new InvalidDataException("DB2 returned EXTDTA without an open procedure cursor or output parameters.");
                            outputExternalData.Add(reply.Payload);
                        }
                        else if (outputDirections is not null && !seenInitialQrydta)
                        {
                            // Db2 11.5.8 sends output-parameter EXTDTA after OPNQRYRM/SQLCINRD/QRYDSC
                            // but before any cursor QRYDTA. Route pre-data EXTDTA to outputs; once cursor
                            // row data starts, EXTDTA belongs to that cursor.
                            outputExternalData.Add(reply.Payload);
                        }
                        else current.ExternalData.Add(reply.Payload);
                        break;
                    case CodePoint.ENDQRYRM:
                        if (current is not null) current.Ended = true;
                        break;
                    case CodePoint.SQLCARD:
                        var card = ParseSqlCardAndReportWarning(reply.Payload);
                        if (card.SqlCode < 0 || card.SqlCode == 464) throw SqlError(card);
                        rowsAffected += card.RowsAffected;
                        if (card.SqlCode == 100 && current is not null) current.Ended = true;
                        break;
                    case CodePoint.SQLERRRM:
                        throw ServerError(reply.Payload);
                }
            }

            if (outputDirections is not null)
            {
                if (outputSqlDtaReply is not null)
                    ApplyOutputParameterReply(outputSqlDtaReply, outputDirections, outputParameters, outputExternalData);
                else if (outputExternalData.Count != 0)
                    throw new InvalidDataException("DB2 returned output-parameter EXTDTA without SQLDTARD metadata.");
                else
                    EnsureOutputParameterDataReturned(outputDirections, dataReturned: false);
            }

            if (sections.Length == 0)
            {
                if (states.Count != 0) throw new InvalidDataException("DB2 returned procedure cursors without RSLSETRM.");
                if (AutoCommit) await CommitProcedureCallLockedAsync(cancellationToken).ConfigureAwait(false);
                return new ProcedureCallResult(
                    new Db2QueryResult(Array.Empty<Db2ColumnMetadata>(), Array.Empty<object?[]>()) { ResultSets = Array.Empty<Db2ResultSet>() },
                    outputParameters,
                    rowsAffected);
            }
            if (declaredCount != sections.Length || states.Count != sections.Length)
                throw new InvalidDataException($"DB2 declared {declaredCount} procedure result sets, returned {sections.Length} package sections and opened {states.Count} cursors.");

            var resultSets = new List<Db2ResultSet>(states.Count);
            foreach (var state in states)
            {
                if (state.QueryId == 0)
                    throw new InvalidDataException("DB2 left a procedure cursor open without its query instance ID.");
                var fetchCount = 0;
                while (!state.Ended)
                {
                    if (++fetchCount > 65_535)
                        throw new InvalidDataException("DB2 procedure cursor exceeded the maximum supported number of fetch blocks.");
                    await SendAsync(DdmCodec.PackContinueProcedureResultSet(state.PackageSection, _blockSize, state.QueryId), false, true, cancellationToken, state.NextCorrelation).ConfigureAwait(false);
                    var fetchReplies = await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false);
                    ConsumeProcedureCursorReplies(fetchReplies, state);
                    if (state.Ended) break;

                    var lastData = fetchReplies.LastOrDefault(reply => (CodePoint)reply.CodePoint == CodePoint.QRYDTA);
                    if (lastData is null)
                        throw new InvalidDataException("DB2 CNTQRY returned no query data or end-of-query reply for a procedure cursor.");
                    // A full DRDA block leaves the cursor open for another CNTQRY.
                    // A short block is the final row block; the cursor itself is
                    // still explicitly closed below before advancing to the next set.
                    if (lastData.Payload.Length < MaximumProcedureQueryDataPayload(_blockSize)) break;
                }

                // Stored-procedure cursors have different completion behavior
                // from an ordinary OPNQRY stream: on Db2 LUW the CLI driver
                // closes the current query explicitly before asking for the
                // next result set rather than waiting for ENDQRYRM.
                await SendAsync(DdmCodec.PackCloseProcedureResultSet(state.PackageSection, state.QueryId), false, true, cancellationToken, state.NextCorrelation).ConfigureAwait(false);
                var closeReplies = await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false);
                ConsumeProcedureCursorReplies(closeReplies, state);
                state.Ended = true;

                var rows = DecodeRows(state.Fields, state.RowData.Data).ToList();
                if (state.Fields.Length > 0 && state.Columns.Count != state.Fields.Length)
                    throw new InvalidDataException($"DB2 returned {state.Fields.Length} procedure result columns in QRYDSC but {state.Columns.Count} in SQLCINRD metadata.");
                var externalUsed = StitchExtdta(state.Fields, rows, state.ExternalData);
                if (externalUsed != state.ExternalData.Count || HasUnresolvedExternalLocator(state.Fields, rows))
                    throw new InvalidDataException("DB2 returned procedure-cursor EXTDTA values that do not match the QRYDSC LOB descriptors.");
                resultSets.Add(new Db2ResultSet(state.Columns, rows));
            }

            if (AutoCommit) await CommitProcedureCallLockedAsync(cancellationToken).ConfigureAwait(false);
            var first = resultSets[0];
            return new ProcedureCallResult(new Db2QueryResult(first.Columns, first.Rows) { ResultSets = resultSets }, outputParameters, rowsAffected);
        }
        catch
        {
            if (states.Any(state => !state.Ended)) await InvalidateAfterExternalDataFailureAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            foreach (var state in states) state.RowData.Dispose();
        }
    }

    internal static void ApplyOutputParameterReply(
        ReadOnlySpan<byte> payload,
        IReadOnlyList<Db2ParameterDirection> directions,
        object?[] outputParameters,
        IReadOnlyList<byte[]> externalData)
    {
        var (fields, values) = DdmCodec.ParseSqlDtaReply(payload);
        if (fields.Length == 0 || values.Length == 0)
        {
            if (externalData.Count != 0)
                throw new InvalidDataException("DB2 returned output-parameter EXTDTA without decodable SQLDTARD values.");
            EnsureOutputParameterDataReturned(directions, dataReturned: false);
            return;
        }
        var rowData = values[0] == 0xff ? values : [0xff, 0x00, .. values];
        var rows = DecodeRowsIncremental(fields, rowData).Rows;
        if (rows.Count == 0)
        {
            if (externalData.Count != 0)
                throw new InvalidDataException("DB2 returned output-parameter EXTDTA without SQLDTARD rows.");
            EnsureOutputParameterDataReturned(directions, dataReturned: false);
            return;
        }
        var externalUsed = StitchExtdta(fields, rows, externalData, externalValuesHaveNullIndicator: true);
        if (externalUsed != externalData.Count || HasUnresolvedExternalLocator(fields, rows))
            throw new InvalidDataException("DB2 returned output-parameter EXTDTA values that do not match the SQLDTARD LOB descriptors.");
        var decoded = rows[0];
        // A short SQLDTARD row would leave a requested output position at its
        // placeholder value without any error. Go skips those positions silently;
        // reject the reply instead of reporting a missing output as null.
        var lastRequested = -1;
        for (var index = 0; index < directions.Count; index++)
            if (directions[index] is Db2ParameterDirection.Output or Db2ParameterDirection.InputOutput)
                lastRequested = index;
        if (lastRequested >= decoded.Length)
            throw new InvalidDataException($"DB2 returned output-parameter SQLDTARD values for {decoded.Length} parameters, but position {lastRequested} was requested as an output.");
        for (var index = 0; index < outputParameters.Length && index < decoded.Length; index++)
            if (directions[index] is Db2ParameterDirection.Output or Db2ParameterDirection.InputOutput)
                outputParameters[index] = decoded[index];
    }

    internal static void EnsureOutputParameterDataReturned(
        IReadOnlyList<Db2ParameterDirection> directions,
        bool dataReturned)
    {
        if (dataReturned) return;
        for (var index = directions.Count - 1; index >= 0; index--)
        {
            if (directions[index] is not (Db2ParameterDirection.Output or Db2ParameterDirection.InputOutput))
                continue;
            throw new InvalidDataException(
                $"DB2 returned no SQLDTARD values for requested output parameter at position {index}.");
        }
    }

    private static uint MaximumProcedureQueryDataPayload(uint blockSize)
    {
        // The block limit includes the DSS header and the outer QRYDTA DDM
        // header: six bytes plus four for a short DDM header, or eight for an
        // extended DDM header. DB2 11.5.8's 65,535-byte block held 65,521
        // bytes of QRYDTA payload.
        var headers = blockSize > 0x7fff ? 14U : 10U;
        return blockSize > headers ? blockSize - headers : 0;
    }

    private void ConsumeProcedureCursorReplies(IReadOnlyList<DssPacket> replies, ProcedureResultSetState state)
    {
        foreach (var reply in replies)
        {
            switch ((CodePoint)reply.CodePoint)
            {
                case CodePoint.SQLDARD:
                    var columns = DdmCodec.ParseSqlDard(reply.Payload, littleEndian: true, synthesizeUnnamedNames: false);
                    if (columns.Length > 0)
                        state.Columns = DdmCodec.MergeCursorColumnNames(state.Columns, columns);
                    break;
                case CodePoint.QRYDTA: state.RowData.Write(reply.Payload); break;
                case CodePoint.EXTDTA: state.ExternalData.Add(reply.Payload); break;
                case CodePoint.ENDQRYRM: state.Ended = true; break;
                case CodePoint.OPNQRYRM:
                    state.NextCorrelation = reply.Header.CorrelationId;
                    if (DdmCodec.TryGetParameter(reply.Payload, (ushort)CodePoint.QRYINSID, out var id) && id.Length >= 8)
                        state.QueryId = BinaryPrimitives.ReadUInt64BigEndian(id);
                    break;
                case CodePoint.SQLCARD:
                    var card = ParseSqlCardAndReportWarning(reply.Payload);
                    if (card.SqlCode < 0 || card.SqlCode == 464) throw SqlError(card);
                    if (card.SqlCode == 100) state.Ended = true;
                    break;
                case CodePoint.SQLERRRM: throw ServerError(reply.Payload);
            }
        }
    }

    private async Task CommitProcedureCallLockedAsync(CancellationToken cancellationToken)
    {
        _correlationId = 1;
        await SendAsync(DdmCodec.PackCommit(), false, true, cancellationToken).ConfigureAwait(false);
        foreach (var reply in await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false))
            ThrowIfServerError(reply);
    }

    private static bool IsCallStatement(string sql)
    {
        var position = sql.Length > 0 && sql[0] == '\ufeff' ? 1 : 0;
        while (true)
        {
            while (position < sql.Length && char.IsWhiteSpace(sql[position])) position++;
            if (position + 1 >= sql.Length) return false;
            if (sql[position] == '/' && sql[position + 1] == '*')
            {
                var end = sql.IndexOf("*/", position + 2, StringComparison.Ordinal);
                if (end < 0) return false;
                position = end + 2;
                continue;
            }
            if (sql[position] == '-' && sql[position + 1] == '-')
            {
                var newline = sql.IndexOfAny(['\r', '\n'], position + 2);
                if (newline < 0) return false;
                position = newline + 1;
                continue;
            }
            break;
        }

        var remaining = sql.AsSpan(position);
        return remaining.StartsWith("CALL", StringComparison.OrdinalIgnoreCase)
            && (remaining.Length == 4 || char.IsWhiteSpace(remaining[4]) || remaining[4] == '(');
    }

    private void EnsureStatement(Db2Statement statement)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(statement);
        statement.EnsureOpen();
        if (!ReferenceEquals(statement.Session, this)) throw new ArgumentException("The prepared statement belongs to another Db2 session.", nameof(statement));
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        // Owns the GSS context and credential handles for SECMEC 11. Disposed on every exit path, so a failed
        // or cancelled handshake leaves nothing behind (the socket is disposed by the caller).
        IKerberosTokenProvider? kerberos = null;
        try
        {
            await HandshakeCoreAsync(provider => kerberos = provider, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            kerberos?.Dispose();
        }
    }

    private async Task HandshakeCoreAsync(Action<IKerberosTokenProvider> trackKerberos, CancellationToken cancellationToken)
    {
        _correlationId = 1;
        var requestedMechanism = _securityMechanism;
        BigInteger? clientPrivateKey = null;
        byte[] accsecToken = [];
        IKerberosTokenProvider? kerberos = null;
        if (requestedMechanism == 9)
        {
            clientPrivateKey = Secmec9.GeneratePrivateKey();
            accsecToken = Secmec9.CalculatePublicKey(clientPrivateKey.Value);
        }
        var clientHost = Environment.MachineName.Length == 0 ? "JustyBase.Db2-client" : Environment.MachineName;
        await SendAsync(DdmCodec.PackExcsat("JustyBase.Db2", clientHost, "SQL12010", "JustyBase.Db2"), false, false, cancellationToken).ConfigureAwait(false);
        await SendAsync(DdmCodec.PackAccsec(_database, _securityMechanism, accsecToken), false, true, cancellationToken).ConfigureAwait(false);
        var negotiation = await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false);
        var negotiated = _securityMechanism;
        byte[] securityToken = [];
        foreach (var reply in negotiation)
        {
            var codePoint = (CodePoint)reply.CodePoint;
            if (codePoint == CodePoint.RDBNFNRM) throw new Db2DatabaseNotFoundException();
            if (codePoint == CodePoint.EXCSATRM)
            {
                var managerValues = DdmCodec.ParseParameters(reply.Payload);
                if (managerValues.TryGetValue((ushort)CodePoint.MGRLVLLS, out var managers))
                {
                    if (managers.Length % 4 != 0) throw new InvalidDataException("DB2 returned a malformed EXCSATRM manager-level list.");
                    for (var offset = 0; offset < managers.Length; offset += 4)
                    {
                        if (BinaryPrimitives.ReadUInt16BigEndian(managers.AsSpan(offset)) != (ushort)CodePoint.SQLAM) continue;
                        _sqlamLevel = BinaryPrimitives.ReadUInt16BigEndian(managers.AsSpan(offset + 2));
                        break;
                    }
                }
            }
            if (codePoint != CodePoint.ACCSECRD) continue;
            var values = DdmCodec.ParseParameters(reply.Payload);
            if (values.TryGetValue((ushort)CodePoint.SECMEC, out var mechanism) && mechanism.Length >= 2)
                negotiated = BinaryPrimitives.ReadUInt16BigEndian(mechanism);
            if (values.TryGetValue((ushort)CodePoint.SECTKN, out var token)) securityToken = token;
        }
        if (negotiated != _securityMechanism)
            throw new NotSupportedException($"DB2 selected security mechanism {negotiated}, but this connection requested {_securityMechanism}; mechanism changes are not allowed.");

        if (negotiated == 3)
        {
            await SendAsync(DdmCodec.PackSecchk(3, securityToken, _database, _config.User, Encoding.UTF8.GetBytes(_config.Password), false, DdmStringEncoding.Cp500), false, false, cancellationToken).ConfigureAwait(false);
        }
        else if (negotiated == 9)
        {
            if (clientPrivateKey is null) throw new Db2UnsupportedFeatureException("DB2 EUSRIDPWD requires a client Diffie-Hellman key pair.");
            if (securityToken.Length < 20) throw new InvalidDataException("DB2 EUSRIDPWD key exchange did not return a usable server security token.");
            var encryptedUserid = Secmec9.Encrypt(Ebcdic.EncodeCp500(_config.User), securityToken, clientPrivateKey.Value);
            var encryptedPassword = Secmec9.Encrypt(Ebcdic.EncodeCp500(_config.Password), securityToken, clientPrivateKey.Value);
            await SendAsync(DdmCodec.PackSecchkEusridpwd(_database, encryptedUserid, encryptedPassword), false, false, cancellationToken).ConfigureAwait(false);
        }
        else if (negotiated == 11)
        {
            kerberos = _kerberosProviderFactory?.Invoke()
                ?? (_kerberosPlan ?? throw new Db2ConfigurationException("Kerberos credential source was not resolved for security mechanism 11.")).CreateProvider();
            trackKerberos(kerberos);
            var kerberosToken = await kerberos.CreateInitialTokenAsync(cancellationToken).ConfigureAwait(false);
            if (kerberosToken.Length > MaximumSecurityTokenLength)
                throw new Db2KerberosException($"The Kerberos token is {kerberosToken.Length} bytes, more than a DRDA SECTKN can carry.");
            // Same shape as IBM's JCC: SECCHK (no USRID, no password) then the token as its own OBJECT DSS.
            await SendAsync(DdmCodec.PackSecchkGssapi(_database), true, false, cancellationToken).ConfigureAwait(false);
            await SendAsync(DdmCodec.PackSecurityTokenObject(kerberosToken), false, false, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new Db2UnsupportedFeatureException($"DB2 negotiated security mechanism {negotiated}; mechanisms 3, 9, and 11 are currently implemented.");
        }
        await SendAsync(DdmCodec.PackAccrdb("SQL12010", _database, DdmStringEncoding.Cp500), false, true, cancellationToken).ConfigureAwait(false);
        var authentication = await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false);
        byte[]? serverKerberosToken = null;
        foreach (var reply in authentication)
        {
            var codePoint = (CodePoint)reply.CodePoint;
            if (codePoint == CodePoint.SECTKN && negotiated == 11)
            {
                serverKerberosToken = reply.Payload;
                continue;
            }
            if (codePoint == CodePoint.SECCHKRM)
            {
                var values = DdmCodec.ParseParameters(reply.Payload);
                if (values.TryGetValue((ushort)CodePoint.SECCHKCD, out var code) && code.Length > 0 && code[0] != 0)
                    throw new Db2AuthenticationException(code[0]);
            }
            else if (codePoint == CodePoint.ACCRDBRM)
            {
                var values = DdmCodec.ParseParameters(reply.Payload);
                if (values.TryGetValue((ushort)CodePoint.RDBINTTKN, out var token) && token.Length > 0) _rdbInterruptToken = token;
            }
            else if (codePoint is CodePoint.RDBAFLRM or CodePoint.RDBATHRM)
                throw new Db2AuthorizationException();
            else if (codePoint == CodePoint.SQLERRRM) throw ServerError(reply.Payload);
        }
        if (kerberos is { RequiresServerToken: true })
        {
            // Mutual authentication: only the real Db2 service can produce a token the context accepts.
            if (serverKerberosToken is null or { Length: 0 })
                throw new Db2KerberosException("Db2 accepted the connection but returned no Kerberos server token, so the server could not be authenticated.");
            await kerberos.CompleteAsync(serverKerberosToken, cancellationToken).ConfigureAwait(false);
        }
        await InitializeSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeSessionAsync(CancellationToken cancellationToken)
    {
        _correlationId = 1;
        await SendAsync(DdmCodec.PackExcsatManagerLevels([new ManagerLevel(CodePoint.CCSIDMGR, 1208)]), false, false, cancellationToken).ConfigureAwait(false);
        await SendAsync(DdmCodec.PackExecuteSqlSet(PackageId, "", 1, _database), true, false, cancellationToken).ConfigureAwait(false);
        var workstation = string.IsNullOrEmpty(_config.ClientWrkstnName) ? Environment.MachineName : _config.ClientWrkstnName;
        workstation = SanitizeClientRegister(workstation);
        await SendAsync(DdmCodec.PackSqlStatement($"SET CLIENT WRKSTNNAME '{EscapeSqlValue(workstation)}'"), false, false, cancellationToken).ConfigureAwait(false);
        await SendAsync(DdmCodec.PackCommit(), false, true, cancellationToken).ConfigureAwait(false);
        foreach (var reply in await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false))
        {
            if ((CodePoint)reply.CodePoint == CodePoint.SQLCARD)
            {
                var card = ParseSqlCardAndReportWarning(reply.Payload);
                if (card.SqlCode < 0) throw SqlError(card);
            }
            else if ((CodePoint)reply.CodePoint == CodePoint.SQLERRRM) throw ServerError(reply.Payload);
        }
        _clientInfo = new Db2ClientInfo(WorkstationName: workstation);
        await ApplyClientInfoLockedAsync(new Db2ClientInfo(
            _config.ClientApplName,
            workstation,
            _config.ClientUserid,
            _config.ClientAcctng,
            _config.ClientCorrToken), cancellationToken).ConfigureAwait(false);
        _baseClientInfo = _clientInfo;
    }

    private async Task ApplyClientInfoLockedAsync(Db2ClientInfo target, CancellationToken cancellationToken)
    {
        target = SanitizeClientInfo(target);
        var registers = new (string Key, string Current, string Value, string[] Commands)[]
        {
            (nameof(Db2ClientInfo.ApplicationName), _clientInfo.ApplicationName, target.ApplicationName, [$"SET CLIENT APPLNAME '{EscapeSqlValue(target.ApplicationName)}'"]),
            (nameof(Db2ClientInfo.WorkstationName), _clientInfo.WorkstationName, target.WorkstationName, [$"SET CLIENT WRKSTNNAME '{EscapeSqlValue(target.WorkstationName)}'"]),
            (nameof(Db2ClientInfo.UserId), _clientInfo.UserId, target.UserId, [$"SET CLIENT USERID '{EscapeSqlValue(target.UserId)}'"]),
            (nameof(Db2ClientInfo.CorrelationToken), _clientInfo.CorrelationToken, target.CorrelationToken,
                [$"SET CLIENT CORR_TOKEN '{EscapeSqlValue(target.CorrelationToken)}'", $"SET CLIENT_CORR_TOKEN = '{EscapeSqlValue(target.CorrelationToken)}'", $"SET CLIENT PROGRAMID '{EscapeSqlValue(target.CorrelationToken)}'"]),
        };
        foreach (var item in registers)
        {
            if (item.Current == item.Value) continue;
            Exception? lastError = null;
            foreach (var sql in item.Commands)
            {
                try
                {
                    await ExecuteSqlSetLockedAsync(sql, cancellationToken).ConfigureAwait(false);
                    _clientInfo = item.Key switch
                    {
                        nameof(Db2ClientInfo.ApplicationName) => _clientInfo with { ApplicationName = item.Value },
                        nameof(Db2ClientInfo.WorkstationName) => _clientInfo with { WorkstationName = item.Value },
                        nameof(Db2ClientInfo.UserId) => _clientInfo with { UserId = item.Value },
                        _ => _clientInfo with { CorrelationToken = item.Value },
                    };
                    lastError = null;
                    break;
                }
                catch (Exception ex) { lastError = ex; }
            }
            if (lastError is not null) throw lastError;
        }
        if (_clientInfo.Accounting != target.Accounting)
        {
            await ExecuteLockedAsync($"CALL SYSPROC.WLM_SET_CLIENT_INFO('', '', '', '{EscapeSqlValue(target.Accounting)}', NULL)", cancellationToken).ConfigureAwait(false);
            await ExecuteSqlSetLockedAsync($"SET CLIENT APPLNAME '{EscapeSqlValue(target.ApplicationName)}'", cancellationToken).ConfigureAwait(false);
            await ExecuteSqlSetLockedAsync($"SET CLIENT WRKSTNNAME '{EscapeSqlValue(target.WorkstationName)}'", cancellationToken).ConfigureAwait(false);
            await ExecuteSqlSetLockedAsync($"SET CLIENT USERID '{EscapeSqlValue(target.UserId)}'", cancellationToken).ConfigureAwait(false);
            _clientInfo = _clientInfo with { Accounting = target.Accounting };
        }
    }

    private async Task SwitchUserLockedAsync(string target, CancellationToken cancellationToken, bool force = false, string? password = null, bool allowFallback = true)
    {
        if (!force && target == _currentUser) return;
        Exception? lastError = null;
        foreach (var sql in new[] { $"SET SESSION AUTHORIZATION = \"{target}\"", $"SET SESSION_USER = \"{target}\"" })
        {
            try
            {
                await ExecuteLockedAsync(sql, cancellationToken).ConfigureAwait(false);
                _currentUser = target;
                return;
            }
            catch (Exception ex) { lastError = ex; }
        }
        if (!allowFallback) throw lastError ?? new InvalidOperationException("DB2 session user switch failed.");
        // DRDA re-authentication fallback, mirroring go-db2: a standalone SECCHK
        // revalidates the new identity on the existing connection context.
        if (_securityMechanism != 3)
            throw new Db2UnsupportedFeatureException($"DB2 user switch fallback requires security mechanism 3; current mechanism {_securityMechanism} is not supported for mid-session re-authentication.");
        _correlationId = 1;
        var passwordBytes = password is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(password);
        await SendAsync(DdmCodec.PackSecchk(3, [], _database, target, passwordBytes, false, DdmStringEncoding.Cp500), false, true, cancellationToken).ConfigureAwait(false);
        var sawSecchkRm = false;
        foreach (var reply in await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false))
        {
            if ((CodePoint)reply.CodePoint == CodePoint.SECCHKRM)
            {
                sawSecchkRm = true;
                var values = DdmCodec.ParseParameters(reply.Payload);
                if (values.TryGetValue((ushort)CodePoint.SECCHKCD, out var code) && code.Length > 0 && code[0] != 0)
                    throw new Db2AuthenticationException(code[0]);
            }
            else if ((CodePoint)reply.CodePoint == CodePoint.SQLERRRM) throw ServerError(reply.Payload);
        }
        if (!sawSecchkRm) throw new InvalidDataException("DB2 user switch SECCHK response did not contain SECCHKRM.");
        _currentUser = target;
    }

    private async Task ExecuteSqlSetLockedAsync(string sql, CancellationToken cancellationToken)
    {
        _correlationId = 1;
        await SendAsync(DdmCodec.PackExecuteSqlSet(PackageId, "", 1, _database), true, false, cancellationToken).ConfigureAwait(false);
        await SendAsync(DdmCodec.PackSqlStatement(sql), false, !AutoCommit, cancellationToken).ConfigureAwait(false);
        if (AutoCommit) await SendAsync(DdmCodec.PackCommit(), false, true, cancellationToken).ConfigureAwait(false);
        foreach (var reply in await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false))
        {
            if ((CodePoint)reply.CodePoint == CodePoint.SQLCARD)
            {
                var card = ParseSqlCardAndReportWarning(reply.Payload);
                if (card.SqlCode < 0) throw SqlError(card);
            }
            else if ((CodePoint)reply.CodePoint == CodePoint.SQLERRRM) throw ServerError(reply.Payload);
        }
    }

    private static Db2ClientInfo SanitizeClientInfo(Db2ClientInfo info) => info with
    {
        ApplicationName = SanitizeClientRegister(info.ApplicationName),
        WorkstationName = SanitizeClientRegister(info.WorkstationName),
        UserId = SanitizeClientRegister(info.UserId),
        Accounting = SanitizeClientRegister(info.Accounting),
        CorrelationToken = SanitizeClientRegister(info.CorrelationToken),
    };

    private static string SanitizeClientRegister(string value) =>
        value.Replace("\0", "", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal);

    private static string EscapeSqlValue(string value) => SanitizeClientRegister(value).Replace("'", "''", StringComparison.Ordinal);

    private async Task<Db2QueryResult> FetchQueryAsync(IReadOnlyList<DssPacket> first, CancellationToken cancellationToken, IReadOnlyList<Db2ColumnMetadata>? describedColumns = null)
    {
        var state = new QueryStreamState { Columns = describedColumns?.ToArray() ?? Array.Empty<Db2ColumnMetadata>() };
        var rows = new List<object?[]>();
        long materializedBytes = 0;
        await ConsumeAndResolveAsync(first).ConfigureAwait(false);
        while (state.IsOpen && !state.Ended)
        {
            var priorPending = state.PendingRowData.Length;
            var priorExternal = state.ExternalData.Count;
            await SendAsync(DdmCodec.PackContinueQuery(PackageId, ConsistencyToken, SectionNumber, _database, _blockSize, state.QueryId), false, true, cancellationToken, state.NextCorrelation).ConfigureAwait(false);
            var replies = await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false);
            var gotData = replies.Any(reply => (CodePoint)reply.CodePoint is CodePoint.QRYDTA or CodePoint.EXTDTA);
            await ConsumeAndResolveAsync(replies).ConfigureAwait(false);
            if (!gotData && state.PendingRowData.Length == priorPending && state.ExternalData.Count == priorExternal && !state.Ended)
                throw new InvalidDataException("DB2 CNTQRY returned neither query data nor an end-of-query reply.");
        }
        if (state.PendingRowData.Length != 0)
            throw new InvalidDataException("DB2 ended a query in the middle of a row.");
        if (state.ExternalData.Count != 0 || state.HasLobFields && HasUnresolvedExternalLocator(state.Fields, rows))
            throw new InvalidDataException("DB2 ended a query with unresolved LOB data.");
        return new Db2QueryResult(state.Columns, rows);

        async Task ConsumeAndResolveAsync(IReadOnlyList<DssPacket> replies)
        {
            var blockRows = ConsumeStreamingReplies(replies, state);
            if (state.HasLobFields)
                await ResolveProgressiveLobsAsync(blockRows, state.Fields, state.QueryId, state.NextCorrelation, cancellationToken).ConfigureAwait(false);
            foreach (var row in blockRows)
            {
                var rowBytes = EstimateMaterializedRowBytes(row);
                if (rowBytes > MaxMaterializedResultBytes - materializedBytes)
                    throw new Db2ResourceLimitException($"DB2 materialized result exceeds the {MaxMaterializedResultBytes}-byte limit.");
                materializedBytes += rowBytes;
            }
            rows.AddRange(blockRows);
        }
    }

    private static long EstimateMaterializedRowBytes(IEnumerable<object?> row)
    {
        long total = 0;
        foreach (var value in row)
        {
            var bytes = value switch
            {
                null => 1,
                string text => Encoding.UTF8.GetByteCount(text),
                byte[] data => data.LongLength,
                decimal => 16,
                DateTime => 8,
                DateTimeOffset => 10,
                TimeSpan => 8,
                Guid => 16,
                _ => 16,
            };
            total = checked(total + bytes);
        }
        return total;
    }

    private sealed class QueryStreamState
    {
        internal IReadOnlyList<Db2ColumnMetadata> Columns = Array.Empty<Db2ColumnMetadata>();
        internal FieldDescriptor[] Fields = [];
        internal Db2ColumnDecoder[] Decoders = [];
        internal Db2ResultMetadataCache? MetadataCache;
        internal bool UseBufferedRows;
        internal Db2BufferedRows? BufferedRows;
        internal Db2DiscardRows? DiscardRows;
        // Scratch lists belong to this enumeration, never to the idle session.
        // Yielded rows retain their own arrays; only list storage is reused.
        internal List<Db2DecodedRow>? DecodedRows;
        internal List<object?[]>? MaterializedRows;
        internal int PendingByteCount => DiscardRows?.PendingBytes ?? BufferedRows?.PendingBytes ?? PendingRowData.Length;
        internal bool HasLobFields;
        internal byte[] PendingRowData = [];
        internal readonly List<byte[]> ExternalData = [];
        internal bool IsOpen;
        internal bool Ended;
        internal bool Failed;
        internal ulong QueryId;
        internal ushort NextCorrelation = 1;
        internal long CurrentRowGeneration;
        internal Db2ProgressiveLobStream? ActiveLobStream;
        internal Action? CancelCommand;
        internal object?[]? CurrentYieldedRow;
        internal readonly Queue<object?[]> PendingRows = new();
    }

    private void ExposeProgressiveLobs(object?[] row, QueryStreamState state, CancellationToken operationToken)
    {
        if (!state.HasLobFields) return;
        for (var column = 0; column < state.Fields.Length && column < row.Length; column++)
        {
            if (row[column] is not Db2ProgressiveLobReference locator) continue;
            var field = state.Fields[column];
            var length = locator.Length <= long.MaxValue ? (long?)locator.Length : null;
            Encoding? textEncoding = IsDbClobType(field.Type) ? Encoding.BigEndianUnicode : IsClobType(field.Type) ? Encoding.UTF8 : null;
            var generation = state.CurrentRowGeneration;
            row[column] = new Db2LobValue(length, textEncoding,
                () => CreateProgressiveLobStream(state, locator, generation, operationToken));
        }
    }

    private Db2ProgressiveLobStream CreateProgressiveLobStream(
        QueryStreamState state,
        Db2ProgressiveLobReference locator,
        long generation,
        CancellationToken operationToken)
    {
        Db2ProgressiveLobStream? stream = null;
        stream = new Db2ProgressiveLobStream(
            locator.Length <= long.MaxValue ? (long?)locator.Length : null,
            cancellationToken => FetchProgressiveLobChunkAsync(state, locator, generation, stream!, operationToken, cancellationToken),
            state.CancelCommand);
        return stream;
    }

    private async ValueTask<(byte[] Data, bool IsFinal)> FetchProgressiveLobChunkAsync(
        QueryStreamState state,
        Db2ProgressiveLobReference locator,
        long generation,
        Db2ProgressiveLobStream stream,
        CancellationToken operationToken,
        CancellationToken readToken)
    {
        if (generation != state.CurrentRowGeneration)
            throw new ObjectDisposedException(nameof(Db2LobValue), "The DB2 LOB locator is no longer on the current row.");
        if (state.ActiveLobStream is not null && !ReferenceEquals(state.ActiveLobStream, stream))
            await state.ActiveLobStream.DisposeAsync().ConfigureAwait(false);
        state.ActiveLobStream = stream;
        using var requestToken = CancellationTokenSource.CreateLinkedTokenSource(operationToken, readToken);
        try
        {
            var request = DdmCodec.PackGetNextChunk(
                PackageId, ConsistencyToken, SectionNumber, _database, state.QueryId, locator.Reference, ProgressiveLobChunkLength);
            await SendAsync(request, false, true, requestToken.Token, state.NextCorrelation).ConfigureAwait(false);
            var replies = await ReadProgressiveChunkReplyChainAsync(requestToken.Token).ConfigureAwait(false);
            foreach (var reply in replies) ThrowIfServerError(reply);
            var chunk = DdmCodec.ParseProgressiveChunkReply(replies);
            if (chunk.EndOfReference || chunk.IsLastChunk) state.ActiveLobStream = null;
            return (chunk.Data, chunk.EndOfReference || chunk.IsLastChunk);
        }
        catch (Exception error) when ((error as IDb2Error)?.SqlCode != -952)
        {
            _closed = true;
            _client.Dispose();
            throw;
        }
        catch (Db2SqlException error) when (error.SqlCode == -952)
        {
            state.ActiveLobStream = null;
            state.Ended = true;
            state.Failed = true;
            throw;
        }
    }

    private async Task DrainProgressiveLobsAsync(object?[] row)
    {
        var hasLob = false;
        foreach (var value in row)
        {
            if (value is Db2LobValue)
            {
                hasLob = true;
                break;
            }
        }
        if (!hasLob) return;

        // Keep the primary exchange alive after user cancellation so SQLINTR can receive its
        // acknowledgment and final SQLCODE. The operation token still bounds this wait after
        // the configured cancellation-cleanup budget expires.
        using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var value in row)
            if (value is Db2LobValue lob) await lob.DrainAsync(drainTimeout.Token).ConfigureAwait(false);
    }

    private static readonly List<object?[]> EmptyDecodedRows = [];
    private Action<Db2Warning?>? _rowWarningReporter;

    private List<object?[]> ConsumeStreamingReplies(IReadOnlyList<DssPacket> replies, QueryStreamState state)
    {
        var rowDataLength = state.PendingRowData.Length;
        foreach (var reply in replies)
            if ((CodePoint)reply.CodePoint == CodePoint.QRYDTA)
                rowDataLength = checked(rowDataLength + reply.Payload.Length);

        foreach (var reply in replies)
        {
            switch ((CodePoint)reply.CodePoint)
            {
                case CodePoint.SQLDARD: state.Columns = state.MetadataCache?.ReadColumns(reply.Payload) ?? DdmCodec.ParseSqlDard(reply.Payload); break;
                case CodePoint.QRYDSC:
                    state.Fields = state.MetadataCache?.ReadFields(reply.Payload) ?? DdmCodec.ParseQueryDescriptor(reply.Payload);
                    state.Decoders = state.MetadataCache?.Decoders ?? Db2ColumnDecoder.Create(state.Fields);
                    state.HasLobFields = state.Fields.Any(static field => IsLobType(field.Type));
                    break;
                case CodePoint.EXTDTA: state.ExternalData.Add(reply.Payload); break;
                case CodePoint.OPNQRYRM:
                    if (DdmCodec.TryGetParameter(reply.Payload, (ushort)CodePoint.QRYINSID, out var id) && id.Length >= 8)
                        state.QueryId = BinaryPrimitives.ReadUInt64BigEndian(id);
                    state.IsOpen = true;
                    state.NextCorrelation = reply.Header.CorrelationId;
                    break;
                case CodePoint.ENDQRYRM: state.Ended = true; break;
                case CodePoint.SQLCARD:
                    var card = ParseSqlCardAndReportWarning(reply.Payload);
                    if (card.SqlCode < 0) { state.Ended = true; state.Failed = true; throw SqlError(card); }
                    if (card.SqlCode == 100) state.Ended = true;
                    break;
                case CodePoint.SQLERRRM: state.Ended = true; state.Failed = true; throw ServerError(reply.Payload);
            }
        }

        if (state.UseBufferedRows && !state.HasLobFields && state.Decoders.Length > 0)
        {
            state.BufferedRows ??= new Db2BufferedRows(state.Decoders);
            state.BufferedRows.BeginFetch();
            if (state.PendingRowData.Length > 0)
            {
                state.BufferedRows.Append(state.PendingRowData);
                state.PendingRowData = [];
            }
            foreach (var reply in replies)
                if ((CodePoint)reply.CodePoint == CodePoint.QRYDTA) state.BufferedRows.Append(reply.Payload);
            state.BufferedRows.Validate(_rowWarningReporter ??= ReportWarning);
            return EmptyDecodedRows;
        }

        var rowBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(rowDataLength, 1));
        try
        {
            var written = state.PendingRowData.Length;
            state.PendingRowData.CopyTo(rowBuffer, 0);
            foreach (var reply in replies)
            {
                if ((CodePoint)reply.CodePoint != CodePoint.QRYDTA) continue;
#if DB2_PERF_COUNTERS
                Interlocked.Add(ref Db2PerformanceCounters.RowBufferCopyBytes, reply.Payload.Length);
#endif
                reply.Payload.CopyTo(rowBuffer, written);
                written += reply.Payload.Length;
            }

            var reusableRows = !state.HasLobFields ? state.DecodedRows ??= [] : null;
            var (decodedRows, consumed) = DecodeRowsWithWarnings(state.Fields, rowBuffer.AsSpan(0, written), reusableRows);
            state.PendingRowData = consumed < written ? rowBuffer.AsSpan(consumed, written - consumed).ToArray() : [];
            foreach (var row in decodedRows) ReportWarning(row.Warning);
            var rows = !state.HasLobFields ? state.MaterializedRows ??= [] : new List<object?[]>(decodedRows.Count);
            rows.Clear();
            rows.EnsureCapacity(decodedRows.Count);
            foreach (var decodedRow in decodedRows) rows.Add(decodedRow.Values);
            if (reusableRows is not null) decodedRows.Clear();
            var extdtaUsed = state.HasLobFields
                ? StitchExtdta(state.Fields, rows, state.ExternalData)
                : 0;
            if (extdtaUsed > 0) state.ExternalData.RemoveRange(0, extdtaUsed);
            return rows;
        }
        finally { ArrayPool<byte>.Shared.Return(rowBuffer, clearArray: false); }
    }

    private IReadOnlyList<object?[]> DecodeRows(IReadOnlyList<FieldDescriptor> fields, ReadOnlySpan<byte> data)
    {
        var (decodedRows, _) = DecodeRowsWithWarnings(fields, data);
        foreach (var row in decodedRows) ReportWarning(row.Warning);
        var rows = new object?[decodedRows.Count][];
        for (var index = 0; index < decodedRows.Count; index++) rows[index] = decodedRows[index].Values;
        return rows;
    }

    internal static (List<object?[]> Rows, int Consumed) DecodeRowsIncremental(IReadOnlyList<FieldDescriptor> fields, byte[] data)
    {
        var (decodedRows, consumed) = DecodeRowsWithWarnings(fields, data);
        var rows = new List<object?[]>(decodedRows.Count);
        foreach (var row in decodedRows) rows.Add(row.Values);
        return (rows, consumed);
    }

    internal static (List<Db2DecodedRow> Rows, int Consumed) DecodeRowsWithWarnings(IReadOnlyList<FieldDescriptor> fields, ReadOnlySpan<byte> data,
        List<Db2DecodedRow>? reusableRows = null)
    {
        var rows = reusableRows ?? [];
        rows.Clear();
        if (fields.Count == 0 || data.Length == 0) return (rows, 0);
        var offset = 0;
        while (offset + 2 <= data.Length)
        {
            var rowStart = offset;
            if (!TryReadRowSqlca(data, ref offset, out var warning)) return (rows, rowStart);

            var row = new object?[fields.Count];
            try
            {
                for (var i = 0; i < fields.Count; i++) row[i] = DecodeField(fields[i], data, ref offset);
            }
            catch (InvalidDataException ex) when (ex.Message.StartsWith("Truncated DB2 query field", StringComparison.Ordinal))
            {
                return (rows, rowStart);
            }
            rows.Add(new Db2DecodedRow(row, warning));
        }
        return (rows, offset);
    }

    internal static bool TryMeasureRow(Db2ColumnDecoder[] decoders, ReadOnlySpan<byte> data, ref int offset, out int fieldStart, out Db2Warning? warning)
    {
        var start = offset;
        fieldStart = offset;
        if (!TryReadRowSqlca(data, ref offset, out warning)) { offset = start; return false; }
        fieldStart = offset;
        foreach (ref readonly var decoder in decoders.AsSpan())
        {
            if (decoder.TrySkip(data, ref offset, out _, out _)) continue;
            offset = start;
            return false;
        }
        return true;
    }

    internal static bool TryReadRowSqlca(ReadOnlySpan<byte> data, ref int offset, out Db2Warning? warning)
    {
        var rowStart = offset;
        warning = null;
        if (offset >= data.Length) return false;
        var sqlcaIndicator = data[offset++];
        if (sqlcaIndicator == 0xff)
        {
            if (offset >= data.Length || data[offset] != 0)
            {
                offset = rowStart;
                return false;
            }
            offset++;
            return true;
        }
        if (sqlcaIndicator != 0 || data.Length - offset < 18)
        {
            offset = rowStart;
            return false;
        }

        var sqlCode = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        var sqlState = Encoding.ASCII.GetString(data.Slice(offset + 4, 5));
        offset += 4 + 5 + 8; // SQLCODE, SQLSTATE, SQLERRPROC.

        var xGroupIndicator = data[offset++];
        var warningFlags = "";
        var messages = new List<string>(2);
        if (xGroupIndicator == 0)
        {
            if (data.Length - offset < 24 + 11)
            {
                offset = rowStart;
                return false;
            }
            offset += 24; // SQLERRD(1..6).
            warningFlags = Encoding.ASCII.GetString(data.Slice(offset, 11));
            offset += 11;
            if (!TryReadSqlcaString(data, ref offset, out _)
                || !TryReadSqlcaString(data, ref offset, out var messageMain)
                || !TryReadSqlcaString(data, ref offset, out var messageSecondary))
            {
                offset = rowStart;
                return false;
            }
            if (!string.IsNullOrWhiteSpace(messageMain)) messages.Add(messageMain);
            if (!string.IsNullOrWhiteSpace(messageSecondary)) messages.Add(messageSecondary);
        }
        else if (xGroupIndicator != 0xff)
        {
            throw new InvalidDataException($"Invalid SQLCAXGRP indicator 0x{xGroupIndicator:X2} in query row.");
        }

        if (offset >= data.Length)
        {
            offset = rowStart;
            return false;
        }
        var diagnosticIndicator = data[offset++];
        if (diagnosticIndicator != 0xff)
            throw new Db2UnsupportedFeatureException("SQLDIAGGRP in a query row is not supported by this driver.");

        if (sqlCode < 0)
            throw new Db2SqlException(sqlCode, sqlState, $"DB2 SQLCODE={sqlCode} SQLSTATE={sqlState} in query row.");

        if ((sqlCode > 0 && sqlCode != 100)
            || sqlState.StartsWith("01", StringComparison.Ordinal)
            || warningFlags.Length > 0 && warningFlags[0] == 'W')
        {
            var message = messages.Count > 0
                ? string.Join(Environment.NewLine, messages)
                : $"DB2 returned a row warning (SQLCODE={sqlCode}, SQLSTATE={sqlState}).";
            warning = new Db2Warning(sqlCode, sqlState, message, warningFlags);
        }

        if (offset >= data.Length || data[offset] != 0)
        {
            offset = rowStart;
            return false;
        }
        offset++;
        return true;
    }

    private static bool TryReadSqlcaString(ReadOnlySpan<byte> data, ref int offset, out string value)
    {
        value = "";
        if (data.Length - offset < 2) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;
        if (data.Length - offset < length) return false;
        value = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return true;
    }

    internal static object? DecodeField(FieldDescriptor field, ReadOnlySpan<byte> data, ref int offset)
    {
        if (IsNullableType(field.Type))
        {
            Require(data, offset, 1);
            var indicator = data[offset++];
            if (indicator is 0xff or 0x80 or 0x7f) return null;
        }
        var length = (field.Precision << 8) | field.Scale;
        switch (field.Type)
        {
            case 0x02: case 0x03: Require(data, offset, 4); var int32 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset)); offset += 4; return int32;
            case 0x04: case 0x05: Require(data, offset, 2); var int16 = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset)); offset += 2; return int16;
            case 0x16: case 0x17: Require(data, offset, 8); var int64 = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset)); offset += 8; return int64;
            case 0x06: case 0x07: Require(data, offset, 1); return (sbyte)data[offset++];
            case 0x0a: case 0x0b: Require(data, offset, 8); var float64 = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset))); offset += 8; return float64;
            case 0x0c: case 0x0d: Require(data, offset, 4); var float32 = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset))); offset += 4; return float32;
            case 0x30: case 0x31: case 0x3c: case 0x3d: return ReadUtf8(data, ref offset, length);
            case 0x32: case 0x33: case 0x34: case 0x35: case 0x3e: case 0x3f: case 0x40: case 0x41: return ReadUtf8(data, ref offset, ReadLength(data, ref offset));
            case 0x36: case 0x37: return ReadUtf16(data, ref offset, length * 2);
            case 0x38: case 0x39: case 0x3a: case 0x3b: return ReadUtf16(data, ref offset, ReadLength(data, ref offset) * 2);
            case 0x20: case 0x21: case 0x22: case 0x23: case 0x24: case 0x25: return ReadUtf8Trimmed(data, ref offset, length);
            case 0x0e: case 0x0f:
            {
                var packedLength = (field.Precision + 2) / 2;
                Require(data, offset, packedLength);
                var value = DecodePackedDecimal(data.Slice(offset, packedLength), field.Scale);
                offset += packedLength;
                return value;
            }
            case 0xba: case 0xbb:
            {
                var dfpLength = field.Precision == 16 || field.Scale == 16 ? 16 : 8;
                Require(data, offset, dfpLength);
                var value = Decfloat.Decode(data.Slice(offset, dfpLength));
                offset += dfpLength;
                return value;
            }
            case 0xbe: case 0xbf:
                Require(data, offset, length);
                var boolean = length > 0 && data[offset + length - 1] != 0;
                offset += length;
                return boolean;
            case 0x26: case 0x27: case 0x1e: case 0x1f: case 0xc0: case 0xc1: return ReadBytes(data, ref offset, length & 0x7fff);
            case 0x28: case 0x29: case 0x2a: case 0x2b: case 0xc2: case 0xc3: return ReadBytes(data, ref offset, ReadLength(data, ref offset));
            case 0x10: case 0x11: case 0x18: case 0x19: case 0x1a: case 0x1b: case 0x1c: case 0x1d:
            case 0xcc: // Non-nullable DBCLOB, observed in direct DBCLOB expression results.
            case 0xcd: case 0xc6: case 0xc7: case 0xc8: case 0xc9: case 0xce: case 0xcf:
            case 0xf4: case 0xf5: case 0xf6: case 0xf7: case 0xf8: case 0xf9:
                var lobLength = length & 0x7fff;
                var locatorLength = lobLength == 0 ? 4 : lobLength;
                var locator = ReadBytes(data, ref offset, locatorLength);
                if ((field.Precision & 0x80) != 0 && field.Scale == 9 && locatorLength == 9 && locator[0] == 0x03)
                {
                    var progressiveChars = BinaryPrimitives.ReadUInt64BigEndian(locator.AsSpan(1, 8));
                    // Progressive DBCLOB advertises UTF-16 code units while its GETNXTCHK
                    // chunks are UTF-16BE bytes, so the byte budget is twice the locator
                    // value. CLOB/BLOB locators already advertise bytes.
                    var progressiveLength = IsDbClobType(field.Type) ? checked(progressiveChars * 2) : progressiveChars;
                    var reference = BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(data, ref offset, 8));
                    return new Db2ProgressiveLobReference(progressiveLength, reference);
                }
                if ((field.Precision & 0x80) != 0 && field.Scale == 9 && locatorLength == 9 && locator[0] == 0x01)
                {
                    var inlineLength = BinaryPrimitives.ReadUInt64BigEndian(locator.AsSpan(1, 8));
                    if (IsDbClobType(field.Type)) inlineLength = checked(inlineLength * 2);
                    if (inlineLength > MaxMaterializedLobBytes)
                        throw new Db2ResourceLimitException($"DB2 inline LOB exceeds the {MaxMaterializedLobBytes}-byte materialization limit.");
                    if (inlineLength > int.MaxValue)
                        throw new InvalidDataException("DB2 inline LOB length exceeds this runtime's array limit.");
                    var value = ReadBytes(data, ref offset, checked((int)inlineLength));
                    if (IsDbClobType(field.Type)) return Encoding.BigEndianUnicode.GetString(value);
                    if (IsClobType(field.Type)) return Encoding.UTF8.GetString(value);
                    return value;
                }
                return locator;
            default: throw new Db2UnsupportedFeatureException($"Unsupported DB2 query field type 0x{field.Type:X2}.");
        }
    }

    internal static int StitchExtdta(
        IReadOnlyList<FieldDescriptor> fields,
        IList<object?[]> rows,
        IReadOnlyList<byte[]> values,
        bool externalValuesHaveNullIndicator = false)
    {
        var lobColumns = fields.Select((field, index) => (field, index)).Where(item => IsLobType(item.field.Type)).Select(item => item.index).ToArray();
        var valueIndex = 0;
        foreach (var row in rows)
        {
            foreach (var column in lobColumns)
            {
                var field = fields[column];
                if (!TryGetExternalLength(field, row[column], out var externalLength)) continue;
                if (externalLength == 0)
                {
                    row[column] = IsClobType(field.Type) ? string.Empty : Array.Empty<byte>();
                    continue;
                }
                if (valueIndex >= values.Count) continue;
                var raw = values[valueIndex++];
                // SQLDTARD output EXTDTA always carries a nullable value
                // indicator. Query and procedure-cursor EXTDTA carry it only
                // for a nullable LOB descriptor; a NOT NULL LOB is sent
                // verbatim, so its first byte may legitimately be 0x00 and must
                // not be stripped.
                var hasLeadingNullIndicator = externalValuesHaveNullIndicator || IsNullableLobType(field.Type);
                var data = hasLeadingNullIndicator && raw.Length > 0 && raw[0] == 0
                    ? raw.AsSpan(1).ToArray()
                    : raw;
                row[column] = DecodeExternalLobValue(field, data);
            }
        }
        return valueIndex;
    }

    private const ulong ProgressiveLobChunkLength = 1024 * 1024;

    private async Task ResolveProgressiveLobsAsync(
        IList<object?[]> rows,
        IReadOnlyList<FieldDescriptor> fields,
        ulong queryId,
        ushort correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await ResolveProgressiveLobsCoreAsync(rows, fields, queryId, correlationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when ((error as IDb2Error)?.SqlCode != -952)
        {
            // A locator failure leaves the open cursor's position uncertain. Only the
            // server-confirmed -952 response proves that Db2 ended the active request.
            _closed = true;
            _client.Dispose();
            throw;
        }
    }

    private async Task ResolveProgressiveLobsCoreAsync(
        IList<object?[]> rows,
        IReadOnlyList<FieldDescriptor> fields,
        ulong queryId,
        ushort correlationId,
        CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            for (var column = 0; column < fields.Count && column < row.Length; column++)
            {
                if (row[column] is not Db2ProgressiveLobReference progressive) continue;
                if (progressive.Length == 0)
                {
                    row[column] = DecodeExternalLobValue(fields[column], Array.Empty<byte>());
                    continue;
                }
                using var value = new MemoryStream();
                while (true)
                {
                    var request = DdmCodec.PackGetNextChunk(
                        PackageId, ConsistencyToken, SectionNumber, _database, queryId, progressive.Reference, ProgressiveLobChunkLength);
                    await SendAsync(request, false, true, cancellationToken, correlationId).ConfigureAwait(false);
                    var replies = await ReadProgressiveChunkReplyChainAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var reply in replies) ThrowIfServerError(reply);
                    var chunk = DdmCodec.ParseProgressiveChunkReply(replies);
                    if (chunk.Data.Length > MaxMaterializedLobBytes - value.Length)
                        throw new Db2ResourceLimitException($"DB2 progressive LOB exceeds the {MaxMaterializedLobBytes}-byte materialization limit.");
                    if (value.Length > int.MaxValue - chunk.Data.Length)
                        throw new InvalidDataException("DB2 progressive LOB exceeds this runtime's array limit.");
                    value.Write(chunk.Data);
                    if (chunk.EndOfReference || chunk.IsLastChunk) break;
                    if (chunk.Data.Length == 0)
                        throw new InvalidDataException("DB2 GETNXTCHK made no progress before the final chunk.");
                }
                row[column] = DecodeExternalLobValue(fields[column], value.ToArray());
            }
        }
    }

    private static object DecodeExternalLobValue(FieldDescriptor field, byte[] data)
    {
        if (data.Length > MaxMaterializedLobBytes)
            throw new Db2ResourceLimitException($"DB2 LOB exceeds the {MaxMaterializedLobBytes}-byte materialization limit.");
        if (IsDbClobType(field.Type))
        {
            var position = 0;
            return ReadUtf16(data, ref position, data.Length);
        }
        if (IsClobType(field.Type)) return Encoding.UTF8.GetString(data);
        return data;
    }

    private static bool TryGetExternalLength(FieldDescriptor field, object? value, out ulong length)
    {
        if (value is byte[] locator && IsLobType(field.Type) && (field.Precision & 0x80) != 0
            && field.Scale == 9 && locator.Length == 9 && locator[0] == 0x02)
        {
            // A zero-length CLOB/BLOB/DBCLOB locator has no EXTDTA payload. XML
            // locators use an all-ones length and remain external by this rule.
            length = BinaryPrimitives.ReadUInt64BigEndian(locator.AsSpan(1));
            return true;
        }
        length = 0;
        return false;
    }

    internal static bool HasUnresolvedExternalLocator(IReadOnlyList<FieldDescriptor> fields, IList<object?[]> rows)
    {
        foreach (var row in rows)
        {
            for (var column = 0; column < fields.Count && column < row.Length; column++)
            {
                if (TryGetExternalLength(fields[column], row[column], out var length) && length != 0)
                    return true;
                if (row[column] is Db2ProgressiveLobReference)
                    return true;
            }
        }
        return false;
    }

    internal static bool IsNullableType(byte type) => type is 0x03 or 0x05 or 0x07 or 0x0b or 0x0d or 0x0f or
        0x17 or 0x19 or 0x1b or 0x1d or 0x1f or 0x21 or 0x23 or 0x25 or 0x27 or 0x29 or 0x2b or
        0x31 or 0x33 or 0x35 or 0x37 or 0x39 or 0x3b or 0x3d or 0x3f or 0x41 or 0x43 or 0xbb or
        0xbf or 0xc1 or 0xc3 or 0xc7 or 0xc9 or 0xcd or 0xcf or 0xf5 or 0xf7 or 0xf9;

    internal static bool IsLobType(byte type) => type is 0x10 or 0x11 or 0x18 or 0x19 or 0x1a or 0x1b or 0x1c or 0x1d or
        0xc6 or 0xc7 or 0xc8 or 0xc9 or 0xcc or 0xcd or 0xce or 0xcf or 0xf4 or 0xf5 or 0xf6 or 0xf7 or 0xf8 or 0xf9;

    private static bool IsClobType(byte type) => type is 0x1a or 0x1b or 0x1c or 0x1d or 0xc6 or 0xc7 or 0xcc or 0xcd or 0xce or 0xcf or 0xf6 or 0xf7 or 0xf8 or 0xf9;

    /// <summary>Nullable LOB descriptors use the odd member of each type pair (e.g. 0xc9 BLOB, 0xcd DBCLOB).</summary>
    internal static bool IsNullableLobType(byte type) => IsLobType(type) && (type & 1) == 1;

    internal static bool IsDbClobType(byte type) => type is 0x1c or 0x1d or 0xcc or 0xcd or 0xf8 or 0xf9;

    private static int ReadLength(ReadOnlySpan<byte> data, ref int offset)
    {
        Require(data, offset, 2); var value = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset)); offset += 2; return value;
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> data, ref int offset, int length)
    {
        Require(data, offset, length); var result = data.Slice(offset, length).ToArray(); offset += length; return result;
    }

    private static string ReadUtf8(ReadOnlySpan<byte> data, ref int offset, int length)
    {
        Require(data, offset, length);
        var result = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return result;
    }

    private static string ReadUtf8Trimmed(ReadOnlySpan<byte> data, ref int offset, int length)
    {
        Require(data, offset, length);
        var bytes = data.Slice(offset, length);
        offset += length;
        var charCount = Encoding.UTF8.GetCharCount(bytes);
        if (charCount <= 128)
        {
            Span<char> chars = stackalloc char[charCount];
            var written = Encoding.UTF8.GetChars(bytes, chars);
            return new string(chars[..written].Trim());
        }

        var rented = ArrayPool<char>.Shared.Rent(charCount);
        try
        {
            var written = Encoding.UTF8.GetChars(bytes, rented.AsSpan(0, charCount));
            return new string(rented.AsSpan(0, written).Trim());
        }
        finally { ArrayPool<char>.Shared.Return(rented); }
    }

    private static string ReadUtf16(ReadOnlySpan<byte> data, ref int offset, int length)
    {
        Require(data, offset, length);
        var evenLength = length & ~1;
        var result = Encoding.BigEndianUnicode.GetString(data.Slice(offset, evenLength));
        offset += length;
        return result;
    }

    private static string DecodePackedDecimal(ReadOnlySpan<byte> bytes, int scale)
    {
        if (bytes.Length == 0) return "0";
        var digits = new StringBuilder(bytes.Length * 2);
        for (var i = 0; i < bytes.Length - 1; i++) { digits.Append(bytes[i] >> 4); digits.Append(bytes[i] & 0x0f); }
        var last = bytes[^1]; digits.Append(last >> 4);
        var result = digits.ToString().TrimStart('0'); if (result.Length == 0) result = "0";
        if (scale > 0) result = result.Length <= scale ? $"0.{new string('0', scale - result.Length)}{result}" : $"{result[..^scale]}.{result[^scale..]}";
        if ((last & 0x0f) is 0x0b or 0x0d) result = "-" + result;
        return result;
    }

    private async Task ControlAsync(byte[] payload, CancellationToken cancellationToken) =>
        await WithGateAsync(async token =>
        {
            EnsureOpen();
            await ControlLockedAsync(payload, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    private async Task ControlLockedAsync(byte[] payload, CancellationToken cancellationToken)
    {
        _correlationId = 1;
        await SendAsync(payload, false, true, cancellationToken).ConfigureAwait(false);
        foreach (var reply in await ReadReplyChainAsync(cancellationToken).ConfigureAwait(false))
            ThrowIfServerError(reply);
    }

    private void ThrowIfServerError(DssPacket reply)
    {
        switch ((CodePoint)reply.CodePoint)
        {
            case CodePoint.SQLCARD:
                var card = ParseSqlCardAndReportWarning(reply.Payload);
                if (card.SqlCode < 0) throw SqlError(card);
                break;
            case CodePoint.SQLERRRM:
                throw ServerError(reply.Payload);
        }
    }

    private Task SendAsync(byte[] payload, bool nextHasSameId, bool lastPacket, CancellationToken token, ushort? correlationId = null)
    {
#if DB2_PERF_COUNTERS
        if (payload.Length >= 4 && BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2)) == (ushort)CodePoint.CNTQRY)
            Interlocked.Increment(ref Db2PerformanceCounters.Fetches);
#endif
        _poolStateDirty = true;
        try
        {
            token.ThrowIfCancellationRequested();
            _correlationId = _writeBuffer.Append(payload, correlationId ?? _correlationId, nextHasSameId, lastPacket);
            return lastPacket ? FlushWritesAsync(token) : Task.CompletedTask;
        }
        catch { _closed = true; throw; }
    }

    private async Task FlushWritesAsync(CancellationToken token)
    {
        try { await _writeBuffer.FlushAsync(_stream, token).ConfigureAwait(false); }
        catch { _closed = true; throw; }
    }

    private async Task SendExternalDataAsync(SqlDtaEncoder.ExternalDataParameter externalData, bool nextHasSameId, bool lastPacket, CancellationToken cancellationToken, CancellationToken? sourceReadCancellationToken = null)
    {
        if (externalData.Source is not { } source)
        {
            await SendAsync(externalData.DdmObject ?? throw new InvalidOperationException("External parameter has no encoded DDM object."), nextHasSameId, lastPacket, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            // Forward-only EXTDTA cannot be held in the chain buffer. Flush its
            // preceding chained commands before streaming the external source.
            await FlushWritesAsync(cancellationToken).ConfigureAwait(false);
            _correlationId = await DssCodec.WriteExternalLobAsync(_stream, source, externalData.Length, _correlationId, nextHasSameId, lastPacket, cancellationToken, sourceReadCancellationToken ?? cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await InvalidateAfterExternalDataFailureAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!externalData.LeaveOpen)
            {
                try { await source.DisposeAsync().ConfigureAwait(false); }
                catch
                {
                    await InvalidateAfterExternalDataFailureAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
    }

    private async Task InvalidateAfterExternalDataFailureAsync()
    {
        _closed = true;
        try { await _stream.DisposeAsync().ConfigureAwait(false); }
        catch { /* Preserve the encoding, source, or transport error that made the protocol stream unusable. */ }
    }

    private async Task<IReadOnlyList<DssPacket>> ReadReplyChainAsync(CancellationToken token)
    {
        var replies = new List<DssPacket>();
        try
        {
            bool chained;
            do
            {
                var reply = await (_readBuffer ??= new DssReadBuffer(_stream)).ReadAsync(token).ConfigureAwait(false);
                replies.Add(reply);
                chained = reply.Header.Chained;
            } while (chained);
            foreach (var reply in replies)
                if ((CodePoint)reply.CodePoint == CodePoint.DTAMCHRM)
                    throw new InvalidOperationException("DB2 server returned DTAMCHRM (SQLDTA descriptor and parameter data do not match).");
            return replies;
        }
        catch { _closed = true; throw; }
    }

    private ValueTask<IReadOnlyList<DssPacket>> ReadProgressiveChunkReplyChainAsync(CancellationToken token) =>
        (_readBuffer ??= new DssReadBuffer(_stream)).ReadStreamedChainAsync(4 * 1024 * 1024, token);

    /// <summary>Applies go-db2-style per-command context: target user and client-info registers.</summary>
    private async Task ApplyContextStateLockedAsync(Db2CommandOptions options, CancellationToken cancellationToken)
    {
        if (options.User is not null)
        {
            var target = ValidateUserIdentifier(options.User);
            if (target != _currentUser) await SwitchUserLockedAsync(target, cancellationToken).ConfigureAwait(false);
        }
        if (options.ClientInfo is { } info)
        {
            var target = new Db2ClientInfo(
                string.IsNullOrEmpty(info.ApplicationName) ? _clientInfo.ApplicationName : info.ApplicationName,
                string.IsNullOrEmpty(info.WorkstationName) ? _clientInfo.WorkstationName : info.WorkstationName,
                string.IsNullOrEmpty(info.UserId) ? _clientInfo.UserId : info.UserId,
                string.IsNullOrEmpty(info.Accounting) ? _clientInfo.Accounting : info.Accounting,
                string.IsNullOrEmpty(info.CorrelationToken) ? _clientInfo.CorrelationToken : info.CorrelationToken);
            await ApplyClientInfoLockedAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<T> WithGateAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken, Db2CommandOptions? commandOptions = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        return WithGateAsync((operationToken, _) => action(operationToken), cancellationToken, commandOptions);
    }

    private async Task<T> WithGateAsync<T>(Func<CancellationToken, CancellationToken, Task<T>> action, CancellationToken cancellationToken, Db2CommandOptions? commandOptions = null)
    {
        commandOptions?.Validate();
        EnsureOpen();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task<bool>? pending;
            lock (_interruptGate) pending = _interruptPending;
            if (pending is not null)
            {
                try { await pending.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { /* A completed interrupt does not poison the next request. */ }
            }

            if (commandOptions is { User: not null } or { ClientInfo: not null })
                await ApplyContextStateLockedAsync(commandOptions, cancellationToken).ConfigureAwait(false);

            if (!cancellationToken.CanBeCanceled && !(commandOptions?.Timeout > TimeSpan.Zero))
            {
                // Manual InterruptAsync still observes an active operation. No caller
                // cancellation or command deadline exists to require a second race task.
                Volatile.Write(ref _operationActive, 1);
                return await action(CancellationToken.None, CancellationToken.None).ConfigureAwait(false);
            }

            // Caller cancellation must leave the main socket alive long enough to receive Db2's
            // SQLCODE -952 response. If the interrupt or original command cannot settle inside
            // the cleanup budget, the session is discarded.
            using var operationTimeout = new CancellationTokenSource();
            using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var cleanupTimeout = commandOptions?.CancellationCleanupTimeout ?? TimeSpan.FromSeconds(30);
            if (commandOptions?.Timeout > TimeSpan.Zero) commandCancellation.CancelAfter(commandOptions.Timeout);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool>? cancellationInterrupt = null;
            Volatile.Write(ref _operationActive, 1);
            using var cancellationRegistration = commandCancellation.Token.Register(() =>
            {
                if (Volatile.Read(ref _operationActive) == 0) return;
                operationTimeout.CancelAfter(cleanupTimeout);
                try { cancellationInterrupt = InterruptAsync(cleanupTimeout); }
                catch { /* The cleanup deadline aborts and discards the unresponsive primary exchange. */ }
                cancelled.TrySetResult();
            });
            try
            {
                var actionTask = action(operationTimeout.Token, commandCancellation.Token);
                if (commandCancellation.IsCancellationRequested)
                {
                    await ConfirmCancellationAsync(actionTask, () => cancellationInterrupt, cleanupTimeout).ConfigureAwait(false);
                    ThrowCommandCancellation(cancellationToken, commandCancellation.Token, commandOptions);
                }
                var completed = await Task.WhenAny(actionTask, cancelled.Task).ConfigureAwait(false);
                if (completed != actionTask)
                {
                    await ConfirmCancellationAsync(actionTask, () => cancellationInterrupt, cleanupTimeout).ConfigureAwait(false);
                    ThrowCommandCancellation(cancellationToken, commandCancellation.Token, commandOptions);
                }
                var result = await actionTask.ConfigureAwait(false);
                if (commandCancellation.IsCancellationRequested)
                {
                    await ConfirmInterruptAsync(() => cancellationInterrupt, cleanupTimeout).ConfigureAwait(false);
                    ThrowCommandCancellation(cancellationToken, commandCancellation.Token, commandOptions);
                }
                return result;
            }
            catch (Exception error) when (commandCancellation.IsCancellationRequested && error is not Db2ConnectionClosedException)
            {
                await ConfirmInterruptAsync(() => cancellationInterrupt, cleanupTimeout).ConfigureAwait(false);
                ThrowCommandCancellation(cancellationToken, commandCancellation.Token, commandOptions, error);
                throw;
            }
            finally
            {
                Volatile.Write(ref _operationActive, 0);
            }
        }
        finally { Volatile.Write(ref _operationActive, 0); _gate.Release(); }
    }

    private async Task ConfirmCancellationAsync(Task pending, Func<Task<bool>?> interrupt, TimeSpan cleanupTimeout)
    {
        await ConfirmInterruptAsync(interrupt, cleanupTimeout).ConfigureAwait(false);
        try { await pending.WaitAsync(cleanupTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new Db2ConnectionClosedException($"DB2 command did not settle within {cleanupTimeout} after SQLINTR.");
        }
        catch { /* The server's SQLCODE -952 is expected; the task is now fully settled. */ }
    }

    private async Task ConfirmInterruptAsync(Func<Task<bool>?> interrupt, TimeSpan cleanupTimeout)
    {
        var pending = interrupt();
        if (pending is null)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new Db2ConnectionClosedException("DB2 cancellation was requested but no SQLINTR acknowledgment was received.");
        }
        try
        {
            if (!await pending.WaitAsync(cleanupTimeout).ConfigureAwait(false))
                throw new InvalidDataException("DB2 did not acknowledge SQLINTR.");
        }
        catch (Exception error)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new Db2ConnectionClosedException($"DB2 SQLINTR could not be confirmed; the session was discarded. {error.Message}");
        }
    }

    private static void ThrowCommandCancellation(CancellationToken callerToken, CancellationToken operationToken, Db2CommandOptions? options, Exception? inner = null)
    {
        if (options?.Timeout > TimeSpan.Zero && operationToken.IsCancellationRequested && !callerToken.IsCancellationRequested)
            throw new Db2CommandTimeoutException(options.Timeout, inner);
        throw new OperationCanceledException(callerToken.IsCancellationRequested ? callerToken : operationToken);
    }

    private void EnsureOpen() { if (_closed || _client.Client is null) throw new Db2ConnectionClosedException(); }

    private SqlCard ParseSqlCardAndReportWarning(ReadOnlySpan<byte> payload)
    {
        var card = DdmCodec.ParseSqlCard(payload);
        ReportWarning(card.Warning);
        return card;
    }

    private void ReportWarning(Db2Warning? warning)
    {
        if (warning is not null) Warning?.Invoke(this, new Db2WarningEventArgs(warning));
    }

    private static void Require(ReadOnlySpan<byte> data, int offset, int count)
    {
        if (count < 0 || offset < 0 || offset + count > data.Length) throw new InvalidDataException("Truncated DB2 query field.");
    }

    private static Db2SqlException SqlError(SqlCard card) => new(card.SqlCode, card.SqlState,
        $"DB2 SQLCODE={card.SqlCode} SQLSTATE={card.SqlState}{(card.Message.Length > 0 ? " " + card.Message : "")}");

    internal static Exception ServerError(byte[] payload)
    {
        try
        {
            var parameters = DdmCodec.ParseParameters(payload);
            return new Db2ServerErrorException(parameters.TryGetValue((ushort)CodePoint.SRVDGN, out var diagnostic) ? DecodeServerDiagnostic(diagnostic) : "DB2 server returned SQLERRRM.");
        }
        catch (InvalidDataException) { return new Db2ServerErrorException("DB2 server returned SQLERRRM."); }
    }

    private static string DecodeServerDiagnostic(byte[] diagnostic)
    {
        try { return new UTF8Encoding(false, true).GetString(diagnostic); }
        catch (DecoderFallbackException) { return Ebcdic.DecodeCp500(diagnostic); }
    }

    private static string ValidateUserIdentifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var target = value.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(target, "^[A-Za-z0-9_#$]{1,128}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException($"Invalid DB2 session user identifier '{value}'.", nameof(value));
        return target;
    }

    private static RemoteCertificateValidationCallback? CreateCertificateValidator(string caPath)
    {
        if (string.IsNullOrEmpty(caPath)) return null;
        var root = X509CertificateLoader.LoadCertificateFromFile(caPath);
        return (_, certificate, _, errors) =>
        {
            if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            return chain.Build(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
        };
    }
}
