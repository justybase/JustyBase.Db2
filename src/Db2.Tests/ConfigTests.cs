using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class ConfigTests
{
    [Fact]
    public void ParsesUrlAndSslSettings()
    {
        var basic = Db2Config.Parse("db2://db2inst1:password@localhost:50000/SAMPLE");
        Assert.Equal(("localhost", 50000, "SAMPLE", "db2inst1", "password", false, 30_000d),
            (basic.Host, basic.Port, basic.Database, basic.User, basic.Password, basic.UseSsl, basic.Timeout.TotalMilliseconds));

        var secure = Db2Config.Parse("db2s://myuser:sec%40ret@db2server:50001/PRODDB?timeout=15s&block_size=131072");
        Assert.Equal(("db2server", 50001, "PRODDB", "myuser", "sec@ret", true, 15_000d, 131_072),
            (secure.Host, secure.Port, secure.Database, secure.User, secure.Password, secure.UseSsl, secure.Timeout.TotalMilliseconds, secure.BlockSize));
    }

    [Fact]
    public void ParsesUrlSchemeCaseInsensitively()
    {
        // RFC 3986: the scheme is case-insensitive.
        var upper = Db2Config.Parse("DB2://db2admin:pw@host:50000/TESTDB?timeout=5s");
        Assert.Equal(("host", 50000, "TESTDB", "db2admin", "pw", false, 5_000d),
            (upper.Host, upper.Port, upper.Database, upper.User, upper.Password, upper.UseSsl, upper.Timeout.TotalMilliseconds));
        Assert.True(Db2Config.Parse("Db2S://host/TESTDB").UseSsl);
    }

    [Fact]
    public void DsnParityCorpusAcceptsAndRejectsConsistently()
    {
        var full = Db2Config.Parse("db2://user:pass@host:50000/TESTDB");
        Assert.Equal(("host", 50000, "TESTDB", "user", "pass", false),
            (full.Host, full.Port, full.Database, full.User, full.Password, full.UseSsl));

        var ssl = Db2Config.Parse("db2s://user:pass@host/TESTDB");
        Assert.True(ssl.UseSsl);
        Assert.Equal(50000, ssl.Port);

        var upper = Db2Config.Parse("DB2://user:pass@host:50000/TESTDB");
        Assert.Equal(("host", 50000), (upper.Host, upper.Port));

        var escaped = Db2Config.Parse("db2://us%40er:pa%40ss@host/TESTDB");
        Assert.Equal(("us@er", "pa@ss"), (escaped.User, escaped.Password));

        var ipv6 = Db2Config.Parse("db2://user:pass@[::1]:50000/TESTDB");
        Assert.Equal(("::1", 50000), (ipv6.Host, ipv6.Port));

        var userOnly = Db2Config.Parse("db2://user@host/TESTDB");
        Assert.Equal(("user", ""), (userOnly.User, userOnly.Password));
        var noUser = Db2Config.Parse("db2://host/TESTDB");
        Assert.Equal(("", ""), (noUser.User, noUser.Password));

        Assert.Equal(5_000d, Db2Config.Parse("db2://user:pass@host/TESTDB?timeout=5s").Timeout.TotalMilliseconds);
        Assert.Equal(7_000d, Db2Config.Parse("db2://user:pass@host/TESTDB?timeout=7").Timeout.TotalMilliseconds);
        Assert.Equal(9, Db2Config.Parse("db2://user:pass@host/TESTDB?security_mechanism=9").SecurityMechanism);
        Assert.Equal(131_072, Db2Config.Parse("db2://user:pass@host/TESTDB?block_size=131072").BlockSize);

        // Not a db2 scheme: the key/value parser finds no pairs and keeps defaults.
        var notDb2 = Db2Config.Parse("http://user:pass@host/TESTDB");
        Assert.Equal("", notDb2.Database);

        Assert.Equal(30_000d, Db2Config.Parse("db2://u:p@host/TESTDB?timeout=abc").Timeout.TotalMilliseconds);
        Assert.Equal(50000, Db2Config.Parse("db2://u:p@host/TESTDB?port=abc").Port);
        Assert.Equal(50000, Db2Config.Parse("db2://u:p@host/TESTDB?port=70000").Port);
        Assert.Equal(65_535, Db2Config.Parse("db2://u:p@host/TESTDB?block_size=512").BlockSize);

        var kv = Db2Config.Parse("Database=TESTDB;Host=h;Port=50001;UID=u;PWD=p");
        Assert.Equal(("h", 50001, "u", "p"), (kv.Host, kv.Port, kv.User, kv.Password));
        Assert.Equal("h", Db2Config.Parse("DATABASE=TESTDB;HOST=h").Host);
        Assert.Equal("b", Db2Config.Parse("Database=TESTDB;UID=a;UID=b").User);
        Assert.Equal("1", Db2Config.Parse("Database=TESTDB;Unknown=1").Parameters["Unknown"]);
        Assert.Equal("a=b", Db2Config.Parse("Database=TESTDB;PWD=a=b").Password);
        Assert.Equal("a", Db2Config.Parse("Database=TESTDB;PWD=a;b").Password);
        var server = Db2Config.Parse("Server=host:50001;Database=TESTDB");
        Assert.Equal(("host", 50001), (server.Host, server.Port));
        var server6 = Db2Config.Parse("Server=[::1]:50001;Database=TESTDB");
        Assert.Equal(("::1", 50001), (server6.Host, server6.Port));
        Assert.Equal(9, Db2Config.Parse("Database=TESTDB;Authentication=SERVER_ENCRYPT").SecurityMechanism);
        Assert.Equal(5_000d, Db2Config.Parse("Database=TESTDB;Timeout=5s").Timeout.TotalMilliseconds);
        var defaults = Db2Config.Parse("Database=TESTDB");
        Assert.Equal(("localhost", 50000), (defaults.Host, defaults.Port));

        foreach (var invalid in new[]
        {
            "", "   ", "db2://host", "db2://user:pass@host:0/TESTDB", "db2://user:pass@host:70000/TESTDB",
            "db2://user:pa%ZZss@host/TESTDB", "db2://u:p@host/TESTDB?Authentication=BOGUS", "Database=TESTDB;Authentication=TOKEN",
        })
        {
            var rejected = false;
            try { Db2Config.Parse(invalid); } catch { rejected = true; }
            Assert.True(rejected, $"expected reject: {invalid}");
        }
    }

    [Fact]
    public void ParsesKeyValueAliasesAndKeepsUnknownOptions()
    {
        var config = Db2Config.Parse("server=mydb2.local;port=50000;dbname=TESTDB;uid=alice;pwd=secret123;ssl=no;timeout=45s;blocksize=65536;custom_key=x;");
        Assert.Equal(("mydb2.local", 50000, "TESTDB", "alice", "secret123", 45_000d, 65_536, "x"),
            (config.Host, config.Port, config.Database, config.User, config.Password, config.Timeout.TotalMilliseconds, config.BlockSize, config.Parameters["custom_key"]));
        Assert.Equal(50001, Db2Config.Parse("database=TESTDB;ssl=yes").Port);
        Assert.Equal(50000, Db2Config.Parse("database=TESTDB;ssl=yes;port=50000").Port);
        Assert.Equal(150_000d, Db2Config.Parse("db2://host/TESTDB?timeout=2m30s").Timeout.TotalMilliseconds);
    }

    [Fact]
    public void ParsesIbmCliTlsKeywordsAndRejectsUnsupportedSecurityModes()
    {
        var config = Db2Config.Parse("Server=db.example.test:50001;Database=TEST;UID=user;PWD=secret;Security=SSL;SSLServerCertificate=/tmp/db2-ca.pem");

        Assert.Equal(("db.example.test", 50001, "TEST", "user", "secret", true, "/tmp/db2-ca.pem"),
            (config.Host, config.Port, config.Database, config.User, config.Password, config.UseSsl, config.SslRootCaPath));
        Assert.True(Db2Config.Parse("db2://host/TEST?Security=SSL&SSLServerCertificate=%2Ftmp%2Fca.pem").UseSsl);
        Assert.Throws<Db2ConfigurationException>(() => Db2Config.Parse("Hostname=host;Port=50001;Database=TEST;Security=SERVER_ENCRYPT"));
    }

    [Fact]
    public void ParsesSupportedServerAuthenticationAndClientInfoSettings()
    {
        var config = Db2Config.Parse("Server=localhost:50000;Database=TESTDB;Authentication=SERVER;security_mechanism=3;spn=db2/server@CORP.LOCAL;krb5_config=/etc/krb5.conf;krb5_keytab=/etc/db2.keytab;krb5_ccache=/tmp/krb5cc_1000;client_applname=orders;client_corr_token=trace");
        Assert.Equal((3, "db2/server@CORP.LOCAL", "/etc/krb5.conf", "/etc/db2.keytab", "/tmp/krb5cc_1000"),
            (config.SecurityMechanism, config.KerberosSpn, config.Krb5ConfigFile, config.Krb5KeytabFile, config.Krb5CcacheFile));
        Assert.Equal(("orders", "trace"), (config.ClientApplName, config.ClientCorrToken));
    }

    [Theory]
    [InlineData("Authentication=CLIENT")]
    [InlineData("Authentication=TOKEN")]
    [InlineData("Authentication=certificate")]
    [InlineData("security_mechanism=4")]
    [InlineData("security_mechanism=6")]
    [InlineData("security_mechanism=encrypted_password")]
    [InlineData("security_mechanism=usrenpwd")]
    [InlineData("security_mechanism=10")]
    [InlineData("security_mechanism=12")]
    [InlineData("security_mechanism=garbage")]
    public void RejectsUnsupportedAuthenticationModes(string option)
    {
        var parts = option.Split('=');
        Assert.Throws<Db2ConfigurationException>(() => Db2Config.Parse($"Server=localhost:50001;Database=TESTDB;{option}"));
        Assert.Throws<Db2ConfigurationException>(() => Db2Config.Parse($"db2://localhost/TESTDB?{Uri.EscapeDataString(parts[0])}={Uri.EscapeDataString(parts[1])}"));
    }

    [Theory]
    [InlineData("Authentication=SERVER_ENCRYPT", 9)]
    [InlineData("Authentication=KERBEROS", 11)]
    [InlineData("Authentication=GSSPLUGIN", 11)]
    [InlineData("security_mechanism=7", 7)]
    [InlineData("security_mechanism=usrencpwd", 7)]
    [InlineData("security_mechanism=ENCRYPTED_PASSWORD_SECURITY", 7)]
    [InlineData("secmec=secmec7", 7)]
    [InlineData("security_mechanism=9", 9)]
    [InlineData("security_mechanism=dh_encrypted_password", 9)]
    [InlineData("security_mechanism=11", 11)]
    [InlineData("security_mechanism=gssapi", 11)]
    public void ParsesEncryptedAndKerberosAuthenticationModes(string option, ushort expected)
    {
        Assert.Equal(expected, Db2Config.Parse($"Server=localhost:50001;Database=TESTDB;{option}").SecurityMechanism);
    }

    [Fact]
    public void ParsesGssapiTokenFile()
    {
        var config = Db2Config.Parse("Server=localhost:50001;Database=TESTDB;security_mechanism=11;gssapi_token_file=/tmp/token.bin");
        Assert.Equal("/tmp/token.bin", config.GssapiTokenFile);
    }

    [Fact]
    public void RejectsInvalidUrlAndRedactsCredentials()
    {
        var emptyDsn = Assert.Throws<Db2ConfigurationException>(() => Db2Config.Parse("  "));
        Assert.Equal(Db2ErrorKind.Configuration, Assert.IsAssignableFrom<IDb2Error>(emptyDsn).ErrorKind);
        Assert.Throws<Db2ConfigurationException>(() => Db2Config.Parse("db2://u:secret@host/"));
        Assert.Equal("db2://u:******@host/DB?x=1;pwd=******", Db2Config.RedactDsn("db2://u:secret@host/DB?x=1;pwd=other"));
        // A password that itself contains '@' must be masked completely, not only up to the first one.
        Assert.Equal("db2://u:******@host/DB", Db2Config.RedactDsn("db2://u:p@ss@host/DB"));
        Assert.Equal("db2://u:******@host/DB;pwd=******", Db2Config.RedactDsn("db2://u:p@ss@host/DB;pwd=other"));
        var config = Db2Config.Parse("db2://db2admin:super_secret_password@db2.example.com:50000/TESTDB?ssl=true&timeout=10s");
        Assert.Equal("Config{Host:db2.example.com, Port:50001, Database:TESTDB, User:db2admin, Password:\"******\", UseSSL:true, Timeout:10s}", config.ToString());
        Assert.DoesNotContain("super_secret_password", config.ToString());
    }

    [Fact]
    public async Task ConnectClassifiesInvalidConfigObjectWithoutConnecting()
    {
        var error = await Assert.ThrowsAsync<Db2ConfigurationException>(() => Db2Session.ConnectAsync(new Db2Config
        {
            Host = "127.0.0.1", Port = 0, Database = "TESTDB", User = "db2inst1", Password = "secret",
        }));

        Assert.Equal(Db2ErrorKind.Configuration, Assert.IsAssignableFrom<IDb2Error>(error).ErrorKind);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectRejectsUnsupportedMechanismBeforeOpeningSocket()
    {
        var error = await Assert.ThrowsAsync<Db2ConfigurationException>(() => Db2Session.ConnectAsync(new Db2Config
        {
            Host = "127.0.0.1", Port = 1, Database = "TESTDB", User = "db2inst1", Password = "secret", SecurityMechanism = 4,
        }));

        Assert.Contains("Supported mechanisms", error.Message, StringComparison.Ordinal);
    }
}
