using Xunit;
namespace JustyBase.Db2.Tests;
public sealed class ProgressiveLobBufferedTests
{
    [Fact]
    public void SynchronousBufferedReadsAllocateZeroAndKeepPosition()
    {
        var chunk = new byte[16 * 1101]; var destination = new byte[16]; var fetches = 0;
        using var stream = new Db2ProgressiveLobStream(chunk.Length, _ => { fetches++; return ValueTask.FromResult((chunk, true)); });
        for (var i = 0; i < 101; i++) Assert.Equal(16, stream.Read(destination, 0, 16));
        var before = GC.GetAllocatedBytesForCurrentThread(); var total = 0;
        for (var i = 0; i < 1000; i++) total += stream.Read(destination, 0, 16);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(16000, total); Assert.Equal(chunk.Length, stream.Position); Assert.Equal(1, fetches);
        Assert.Equal(0, stream.Read(destination, 0, 16));
    }
    [Fact]
    public async Task SynchronousReaderSharesGateWithPendingFetch()
    {
        var pending = new TaskCompletionSource<(byte[], bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stream = new Db2ProgressiveLobStream(2, _ => new(pending.Task));
        var firstByte = new byte[1]; var secondByte = new byte[1];
        var first = stream.ReadAsync(firstByte).AsTask();
        var second = Task.Run(() => stream.Read(secondByte, 0, 1));
        pending.SetResult(([1, 2], true));
        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, firstByte[0]); Assert.Equal(2, secondByte[0]); Assert.Equal(2, stream.Position);
    }

    [Fact]
    public async Task ReadCancellationRequestsQueryInterruptAndWaitsForTheDb2Reply()
    {
        var pending = new TaskCompletionSource<(byte[], bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetchStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        await using var stream = new Db2ProgressiveLobStream(
            1,
            token =>
            {
                fetchStarted.TrySetResult(token);
                return new(pending.Task);
            },
            () =>
            {
                interruptRequested.TrySetResult();
                pending.TrySetException(new Db2SqlException(-952, "57014", "DB2 interrupted the LOB fetch."));
            });

        var read = stream.ReadAsync(new byte[1], cancellation.Token).AsTask();
        var fetchToken = await fetchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<Db2SqlException>(() => read);
        Assert.Equal(-952, error.SqlCode);
        Assert.True(interruptRequested.Task.IsCompleted);
        Assert.False(fetchToken.CanBeCanceled, "the read token must request SQLINTR without aborting the primary reply read");
    }
}
