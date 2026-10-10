using JustyBase.Db2.AdoNet;
using Xunit;
namespace JustyBase.Db2.Tests;
public sealed class ImporterCacheTests
{
    [Fact]
    public void CacheKeepsExactSqlAndDoesNotMergeProgressBoundaries()
    {
        string[] columns = ["\"ID\"", "\"VALUE\""];
        var cache = new Db2InsertStatementCache("\"T\"", columns);
        var sql = cache.Get(128);
        Assert.Same(sql, cache.Get(128));
        Assert.Equal(Db2AdoImporter.BuildInsertStatement("\"T\"", columns, 3), cache.Get(3));
        Assert.Same(sql, cache.Get(128));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Get(0));
    }
}
