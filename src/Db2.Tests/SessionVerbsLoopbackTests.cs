using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using JustyBase.Db2.Converters;
using JustyBase.Db2.Protocol;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class SessionVerbsLoopbackTests
{
    [Fact]
    public async Task ConnectAppliesConfiguredSchemaAndSetterTracksIt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync(listener, async stream =>
        {
            await ServeHandshakeAsync(stream, "FIRSTDB", schema: "BASE");
            var set = await ReadChainAsync(stream);
            Assert.Equal(
                new ushort[] { (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
                set.Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
            await ReadEofAsync(stream);
        });

        var session = await Db2Session.ConnectAsync(Config(port, "FIRSTDB", schema: "BASE"));
        await using (session)
        {
            Assert.Equal("BASE", session.CurrentSchema);
            await session.SetCurrentSchemaAsync("  APP  ");
            Assert.Equal("APP", session.CurrentSchema);
        }
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ChangeDatabaseReconnectsToTargetAndRestoresConfiguredSchema()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync(listener, async stream =>
        {
            await ServeHandshakeAsync(stream, "FIRSTDB", schema: "BASE");
            var set = await ReadChainAsync(stream);
            Assert.Equal(
                new ushort[] { (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
                set.Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
            await ReadEofAsync(stream);

            using var secondClient = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await using var secondStream = secondClient.GetStream();
            await ServeHandshakeAsync(secondStream, "NEXTDB", schema: "BASE");
            await ReadEofAsync(secondStream);
        });

        var session = await Db2Session.ConnectAsync(Config(port, "FIRSTDB", schema: "BASE"));
        await using (session)
        {
            await session.SetCurrentSchemaAsync("OTHER");
            Assert.Equal("OTHER", session.CurrentSchema);

            await session.ChangeDatabaseAsync("  NEXTDB  ");

            Assert.False(session.IsClosed);
            Assert.True(session.AutoCommit);
            Assert.Equal("BASE", session.CurrentSchema);
        }
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ChangeDatabaseRejectsInvalidNamesTransactionsAndPooledLeasesBeforeIo()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync(listener, async stream =>
        {
            await ServeHandshakeAsync(stream, "FIRSTDB");
            await ReadEofAsync(stream);
        });

        var session = await Db2Session.ConnectAsync(Config(port, "FIRSTDB"));
        await using (session)
        {
            await Assert.ThrowsAsync<Db2ConfigurationException>(() => session.ChangeDatabaseAsync("   "));
            await Assert.ThrowsAsync<Db2ConfigurationException>(() => session.ChangeDatabaseAsync("A\0B"));
            await Assert.ThrowsAsync<Db2ConfigurationException>(() => session.ChangeDatabaseAsync(new string('D', 129)));

            session.AutoCommit = false;
            var transaction = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ChangeDatabaseAsync("NEXTDB"));
            Assert.Contains("transaction", transaction.Message, StringComparison.OrdinalIgnoreCase);
            session.AutoCommit = true;

            var pooled = typeof(Db2Session).GetField("_poolBaselineCaptured", BindingFlags.Instance | BindingFlags.NonPublic)!;
            pooled.SetValue(session, true);
            try
            {
                var lease = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ChangeDatabaseAsync("NEXTDB"));
                Assert.Contains("pooled session", lease.Message, StringComparison.OrdinalIgnoreCase);
                Assert.False(session.IsClosed);
            }
            finally { pooled.SetValue(session, false); }
        }
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task FailedChangeDatabaseLeavesTheSessionClosed()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ServeHandshakeAsync(stream, "FIRSTDB");
            listener.Stop();
            stopped.SetResult();
        });

        var config = Config(port, "FIRSTDB");
        config.Timeout = TimeSpan.FromSeconds(2);
        var session = await Db2Session.ConnectAsync(config);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (session)
        {
            var error = await Record.ExceptionAsync(() => session.ChangeDatabaseAsync("NEXTDB"));
            Assert.NotNull(error);
            Assert.True(session.IsClosed);
        }
        await server.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Db2Config Config(int port, string database, string? schema = null) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Database = database,
        CurrentSchema = schema ?? "",
        User = "db2inst1",
        Password = "secret",
        SecurityMechanism = 3,
    };

    private static Task ServeAsync(TcpListener listener, Func<Stream, Task> handler) => Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await using var stream = client.GetStream();
        await handler(stream);
    });

    private static async Task ServeHandshakeAsync(Stream stream, string expectedDatabase, string? schema = null)
    {
        var startup = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC }, startup.Select(packet => packet.CodePoint));
        AssertDatabase(startup.Single(packet => packet.CodePoint == (ushort)CodePoint.ACCSEC), expectedDatabase);
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));

        var authentication = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB }, authentication.Select(packet => packet.CodePoint));
        AssertDatabase(authentication.Single(packet => packet.CodePoint == (ushort)CodePoint.ACCRDB), expectedDatabase);
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), chained: true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);

        var setup = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            setup.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());

        if (schema is null) return;
        var set = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM },
            set.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
    }

    private static async Task ReadEofAsync(Stream stream)
    {
        var buffer = new byte[1];
        Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static void AssertDatabase(DssPacket packet, string expectedDatabase)
    {
        var parameters = DdmCodec.ParseParameters(packet.Payload);
        var actual = parameters.TryGetValue((ushort)CodePoint.RDBNAM, out var rdbnam)
            ? Ebcdic.DecodeCp500(rdbnam).TrimEnd()
            : Encoding.UTF8.GetString(parameters[(ushort)CodePoint.PKGNAMCSN].AsSpan(0, 18)).TrimEnd();
        Assert.Equal(expectedDatabase, actual);
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

    private static byte[] SqlCard()
    {
        var result = new byte[54];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1), 0);
        Encoding.ASCII.GetBytes("00000").CopyTo(result, 5);
        return result;
    }
}
