using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace JustyBase.Db2.Security;

/// <summary>
/// Owns the client certificate and private-key material used for one TLS handshake.
/// <para>
/// On Windows, Schannel rejects the ephemeral key produced by
/// <see cref="X509Certificate2.CreateFromPemFile(string, string)"/> for client
/// authentication. The certificate is therefore re-imported into a user key
/// container, and this lease removes that container again on dispose. Every TLS
/// client path uses this type so the certificate, the CNG key handle and the
/// persisted container are always released together, including when the handshake
/// fails.
/// </para>
/// </summary>
internal sealed class TlsClientCertificate : IDisposable
{
    private X509Certificate2? _certificate;
    private CngKey? _key;
    private IDisposable? _keyHandle;

    private TlsClientCertificate(X509Certificate2 certificate, CngKey? key, IDisposable? keyHandle)
    {
        _certificate = certificate;
        _key = key;
        _keyHandle = keyHandle;
    }

    /// <summary>Loads the configured client certificate, or returns <see langword="null"/> when none is configured.</summary>
    public static TlsClientCertificate? Load(string? certificatePath, string? keyPath)
    {
        if (string.IsNullOrEmpty(certificatePath)) return null;

        var ephemeral = string.IsNullOrEmpty(keyPath)
            ? X509CertificateLoader.LoadCertificateFromFile(certificatePath)
            : X509Certificate2.CreateFromPemFile(certificatePath, keyPath);

        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(keyPath))
            return new TlsClientCertificate(ephemeral, key: null, keyHandle: null);

        // Schannel needs a persisted key container; an ephemeral PEM key fails the
        // handshake with "the platform does not support ephemeral keys".
        X509Certificate2 certificate;
        using (ephemeral)
        {
            certificate = X509CertificateLoader.LoadPkcs12(
                ephemeral.Export(X509ContentType.Pkcs12),
                password: null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        }

        if (certificate.GetRSAPrivateKey() is RSACng rsa)
            return new TlsClientCertificate(certificate, rsa.Key, rsa);
        if (certificate.GetECDsaPrivateKey() is ECDsaCng ecdsa)
            return new TlsClientCertificate(certificate, ecdsa.Key, ecdsa);
        return new TlsClientCertificate(certificate, key: null, keyHandle: null);
    }

    /// <summary>The certificate to place in <see cref="System.Net.Security.SslClientAuthenticationOptions.ClientCertificates"/>.</summary>
    public X509Certificate2 Certificate => _certificate ?? throw new ObjectDisposedException(nameof(TlsClientCertificate));

    public void Dispose()
    {
        var certificate = _certificate;
        if (certificate is null) return;
        _certificate = null;

        // Delete the container before disposing the handle that resolves its name.
        _key?.Delete();
        _key = null;
        _keyHandle?.Dispose();
        _keyHandle = null;
        certificate.Dispose();
    }
}
