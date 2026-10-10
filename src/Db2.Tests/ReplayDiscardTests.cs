using System.Data;
using Db2Replay;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class ReplayDiscardTests
{
    [Fact]
    public async Task NoProgressReadErrorPrecedesLateCleanupSqlCardError()
    {
        var fixture = new ReplayFixture("Int", 20000);
        await using var server = new ReplayServer(fixture, new(NoProgressOnce: true,
            LateSqlCode: -811, LateSqlState: "21000"));
        await using var session = await Db2Session.ConnectAsync(ReplayInfrastructureTests.Dsn(server.Port));
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in session.QueryStreamAsync(fixture.Sql)) { }
        });
        Assert.Contains("neither query data", error.Message);
        Assert.True(session.IsClosed);
        server.ThrowIfFailed();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task PoolReturnsAndReusesTheSameSessionAfterEarlyDispose(int take)
    {
        var fixture = new ReplayFixture("Int", 20000);
        await using var server = new ReplayServer(fixture);
        await using var pool = new Db2Pool(ReplayInfrastructureTests.Dsn(server.Port), new Db2PoolOptions(1));
        Db2Session first;
        await using (var lease = await pool.AcquireSessionAsync())
        {
            first = lease.Session;
            var count = 0;
            await foreach (var row in first.QueryStreamAsync(fixture.Sql))
            {
                Assert.Equal(++count, row.Values[0]);
                if (count == take) break;
            }
        }
        Assert.Equal(1, pool.IdleSessionCount);
        await using (var lease = await pool.AcquireSessionAsync())
        {
            Assert.Same(first, lease.Session);
            long sum = 0;
            await foreach (var row in lease.Session.QueryStreamAsync(fixture.Sql)) sum += (int)row.Values[0]!;
            Assert.Equal(fixture.IdChecksum, sum);
        }
        Assert.Equal(0, server.Counters.ActiveQueries);
    }

    [Fact]
    public async Task CancellationDuringDrainWaitsForCompletionAndPreservesSessionBoundary()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new ReplayFixture("Int", 20000);
        await using var server = new ReplayServer(fixture, new(BeforeContinue: token =>
        {
            started.TrySetResult();
            return release.Task.WaitAsync(token);
        }));
        await using var session = await Db2Session.ConnectAsync(ReplayInfrastructureTests.Dsn(server.Port));
        using var cancellation = new CancellationTokenSource();
        var iterator = session.QueryStreamAsync(fixture.Sql, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        var drain = iterator.DisposeAsync().AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(drain.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, server.Counters.ActiveQueries);
        // Early disposal ends the operation registration. Its bounded cleanup
        // completes independently of subsequent caller cancellation and SQLINTR.
        long sum = 0;
        await foreach (var row in session.QueryStreamAsync(fixture.Sql)) sum += (int)row.Values[0]!;
        Assert.Equal(fixture.IdChecksum, sum);
    }

    [Fact]
    public async Task ClosingConnectionDuringDrainInvalidatesTransport()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new ReplayFixture("Int", 20000);
        await using var server = new ReplayServer(fixture, new(BeforeContinue: token =>
        {
            started.TrySetResult();
            return release.Task.WaitAsync(token);
        }));
        await using var session = await Db2Session.ConnectAsync(ReplayInfrastructureTests.Dsn(server.Port));
        var iterator = session.QueryStreamAsync(fixture.Sql).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        var drain = iterator.DisposeAsync().AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => drain.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsNotType<TimeoutException>(error);
        Assert.True(session.IsClosed);
        // Peer shutdown cancels its intentionally blocked fetch without a send.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyDisposeReportsLateSqlCardError(bool synchronous)
    {
        var fixture = new ReplayFixture("Int", 20000);
        await using var server = new ReplayServer(fixture, new(LateSqlCode: -811, LateSqlState: "21000"));
        await using var connection = new Db2AdoConnection(ReplayInfrastructureTests.Dsn(server.Port));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = fixture.Sql;
        var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync()); Assert.Equal(1, reader.GetInt32(0));
        var error = synchronous
            ? Assert.Throws<Db2AdoException>(() => reader.Dispose())
            : await Assert.ThrowsAsync<Db2AdoException>(() => reader.DisposeAsync().AsTask());
        Assert.Equal(-811, error.SqlCode); Assert.Equal("21000", error.SqlState);
        Assert.True(reader.IsClosed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyDisposeReportsRowSqlcaErrorAndMalformedRow(bool malformed)
    {
        var fixture = new ReplayFixture("Int", 20000);
        await using var server = new ReplayServer(fixture, new(LastRowSqlCode: malformed ? 0 : -811, MalformedLastRow: malformed));
        await using var session = await Db2Session.ConnectAsync(ReplayInfrastructureTests.Dsn(server.Port));
        var iterator = session.QueryStreamAsync(fixture.Sql).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync()); Assert.Equal(1, iterator.Current.Values[0]);
        if (malformed)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => iterator.DisposeAsync().AsTask());
            Assert.True(session.IsClosed);
        }
        else
        {
            var error = await Assert.ThrowsAsync<Db2SqlException>(() => iterator.DisposeAsync().AsTask());
            Assert.Equal(-811, error.SqlCode); Assert.Equal("21000", error.SqlState);
            Assert.True(session.IsClosed);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DisposeRejectsTruncatedFinalRowIncludingAlreadyEndedFirstFetch(bool singleFetch, bool sqlCode100)
    {
        var fixture = new ReplayFixture("Int", singleFetch ? 10 : 20000);
        await using var server = new ReplayServer(fixture, new(TruncateLastRow: true, EndWithSqlCode100: sqlCode100));
        await using var connection = new Db2AdoConnection(ReplayInfrastructureTests.Dsn(server.Port));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = fixture.Sql;
        var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var error = await Assert.ThrowsAsync<Db2AdoException>(() => reader.DisposeAsync().AsTask());
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal(ConnectionState.Broken, connection.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainReportsLateWarningsAndKeepsConnectionReusable(bool rowWarning)
    {
        var fixture = new ReplayFixture("Int", 20000);
        await using var server = new ReplayServer(fixture, rowWarning
            ? new(LastRowSqlCode: 12) : new(LateSqlCode: 12, LateSqlState: "01003"));
        await using var connection = new Db2AdoConnection(ReplayInfrastructureTests.Dsn(server.Port));
        var warnings = new List<Db2Warning>();
        connection.InfoMessage += (_, e) => warnings.AddRange(e.Errors);
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = fixture.Sql;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); Assert.Empty(warnings);
        }
        var warning = Assert.Single(warnings);
        Assert.Equal(12, warning.SqlCode); Assert.Equal("01003", warning.SqlState);
        Assert.Equal(ConnectionState.Open, connection.State);
        await using var next = connection.CreateCommand(); next.CommandText = fixture.Sql;
        Assert.Equal(1, await next.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainPreservesAutocommitAndExplicitTransaction(bool transaction)
    {
        var fixture = new ReplayFixture("Int", 20000);
        await using var server = new ReplayServer(fixture);
        await using var session = await Db2Session.ConnectAsync(ReplayInfrastructureTests.Dsn(server.Port));
        await using var scope = transaction ? await session.BeginTransactionAsync() : null;
        var before = server.Counters.Commits;
        await foreach (var row in session.QueryStreamAsync(fixture.Sql)) { Assert.Equal(1, row.Values[0]); break; }
        Assert.Equal(!transaction, session.AutoCommit);
        Assert.Equal(before, server.Counters.Commits);
        if (scope is not null) await scope.CommitAsync();
        else await session.CommitAsync();
        Assert.True(session.AutoCommit);
        Assert.Equal(before + 1, server.Counters.Commits);
        Assert.Equal(0, server.Counters.ActiveQueries);
    }
}
