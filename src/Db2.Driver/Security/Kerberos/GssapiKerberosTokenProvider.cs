namespace JustyBase.Db2.Security.Kerberos;

/// <summary>
/// Real GSS-API Kerberos exchange via the system MIT library. Credentials come from a per-call
/// credential store (ccache or client keytab) or from the platform default; no process-global
/// Kerberos state is touched. Native calls can block on the KDC, so they run on the thread pool and
/// the caller's token/timeout stops the wait. If a wait is abandoned, the in-flight native call
/// finishes on its own and the handles are released only afterwards.
/// </summary>
internal sealed class GssapiKerberosTokenProvider(KerberosCredentialPlan plan) : IKerberosTokenProvider
{
    private readonly object _gate = new();
    private readonly GssContextHandle _context = new();
    private GssNameHandle? _principal;
    private GssNameHandle? _target;
    private GssCredentialHandle? _credential;
    // Keytab connections keep their ticket in a private in-memory cache, never in the default cache.
    private readonly string? _privateCache = plan.Source == KerberosCredentialSource.Keytab ? "MEMORY:JustyBase.Db2-" + Guid.NewGuid().ToString("N") : null;
    private int _active;
    private bool _disposeRequested;
    private int _released;

    public bool RequiresServerToken => true;

    public Task<byte[]> CreateInitialTokenAsync(CancellationToken cancellationToken) => RunAsync(CreateInitialToken, cancellationToken);

    public Task CompleteAsync(byte[] serverToken, CancellationToken cancellationToken) =>
        RunAsync(() => { Complete(serverToken); return true; }, cancellationToken);

    private async Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            _active++;
        }
        Task<T> task;
        try
        {
            task = Task.Run(() =>
            {
                try { return work(); }
                finally { Leave(); }
            }, CancellationToken.None);
        }
        catch
        {
            Leave();
            throw;
        }
        // If the wait is abandoned (cancellation/timeout) the native call may still fail later; observe that failure.
        _ = task.ContinueWith(static finished => _ = finished.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Leave()
    {
        bool release;
        lock (_gate)
        {
            _active--;
            release = _disposeRequested && _active == 0;
        }
        if (release) ReleaseHandles();
    }

    public void Dispose()
    {
        bool release;
        lock (_gate)
        {
            _disposeRequested = true;
            release = _active == 0;
        }
        if (release) ReleaseHandles();
    }

    private void ReleaseHandles()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        _context.Dispose();
        _target?.Dispose();
        _principal?.Dispose();
        _credential?.Dispose();
        if (_privateCache is not null) Krb5Native.TryDestroyCache(_privateCache);
    }

    private byte[] CreateInitialToken()
    {
        try { return CreateInitialTokenCore(); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new Db2UnsupportedFeatureException("Kerberos needs the MIT Kerberos libraries (libgssapi_krb5.so.2 and libkrb5.so.3, for example Debian/Ubuntu package libgssapi-krb5-2) with credential-store support (krb5 1.11 or newer).");
        }
    }

    private byte[] CreateInitialTokenCore()
    {
        nint principalNative = 0;
        if (plan.Principal is not null)
        {
            var major = GssapiNative.ImportPrincipal(plan.Principal, out var minor, out var imported);
            if (major != GssapiNative.Complete) throw Fail("client principal import", major, minor);
            _principal = GssNameHandle.FromNative(imported);
            principalNative = imported;
        }

        // An explicit principal also narrows the platform default cache (for example a cache collection).
        if (plan.Source is KerberosCredentialSource.CredentialCache or KerberosCredentialSource.Keytab || principalNative != 0)
        {
            var ccache = plan.Source == KerberosCredentialSource.CredentialCache ? plan.CredentialStoreName : _privateCache;
            var keytab = plan.Source == KerberosCredentialSource.Keytab ? plan.CredentialStoreName : null;
            var major = GssapiNative.AcquireInitiatorCredential(principalNative, ccache, keytab, out var minor, out var acquired);
            if (major != GssapiNative.Complete) throw Fail("credential acquisition", major, minor);
            _credential = GssCredentialHandle.FromNative(acquired);
        }

        var targetMajor = GssapiNative.ImportPrincipal(plan.ServicePrincipal!, out var targetMinor, out var target);
        if (targetMajor != GssapiNative.Complete) throw Fail("service principal import", targetMajor, targetMinor);
        _target = GssNameHandle.FromNative(target);

        nint context = 0;
        var contextMajor = GssapiNative.InitSecurityContext(_credential?.DangerousGetHandle() ?? 0, ref context, target, [],
            GssapiNative.MutualFlag, out var contextMinor, out var token);
        _context.Assign(context);
        if ((contextMajor & GssapiNative.ErrorMask) != 0) throw Fail("service ticket / security context", contextMajor, contextMinor);
        if (token.Length == 0) throw new Db2KerberosException("Kerberos security context produced no initial token.");
        return token;
    }

    private void Complete(byte[] serverToken)
    {
        if (_context.IsInvalid || _target is null) throw new InvalidOperationException("The Kerberos context has not produced an initial token.");
        var context = _context.DangerousGetHandle();
        var major = GssapiNative.InitSecurityContext(_credential?.DangerousGetHandle() ?? 0, ref context, _target.DangerousGetHandle(), serverToken,
            GssapiNative.MutualFlag, out var minor, out _);
        // On an error MIT deletes the context itself and nulls the handle; do not delete it a second time.
        if (context == 0) _context.SetHandleAsInvalid();
        if ((major & GssapiNative.ErrorMask) != 0) throw Fail("server authentication (mutual)", major, minor);
        if (major != GssapiNative.Complete)
            throw new Db2KerberosException("Kerberos mutual authentication needs more exchange rounds than DRDA security mechanism 11 carries.");
    }

    private static Db2KerberosException Fail(string stage, uint major, uint minor) =>
        new($"Kerberos {stage} failed: {GssapiNative.DescribeStatus(major, minor)} (GSS major=0x{major:x8}, minor={minor}).");
}
