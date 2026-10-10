using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

[Trait("Category", "Live")]
[Trait("Category", "Secmec9Integration")]
public sealed class Secmec9LiveTests
{
    // Opt-in SERVER_ENCRYPT profile, e.g.
    // db2://DB2INST1:<password>@localhost:50002/TESTDB?security_mechanism=9
    [Secmec9Fact]
    public async Task Secmec9LiveAuthenticationAgainstServerEncryptProfile()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_SECMEC9_TEST_DSN")!;
        await using var session = await Db2Session.ConnectAsync(dsn);
        var scalar = await session.QueryAsync("VALUES 42");
        Assert.Equal(42, Assert.IsType<int>(Assert.Single(Assert.Single(scalar.Rows))));
        var bound = await session.QueryAsync("VALUES CAST(? AS INTEGER)", [43]);
        Assert.Equal(43, Assert.IsType<int>(Assert.Single(Assert.Single(bound.Rows))));
    }
}

public sealed class Secmec9FactAttribute : FactAttribute
{
    public Secmec9FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DB2_SECMEC9_TEST_DSN")))
            Skip = "Set DB2_SECMEC9_TEST_DSN to enable the opt-in SERVER_ENCRYPT integration test.";
    }
}
