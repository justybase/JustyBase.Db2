using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using JustyBase.Db2;
using JustyBase.Db2.Protocol;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed partial class SessionTests
{
    private static string TlsFixture(string name) => TestTlsCertificates.Path(name);

    private static readonly Lazy<X509Certificate2> TestServerCertificate = new(CreateTestServerCertificate);

    private static X509Certificate2 LoadTestServerCertificate() => TestServerCertificate.Value;

    private static X509Certificate2 CreateTestServerCertificate()
    {
        // Windows Schannel rejects the ephemeral key returned by CreateFromPemFile
        // for server authentication ("platform does not support ephemeral keys").
        // Import a PKCS#12 copy so the key is persisted for the test process, then
        // delete the container when the process exits.
        using var ephemeral = X509Certificate2.CreateFromPemFile(TlsFixture("server.pem"), TlsFixture("server-key.pem"));
        var certificate = X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pkcs12),
            password: null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                using var ecdsa = certificate.GetECDsaPrivateKey();
                if (ecdsa is ECDsaCng ecdsaCng) ecdsaCng.Key.Delete();
                using var rsa = certificate.GetRSAPrivateKey();
                if (rsa is RSACng rsaCng) rsaCng.Key.Delete();
            }
            catch { /* best-effort cleanup of the throwaway test key */ }
            certificate.Dispose();
        };
        return certificate;
    }

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
        var serverError = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            try
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
                serverError.TrySetResult(null);
            }
            catch (Exception error)
            {
                serverError.TrySetResult(error);
                throw;
            }
        });

        try
        {
            await using var session = await Db2Session.ConnectAsync(MutualTlsConfig(port));
            Assert.False(session.IsClosed);
        }
        catch (Exception clientError)
        {
            var observed = await serverError.Task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException(
                $"Client failure: {clientError.Message}; server failure: {observed?.Message ?? "<none>"}",
                clientError);
        }
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
