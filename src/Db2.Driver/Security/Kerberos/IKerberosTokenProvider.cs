namespace JustyBase.Db2.Security.Kerberos;

/// <summary>
/// Produces the GSS-API tokens carried by DRDA security mechanism 11. One provider serves exactly one
/// physical connection attempt and is disposed when the handshake ends, successfully or not.
/// </summary>
internal interface IKerberosTokenProvider : IDisposable
{
    /// <summary>True when the server's reply token must be processed to finish mutual authentication.</summary>
    bool RequiresServerToken { get; }

    /// <summary>Returns the initial context token (a standard GSS-API Kerberos AP-REQ token).</summary>
    Task<byte[]> CreateInitialTokenAsync(CancellationToken cancellationToken);

    /// <summary>Completes the security context with the server's reply token (AP-REP).</summary>
    Task CompleteAsync(byte[] serverToken, CancellationToken cancellationToken);
}

/// <summary>Compatibility mode: the caller supplies a ready GSS-API token in a file.</summary>
internal sealed class TokenFileKerberosTokenProvider(string path) : IKerberosTokenProvider
{
    public bool RequiresServerToken => false;

    public async Task<byte[]> CreateInitialTokenAsync(CancellationToken cancellationToken)
    {
        var token = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (token.Length == 0) throw new Db2ConfigurationException("gssapi_token_file is empty; it must contain a GSS-API token.");
        return token;
    }

    public Task CompleteAsync(byte[] serverToken, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() { }
}
