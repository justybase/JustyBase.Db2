namespace JustyBase.Db2.Converters;

/// <summary>Canonical value encodings only. Noncanonical values retain the compatibility decoder.</summary>
internal static class RowValueParser
{
    internal static bool TryDecimal(ReadOnlySpan<byte> bytes, int scale, out decimal value)
    {
        value = default;
        if (bytes.IsEmpty || bytes.Length > 15 || scale is < 0 or > 28) return false;
        UInt128 magnitude = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            var high = bytes[i] >> 4;
            if (high > 9) return false;
            magnitude = magnitude * 10 + (uint)high;
            if (i == bytes.Length - 1) break;
            var low = bytes[i] & 15;
            if (low > 9) return false;
            magnitude = magnitude * 10 + (uint)low;
        }
        if (magnitude > (((UInt128)1 << 96) - 1)) return false;
        value = new decimal(unchecked((int)(uint)magnitude), unchecked((int)(uint)(magnitude >> 32)),
            unchecked((int)(uint)(magnitude >> 64)), (bytes[^1] & 15) is 11 or 13, (byte)scale);
        return true;
    }

    internal static bool TryDate(ReadOnlySpan<byte> bytes, out DateTime value)
    {
        value = default;
        bytes = Trim(bytes);
        if (bytes.Length != 10 || bytes[4] != '-' || bytes[7] != '-') return false;
        if (!Digits(bytes[..4], out var year) || !Digits(bytes.Slice(5, 2), out var month) || !Digits(bytes.Slice(8, 2), out var day)) return false;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        value = new DateTime(year, month, day);
        return true;
    }

    internal static bool TryTime(ReadOnlySpan<byte> bytes, out TimeSpan value)
    {
        value = default;
        bytes = Trim(bytes);
        if (bytes.Length < 8 || bytes[2] is not ((byte)'.' or (byte)':') || bytes[5] is not ((byte)'.' or (byte)':')) return false;
        if (!Digits(bytes[..2], out var hour) || !Digits(bytes.Slice(3, 2), out var minute) || !Digits(bytes.Slice(6, 2), out var second)) return false;
        if (hour > 24 || minute > 59 || second > 59) return false;
        long fraction = 0;
        if (bytes.Length > 8)
        {
            if (bytes[8] != '.' || bytes.Length is < 10 or > 16 || !Digits(bytes[9..], out var digits)) return false;
            fraction = digits;
            for (var i = bytes.Length - 9; i < 7; i++) fraction *= 10;
        }
        if (hour == 24 && (minute != 0 || second != 0 || fraction != 0)) return false;
        value = new TimeSpan((hour * 3600L + minute * 60L + second) * TimeSpan.TicksPerSecond + fraction);
        return true;
    }

    internal static bool TryTimestamp(ReadOnlySpan<byte> bytes, out DateTime value)
    {
        value = default;
        bytes = Trim(bytes);
        if (bytes.Length < 19 || bytes[10] is not ((byte)'-' or (byte)' ' or (byte)'T')) return false;
        if (!TryDate(bytes[..10], out var date) || !TryTime(bytes[11..], out var time)) return false;
        if (time.Ticks > DateTime.MaxValue.Ticks - date.Ticks) return false;
        value = date.AddTicks(time.Ticks);
        return true;
    }

    private static bool Digits(ReadOnlySpan<byte> bytes, out int value)
    {
        value = 0;
        foreach (var b in bytes)
        {
            if (b is < (byte)'0' or > (byte)'9') return false;
            value = value * 10 + b - '0';
        }
        return true;
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty && bytes[0] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') bytes = bytes[1..];
        while (!bytes.IsEmpty && bytes[^1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') bytes = bytes[..^1];
        return bytes;
    }
}
