namespace JustyBase.Db2;

/// <summary>FIFO admission with bounded waiters; cancellation never consumes capacity.</summary>
internal sealed class PoolAdmission(int capacity, int maxWaiters)
{
    private sealed class Waiter
    {
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Waiter>? Node;
    }
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _waiting = new();
    private int _available = capacity;
    private bool _closed;
    internal int WaitingCount { get { lock (_gate) return _waiting.Count; } }

    internal async Task AcquireAsync(TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_gate)
        {
            if (_closed) throw new ObjectDisposedException(nameof(Db2Pool));
            if (_available > 0 && _waiting.Count == 0) { _available--; return; }
            if (_waiting.Count >= maxWaiters) throw new Db2PoolExhaustedException();
            waiter = new Waiter();
            waiter.Node = _waiting.AddLast(waiter);
        }
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        using var registration = linked.Token.Register(() =>
        {
            lock (_gate)
            {
                if (waiter.Node?.List is null) return;
                _waiting.Remove(waiter.Node);
                if (token.IsCancellationRequested) waiter.Completion.TrySetCanceled(token);
                else waiter.Completion.TrySetException(new Db2PoolTimeoutException());
            }
        });
        await waiter.Completion.Task.ConfigureAwait(false);
    }

    internal void Release()
    {
        lock (_gate)
        {
            if (_waiting.First is { } node)
            {
                _waiting.RemoveFirst();
                node.Value.Completion.TrySetResult();
            }
            else _available++;
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            _closed = true;
            while (_waiting.First is { } node)
            {
                _waiting.RemoveFirst();
                node.Value.Completion.TrySetException(new ObjectDisposedException(nameof(Db2Pool)));
            }
        }
    }
}
