using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using JustyBase.Db2;
using JustyBase.Db2.Converters;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Security;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed partial class SessionTests
{
    private static Db2Config Secmec7TestConfig(int port, ushort mechanism) => new()
    {
        Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "DB2INST1", Password = "clear-password",
        Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = mechanism,
    };

    private static byte[] UInt16List(CodePoint codePoint, params ushort[] values)
    {
        var body = new byte[values.Length * 2];
        for (var index = 0; index < values.Length; index++) BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(index * 2), values[index]);
        return DdmCodec.PackBytes(codePoint, body);
    }

    private static ushort UInt16Field(Dictionary<ushort, byte[]> fields, CodePoint codePoint) =>
        BinaryPrimitives.ReadUInt16BigEndian(fields[(ushort)codePoint]);

    private static async Task<bool> ClientSendsMoreAsync(Stream stream)
    {
        var buffer = new byte[1];
        try { return await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) > 0; }
        catch (Exception ex) when (ex is IOException or TimeoutException) { return false; }
    }

    private static async Task CompleteLoginAsync(Stream stream)
    {
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, DdmCodec.PackBytes(CodePoint.RDBINTTKN, [4, 28, 167, 50]));
        var initialize = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, initialize.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
    }

    private static async Task<(int Port, Task Server)> StartMockAsync(TcpListener listener, Func<Stream, Task> handler)
    {
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await handler(stream);
        });
        await Task.Yield();
        return (port, server);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Secmec7SendsEncryptedPasswordInSecchkSectkn(bool echoEncalg)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var serverPrivate = Secmec9FromHex(Secmec9ServerPrivateHex);
        var serverPublic = Convert.FromHexString(Secmec9ServerPublicHex);
        var (port, serverTask) = await StartMockAsync(listener, async stream =>
        {
            var opening = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC }, opening.Select(packet => packet.CodePoint));
            var request = DdmCodec.ParseParameters(opening[1].Payload);
            Assert.Equal((ushort)7, UInt16Field(request, CodePoint.SECMEC));
            Assert.Equal((ushort)1, UInt16Field(request, CodePoint.ENCALG));
            var clientPublic = request[(ushort)CodePoint.SECTKN];
            Assert.Equal(32, clientPublic.Length);

            // Db2 LUW SERVER_ENCRYPT reply: SECMEC=7, SECTKN(32), ENCALG=1.
            await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 7)
                .Concat(DdmCodec.PackBytes(CodePoint.SECTKN, serverPublic))
                .Concat(echoEncalg ? DdmCodec.PackUInt16(CodePoint.ENCALG, 1) : []).ToArray());

            var authentication = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB }, authentication.Select(packet => packet.CodePoint));
            var secchk = DdmCodec.ParseParameters(authentication[0].Payload);
            Assert.Equal(new ushort[] { (ushort)CodePoint.SECMEC, (ushort)CodePoint.RDBNAM, (ushort)CodePoint.USRID, (ushort)CodePoint.SECTKN }, secchk.Keys);
            Assert.Equal((ushort)7, UInt16Field(secchk, CodePoint.SECMEC));
            Assert.Equal("DB2INST1", Ebcdic.DecodeCp500(secchk[(ushort)CodePoint.USRID]));
            Assert.False(authentication[0].Payload.AsSpan().IndexOf(Ebcdic.EncodeCp500("clear-password")) >= 0, "the plaintext password must not appear anywhere in SECCHK");

            var serverKey = Secmec9.CalculateSessionKey(clientPublic, serverPrivate)[12..20];
            var iv = new byte[8];
            Ebcdic.EncodeCp500("DB2INST1").CopyTo(iv, 0);
            Assert.Equal("clear-password", Ebcdic.DecodeCp500(Secmec9Decrypt(serverKey, iv, secchk[(ushort)CodePoint.SECTKN])));

            await CompleteLoginAsync(stream);
        });

        await using (var session = await Db2Session.ConnectAsync(Secmec7TestConfig(port, 7)))
        {
            Assert.False(session.IsClosed);
        }
        await serverTask;
    }

    [Fact]
    public void Secmec7IvIsTheCp500UserIdPaddedOrTruncatedToOneBlock()
    {
        var clientPrivate = Secmec9FromHex(Secmec9ClientPrivateHex);
        var serverPublic = Convert.FromHexString(Secmec9ServerPublicHex);
        var key = Convert.FromHexString(Secmec9SharedHex)[12..20];
        foreach (var user in new[] { "U1", "DB2INST1", "AVERYLONGUSERNAME" })
        {
            var ciphertext = Secmec9.EncryptUsrencpwd(Ebcdic.EncodeCp500("SecretPass123"), user, serverPublic, clientPrivate);
            var iv = new byte[8];
            var userId = Ebcdic.EncodeCp500(user);
            userId.AsSpan(0, Math.Min(8, userId.Length)).CopyTo(iv);
            Assert.Equal("SecretPass123", Ebcdic.DecodeCp500(Secmec9Decrypt(key, iv, ciphertext)));
        }
        Assert.Throws<ArgumentException>(() => Secmec9.EncryptUsrencpwd([1], "", serverPublic, clientPrivate));
    }

    [Fact]
    public async Task Secmec3AgainstServerEncryptFailsWithAcceptedListAndNoSecchk()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var extraRequest = true;
        var (port, serverTask) = await StartMockAsync(listener, async stream =>
        {
            var opening = await ReadChainAsync(stream);
            var request = DdmCodec.ParseParameters(opening[1].Payload);
            Assert.Equal((ushort)3, UInt16Field(request, CodePoint.SECMEC));
            Assert.DoesNotContain((ushort)CodePoint.SECTKN, request.Keys);
            Assert.DoesNotContain((ushort)CodePoint.ENCALG, request.Keys);
            // Captured from Db2 LUW AUTHENTICATION=SERVER_ENCRYPT: one SECMEC parameter listing 7, 9, 10,
            // SECCHKCD=1 and an ENCALG list; no server SECTKN.
            await SendAsync(stream, CodePoint.ACCSECRD, UInt16List(CodePoint.SECMEC, 7, 9, 10)
                .Concat(DdmCodec.PackBytes(CodePoint.SECCHKCD, [1]))
                .Concat(UInt16List(CodePoint.ENCALG, 1, 2)).ToArray());
            extraRequest = await ClientSendsMoreAsync(stream);
        });

        var error = await Assert.ThrowsAsync<Db2UnsupportedFeatureException>(() => Db2Session.ConnectAsync(Secmec7TestConfig(port, 3)));
        Assert.Contains("rejected security mechanism 3 (SECCHKCD=1); the server accepts 7, 9, 10", error.Message, StringComparison.Ordinal);
        await serverTask;
        Assert.False(extraRequest, "client must not send SECCHK, plaintext or otherwise, after a rejected ACCSEC");
    }

    [Fact]
    public async Task Secmec7AgainstServerOnlyServerFailsWithAcceptedListAndNoSecchk()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var extraRequest = true;
        var (port, serverTask) = await StartMockAsync(listener, async stream =>
        {
            await ReadChainAsync(stream);
            // Captured from Db2 LUW AUTHENTICATION=SERVER: SECMEC list 3, 5 and SECCHKCD=1.
            await SendAsync(stream, CodePoint.ACCSECRD, UInt16List(CodePoint.SECMEC, 3, 5).Concat(DdmCodec.PackBytes(CodePoint.SECCHKCD, [1])).ToArray());
            extraRequest = await ClientSendsMoreAsync(stream);
        });

        var error = await Assert.ThrowsAsync<Db2UnsupportedFeatureException>(() => Db2Session.ConnectAsync(Secmec7TestConfig(port, 7)));
        Assert.Contains("rejected security mechanism 7 (SECCHKCD=1); the server accepts 3, 5", error.Message, StringComparison.Ordinal);
        await serverTask;
        Assert.False(extraRequest, "client must not downgrade to mechanism 3");
    }

    [Fact]
    public async Task Secmec7RejectsMalformedAccsecrdMechanismList()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var extraRequest = true;
        var (port, serverTask) = await StartMockAsync(listener, async stream =>
        {
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackBytes(CodePoint.SECMEC, [0, 7, 0]));
            extraRequest = await ClientSendsMoreAsync(stream);
        });

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Db2Session.ConnectAsync(Secmec7TestConfig(port, 7)));
        Assert.Contains("malformed security mechanism list", error.Message, StringComparison.Ordinal);
        await serverTask;
        Assert.False(extraRequest);
    }

    [Fact]
    public async Task Secmec7RejectsAesSelectionWithoutSendingSecchk()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var serverPublic = Convert.FromHexString(Secmec9ServerPublicHex);
        var extraRequest = true;
        var (port, serverTask) = await StartMockAsync(listener, async stream =>
        {
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 7)
                .Concat(DdmCodec.PackUInt16(CodePoint.ENCALG, 2))
                .Concat(DdmCodec.PackBytes(CodePoint.SECTKN, serverPublic)).ToArray());
            extraRequest = await ClientSendsMoreAsync(stream);
        });

        var error = await Assert.ThrowsAsync<Db2UnsupportedFeatureException>(() => Db2Session.ConnectAsync(Secmec7TestConfig(port, 7)));
        Assert.Matches("ENCALG=2.*DES \\(ENCALG=1\\) only", error.Message);
        await serverTask;
        Assert.False(extraRequest, "client must fail before SECCHK rather than sending credentials");
    }

    [Fact]
    public async Task Secmec7RejectsMissingServerTokenBeforeSecchk()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var extraRequest = true;
        var (port, serverTask) = await StartMockAsync(listener, async stream =>
        {
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 7).Concat(DdmCodec.PackUInt16(CodePoint.ENCALG, 1)).ToArray());
            extraRequest = await ClientSendsMoreAsync(stream);
        });

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Db2Session.ConnectAsync(Secmec7TestConfig(port, 7)));
        Assert.Contains("did not return a usable server security token", error.Message, StringComparison.Ordinal);
        await serverTask;
        Assert.False(extraRequest);
    }

    [Fact]
    public async Task Secmec7SurfacesNonZeroAccsecrdCheckCode()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var extraRequest = true;
        var (port, serverTask) = await StartMockAsync(listener, async stream =>
        {
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 7).Concat(DdmCodec.PackBytes(CodePoint.SECCHKCD, [0x13])).ToArray());
            extraRequest = await ClientSendsMoreAsync(stream);
        });

        var error = await Assert.ThrowsAsync<Db2AuthenticationException>(() => Db2Session.ConnectAsync(Secmec7TestConfig(port, 7)));
        Assert.Equal(0x13, error.SecurityCheckCode);
        await serverTask;
        Assert.False(extraRequest);
    }

    [Fact]
    public async Task Secmec7AuthenticationFailureDoesNotRetryWithMechanism3()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var serverPublic = Convert.FromHexString(Secmec9ServerPublicHex);
        var extraRequest = true;
        var (port, serverTask) = await StartMockAsync(listener, async stream =>
        {
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 7)
                .Concat(DdmCodec.PackBytes(CodePoint.SECTKN, serverPublic))
                .Concat(DdmCodec.PackUInt16(CodePoint.ENCALG, 1)).ToArray());
            var authentication = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB }, authentication.Select(packet => packet.CodePoint));
            Assert.Equal((ushort)7, UInt16Field(DdmCodec.ParseParameters(authentication[0].Payload), CodePoint.SECMEC));
            // Db2 answers a wrong password with SECCHKCD=0x0F.
            await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0x0f]));
            extraRequest = await ClientSendsMoreAsync(stream);
        });

        var error = await Assert.ThrowsAsync<Db2AuthenticationException>(() => Db2Session.ConnectAsync(Secmec7TestConfig(port, 7)));
        Assert.Equal(15, error.SecurityCheckCode);
        await serverTask;
        Assert.False(extraRequest, "client must close after failed encrypted authentication without a mechanism 3 retry");
    }

    // No configured mechanism (SecurityMechanism 0): the driver requests 3 and, when a SERVER_ENCRYPT server refuses
    // it, sends a second ACCSEC on the same connection with the strongest encrypting mechanism the server listed.
    [Theory]
    [InlineData(new ushort[] { 7, 9, 10 }, (ushort)9)]
    [InlineData(new ushort[] { 7, 10 }, (ushort)7)]
    public async Task UnsetMechanismFollowsServerEncryptToEncryptingMechanism(ushort[] listed, ushort expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var serverPublic = Convert.FromHexString(Secmec9ServerPublicHex);
        var (port, serverTask) = await StartMockAsync(listener, async stream =>
        {
            var opening = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC }, opening.Select(packet => packet.CodePoint));
            Assert.Equal((ushort)3, UInt16Field(DdmCodec.ParseParameters(opening[1].Payload), CodePoint.SECMEC));
            await SendAsync(stream, CodePoint.ACCSECRD, UInt16List(CodePoint.SECMEC, listed)
                .Concat(DdmCodec.PackBytes(CodePoint.SECCHKCD, [1]))
                .Concat(UInt16List(CodePoint.ENCALG, 1, 2)).ToArray());

            var retry = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.ACCSEC }, retry.Select(packet => packet.CodePoint));
            var request = DdmCodec.ParseParameters(retry[0].Payload);
            Assert.Equal(expected, UInt16Field(request, CodePoint.SECMEC));
            Assert.Equal(32, request[(ushort)CodePoint.SECTKN].Length);
            await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, expected)
                .Concat(DdmCodec.PackBytes(CodePoint.SECTKN, serverPublic))
                .Concat(DdmCodec.PackUInt16(CodePoint.ENCALG, 1)).ToArray());

            var authentication = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB }, authentication.Select(packet => packet.CodePoint));
            Assert.False(authentication[0].Payload.AsSpan().IndexOf(Ebcdic.EncodeCp500("clear-password")) >= 0, "the plaintext password must not appear in SECCHK");
            var secchk = DdmCodec.ParseParameters(authentication[0].Payload);
            Assert.Equal(expected, UInt16Field(secchk, CodePoint.SECMEC));
            Assert.DoesNotContain((ushort)CodePoint.PASSWORD, secchk.Keys);
            Assert.Equal(expected == 7, secchk.ContainsKey((ushort)CodePoint.USRID));

            await CompleteLoginAsync(stream);
        });

        await using (var session = await Db2Session.ConnectAsync(Secmec7TestConfig(port, 0)))
        {
            Assert.False(session.IsClosed);
        }
        await serverTask;
    }

    [Fact]
    public async Task UnsetMechanismDoesNotFollowServerListingNoEncryptingMechanism()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var extraRequest = true;
        var (port, serverTask) = await StartMockAsync(listener, async stream =>
        {
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.ACCSECRD, UInt16List(CodePoint.SECMEC, 10, 11).Concat(DdmCodec.PackBytes(CodePoint.SECCHKCD, [1])).ToArray());
            extraRequest = await ClientSendsMoreAsync(stream);
        });

        var error = await Assert.ThrowsAsync<Db2UnsupportedFeatureException>(() => Db2Session.ConnectAsync(Secmec7TestConfig(port, 0)));
        Assert.Contains("rejected security mechanism 3 (SECCHKCD=1); the server accepts 10, 11", error.Message, StringComparison.Ordinal);
        await serverTask;
        Assert.False(extraRequest, "no second ACCSEC and no SECCHK");
    }

    [Fact]
    public async Task UnsetMechanismStopsWhenServerRejectsTheMechanismItListed()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var extraRequest = true;
        var (port, serverTask) = await StartMockAsync(listener, async stream =>
        {
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.ACCSECRD, UInt16List(CodePoint.SECMEC, 7, 9).Concat(DdmCodec.PackBytes(CodePoint.SECCHKCD, [1])).ToArray());
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.ACCSECRD, UInt16List(CodePoint.SECMEC, 3).Concat(DdmCodec.PackBytes(CodePoint.SECCHKCD, [1])).ToArray());
            extraRequest = await ClientSendsMoreAsync(stream);
        });

        var error = await Assert.ThrowsAsync<Db2UnsupportedFeatureException>(() => Db2Session.ConnectAsync(Secmec7TestConfig(port, 0)));
        Assert.Contains("listed security mechanism 9 as accepted but then rejected it", error.Message, StringComparison.Ordinal);
        await serverTask;
        Assert.False(extraRequest, "no third ACCSEC and no SECCHK");
    }

    [Fact]
    public async Task ExplicitMechanism3IsNeverUpgraded()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var extraRequest = true;
        var (port, serverTask) = await StartMockAsync(listener, async stream =>
        {
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.ACCSECRD, UInt16List(CodePoint.SECMEC, 7, 9, 10).Concat(DdmCodec.PackBytes(CodePoint.SECCHKCD, [1])).ToArray());
            extraRequest = await ClientSendsMoreAsync(stream);
        });

        await Assert.ThrowsAsync<Db2UnsupportedFeatureException>(() => Db2Session.ConnectAsync(Db2Config.Parse(
            $"Server=127.0.0.1:{port};Database=TESTDB;User=DB2INST1;Password=clear-password;Authentication=SERVER;Timeout=3")));
        await serverTask;
        Assert.False(extraRequest, "an explicitly configured mechanism 3 must not be switched");
    }

    [Fact]
    public async Task SwitchUserFallbackStaysRefusedAfterAutomaticSwitch()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        var serverPublic = Convert.FromHexString(Secmec9ServerPublicHex);
        var extraRequest = true;
        var (port, serverTask) = await StartMockAsync(listener, async stream =>
        {
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.ACCSECRD, UInt16List(CodePoint.SECMEC, 7, 9, 10).Concat(DdmCodec.PackBytes(CodePoint.SECCHKCD, [1])).ToArray());
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 9).Concat(DdmCodec.PackBytes(CodePoint.SECTKN, serverPublic)).ToArray());
            await ReadChainAsync(stream);
            await CompleteLoginAsync(stream);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await ReadChainAsync(stream);
                await SendAsync(stream, CodePoint.SQLCARD, SqlCard(-551, "42501"));
            }
            extraRequest = await ClientSendsMoreAsync(stream);
        });

        await using var session = await Db2Session.ConnectAsync(Secmec7TestConfig(port, 0));
        await Assert.ThrowsAsync<Db2UnsupportedFeatureException>(() => session.SwitchUserAsync("db2test2", "secret"));
        await serverTask;
        Assert.False(extraRequest, "the plaintext SECCHK fallback must stay refused after switching to mechanism 9");
    }
}
