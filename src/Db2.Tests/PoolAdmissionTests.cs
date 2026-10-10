using Xunit;
namespace JustyBase.Db2.Tests;

public sealed class PoolAdmissionTests
{
    [Fact]
    public async Task AdmissionIsFifoAndRejectsExcessWaiters()
    {
        var gate = new PoolAdmission(1, 2);
        await gate.AcquireAsync(TimeSpan.FromSeconds(1), default);
        var first = gate.AcquireAsync(TimeSpan.FromSeconds(1), default);
        var second = gate.AcquireAsync(TimeSpan.FromSeconds(1), default);
        await Assert.ThrowsAsync<Db2PoolExhaustedException>(() => gate.AcquireAsync(TimeSpan.FromSeconds(1), default));
        gate.Release(); await first;
        Assert.False(second.IsCompleted);
        gate.Release(); await second;
        Assert.Equal(0, gate.WaitingCount);
        gate.Release(); gate.Close();
    }
    [Fact]
    public async Task TimeoutAndCancellationRemoveOnlyTheirWaiter()
    {
        var gate = new PoolAdmission(1, 2);
        await gate.AcquireAsync(TimeSpan.FromSeconds(1), default);
        await Assert.ThrowsAsync<Db2PoolTimeoutException>(() => gate.AcquireAsync(TimeSpan.FromMilliseconds(10), default));
        using var cts = new CancellationTokenSource();
        var waiting = gate.AcquireAsync(TimeSpan.FromSeconds(1), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, gate.WaitingCount);
        gate.Release();
        await gate.AcquireAsync(TimeSpan.FromSeconds(1), default);
        gate.Release(); gate.Close();
    }
    [Fact]
    public async Task CloseRejectsQueuedAdmission()
    {
        var gate = new PoolAdmission(1, 1);
        await gate.AcquireAsync(TimeSpan.FromSeconds(1), default);
        var waiting = gate.AcquireAsync(TimeSpan.FromSeconds(1), default);
        gate.Close();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting);
        gate.Release();
    }
}
