using System.Data;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Types;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class ReaderCleanupErrorTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task CleanupFailureDoesNotReplacePrimaryReadError(bool initialization, bool synchronous, bool defaultTranslator = false)
    {
        var primary = new Db2SqlException(-811, "21000", "primary query error");
        var cleanup = new IOException("secondary cleanup error");
        using var connection = new Db2AdoConnection();
        using var command = new Db2AdoCommand();
        using var cancellation = new CancellationTokenSource();
        Db2ColumnMetadata[] columns = [new("ID", SqlType.Integer, 4, 0, 0, false)];
        await using var reader = new Db2AdoDataReader(command, connection, columns,
            new FailingRows(columns, primary, cleanup, initialization), CommandBehavior.Default,
            defaultTranslator ? static error => Db2AdoException.From(error) : static error => error, cancellation);
        Exception observed;
        if (initialization)
            observed = await Record.ExceptionAsync(() => reader.InitializeAsync()) ?? throw new Xunit.Sdk.XunitException("Expected read failure.");
        else
        {
            await reader.InitializeAsync();
            Assert.True(await reader.ReadAsync()); Assert.Equal(42, reader.GetInt32(0));
            observed = synchronous
                ? Record.Exception(() => reader.Read()) ?? throw new Xunit.Sdk.XunitException("Expected read failure.")
                : await Record.ExceptionAsync(() => reader.ReadAsync()) ?? throw new Xunit.Sdk.XunitException("Expected read failure.");
        }
        if (defaultTranslator)
        {
            var translated = Assert.IsType<Db2AdoException>(observed);
            Assert.Equal(-811, translated.SqlCode);
            Assert.Equal("21000", translated.SqlState);
            Assert.Same(primary, translated.InnerException);
        }
        else Assert.Same(primary, observed);
        if (defaultTranslator)
        {
            var translatedCleanup = Assert.IsType<Db2AdoException>(observed.Data["Db2CleanupError"]);
            Assert.Same(cleanup, translatedCleanup.InnerException);
        }
        else Assert.Same(cleanup, observed.Data["Db2CleanupError"]);
        Assert.True(reader.IsClosed);
    }

    private sealed class FailingRows(IReadOnlyList<Db2ColumnMetadata> columns, Exception primary, Exception cleanup,
        bool initialization) : IAsyncEnumerator<Db2StreamRow>
    {
        private int _moves;
        public Db2StreamRow Current => new(columns, [42]);
        public ValueTask<bool> MoveNextAsync() => !initialization && _moves++ == 0
            ? ValueTask.FromResult(true) : ValueTask.FromException<bool>(primary);
        public ValueTask DisposeAsync() => ValueTask.FromException(cleanup);
    }
}
