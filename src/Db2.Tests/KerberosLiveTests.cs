using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Security.Kerberos;
using Xunit;

namespace JustyBase.Db2.Tests;

/// <summary>
/// Describes the isolated MIT KDC + Db2 environment that <c>scripts/test-db2-kerberos.sh</c> provisions.
/// Every value comes from <c>DB2_KRB_*</c> variables that the script exports into the test process;
/// without <c>DB2_KRB_HOST</c> the Kerberos integration tests are skipped.
/// </summary>
internal static class KerberosLiveEnvironment
{
    private static string Read(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    public static bool Available => !string.IsNullOrWhiteSpace(Read("DB2_KRB_HOST"));
    /// <summary>"kerberos": Db2 runs AUTHENTICATION KERBEROS. "krb_server_encrypt": password auth also works.</summary>
    public static string Phase => Read("DB2_KRB_PHASE");
    public static string Host => Read("DB2_KRB_HOST");
    public static int Port => int.Parse(Read("DB2_KRB_PORT"), System.Globalization.CultureInfo.InvariantCulture);
    public static string Database => Read("DB2_KRB_DATABASE");
    public static string Spn => Read("DB2_KRB_SPN");
    /// <summary>A service principal that exists in the KDC but whose key Db2 does not hold.</summary>
    public static string SpnUnknownToDb2 => Read("DB2_KRB_SPN_UNKNOWN_TO_DB2");
    public static string SpnUnknownToKdc => Read("DB2_KRB_SPN_UNKNOWN_TO_KDC");
    public static string Principal => Read("DB2_KRB_PRINCIPAL");
    public static string WrongRealmPrincipal => Read("DB2_KRB_WRONG_REALM_PRINCIPAL");
    public static string Keytab => Read("DB2_KRB_KEYTAB");
    public static string Ccache => Read("DB2_KRB_CCACHE");
    /// <summary>A keytab holding only a principal other than <see cref="Principal"/>.</summary>
    public static string OtherKeytab => Read("DB2_KRB_OTHER_KEYTAB");
    public static string OtherPrincipal => Read("DB2_KRB_OTHER_PRINCIPAL");
    public static string OtherAuthId => Read("DB2_KRB_OTHER_AUTHID");
    /// <summary>Numeric uid of the test process; the default credential cache would be /tmp/krb5cc_&lt;uid&gt;.</summary>
    public static string Uid => Read("DB2_KRB_UID");
    public static string ExpiredCcache => Read("DB2_KRB_EXPIRED_CCACHE");
    public static string MissingKeytab => Read("DB2_KRB_MISSING_KEYTAB");
    public static string ExpectedAuthId => Read("DB2_KRB_EXPECTED_AUTHID");
    public static string User => Read("DB2_KRB_USER");
    /// <summary>A valid password for <see cref="User"/>; used only to prove Kerberos never falls back to it.</summary>
    public static string Password => Read("DB2_KRB_PASSWORD");

    public static Db2Config KerberosConfig(Action<Db2Config>? customize = null)
    {
        var config = new Db2Config
        {
            Host = Host, Port = Port, Database = Database, SecurityMechanism = 11,
            KerberosSpn = Spn, Timeout = TimeSpan.FromSeconds(30),
        };
        customize?.Invoke(config);
        return config;
    }

    public static Db2Config KeytabConfig(Action<Db2Config>? customize = null) => KerberosConfig(config =>
    {
        config.Krb5KeytabFile = Keytab;
        config.KerberosPrincipal = Principal;
        customize?.Invoke(config);
    });

    public static Db2Config CcacheConfig(Action<Db2Config>? customize = null) => KerberosConfig(config =>
    {
        config.Krb5CcacheFile = Ccache;
        customize?.Invoke(config);
    });
}

public sealed class KerberosFactAttribute : FactAttribute
{
    public KerberosFactAttribute(string phase = "kerberos")
    {
        if (!KerberosLiveEnvironment.Available)
            Skip = "Run scripts/test-db2-kerberos.sh to provision the isolated KDC and Db2 for the Kerberos integration tests.";
        else if (!string.Equals(KerberosLiveEnvironment.Phase, phase, StringComparison.Ordinal))
            Skip = $"This test belongs to the '{phase}' provisioning phase of scripts/test-db2-kerberos.sh.";
    }
}

/// <summary>
/// Real end-to-end Kerberos: MIT KDC -> GSS-API credentials -> JustyBase.Db2 -> DRDA SECMEC 11 -> IBM Db2 11.5.8.
/// Each test is a fresh connection against the Db2 instance provisioned by the script.
/// </summary>
[Trait("Category", "KerberosIntegration")]
public sealed class KerberosLiveTests
{
    private static async Task AssertKerberosSqlAsync(Db2Session session)
    {
        var scalar = await session.QueryAsync("VALUES 42");
        Assert.Equal(42, Assert.IsType<int>(Assert.Single(Assert.Single(scalar.Rows))));

        var user = await session.QueryAsync("VALUES CURRENT USER");
        Assert.Equal(KerberosLiveEnvironment.ExpectedAuthId, Assert.IsType<string>(Assert.Single(Assert.Single(user.Rows))).TrimEnd());

        var bound = await session.QueryAsync("VALUES CAST(? AS INTEGER)", [43]);
        Assert.Equal(43, Assert.IsType<int>(Assert.Single(Assert.Single(bound.Rows))));
    }

    private static async Task<Exception> ConnectFailureAsync(Db2Config config)
    {
        var failure = await Record.ExceptionAsync(async () => { await using var session = await Db2Session.ConnectAsync(config); });
        Assert.NotNull(failure);
        return failure;
    }

    private static void AssertAuthenticationFailure(Exception failure)
    {
        var error = Assert.IsAssignableFrom<IDb2Error>(failure);
        Assert.True(error.ErrorKind is Db2ErrorKind.Authentication or Db2ErrorKind.Authorization,
            $"Expected a Kerberos/authentication failure but got {failure.GetType().Name} ({error.ErrorKind}): {failure.Message}");
    }

    [KerberosFact]
    public async Task KeytabAuthenticationRunsSqlAndTransactions()
    {
        await using var session = await Db2Session.ConnectAsync(KerberosLiveEnvironment.KeytabConfig());
        await AssertKerberosSqlAsync(session);

        var table = $"{await LiveFixtureSchema.ForSessionAsync(session)}.KRB_TX_{Guid.NewGuid().ToString("N")[..8]}";
        await session.ExecuteAsync($"CREATE TABLE {table} (ID INTEGER NOT NULL)");
        try
        {
            await using (var transaction = await session.BeginTransactionAsync())
            {
                await session.ExecuteAsync($"INSERT INTO {table} VALUES (1)");
                await transaction.RollbackAsync();
            }
            Assert.Equal(0, Assert.IsType<int>(Assert.Single(Assert.Single((await session.QueryAsync($"SELECT COUNT(*) FROM {table}")).Rows))));
            await using (var transaction = await session.BeginTransactionAsync())
            {
                await session.ExecuteAsync($"INSERT INTO {table} VALUES (2)");
                await transaction.CommitAsync();
            }
            Assert.Equal(1, Assert.IsType<int>(Assert.Single(Assert.Single((await session.QueryAsync($"SELECT COUNT(*) FROM {table}")).Rows))));
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE {table}");
        }
    }

    [KerberosFact]
    public async Task KeytabAuthenticationNeverTouchesTheDefaultCredentialCache()
    {
        // The driver must not borrow from or write to the platform default cache when a keytab is configured.
        Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KRB5CCNAME")), "the harness must not set KRB5CCNAME");
        var defaultCache = $"/tmp/krb5cc_{KerberosLiveEnvironment.Uid}";
        Assert.False(File.Exists(defaultCache), $"{defaultCache} exists before the test; an earlier connection leaked credentials into the default cache.");
        await using (var session = await Db2Session.ConnectAsync(KerberosLiveEnvironment.KeytabConfig()))
            await AssertKerberosSqlAsync(session);
        Assert.False(File.Exists(defaultCache), $"{defaultCache} was created by a keytab connection.");
    }

    [KerberosFact]
    public async Task DifferentPrincipalsConnectedConcurrentlyKeepSeparateIdentities()
    {
        for (var round = 0; round < 3; round++)
        {
            var sessions = await Task.WhenAll(
                Db2Session.ConnectAsync(KerberosLiveEnvironment.KeytabConfig()),
                Db2Session.ConnectAsync(KerberosLiveEnvironment.KeytabConfig(c =>
                {
                    c.Krb5KeytabFile = KerberosLiveEnvironment.OtherKeytab;
                    c.KerberosPrincipal = KerberosLiveEnvironment.OtherPrincipal;
                })));
            try
            {
                var first = Assert.IsType<string>(Assert.Single(Assert.Single((await sessions[0].QueryAsync("VALUES CURRENT USER")).Rows)));
                var second = Assert.IsType<string>(Assert.Single(Assert.Single((await sessions[1].QueryAsync("VALUES CURRENT USER")).Rows)));
                Assert.Equal(KerberosLiveEnvironment.ExpectedAuthId, first.TrimEnd());
                Assert.Equal(KerberosLiveEnvironment.OtherAuthId, second.TrimEnd());
            }
            finally
            {
                foreach (var session in sessions) await session.DisposeAsync();
            }
        }
    }

    [KerberosFact]
    public async Task TokenFileCompatibilityModeAuthenticatesWithAnExternallyProducedGssToken()
    {
        // An external provider would hand over a ready GSS token; here the driver's own GSS layer plays that role.
        byte[] token;
        using (var provider = KerberosCredentialPlan.Resolve(KerberosLiveEnvironment.KeytabConfig()).CreateProvider())
            token = await provider.CreateInitialTokenAsync(CancellationToken.None);
        var path = Path.Combine(Path.GetTempPath(), "JustyBase.Db2-krb-token-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(path, token);
        try
        {
            await using var session = await Db2Session.ConnectAsync(KerberosLiveEnvironment.KerberosConfig(c => c.GssapiTokenFile = path));
            await AssertKerberosSqlAsync(session);
        }
        finally { File.Delete(path); }
    }

    [KerberosFact]
    public async Task PooledSessionsAuthenticateOnceAndAreReused()
    {
        await using var pool = new Db2Pool(KerberosLiveEnvironment.KeytabConfig(), new Db2PoolOptions(1));
        for (var i = 0; i < 3; i++)
        {
            var user = await pool.QueryAsync("VALUES CURRENT USER");
            Assert.Equal(KerberosLiveEnvironment.ExpectedAuthId, Assert.IsType<string>(Assert.Single(Assert.Single(user.Rows))).TrimEnd());
        }
        Assert.Equal(1, pool.SessionCount);
    }

    [KerberosFact]
    public async Task AdoNetConnectionAuthenticatesWithKerberos()
    {
        var connectionString = $"Server={KerberosLiveEnvironment.Host}:{KerberosLiveEnvironment.Port};Database={KerberosLiveEnvironment.Database};Authentication=KERBEROS;" +
                               $"Krb5Ccache={KerberosLiveEnvironment.Ccache};SPN={KerberosLiveEnvironment.Spn}";
        for (var i = 0; i < 2; i++)
        {
            await using var connection = new Db2AdoConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "VALUES CURRENT USER";
            Assert.Equal(KerberosLiveEnvironment.ExpectedAuthId, Assert.IsType<string>(await command.ExecuteScalarAsync()).TrimEnd());
        }
    }

    [KerberosFact]
    public async Task EmptyDefaultCredentialCacheFailsWithAClearKerberosError()
    {
        Assert.True(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KRB5CCNAME")), "the harness must not set KRB5CCNAME in this phase");
        var failure = await ConnectFailureAsync(KerberosLiveEnvironment.KerberosConfig());
        var kerberos = Assert.IsType<Db2KerberosException>(failure);
        Assert.Contains("credential", kerberos.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The harness starts this phase with KRB5CCNAME pointing at the client ccache (set before the process starts).</summary>
    [KerberosFact("default_ccache")]
    public async Task PlatformDefaultCredentialCacheAuthenticates()
    {
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KRB5CCNAME")));
        await using var session = await Db2Session.ConnectAsync(KerberosLiveEnvironment.KerberosConfig());
        await AssertKerberosSqlAsync(session);
    }

    [KerberosFact]
    public async Task CcacheAuthenticationRunsSql()
    {
        await using var session = await Db2Session.ConnectAsync(KerberosLiveEnvironment.CcacheConfig());
        await AssertKerberosSqlAsync(session);
    }

    [KerberosFact]
    public async Task ConnectionStringAuthenticationRunsSql()
    {
        var dsn = $"Server={KerberosLiveEnvironment.Host}:{KerberosLiveEnvironment.Port};Database={KerberosLiveEnvironment.Database};Authentication=KERBEROS;" +
                  $"spn={KerberosLiveEnvironment.Spn};krb5_keytab={KerberosLiveEnvironment.Keytab};krb5_principal={KerberosLiveEnvironment.Principal}";
        await using var session = await Db2Session.ConnectAsync(dsn);
        await AssertKerberosSqlAsync(session);
    }

    [KerberosFact]
    public async Task SecondIndependentKerberosConnectionSucceeds()
    {
        await using var first = await Db2Session.ConnectAsync(KerberosLiveEnvironment.KeytabConfig());
        await using var second = await Db2Session.ConnectAsync(KerberosLiveEnvironment.CcacheConfig());
        Assert.NotSame(first, second);
        // Closing one must not disturb the other: no shared GSS context or credential.
        await AssertKerberosSqlAsync(first);
        await second.DisposeAsync();
        await AssertKerberosSqlAsync(first);

        await using var third = await Db2Session.ConnectAsync(KerberosLiveEnvironment.KeytabConfig());
        await AssertKerberosSqlAsync(third);
    }

    [KerberosFact]
    public async Task ParallelKerberosConnectionsAuthenticateIndependently()
    {
        var sessions = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => index % 2 == 0
            ? Db2Session.ConnectAsync(KerberosLiveEnvironment.KeytabConfig())
            : Db2Session.ConnectAsync(KerberosLiveEnvironment.CcacheConfig())));
        try
        {
            foreach (var session in sessions) await AssertKerberosSqlAsync(session);
        }
        finally
        {
            foreach (var session in sessions) await session.DisposeAsync();
        }
    }

    [KerberosFact]
    public async Task ServicePrincipalUnknownToTheKdcFails()
    {
        var failure = await ConnectFailureAsync(KerberosLiveEnvironment.KeytabConfig(c => c.KerberosSpn = KerberosLiveEnvironment.SpnUnknownToKdc));
        Assert.IsType<Db2KerberosException>(failure);
    }

    [KerberosFact]
    public async Task ServicePrincipalUnknownToDb2Fails()
    {
        // The KDC issues a ticket for this name, but Db2 holds no key for it and must reject the token.
        var failure = await ConnectFailureAsync(KerberosLiveEnvironment.KeytabConfig(c => c.KerberosSpn = KerberosLiveEnvironment.SpnUnknownToDb2));
        AssertAuthenticationFailure(failure);
    }

    [KerberosFact]
    public async Task WrongRealmFails()
    {
        var failure = await ConnectFailureAsync(KerberosLiveEnvironment.KeytabConfig(c => c.KerberosPrincipal = KerberosLiveEnvironment.WrongRealmPrincipal));
        Assert.IsType<Db2KerberosException>(failure);
    }

    [KerberosFact]
    public async Task KeytabWithoutTheRequestedPrincipalFails()
    {
        var failure = await ConnectFailureAsync(KerberosLiveEnvironment.KeytabConfig(c => c.Krb5KeytabFile = KerberosLiveEnvironment.OtherKeytab));
        Assert.IsType<Db2KerberosException>(failure);
    }

    [KerberosFact]
    public async Task MissingKeytabIsAConfigurationErrorBeforeAuthentication()
    {
        var failure = await ConnectFailureAsync(KerberosLiveEnvironment.KeytabConfig(c => c.Krb5KeytabFile = KerberosLiveEnvironment.MissingKeytab));
        Assert.IsType<Db2ConfigurationException>(failure);
        Assert.Equal(Db2OperationOutcome.NotStarted, ((IDb2Error)failure).Outcome);
    }

    [KerberosFact]
    public async Task ExpiredCredentialCacheFails()
    {
        var failure = await ConnectFailureAsync(KerberosLiveEnvironment.CcacheConfig(c => c.Krb5CcacheFile = KerberosLiveEnvironment.ExpiredCcache));
        Assert.IsType<Db2KerberosException>(failure);
    }

    /// <summary>
    /// Runs while Db2 also accepts passwords (KRB_SERVER_ENCRYPT). A valid password sits in the config; a failed
    /// Kerberos attempt must still fail instead of silently authenticating with it.
    /// </summary>
    [KerberosFact("krb_server_encrypt")]
    public async Task KerberosFailureNeverFallsBackToPasswordAuthentication()
    {
        // Control: this very password is accepted by the server over SECMEC 9, so a fallback would succeed.
        await using (var control = await Db2Session.ConnectAsync(new Db2Config
        {
            Host = KerberosLiveEnvironment.Host, Port = KerberosLiveEnvironment.Port, Database = KerberosLiveEnvironment.Database,
            User = KerberosLiveEnvironment.User, Password = KerberosLiveEnvironment.Password, SecurityMechanism = 9,
        }))
        {
            Assert.Equal(1, Assert.IsType<int>(Assert.Single(Assert.Single((await control.QueryAsync("VALUES 1")).Rows))));
        }

        // Kerberos asked for, but with a service principal Db2 cannot honour, and a working password present.
        var failure = await ConnectFailureAsync(KerberosLiveEnvironment.KeytabConfig(c =>
        {
            c.KerberosSpn = KerberosLiveEnvironment.SpnUnknownToDb2;
            c.User = KerberosLiveEnvironment.User;
            c.Password = KerberosLiveEnvironment.Password;
        }));
        AssertAuthenticationFailure(failure);

        // Same with a ticket that cannot be obtained at all.
        failure = await ConnectFailureAsync(KerberosLiveEnvironment.KeytabConfig(c =>
        {
            c.Krb5KeytabFile = KerberosLiveEnvironment.OtherKeytab;
            c.User = KerberosLiveEnvironment.User;
            c.Password = KerberosLiveEnvironment.Password;
        }));
        Assert.IsType<Db2KerberosException>(failure);
    }
}
