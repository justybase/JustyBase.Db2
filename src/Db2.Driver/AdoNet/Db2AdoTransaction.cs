using System.Data;
using System.Data.Common;

namespace JustyBase.Db2.AdoNet;

/// <summary>A transaction pinned to the physical session owned by its DbConnection.</summary>
public sealed class Db2AdoTransaction : DbTransaction
{
    private readonly Db2AdoConnection _connection;
    private readonly Db2Transaction _transaction;
    private readonly IsolationLevel _isolationLevel;
    private int _completed;

    internal Db2AdoTransaction(Db2AdoConnection connection, Db2Transaction transaction, IsolationLevel isolationLevel)
    {
        _connection = connection;
        _transaction = transaction;
        _isolationLevel = isolationLevel;
    }

    public override IsolationLevel IsolationLevel => _isolationLevel;
    protected override DbConnection DbConnection => _connection;

    public override void Commit() => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        try
        {
            await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Complete();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Complete();
            throw;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            Complete();
            throw _connection.TranslateProviderError(error);
        }
    }

    public override void Rollback() => RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        try
        {
            await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            Complete();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Complete();
            throw;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            Complete();
            throw _connection.TranslateProviderError(error);
        }
    }

    internal async Task RollbackFromConnectionCloseAsync()
    {
        if (Volatile.Read(ref _completed) != 0) return;
        try
        {
            await _transaction.RollbackAsync().ConfigureAwait(false);
            Complete();
        }
        catch
        {
            Complete();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Volatile.Read(ref _completed) == 0)
            Rollback();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _completed) == 0) await RollbackAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void EnsureActive()
    {
        if (Volatile.Read(ref _completed) != 0) throw new InvalidOperationException("The DB2 transaction has completed.");
        if (!ReferenceEquals(_connection.CurrentTransaction, this)) throw new InvalidOperationException("The DB2 transaction is no longer active on its connection.");
    }

    private void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0) _connection.CompleteTransaction(this);
    }
}
