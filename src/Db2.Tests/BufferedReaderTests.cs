using System.Data;
using System.Text;
using System.Buffers.Binary;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class BufferedReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferedTypedRowAdvancementAllocatesZeroBytes(bool asyncRead)
    {
        FieldDescriptor[] fields = [new(0x03, 0, 4), new(0x0f, 8, 2), new(0x25, 0, 26), new(0x03, 0, 4), new(0x17, 0, 8), new(0x05, 0, 2),
            new(0x0d, 0, 4), new(0x0b, 0, 8), new(0xbf, 0, 1), new(0x23, 0, 8)];
        Db2ColumnMetadata[] columns = [new("I", SqlType.NInteger, 4, 10, 0, true),
            new("D", SqlType.NDecimal, 5, 8, 2, true), new("T", SqlType.NTimestamp, 26, 0, 6, true),
            new("N", SqlType.NInteger, 4, 10, 0, true), new("BIG", SqlType.NBigInt, 8, 19, 0, true),
            new("SMALL", SqlType.NSmall, 2, 5, 0, true), new("REAL", SqlType.NFloat, 4, 7, 0, true),
            new("DOUBLE", SqlType.NFloat, 8, 15, 0, true), new("BOOL", SqlType.NBoolean, 1, 0, 0, true),
            new("TIME", SqlType.NTime, 8, 0, 0, true)];
        byte[] row = [0xff, 0, 0, 42, 0, 0, 0, 0, 0, 0, 0x12, 0x34, 0x5c, 0, ..Encoding.ASCII.GetBytes("2026-01-01-12.34.56.123456"), 0xff];
        var big = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(big, 12345678901234567L);
        row = [..row, 0, ..big, 0, 0xff, 0x7f, 0, 0, 0, 0xa0, 0x3f, 0, 0, 0, 0, 0, 0, 0, 0x04, 0xc0,
            0, 1, 0, ..Encoding.ASCII.GetBytes("12.34.56")];
        // Pad the fixed timestamp field to its advertised width (26 bytes).
        Assert.Equal(26, Encoding.ASCII.GetByteCount("2026-01-01-12.34.56.123456"));
        var block = new Db2BufferedRows(Db2ColumnDecoder.Create(fields));
        for (var i = 0; i < 11000; i++) block.Append(row);
        block.Validate(static _ => { });
        using var command = new Db2AdoCommand();
        using var connection = new Db2AdoConnection();
        using var cancellation = new CancellationTokenSource();
        await using var reader = new Db2AdoDataReader(command, connection, columns,
            new SingleBlockEnumerator(block), CommandBehavior.Default, static ex => ex, cancellation);
        await reader.InitializeAsync();
        for (var i = 0; i < 1000; i++) { reader.Read(); Consume(reader); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        long sum = 0;
        for (var i = 0; i < 10000; i++)
        {
#pragma warning disable xUnit1031 // Deliberately measure cached completed Task<bool>; the entire block is already buffered.
            var read = asyncRead ? reader.ReadAsync(default).GetAwaiter().GetResult() : reader.Read();
#pragma warning restore xUnit1031
            if (read) sum += Consume(reader);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(420000, sum);
    }

    private static int Consume(Db2AdoDataReader reader)
    {
        var i = reader.GetInt32(0);
        if (reader.GetFieldValue<int>(0) != i || reader.GetDecimal(1) != 123.45m || reader.GetFieldValue<decimal>(1) != 123.45m
            || reader.GetDateTime(2) != new DateTime(2026, 1, 1, 12, 34, 56).AddTicks(1234560)
            || reader.GetFieldValue<DateTime>(2) != reader.GetDateTime(2) || !reader.IsDBNull(3)
            || reader.GetInt64(4) != 12345678901234567L || reader.GetFieldValue<long>(4) != reader.GetInt64(4)
            || reader.GetInt16(5) != short.MaxValue || reader.GetFieldValue<short>(5) != reader.GetInt16(5)
            || reader.GetFloat(6) != 1.25f || reader.GetFieldValue<float>(6) != reader.GetFloat(6)
            || reader.GetDouble(7) != -2.5 || reader.GetFieldValue<double>(7) != reader.GetDouble(7)
            || !reader.GetBoolean(8) || !reader.GetFieldValue<bool>(8)
            || reader.GetFieldValue<TimeSpan>(9) != new TimeSpan(12, 34, 56)
            || reader.GetDateTime(9) != DateTime.MinValue.Add(new TimeSpan(12, 34, 56))) return -1;
        return i;
    }

    [Fact]
    public async Task BufferedCompatibilityGettersKeepTemporalLexicalValuesAndObjectConversions()
    {
        FieldDescriptor[] fields = [new(0x20, 0, 10), new(0x22, 0, 8), new(0x24, 0, 26), new(0x0e, 8, 2)];
        Db2ColumnMetadata[] columns = [new("D", SqlType.Date, 10, 0, 0, false),
            new("TIME", SqlType.Time, 8, 0, 0, false), new("TS", SqlType.Timestamp, 26, 0, 6, false),
            new("DEC", SqlType.Decimal, 5, 8, 2, false)];
        byte[] data = [0xff, 0, ..Encoding.ASCII.GetBytes("2026-01-0124.00.002026-01-01-24.00.00.000000"), 0, 0, 0x12, 0x34, 0x5d];
        using var command = new Db2AdoCommand(); using var connection = new Db2AdoConnection(); using var cancellation = new CancellationTokenSource();
        var block = new Db2BufferedRows(Db2ColumnDecoder.Create(fields)); block.Append(data); block.Validate(static _ => { });
        await using var buffered = new Db2AdoDataReader(command, connection, columns, new SingleBlockEnumerator(block), CommandBehavior.Default, static ex => ex, cancellation);
        using var legacy = new Db2AdoDataReader(command, connection, [new Db2ResultSet(columns, Db2Session.DecodeRowsIncremental(fields, data).Rows)], -1, CommandBehavior.Default, static ex => ex);
        await buffered.InitializeAsync(); Assert.True(buffered.Read()); Assert.True(legacy.Read());
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(legacy.GetValue(i), buffered.GetValue(i));
            Assert.Equal(legacy.GetString(i), buffered.GetString(i));
            Assert.Equal(legacy.GetFieldValue<object>(i), buffered.GetFieldValue<object>(i));
            Assert.Equal(legacy.GetFieldType(i), buffered.GetFieldType(i));
        }
        Assert.Equal(legacy.GetDateTime(1), buffered.GetDateTime(1));
        Assert.Throws<InvalidCastException>(() => buffered.GetFieldValue<DateTime>(1));
        Assert.Throws<InvalidCastException>(() => buffered.GetFieldValue<string>(0));
        Assert.Same(buffered.GetString(2), buffered.GetString(2));
    }

    private sealed class SingleBlockEnumerator(Db2BufferedRows rows) : IAsyncEnumerator<Db2ReaderBlock>
    {
        private bool _sent;
        public Db2ReaderBlock Current => new(rows);
        public ValueTask<bool> MoveNextAsync() { var result = !_sent; _sent = true; return ValueTask.FromResult(result); }
        public ValueTask DisposeAsync() { rows.Dispose(); return ValueTask.CompletedTask; }
    }
}
