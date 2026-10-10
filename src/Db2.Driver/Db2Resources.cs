namespace JustyBase.Db2;

/// <summary>A shared quota reserved before writes, never by probing free disk space.</summary>
internal sealed class Db2ResourceBudget
{
    private long _used;
    public long LimitBytes { get; }
    public long UsedBytes => Interlocked.Read(ref _used);
    public Db2ResourceBudget(long limitBytes = 10L * 1024 * 1024 * 1024)
    {
        if (limitBytes < 1) throw new ArgumentOutOfRangeException(nameof(limitBytes));
        LimitBytes = limitBytes;
    }
    internal void Reserve(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        for (;;)
        {
            var previous = UsedBytes;
            if (bytes > LimitBytes - previous) throw new Db2ResourceLimitException("DB2 process spool quota exceeded.");
            if (Interlocked.CompareExchange(ref _used, previous + bytes, previous) == previous) return;
        }
    }
    internal void Release(long bytes) => Interlocked.Add(ref _used, -bytes);
    internal static Db2ResourceBudget Spool { get; } = new();
}

/// <summary>Private, bounded spool owned by one row/LOB; asynchronous disposal releases disk first.</summary>
internal sealed class Db2LobSpool : IAsyncDisposable
{
    internal const long MaxValueBytes = 1024L * 1024 * 1024;
    private readonly string _directory;
    private readonly Db2ResourceBudget _budget;
    private long _reserved;
    private Task? _disposal;
    private readonly object _disposeGate = new();
    internal FileStream File { get; }
    internal Db2LobSpool(Db2ResourceBudget? budget = null)
    {
        _budget = budget ?? Db2ResourceBudget.Spool;
        _directory = Directory.CreateTempSubdirectory($"JustyBase.Db2-spool-{Environment.ProcessId}-").FullName;
        if (!OperatingSystem.IsWindows()) System.IO.File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            File = new FileStream(Path.Combine(_directory, "lob.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            if (!OperatingSystem.IsWindows()) System.IO.File.SetUnixFileMode(File.Name, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { Directory.Delete(_directory, true); throw; }
    }
    internal async Task AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (_disposeGate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (bytes.Length > MaxValueBytes - _reserved) throw new Db2ResourceLimitException("DB2 LOB exceeds streamed-value limit.");
            _budget.Reserve(bytes.Length); _reserved += bytes.Length;
        }
        while (!bytes.IsEmpty)
        {
            var count = Math.Min(bytes.Length, 64 * 1024);
            await File.WriteAsync(bytes[..count], token).ConfigureAwait(false); bytes = bytes[count..];
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        Exception? cleanupError = null;
        try { await File.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { cleanupError = error; }
        try { Directory.Delete(_directory, true); }
        catch (Exception error) { cleanupError ??= error; }
        finally
        {
            var reserved = _reserved;
            _reserved = 0;
            _budget.Release(reserved);
        }
        if (cleanupError is not null) throw cleanupError;
    }
}
