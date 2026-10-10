using JustyBase.Db2;

namespace JustyBase.Db2.Tests;

internal static class LiveTestCleanup
{
    public static bool IsMissingObjectException(Exception error) =>
        error is IDb2Error { SqlCode: -204, SqlState: "42704" };

    public static async Task TryDropMissingObjectAsync(Func<Task> dropAsync)
    {
        try
        {
            await dropAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (IsMissingObjectException(error))
        {
            // A failed Db2 statement can roll back the fixture creation itself.
        }
    }

    public static async Task RunPreservingFailureAsync(Exception? testFailure, Func<Task> cleanupAsync)
    {
        try
        {
            await cleanupAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure) when (testFailure is not null)
        {
            throw new AggregateException(
                "The test failed and its fixture cleanup also failed.",
                testFailure,
                cleanupFailure);
        }
    }
}
