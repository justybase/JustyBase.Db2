using System.Buffers.Binary;
using System.Data;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;
using Xunit;
namespace JustyBase.Db2.Tests;

public sealed class BufferedReaderShapeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(33)]
    public async Task ValueAndNullShapesAllocateZeroPerBufferedRow(int count)
    {
        var fields = new FieldDescriptor[count]; var columns = new Db2ColumnMetadata[count];
        using var bytes = new MemoryStream(); bytes.Write([0xff, 0]);
        for (var i = 0; i < count; i++)
        {
            fields[i] = new(3, 0, 4); columns[i] = new("I" + i, SqlType.NInteger, 4, 10, 0, true);
            if (i % 2 == 1) bytes.WriteByte(0xff);
            else { bytes.WriteByte(0); byte[] value = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(value, 42); bytes.Write(value); }
        }
        var block = new Db2BufferedRows(Db2ColumnDecoder.Create(fields));
        var row = bytes.ToArray(); for (var i = 0; i < 11000; i++) block.Append(row);
        block.Validate(static _ => { });
        using var command = new Db2AdoCommand(); using var connection = new Db2AdoConnection(); using var cancellation = new CancellationTokenSource();
        await using var reader = new Db2AdoDataReader(command, connection, columns, new Blocks(block), CommandBehavior.Default, static ex => ex, cancellation);
        await reader.InitializeAsync();
        for (var i = 0; i < 1000; i++) { reader.Read(); Sum(reader); }
        var before = GC.GetAllocatedBytesForCurrentThread(); long sum = 0;
        for (var i = 0; i < 10000; i++) { reader.Read(); sum += Sum(reader); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(42L * ((count + 1) / 2) * 10000, sum);
    }
    private static long Sum(Db2AdoDataReader reader)
    {
        long sum = 0; for (var i = 0; i < reader.FieldCount; i++) if (!reader.IsDBNull(i)) sum += reader.GetFieldValue<int>(i);
        return sum;
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchWaitsUntilConsumerNeedsNextBlock(bool singleRow)
    {
        var rows = new Db2BufferedRows(Db2ColumnDecoder.Create([new FieldDescriptor(2, 0, 4)]));
        for (var i = 0; i < 3; i++) rows.Append([0xff, 0, 42, 0, 0, 0]); rows.Validate(static _ => { });
        var source = new Blocks(rows);
        using var command = new Db2AdoCommand(); using var connection = new Db2AdoConnection(); using var cancellation = new CancellationTokenSource();
        await using var reader = new Db2AdoDataReader(command, connection, [new Db2ColumnMetadata("I", SqlType.Integer, 4, 10, 0, false)],
            source, singleRow ? CommandBehavior.SingleRow : CommandBehavior.Default, static ex => ex, cancellation);
        await reader.InitializeAsync(); Assert.Equal(1, source.Fetches);
        Assert.True(reader.Read()); Assert.Equal(1, source.Fetches);
        if (singleRow) { Assert.False(await reader.ReadAsync()); Assert.Equal(1, source.Fetches); }
        else
        {
            Assert.True(reader.Read()); Assert.True(reader.Read()); Assert.Equal(1, source.Fetches);
            Assert.False(await reader.ReadAsync()); Assert.Equal(2, source.Fetches);
        }
    }
    private sealed class Blocks(Db2BufferedRows rows) : IAsyncEnumerator<Db2ReaderBlock>
    {
        internal int Fetches { get; private set; }
        public Db2ReaderBlock Current => new(rows);
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(++Fetches == 1);
        public ValueTask DisposeAsync() { rows.Dispose(); return ValueTask.CompletedTask; }
    }
}
