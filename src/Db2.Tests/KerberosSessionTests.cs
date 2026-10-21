using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using JustyBase.Db2;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Security.Kerberos;
using Xunit;

namespace JustyBase.Db2.Tests;

/// <summary>
/// Protocol-level tests for DRDA security mechanism 11 with a scripted server and a stand-in token provider.
/// The framing asserted here is what IBM's own clients (CLP, JCC) put on the wire to a real Db2 11.5.8;
/// the real GSS-API exchange is covered by scripts/test-db2-kerberos.sh.
/// </summary>
public sealed partial class SessionTests
{
    private static readonly byte[] KerberosClientToken = [0x60, 0x82, 0x02, 0xf3, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x12, 0x01, 0x02, 0x02, 0x01, 0x00, 0x6e, 0x82];
    private static readonly byte[] KerberosServerToken = [0x60, 0x81, 0x99, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x12, 0x01, 0x02, 0x02, 0x02, 0x00, 0x6f, 0x81];

    private sealed class ScriptedKerberosProvider(byte[]? initialToken = null, bool requiresServerToken = true, Exception? failure = null, bool blockUntilCancelled = false)
        : IKerberosTokenProvider
    {
        public int Disposals;
        public int InitialTokenCalls;
        public byte[]? ReceivedServerToken;
        public bool RequiresServerToken => requiresServerToken;

        public async Task<byte[]> CreateInitialTokenAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref InitialTokenCalls);
            if (blockUntilCancelled) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (failure is not null) throw failure;
            return initialToken ?? KerberosClientToken;
        }

        public Task CompleteAsync(byte[] serverToken, CancellationToken cancellationToken)
        {
            ReceivedServerToken = serverToken;
            return Task.CompletedTask;
        }

        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    private static Db2Config KerberosTestConfig(int port, Action<Db2Config>? customize = null)
    {
        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", SecurityMechanism = 11,
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535,
        };
        customize?.Invoke(config);
        return config;
    }

    private static async Task ExpectOpeningWithoutTokenAsync(Stream stream)
    {
        var opening = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC }, opening.Select(packet => packet.CodePoint));
        var accsec = DdmCodec.ParseParameters(opening[1].Payload);
        Assert.Equal((ushort)11, BinaryPrimitives.ReadUInt16BigEndian(accsec[(ushort)CodePoint.SECMEC]));
        Assert.DoesNotContain((ushort)CodePoint.SECTKN, accsec.Keys);
        Assert.DoesNotContain((ushort)CodePoint.PASSWORD, accsec.Keys);
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 11));
    }

    [Fact]
    public async Task KerberosSecchkMatchesIbmClientFramingAndNeverCarriesPasswordOrUser()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ExpectOpeningWithoutTokenAsync(stream);

            var check = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.SECTKN, (ushort)CodePoint.ACCRDB }, check.Select(packet => packet.CodePoint));
            var secchk = DdmCodec.ParseParameters(check[0].Payload);
            Assert.Equal((ushort)11, BinaryPrimitives.ReadUInt16BigEndian(secchk[(ushort)CodePoint.SECMEC]));
            Assert.Contains((ushort)CodePoint.RDBNAM, secchk.Keys);
            // IBM clients send neither USRID nor PASSWORD, and the token is not a SECCHK parameter.
            Assert.DoesNotContain((ushort)CodePoint.PASSWORD, secchk.Keys);
            Assert.DoesNotContain((ushort)CodePoint.USRID, secchk.Keys);
            Assert.DoesNotContain((ushort)CodePoint.SECTKN, secchk.Keys);
            // The token travels as its own OBJECT DSS in the same correlation as the SECCHK.
            Assert.Equal(DssCodec.Object, check[1].Header.Type);
            Assert.True(check[0].Header.SameId);
            Assert.Equal(check[0].Header.CorrelationId, check[1].Header.CorrelationId);
            Assert.Equal(KerberosClientToken, check[1].Payload);

            await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
            await SendAsync(stream, CodePoint.SECTKN, KerberosServerToken, true);
            await SendAsync(stream, CodePoint.ACCRDBRM, DdmCodec.PackBytes(CodePoint.RDBINTTKN, [4, 28, 167, 50]));

            var initialize = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, initialize.Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        });

        var provider = new ScriptedKerberosProvider();
        await using (var session = await Db2Session.ConnectAsync(
            KerberosTestConfig(port, c => { c.User = "db2inst1"; c.Password = "must-never-be-sent"; }), () => provider, CancellationToken.None))
        {
            Assert.False(session.IsClosed);
        }
        await serverTask;
        Assert.Equal(KerberosServerToken, provider.ReceivedServerToken);
        Assert.Equal(1, provider.Disposals);
    }

    [Fact]
    public async Task KerberosHandshakeFailsWhenTheServerReturnsNoMutualAuthenticationToken()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ExpectOpeningWithoutTokenAsync(stream);
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
            await SendAsync(stream, CodePoint.ACCRDBRM, DdmCodec.PackBytes(CodePoint.RDBINTTKN, [4, 28, 167, 50]));
        });

        var provider = new ScriptedKerberosProvider();
        var error = await Assert.ThrowsAsync<Db2KerberosException>(() => Db2Session.ConnectAsync(KerberosTestConfig(port), () => provider, CancellationToken.None));
        Assert.Contains("server token", error.Message, StringComparison.Ordinal);
        await serverTask;
        Assert.Equal(1, provider.Disposals);
    }

    [Fact]
    public async Task KerberosCredentialFailureStopsBeforeSecchkAndNeverReconnectsWithAPassword()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var framesAfterNegotiation = new List<ushort>();
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ExpectOpeningWithoutTokenAsync(stream);
            // After the failed credential acquisition the client closes the connection without another frame.
            try
            {
                foreach (var packet in await ReadChainAsync(stream)) framesAfterNegotiation.Add(packet.CodePoint);
            }
            catch (EndOfStreamException) { }
        });

        var provider = new ScriptedKerberosProvider(failure: new Db2KerberosException("No Kerberos credentials available."));
        var error = await Assert.ThrowsAsync<Db2KerberosException>(() => Db2Session.ConnectAsync(
            KerberosTestConfig(port, c => { c.User = "db2inst1"; c.Password = "valid-password-that-must-not-be-used"; }), () => provider, CancellationToken.None));
        Assert.DoesNotContain("valid-password", error.Message, StringComparison.Ordinal);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(framesAfterNegotiation);
        Assert.Equal(1, provider.Disposals);

        // No second connection attempt (for example with SECMEC 3 or 9) follows the Kerberos failure.
        await Task.Delay(300);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task ServerOfferingAPasswordMechanismIsRefusedWithoutSendingCredentials()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var framesAfterNegotiation = new List<ushort>();
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var opening = await ReadChainAsync(stream);
            Assert.Equal((ushort)11, BinaryPrimitives.ReadUInt16BigEndian(DdmCodec.ParseParameters(opening[1].Payload)[(ushort)CodePoint.SECMEC]));
            await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
            try
            {
                foreach (var packet in await ReadChainAsync(stream)) framesAfterNegotiation.Add(packet.CodePoint);
            }
            catch (EndOfStreamException) { }
        });

        var provider = new ScriptedKerberosProvider();
        await Assert.ThrowsAsync<Db2UnsupportedFeatureException>(() => Db2Session.ConnectAsync(
            KerberosTestConfig(port, c => { c.User = "db2inst1"; c.Password = "secret"; }), () => provider, CancellationToken.None));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(framesAfterNegotiation);
        Assert.Equal(0, provider.InitialTokenCalls);
        await Task.Delay(300);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task KerberosRejectionBySecchkcdSurfacesAsAuthenticationFailureAndReleasesTheProvider()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ExpectOpeningWithoutTokenAsync(stream);
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0x0f]));
        });

        var provider = new ScriptedKerberosProvider();
        var error = await Assert.ThrowsAsync<Db2AuthenticationException>(() => Db2Session.ConnectAsync(KerberosTestConfig(port), () => provider, CancellationToken.None));
        Assert.Equal((byte)0x0f, error.SecurityCheckCode);
        await serverTask;
        Assert.Equal(1, provider.Disposals);
    }

    [Fact]
    public async Task CancellingWhileCredentialsAreBeingAcquiredReleasesTheProviderAndTheSocket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ExpectOpeningWithoutTokenAsync(stream);
            try { await ReadChainAsync(stream); } catch (EndOfStreamException) { }
        });

        var provider = new ScriptedKerberosProvider(blockUntilCancelled: true);
        using var cancellation = new CancellationTokenSource();
        var connect = Db2Session.ConnectAsync(KerberosTestConfig(port), () => provider, cancellation.Token);
        await Task.Delay(300);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, provider.Disposals);
    }

    [Fact]
    public async Task ConnectTimeoutStopsABlockedKerberosExchange()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ExpectOpeningWithoutTokenAsync(stream);
            try { await ReadChainAsync(stream); } catch (EndOfStreamException) { }
        });

        var provider = new ScriptedKerberosProvider(blockUntilCancelled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Db2Session.ConnectAsync(
            KerberosTestConfig(port, c => c.Timeout = TimeSpan.FromMilliseconds(400)), () => provider, CancellationToken.None));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, provider.Disposals);
    }

    [Theory]
    [InlineData("token+ccache")]
    [InlineData("ccache+keytab")]
    [InlineData("token+keytab")]
    public async Task AmbiguousKerberosConfigurationFailsBeforeAnySocketIsOpened(string combination)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var config = KerberosTestConfig(port, c =>
        {
            c.KerberosSpn = "db2inst1/db2.example.com@EXAMPLE.COM";
            c.KerberosPrincipal = "user@EXAMPLE.COM";
            if (combination.Contains("token", StringComparison.Ordinal)) c.GssapiTokenFile = "/tmp/token.bin";
            if (combination.Contains("ccache", StringComparison.Ordinal)) c.Krb5CcacheFile = "/tmp/krb5cc_test";
            if (combination.Contains("keytab", StringComparison.Ordinal)) c.Krb5KeytabFile = "/tmp/user.keytab";
        });
        var error = await Assert.ThrowsAsync<Db2ConfigurationException>(() => Db2Session.ConnectAsync(config));
        Assert.Contains("ambiguous", error.Message, StringComparison.Ordinal);
        Assert.Equal(Db2OperationOutcome.NotStarted, ((IDb2Error)error).Outcome);
        await Task.Delay(200);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task MissingKeytabIsAConfigurationErrorBeforeAnySocketIsOpened()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var config = KerberosTestConfig(port, c =>
        {
            c.KerberosSpn = "db2inst1/db2.example.com@EXAMPLE.COM";
            c.KerberosPrincipal = "user@EXAMPLE.COM";
            c.Krb5KeytabFile = Path.Combine(Path.GetTempPath(), "JustyBase.Db2-missing-" + Guid.NewGuid().ToString("N") + ".keytab");
        });
        if (!OperatingSystem.IsLinux())
        {
            await Assert.ThrowsAsync<Db2UnsupportedFeatureException>(() => Db2Session.ConnectAsync(config));
            return;
        }
        var error = await Assert.ThrowsAsync<Db2ConfigurationException>(() => Db2Session.ConnectAsync(config));
        Assert.Contains("krb5_keytab", error.Message, StringComparison.Ordinal);
        await Task.Delay(200);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task KerberosMechanismWithoutAnyCredentialSourceOrServicePrincipalIsAConfigurationError()
    {
        var error = await Assert.ThrowsAsync<Db2ConfigurationException>(() => Db2Session.ConnectAsync(new Db2Config
        {
            Host = "127.0.0.1", Port = 1, Database = "TESTDB", SecurityMechanism = 11,
        }));
        Assert.Contains("spn", error.Message, StringComparison.Ordinal);
        Assert.Contains("gssapi_token_file", error.Message, StringComparison.Ordinal);
    }
}
