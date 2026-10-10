using System.Buffers.Binary;
using System.Globalization;

namespace JustyBase.Db2.Converters;

internal static class Decfloat
{
    public static byte[] Encode(object value, int byteLength)
    {
        if (byteLength is not (8 or 16)) throw new ArgumentOutOfRangeException(nameof(byteLength), "DFP width must be 8 or 16 bytes.");
        var small = byteLength == 8;
        var bias = small ? 398 : 6176;
        var groups = small ? 5 : 11;
        var exponentBits = small ? 8 : 12;
        var maxDigits = small ? 16 : 34;
        var coefficientBits = groups * 10;
        var totalBits = byteLength * 8;
        ReadOnlySpan<char> text = (Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "0").AsSpan();
        if (text.Length == 0 || text.SequenceEqual("<nil>")) text = "0";
        var negative = false;
        if (text.Length > 0 && text[0] == '-') { negative = true; text = text[1..]; }
        else if (text.Length > 0 && text[0] == '+') text = text[1..];
        if (text.Equals("Infinity", StringComparison.OrdinalIgnoreCase) || text.Equals("Inf", StringComparison.OrdinalIgnoreCase))
            return EncodeSpecial(byteLength, 0x1e, negative);
        if (text.Equals("NaN", StringComparison.OrdinalIgnoreCase)) return EncodeSpecial(byteLength, 0x1f, false);

        var exponentPosition = text.IndexOfAny('e', 'E');
        var mantissa = exponentPosition < 0 ? text : text[..exponentPosition];
        var exponentText = exponentPosition < 0 ? "0".AsSpan() : text[(exponentPosition + 1)..];
        var point = -1;
        var digitCount = 0;
        for (var i = 0; i < mantissa.Length; i++)
        {
            if (mantissa[i] == '.' && point < 0) { point = i; continue; }
            if (!char.IsDigit(mantissa[i])) throw new FormatException($"Invalid decimal floating-point value '{value}'.");
            digitCount++;
        }
        var exponentDigits = exponentText;
        if (!exponentDigits.IsEmpty && exponentDigits[0] is '+' or '-') exponentDigits = exponentDigits[1..];
        if (digitCount == 0 || exponentDigits.IsEmpty)
            throw new FormatException($"Invalid decimal floating-point value '{value}'.");
        foreach (var character in exponentDigits)
            if (!char.IsDigit(character)) throw new FormatException($"Invalid decimal floating-point value '{value}'.");
        if (!int.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var explicitExponent))
            throw new FormatException($"Invalid decimal floating-point exponent in '{value}'.");
        var exponent = explicitExponent - (point < 0 ? 0 : mantissa.Length - point - 1);
        Span<int> digits = stackalloc int[34]; digits = digits[..maxDigits]; digits.Clear();
        var retained = 0;
        var significant = false;
        foreach (var character in mantissa)
        {
            if (character == '.') continue;
            if (!significant && character == '0') continue;
            significant = true;
            if (retained == maxDigits) continue; // Preserve the existing truncation policy.
            var digit = character - '0';
            if (digit is < 0 or > 9) throw new FormatException($"Invalid decimal floating-point value '{value}'.");
            digits[retained++] = digit;
        }
        if (retained < maxDigits)
        {
            digits[..retained].CopyTo(digits[(maxDigits - retained)..]);
            digits[..(maxDigits - retained)].Clear();
        }
        var biasedExponent = exponent + bias;
        var maxBiasedExponent = 3 * (1 << exponentBits) - 1;
        if (biasedExponent < 0 || biasedExponent > maxBiasedExponent)
            throw new FormatException($"Decimal floating-point exponent is out of range in '{value}'.");

        var leading = digits[0];
        var highExponent = biasedExponent >> exponentBits;
        var combination = leading >= 8
            ? 0x18 | ((highExponent & 0x03) << 1) | (leading - 8)
            : (highExponent << 3) | leading;
        var exponentContinuation = biasedExponent & ((1 << exponentBits) - 1);
        UInt128 coefficient = 0;
        for (var index = 0; index < groups; index++)
            coefficient = (coefficient << 10) | (uint)EncodeDpd(digits[index * 3 + 1], digits[index * 3 + 2], digits[index * 3 + 3]);
        var word = ((UInt128)combination << (totalBits - 6)) |
            ((UInt128)exponentContinuation << coefficientBits) | coefficient;
        if (negative) word |= (UInt128)1 << (totalBits - 1);
        return ToFixedBytes(word, byteLength);
    }

    public static string Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length is not (8 or 16)) throw new InvalidDataException($"Invalid DFP byte length {data.Length}; expected 8 or 16.");
        var small = data.Length == 8;
        var bias = small ? 398 : 6176;
        var groups = small ? 5 : 11;
        var exponentBits = small ? 8 : 12;
        var coefficientBits = groups * 10;
        var totalBits = data.Length * 8;
        var word = small ? BinaryPrimitives.ReadUInt64BigEndian(data) : BinaryPrimitives.ReadUInt128BigEndian(data);
        var negative = ((word >> (totalBits - 1)) & (UInt128)1) != 0;
        var combination = (int)((word >> (totalBits - 6)) & 0x1f);
        var exponentMask = ((UInt128)1 << exponentBits) - (UInt128)1;
        var exponentContinuation = (int)((word >> coefficientBits) & exponentMask);
        var coefficient = word & (((UInt128)1 << coefficientBits) - (UInt128)1);

        if (combination >= 0x1e) return (combination & 1) != 0 ? "NaN" : negative ? "-Infinity" : "Infinity";
        var biasedExponent = combination >= 0x18
            ? (((combination >> 1) & 0x03) << exponentBits) | exponentContinuation
            : ((combination >> 3) << exponentBits) | exponentContinuation;
        Span<int> digits = stackalloc int[34]; digits = digits[..(1 + groups * 3)];
        digits[0] = combination >= 0x18 ? 8 + (combination & 1) : combination & 0x07;
        var position = 1;
        for (var index = groups - 1; index >= 0; index--)
        {
            var encoded = (int)((coefficient >> (index * 10)) & 0x3ff);
            var decoded = DecodeDpd(encoded);
            digits[position++] = decoded.First; digits[position++] = decoded.Second; digits[position++] = decoded.Third;
        }
        return Format(negative, digits, biasedExponent - bias);
    }

    private static (int First, int Second, int Third) DecodeDpd(int value)
    {
        var b9=(value>>9)&1;var b8=(value>>8)&1;var b7=(value>>7)&1;var b6=(value>>6)&1;var b5=(value>>5)&1;
        var b4=(value>>4)&1;var b3=(value>>3)&1;var b2=(value>>2)&1;var b1=(value>>1)&1;var b0=value&1;
        if(b3==0)return(b9*4+b8*2+b7,b6*4+b5*2+b4,b2*4+b1*2+b0);
        if(b2==0&&b1==0)return(b9*4+b8*2+b7,b6*4+b5*2+b4,8+b0);
        if(b2==0&&b1==1)return(b9*4+b8*2+b7,8+b4,b6*4+b5*2+b0);
        if(b2==1&&b1==0)return(8+b7,b6*4+b5*2+b4,b9*4+b8*2+b0);
        if(b6==0&&b5==0)return(8+b7,8+b4,b9*4+b8*2+b0);
        if(b6==0&&b5==1)return(8+b7,b9*4+b8*2+b4,8+b0);
        if(b6==1&&b5==0)return(b9*4+b8*2+b7,8+b4,8+b0);
        return(8+b7,8+b4,8+b0);
    }

    private static byte[] EncodeSpecial(int byteLength, int combination, bool negative)
    {
        var word = (UInt128)combination << (byteLength * 8 - 6);
        if (negative) word |= (UInt128)1 << (byteLength * 8 - 1);
        return ToFixedBytes(word, byteLength);
    }

    private static int EncodeDpd(int d2, int d1, int d0)
    {
        var h2 = d2 >= 8; var h1 = d1 >= 8; var h0 = d0 >= 8;
        var v2 = d2 & 7; var v1 = d1 & 7; var v0 = d0 & 7;
        if (!h2 && !h1 && !h0) return (v2 << 7) | (v1 << 4) | v0;
        if (!h2 && !h1 && h0) return (v2 << 7) | (v1 << 4) | 8 | (v0 & 1);
        if (!h2 && h1 && !h0) return (v2 << 7) | (((v0 >> 2) & 1) << 6) | (((v0 >> 1) & 1) << 5) | ((v1 & 1) << 4) | (1 << 3) | (1 << 1) | (v0 & 1);
        if (h2 && !h1 && !h0) return (((v0 >> 2) & 1) << 9) | (((v0 >> 1) & 1) << 8) | ((v2 & 1) << 7) | (v1 << 4) | (1 << 3) | (1 << 2) | (v0 & 1);
        if (h2 && h1 && !h0) return (((v0 >> 2) & 1) << 9) | (((v0 >> 1) & 1) << 8) | ((v2 & 1) << 7) | ((v1 & 1) << 4) | (1 << 3) | (1 << 2) | (1 << 1) | (v0 & 1);
        if (h2 && !h1 && h0) return (((v1 >> 2) & 1) << 9) | (((v1 >> 1) & 1) << 8) | ((v2 & 1) << 7) | (1 << 5) | ((v1 & 1) << 4) | (1 << 3) | (1 << 2) | (1 << 1) | (v0 & 1);
        if (!h2 && h1 && h0) return (v2 << 7) | (1 << 6) | ((v1 & 1) << 4) | (1 << 3) | (1 << 2) | (1 << 1) | (v0 & 1);
        return ((v2 & 1) << 7) | (1 << 6) | (1 << 5) | ((v1 & 1) << 4) | (1 << 3) | (1 << 2) | (1 << 1) | (v0 & 1);
    }

    private static byte[] ToFixedBytes(UInt128 value, int length)
    {
        var result = new byte[length];
        if (length == 8) BinaryPrimitives.WriteUInt64BigEndian(result, (ulong)value);
        else BinaryPrimitives.WriteUInt128BigEndian(result, value);
        return result;
    }

    private static string Format(bool negative, ReadOnlySpan<int> digits, int exponent)
    {
        var first = 0;
        while (first < digits.Length - 1 && digits[first] == 0) first++;
        digits = digits[first..];
        if (digits.Length == 1 && digits[0] == 0) return negative ? "-0" : "0";
        Span<char> significant = stackalloc char[34]; significant = significant[..digits.Length];
        for (var i = 0; i < digits.Length; i++) significant[i] = (char)('0' + digits[i]);
        // Normal/scientific formatting is bounded by 34 digits, ten added zeroes,
        // separators and an exponent. Unusual custom negative signs use fallback.
        Span<char> text = stackalloc char[64];
        var position = 0;
        if (negative) text[position++] = '-';
        var decimalPosition = significant.Length + exponent;
        if (decimalPosition > 0 && decimalPosition <= significant.Length)
        {
            significant[..decimalPosition].CopyTo(text[position..]); position += decimalPosition;
            if (decimalPosition < significant.Length)
            {
                text[position++] = '.';
                significant[decimalPosition..].CopyTo(text[position..]); position += significant.Length - decimalPosition;
            }
        }
        else if (decimalPosition > significant.Length && decimalPosition <= significant.Length + 10)
        {
            significant.CopyTo(text[position..]); position += significant.Length;
            text.Slice(position, decimalPosition - significant.Length).Fill('0'); position += decimalPosition - significant.Length;
        }
        else if (decimalPosition <= 0 && decimalPosition >= -6)
        {
            text[position++] = '0'; text[position++] = '.';
            text.Slice(position, -decimalPosition).Fill('0'); position -= decimalPosition;
            significant.CopyTo(text[position..]); position += significant.Length;
        }
        else
        {
            text[position++] = significant[0];
            if (significant.Length > 1)
            {
                text[position++] = '.'; significant[1..].CopyTo(text[position..]); position += significant.Length - 1;
            }
            text[position++] = 'E';
            var scientificExponent = exponent + significant.Length - 1;
            if (scientificExponent >= 0) text[position++] = '+';
            if (!scientificExponent.TryFormat(text[position..], out var written, provider: CultureInfo.CurrentCulture))
                return string.Concat(text[..position], scientificExponent.ToString(CultureInfo.CurrentCulture).AsSpan());
            position += written;
        }
        return new string(text[..position]);
    }
}
