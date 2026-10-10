using System.Data;
using System.Text;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class ReaderTemporalCompatibilityTests
{
    [Theory]
    [InlineData("2024-02-29-24.00.00", 0, 2024, 3, 1)]
    [InlineData("2026-12-31-24.00.00.000000", 6, 2027, 1, 1)]
    [InlineData("2026-01-31-24.00.00.0000000", 7, 2026, 2, 1)]
    public async Task EndOfDayTimestampKeepsCalendarRolloverAndRawString(string value, int scale, int year, int month, int day)
    {
        using var connection = new Db2AdoConnection();
        using var command = new Db2AdoCommand();
        await using var reader = CreateTemporalReader(command, connection, SqlType.NTimestamp, value, scale);
        Assert.True(await reader.ReadAsync());
        var expected = new DateTime(year, month, day);
        Assert.Equal(expected, reader.GetValue(0));
        Assert.Equal(expected, reader.GetFieldValue<object>(0));
        Assert.Equal(expected, reader.GetFieldValue<DateTime>(0));
        Assert.Equal(expected, reader.GetDateTime(0));
        Assert.Equal(value, reader.GetString(0));
    }

    [Theory]
    [InlineData("9999-12-31-24.00.00", 0)]
    [InlineData("2024-02-29-23.59.58.123456789012", 12)]
    [InlineData("not-a-timestamp", 6)]
    public async Task UnrepresentableTimestampKeepsObjectAndStringFallback(string value, int scale)
    {
        using var connection = new Db2AdoConnection();
        using var command = new Db2AdoCommand();
        await using var reader = CreateTemporalReader(command, connection, SqlType.NTimestamp, value, scale);
        Assert.True(await reader.ReadAsync());
        Assert.Equal(value, Assert.IsType<string>(reader.GetValue(0)));
        Assert.Equal(value, Assert.IsType<string>(reader.GetFieldValue<object>(0)));
        Assert.Equal(value, reader.GetString(0));
    }

    private static Db2AdoDataReader CreateTemporalReader(Db2AdoCommand command, Db2AdoConnection connection,
        SqlType type, string value, int scale) => new(command, connection,
            [new Db2ResultSet([new Db2ColumnMetadata("VALUE", (SqlType)type, (ulong)value.Length, 0, (ushort)scale, true)], [[value]])],
            -1, CommandBehavior.Default, error => error);
}
