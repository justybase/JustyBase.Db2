using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace JustyBase.Db2.Tests;

/// <summary>
/// Creates throwaway TLS material for the mutual-TLS tests at test setup. Nothing is committed and no
/// key survives the test run beyond the git-ignored output directory. Trust is deterministic:
/// <c>client.pem</c> chains to <c>ca.pem</c>, while <c>other-client.pem</c> chains to the unrelated
/// <c>other-ca.pem</c>.
/// </summary>
internal static class TestTlsCertificates
{
    private static readonly Lazy<string> OutputDirectory = new(WriteAll);

    /// <summary>Returns the absolute path of a generated file such as <c>server.pem</c> or <c>client-key.pem</c>.</summary>
    public static string Path(string name) => System.IO.Path.Combine(OutputDirectory.Value, name);

    private static string WriteAll()
    {
        var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "generated-tls");
        if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        System.IO.Directory.CreateDirectory(directory);

        using var ca = CreateAuthority("CN=db2-driver-test-ca");
        using var otherCa = CreateAuthority("CN=db2-driver-other-test-ca");
        File.WriteAllText(System.IO.Path.Combine(directory, "ca.pem"), ca.ExportCertificatePem());
        File.WriteAllText(System.IO.Path.Combine(directory, "other-ca.pem"), otherCa.ExportCertificatePem());

        WriteLeaf(directory, "server", Issue(ca, "CN=localhost", server: true));
        WriteLeaf(directory, "client", Issue(ca, "CN=db2-driver-test-client", server: false));
        WriteLeaf(directory, "other-client", Issue(otherCa, "CN=db2-driver-other-test-client", server: false));
        return directory;
    }

    private static X509Certificate2 CreateAuthority(string subject)
    {
        // The key stays alive with the certificate's private key so the authority can sign leaves.
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static Leaf Issue(X509Certificate2 authority, string subject, bool server)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], critical: false));
        if (server)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }
        var issued = request.Create(
            authority,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30),
            RandomNumberGenerator.GetBytes(16));
        return new Leaf(issued.CopyWithPrivateKey(key), key);
    }

    private static void WriteLeaf(string directory, string prefix, Leaf leaf)
    {
        File.WriteAllText(System.IO.Path.Combine(directory, $"{prefix}.pem"), leaf.Certificate.ExportCertificatePem());
        File.WriteAllText(System.IO.Path.Combine(directory, $"{prefix}-key.pem"), leaf.Key.ExportPkcs8PrivateKeyPem());
        leaf.Dispose();
    }

    private sealed class Leaf(X509Certificate2 certificate, ECDsa key) : IDisposable
    {
        public X509Certificate2 Certificate { get; } = certificate;
        public ECDsa Key { get; } = key;

        public void Dispose()
        {
            Certificate.Dispose();
            Key.Dispose();
        }
    }
}
