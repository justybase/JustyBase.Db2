using System.Text;

namespace JustyBase.Db2;

/// <summary>
/// Bounds one batch pipeline. The native session sends one parameter set per
/// request, so every request is already bounded by one row; these options also
/// validate the row/byte contract and cap the caller-side chunking.
/// </summary>
public sealed record Db2BatchOptions
{
    /// <summary>Maximum parameter sets in one batch chunk. Defaults to 256.</summary>
    public int MaxRows { get; init; } = 256;

    /// <summary>Soft encoded-byte budget for one chunk. Defaults to 8 MiB.</summary>
    public long MaxEncodedBytes { get; init; } = 8 * 1024 * 1024;

    internal void Validate()
    {
        if (MaxRows is < 1 or > 32767)
            throw new ArgumentOutOfRangeException(nameof(MaxRows), "Db2 batch MaxRows must be between 1 and 32767.");
        if (MaxEncodedBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxEncodedBytes), "Db2 batch MaxEncodedBytes must be positive.");
    }
}

/// <summary>Shared encoded-size estimate used by bounded batch and import paths.</summary>
internal static class Db2BatchSizing
{
    internal static long EstimateEncodedBytes(IEnumerable<object?> values)
    {
        long total = 0;
        foreach (var value in values)
        {
            var size = value switch
            {
                byte[] bytes => bytes.LongLength,
                ReadOnlyMemory<byte> bytes => bytes.Length,
                Memory<byte> bytes => bytes.Length,
                string text => Encoding.UTF8.GetByteCount(text),
                char[] chars => (long)chars.Length * sizeof(char),
                Stream or TextReader or Db2BlobStream or Db2ClobStream or Db2DbClobStream or Db2XmlStream => 0,
                null or DBNull => 1,
                _ => 32,
            };
            if (long.MaxValue - total < size) return long.MaxValue;
            total += size;
        }
        return total;
    }
}
