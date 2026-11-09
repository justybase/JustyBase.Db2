using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class PoolStatsTests
{
    [Fact]
    public async Task NewPoolExposesZeroedCountersAndAllowsZeroWaiters()
    {
        await using var pool = new Db2Pool(
            _ => throw new InvalidOperationException("factory must not run"),
            new Db2PoolOptions(1) { MaxWaiters = 0 });
        Assert.Equal(0, pool.DiscardedSessionCount);
        Assert.Equal(0, pool.AcquireTimeoutCount);
        Assert.Equal(0, pool.WaitingCount);
    }

    [Fact]
    public async Task AcquireDeadlineIncrementsTheTimeoutCounter()
    {
        var options = new Db2PoolOptions(1) { AcquireTimeout = TimeSpan.FromMilliseconds(50) };
        await using var pool = new Db2Pool(
            async (CancellationToken token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            },
            options);
        using var firstCancellation = new CancellationTokenSource();
        var first = pool.WithSessionAsync((_, _) => Task.FromResult(0), firstCancellation.Token);
        await Assert.ThrowsAsync<Db2PoolTimeoutException>(
            () => pool.WithSessionAsync((_, _) => Task.FromResult(0)));
        Assert.Equal(1, pool.AcquireTimeoutCount);
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }
}
