using System.Text;
using System.Text.Json;
using JustyBase.Db2.Protocol;
using Xunit;
namespace JustyBase.Db2.Tests;
public sealed class DdmRequestWriterTests
{
    [Fact]
    public void DirectRequestsMatchFrozenOriginalBytesIncludingExtendedAndUnicodeForms()
    {
        var cases = JsonSerializer.Deserialize<RequestCase[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Drda", "ddm-requests-original.json")))!;
        var section = new ResultSetPackageSection(Encoding.ASCII.GetBytes("PERFDB2"), Encoding.ASCII.GetBytes("MYSCHEMA"), Encoding.ASCII.GetBytes("SYSSH200"), Encoding.ASCII.GetBytes("SYSLVL01"), 65);
        foreach (var item in cases)
        {
            var actual = item.Kind switch
            {
                "Package" => DdmCodec.PackPackageNameCsn(item.Database, item.Package, item.Token, 65),
                "Prepare" => DdmCodec.PackPrepareSqlStatement(item.Package, item.Token, 65, item.Database),
                "Open" => DdmCodec.PackOpenQuery(item.Package, item.Token, 65, item.Database, 65535),
                "Continue" => DdmCodec.PackContinueQuery(item.Package, item.Token, 65, item.Database, 65535, 1234567890123),
                "RecordDescribe" => DdmCodec.PackDescribeSqlStatement(section),
                "RecordContinue" => DdmCodec.PackContinueQuery(section, 65535, 1234567890123, 32, true),
                "ProcedureContinue" => DdmCodec.PackContinueProcedureResultSet(section, 65535, 1234567890123),
                "ProcedureClose" => DdmCodec.PackCloseProcedureResultSet(section, 1234567890123),
                "GetChunk" => DdmCodec.PackGetNextChunk(item.Package, item.Token, 65, item.Database, 1234567890123, 17, 65536),
                "ExecuteResults" => DdmCodec.PackExecuteSqlStatement(item.Package, item.Token, 65, item.Database, 3, 65535, null, false),
                _ => DdmCodec.PackSqlStatement(item.Sql),
            };
            Assert.Equal(item.Hex, Convert.ToHexString(actual));
        }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    public void OwnedRequestsNeverShareMutableOutput(ushort section)
    {
        var first = DdmCodec.PackOpenQuery("SYSSH200", "SYSLVL01", section, "PERFDB2", 65535);
        var expected = first.ToArray(); first.AsSpan().Fill(0);
        Assert.Equal(expected, DdmCodec.PackOpenQuery("SYSSH200", "SYSLVL01", section, "PERFDB2", 65535));
    }
    private sealed record RequestCase(string Kind, string Database, string Package, string Token, string Sql, string Hex);
}
