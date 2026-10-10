namespace JustyBase.Db2.Security.Kerberos;

internal enum KerberosCredentialSource
{
    /// <summary>A caller-supplied GSS-API token read from <c>gssapi_token_file</c>.</summary>
    TokenFile,
    /// <summary>An existing credential cache (<c>krb5_ccache</c>).</summary>
    CredentialCache,
    /// <summary>A client keytab (<c>krb5_keytab</c> + principal).</summary>
    Keytab,
    /// <summary>The platform default credential cache.</summary>
    Default,
}

/// <summary>
/// Resolves the one credential source of a SECMEC 11 connection before any socket is opened. Setting more
/// than one explicit source is a configuration error: the driver never guesses which one wins.
/// </summary>
internal sealed class KerberosCredentialPlan
{
    private static readonly string[] CredentialStoreSchemes = ["FILE:", "DIR:", "KEYRING:", "KCM:", "MEMORY:", "API:"];

    private KerberosCredentialPlan(KerberosCredentialSource source, string? path, string? principal, string? servicePrincipal)
    {
        Source = source;
        Path = path;
        Principal = principal;
        ServicePrincipal = servicePrincipal;
    }

    public KerberosCredentialSource Source { get; }
    public string? Path { get; }
    public string? Principal { get; }
    public string? ServicePrincipal { get; }

    /// <summary>Name passed to the GSS credential store (<c>FILE:/path</c> unless the caller gave a typed name).</summary>
    public string? CredentialStoreName => Path is null ? null : NormalizeStoreName(Path);

    public static KerberosCredentialPlan Resolve(Db2Config config)
    {
        var explicitSources = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(config.GssapiTokenFile)) explicitSources.Add("gssapi_token_file");
        if (!string.IsNullOrWhiteSpace(config.Krb5CcacheFile)) explicitSources.Add("krb5_ccache");
        if (!string.IsNullOrWhiteSpace(config.Krb5KeytabFile)) explicitSources.Add("krb5_keytab");
        if (explicitSources.Count > 1)
            throw new Db2ConfigurationException($"Kerberos credential source is ambiguous: {string.Join(", ", explicitSources)} are all set. Specify only one of gssapi_token_file, krb5_ccache or krb5_keytab.");

        if (explicitSources.Count == 1 && explicitSources[0] == "gssapi_token_file")
        {
            if (!File.Exists(config.GssapiTokenFile))
                throw new Db2ConfigurationException($"gssapi_token_file '{config.GssapiTokenFile}' does not exist.");
            return new KerberosCredentialPlan(KerberosCredentialSource.TokenFile, config.GssapiTokenFile, null, null);
        }

        var spn = config.KerberosSpn.Trim();
        if (spn.Length == 0)
            throw new Db2ConfigurationException("Security mechanism 11 (Kerberos/GSSAPI) needs the Db2 service principal (spn=instance/host.example.com[@REALM]) with krb5_ccache, krb5_keytab or the default credential cache; alternatively supply a ready token with gssapi_token_file.");
        spn = QualifyServicePrincipal(spn, config.KerberosRealm);
        ValidateName(spn, "spn");

        if (!OperatingSystem.IsLinux())
            throw new Db2UnsupportedFeatureException("Kerberos credential acquisition (krb5_ccache, krb5_keytab, default credentials) is only supported on Linux; use gssapi_token_file on this platform.");
        EnsureKrb5ConfigIsProcessWide(config.Krb5ConfigFile);

        var principal = config.KerberosPrincipal.Trim();
        if (principal.Length > 0) ValidateName(principal, "krb5_principal");

        if (explicitSources.Count == 1 && explicitSources[0] == "krb5_ccache")
        {
            EnsureFileExists(config.Krb5CcacheFile, "krb5_ccache");
            return new KerberosCredentialPlan(KerberosCredentialSource.CredentialCache, config.Krb5CcacheFile, principal.Length == 0 ? null : principal, spn);
        }
        if (explicitSources.Count == 1)
        {
            if (principal.Length == 0)
                throw new Db2ConfigurationException("krb5_keytab needs the client principal (krb5_principal=user@REALM) to pick the key to use.");
            EnsureFileExists(config.Krb5KeytabFile, "krb5_keytab");
            return new KerberosCredentialPlan(KerberosCredentialSource.Keytab, config.Krb5KeytabFile, principal, spn);
        }
        return new KerberosCredentialPlan(KerberosCredentialSource.Default, null, principal.Length == 0 ? null : principal, spn);
    }

    public IKerberosTokenProvider CreateProvider() => Source == KerberosCredentialSource.TokenFile
        ? new TokenFileKerberosTokenProvider(Path!)
        : new GssapiKerberosTokenProvider(this);

    private static string QualifyServicePrincipal(string spn, string realm)
    {
        var at = spn.LastIndexOf('@');
        realm = realm.Trim();
        if (realm.Length == 0) return spn;
        if (at < 0) return $"{spn}@{realm}";
        if (!string.Equals(spn[(at + 1)..], realm, StringComparison.Ordinal))
            throw new Db2ConfigurationException($"spn realm '{spn[(at + 1)..]}' does not match realm '{realm}'.");
        return spn;
    }

    private static void ValidateName(string value, string option)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
                throw new Db2ConfigurationException($"{option} must not contain whitespace or control characters.");
        }
    }

    private static void EnsureFileExists(string name, string option)
    {
        var store = NormalizeStoreName(name);
        // Only file-backed stores can be checked up front; KCM:, KEYRING: etc. are resolved by libkrb5.
        if (!store.StartsWith("FILE:", StringComparison.Ordinal)) return;
        var path = store["FILE:".Length..];
        if (!File.Exists(path)) throw new Db2ConfigurationException($"{option} '{path}' does not exist.");
    }

    private static string NormalizeStoreName(string name)
    {
        foreach (var scheme in CredentialStoreSchemes)
            if (name.StartsWith(scheme, StringComparison.Ordinal)) return name;
        return "FILE:" + System.IO.Path.GetFullPath(name);
    }

    /// <summary>
    /// MIT libkrb5 reads KRB5_CONFIG once per process, so one connection cannot select its own krb5.conf without
    /// changing process-global state, which this driver never does. A configured file must therefore match the
    /// process environment.
    /// </summary>
    private static void EnsureKrb5ConfigIsProcessWide(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return;
        var active = Environment.GetEnvironmentVariable("KRB5_CONFIG");
        if (!string.IsNullOrEmpty(active) && string.Equals(System.IO.Path.GetFullPath(active), System.IO.Path.GetFullPath(configured), StringComparison.Ordinal)) return;
        throw new Db2ConfigurationException("krb5_config cannot be applied per connection: libkrb5 reads KRB5_CONFIG once per process. Start the process with KRB5_CONFIG pointing at the same file, or remove krb5_config.");
    }
}
