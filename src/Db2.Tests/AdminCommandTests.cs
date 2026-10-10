using JustyBase.Db2;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class AdminCommandTests
{
    [Fact]
    public void NormalizesOuterWhitespaceAndPreservesCommandContent()
    {
        Assert.Equal("CALL SYSPROC.ADMIN_CMD(?)", Db2AdminCommand.CallSql);
        Assert.Equal("RUNSTATS ON TABLE S.T WITH DISTRIBUTION", Db2AdminCommand.Normalize("  RUNSTATS ON TABLE S.T WITH DISTRIBUTION  "));
        Assert.Equal("COMMENT ON TABLE S.T IS 'O''Reilly'", Db2AdminCommand.Normalize("COMMENT ON TABLE S.T IS 'O''Reilly'"));
    }

    [Fact]
    public void RejectsNullBlankAndNulContainingCommands()
    {
        Assert.Throws<ArgumentNullException>(() => Db2AdminCommand.Normalize(null!));
        Assert.Throws<ArgumentException>(() => Db2AdminCommand.Normalize(" \t\r\n"));
        Assert.Throws<ArgumentException>(() => Db2AdminCommand.Normalize("RUNSTATS\0 ON TABLE S.T"));
    }
}
