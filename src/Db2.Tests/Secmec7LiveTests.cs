using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

[Trait("Category", "Live")]
[Trait("Category", "Secmec7Integration")]
public sealed class Secmec7LiveTests
{
    // Opt-in AUTHENTICATION=SERVER_ENCRYPT profile, e.g.
    // db2://DB2INST1:<password>@localhost:50000/TESTDB?security_mechanism=7
    private static string Dsn => Environment.GetEnvironmentVariable("DB2_SECMEC7_TEST_DSN")!;

    private static string WithMechanism(string? mechanism)
    {
        var builder = new UriBuilder(Dsn);
        var query = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !pair.StartsWith("security_mechanism=", StringComparison.OrdinalIgnoreCase) && !pair.StartsWith("secmec=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (mechanism is not null) query.Add($"security_mechanism={mechanism}");
        builder.Query = string.Join('&', query);
        return builder.Uri.AbsoluteUri;
    }

    [Secmec7Fact]
    public async Task Secmec7LiveAuthenticationAgainstServerEncryptProfile()
    {
        await using var session = await Db2Session.ConnectAsync(WithMechanism("7"));
        var scalar = await session.QueryAsync("VALUES 42");
        Assert.Equal(42, Assert.IsType<int>(Assert.Single(Assert.Single(scalar.Rows))));
        var bound = await session.QueryAsync("VALUES CAST(? AS INTEGER)", [43]);
        Assert.Equal(43, Assert.IsType<int>(Assert.Single(Assert.Single(bound.Rows))));
    }

    [Secmec7Fact]
    public async Task Secmec7LiveProfileRejectsExplicitMechanism3WithoutSwitching()
    {
        var error = await Assert.ThrowsAsync<Db2UnsupportedFeatureException>(() => Db2Session.ConnectAsync(WithMechanism("3")));
        Assert.Matches(@"rejected security mechanism 3 .*accepts .*\b7\b", error.Message);
    }

    [Secmec7Fact]
    public async Task Secmec7LiveProfileUnsetMechanismConnectsThroughEncryptingMechanism()
    {
        await using var session = await Db2Session.ConnectAsync(WithMechanism(null));
        var scalar = await session.QueryAsync("VALUES 44");
        Assert.Equal(44, Assert.IsType<int>(Assert.Single(Assert.Single(scalar.Rows))));
    }

    [Secmec7Fact]
    public async Task Secmec7LiveProfileReportsWrongPasswordAsAuthenticationFailure()
    {
        var config = Db2Config.Parse(WithMechanism("7"));
        config.Password += "-wrong";
        var error = await Assert.ThrowsAsync<Db2AuthenticationException>(() => Db2Session.ConnectAsync(config));
        Assert.Equal(15, error.SecurityCheckCode);
    }
}

public sealed class Secmec7FactAttribute : FactAttribute
{
    public Secmec7FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DB2_SECMEC7_TEST_DSN")))
            Skip = "Set DB2_SECMEC7_TEST_DSN to enable the opt-in SERVER_ENCRYPT SECMEC 7 integration test.";
    }
}
