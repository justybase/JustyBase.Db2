using System.Data;
using JustyBase.Db2.AdoNet;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class AdoNetImportTests
{
    [Fact]
    public void ImportSqlQuotesIdentifiersAndCreatesMultiRowParameterSets()
    {
        var table = Db2AdoImporter.QuoteTableName("\"App\".\"order details\"");
        var columns = new[] { "\"ORDER\"", "\"DETAIL\"" };

        Assert.Equal("\"App\".\"order details\"", table);
        Assert.Equal(
            "INSERT INTO \"App\".\"order details\" (\"ORDER\", \"DETAIL\") VALUES (?,?),(?,?)",
            Db2AdoImporter.BuildInsertStatement(table, columns, 2));
    }

    [Theory]
    [InlineData("APP.T; DROP TABLE USERS", "tableName")]
    [InlineData("APP..T", "tableName")]
    [InlineData("APP.", "tableName")]
    [InlineData("APP.   ", "tableName")]
    [InlineData("\"APP\".\"T\" -- comment", "tableName")]
    [InlineData("APP.T.U", "tableName")]
    public void ImportRejectsSqlFragmentsAsDestinationTable(string value, string parameterName)
    {
        var error = Assert.Throws<ArgumentException>(() => Db2AdoImporter.QuoteTableName(value));
        Assert.Equal(parameterName, error.ParamName);
    }

    [Fact]
    public void ImportOptionsRejectInvalidBatchAndCommitSettings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Db2AdoImportOptions { BatchSize = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Db2AdoImportOptions { MaxBatchBytes = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Db2AdoImportOptions { NotifyAfterRows = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Db2AdoImportOptions { CommitEveryRows = -1 }.Validate());
    }

    [Fact]
    public async Task ImportRejectsRowsExceedingMarkerLimitBeforeOpeningConnection()
    {
        using var source = new DataTable();
        for (var index = 0; index < 32_768; index++) source.Columns.Add($"C{index}", typeof(int));
        using var reader = source.CreateDataReader();
        using var connection = new Db2AdoConnection();

        await Assert.ThrowsAsync<ArgumentException>(() => Db2AdoImporter.ImportAsync(connection, reader, "WIDE_TABLE"));
    }
}
