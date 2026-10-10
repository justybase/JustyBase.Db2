using System.Runtime.InteropServices;
using JustyBase.Db2;
using JustyBase.Db2.Security.Kerberos;
using Xunit;

namespace JustyBase.Db2.Tests;

/// <summary>Offline tests for Kerberos credential-source resolution and the token providers (no KDC needed).</summary>
public sealed class KerberosAcquisitionTests : IDisposable
{
    private const string Spn = "db2inst1/db2.example.com@EXAMPLE.COM";
    private readonly string _directory = Directory.CreateTempSubdirectory("JustyBase.Db2-krb-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, true);

    private string TempFile(string name, byte[]? content = null)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content ?? [1, 2, 3, 4]);
        return path;
    }

    private static Db2Config Config(Action<Db2Config>? customize = null)
    {
        var config = new Db2Config { Host = "db2.example.com", Database = "TESTDB", SecurityMechanism = 11, KerberosSpn = Spn };
        customize?.Invoke(config);
        return config;
    }

    private static bool GssapiAvailable => OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libgssapi_krb5.so.2", out var handle) && Free(handle);
    private static bool Free(nint handle) { NativeLibrary.Free(handle); return true; }

    [Fact]
    public void TokenFileIsTheOnlySourceThatNeedsNoServicePrincipalOrPlatformSupport()
    {
        var plan = KerberosCredentialPlan.Resolve(new Db2Config { SecurityMechanism = 11, GssapiTokenFile = TempFile("token.bin") });
        Assert.Equal(KerberosCredentialSource.TokenFile, plan.Source);
        Assert.IsType<TokenFileKerberosTokenProvider>(plan.CreateProvider());
    }

    [Fact]
    public void MissingTokenFileIsAConfigurationError()
    {
        var error = Assert.Throws<Db2ConfigurationException>(() => KerberosCredentialPlan.Resolve(
            new Db2Config { SecurityMechanism = 11, GssapiTokenFile = Path.Combine(_directory, "absent.bin") }));
        Assert.Contains("gssapi_token_file", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void MoreThanOneExplicitSourceIsRejectedInsteadOfPickingAWinner(bool token, bool ccache, bool keytab)
    {
        var error = Assert.Throws<Db2ConfigurationException>(() => KerberosCredentialPlan.Resolve(Config(c =>
        {
            c.KerberosPrincipal = "user@EXAMPLE.COM";
            if (token) c.GssapiTokenFile = TempFile("token.bin");
            if (ccache) c.Krb5CcacheFile = TempFile("cc");
            if (keytab) c.Krb5KeytabFile = TempFile("user.keytab");
        })));
        Assert.Contains("ambiguous", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAServicePrincipalOrTokenFileTheErrorNamesTheOptions()
    {
        var error = Assert.Throws<Db2ConfigurationException>(() => KerberosCredentialPlan.Resolve(new Db2Config { SecurityMechanism = 11 }));
        Assert.Contains("spn", error.Message, StringComparison.Ordinal);
        Assert.Contains("gssapi_token_file", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialCacheDefaultAndKeytabSourcesAreSelectedByWhatIsConfigured()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<Db2UnsupportedFeatureException>(() => KerberosCredentialPlan.Resolve(Config()));
            return;
        }
        var cache = TempFile("cc");
        var keytab = TempFile("user.keytab");

        var ccachePlan = KerberosCredentialPlan.Resolve(Config(c => c.Krb5CcacheFile = cache));
        Assert.Equal(KerberosCredentialSource.CredentialCache, ccachePlan.Source);
        Assert.Equal("FILE:" + cache, ccachePlan.CredentialStoreName);
        Assert.Null(ccachePlan.Principal);

        var keytabPlan = KerberosCredentialPlan.Resolve(Config(c => { c.Krb5KeytabFile = keytab; c.KerberosPrincipal = "user@EXAMPLE.COM"; }));
        Assert.Equal(KerberosCredentialSource.Keytab, keytabPlan.Source);
        Assert.Equal("user@EXAMPLE.COM", keytabPlan.Principal);
        Assert.Equal(Spn, keytabPlan.ServicePrincipal);

        var defaultPlan = KerberosCredentialPlan.Resolve(Config());
        Assert.Equal(KerberosCredentialSource.Default, defaultPlan.Source);
        Assert.Null(defaultPlan.CredentialStoreName);
        Assert.IsType<GssapiKerberosTokenProvider>(defaultPlan.CreateProvider());
    }

    [Fact]
    public void MissingCredentialFilesAndMissingKeytabPrincipalAreConfigurationErrors()
    {
        if (!OperatingSystem.IsLinux()) return;
        var missing = Path.Combine(_directory, "absent");
        Assert.Contains("krb5_ccache", Assert.Throws<Db2ConfigurationException>(() => KerberosCredentialPlan.Resolve(Config(c => c.Krb5CcacheFile = missing))).Message, StringComparison.Ordinal);
        Assert.Contains("krb5_keytab", Assert.Throws<Db2ConfigurationException>(() => KerberosCredentialPlan.Resolve(Config(c => { c.Krb5KeytabFile = missing; c.KerberosPrincipal = "u@R"; }))).Message, StringComparison.Ordinal);
        Assert.Contains("krb5_principal", Assert.Throws<Db2ConfigurationException>(() => KerberosCredentialPlan.Resolve(Config(c => c.Krb5KeytabFile = TempFile("k.keytab")))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RealmQualifiesABareServicePrincipalAndRejectsAMismatch()
    {
        if (!OperatingSystem.IsLinux()) return;
        var plan = KerberosCredentialPlan.Resolve(Config(c => { c.KerberosSpn = "db2inst1/db2.example.com"; c.KerberosRealm = "EXAMPLE.COM"; }));
        Assert.Equal(Spn, plan.ServicePrincipal);
        Assert.Throws<Db2ConfigurationException>(() => KerberosCredentialPlan.Resolve(Config(c => c.KerberosRealm = "OTHER.REALM")));
    }

    [Theory]
    [InlineData("db2inst1/db2.example.com @EXAMPLE.COM")]
    [InlineData("db2inst1/db2.example.com\n@EXAMPLE.COM")]
    public void WhitespaceAndControlCharactersInPrincipalNamesAreRejected(string spn)
    {
        Assert.Throws<Db2ConfigurationException>(() => KerberosCredentialPlan.Resolve(Config(c => c.KerberosSpn = spn)));
    }

    [Fact]
    public void Krb5ConfigMustMatchTheProcessWideKrb5ConfigVariable()
    {
        if (!OperatingSystem.IsLinux()) return;
        var original = Environment.GetEnvironmentVariable("KRB5_CONFIG");
        try
        {
            var file = TempFile("krb5.conf");
            Environment.SetEnvironmentVariable("KRB5_CONFIG", null);
            var error = Assert.Throws<Db2ConfigurationException>(() => KerberosCredentialPlan.Resolve(Config(c => c.Krb5ConfigFile = file)));
            Assert.Contains("KRB5_CONFIG", error.Message, StringComparison.Ordinal);
            Environment.SetEnvironmentVariable("KRB5_CONFIG", file);
            Assert.Equal(KerberosCredentialSource.Default, KerberosCredentialPlan.Resolve(Config(c => c.Krb5ConfigFile = file)).Source);
        }
        finally { Environment.SetEnvironmentVariable("KRB5_CONFIG", original); }
    }

    [Fact]
    public async Task TokenFileProviderReadsTheTokenAndRejectsAnEmptyFile()
    {
        var token = new byte[] { 0x60, 0x82, 1, 2, 3 };
        using (var provider = new TokenFileKerberosTokenProvider(TempFile("token.bin", token)))
        {
            Assert.False(provider.RequiresServerToken);
            Assert.Equal(token, await provider.CreateInitialTokenAsync(CancellationToken.None));
            await provider.CompleteAsync([1], CancellationToken.None);
        }
        using var empty = new TokenFileKerberosTokenProvider(TempFile("empty.bin", []));
        await Assert.ThrowsAsync<Db2ConfigurationException>(() => empty.CreateInitialTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CorruptCredentialCacheFailsAsKerberosErrorWithoutLeakingContents()
    {
        if (!GssapiAvailable) return;
        var secret = "TOP-SECRET-TICKET-BYTES";
        var cache = TempFile("corrupt.cc", System.Text.Encoding.ASCII.GetBytes(secret));
        var plan = KerberosCredentialPlan.Resolve(Config(c => c.Krb5CcacheFile = cache));
        using var provider = plan.CreateProvider();
        var error = await Assert.ThrowsAsync<Db2KerberosException>(() => provider.CreateInitialTokenAsync(CancellationToken.None));
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.Equal(Db2ErrorKind.Authentication, error.ErrorKind);
    }

    [Fact]
    public async Task GssProviderDisposalIsDeterministicIdempotentAndRefusesFurtherUse()
    {
        if (!GssapiAvailable) return;
        var plan = KerberosCredentialPlan.Resolve(Config());
        var unused = plan.CreateProvider();
        unused.Dispose();
        unused.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => unused.CreateInitialTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GssProviderHonoursAnAlreadyCancelledToken()
    {
        if (!GssapiAvailable) return;
        using var provider = KerberosCredentialPlan.Resolve(Config()).CreateProvider();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CreateInitialTokenAsync(cancelled.Token));
    }

    [Fact]
    public void ConnectionStringKerberosOptionsMapToConfigAndTyposAreRejected()
    {
        var config = Db2Config.Parse("Server=db2.example.com:50000;Database=TESTDB;Authentication=KERBEROS;SPN=db2inst1/db2.example.com@EXAMPLE.COM;Krb5Ccache=/tmp/cc;Krb5Principal=u@EXAMPLE.COM;Krb5Keytab=/tmp/k;Krb5Config=/tmp/c;KerberosRealm=EXAMPLE.COM");
        Assert.Equal((ushort)11, config.SecurityMechanism);
        Assert.Equal(("/tmp/cc", "u@EXAMPLE.COM", "/tmp/k", "/tmp/c", "EXAMPLE.COM"),
            (config.Krb5CcacheFile, config.KerberosPrincipal, config.Krb5KeytabFile, config.Krb5ConfigFile, config.KerberosRealm));
        Assert.Throws<Db2ConfigurationException>(() => Db2Config.Parse("Server=h;Database=D;Authentication=KERBEROS;Krb5Cachee=/tmp/cc"));
        Assert.Throws<Db2ConfigurationException>(() => Db2Config.Parse("db2://h/D?security_mechanism=11&kerberos_keytabb=/tmp/k"));
    }

    [Fact]
    public void ConnectionPoolKeyChangesWithEveryKerberosIdentityInput()
    {
        const string baseString = "Server=h;Database=D;Authentication=KERBEROS;spn=db2inst1/h@R;";
        var keys = new[]
        {
            baseString + "krb5_keytab=/a.keytab;krb5_principal=one@R",
            baseString + "krb5_keytab=/a.keytab;krb5_principal=two@R",
            baseString + "krb5_keytab=/b.keytab;krb5_principal=one@R",
            baseString + "krb5_ccache=/tmp/cc1",
            baseString + "krb5_ccache=/tmp/cc2",
            baseString.Replace("db2inst1/h@R", "db2inst2/h@R", StringComparison.Ordinal) + "krb5_ccache=/tmp/cc1",
        }.Select(text => JustyBase.Db2.AdoNet.Db2AdoConnection.CreatePoolKey(text, 10)).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(keys[0], JustyBase.Db2.AdoNet.Db2AdoConnection.CreatePoolKey(baseString + "krb5_keytab=/a.keytab;krb5_principal=one@R", 10));
    }
}
