using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;

namespace JustyBase.Db2;

public sealed record Db2PoolOptions(int MaxConnections = 10)
{
    public int MaxWaiters { get; init; } = 100;
    public TimeSpan AcquireTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan ReapInterval { get; init; } = TimeSpan.FromSeconds(30);
    internal void Validate()
    {
        if (MaxConnections < 1 || MaxWaiters < 1) throw new ArgumentOutOfRangeException(nameof(MaxConnections), "Pool capacities must be positive integers.");
        foreach (var value in new[] { AcquireTimeout, ConnectTimeout, IdleTimeout, MaxLifetime, ReapInterval })
            if (value <= TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1) throw new ArgumentOutOfRangeException(nameof(AcquireTimeout), "Pool intervals must be positive and timer-compatible.");
    }
}

/// <summary>A bounded pool of native Db2 sessions with asynchronous session leases.</summary>
public sealed class Db2Pool : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<Db2Session>> _connectionFactory;
    private readonly ConcurrentQueue<Db2Session> _idle = new();
    private readonly object _idleGate = new();
    private readonly PoolAdmission _slots;
    private readonly Db2PoolOptions _options;
    private readonly ConcurrentDictionary<Db2Session, (long Born, long Returned)> _times = new();
    private readonly ConcurrentDictionary<Db2Session, byte> _active = new();
    private readonly Timer _reaper;
    private readonly CancellationTokenSource _closedSignal = new();
    private readonly TaskCompletionSource _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _sessionCount;
    private int _closed;

    public Db2Pool(string dsn, Db2PoolOptions? options = null)
        : this(token => Db2Session.ConnectAsync(dsn, token), options) { }

    public Db2Pool(Db2Config config, Db2PoolOptions? options = null)
        : this(token => Db2Session.ConnectAsync(config, token), options) { }

    internal Db2Pool(Func<CancellationToken, Task<Db2Session>> connectionFactory, Db2PoolOptions? options)
    {
        options ??= new Db2PoolOptions();
        if (options.MaxConnections < 1) throw new ArgumentOutOfRangeException(nameof(options), "Db2 pool MaxConnections must be a positive integer.");
        _connectionFactory = connectionFactory;
        options.Validate();
        _options = options;
        _slots = new PoolAdmission(options.MaxConnections, options.MaxWaiters);
        _reaper = new Timer(_ => { _ = ReapIdleAsync(); }, null, options.ReapInterval, options.ReapInterval);
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public int SessionCount => Volatile.Read(ref _sessionCount);
    public int IdleSessionCount { get { lock (_idleGate) return _idle.Count; } }

    public int WaitingCount => _slots.WaitingCount;
    public int ActiveSessionCount => _active.Count;

    private bool Expired(Db2Session session, bool includeIdle)
    {
        if (!_times.TryGetValue(session, out var time)) return false;
        return Stopwatch.GetElapsedTime(time.Born) >= _options.MaxLifetime
            || includeIdle && Stopwatch.GetElapsedTime(time.Returned) >= _options.IdleTimeout;
    }

    private async Task ReapIdleAsync()
    {
        try
        {
            List<Db2Session> expired = [];
            lock (_idleGate)
            {
                var count = _idle.Count;
                for (var i = 0; i < count; i++) if (_idle.TryDequeue(out var session))
                {
                    if (session.IsClosed || Expired(session, true)) { expired.Add(session); Interlocked.Decrement(ref _sessionCount); }
                    else _idle.Enqueue(session);
                }
            }
            foreach (var session in expired)
            {
                _times.TryRemove(session, out _); Db2Diagnostics.Discard();
                try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
        catch { /* Timer cleanup cannot throw into the application. */ }
    }

    public async Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(30);
        if (budget < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        await DisposeAsync().ConfigureAwait(false);
        var start = Stopwatch.GetTimestamp();
        while (!_active.IsEmpty && Stopwatch.GetElapsedTime(start) < budget)
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        foreach (var session in _active.Keys) await session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Runs one operation on a pooled session and returns a healthy session to the pool.</summary>
    public async Task<T> WithSessionAsync<T>(Func<Db2Session, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        using var activity = Db2Diagnostics.Begin("execute");
        var executeStarted = Stopwatch.GetTimestamp();
        Exception? primary = null;
        T result;
        try { result = await operation(lease.Session, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            // The pool still attempts its reset so server state is cleaned, but a reset
            // failure is attached as cleanup context instead of masking the operation
            // error. Uncertain DML is never retried automatically.
            Db2Diagnostics.Record("execute", executeStarted, false);
            primary = ex;
            await lease.DisposeAsync(primary).ConfigureAwait(false);
            throw;
        }
        Db2Diagnostics.Record("execute", executeStarted, true);
        await lease.DisposeAsync(null).ConfigureAwait(false);
        return result;
    }

    /// <summary>Acquires one physical session for a connection that must retain it across commands.</summary>
    internal async Task<Db2SessionLease> AcquireSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        var acquireStarted = Stopwatch.GetTimestamp();
        using var activity = Db2Diagnostics.Begin("acquire");
        try { await _slots.AcquireAsync(_options.AcquireTimeout, cancellationToken).ConfigureAwait(false); }
        catch { Db2Diagnostics.Record("acquire", acquireStarted, false); throw; }
        Db2Diagnostics.Record("acquire", acquireStarted, true);
        Db2Session? session = null;
        try
        {
            ThrowIfClosed();
            List<Db2Session>? broken = null;
            lock (_idleGate)
            {
                while (_idle.TryDequeue(out var candidate))
                {
                    if (!candidate.IsClosed && !Expired(candidate, true)) { session = candidate; break; }
                    Interlocked.Decrement(ref _sessionCount);
                    (broken ??= []).Add(candidate);
                }
            }
            Exception? brokenDisposalError = null;
            if (broken is not null) foreach (var candidate in broken)
            {
                _times.TryRemove(candidate, out _);
                Db2Diagnostics.Discard();
                try { await candidate.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { brokenDisposalError ??= error; }
            }
            if (brokenDisposalError is not null) throw brokenDisposalError;
            if (session is null)
            {
                using var connectBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectBudget.CancelAfter(_options.ConnectTimeout);
                var connectStarted = Stopwatch.GetTimestamp();
                using var connectActivity = Db2Diagnostics.Begin("connect");
                var pending = _connectionFactory(connectBudget.Token);
                try { session = await pending.WaitAsync(connectBudget.Token).ConfigureAwait(false); }
                catch
                {
                    Db2Diagnostics.Record("connect", connectStarted, false);
                    _ = pending.ContinueWith(async task => { if (task.IsCompletedSuccessfully) { try { await task.Result.DisposeAsync().ConfigureAwait(false); } catch { } } else _ = task.Exception; }, TaskScheduler.Default).Unwrap();
                    throw;
                }
                Db2Diagnostics.Record("connect", connectStarted, true);
                _times[session] = (Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp());
                Interlocked.Increment(ref _sessionCount);
            }
            ThrowIfClosed();
            if (!session.HasPoolBaseline)
            {
                // Record CURRENT SCHEMA/ISOLATION before the session reaches its first
                // caller. A closed pool skips the baseline because the session is
                // discarded without a lease. The acquire catch below discards the
                // session when the baseline read itself fails.
                await session.CapturePoolBaselineAsync(cancellationToken).ConfigureAwait(false);
            }
            if (_times.TryGetValue(session, out var times) && Stopwatch.GetElapsedTime(times.Returned) > TimeSpan.FromSeconds(60))
                await session.QueryAsync("VALUES 1", cancellationToken).ConfigureAwait(false);
            _active[session] = 0;
            return new Db2SessionLease(this, session);
        }
        catch (Exception primaryError)
        {
            try
            {
                if (session is not null)
                {
                    Interlocked.Decrement(ref _sessionCount);
                    _times.TryRemove(session, out _);
                    try { await session.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanupError) { AttachCleanupError(primaryError, cleanupError); }
                }
            }
            finally { _slots.Release(); }
            ExceptionDispatchInfo.Capture(primaryError).Throw();
            throw;
        }
    }

    internal async ValueTask ReturnSessionAsync(Db2Session session, Exception? primaryError = null)
    {
        try
        {
            Exception? cleanupError = null;
            if (!IsClosed && !session.IsClosed && !Expired(session, false))
            {
                var resetStarted = Stopwatch.GetTimestamp();
                using var resetActivity = Db2Diagnostics.Begin("reset");
                try { await session.ResetForPoolAsync().ConfigureAwait(false); Db2Diagnostics.Record("reset", resetStarted, true); }
                catch (Exception resetError) { cleanupError = resetError; Db2Diagnostics.Record("reset", resetStarted, false); }
            }
            var dispose = false;
            lock (_idleGate)
            {
                if (IsClosed || session.IsClosed || Expired(session, false) || cleanupError is not null)
                {
                    Interlocked.Decrement(ref _sessionCount);
                    dispose = true;
                }
                else
                {
                    var born = _times.TryGetValue(session, out var time) ? time.Born : Stopwatch.GetTimestamp();
                    _times[session] = (born, Stopwatch.GetTimestamp());
                    _idle.Enqueue(session);
                }
            }
            if (dispose)
            {
                _times.TryRemove(session, out _);
                Db2Diagnostics.Discard();
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeError)
                {
                    if (cleanupError is Db2AbandonedTransactionException abandoned)
                        AttachCleanupError(abandoned, disposeError);
                    else
                        cleanupError = cleanupError is null
                            ? disposeError
                            : new AggregateException("DB2 session reset and disposal both failed.", cleanupError, disposeError);
                }
            }
            if (cleanupError is not null && primaryError is not null) AttachCleanupError(primaryError, cleanupError);
            else if (cleanupError is Db2AbandonedTransactionException) ExceptionDispatchInfo.Capture(cleanupError).Throw();
        }
        finally { _active.TryRemove(session, out _); if (IsClosed || session.IsClosed || Expired(session, false)) _times.TryRemove(session, out _); _slots.Release(); }
    }

    private static void AttachCleanupError(Exception primaryError, Exception cleanupError)
    {
        try
        {
            if (primaryError.Data["Db2CleanupError"] is Exception previous)
                primaryError.Data["Db2CleanupError"] = new AggregateException("Multiple DB2 cleanup operations failed.", previous, cleanupError);
            else
                primaryError.Data["Db2CleanupError"] = cleanupError;
        }
        catch { /* Cleanup reporting must never replace the primary operation failure. */ }
    }

    public Task<Db2QueryResult> QueryAsync(string sql, CancellationToken cancellationToken = default) =>
        WithSessionAsync((session, token) => session.QueryAsync(sql, token), cancellationToken);

    public Task<Db2QueryResult> QueryAsync(string sql, Db2CommandOptions options, CancellationToken cancellationToken = default) =>
        WithSessionAsync((session, token) => session.QueryAsync(sql, options, token), cancellationToken);

    public Task<Db2QueryResult> QueryAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default) =>
        WithSessionAsync((session, token) => session.QueryAsync(sql, parameters, token), cancellationToken);

    public Task<Db2QueryResult> QueryAsync(string sql, IReadOnlyList<object?> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default) =>
        WithSessionAsync((session, token) => session.QueryAsync(sql, parameters, options, token), cancellationToken);

    /// <summary>Streams rows while keeping a bounded pool lease until enumeration ends.</summary>
    public IAsyncEnumerable<Db2StreamRow> QueryStreamAsync(string sql, CancellationToken cancellationToken = default) =>
        QueryStreamCoreAsync(sql, null, null, cancellationToken);

    public IAsyncEnumerable<Db2StreamRow> QueryStreamAsync(string sql, Db2CommandOptions options, CancellationToken cancellationToken = default) =>
        QueryStreamCoreAsync(sql, null, options, cancellationToken);

    public IAsyncEnumerable<Db2StreamRow> QueryStreamAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default) =>
        QueryStreamCoreAsync(sql, parameters, null, cancellationToken);

    public IAsyncEnumerable<Db2StreamRow> QueryStreamAsync(string sql, IReadOnlyList<object?> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default) =>
        QueryStreamCoreAsync(sql, parameters, options, cancellationToken);

    private async IAsyncEnumerable<Db2StreamRow> QueryStreamCoreAsync(string sql, IReadOnlyList<object?>? parameters, Db2CommandOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var streamLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lease = await AcquireSessionAsync(streamLifetime.Token).ConfigureAwait(false);
        var token = streamLifetime.Token;
        IAsyncEnumerator<Db2StreamRow>? rows = null;
        Exception? primary = null;
        var completed = false;
        try
        {
            try
            {
                var session = lease.Session;
                var source = parameters is null
                    ? options is null ? session.QueryStreamAsync(sql, token) : session.QueryStreamAsync(sql, options, token)
                    : options is null ? session.QueryStreamAsync(sql, parameters, token) : session.QueryStreamAsync(sql, parameters, options, token);
                rows = source.GetAsyncEnumerator(token);
            }
            catch (Exception error) { primary = error; throw; }
            while (true)
            {
                bool available;
                try { available = await rows.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception error) { primary = error; throw; }
                if (!available) { completed = true; break; }
                yield return rows.Current;
            }
        }
        finally
        {
            // Preserve the previous producer's cancellation on early disposal,
            // including SQLINTR and the existing uncertain-cursor discard policy.
            streamLifetime.Cancel();
            try { if (rows is not null) await rows.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupError)
            {
                if (primary is not null) AttachCleanupError(primary, cleanupError);
                else
                {
                    primary = cleanupError;
                    // The former producer completed its channel with disposal errors.
                    // A consumer that stopped early never observed that completion.
                    if (completed) throw;
                }
            }
            finally
            {
                try { await lease.DisposeAsync(primary).ConfigureAwait(false); }
                catch when (!completed && primary is null) { /* Preserve unobserved early-disposal cleanup. */ }
            }
        }
    }

    public Task<long> ExecuteAsync(string sql, CancellationToken cancellationToken = default) =>
        WithSessionAsync((session, token) => session.ExecuteAsync(sql, token), cancellationToken);

    public Task<long> ExecuteAsync(string sql, Db2CommandOptions options, CancellationToken cancellationToken = default) =>
        WithSessionAsync((session, token) => session.ExecuteAsync(sql, options, token), cancellationToken);

    public Task<long> ExecuteAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default) =>
        WithSessionAsync((session, token) => session.ExecuteAsync(sql, parameters, token), cancellationToken);

    public Task<long> ExecuteAsync(string sql, IReadOnlyList<object?> parameters, Db2CommandOptions options, CancellationToken cancellationToken = default) =>
        WithSessionAsync((session, token) => session.ExecuteAsync(sql, parameters, options, token), cancellationToken);

    /// <summary>Executes one Db2 administrative command through a pooled session.</summary>
    public async Task<long> ExecuteAdminCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        var normalized = Db2AdminCommand.Normalize(command);
        return await WithSessionAsync((session, token) => session.ExecuteAdminCommandAsync(normalized, token), cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> ExecuteBatchAsync(string sql, IReadOnlyList<IReadOnlyList<object?>> parameterSets, CancellationToken cancellationToken = default)
        => await ExecuteBatchCoreAsync(sql, parameterSets, null, cancellationToken).ConfigureAwait(false);

    public Task<long> ExecuteBatchAsync(string sql, IReadOnlyList<IReadOnlyList<object?>> parameterSets, Db2CommandOptions options, CancellationToken cancellationToken = default) =>
        ExecuteBatchCoreAsync(sql, parameterSets, options, cancellationToken);

    private async Task<long> ExecuteBatchCoreAsync(string sql, IReadOnlyList<IReadOnlyList<object?>> parameterSets, Db2CommandOptions? options, CancellationToken cancellationToken)
    {
        var operationStarted = false;
        try
        {
            return await WithSessionAsync((session, token) =>
            {
                operationStarted = true;
                return options is null ? session.ExecuteBatchAsync(sql, parameterSets, token) : session.ExecuteBatchAsync(sql, parameterSets, options, token);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !operationStarted)
        {
            throw new Db2BatchCancellationException(cancellationToken, 0, null, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            await _closeCompletion.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            _closedSignal.Cancel();
            _slots.Close();
            _reaper.Dispose();
            List<Db2Session> idle = [];
            lock (_idleGate) while (_idle.TryDequeue(out var session)) idle.Add(session);
            List<Exception>? disposalErrors = null;
            foreach (var session in idle)
            {
                Interlocked.Decrement(ref _sessionCount);
                _times.TryRemove(session, out _);
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { (disposalErrors ??= []).Add(error); }
            }
            if (disposalErrors is not null) throw new AggregateException("One or more idle DB2 sessions failed to dispose.", disposalErrors);
            _closeCompletion.TrySetResult();
        }
        catch (Exception error)
        {
            _closeCompletion.TrySetException(error);
            throw;
        }
    }

    private void ThrowIfClosed()
    {
        if (IsClosed) throw new ObjectDisposedException(nameof(Db2Pool));
    }
}

internal sealed class Db2SessionLease(Db2Pool pool, Db2Session session) : IAsyncDisposable
{
    private int _returned;
    public Db2Session Session { get; } = session;

    public ValueTask DisposeAsync() => DisposeAsync(null);

    public ValueTask DisposeAsync(Exception? primaryError) => Interlocked.Exchange(ref _returned, 1) == 0
        ? pool.ReturnSessionAsync(Session, primaryError)
        : ValueTask.CompletedTask;
}
