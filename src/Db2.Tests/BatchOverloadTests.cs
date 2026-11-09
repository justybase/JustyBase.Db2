using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class BatchOverloadTests
{
    [Fact]
    public void BatchOverloadsBindExplicitNullCommandOptionsWithoutAmbiguity()
    {
        Db2Session session = null!;
        // The lambdas are never invoked; compiling them guards overload
        // resolution. An explicit null command-options argument must bind to the
        // command overload rather than make the Db2BatchOptions overload a
        // better candidate.
        Func<Task<long>> commandOnly = () => session.ExecuteBatchAsync(
            "SELECT 1", Array.Empty<IReadOnlyList<object?>>(), null!, CancellationToken.None);
        Func<Task<long>> combined = () => session.ExecuteBatchAsync(
            "SELECT 1", Array.Empty<IReadOnlyList<object?>>(), new Db2BatchOptions(), null, CancellationToken.None);
        Func<Task<long>> batchWithToken = () => session.ExecuteBatchAsync(
            "SELECT 1", Array.Empty<IReadOnlyList<object?>>(), new Db2BatchOptions(),
            cancellationToken: CancellationToken.None);
        Assert.NotNull(commandOnly);
        Assert.NotNull(combined);
        Assert.NotNull(batchWithToken);
    }
}
