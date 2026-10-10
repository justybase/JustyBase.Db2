using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using JustyBase.Db2;
using JustyBase.Db2.Protocol;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed partial class SessionTests
{
    private static string TlsFixture(string name) => TestTlsCertificates.Path(name);

    private static X509Certificate2 LoadTestServerCertificate() =>
        X509Certificate2.CreateFromPemFile(TlsFixture("server.pem"), TlsFixture("server-key.pem"));

    private static bool ClientCertificateIsTrusted(X509Certificate? certificate, X509Certificate2 root)
    {
        if (certificate is null) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        return chain.Build(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
    }

    private static async Task ServeMutualTlsHandshakeAsync(SslStream stream)
    {
        var initial = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC }, initial.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        var auth = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB }, auth.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, DdmCodec.PackBytes(CodePoint.RDBINTTKN, [4, 28, 167, 50]));
        var initialize = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, initialize.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
    }

    private static Db2Config MutualTlsConfig(int port, string? clientCertificate = "client.pem", string? clientKey = "client-key.pem") => new()
    {
        Host = "localhost", Port = port, Database = "TESTDB", User = "db2inst1", Password = "secret",
        Timeout = TimeSpan.FromSeconds(8), BlockSize = 65535, SecurityMechanism = 3, UseSsl = true,
        SslRootCaPath = TlsFixture("ca.pem"),
        SslClientCertPath = clientCertificate is null ? "" : TlsFixture(clientCertificate),
        SslClientKeyPath = clientKey is null ? "" : TlsFixture(clientKey),
    };

    [Fact]
    public void ServerCertificateChainBuildsAgainstTheTestCa()
    {
        var root = X509CertificateLoader.LoadCertificateFromFile(TlsFixture("ca.pem"));
        var leaf = X509CertificateLoader.LoadCertificateFromFile(TlsFixture("server.pem"));
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        Assert.True(chain.Build(leaf), string.Join("; ", chain.ChainStatus.Select(status => $"{status.Status}:{status.StatusInformation}")));
    }

    [Fact]
    public async Task MutualTlsWithATrustedClientCertificateCompletesTheHandshake()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverCertificate = LoadTestServerCertificate();
        var root = X509CertificateLoader.LoadCertificateFromFile(TlsFixture("ca.pem"));
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) => ClientCertificateIsTrusted(certificate, root));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCertificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });
            await ServeMutualTlsHandshakeAsync(ssl);
        });

        await using var session = await Db2Session.ConnectAsync(MutualTlsConfig(port));
        Assert.False(session.IsClosed);
        await serverTask;
    }

    [Fact]
    public async Task MutualTlsRejectsAClientWithoutACertificate()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverCertificate = LoadTestServerCertificate();
        var root = X509CertificateLoader.LoadCertificateFromFile(TlsFixture("ca.pem"));
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) => ClientCertificateIsTrusted(certificate, root));
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                });
            }
            catch (AuthenticationException) { }
        });

        await Assert.ThrowsAnyAsync<Exception>(() => Db2Session.ConnectAsync(MutualTlsConfig(port, clientCertificate: null, clientKey: null)));
        await serverTask;
    }

    [Fact]
    public async Task MutualTlsRejectsAClientCertificateFromAnUntrustedCa()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverCertificate = LoadTestServerCertificate();
        var root = X509CertificateLoader.LoadCertificateFromFile(TlsFixture("ca.pem"));
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) => ClientCertificateIsTrusted(certificate, root));
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                });
            }
            catch (AuthenticationException) { }
        });

        await Assert.ThrowsAnyAsync<Exception>(() => Db2Session.ConnectAsync(MutualTlsConfig(port, "other-client.pem", "other-client-key.pem")));
        await serverTask;
    }
}
