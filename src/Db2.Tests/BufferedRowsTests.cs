using System.Buffers.Binary;
using JustyBase.Db2.Protocol;
using Xunit;
namespace JustyBase.Db2.Tests;

public sealed class BufferedRowsTests
{
    [Fact]
    public void EveryFragmentBoundaryPreservesNullsVariableLengthsAndUnreadColumns()
    {
        FieldDescriptor[] fields = [new(0x03, 0, 4), new(0x3f, 0, 99), new(0x03, 0, 4)];
        byte[] rows = [0xff, 0, 0, 42, 0, 0, 0, 0, 0, 3, 65, 66, 67, 0xff,
                       0xff, 0, 0xff, 0xff, 0, 43, 0, 0, 0];
        for (var split = 0; split <= rows.Length; split++)
        {
            using var block = new Db2BufferedRows(Db2ColumnDecoder.Create(fields));
            block.BeginFetch(); block.Append(rows.AsSpan(0, split)); block.Validate(static _ => { });
            var count = 0;
            CheckRows(block, ref count);
            var pending = block.PendingBytes;
            block.BeginFetch(); Assert.Equal(pending, block.PendingBytes);
            block.Append(rows.AsSpan(split)); block.Validate(static _ => { });
            CheckRows(block, ref count);
            Assert.Equal(2, count); Assert.Equal(0, block.PendingBytes);
        }
    }

    private static void CheckRows(Db2BufferedRows block, ref int count)
    {
        while (block.TryRead())
        {
            if (count == 0)
            {
                Assert.True(block.IsNull(2));
                Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(block.GetSpan(0)));
                // Column 1 remains unread and must be skipped on the next Read.
            }
            else
            {
                Assert.Equal(43, BinaryPrimitives.ReadInt32LittleEndian(block.GetSpan(2)));
                Assert.True(block.IsNull(0)); Assert.True(block.IsNull(1));
            }
            count++;
        }
    }

    [Fact]
    public async Task LaterMalformedRowDoesNotExposeEarlierWarnings()
    {
        var warningPacket = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Drda", "null-warning.bin"));
        var payload = (await DssCodec.ReadAsync(new MemoryStream(warningPacket))).Payload;
        using var block = new Db2BufferedRows(Db2ColumnDecoder.Create([new FieldDescriptor(0x03, 0, 4)]));
        block.Append(payload);
        // Invalid SQLCAXGRP in a later row.
        block.Append([0, 0, 0, 0, 0, .."00000"u8.ToArray(), ..new byte[8], 0x01]);
        var observed = 0;
        Assert.Throws<InvalidDataException>(() => block.Validate(_ => observed++));
        Assert.Equal(0, observed);
    }
}
