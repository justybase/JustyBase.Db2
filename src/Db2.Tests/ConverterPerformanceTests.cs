using System.Text;
using System.Text.Json;
using JustyBase.Db2.Converters;
using Xunit;
namespace JustyBase.Db2.Tests;

public sealed class ConverterPerformanceTests
{
    [Fact]
    public void Cp500KeepsEveryDecodedByteAndLastDuplicateReverseMapping()
    {
        byte[] bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var decoded = Ebcdic.DecodeCp500(bytes);
        Assert.Equal(256, decoded.Length);
        var expected = new Dictionary<char, byte>();
        for (var i = 0; i < bytes.Length; i++) expected[decoded[i]] = bytes[i];
        var encoded = Ebcdic.EncodeCp500(decoded);
        for (var i = 0; i < bytes.Length; i++) Assert.Equal(expected[decoded[i]], encoded[i]);
        Assert.Throws<EncoderFallbackException>(() => Ebcdic.EncodeCp500("東京"));
        Assert.Throws<EncoderFallbackException>(() => Ebcdic.EncodeCp500("😀"));
        Assert.Throws<EncoderFallbackException>(() => Ebcdic.EncodeCp500("\ud800"));
        Assert.Equal("", Ebcdic.DecodeCp500([])); Assert.Empty(Ebcdic.EncodeCp500(""));
    }
    [Fact]
    public void UInt128DecfloatMatchesFrozenOriginalRandomWireVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Drda", "decfloat-original.json");
        var cases = JsonSerializer.Deserialize<DecfloatCase[]>(File.ReadAllText(path))!;
        foreach (var item in cases) Assert.Equal(item.Text, Decfloat.Decode(Convert.FromHexString(item.Hex)));
    }
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void EveryDecimalTripletRoundTripsWithoutChangingItsRepresentation(int width)
    {
        for (var i = 0; i < 1000; i++)
        {
            var digits = "1" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(digits, Decfloat.Decode(Decfloat.Encode(digits, width)));
        }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8192)]
    [InlineData(100000)]
    public void LobMaterializationConsumesActualSourceAndPreservesCachedBytes(int length)
    {
        var bytes = new byte[length]; for (var i = 0; i < length; i++) bytes[i] = (byte)i;
        var lob = new Db2LobValue(long.MaxValue, null, () => new MemoryStream(bytes));
        var value = Assert.IsType<byte[]>(lob.Materialize());
        Assert.Equal(bytes, value); Assert.Same(value, lob.Materialize());
        using var stream = lob.OpenReadStream(); using var copied = new MemoryStream(); stream.CopyTo(copied);
        Assert.Equal(bytes, copied.ToArray());
    }
    private sealed record DecfloatCase(string Hex, string Text);
}
