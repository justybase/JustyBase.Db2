using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed partial class SessionTests
{
    [Fact]
    public async Task SessionConnectsPingsExecutesAndDecodesQueryAgainstMockServer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeAsync(listener);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using (var session = await Db2Session.ConnectAsync(config))
        {
            await session.PingAsync();
            Assert.Equal(1, await session.ExecuteAsync("VALUES 1"));
        var query = await session.QueryAsync("VALUES CAST(42 AS INTEGER)");
        Assert.Empty(query.Columns);
        Assert.Equal(42, Assert.IsType<int>(Assert.Single(Assert.Single(query.Rows))));
        var statement = await session.PrepareAsync("VALUES CAST(? AS INTEGER)");
        Assert.Single(statement.ParameterColumns);
        var parameterQuery = await statement.QueryAsync([42]);
        Assert.Equal(42, Assert.IsType<int>(Assert.Single(Assert.Single(parameterQuery.Rows))));
        }
        await serverTask;
    }

    [Fact]
    public async Task HandshakeRejectsServerChangingTheRequestedSecurityMechanism()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var initial = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC }, initial.Select(packet => packet.CodePoint));
            var requested = DdmCodec.ParseParameters(initial[1].Payload);
            Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16BigEndian(requested[(ushort)CodePoint.SECMEC]));
            await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 4));
            var buffer = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        });

        var config = TestConfig(port);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Db2Session.ConnectAsync(config));

        Assert.Contains("selected security mechanism 4", error.Message, StringComparison.Ordinal);
        await serverTask;
    }

    [Fact]
    public async Task SetClientInfoStripsControlCharactersBeforeSendingSqlAndUpdatingCache()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
                (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
            Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
                (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
            await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
                (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

            var update = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
                update.Select(packet => packet.CodePoint));
            var sqlPacket = Assert.Single(update, packet => packet.CodePoint == (ushort)CodePoint.SQLSTT);
            var sqlLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(sqlPacket.Payload.AsSpan(1, 4)));
            var sql = System.Text.Encoding.UTF8.GetString(sqlPacket.Payload, 5, sqlLength);
            Assert.Equal("SET CLIENT APPLNAME 'audit''app'", sql);
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        });

        await using var session = await Db2Session.ConnectAsync(TestConfig(port));
        await session.SetClientInfoAsync(new Db2ClientInfo(ApplicationName: "audit\r\n'app"));

        Assert.Equal("audit'app", session.GetClientInfo().ApplicationName);
        await serverTask;
    }

    [Fact]
    public async Task ClosedSessionUsesStableDb2ErrorClassification()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = ServeConnectionOnlyAsync(listener);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, Database = "TESTDB", User = "db2inst1", Password = "secret",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        await session.DisposeAsync();

        var error = await Assert.ThrowsAsync<Db2ConnectionClosedException>(() => session.PingAsync());
        Assert.Equal(Db2ErrorKind.ConnectionClosed, Assert.IsAssignableFrom<IDb2Error>(error).ErrorKind);
        Assert.Equal("DB2 connection is closed.", error.Message);
        await serverTask;
    }

    [Fact]
    public async Task ConnectMapsRdbnfNrmReplyToDatabaseNotFound()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeFailureAsync(listener, WireErrorStage.Negotiation, CodePoint.RDBNFNRM, ReadOnlyMemory<byte>.Empty);

        var error = await Assert.ThrowsAsync<Db2DatabaseNotFoundException>(() => Db2Session.ConnectAsync(TestConfig(port)));

        Assert.Equal(Db2ErrorKind.DatabaseNotFound, Assert.IsAssignableFrom<IDb2Error>(error).ErrorKind);
        Assert.Equal("DB2 relational database was not found.", error.Message);
        await serverTask;
    }

    [Fact]
    public async Task ConnectMapsSecchkcdAndDoesNotExposeCredentials()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeFailureAsync(listener, WireErrorStage.Authentication, CodePoint.SECCHKRM,
            DdmCodec.PackBytes(CodePoint.SECCHKCD, [8]));

        var error = await Assert.ThrowsAsync<Db2AuthenticationException>(() => Db2Session.ConnectAsync(TestConfig(port)));

        Assert.Equal(Db2ErrorKind.Authentication, Assert.IsAssignableFrom<IDb2Error>(error).ErrorKind);
        Assert.Equal((byte)8, error.SecurityCheckCode);
        Assert.Contains("SECCHKCD=8", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        await serverTask;
    }

    [Theory]
    [InlineData(CodePoint.RDBAFLRM)]
    [InlineData(CodePoint.RDBATHRM)]
    private static async Task ConnectMapsAuthorizationReplies(CodePoint reply)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeFailureAsync(listener, WireErrorStage.Authentication, reply, ReadOnlyMemory<byte>.Empty);

        var error = await Assert.ThrowsAsync<Db2AuthorizationException>(() => Db2Session.ConnectAsync(TestConfig(port)));

        Assert.Equal(Db2ErrorKind.Authorization, Assert.IsAssignableFrom<IDb2Error>(error).ErrorKind);
        Assert.Equal("DB2 authorization failed while connecting to the database.", error.Message);
        await serverTask;
    }

    [Fact]
    public async Task ConnectMapsSqlerrrmDiagnosticDuringHandshake()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const string diagnostic = "SQLCODE:-204 SQLSTATE:42704 object not found";
        var serverTask = ServeFailureAsync(listener, WireErrorStage.Authentication, CodePoint.SQLERRRM,
            DdmCodec.PackBytes(CodePoint.SRVDGN, System.Text.Encoding.UTF8.GetBytes(diagnostic)));

        var error = await Assert.ThrowsAsync<Db2ServerErrorException>(() => Db2Session.ConnectAsync(TestConfig(port)));

        Assert.Equal(Db2ErrorKind.SqlError, Assert.IsAssignableFrom<IDb2Error>(error).ErrorKind);
        Assert.Equal(-204, error.SqlCode);
        Assert.Equal("42704", error.SqlState);
        Assert.Equal(diagnostic, error.Message);
        await serverTask;
    }

    [Fact]
    public async Task ConnectMapsSqlcardErrorDuringSessionInitialization()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const string detail = "invalid workstation name";
        var serverTask = ServeFailureAsync(listener, WireErrorStage.Initialization, CodePoint.SQLCARD, SqlCardWithMessage(-104, "42601", detail));

        var error = await Assert.ThrowsAsync<Db2SqlException>(() => Db2Session.ConnectAsync(TestConfig(port)));

        Assert.Equal(Db2ErrorKind.SqlError, Assert.IsAssignableFrom<IDb2Error>(error).ErrorKind);
        Assert.Equal(-104, error.SqlCode);
        Assert.Equal("42601", error.SqlState);
        Assert.Equal($"DB2 SQLCODE=-104 SQLSTATE=42601 {detail}", error.Message);
        await serverTask;
    }

    [Theory]
    [InlineData(CodePoint.SQLCARD)]
    [InlineData(CodePoint.SQLERRRM)]
    private static async Task ExecuteMapsSqlcardAndSqlerrrmReplies(CodePoint replyCode)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const string diagnostic = "SQLCODE=-911 SQLSTATE=40001 deadlock";
        var payload = replyCode == CodePoint.SQLCARD
            ? SqlCardWithMessage(-911, "40001", "deadlock")
            : DdmCodec.PackBytes(CodePoint.SRVDGN, System.Text.Encoding.UTF8.GetBytes(diagnostic));
        var serverTask = ServeFailureAsync(listener, WireErrorStage.Execute, replyCode, payload);
        await using var session = await Db2Session.ConnectAsync(TestConfig(port));

        IDb2Error error = replyCode switch
        {
            CodePoint.SQLCARD => Assert.IsAssignableFrom<IDb2Error>(await Assert.ThrowsAsync<Db2SqlException>(() => session.ExecuteAsync("VALUES 1"))),
            _ => Assert.IsAssignableFrom<IDb2Error>(await Assert.ThrowsAsync<Db2ServerErrorException>(() => session.ExecuteAsync("VALUES 1"))),
        };

        Assert.Equal(Db2ErrorKind.SqlError, error.ErrorKind);
        Assert.Equal(-911, error.SqlCode);
        Assert.Equal("40001", error.SqlState);
        await serverTask;
    }

    [Fact]
    public async Task PoolCloseRejectsWaitersWithoutInterruptingActiveLease()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeConnectionOnlyAsync(listener, servePoolBaseline: true);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var pool = new Db2Pool(config, new Db2PoolOptions(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Db2Session? activeSession = null;
        var active = pool.WithSessionAsync(async (session, _) =>
        {
            activeSession = session;
            entered.SetResult();
            await finishOperation.Task;
            return 7;
        });

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = pool.QueryAsync("VALUES CAST(42 AS INTEGER)");
        var waitingFailure = Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        await pool.DisposeAsync();
        await waitingFailure;
        Assert.False(active.IsCompleted);

        finishOperation.SetResult();
        Assert.Equal(7, await active);
        Assert.True(activeSession!.IsClosed);
        Assert.Equal(0, pool.SessionCount);
        await serverTask;
    }

    [Fact]
    public async Task PoolCancellationTokenCancelsQueuedWaiterWithoutInterruptingActiveLease()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeConnectionOnlyAsync(listener, servePoolBaseline: true);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var pool = new Db2Pool(config, new Db2PoolOptions(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Db2Session? activeSession = null;
        var active = pool.WithSessionAsync(async (session, _) =>
        {
            activeSession = session;
            entered.SetResult();
            await finishOperation.Task;
            return 29;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancellation = new CancellationTokenSource();
        var invoked = false;
        var waiting = pool.WithSessionAsync((_, _) =>
        {
            invoked = true;
            return Task.FromResult(42);
        }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));

        using var batchCancellation = new CancellationTokenSource();
        IReadOnlyList<IReadOnlyList<object?>> parameterSets = [new object?[] { 42 }];
        var waitingBatch = pool.ExecuteBatchAsync("INSERT INTO T VALUES (?)", parameterSets, batchCancellation.Token);
        batchCancellation.Cancel();
        var batchCancellationError = await Assert.ThrowsAsync<Db2BatchCancellationException>(() => waitingBatch.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, batchCancellationError.CompletedParameterSets);
        Assert.Null(batchCancellationError.InFlightParameterSetIndex);
        Assert.Equal(0, batchCancellationError.RowsAffected);

        Assert.False(invoked, "a canceled waiter must not execute on the active session");
        Assert.False(active.IsCompleted, "canceling a queued waiter must not interrupt the active lease");
        Assert.False(activeSession!.IsClosed);
        Assert.Equal(1, pool.SessionCount);

        await pool.DisposeAsync();
        finishOperation.SetResult();
        Assert.Equal(29, await active);
        Assert.True(activeSession.IsClosed);
        Assert.Equal(0, pool.SessionCount);
        await serverTask;
    }

    [Fact]
    public async Task PoolCloseDrainsConnectionThatFinishesOpeningAfterClose()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var handshakeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueHandshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeDelayedConnectionAsync(listener, handshakeStarted, continueHandshake.Task);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(5), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var pool = new Db2Pool(config, new Db2PoolOptions(1));

        var opening = pool.QueryAsync("VALUES 1");
        await handshakeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await pool.DisposeAsync();
        continueHandshake.SetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await opening.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, pool.SessionCount);
        await serverTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PoolCloseDuringSessionResetDisposesTheReleasingSession(bool resetFails)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var resetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServePoolResetAsync(listener, resetStarted, continueReset.Task, resetFails);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var pool = new Db2Pool(config, new Db2PoolOptions(1));
        var operation = pool.WithSessionAsync(async (session, cancellationToken) =>
        {
            _ = await session.BeginTransactionAsync();
            return 29;
        });

        await resetStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await pool.DisposeAsync();
        continueReset.SetResult();

        var abandoned = await Assert.ThrowsAsync<Db2AbandonedTransactionException>(() => operation);
        if (resetFails) Assert.Equal(-911, Assert.IsType<Db2SqlException>(abandoned.RollbackError).SqlCode);
        else Assert.Null(abandoned.RollbackError);
        Assert.Equal(0, pool.SessionCount);
        Assert.Equal(0, pool.IdleSessionCount);
        await serverTask;
    }

    [Fact]
    public async Task PoolCloseDuringReplacementHandshakeAfterResetFailureDisposesTheLateSession()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var resetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementHandshakeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueReplacementHandshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServePoolResetFailureThenDelayedReplacementAsync(
            listener,
            resetStarted,
            continueReset.Task,
            replacementHandshakeStarted,
            continueReplacementHandshake.Task);
        await using var pool = new Db2Pool(TestConfig(port), new Db2PoolOptions(1));

        var releasing = pool.WithSessionAsync(async (session, _) =>
        {
            await session.SetClientInfoAsync(new Db2ClientInfo(ApplicationName: "temporary-pool-state"));
            return 31;
        });
        await resetStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var replacementOpening = pool.QueryAsync("VALUES 1");
        Assert.False(replacementOpening.IsCompleted);
        continueReset.SetResult();
        Assert.Equal(31, await releasing.WaitAsync(TimeSpan.FromSeconds(5)));
        await replacementHandshakeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var queued = pool.QueryAsync("VALUES 2");
        await Task.Yield();
        Assert.False(queued.IsCompleted, "the second waiter must remain queued behind the replacement handshake");
        await pool.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued.WaitAsync(TimeSpan.FromSeconds(3)));
        continueReplacementHandshake.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => replacementOpening.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(0, pool.SessionCount);
        Assert.Equal(0, pool.IdleSessionCount);
        await serverTask;
    }

    [Fact]
    public async Task PoolCancellationDuringReplacementHandshakeAfterResetFailureDiscardsTheLateSession()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var resetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementHandshakeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueReplacementHandshake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServePoolResetFailureThenDelayedReplacementAsync(
            listener,
            resetStarted,
            continueReset.Task,
            replacementHandshakeStarted,
            continueReplacementHandshake.Task);
        await using var pool = new Db2Pool(TestConfig(port), new Db2PoolOptions(1));

        var releasing = pool.WithSessionAsync(async (session, _) =>
        {
            await session.SetClientInfoAsync(new Db2ClientInfo(ApplicationName: "temporary-pool-state"));
            return 31;
        });
        await resetStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource();
        var replacementOpening = pool.QueryAsync("VALUES 1", cancellation.Token);
        Assert.False(replacementOpening.IsCompleted, "the waiter must be queued behind the failed reset");
        continueReset.SetResult();
        Assert.Equal(31, await releasing.WaitAsync(TimeSpan.FromSeconds(5)));
        await replacementHandshakeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => replacementOpening.WaitAsync(TimeSpan.FromSeconds(3)));
        continueReplacementHandshake.SetResult();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (pool.SessionCount != 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

        Assert.Equal(0, pool.SessionCount);
        Assert.Equal(0, pool.IdleSessionCount);
        await serverTask;
    }

    [Fact]
    public async Task PoolReplacesSessionAfterClientInfoResetFailureAndServesQueuedOperation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var resetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServePoolResetFailureRecoveryAsync(listener, resetStarted, continueReset.Task);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var pool = new Db2Pool(config, new Db2PoolOptions(1));

        var releasing = pool.WithSessionAsync(async (session, _) =>
        {
            await session.SetClientInfoAsync(new Db2ClientInfo(ApplicationName: "temporary-pool-state"));
            return 29;
        });
        await resetStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var waiting = pool.QueryAsync("VALUES 1");
        Assert.False(waiting.IsCompleted, "the only pool slot remains occupied until failed reset cleanup finishes");
        continueReset.SetResult();

        Assert.Equal(29, await releasing.WaitAsync(TimeSpan.FromSeconds(5)));
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42, Assert.IsType<int>(Assert.Single(Assert.Single(result.Rows))));
        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(1, pool.IdleSessionCount);

        await pool.DisposeAsync();
        Assert.Equal(0, pool.SessionCount);
        await serverTask;
    }

    [Fact]
    public async Task PoolDiscardsSessionWhenRegisterRestoreFails()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeRegisterRestoreFailureAsync(listener);
        await using var pool = new Db2Pool(TestConfig(port), new Db2PoolOptions(1));

        Assert.Equal(29, await pool.WithSessionAsync(async (session, token) =>
        {
            await session.ExecuteAsync("SET CURRENT SCHEMA 'MOCKSCHEMA'", cancellationToken: token);
            return 29;
        }));
        Assert.Equal(0, pool.SessionCount);
        Assert.Equal(0, pool.IdleSessionCount);

        await pool.DisposeAsync();
        await serverTask;
    }

    [Fact]
    public async Task PoolRestoresRawClientRegisterMutationThatBypassesLocalCache()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeRawClientRegisterPoolResetAsync(listener);
        await using var pool = new Db2Pool(TestConfig(port), new Db2PoolOptions(1));

        try
        {
            await pool.WithSessionAsync(async (session, _) =>
            {
                await session.ExecuteAsync("SET CLIENT APPLNAME 'raw_application'");
                return true;
            });
        }
        catch
        {
            await serverTask;
            throw;
        }

        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(1, pool.IdleSessionCount);
        await pool.DisposeAsync();
        await serverTask;
    }

    [Fact]
    public async Task PoolRestoresRawSessionAuthorizationMutationThatBypassesLocalCache()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeRawSessionAuthorizationPoolResetAsync(listener);
        await using var pool = new Db2Pool(TestConfig(port), new Db2PoolOptions(1));

        await pool.WithSessionAsync(async (session, _) =>
        {
            await session.ExecuteAsync("SET SESSION AUTHORIZATION = \"OTHERUSER\"");
            return true;
        });

        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(1, pool.IdleSessionCount);
        await pool.DisposeAsync();
        await serverTask;
    }

    [Fact]
    public async Task PoolProbesAndRestoresStateChangedBehindCallBeforeReusingSession()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = ServeCallMutationAndReuseAsync(listener);
        await using var pool = new Db2Pool(TestConfig(((IPEndPoint)listener.LocalEndpoint).Port), new Db2PoolOptions(1));

        await pool.WithSessionAsync(async (session, _) =>
        {
            await session.ExecuteAsync("CALL MUTATE_CLIENT_STATE()");
            return true;
        });

        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(1, pool.IdleSessionCount);
        var result = await pool.QueryAsync("VALUES 29");
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single(result.Rows))));
        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(1, pool.IdleSessionCount);
        await pool.DisposeAsync();
        await serverTask;
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("client-register")]
    public async Task PoolDiscardsSessionWhenObservedStateRestoreIsDenied(string stateKind)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serverTask = ServeDeniedPoolStateRestoreAsync(listener, stateKind);
        await using var pool = new Db2Pool(TestConfig(((IPEndPoint)listener.LocalEndpoint).Port), new Db2PoolOptions(1));

        Assert.Equal(29, await pool.WithSessionAsync(async (session, token) =>
        {
            await session.ExecuteAsync("SET CURRENT SCHEMA 'MOCKSCHEMA'", cancellationToken: token);
            return 29;
        }));
        Assert.Equal(0, pool.SessionCount);
        Assert.Equal(0, pool.IdleSessionCount);
        await pool.DisposeAsync();
        await serverTask;
    }

    [Fact]
    public async Task AdoProcedureReaderStreamsMultipleAndEmptyCursorSetsAndDrainsOnClose()
    {
        await Db2AdoConnection.ClearAllPoolsAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeAdoProcedureCursorStreamAsync(listener, withOutputParameters: true);
        var connectionString = $"Server=127.0.0.1:{port};Database=TESTDB;UID=db2inst1;PWD=secret;Pooling=true;MaxPoolSize=1;Block_Size=65535";
        var protocolTrace = new List<string>();
        try
        {
            await using var connection = new Db2AdoConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            ((Db2AdoCommand)command).ProcedureProtocolTrace = protocolTrace.Add;
            command.CommandText = "CALL STREAM_PROCEDURE(?, ?)";
            command.Parameters.Add(new Db2AdoParameter { ParameterName = "P", DbType = DbType.Int32, Value = 21 });
            command.Parameters.Add(new Db2AdoParameter { ParameterName = "RESULT", DbType = DbType.Int32, Direction = ParameterDirection.Output });
            DbDataReader reader;
            try { reader = await command.ExecuteReaderAsync(); }
            catch
            {
                await serverTask;
                throw;
            }
            Assert.Equal(1, reader.FieldCount);
            Assert.Equal("VALUE", reader.GetName(0));
            Assert.True(reader.HasRows);
            Assert.Null(command.Parameters["RESULT"].Value);
            Assert.True(await reader.ReadAsync());
            Assert.Equal(11, reader.GetInt32(0));
            Assert.True(await reader.NextResultAsync());
            Assert.True(reader.HasRows);
            Assert.True(await reader.ReadAsync());
            Assert.Equal(22, reader.GetInt32(0));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(23, reader.GetInt32(0));
            Assert.False(await reader.ReadAsync());
            Assert.True(await reader.NextResultAsync());
            Assert.Equal(1, reader.FieldCount);
            Assert.False(reader.HasRows);
            Assert.False(await reader.ReadAsync());
            Assert.False(await reader.NextResultAsync());
            Assert.Null(command.Parameters["RESULT"].Value);
            // A cancellation request after the terminal cursor block must not turn completed
            // procedure success into OperationCanceledException during reader disposal.
            command.Cancel();
            await reader.DisposeAsync();
            Assert.True(reader.IsClosed);
            Assert.False(((Db2AdoCommand)command).HasActiveReader);
            Assert.False(((Db2AdoCommand)command).HasActiveOperation);
            Assert.Equal(42, command.Parameters["RESULT"].Value);
            Assert.Contains(protocolTrace, entry => entry.Contains("codepoint=SQLCINRD", StringComparison.Ordinal));
            Assert.Contains(protocolTrace, entry => entry.Contains("source=SQLCINRD", StringComparison.Ordinal));
            Assert.DoesNotContain(protocolTrace, entry => entry.Contains("payload=", StringComparison.Ordinal));
            await connection.CloseAsync();
            await Db2AdoConnection.ClearAllPoolsAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await Db2AdoConnection.ClearAllPoolsAsync();
            listener.Stop();
        }
    }

    [Fact]
    public async Task AdoProcedureReaderDescribesCursorSectionBeforeNextResultReturns()
    {
        await Db2AdoConnection.ClearAllPoolsAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeAdoProcedureWithLateCursorNameAsync(listener);
        var connectionString = $"Server=127.0.0.1:{port};Database=TESTDB;UID=db2inst1;PWD=secret;Pooling=true;MaxPoolSize=1;Block_Size=65535";
        try
        {
            await using var connection = new Db2AdoConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            var protocolTrace = new List<string>();
            ((Db2AdoCommand)command).ProcedureProtocolTrace = protocolTrace.Add;
            command.CommandText = "CALL STREAM_LATE_NAME()";
            DbDataReader reader;
            try { reader = await command.ExecuteReaderAsync(); }
            catch
            {
                await serverTask;
                throw;
            }
            await using (reader)
            {
                Assert.Equal("FIRST_VALUE", reader.GetName(0));
                Assert.True(await reader.ReadAsync());
                Assert.Equal(11, reader.GetInt32(0));
                Assert.True(await reader.NextResultAsync());
                Assert.Equal("PAYLOAD", reader.GetName(0));
                Assert.Contains(protocolTrace, entry => entry.Contains("phase=describe", StringComparison.Ordinal)
                    && entry.Contains("codepoint=SQLDARD", StringComparison.Ordinal));
                Assert.Contains(protocolTrace, entry => entry.Contains("SQLCINRD parser offsets", StringComparison.Ordinal)
                    && entry.Contains("c1:fixed=", StringComparison.Ordinal)
                    && entry.Contains("optional=", StringComparison.Ordinal));
                Assert.True(await reader.ReadAsync());
                Assert.Equal(22, reader.GetInt32(0));
                Assert.False(await reader.NextResultAsync());
                await connection.CloseAsync();
                await Db2AdoConnection.ClearAllPoolsAsync();
                await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            await Db2AdoConnection.ClearAllPoolsAsync();
            listener.Stop();
        }
    }

    [Fact]
    public async Task AdoProcedureReaderEarlyCloseDrainsEveryCursorAndReturnsUsableSession()
    {
        await Db2AdoConnection.ClearAllPoolsAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeAdoProcedureCursorStreamAsync(listener);
        var connectionString = $"Server=127.0.0.1:{port};Database=TESTDB;UID=db2inst1;PWD=secret;Pooling=true;MaxPoolSize=1;Block_Size=65535";
        try
        {
            await using var connection = new Db2AdoConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CALL STREAM_PROCEDURE()";
            var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(11, reader.GetInt32(0));
            await reader.DisposeAsync();
            Assert.Equal(ConnectionState.Open, connection.State);
            await connection.CloseAsync();
            await Db2AdoConnection.ClearAllPoolsAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await Db2AdoConnection.ClearAllPoolsAsync();
            listener.Stop();
        }
    }

    [Fact]
    public async Task AdoProcedureReaderSupportsCallWithNoCursorSetsAndPublishesOutputsOnClose()
    {
        await Db2AdoConnection.ClearAllPoolsAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeAdoProcedureCursorStreamAsync(listener, withOutputParameters: true, zeroCursors: true);
        var connectionString = $"Server=127.0.0.1:{port};Database=TESTDB;UID=db2inst1;PWD=secret;Pooling=true;MaxPoolSize=1;Block_Size=65535";
        try
        {
            await using var connection = new Db2AdoConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CALL NO_CURSOR_PROCEDURE(?, ?)";
            command.Parameters.Add(new Db2AdoParameter { ParameterName = "P", DbType = DbType.Int32, Value = 21 });
            command.Parameters.Add(new Db2AdoParameter { ParameterName = "RESULT", DbType = DbType.Int32, Direction = ParameterDirection.Output });

            DbDataReader reader;
            try { reader = await command.ExecuteReaderAsync(); }
            catch
            {
                await serverTask;
                throw;
            }
            await using (reader)
            {
            Assert.Equal(0, reader.FieldCount);
            Assert.False(reader.HasRows);
            Assert.False(await reader.ReadAsync());
            Assert.False(await reader.NextResultAsync());
            Assert.Null(command.Parameters["RESULT"].Value);
            await reader.CloseAsync();
            Assert.Equal(42, command.Parameters["RESULT"].Value);
            }
            await connection.CloseAsync();
            await Db2AdoConnection.ClearAllPoolsAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await Db2AdoConnection.ClearAllPoolsAsync();
            listener.Stop();
        }
    }

    [Fact]
    public async Task PoolKeepsOperationErrorAndAttachesCleanupErrorWhenResetFails()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeOperationAndResetFailureAsync(listener);
        await using var pool = new Db2Pool(TestConfig(port), new Db2PoolOptions(1));

        var error = await Assert.ThrowsAsync<Db2ServerErrorException>(() =>
            pool.QueryAsync("VALUES CAST(1 AS INTEGER)"));
        Assert.Contains("-911", error.Message, StringComparison.Ordinal);
        var cleanup = Assert.IsType<Db2SqlException>(error.Data["Db2CleanupError"]);
        Assert.Contains("-911", cleanup.Message, StringComparison.Ordinal);
        Assert.Equal(0, pool.SessionCount);
        Assert.Equal(0, pool.IdleSessionCount);

        await pool.DisposeAsync();
        await serverTask;
    }

    private static async Task ServeRegisterRestoreFailureAsync(TcpListener listener)
    {
        using var client = await AcceptInitializedPoolClientAsync(listener);
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLIMM, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard(-911, "40001"));
        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static async Task ServeRawClientRegisterPoolResetAsync(TcpListener listener)
    {
        using var client = await AcceptInitializedPoolClientAsync(listener);
        await using var stream = client.GetStream();

        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLIMM, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS", applicationName: "raw_application");
        // The local cache still says the application name is empty. A restore command
        // here proves the reset path trusted the server probe and repaired the mutation.
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static async Task ServeRawSessionAuthorizationPoolResetAsync(TcpListener listener)
    {
        using var client = await AcceptInitializedPoolClientAsync(listener);
        await using var stream = client.GetStream();

        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLIMM, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS", sessionUser: "OTHERUSER");
        // The pool probes SESSION_USER, refreshes its cache, then issues the existing
        // switch-user operation back to the captured baseline identity.
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLIMM, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static async Task ServeCallMutationAndReuseAsync(TcpListener listener)
    {
        using var client = await AcceptInitializedPoolClientAsync(listener);
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.DSCSQLSTT },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLDARD, ReadOnlyMemory<byte>.Empty);
        Assert.Equal((ushort)CodePoint.EXCSQLSTT, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        Assert.Equal((ushort)CodePoint.RDBCMM, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        // The procedure mutates identity and a client register without a matching SQL string
        // in the client. The reset path must discover both through one actual-state probe.
        await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS", applicationName: "mutated-by-procedure", sessionUser: "OTHERUSER");
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLIMM, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0x02, 0, 4 }, true);
        var queryId = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(queryId, 1);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, queryId), true);
        await SendAsync(stream, CodePoint.QRYDTA, new byte[] { 0xff, 0, 29, 0, 0, 0 }, true);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
        await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS");
        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static async Task ServeDeniedPoolStateRestoreAsync(TcpListener listener, string stateKind)
    {
        using var client = await AcceptInitializedPoolClientAsync(listener);
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLIMM, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        if (stateKind == "identity")
        {
            await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS", sessionUser: "OTHERUSER");
            foreach (var _ in Enumerable.Range(0, 2))
            {
                Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLIMM, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
                    (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
                await SendAsync(stream, CodePoint.SQLCARD, SqlCard(-551, "42501"));
            }
        }
        else
        {
            await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS", applicationName: "denied-register");
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
                (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard(-551, "42501"));
        }
        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static async Task ServeAdoProcedureCursorStreamAsync(
        TcpListener listener,
        bool withOutputParameters = false,
        bool zeroCursors = false)
    {
        using var client = await AcceptInitializedPoolClientAsync(listener);
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.DSCSQLSTT },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        if (withOutputParameters)
        {
            var parameterDard = ProcedureInputParameterDard(2);
            Assert.Equal(2, DdmCodec.ParseSqlDard(parameterDard).Length);
            await SendAsync(stream, CodePoint.SQLDARD, parameterDard);
        }
        else await SendAsync(stream, CodePoint.SQLDARD, ReadOnlyMemory<byte>.Empty);

        var execute = await ReadChainAsync(stream);
        Assert.Equal(withOutputParameters
                ? new ushort[] { (ushort)CodePoint.EXCSQLSTT, (ushort)CodePoint.SQLDTA }
                : [(ushort)CodePoint.EXCSQLSTT],
            execute.Select(packet => packet.CodePoint));
        var sectionList = (zeroCursors ? Array.Empty<byte[]>() : new[]
        {
            DdmCodec.PackPackageNameCsn("TESTDB", "SYSSH200", "SYSLVL01", 65),
            DdmCodec.PackPackageNameCsn("TESTDB", "SYSSH200", "SYSLVL01", 66),
            DdmCodec.PackPackageNameCsn("TESTDB", "SYSSH200", "SYSLVL01", 67),
        }).SelectMany(section => section).ToArray();
        var cursorCount = zeroCursors ? 0 : 3;
        if (!zeroCursors)
        {
            var sectionPayload = DdmCodec.PackObject(CodePoint.PKGSNLST, sectionList);
            Assert.Equal(3, DdmCodec.ParseResultSetSections(sectionPayload).Length);
            await SendAsync(stream, CodePoint.RSLSETRM, sectionPayload, true);
        }
        var summary = new byte[2 + 12 * cursorCount];
        BinaryPrimitives.WriteUInt16LittleEndian(summary, checked((ushort)cursorCount));
        await SendAsync(stream, CodePoint.SQLRSLRD, summary, true);
        if (withOutputParameters)
        {
            byte[] descriptor = [9, 0x76, 0xd0, 0x04, 0, 3, 0x03, 0, 4];
            byte[] values = [0xff, 0, 21, 0, 0, 42, 0, 0, 0];
            var outputData = DdmCodec.PackObject(CodePoint.FDODSC, descriptor)
                .Concat(DdmCodec.PackObject(CodePoint.FDODTA, values)).ToArray();
            await SendAsync(stream, CodePoint.SQLDTARD, outputData, !zeroCursors);
        }

        if (!zeroCursors)
        {
            await SendCursorAsync(1, [11], end: true, endChained: true);
            await SendCursorAsync(2, [22, 23], end: true, endChained: true);
            await SendCursorAsync(3, [], end: true, endChained: false);
        }

        for (var index = 0; index < cursorCount; index++)
        {
            Assert.Equal((ushort)CodePoint.CLSQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        }
        Assert.Equal((ushort)CodePoint.RDBCMM, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS");
        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        async Task SendCursorAsync(ulong queryId, int[] values, bool end, bool endChained)
        {
            var id = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(id, queryId);
            // Each cursor has the same one-column INTEGER metadata. C1 and C2 carry rows;
            // C3 is deliberately empty to verify that its metadata still forms a result set.
            await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, id), true);
            await SendAsync(stream, CodePoint.SQLCINRD, ProcedureIntegerCursorInfo(), true);
            await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0x02, 0, 4 }, true);
            if (values.Length > 0)
            {
                using var rowBlock = new MemoryStream();
                foreach (var value in values)
                {
                    rowBlock.Write([0xff, 0]);
                    var encoded = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(encoded, value);
                    rowBlock.Write(encoded);
                }
                await SendAsync(stream, CodePoint.QRYDTA, rowBlock.ToArray(), true);
            }
            if (end) await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty, endChained);
        }
    }

    private static async Task ServeAdoProcedureWithLateCursorNameAsync(TcpListener listener)
    {
        using var client = await AcceptInitializedPoolClientAsync(listener);
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.DSCSQLSTT },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLDARD, ReadOnlyMemory<byte>.Empty);
        Assert.Equal((ushort)CodePoint.EXCSQLSTT, Assert.Single(await ReadChainAsync(stream)).CodePoint);

        var packageSections = new[]
        {
            DdmCodec.PackPackageNameCsn("TESTDB", "SYSSH200", "SYSLVL01", 65),
            DdmCodec.PackPackageNameCsn("TESTDB", "SYSSH200", "SYSLVL01", 66),
        }.SelectMany(section => section).ToArray();
        await SendAsync(stream, CodePoint.RSLSETRM, DdmCodec.PackObject(CodePoint.PKGSNLST, packageSections), true);
        var resultSummary = new byte[26];
        BinaryPrimitives.WriteUInt16LittleEndian(resultSummary, 2);
        await SendAsync(stream, CodePoint.SQLRSLRD, resultSummary, true);

        await SendCursorHeaderAsync(1, "FIRST_VALUE", endOfChain: true);
        await SendAsync(stream, CodePoint.QRYDTA, new byte[] { 0xff, 0, 11, 0, 0, 0 }, true);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty, true);
        await SendCursorHeaderAsync(2, "", endOfChain: false);

        Assert.Equal((ushort)CodePoint.CLSQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        var describe = await ReadChainAsync(stream);
        Assert.Equal((ushort)CodePoint.DSCSQLSTT, Assert.Single(describe).CodePoint);
        var namedDard = NamedIntegerSqlDard("PAYLOAD");
        Assert.Equal("PAYLOAD", Assert.Single(DdmCodec.ParseSqlDard(namedDard)).Name);
        await SendAsync(stream, CodePoint.SQLDARD, namedDard);
        var fetch = await ReadChainAsync(stream);
        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(fetch).CodePoint);
        await SendAsync(stream, CodePoint.QRYDTA, new byte[] { 0xff, 0, 22, 0, 0, 0 }, true);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);

        Assert.Equal((ushort)CodePoint.CLSQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        Assert.Equal((ushort)CodePoint.RDBCMM, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS");
        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        async Task SendCursorHeaderAsync(ulong queryId, string name, bool endOfChain)
        {
            var id = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(id, queryId);
            await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, id), true);
            await SendAsync(stream, CodePoint.SQLCINRD, ProcedureIntegerCursorInfo(name), true);
            await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0x02, 0, 4 }, endOfChain);
        }
    }

    private static byte[] NamedIntegerSqlDard(string name)
    {
        using var output = new MemoryStream();
        output.Write([0xff, 1, 1, 0]); // Legacy SQLDARD header and one SQLVAR.
        Span<byte> sqlvar = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(sqlvar[4..12], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(sqlvar[12..14], (ushort)SqlType.NInteger);
        output.Write(sqlvar);
        WriteFdocaStringPair(output, name);
        output.Write([0xff, 0xff, 0xff]);
        return output.ToArray();
    }

    private static byte[] ProcedureInputParameterDard(int count)
    {
        const int fixedColumnLength = 16;
        const int unnamedColumnTailLength = 29;
        var result = new byte[4 + count * (fixedColumnLength + unnamedColumnTailLength)];
        result[0] = 0xff;
        result[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), checked((ushort)count));
        var offset = 4;
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(offset + 4), 4);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(offset + 12), (ushort)SqlType.NInteger);
            offset += fixedColumnLength + unnamedColumnTailLength;
        }
        return result;
    }

    private static async Task ServeOperationAndResetFailureAsync(TcpListener listener)
    {
        using var client = await AcceptInitializedPoolClientAsync(listener);
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLERRRM,
            DdmCodec.PackBytes(CodePoint.SRVDGN, System.Text.Encoding.UTF8.GetBytes("SQLCODE=-911 SQLSTATE=40001 query failure")));
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard(-911, "40001"));
        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task PoolRecoversMultipleWaitersAfterResetTransportAndAuthenticationFailures()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var resetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int failedReplacements = 3;
        var serverTask = ServePoolResetFailureWithQueuedTransportAndAuthFailuresAsync(
            listener, resetStarted, continueReset.Task, failedReplacements);
        await using var pool = new Db2Pool(TestConfig(port), new Db2PoolOptions(1));

        var releasing = pool.WithSessionAsync(async (session, _) =>
        {
            await session.SetClientInfoAsync(new Db2ClientInfo(ApplicationName: "temporary-pool-state"));
            return 31;
        });
        await resetStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        async Task<(Db2QueryResult? Result, Exception? Error)> RunWaiterAsync()
        {
            try
            {
                return (await pool.QueryAsync("VALUES 1").WaitAsync(TimeSpan.FromSeconds(5)), null);
            }
            catch (Exception error)
            {
                return (null, error);
            }
        }

        var waiting = Enumerable.Range(0, failedReplacements + 1).Select(_ => RunWaiterAsync()).ToArray();
        Assert.All(waiting, task => Assert.False(task.IsCompleted, "all operations must queue behind the resetting session"));
        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(0, pool.IdleSessionCount);
        continueReset.SetResult();

        Assert.Equal(31, await releasing.WaitAsync(TimeSpan.FromSeconds(5)));
        var outcomes = await Task.WhenAll(waiting.Select(task => task.WaitAsync(TimeSpan.FromSeconds(8))));
        var failures = outcomes.Where(outcome => outcome.Error is not null).ToArray();
        var successes = outcomes.Where(outcome => outcome.Result is not null).ToArray();
        Assert.Equal(failedReplacements, failures.Length);
        var transportFailures = failures.Where(outcome => outcome.Error is IOException).ToArray();
        Assert.Equal(2, transportFailures.Length);
        Assert.All(transportFailures, outcome => Assert.IsAssignableFrom<IOException>(outcome.Error));
        var authenticationFailure = Assert.IsType<Db2AuthenticationException>(
            Assert.Single(failures, outcome => outcome.Error is Db2AuthenticationException).Error);
        Assert.Equal(8, authenticationFailure.SecurityCheckCode);
        var result = Assert.IsType<Db2QueryResult>(Assert.Single(successes).Result);
        Assert.Equal(42, Assert.IsType<int>(Assert.Single(Assert.Single(result.Rows))));
        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(1, pool.IdleSessionCount);

        await pool.DisposeAsync();
        Assert.Equal(0, pool.SessionCount);
        Assert.Equal(0, pool.IdleSessionCount);
        await serverTask;
    }

    [Fact]
    public async Task PoolReleasesCapacityAcrossMultipleQueuedAuthenticationFailures()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        const int failedConnections = 3;
        var serverTask = ServePoolOpenFailuresThenSuccessAsync(listener, failedConnections);
        await using var pool = new Db2Pool(TestConfig(port), new Db2PoolOptions(1));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, failedConnections + 1).Select(async _ =>
        {
            try
            {
                return (Lease: await pool.AcquireSessionAsync().WaitAsync(TimeSpan.FromSeconds(5)), Error: (Exception?)null);
            }
            catch (Exception error)
            {
                return (Lease: (Db2SessionLease?)null, Error: error);
            }
        }));

        var errors = outcomes.Where(outcome => outcome.Error is not null).Select(outcome => outcome.Error!).ToArray();
        Assert.Equal(failedConnections, errors.Length);
        Assert.All(errors, error => Assert.IsType<Db2AuthenticationException>(error));
        var lease = Assert.IsType<Db2SessionLease>(Assert.Single(outcomes.Where(outcome => outcome.Lease is not null).Select(outcome => outcome.Lease)));
        Assert.Equal(1, pool.SessionCount);
        Assert.Equal(0, pool.IdleSessionCount);

        await pool.DisposeAsync();
        await lease.DisposeAsync();
        Assert.Equal(0, pool.SessionCount);
        Assert.Equal(0, pool.IdleSessionCount);
        await serverTask;
    }

    private enum WireErrorStage { Negotiation, Authentication, Initialization, Execute }

    private static Db2Config TestConfig(int port) => new()
    {
        Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "secret",
        Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
    };

    private static byte[] SqlCardWithMessage(int code, string state, string message)
    {
        var messageBytes = System.Text.Encoding.UTF8.GetBytes(message);
        var result = new byte[58 + messageBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1), code);
        System.Text.Encoding.ASCII.GetBytes(state).CopyTo(result, 5);
        // SQLCARD's optional message groups follow the fixed 54-byte header.
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(54), 0);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(56), checked((ushort)messageBytes.Length));
        messageBytes.CopyTo(result, 58);
        return result;
    }

    private static async Task ServeFailureAsync(
        TcpListener listener,
        WireErrorStage stage,
        CodePoint responseCode,
        ReadOnlyMemory<byte> responsePayload)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));

        if (stage == WireErrorStage.Negotiation)
        {
            await SendAsync(stream, responseCode, responsePayload);
            return;
        }

        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        if (stage == WireErrorStage.Authentication)
        {
            if (responseCode == CodePoint.SECCHKRM)
                await SendAsync(stream, responseCode, responsePayload);
            else
            {
                await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
                await SendAsync(stream, responseCode, responsePayload);
            }
            return;
        }

        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        if (stage == WireErrorStage.Initialization)
        {
            await SendAsync(stream, responseCode, responsePayload);
            return;
        }

        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLIMM, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, responseCode, responsePayload);
    }

    private static async Task ServePoolOpenFailuresThenSuccessAsync(TcpListener listener, int failedConnections)
    {
        for (var attempt = 0; attempt < failedConnections; attempt++)
            await ServeFailureAsync(listener, WireErrorStage.Authentication, CodePoint.SECCHKRM,
                DdmCodec.PackBytes(CodePoint.SECCHKCD, [8]));

        using var client = await AcceptInitializedPoolClientAsync(listener);
        await using var stream = client.GetStream();
        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task ServeAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var initial = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC }, initial.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));

        var auth = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB }, auth.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);

        var setup = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, setup.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        var ping = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.RDBCMM }, ping.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        var execute = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLIMM, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, execute.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard(0, "00000", 1));

        var query = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY }, query.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0x02, 0, 4 }, true);
        var queryId = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(queryId, 1);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, queryId), true);
        await SendAsync(stream, CodePoint.QRYDTA, new byte[] { 0xff, 0, 42, 0, 0, 0 }, true);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);

        var prepare = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.DSCSQLSTT }, prepare.Select(packet => packet.CodePoint));
        var parameterDard = new byte[] { 0xff, 1, 1, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 241, 1, 0, 0 };
        await SendAsync(stream, CodePoint.SQLDARD, parameterDard, false);

        var parameterQuery = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.OPNQRY, (ushort)CodePoint.SQLDTA }, parameterQuery.Select(packet => packet.CodePoint));
        Assert.Equal(new byte[]
        {
            0x00,0x10,0x00,0x10,0x06,0x76,0xd0,0x03,0x00,0x04,0x06,0x71,0xe4,0xd0,0x00,0x01,
            0x00,0x0a,0x14,0x7a,0x00,0x00,0x2a,0x00,0x00,0x00,
        }, parameterQuery[1].Payload);
        await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0x02, 0, 4 }, true);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, queryId), true);
        await SendAsync(stream, CodePoint.QRYDTA, new byte[] { 0xff, 0, 42, 0, 0, 0 }, true);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
        Assert.Equal((ushort)CodePoint.RDBCMM, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedTransactionControlInvalidatesSession(bool commit)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var codePoint = commit ? CodePoint.RDBCMM : CodePoint.RDBRLLBCK;
        var serverTask = ServeTransactionControlFailureAsync(listener, codePoint);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        var transaction = await session.BeginTransactionAsync();
        var operation = commit ? transaction.CommitAsync() : transaction.RollbackAsync();
        var error = await Assert.ThrowsAsync<Db2SqlException>(() => operation);
        Assert.Equal(-911, error.SqlCode);
        Assert.Equal("40001", error.SqlState);
        Assert.True(session.IsClosed);
        await session.DisposeAsync();
        await session.DisposeAsync();
        await serverTask;
    }

    [Fact]
    public async Task ControlCommandMapsSqlerrrmReply()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeTransactionControlFailureAsync(listener, CodePoint.RDBCMM, serverError: true);
        await using var session = await Db2Session.ConnectAsync(TestConfig(port));

        var error = await Assert.ThrowsAsync<Db2ServerErrorException>(() => session.PingAsync());

        Assert.Equal(Db2ErrorKind.SqlError, error.ErrorKind);
        Assert.Equal(-911, error.SqlCode);
        Assert.Equal("40001", error.SqlState);
        Assert.Contains("SQLCODE=-911 SQLSTATE=40001", error.Message, StringComparison.Ordinal);
        await session.DisposeAsync();
        await serverTask;
    }

    [Fact]
    public async Task ResetForPoolWithActiveTransactionDiscardsSession()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeAbandonedResetAsync(listener);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        _ = await session.BeginTransactionAsync();
        await Assert.ThrowsAsync<Db2AbandonedTransactionException>(() => session.ResetForPoolAsync());
        Assert.True(session.IsClosed, "ResetForPool with an unfinished transaction must discard the session");
        await session.DisposeAsync();
        await serverTask;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransactionControlTransportFailureInvalidatesSession(bool commit)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var codePoint = commit ? CodePoint.RDBCMM : CodePoint.RDBRLLBCK;
        var serverTask = ServeTransactionControlFailureAsync(listener, codePoint, dropTransport: true);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        var transaction = await session.BeginTransactionAsync();
        var operation = commit ? transaction.CommitAsync() : transaction.RollbackAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => operation);
        Assert.True(session.IsClosed);
        Assert.True(session.AutoCommit);
        await serverTask;
    }

    [Fact]
    public async Task CancellationBeforeTransactionControlKeepsScopeAvailableForRollback()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeTransactionControlFailureAsync(listener, CodePoint.RDBRLLBCK, successfulReply: true);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        var transaction = await session.BeginTransactionAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.CommitAsync(cancellation.Token));
        Assert.False(session.AutoCommit);
        await transaction.RollbackAsync();
        Assert.True(session.AutoCommit);
        await session.DisposeAsync();
        await serverTask;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationDuringTransactionControlUsesServerInterrupt(bool commit)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var controlStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = commit ? CodePoint.RDBCMM : CodePoint.RDBRLLBCK;
        var serverTask = ServeInterruptedTransactionControlAsync(listener, control, controlStarted);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        var transaction = await session.BeginTransactionAsync();
        using var cancellation = new CancellationTokenSource();
        var operation = commit ? transaction.CommitAsync(cancellation.Token) : transaction.RollbackAsync(cancellation.Token);

        await controlStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(session.IsClosed);
        await session.DisposeAsync();
        await serverTask;
    }

    [Fact]
    public async Task CancellationDuringProgressiveLobChunkUsesServerInterruptAndKeepsSessionUsable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var getNextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptAcknowledged = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeInterruptedProgressiveLobQueryAsync(listener, getNextStarted, interruptAcknowledged);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(5), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        using var cancellation = new CancellationTokenSource();
        var query = session.QueryAsync("SELECT B FROM INTERRUPTED_PROGRESSIVE_LOB_FIXTURE", cancellation.Token);
        await getNextStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var cancelAt = System.Diagnostics.Stopwatch.GetTimestamp();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        var operationCompletionMs = System.Diagnostics.Stopwatch.GetElapsedTime(cancelAt).TotalMilliseconds;
        var interruptAckTick = await interruptAcknowledged.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var interruptAcknowledgmentMs = System.Diagnostics.Stopwatch.GetElapsedTime(cancelAt, interruptAckTick).TotalMilliseconds;

        Assert.False(session.IsClosed, "A server-confirmed SQLCODE -952 during GETNXTCHK should preserve the session.");
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await session.QueryAsync("VALUES 29")).Rows))));
        Assert.True(interruptAcknowledgmentMs <= operationCompletionMs);
        Console.WriteLine($"GETNXTCHK cancellation: interrupt acknowledgment {interruptAcknowledgmentMs:F1} ms; operation completion {operationCompletionMs:F1} ms");
        await serverTask;
    }

    [Fact]
    public async Task CommandDeadlineUsesSqlIntrWaitsForServerAndPreservesConfirmedSession()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var getNextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptAcknowledged = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeInterruptedProgressiveLobQueryAsync(listener, getNextStarted, interruptAcknowledged);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(5), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        var query = session.QueryAsync("SELECT B FROM INTERRUPTED_PROGRESSIVE_LOB_FIXTURE",
            new Db2CommandOptions(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2)));
        await getNextStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var deadlineAt = System.Diagnostics.Stopwatch.GetTimestamp();

        await Assert.ThrowsAsync<Db2CommandTimeoutException>(() => query);
        var completedMs = System.Diagnostics.Stopwatch.GetElapsedTime(deadlineAt).TotalMilliseconds;
        var ackAt = await interruptAcknowledged.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var ackMs = System.Diagnostics.Stopwatch.GetElapsedTime(deadlineAt, ackAt).TotalMilliseconds;

        Assert.False(session.IsClosed, "A server-confirmed SQLCODE -952 must leave the physical session usable.");
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await session.QueryAsync("VALUES 29")).Rows))));
        Assert.True(ackMs <= completedMs, "The deadline call must not return before SQLINTR acknowledgment.");
        Console.WriteLine($"Command deadline: SQLINTR acknowledgment {ackMs:F1} ms; operation completion {completedMs:F1} ms");
        await serverTask;
    }

    [Fact]
    public async Task CommandDeadlineWithRejectedInterruptDiscardsSession()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var getNextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeInterruptedProgressiveLobQueryAsync(listener, getNextStarted, acknowledged, rejectInterrupt: true);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(5), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        var query = session.QueryAsync("SELECT B FROM INTERRUPTED_PROGRESSIVE_LOB_FIXTURE",
            new Db2CommandOptions(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2)));
        await getNextStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<Db2ConnectionClosedException>(() => query);
        Assert.True(session.IsClosed, "A rejected SQLINTR leaves command completion uncertain, so the session must be discarded.");
        await serverTask;
    }

    [Fact]
    public async Task CommandDeadlineWithoutInterruptAcknowledgmentDiscardsSession()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var getNextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeInterruptedProgressiveLobQueryAsync(listener, getNextStarted, acknowledged, omitInterruptAcknowledgment: true);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(5), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        var query = session.QueryAsync("SELECT B FROM INTERRUPTED_PROGRESSIVE_LOB_FIXTURE",
            new Db2CommandOptions(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150)));
        await getNextStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<Db2ConnectionClosedException>(() => query);
        Assert.True(session.IsClosed, "A missing SQLINTR acknowledgment leaves command completion uncertain.");
        await serverTask;
    }

    [Fact]
    public async Task StreamingCommandDeadlineUsesSqlIntrAndPreservesConfirmedSession()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var getNextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeInterruptedProgressiveLobQueryAsync(listener, getNextStarted, acknowledged);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(5), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        var firstRow = ConsumeLobAsync();
        await getNextStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<Db2CommandTimeoutException>(() => firstRow);
        Assert.False(session.IsClosed);
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await session.QueryAsync("VALUES 29")).Rows))));
        await serverTask;

        async Task ConsumeLobAsync()
        {
            await foreach (var row in session.QueryStreamAsync("SELECT B FROM INTERRUPTED_PROGRESSIVE_LOB_FIXTURE",
                new Db2CommandOptions(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2))))
            {
                await using var lob = Assert.IsType<Db2LobValue>(row.Values[0]).OpenReadStream();
                var buffer = new byte[16];
                while (await lob.ReadAsync(buffer) > 0) { }
            }
        }
    }

    [Fact]
    public async Task ProgressiveLobChunkWithoutProgressFailsAndInvalidatesTheSession()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeProgressiveLobNoProgressAsync(listener);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => session.QueryAsync("SELECT B FROM PROGRESSIVE_LOB_FIXTURE"));

        Assert.Contains("GETNXTCHK made no progress before the final chunk", error.Message);
        Assert.True(session.IsClosed, "the session cannot be reused while the locator and cursor state are unresolved");
        await serverTask;
    }

    [Fact]
    public async Task PeerResetDuringProgressiveLobChunkInvalidatesTheSession()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServePeerResetProgressiveLobQueryAsync(listener);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var row in session.QueryStreamAsync("SELECT B FROM RESET_LOB_FIXTURE"))
            {
                var lob = Assert.IsType<Db2LobValue>(row.Values[0]);
                await using var stream = lob.OpenReadStream();
                await stream.ReadExactlyAsync(new byte[16]);
            }
        });
        Assert.True(session.IsClosed, "a peer reset during GETNXTCHK leaves the locator uncertain");
        await serverTask;
    }

    [Fact]
    public async Task EarlyRowStreamExitDrainsNextBlockLobAndPreservesSession()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeEarlyExitProgressiveLobQueryAsync(listener);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);

        var received = new List<int>();
        await foreach (var row in session.QueryStreamAsync("SELECT ID, B FROM EARLY_EXIT_LOB_FIXTURE"))
        {
            received.Add(Assert.IsType<int>(row.Values[0]));
            break;
        }

        Assert.Equal([42], received);
        Assert.False(session.IsClosed);
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await session.QueryAsync("VALUES 29")).Rows))));
        await serverTask;
    }

    [Fact]
    public async Task EarlyRowStreamExitResolvesLaterLobFromSameBlockBeforeYield()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var locatorFetchObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeEarlyExitProgressiveLobQueryAsync(
            listener,
            locatorInSameBlock: true,
            locatorFetchObserved: locatorFetchObserved);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);

        var received = new List<int>();
        await foreach (var row in session.QueryStreamAsync("SELECT ID, B FROM EARLY_EXIT_LOB_FIXTURE"))
        {
            Assert.False(locatorFetchObserved.Task.IsCompleted,
                "the first row must be available before the progressive locator is fetched");
            received.Add(Assert.IsType<int>(row.Values[0]));
            break;
        }

        Assert.Equal([42], received);
        await locatorFetchObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(session.IsClosed);
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await session.QueryAsync("VALUES 29")).Rows))));
        await serverTask;
    }

    [Fact]
    public async Task ProgressiveClobIsYieldedBeforeChunkFetchAndAdoTextReaderDecodesSplitUtf8()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var firstChunkSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFetchReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeProgressiveLobStreamQueryAsync(listener, 0xcf, [new byte[] { 0x41, 0xe2 }, new byte[] { 0x82, 0xac, 0x42 }], firstChunkSent, secondFetchReceived);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        await using var source = session.QueryStreamAsync("SELECT C FROM PROGRESSIVE_CLOB_FIXTURE").GetAsyncEnumerator();
        Assert.True(await source.MoveNextAsync());
        var firstRow = source.Current;
        var locator = Assert.IsType<Db2LobValue>(Assert.Single(firstRow.Values));
        Assert.Equal(5, locator.ByteLength);
        Assert.True(locator.IsText);
        Assert.False(firstChunkSent.Task.IsCompleted, "The query row must be yielded before GETNXTCHK returns any LOB bytes.");

        var command = new Db2AdoCommand();
        using var connection = new Db2AdoConnection();
        Db2ColumnMetadata[] clobColumns = [new("C", SqlType.NClob, 5, 0, 0, true)];
        await using var reader = new Db2AdoDataReader(command, connection, clobColumns,
            ReplayFirstRowAsync(firstRow, source).GetAsyncEnumerator(), CommandBehavior.Default, error => error, new CancellationTokenSource());
        await reader.InitializeAsync();
        Assert.True(await reader.ReadAsync());
        using (var text = reader.GetTextReader(0))
        {
            var chars = new char[1];
            Assert.Equal(1, await text.ReadAsync(chars));
            Assert.Equal('A', chars[0]);
            await firstChunkSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(secondFetchReceived.Task.IsCompleted, "The first decoded character must be available while the next locator chunk is still pending.");
            Assert.Equal(1, await text.ReadAsync(chars));
            Assert.Equal('€', chars[0]);
            await secondFetchReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, await text.ReadAsync(chars));
            Assert.Equal('B', chars[0]);
            Assert.Equal(0, await text.ReadAsync(chars));
        }
        Assert.False(await reader.ReadAsync());
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await session.QueryAsync("VALUES 29")).Rows))));
        await serverTask;

        static async IAsyncEnumerable<Db2StreamRow> ReplayFirstRowAsync(Db2StreamRow first, IAsyncEnumerator<Db2StreamRow> rest)
        {
            yield return first;
            try { while (await rest.MoveNextAsync()) yield return rest.Current; }
            finally { await rest.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task ProgressiveDbClobTextReaderDecodesSplitUtf16BeCodeUnitAndSurrogatePair()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var firstChunkSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFetchReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeProgressiveLobStreamQueryAsync(listener, 0xcd,
            [new byte[] { 0x00, 0x41, 0xd8 }, new byte[] { 0x3d, 0xde, 0x00 }], firstChunkSent, secondFetchReceived);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        await using var source = session.QueryStreamAsync("SELECT C FROM PROGRESSIVE_DBCLOB_FIXTURE").GetAsyncEnumerator();
        Assert.True(await source.MoveNextAsync());
        var firstRow = source.Current;
        var command = new Db2AdoCommand();
        using var connection = new Db2AdoConnection();
        Db2ColumnMetadata[] columns = [new("C", SqlType.NDbClob, 3, 0, 0, true)];
        await using var reader = new Db2AdoDataReader(command, connection, columns,
            ReplayFirstRowAsync(firstRow, source).GetAsyncEnumerator(), CommandBehavior.Default, error => error, new CancellationTokenSource());
        await reader.InitializeAsync();
        Assert.True(await reader.ReadAsync());
        using (var text = reader.GetTextReader(0))
        {
            var chars = new char[1];
            Assert.Equal(1, await text.ReadAsync(chars));
            Assert.Equal('A', chars[0]);
            await firstChunkSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, await text.ReadAsync(chars));
            Assert.True(char.IsHighSurrogate(chars[0]));
            Assert.Equal(1, await text.ReadAsync(chars));
            Assert.True(char.IsLowSurrogate(chars[0]));
            await secondFetchReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, await text.ReadAsync(chars));
        }
        Assert.False(await reader.ReadAsync());
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await session.QueryAsync("VALUES 29")).Rows))));
        await serverTask;

        static async IAsyncEnumerable<Db2StreamRow> ReplayFirstRowAsync(Db2StreamRow first, IAsyncEnumerator<Db2StreamRow> rest)
        {
            yield return first;
            try { while (await rest.MoveNextAsync()) yield return rest.Current; }
            finally { await rest.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task ProgressiveBlobAdoStreamDrainsUnreadChunksWhenAdvancingRows()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var firstChunkSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFetchReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeProgressiveLobStreamQueryAsync(listener, 0xc9, [new byte[] { 1, 2 }, new byte[] { 3, 4 }], firstChunkSent, secondFetchReceived);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        await using var source = session.QueryStreamAsync("SELECT B FROM PROGRESSIVE_BLOB_FIXTURE").GetAsyncEnumerator();
        Assert.True(await source.MoveNextAsync());
        var firstRow = source.Current;
        Assert.False(firstChunkSent.Task.IsCompleted);

        var command = new Db2AdoCommand();
        using var connection = new Db2AdoConnection();
        Db2ColumnMetadata[] blobColumns = [new("B", SqlType.NBlob, 4, 0, 0, true)];
        await using var reader = new Db2AdoDataReader(command, connection, blobColumns,
            ReplayFirstRowAsync(firstRow, source).GetAsyncEnumerator(), CommandBehavior.Default, error => error, new CancellationTokenSource());
        await reader.InitializeAsync();
        Assert.True(await reader.ReadAsync());
        await using (var bytes = reader.GetStream(0))
        {
            var prefix = new byte[1];
            Assert.Equal(1, await bytes.ReadAsync(prefix));
            Assert.Equal(1, prefix[0]);
            await firstChunkSent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.False(await reader.ReadAsync());
        await secondFetchReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await session.QueryAsync("VALUES 29")).Rows))));
        await serverTask;

        static async IAsyncEnumerable<Db2StreamRow> ReplayFirstRowAsync(Db2StreamRow first, IAsyncEnumerator<Db2StreamRow> rest)
        {
            yield return first;
            try { while (await rest.MoveNextAsync()) yield return rest.Current; }
            finally { await rest.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task ProgressiveClobNullAndEmptyValuesKeepAdoMaterializationSemantics()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeNullAndEmptyProgressiveClobQueryAsync(listener);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        await using var source = session.QueryStreamAsync("SELECT C FROM NULL_EMPTY_CLOB_FIXTURE").GetAsyncEnumerator();
        Assert.True(await source.MoveNextAsync());
        var firstRow = source.Current;
        Db2ColumnMetadata[] columns = [new("C", SqlType.NClob, 64, 0, 0, true)];
        var command = new Db2AdoCommand();
        using var connection = new Db2AdoConnection();
        await using var reader = new Db2AdoDataReader(command, connection, columns,
            ReplayFirstRowAsync(firstRow, source).GetAsyncEnumerator(), CommandBehavior.Default, error => error, new CancellationTokenSource());
        await reader.InitializeAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal(DBNull.Value, reader.GetValue(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(string.Empty, reader.GetString(0));
        Assert.False(await reader.ReadAsync());
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await session.QueryAsync("VALUES 29")).Rows))));
        await serverTask;

        static async IAsyncEnumerable<Db2StreamRow> ReplayFirstRowAsync(Db2StreamRow first, IAsyncEnumerator<Db2StreamRow> rest)
        {
            yield return first;
            try { while (await rest.MoveNextAsync()) yield return rest.Current; }
            finally { await rest.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task CancellationDuringProgressiveLobRowStreamUsesServerInterruptAndKeepsSessionUsable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var getNextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptAcknowledged = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeInterruptedProgressiveLobQueryAsync(listener, getNextStarted, interruptAcknowledged);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(5), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        using var cancellation = new CancellationTokenSource();
        var query = ConsumeStreamAsync();
        await getNextStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var cancelAt = System.Diagnostics.Stopwatch.GetTimestamp();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        var operationCompletionMs = System.Diagnostics.Stopwatch.GetElapsedTime(cancelAt).TotalMilliseconds;
        var interruptAckTick = await interruptAcknowledged.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var interruptAcknowledgmentMs = System.Diagnostics.Stopwatch.GetElapsedTime(cancelAt, interruptAckTick).TotalMilliseconds;
        Assert.False(session.IsClosed, "A server-confirmed SQLCODE -952 during row-stream GETNXTCHK should preserve the session.");
        Assert.Equal(29, Assert.IsType<int>(Assert.Single(Assert.Single((await session.QueryAsync("VALUES 29")).Rows))));
        Assert.True(interruptAcknowledgmentMs <= operationCompletionMs);
        Console.WriteLine($"GETNXTCHK row-stream cancellation: interrupt acknowledgment {interruptAcknowledgmentMs:F1} ms; operation completion {operationCompletionMs:F1} ms");
        await serverTask;

        async Task ConsumeStreamAsync()
        {
            await foreach (var _ in session.QueryStreamAsync("SELECT B FROM INTERRUPTED_PROGRESSIVE_LOB_FIXTURE", cancellation.Token)) { }
        }
    }

    private static async Task ServeInterruptedProgressiveLobQueryAsync(
        TcpListener listener,
        TaskCompletionSource getNextStarted,
        TaskCompletionSource<long> interruptAcknowledged,
        bool rejectInterrupt = false,
        bool omitInterruptAcknowledgment = false)
    {
        byte[] interruptToken = [0x04, 0x1c, 0xa7, 0x32];
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, DdmCodec.PackBytes(CodePoint.RDBINTTKN, interruptToken));
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        var query = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            query.Select(packet => packet.CodePoint));
        Assert.Equal(new byte[] { 0xf1 }, DdmCodec.ParseParameters(query[2].Payload)[(ushort)CodePoint.DYNDTAFMT]);
        await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0xc9, 0x80, 9 }, true);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, new byte[8]));
        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);

        var row = new byte[20];
        row[0] = 0xff;
        row[1] = 0;
        row[2] = 0;
        row[3] = 3;
        BinaryPrimitives.WriteUInt64BigEndian(row.AsSpan(4, 8), 1);
        BinaryPrimitives.WriteUInt64BigEndian(row.AsSpan(12, 8), 0x1122334455667788);
        await SendAsync(stream, CodePoint.QRYDTA, row);
        Assert.Equal((ushort)CodePoint.GETNXTCHK, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        getNextStarted.TrySetResult();

        using var auxiliary = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await using var auxiliaryStream = auxiliary.GetStream();
        Assert.Equal((ushort)CodePoint.EXCSAT, Assert.Single(await ReadChainAsync(auxiliaryStream)).CodePoint);
        await SendAsync(auxiliaryStream, CodePoint.EXCSATRD, ReadOnlyMemory<byte>.Empty);
        var interrupt = Assert.Single(await ReadChainAsync(auxiliaryStream));
        Assert.Equal((ushort)CodePoint.INTRDBRQS, interrupt.CodePoint);
        Assert.Equal(interruptToken, DdmCodec.ParseParameters(interrupt.Payload)[(ushort)CodePoint.RDBINTTKN]);
        if (omitInterruptAcknowledgment)
        {
            var auxiliaryEof = new byte[1];
            Assert.Equal(0, await auxiliaryStream.ReadAsync(auxiliaryEof).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            var primaryEof = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(primaryEof).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            return;
        }
        await SendAsync(auxiliaryStream, CodePoint.CMDCMPRM, DdmCodec.PackUInt16(CodePoint.SVRCOD, rejectInterrupt ? (ushort)8 : (ushort)0));
        if (rejectInterrupt)
        {
            var eof = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(eof).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            return;
        }
        interruptAcknowledged.TrySetResult(System.Diagnostics.Stopwatch.GetTimestamp());
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard(-952, "57014"));

        var reusedQuery = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            reusedQuery.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0x02, 0, 4 }, true);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, new byte[8]));
        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.QRYDTA, new byte[] { 0xff, 0, 29, 0, 0, 0 }, true);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
    }

    private static async Task ServeProgressiveLobNoProgressAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        var query = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            query.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0xc9, 0x80, 9 }, true);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, new byte[8]));
        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);

        var row = new byte[20];
        row[0] = 0xff;
        row[1] = 0;
        row[2] = 0;
        row[3] = 3;
        BinaryPrimitives.WriteUInt64BigEndian(row.AsSpan(4, 8), 1);
        BinaryPrimitives.WriteUInt64BigEndian(row.AsSpan(12, 8), 0x1122334455667788);
        await SendAsync(stream, CodePoint.QRYDTA, row);
        Assert.Equal((ushort)CodePoint.GETNXTCHK, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.EXTDTA, new byte[] { 0 }, true);
        await SendAsync(stream, CodePoint.ENDDTARM, DdmCodec.PackUInt16(CodePoint.SVRCOD, 0));
    }

    private static async Task ServePeerResetProgressiveLobQueryAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        var query = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            query.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0xc9, 0x80, 9 }, true);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, new byte[8]));
        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);

        var row = new byte[20];
        row[0] = 0xff;
        row[1] = 0;
        row[2] = 0;
        row[3] = 3;
        BinaryPrimitives.WriteUInt64BigEndian(row.AsSpan(4, 8), 300_000);
        BinaryPrimitives.WriteUInt64BigEndian(row.AsSpan(12, 8), 0x1122334455667788);
        await SendAsync(stream, CodePoint.QRYDTA, row);
        Assert.Equal((ushort)CodePoint.GETNXTCHK, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        // Model a peer reset before the first chunk arrives.
        client.Client.LingerState = new LingerOption(true, 0);
        client.Client.Close();
    }

    private static async Task ServeProgressiveLobStreamQueryAsync(
        TcpListener listener,
        byte fieldType,
        IReadOnlyList<byte[]> chunks,
        TaskCompletionSource? firstChunkSent = null,
        TaskCompletionSource? secondFetchReceived = null)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        var query = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            query.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, fieldType, 0x80, 9 }, true);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, new byte[8]));
        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);

        var encodedLength = chunks.Sum(chunk => chunk.Length);
        // Db2 advertises a progressive DBCLOB locator length in UTF-16 code units while
        // CLOB/BLOB advertise bytes; mirror the verified server behavior here.
        var locatorLength = Db2Session.IsDbClobType(fieldType) ? encodedLength / 2 : encodedLength;
        var row = new byte[20];
        row[0] = 0xff;
        row[1] = 0;
        row[2] = 0;
        row[3] = 3;
        BinaryPrimitives.WriteUInt64BigEndian(row.AsSpan(4, 8), (ulong)locatorLength);
        BinaryPrimitives.WriteUInt64BigEndian(row.AsSpan(12, 8), 0x1122334455667788);
        await SendAsync(stream, CodePoint.QRYDTA, row);

        for (var index = 0; index < chunks.Count; index++)
        {
            Assert.Equal((ushort)CodePoint.GETNXTCHK, Assert.Single(await ReadChainAsync(stream)).CodePoint);
            if (index == 1) secondFetchReceived?.TrySetResult();
            var chunk = chunks[index];
            if (chunk.Length > 0) await SendAsync(stream, CodePoint.EXTDTA, new byte[] { 0 }.Concat(chunk).ToArray(), true);
            var terminal = DdmCodec.PackUInt16(CodePoint.SVRCOD, 0);
            if (index == chunks.Count - 1)
                terminal = terminal.Concat(DdmCodec.PackBytes(CodePoint.ENDREF, [0xf1])).Concat(DdmCodec.PackBytes(CodePoint.LSTCHK, [0xf1])).ToArray();
            await SendAsync(stream, CodePoint.ENDDTARM, terminal);
            if (index == 0) firstChunkSent?.TrySetResult();
        }

        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
        // Autocommit LOB readers finish their read unit before reuse/pool return.
        Assert.Equal((ushort)CodePoint.RDBCMM, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        var reuse = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            reuse.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0x02, 0, 4 }, true);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, new byte[8]));
        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.QRYDTA, new byte[] { 0xff, 0, 29, 0, 0, 0 }, true);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
    }

    private static async Task ServeNullAndEmptyProgressiveClobQueryAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        var query = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            query.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0xcf, 0x80, 9 }, true);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, new byte[8]));
        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        var nullRow = new byte[] { 0xff, 0, 0xff };
        var emptyRow = new byte[20];
        emptyRow[0] = 0xff;
        emptyRow[1] = 0;
        emptyRow[2] = 0;
        emptyRow[3] = 3;
        BinaryPrimitives.WriteUInt64BigEndian(emptyRow.AsSpan(4, 8), 0);
        BinaryPrimitives.WriteUInt64BigEndian(emptyRow.AsSpan(12, 8), 0x1122334455667788);
        await SendAsync(stream, CodePoint.QRYDTA, nullRow.Concat(emptyRow).ToArray());
        // A zero-length locator is already complete and requires no GETNXTCHK.
        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
        // Autocommit LOB readers finish their read unit before reuse/pool return.
        Assert.Equal((ushort)CodePoint.RDBCMM, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        var reuse = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            reuse.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0x02, 0, 4 }, true);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, new byte[8]));
        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.QRYDTA, new byte[] { 0xff, 0, 29, 0, 0, 0 }, true);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
    }

    private static async Task ServeEarlyExitProgressiveLobQueryAsync(
        TcpListener listener,
        bool locatorInSameBlock = false,
        TaskCompletionSource? locatorFetchObserved = null)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        var query = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            query.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.QRYDSC,
            new byte[] { 6, 0x76, 0xd0, 0x02, 0, 4, 6, 0x76, 0xd0, 0xc9, 0x80, 9 }, true);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, new byte[8]));
        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        var ordinaryRow = new byte[] { 0xff, 0, 42, 0, 0, 0, 0xff };
        if (!locatorInSameBlock)
        {
            await SendAsync(stream, CodePoint.QRYDTA, ordinaryRow);
            Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        }
        var locatorRow = new byte[24];
        locatorRow[0] = 0xff;
        locatorRow[1] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(locatorRow.AsSpan(2, 4), 43);
        locatorRow[6] = 0;
        locatorRow[7] = 3;
        BinaryPrimitives.WriteUInt64BigEndian(locatorRow.AsSpan(8, 8), 1);
        BinaryPrimitives.WriteUInt64BigEndian(locatorRow.AsSpan(16, 8), 0x1122334455667788);
        await SendAsync(stream, CodePoint.QRYDTA,
            locatorInSameBlock ? ordinaryRow.Concat(locatorRow).ToArray() : locatorRow);
        Assert.Equal((ushort)CodePoint.GETNXTCHK, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        locatorFetchObserved?.TrySetResult();
        await SendAsync(stream, CodePoint.EXTDTA, new byte[] { 0, 0xaa }, true);
        await SendAsync(stream, CodePoint.ENDDTARM,
            DdmCodec.PackUInt16(CodePoint.SVRCOD, 0)
                .Concat(DdmCodec.PackBytes(CodePoint.ENDREF, [0xf1]))
                .Concat(DdmCodec.PackBytes(CodePoint.LSTCHK, [0xf1]))
                .ToArray());

        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);

        var reuse = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            reuse.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0x02, 0, 4 }, true);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, new byte[8]));
        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.QRYDTA, new byte[] { 0xff, 0, 29, 0, 0, 0 }, true);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
    }

    [Fact]
    public async Task ProcedureCursorFetchesAgainAfterAnExactFullBlock()
    {
        const int expectedRows = 169;
        const int exactQueryDataLength = 1014; // 1024-byte DSS block minus its 10-byte headers.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeExactFullBlockProcedureAsync(listener, expectedRows, exactQueryDataLength);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 1024, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);

        var result = await session.QueryAsync("CALL TESTPROC()");
        var rows = Assert.Single(result.ResultSets).Rows;
        Assert.Equal(expectedRows, rows.Count);
        Assert.Equal(Enumerable.Range(1, expectedRows), rows.Select(row => Assert.IsType<int>(Assert.Single(row))));
        await session.DisposeAsync();
        await serverTask;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcedureCursorFetchFailureInvalidatesSession(bool serverError)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeProcedureCursorFetchFailureAsync(listener, serverError);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);

        if (serverError)
        {
            var error = await Assert.ThrowsAsync<Db2SqlException>(() => session.QueryAsync("CALL TESTPROC()"));
            Assert.Equal(-911, error.SqlCode);
            Assert.Equal("40001", error.SqlState);
        Assert.Contains("SQLCODE=-911", error.Message);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => session.QueryAsync("CALL TESTPROC()"));
            Assert.Contains("no query data or end-of-query reply", error.Message);
        }
        Assert.True(session.IsClosed, "A failed procedure-cursor fetch must not leave a reusable session.");
        await session.DisposeAsync();
        await serverTask;
    }

    [Fact]
    public async Task ProcedureCursorCloseFailureInvalidatesSession()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = ServeProcedureCursorFetchFailureAsync(listener, serverError: true, closeFailure: true);
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 3,
        };
        await using var session = await Db2Session.ConnectAsync(config);

        var error = await Assert.ThrowsAsync<Db2SqlException>(() => session.QueryAsync("CALL TESTPROC()"));
        Assert.Equal(-911, error.SqlCode);
        Assert.Equal("40001", error.SqlState);
        Assert.True(session.IsClosed, "A failed procedure-cursor close must not leave a reusable session.");
        await session.DisposeAsync();
        await serverTask;
    }

    [Fact]
    public async Task TransportFailureDuringTextLobStreamsInvalidatesSession()
    {
        const int dataLength = 16_000_000;
        foreach (var (sqlType, typeName) in new (ushort SqlType, string TypeName)[]
        {
            (409, "CLOB"), (413, "DBCLOB"), (989, "XML"),
        })
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = ServeTextLobTransportFailureAsync(listener, sqlType, dataLength);
            var config = new Db2Config
            {
                Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "password",
                Timeout = TimeSpan.FromSeconds(5), BlockSize = 65535, SecurityMechanism = 3,
            };
            await using var session = await Db2Session.ConnectAsync(config);
            var source = new InfinitePatternStream();
            object parameter = sqlType switch
            {
                409 => new Db2ClobStream(source, dataLength),
                413 => new Db2DbClobStream(source, dataLength),
                _ => new Db2XmlStream(source, dataLength),
            };
            var sql = $"VALUES CAST(? AS {typeName}({dataLength}))";
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await foreach (var _ in session.QueryStreamAsync(sql, [parameter])) { }
            });
            Assert.True(session.IsClosed, $"{typeName} transport failure should invalidate the session");
            Assert.True(source.BytesRead < dataLength, $"{typeName} source was fully read despite the TCP reset");
            await serverTask;
        }
    }

    private static async Task ServeTransactionControlFailureAsync(
        TcpListener listener,
        CodePoint expectedControl,
        bool dropTransport = false,
        bool successfulReply = false,
        bool serverError = false)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
        var setup = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            setup.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        Assert.Equal((ushort)expectedControl, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        if (dropTransport)
        {
            client.Client.LingerState = new LingerOption(true, 0);
            return;
        }
        if (serverError)
            await SendAsync(stream, CodePoint.SQLERRRM,
                DdmCodec.PackBytes(CodePoint.SRVDGN, System.Text.Encoding.UTF8.GetBytes("SQLCODE=-911 SQLSTATE=40001 control failure")));
        else
            await SendAsync(stream, CodePoint.SQLCARD, successfulReply ? SqlCard() : SqlCard(-911, "40001"));
        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static async Task ServeInterruptedTransactionControlAsync(
        TcpListener listener,
        CodePoint expectedControl,
        TaskCompletionSource controlStarted)
    {
        byte[] interruptToken = [0x04, 0x1c, 0xa7, 0x32];
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, DdmCodec.PackBytes(CodePoint.RDBINTTKN, interruptToken));
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        Assert.Equal((ushort)expectedControl, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        controlStarted.TrySetResult();

        using var auxiliary = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await using var auxiliaryStream = auxiliary.GetStream();
        Assert.Equal((ushort)CodePoint.EXCSAT, Assert.Single(await ReadChainAsync(auxiliaryStream)).CodePoint);
        await SendAsync(auxiliaryStream, CodePoint.EXCSATRD, ReadOnlyMemory<byte>.Empty);
        var interrupt = Assert.Single(await ReadChainAsync(auxiliaryStream));
        Assert.Equal((ushort)CodePoint.INTRDBRQS, interrupt.CodePoint);
        Assert.Equal(interruptToken, DdmCodec.ParseParameters(interrupt.Payload)[(ushort)CodePoint.RDBINTTKN]);
        await SendAsync(auxiliaryStream, CodePoint.CMDCMPRM, DdmCodec.PackUInt16(CodePoint.SVRCOD, 0));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard(-952, "57014"));

        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static async Task ServeExactFullBlockProcedureAsync(TcpListener listener, int rowCount, int expectedPayloadLength)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.DSCSQLSTT },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLDARD, ReadOnlyMemory<byte>.Empty);
        Assert.Equal((ushort)CodePoint.EXCSQLSTT, Assert.Single(await ReadChainAsync(stream)).CodePoint);

        var section = DdmCodec.PackPackageNameCsn("TESTDB", "SYSSH200", "SYSLVL01", 65);
        var packageList = DdmCodec.PackObject(CodePoint.PKGSNLST, section);
        await SendAsync(stream, CodePoint.RSLSETRM, packageList, true);
        var resultSetSummary = new byte[14];
        resultSetSummary[0] = 1;
        await SendAsync(stream, CodePoint.SQLRSLRD, resultSetSummary, true);
        var queryId = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(queryId, 123);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, queryId), true);
        await SendAsync(stream, CodePoint.SQLCINRD, ProcedureIntegerCursorInfo(), true);
        byte[] queryDescriptor = [6, 0x76, 0xd0, 0x02, 0, 4];
        await SendAsync(stream, CodePoint.QRYDSC, queryDescriptor);

        var fetch = await ReadChainAsync(stream);
        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(fetch).CodePoint);
        var rows = new byte[expectedPayloadLength];
        for (var index = 0; index < rowCount; index++)
        {
            var offset = index * 6;
            rows[offset] = 0xff;
            rows[offset + 1] = 0;
            BinaryPrimitives.WriteInt32LittleEndian(rows.AsSpan(offset + 2, 4), index + 1);
        }
        Assert.Equal(expectedPayloadLength, rows.Length);
        await SendAsync(stream, CodePoint.QRYDTA, rows);

        var nextFetch = await ReadChainAsync(stream);
        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(nextFetch).CodePoint);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
        Assert.Equal((ushort)CodePoint.CLSQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        Assert.Equal((ushort)CodePoint.RDBCMM, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static async Task ServeProcedureCursorFetchFailureAsync(TcpListener listener, bool serverError, bool closeFailure = false)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.DSCSQLSTT },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLDARD, ReadOnlyMemory<byte>.Empty);
        Assert.Equal((ushort)CodePoint.EXCSQLSTT, Assert.Single(await ReadChainAsync(stream)).CodePoint);

        var section = DdmCodec.PackPackageNameCsn("TESTDB", "SYSSH200", "SYSLVL01", 65);
        await SendAsync(stream, CodePoint.RSLSETRM, DdmCodec.PackObject(CodePoint.PKGSNLST, section), true);
        var resultSetSummary = new byte[14];
        resultSetSummary[0] = 1;
        await SendAsync(stream, CodePoint.SQLRSLRD, resultSetSummary, true);
        var queryId = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(queryId, 123);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, queryId), true);
        await SendAsync(stream, CodePoint.SQLCINRD, ProcedureIntegerCursorInfo(), true);
        byte[] queryDescriptor = [6, 0x76, 0xd0, 0x02, 0, 4];
        await SendAsync(stream, CodePoint.QRYDSC, queryDescriptor);

        Assert.Equal((ushort)CodePoint.CNTQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        if (closeFailure)
        {
            await SendAsync(stream, CodePoint.QRYDTA, new byte[] { 0xff, 0, 42, 0, 0, 0 });
            Assert.Equal((ushort)CodePoint.CLSQRY, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        }
        await SendAsync(stream, CodePoint.SQLCARD, serverError ? SqlCard(-911, "40001") : SqlCard());

        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static byte[] ProcedureIntegerCursorInfo(string name = "VALUE")
    {
        using var output = new MemoryStream();
        output.WriteByte(0xff);
        output.Write([1, 0]);
        output.Write([10, 0, 0, 0]);
        output.WriteByte(4);
        output.Write(new byte[7]);
        output.Write([0xf1, 1, 0, 0]);
        output.Write(new byte[10]); // SQLAM 9/10 SQLDAGRP extensions.
        output.WriteByte(0);
        output.Write([0, 0]);
        WriteFdocaStringPair(output, name);
        WriteFdocaStringPair(output, name);
        WriteFdocaStringPair(output, "");
        output.WriteByte(0xff);
        output.WriteByte(0xff);
        output.WriteByte(0xff); // SQLAM 10 trailing SQLDOPTGRP extension marker.
        return output.ToArray();
    }

    private static void WriteFdocaStringPair(Stream output, string value)
    {
        output.Write([0, 0]);
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        output.WriteByte((byte)(bytes.Length >> 8));
        output.WriteByte((byte)bytes.Length);
        output.Write(bytes);
    }

    private static async Task ServeConnectionOnlyAsync(TcpListener listener, bool servePoolBaseline = false)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
        var setup = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            setup.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        if (servePoolBaseline) await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS");
        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer));
    }

    private static async Task ServeAbandonedResetAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        Assert.Equal((ushort)CodePoint.RDBRLLBCK, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static async Task ServePoolResetAsync(
        TcpListener listener,
        TaskCompletionSource resetStarted,
        Task continueReset,
        bool resetFails)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS");
        Assert.Equal((ushort)CodePoint.RDBRLLBCK, Assert.Single(await ReadChainAsync(stream)).CodePoint);
        resetStarted.TrySetResult();
        await continueReset.WaitAsync(TimeSpan.FromSeconds(3));
        await SendAsync(stream, CodePoint.SQLCARD, resetFails ? SqlCard(-911, "40001") : SqlCard());
        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static async Task ServePoolResetFailureRecoveryAsync(
        TcpListener listener,
        TaskCompletionSource resetStarted,
        Task continueReset)
    {
        using (var first = await AcceptInitializedPoolClientAsync(listener))
        {
            await using var stream = first.GetStream();
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
                (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

            await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS", applicationName: "temporary-pool-state");
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
                (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            resetStarted.TrySetResult();
            await continueReset.WaitAsync(TimeSpan.FromSeconds(3));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard(-911, "40001"));
            var buffer = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        }

        using (var replacement = await AcceptInitializedPoolClientAsync(listener))
        {
            await using var stream = replacement.GetStream();
            Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
                (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0x02, 0, 4 }, true);
            var queryId = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(queryId, 1);
            await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, queryId), true);
            await SendAsync(stream, CodePoint.QRYDTA, new byte[] { 0xff, 0, 42, 0, 0, 0 }, true);
            await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
            await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS");
            var buffer = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        }
    }

    private static async Task ServePoolResetFailureThenDelayedReplacementAsync(
        TcpListener listener,
        TaskCompletionSource resetStarted,
        Task continueReset,
        TaskCompletionSource replacementHandshakeStarted,
        Task continueReplacementHandshake)
    {
        using (var first = await AcceptInitializedPoolClientAsync(listener))
        {
            await using var stream = first.GetStream();
            var clientInfo = new ushort[] { (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM };
            Assert.Equal(clientInfo, (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
            await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS", applicationName: "temporary-pool-state");
            Assert.Equal(clientInfo, (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            resetStarted.TrySetResult();
            await continueReset.WaitAsync(TimeSpan.FromSeconds(3));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard(-911, "40001"));
            var buffer = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        }

        using (var replacement = await listener.AcceptTcpClientAsync())
        {
            await using var stream = replacement.GetStream();
            try
            {
                Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
                    (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
                replacementHandshakeStarted.TrySetResult();
                await continueReplacementHandshake.WaitAsync(TimeSpan.FromSeconds(5));

                await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
                Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
                    (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
                await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
                await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
                Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
                    (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
                await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
                await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS");
                var buffer = new byte[1];
                Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            }
            catch (EndOfStreamException)
            {
                // The test closes the pool or cancels the waiter during the replacement handshake.
            }
        }
    }

    private static async Task ServePoolResetFailureWithQueuedTransportAndAuthFailuresAsync(
        TcpListener listener,
        TaskCompletionSource resetStarted,
        Task continueReset,
        int failedReplacements)
    {
        using (var first = await AcceptInitializedPoolClientAsync(listener))
        {
            await using var stream = first.GetStream();
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
                (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

            await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS", applicationName: "temporary-pool-state");
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
                (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            resetStarted.TrySetResult();
            await continueReset.WaitAsync(TimeSpan.FromSeconds(3));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard(-911, "40001"));
            var buffer = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        }

        for (var attempt = 0; attempt < failedReplacements; attempt++)
        {
            if (attempt < 2)
            {
                using var dropped = await listener.AcceptTcpClientAsync();
                await using var droppedStream = dropped.GetStream();
                Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
                    (await ReadChainAsync(droppedStream)).Select(packet => packet.CodePoint));
                continue;
            }
            await ServeFailureAsync(listener, WireErrorStage.Authentication, CodePoint.SECCHKRM,
                DdmCodec.PackBytes(CodePoint.SECCHKCD, [8]));
        }

        using (var replacement = await AcceptInitializedPoolClientAsync(listener))
        {
            await using var stream = replacement.GetStream();
            Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
                (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0x02, 0, 4 }, true);
            var queryId = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(queryId, 1);
            await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, queryId), true);
            await SendAsync(stream, CodePoint.QRYDTA, new byte[] { 0xff, 0, 42, 0, 0, 0 }, true);
            await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
            await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS");
            var buffer = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    private static async Task<TcpClient> AcceptInitializedPoolClientAsync(TcpListener listener)
    {
        var client = await listener.AcceptTcpClientAsync();
        try
        {
            var stream = client.GetStream();
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
                (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
            Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
                (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
            await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
                (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
            await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static byte[] PoolStateDescriptor()
    {
        var descriptor = new byte[3 + 7 * 3];
        descriptor[0] = checked((byte)descriptor.Length);
        descriptor[1] = 0x76;
        descriptor[2] = 0xd0;
        for (var index = 0; index < 7; index++)
        {
            descriptor[3 + index * 3] = 0x3e;
            descriptor[4 + index * 3] = 0x00;
            descriptor[5 + index * 3] = 0xff;
        }
        return descriptor;
    }

    private static byte[] PoolStateRow(params string[] values)
    {
        Assert.Equal(7, values.Length);
        using var row = new MemoryStream();
        row.Write([0xff, 0]); // no SQLCA warning group
        Span<byte> length = stackalloc byte[2];
        foreach (var value in values)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(value);
            BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
            row.Write(length);
            row.Write(bytes);
        }
        return row.ToArray();
    }

    /// <summary>Serves the single combined actual-state probe used for pool baselines and returns.</summary>
    private static async Task ServeRegisterReadsAsync(System.Net.Sockets.NetworkStream stream, string schema, string isolation,
        string applicationName = "", string? workstationName = null, string userId = "", string accounting = "", string sessionUser = "DB2INST1")
    {
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        Assert.Equal(7, DdmCodec.ParseQueryDescriptor(PoolStateDescriptor()).Length);
        await SendAsync(stream, CodePoint.QRYDSC, PoolStateDescriptor(), true);
        var queryId = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(queryId, 1);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, queryId), true);
        await SendAsync(stream, CodePoint.QRYDTA,
            PoolStateRow(sessionUser, schema, isolation, applicationName, workstationName ?? Environment.MachineName, userId, accounting), true);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
    }

    private static async Task ServeDelayedConnectionAsync(
        TcpListener listener,
        TaskCompletionSource handshakeStarted,
        Task continueHandshake)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var initial = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC }, initial.Select(packet => packet.CodePoint));
        handshakeStarted.SetResult();
        await continueHandshake.WaitAsync(TimeSpan.FromSeconds(5));

        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task ServeTextLobTransportFailureAsync(TcpListener listener, ushort sqlType, int dataLength)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.DSCSQLSTT },
            (await ReadChainAsync(stream)).Select(packet => packet.CodePoint));
        var parameterDard = new byte[20];
        parameterDard[0] = 0xff;
        parameterDard[1] = 1;
        parameterDard[2] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(parameterDard.AsSpan(8), (ulong)dataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(parameterDard.AsSpan(16), sqlType);
        await SendAsync(stream, CodePoint.SQLDARD, parameterDard);

        Assert.Equal((ushort)CodePoint.OPNQRY, (await DssCodec.ReadAsync(stream)).CodePoint);
        Assert.Equal((ushort)CodePoint.SQLDTA, (await DssCodec.ReadAsync(stream)).CodePoint);

        // Read one partial EXTDTA DSS segment, then reset TCP before the next segment.
        var header = new byte[6];
        await stream.ReadExactlyAsync(header);
        var segmentLength = (BinaryPrimitives.ReadUInt16BigEndian(header) & 0x7fff);
        var body = new byte[segmentLength - header.Length];
        await stream.ReadExactlyAsync(body);
        Assert.Equal((ushort)CodePoint.EXTDTA, BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2)));
        client.Client.LingerState = new LingerOption(true, 0);
    }

    private sealed class InfinitePatternStream : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, 16_384);
            for (var index = 0; index < count; index++) buffer[index] = (byte)(((BytesRead + index) * 37 + 11) % 251);
            BytesRead += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<List<DssPacket>> ReadChainAsync(Stream stream)
    {
        var result = new List<DssPacket>();
        bool chained;
        do
        {
            var packet = await DssCodec.ReadAsync(stream);
            result.Add(packet);
            chained = packet.Header.Chained;
        } while (chained);
        return result;
    }

    private static async Task SendAsync(Stream stream, CodePoint codePoint, ReadOnlyMemory<byte> payload, bool chained = false)
    {
        var ddm = DdmCodec.PackObject(codePoint, payload.Span);
        await stream.WriteAsync(DssCodec.EncodeReply(ddm, chained, 1));
    }

    private static byte[] SqlCard(int code = 0, string state = "00000", int affected = 0)
    {
        var result = new byte[54];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1), code);
        System.Text.Encoding.ASCII.GetBytes(state).CopyTo(result, 5);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(27), affected);
        return result;
    }
}
