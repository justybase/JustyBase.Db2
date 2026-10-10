using JustyBase.Db2.Protocol;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class DiscardRowsTests
{
    [Fact]
    public void EveryFragmentSizePreservesPartialRowsAndNullableFields()
    {
        FieldDescriptor[] fields = [new(0x03, 0, 4), new(0x3f, 0, 99), new(0x03, 0, 4)];
        byte[] data = [0xff, 0, 0, 42, 0, 0, 0, 0, 0, 3, 65, 66, 67, 0xff,
            0xff, 0, 0xff, 0xff, 0, 43, 0, 0, 0];
        for (var fragment = 1; fragment <= data.Length; fragment++)
        {
            var decoders = Db2ColumnDecoder.Create(fields);
            using var buffered = new Db2BufferedRows(decoders);
            using var discard = new Db2DiscardRows(decoders);
            for (var offset = 0; offset < data.Length; offset += fragment)
            {
                var part = data.AsSpan(offset, Math.Min(fragment, data.Length - offset));
                buffered.BeginFetch(); buffered.Append(part); buffered.Validate(static _ => { });
                discard.BeginFetch(); discard.Append(part); discard.CompleteFetch(static _ => { });
                Assert.Equal(buffered.PendingBytes, discard.PendingBytes);
            }
            Assert.Equal(0, discard.PendingBytes);
        }
    }

    [Fact]
    public async Task FragmentedRowSqlcaWarningIsReportedExactlyOnce()
    {
        var packet = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Drda", "null-warning.bin"));
        var data = (await DssCodec.ReadAsync(new MemoryStream(packet))).Payload;
        for (var fragment = 1; fragment <= data.Length; fragment++)
        {
            using var discard = new Db2DiscardRows(Db2ColumnDecoder.Create([new(0x03, 0, 4)]));
            var warnings = new List<Db2Warning>();
            for (var offset = 0; offset < data.Length; offset += fragment)
            {
                discard.BeginFetch();
                discard.Append(data.AsSpan(offset, Math.Min(fragment, data.Length - offset)));
                discard.CompleteFetch(warning => { if (warning is not null) warnings.Add(warning); });
            }
            Assert.Equal("01003", Assert.Single(warnings).SqlState);
            Assert.Equal(0, discard.PendingBytes);
        }
    }

    [Fact]
    public async Task MalformedLaterRowDoesNotPublishEarlierRowWarnings()
    {
        var packet = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Drda", "null-warning.bin"));
        var data = (await DssCodec.ReadAsync(new MemoryStream(packet))).Payload;
        using var discard = new Db2DiscardRows(Db2ColumnDecoder.Create([new(0x03, 0, 4)]));
        var observed = 0;
        discard.BeginFetch(); discard.Append(data);
        Assert.Throws<InvalidDataException>(() =>
        {
            discard.Append([0, 0, 0, 0, 0, .."00000"u8.ToArray(), ..new byte[8], 0x01]);
            discard.CompleteFetch(_ => observed++);
        });
        Assert.Equal(0, observed);
    }
}
