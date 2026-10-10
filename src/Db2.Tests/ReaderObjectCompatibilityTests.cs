using System.Data;
using System.Text;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class ReaderObjectCompatibilityTests
{
    [Fact]
    public async Task GetValueRejectsLobsAboveTheMaterializationBudget()
    {
        const int budget = 16 * 1024 * 1024;
        var payload = new byte[budget + 1];
        var opens = 0;
        var lob = new Db2LobValue(payload.Length, null, () =>
        {
            opens++;
            return new MemoryStream(payload, writable: false);
        });
        var columns = new[] { new Db2ColumnMetadata("BLOB", SqlType.NBlob, (ulong)payload.Length, 0, 0, true) };
        using var connection = new Db2AdoConnection();
        using var command = new Db2AdoCommand();
        await using var reader = new Db2AdoDataReader(command, connection,
            [new Db2ResultSet(columns, [[lob]])], -1, CommandBehavior.Default, error => error);

        Assert.True(await reader.ReadAsync());
        var error = Assert.Throws<Db2ResourceLimitException>(() => reader.GetValue(0));
        Assert.Contains("materialization limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task GetValueBoundsUnknownLengthLobReads()
    {
        const int budget = 16 * 1024 * 1024;
        var payload = new byte[budget + 1];
        var lob = new Db2LobValue(null, null, () => new MemoryStream(payload, writable: false));
        var columns = new[] { new Db2ColumnMetadata("BLOB", SqlType.NBlob, (ulong)payload.Length, 0, 0, true) };
        using var connection = new Db2AdoConnection();
        using var command = new Db2AdoCommand();
        await using var reader = new Db2AdoDataReader(command, connection,
            [new Db2ResultSet(columns, [[lob]])], -1, CommandBehavior.Default, error => error);

        Assert.True(await reader.ReadAsync());
        Assert.Throws<Db2ResourceLimitException>(() => reader.GetValue(0));
    }

    [Fact]
    public async Task ObjectGetterMatchesNormalizedValuesAndMaterializedLobs()
    {
        byte[] binary = [1, 2, 3];
        var text = Encoding.UTF8.GetBytes("café");
        Db2ColumnMetadata[] columns =
        [
            new("DATE", SqlType.NDate, 10, 0, 0, true),
            new("TIME", SqlType.NTime, 15, 0, 6, true),
            new("TIMESTAMP", SqlType.NTimestamp, 26, 0, 6, true),
            new("DECIMAL", SqlType.NDecimal, 8, 12, 2, true),
            new("BLOB", SqlType.NBlob, 3, 0, 0, true),
            new("CLOB", SqlType.NClob, (ulong)text.Length, 0, 0, true),
        ];
        var binaryOpens = 0;
        var textOpens = 0;
        object?[][] rows =
        [
            ["2024-02-29", "24.00.00", "2024-02-29-24.00.00", "12.50",
                new Db2LobValue(binary.Length, null, () => { binaryOpens++; return new MemoryStream(binary); }),
                new Db2LobValue(text.Length, Encoding.UTF8, () => { textOpens++; return new MemoryStream(text); })],
            [null, null, null, null, null, null],
        ];
        using var connection = new Db2AdoConnection();
        using var command = new Db2AdoCommand();
        await using var reader = new Db2AdoDataReader(command, connection,
            [new Db2ResultSet(columns, rows)], -1, CommandBehavior.Default, error => error);
        Assert.True(await reader.ReadAsync());
        for (var ordinal = 0; ordinal < columns.Length; ordinal++)
            Assert.Equal(reader.GetValue(ordinal), reader.GetFieldValue<object>(ordinal));
        Assert.IsType<DateTime>(reader.GetFieldValue<object>(0));
        Assert.IsType<TimeSpan>(reader.GetFieldValue<object>(1));
        Assert.IsType<DateTime>(reader.GetFieldValue<object>(2));
        Assert.IsType<decimal>(reader.GetFieldValue<object>(3));
        Assert.Same(reader.GetValue(4), reader.GetFieldValue<object>(4));
        Assert.Equal("café", reader.GetFieldValue<object>(5));
        Assert.Equal(1, binaryOpens);
        Assert.Equal(1, textOpens);
        Assert.True(await reader.ReadAsync());
        for (var ordinal = 0; ordinal < columns.Length; ordinal++)
            Assert.Same(DBNull.Value, reader.GetFieldValue<object>(ordinal));
    }
}
