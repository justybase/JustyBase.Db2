using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class LiveTestCleanupTests
{
    [Theory]
    [InlineData(-204, "42704", true)]
    [InlineData(-204, "42703", false)]
    [InlineData(-803, "23505", false)]
    public void MissingObjectDetectionUsesSqlCodeAndState(int sqlCode, string sqlState, bool expected)
    {
        var error = new Db2TestErrorException(sqlCode, sqlState);

        Assert.Equal(expected, LiveTestCleanup.IsMissingObjectException(error));
    }

    [Fact]
    public async Task TryDropMissingObjectIgnoresOnlyMissingObjectErrors()
    {
        await LiveTestCleanup.TryDropMissingObjectAsync(
            () => Task.FromException(new Db2TestErrorException(-204, "42704")));

        var failure = new InvalidOperationException("cleanup failed");
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LiveTestCleanup.TryDropMissingObjectAsync(() => Task.FromException(failure)));

        Assert.Same(failure, observed);
    }

    [Fact]
    public async Task CleanupFailureDoesNotReplaceTheOriginalTestFailure()
    {
        var testFailure = new InvalidOperationException("test failed");
        var cleanupFailure = new InvalidOperationException("cleanup failed");

        var observed = await Assert.ThrowsAsync<AggregateException>(() =>
            LiveTestCleanup.RunPreservingFailureAsync(testFailure, () => Task.FromException(cleanupFailure)));

        Assert.Collection(observed.InnerExceptions,
            error => Assert.Same(testFailure, error),
            error => Assert.Same(cleanupFailure, error));
    }

    [Fact]
    public async Task CleanupFailureRemainsTheFailureWhenTheTestSucceeded()
    {
        var cleanupFailure = new InvalidOperationException("cleanup failed");

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LiveTestCleanup.RunPreservingFailureAsync(null, () => Task.FromException(cleanupFailure)));

        Assert.Same(cleanupFailure, observed);
    }

    private sealed class Db2TestErrorException(int? sqlCode, string? sqlState) : Exception("test Db2 error"), IDb2Error
    {
        public Db2ErrorKind ErrorKind => Db2ErrorKind.SqlError;
        public int? SqlCode { get; } = sqlCode;
        public string? SqlState { get; } = sqlState;
    }
}
