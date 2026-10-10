using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class ConfigSecurityTests
{
    [Fact]
    public void ToStringRedactsPasswordAndStripsControlCharactersFromLoggedFields()
    {
        var config = new Db2Config
        {
            Host = "db2.example.com\r\n[SECURITY] forged",
            Port = 50000,
            Database = "TEST\nDB",
            User = "db2\0admin\u007f",
            Password = "super_secret_password",
            UseSsl = true,
            Timeout = TimeSpan.FromSeconds(10),
        };

        var text = config.ToString();

        Assert.Equal("Config{Host:db2.example.com[SECURITY] forged, Port:50000, Database:TESTDB, User:db2admin, Password:\"******\", UseSSL:true, Timeout:10s}", text);
        Assert.DoesNotContain("super_secret_password", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain('\0', text);
        Assert.DoesNotContain('\u007f', text);
    }

    [Fact]
    public void ToStringKeepsOrdinaryDiagnosticValuesUnchanged()
    {
        var config = new Db2Config
        {
            Host = "db2.example.com",
            Port = 50001,
            Database = "TESTDB",
            User = "db2admin",
            Password = "secret",
            UseSsl = true,
            Timeout = TimeSpan.FromSeconds(10),
        };

        Assert.Equal("Config{Host:db2.example.com, Port:50001, Database:TESTDB, User:db2admin, Password:\"******\", UseSSL:true, Timeout:10s}", config.ToString());
    }
}
