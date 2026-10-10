using JustyBase.Db2.AdoNet;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class ParameterPerformanceTests
{
    [Fact]
    public void ParameterLookupObservesMutableNamesAndFirstDuplicate()
    {
        var p = new Db2AdoParameterCollection();
        var first = new Db2AdoParameter { ParameterName = "@Foo" };
        var second = new Db2AdoParameter { ParameterName = ":foo" };
        p.Add(first); p.Add(second);
        Assert.Equal(0, p.IndexOf("?FOO"));
        first.ParameterName = "renamed";
        Assert.Equal(1, p.IndexOf("foo"));
        Assert.Equal(-1, p.IndexOf("@missing"));
        Assert.Equal(-1, p.IndexOf((string)null!));
    }

    [Fact]
    public void ParameterSnapshotsRetainTheirOrderButObserveParameterValues()
    {
        var p = new Db2AdoParameterCollection();
        var first = new Db2AdoParameter { ParameterName = "first", Value = 1 };
        var second = new Db2AdoParameter { ParameterName = "second", Value = 2 };
        p.Add(first);
        var original = p.Snapshot();
        first.Value = 3;
        Assert.Equal(3, original[0].Value);
        p.Insert(0, second);
        Assert.Same(first, Assert.Single(original));
        Assert.Equal(new[] { second, first }, p.Snapshot());
        p[0] = first;
        Assert.Equal(new[] { first, first }, p.Snapshot());
        p.RemoveAt(1);
        Assert.Same(first, Assert.Single(p.Snapshot()));
        p.Remove(first);
        Assert.Empty(p.Snapshot());
        p.AddRange(new[] { first, second });
        p["third"] = second;
        Assert.Equal(3, p.Snapshot().Length);
        p.Clear();
        Assert.Empty(p.Snapshot());
    }
}
