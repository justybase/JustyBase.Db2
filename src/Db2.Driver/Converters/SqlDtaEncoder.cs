using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using JustyBase.Db2;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;

namespace JustyBase.Db2.Converters;

/// <summary>Encodes positional parameter values in the DRDA SQLDTA format.</summary>
internal static class SqlDtaEncoder
{
    public static byte[] Build(IReadOnlyList<Db2ColumnMetadata> columns, IReadOnlyList<object?> values) =>
        BuildWithExternalData(columns, values).SqlDta;

    internal sealed record ExternalDataParameter(byte[]? DdmObject, Stream? Source, bool LeaveOpen, long Length);
    internal sealed record EncodedSqlDta(byte[] SqlDta, IReadOnlyList<ExternalDataParameter> ExternalData);

    internal static EncodedSqlDta BuildWithExternalData(IReadOnlyList<Db2ColumnMetadata> columns, IReadOnlyList<object?> values, DescriptorCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(values);
        if (columns.Count != values.Count) throw new ArgumentException($"DB2 expected {columns.Count} parameters, got {values.Count}.", nameof(values));

        var normalized = new object?[values.Count];
        for (var index = 0; index < columns.Count; index++) normalized[index] = NormalizeParameterSource(columns[index], values[index]);
        var external = new ExternalLobValue?[columns.Count];
        var hasNonEmptyExternal = false;
        for (var i = 0; i < columns.Count; i++)
        {
            external[i] = ExternalLob(columns[i], normalized[i]);
            if (external[i] is { Length: > 0 }) hasNonEmptyExternal = true;
        }
        using var descriptors = new DdmBufferWriter();
        var sqlDta = descriptors.BeginObject(CodePoint.SQLDTA);
        if (cache?.TryGet(columns, normalized, external) is { } descriptor)
            descriptors.Write(descriptor);
        else
        {
            var descriptorStart = descriptors.BeginObject(CodePoint.FDODSC);
            for (var first = 0; first < columns.Count; first += 84)
            {
                var count = Math.Min(84, columns.Count - first);
                descriptors.WriteByte(checked((byte)((count + 1) * 3)));
                // SQLDTAGRP starts with NGDA (0x76 / LID 0xD0); every further
                // group continues it with CPT (0x7F / NULL LID), not another NGDA.
                // See Apache Derby's DRDA encoder and IBM's FDOCA continuation notes:
                // https://raw.githubusercontent.com/apache/derby/trunk/java/org.apache.derby.client/org/apache/derby/client/net/NetStatementRequest.java
                // https://www.ibm.com/docs/en/db2-for-zos/12.0.0?topic=procedures-interpreting-ifcid-0191-records
                descriptors.WriteByte(first == 0 ? (byte)0x76 : (byte)0x7f);
                descriptors.WriteByte(first == 0 ? (byte)0xd0 : (byte)0x00);
                for (var i = 0; i < count; i++) WriteFieldDescriptor(descriptors, columns[first + i], normalized[first + i], external[first + i]);
            }
            descriptors.Write([0x06, 0x71, 0xe4, 0xd0, 0x00, 0x01]);
            descriptors.EndObject(descriptorStart);
            cache?.Store(columns, normalized, external, descriptors.WrittenSpan[descriptorStart..]);
        }
        var dataStart = descriptors.BeginObject(CodePoint.FDODTA);
        // Alignment precedes the first nullable indicator even for even payloads.
        if (columns.Count > 0) descriptors.WriteByte(0);
        for (var i = 0; i < columns.Count; i++) WriteParameter(descriptors, columns[i], normalized[i], external[i]);
        descriptors.EndObject(dataStart);
        descriptors.EndObject(sqlDta);
        List<ExternalDataParameter>? externalObjects = null;
        foreach (var item in external)
        {
            if (item is null) continue;
            // Zero-length materialized LOBs need no data object when later
            // external values supply the exchange's EXTDTA chain.
            if (item.Bytes is { Length: 0 } && hasNonEmptyExternal) continue;
            externalObjects ??= [];
            if (item.Source is { } source) externalObjects.Add(new(null, source, item.LeaveOpen, item.Length));
            else
            {
                using var writer = new DdmBufferWriter();
                var start = writer.BeginObject(CodePoint.EXTDTA);
                writer.WriteByte(0); writer.Write(item.Bytes!); writer.EndObject(start);
                externalObjects.Add(new(writer.ToArray(), null, true, item.Length));
            }
        }
        return new EncodedSqlDta(descriptors.ToArray(), externalObjects is null ? Array.Empty<ExternalDataParameter>() : externalObjects);
    }

    // A column record is immutable, but its public containing list may change.
    // Wire shape also depends on binary-to-character and external-LOB binding.
    internal sealed class DescriptorCache
    {
        private Db2ColumnMetadata[] _columns = [];
        private byte[] _kinds = [];
        private byte[]? _descriptor;
        internal byte[]? TryGet(IReadOnlyList<Db2ColumnMetadata> columns, object?[] values, ExternalLobValue?[] external)
        {
            if (_descriptor is null || columns.Count != _columns.Length) return null;
            for (var i = 0; i < columns.Count; i++)
                if (!ReferenceEquals(columns[i], _columns[i]) || BindingKind(columns[i], values[i], external[i]) != _kinds[i]) return null;
            return _descriptor;
        }
        internal void Store(IReadOnlyList<Db2ColumnMetadata> columns, object?[] values, ExternalLobValue?[] external, ReadOnlySpan<byte> descriptor)
        {
            var keys = new Db2ColumnMetadata[columns.Count]; var kinds = new byte[columns.Count];
            for (var i = 0; i < columns.Count; i++) { keys[i] = columns[i]; kinds[i] = BindingKind(columns[i], values[i], external[i]); }
            _columns = keys; _kinds = kinds; _descriptor = descriptor.ToArray();
        }
        private static byte BindingKind(Db2ColumnMetadata column, object? value, ExternalLobValue? external)
        {
            var type = SqlTypeNames.BaseType(column.SqlType);
            return value is not null && IsBinaryValue(value) && IsBinaryToCharacterTarget(type) ? (byte)1 : external is not null ? (byte)2 : (byte)0;
        }
    }

    private static void WriteFieldDescriptor(DdmBufferWriter output, Db2ColumnMetadata column, object? value, ExternalLobValue? externalLob)
    {
        var type = SqlTypeNames.BaseType(column.SqlType);
        ReadOnlySpan<byte> field = value is not null && IsBinaryValue(value) && IsBinaryToCharacterTarget(type)
            ? [0x3f, 0x7f, 0xff]
            : type switch
        {
            SqlType.Small => [0x05, 0, 2],
            SqlType.Integer => [0x03, 0, 4],
            SqlType.BigInt => [0x17, 0, 8],
            SqlType.Float when column.Length == 4 => [0x0d, 0, 4],
            SqlType.Float => [0x0b, 0, 8],
            SqlType.Date => [0x21, 0, 10],
            SqlType.Time => [0x23, 0, 8],
            SqlType.Timestamp => [0x25, 0, 32],
            SqlType.Boolean when column.Length == 2 => [0x05, 0, 2],
            SqlType.Boolean => [0xbf, 0, 1],
            SqlType.Blob => externalLob is not null ? [0xc9, 0x80, 9] : [0xc9, 0x80, 2],
            SqlType.Clob when externalLob is not null => [0xcf, 0x80, 9],
            SqlType.DbClob when externalLob is not null => [0xcf, 0x80, 9],
            SqlType.XML when externalLob is not null => [0xc7, 0x80, 9],
            SqlType.Graphic => [0x37, (byte)(column.Length >> 8), (byte)column.Length],
            SqlType.VarBinary => [0xc3, (byte)(column.Length >> 8), (byte)column.Length],
            SqlType.Binary => [0x27, (byte)(column.Length >> 8), (byte)column.Length],
            SqlType.Decimal or SqlType.Numeric => [0x0f, (byte)column.Precision, (byte)column.Scale],
            SqlType.DecFloat when column.Length == 16 => [0xbb, 0, 16],
            SqlType.DecFloat => [0xbb, 0, 8],
            _ => [0x39, 0x3f, 0xff],
        };
        output.Write(field);
    }

    private static void WriteParameter(DdmBufferWriter output, Db2ColumnMetadata column, object? value, ExternalLobValue? externalLob)
    {
        if (value is null or DBNull) { output.WriteByte(0xff); return; }
        if (externalLob is not null)
        {
            output.WriteByte(0);
            Span<byte> length = stackalloc byte[9]; length[0] = 0x02;
            BinaryPrimitives.WriteUInt64BigEndian(length[1..], checked((ulong)externalLob.Length));
            output.Write(length);
            return;
        }
        var type = SqlTypeNames.BaseType(column.SqlType);
        output.WriteByte(0);
        if (IsBinaryValue(value) && IsBinaryToCharacterTarget(type))
        {
            WriteLengthPrefixed(output, ToBytes(value));
            return;
        }
        switch (type)
        {
            case SqlType.Small: WriteSigned(output, Convert.ToInt16(TruncateFractionalInteger(value), CultureInfo.InvariantCulture), 2); break;
            case SqlType.Integer: WriteSigned(output, Convert.ToInt32(TruncateFractionalInteger(value), CultureInfo.InvariantCulture), 4); break;
            case SqlType.BigInt: WriteSigned(output, Convert.ToInt64(TruncateFractionalInteger(value), CultureInfo.InvariantCulture), 8); break;
            case SqlType.Float:
                if (column.Length == 4)
                {
                    Span<byte> floatBytes = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(floatBytes, Convert.ToSingle(value, CultureInfo.InvariantCulture)); output.Write(floatBytes);
                }
                else
                {
                    Span<byte> doubleBytes = stackalloc byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(doubleBytes, Convert.ToDouble(value, CultureInfo.InvariantCulture)); output.Write(doubleBytes);
                }
                break;
            case SqlType.Boolean:
                if (column.Length == 2) WriteSigned(output, ToBoolean(value) ? (short)1 : (short)0, 2);
                else output.WriteByte(ToBoolean(value) ? (byte)1 : (byte)0);
                break;
            case SqlType.Date: WriteTemporal(output, value, "date"); break;
            case SqlType.Time: WriteTemporal(output, value, "time"); break;
            case SqlType.Timestamp: WriteTemporal(output, value, "timestamp"); break;
            case SqlType.VarChar: case SqlType.Char: case SqlType.Long: case SqlType.CStr:
            case SqlType.VarGraph: case SqlType.LonGraph:
                WriteStringParameter(output, CharacterText(value));
                break;
            case SqlType.Clob:
                WriteStringParameter(output, CharacterText(value));
                break;
            case SqlType.DbClob:
                WriteStringParameter(output, IsBinaryValue(value) ? BinaryDbClobText(value) : CharacterText(value));
                break;
            case SqlType.Graphic:
                var graphic = Encoding.BigEndianUnicode.GetBytes(CharacterText(value));
                var target = column.Length > 0 ? checked((int)column.Length) : graphic.Length / 2;
                var graphicBytes = new byte[checked(target * 2)];
                for (var index = 1; index < graphicBytes.Length; index += 2) graphicBytes[index] = 0x20;
                graphic.AsSpan(0, Math.Min(graphic.Length, graphicBytes.Length)).CopyTo(graphicBytes);
                output.Write(graphicBytes);
                break;
            case SqlType.Binary:
                var fixedBytes = ToBytes(value); var fixedLength = column.Length > 0 ? checked((int)column.Length) : fixedBytes.Length;
                var fixedBuffer = new byte[fixedLength]; fixedBytes.AsSpan(0, Math.Min(fixedBytes.Length, fixedBuffer.Length)).CopyTo(fixedBuffer); output.Write(fixedBuffer);
                break;
            case SqlType.VarBinary:
            case SqlType.Blob:
                WriteLengthPrefixed(output, ToBytes(value));
                break;
            case SqlType.Decimal:
            case SqlType.Numeric: WritePackedDecimal(output, value, column.Precision, column.Scale); break;
            case SqlType.XML:
                WriteStringParameter(output, CharacterText(value));
                break;
            case SqlType.DecFloat: output.Write(Decfloat.Encode(value is bool boolean ? (boolean ? 1 : 0) : value, column.Length == 16 ? 16 : 8)); break;
            default:
                if (value is byte[] bytes) WriteLengthPrefixed(output, bytes);
                else WriteStringParameter(output, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                break;
        }
    }

    private static object? NormalizeParameterSource(Db2ColumnMetadata column, object? value)
    {
        if (value is null or DBNull) return value;
        if (value is Db2BlobStream or Db2ClobStream or Db2DbClobStream or Db2XmlStream) return value;

        var type = SqlTypeNames.BaseType(column.SqlType);
        var binary = IsBinaryValue(value);
        if (type is SqlType.Small or SqlType.Integer or SqlType.BigInt or SqlType.Float
            or SqlType.Decimal or SqlType.Numeric or SqlType.DecFloat or SqlType.Boolean)
        {
            if (binary) throw new InvalidCastException($"A binary value cannot be bound to Db2 {type}.");
            return value;
        }

        if (type is SqlType.Binary or SqlType.VarBinary or SqlType.Blob)
        {
            if (binary) return value;
            if (value is not string text) throw new InvalidCastException($"Db2 {type} parameters require a binary value or an even-length hexadecimal string.");
            return DecodeHexBinary(column, text);
        }

        if (type is SqlType.Clob or SqlType.DbClob or SqlType.XML)
        {
            if (!binary && value is not string and not char)
                throw new InvalidCastException($"Db2 {type} parameters require text or binary input.");
            return value;
        }

        if ((type is SqlType.Graphic or SqlType.VarGraph or SqlType.LonGraph) && binary)
            throw new InvalidCastException($"A binary value cannot be bound to Db2 {type}.");
        return value;
    }

    private static byte[] DecodeHexBinary(Db2ColumnMetadata column, string value)
    {
        if ((value.Length & 1) != 0) throw new InvalidCastException($"Db2 {SqlTypeNames.Get(SqlTypeNames.BaseType(column.SqlType))} parameters require an even-length hexadecimal string.");
        var bytes = new byte[value.Length / 2];
        for (var index = 0; index < value.Length; index += 2)
        {
            var high = HexNibble(value[index]);
            var low = HexNibble(value[index + 1]);
            if (high < 0 || low < 0) throw new InvalidCastException("Db2 binary string parameters must contain hexadecimal characters only.");
            bytes[index / 2] = (byte)((high << 4) | low);
        }
        if (column.Length > 0 && (ulong)bytes.Length > column.Length)
            throw new InvalidCastException($"Db2 binary string input exceeds its {column.Length}-byte target length.");
        return bytes;
    }

    private static int HexNibble(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1,
    };

    private static bool IsBinaryValue(object value) =>
        value is byte[] or ReadOnlyMemory<byte> or Memory<byte>;

    private static bool IsBinaryToCharacterTarget(SqlType type) =>
        type is SqlType.VarChar or SqlType.Char or SqlType.Long or SqlType.Clob or SqlType.XML;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private static string CharacterText(object value) => value is bool boolean
        ? boolean ? "1" : "0"
        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static string BinaryText(object value) => StrictUtf8.GetString(ToBytes(value));

    private static string BinaryDbClobText(object value)
    {
        var bytes = ToBytes(value);
        return Encoding.BigEndianUnicode.GetString(bytes, 0, bytes.Length & ~1);
    }

    internal sealed record ExternalLobValue(byte[]? Bytes, Stream? Source, bool LeaveOpen, SqlType Type, long Length);

    private static ExternalLobValue? ExternalLob(Db2ColumnMetadata column, object? value)
    {
        var type = SqlTypeNames.BaseType(column.SqlType);
        if (value is Db2BlobStream stream)
        {
            if (type != SqlType.Blob) throw new ArgumentException($"A Db2BlobStream can only be bound to a BLOB parameter, not {column.Name} ({type}).", nameof(value));
            ArgumentNullException.ThrowIfNull(stream.Source);
            if (stream.Length < 0) throw new ArgumentOutOfRangeException(nameof(value), "The streamed BLOB length cannot be negative.");
            if (!stream.Source.CanRead) throw new ArgumentException("The streamed BLOB source must be readable.", nameof(value));
            return new ExternalLobValue(null, stream.Source, stream.LeaveOpen, type, stream.Length);
        }
        if (value is Db2ClobStream clobStream)
        {
            if (type != SqlType.Clob) throw new ArgumentException($"A Db2ClobStream can only be bound to a CLOB parameter, not {column.Name} ({type}).", nameof(value));
            ArgumentNullException.ThrowIfNull(clobStream.Source);
            if (clobStream.Length < 0) throw new ArgumentOutOfRangeException(nameof(value), "The streamed UTF-8 CLOB length cannot be negative.");
            if (!clobStream.Source.CanRead) throw new ArgumentException("The streamed CLOB source must be readable.", nameof(value));
            return new ExternalLobValue(null, clobStream.Source, clobStream.LeaveOpen, type, clobStream.Length);
        }
        if (value is Db2DbClobStream dbClobStream)
        {
            if (type != SqlType.DbClob) throw new ArgumentException($"A Db2DbClobStream can only be bound to a DBCLOB parameter, not {column.Name} ({type}).", nameof(value));
            ArgumentNullException.ThrowIfNull(dbClobStream.Source);
            if (dbClobStream.Length < 0) throw new ArgumentOutOfRangeException(nameof(value), "The streamed UTF-8 DBCLOB length cannot be negative.");
            if (!dbClobStream.Source.CanRead) throw new ArgumentException("The streamed DBCLOB source must be readable.", nameof(value));
            return new ExternalLobValue(null, dbClobStream.Source, dbClobStream.LeaveOpen, type, dbClobStream.Length);
        }
        if (value is Db2XmlStream xmlStream)
        {
            if (type != SqlType.XML) throw new ArgumentException($"A Db2XmlStream can only be bound to an XML parameter, not {column.Name} ({type}).", nameof(value));
            ArgumentNullException.ThrowIfNull(xmlStream.Source);
            if (xmlStream.Length < 0) throw new ArgumentOutOfRangeException(nameof(value), "The streamed XML length cannot be negative.");
            if (!xmlStream.Source.CanRead) throw new ArgumentException("The streamed XML source must be readable.", nameof(value));
            return new ExternalLobValue(null, xmlStream.Source, xmlStream.LeaveOpen, type, xmlStream.Length);
        }
        // Materialized text LOBs exceeding the inline character length use the
        // same UTF-8 EXTDTA representation as explicit text streams.
        if (type is SqlType.Clob or SqlType.DbClob or SqlType.XML
            && value is string text && text.Length > ushort.MaxValue)
        {
            var textBytes = Encoding.UTF8.GetBytes(text);
            return new ExternalLobValue(textBytes, null, true, type, textBytes.Length);
        }
        if (type != SqlType.Blob || value is null or DBNull) return null;
        var bytes = ToBytes(value);
        return new ExternalLobValue(bytes, null, true, type, bytes.Length);
    }

    private static void WriteSigned(DdmBufferWriter output, short value, int length)
    {
        Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(bytes, value); output.Write(bytes);
    }

    private static object TruncateFractionalInteger(object value) => value switch
    {
        decimal number => decimal.Truncate(number),
        double number => Math.Truncate(number),
        float number => MathF.Truncate(number),
        string text when TryTruncateNumericText(text, out var integer) => integer,
        _ => value,
    };

    private static bool TryTruncateNumericText(string value, out string integer)
    {
        integer = value;
        var text = value.Trim();
        var exponentIndex = text.IndexOfAny(['e', 'E']);
        var exponent = 0L;
        if (exponentIndex >= 0)
        {
            if (text.IndexOfAny(['e', 'E'], exponentIndex + 1) >= 0
                || !long.TryParse(text[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
                return false;
            text = text[..exponentIndex];
        }

        var negative = text.StartsWith("-", StringComparison.Ordinal);
        if (negative || text.StartsWith("+", StringComparison.Ordinal)) text = text[1..];
        var decimalIndex = text.IndexOf('.');
        if (decimalIndex >= 0 && text.IndexOf('.', decimalIndex + 1) >= 0) return false;
        var whole = decimalIndex < 0 ? text : text[..decimalIndex];
        var fraction = decimalIndex < 0 ? string.Empty : text[(decimalIndex + 1)..];
        if (whole.Length == 0 && fraction.Length == 0
            || !whole.All(char.IsAsciiDigit)
            || !fraction.All(char.IsAsciiDigit))
            return false;

        var significant = (whole + fraction).TrimStart('0');
        if (significant.Length == 0)
        {
            integer = "0";
            return true;
        }

        if (exponent > fraction.Length + 19L)
        {
            integer = negative ? "-9223372036854775809" : "9223372036854775808";
            return true;
        }
        if (exponent < fraction.Length - (long)significant.Length)
        {
            integer = "0";
            return true;
        }

        var integerLength = significant.Length - (long)fraction.Length + exponent;
        if (integerLength <= 0)
        {
            integer = "0";
            return true;
        }
        if (integerLength > 19)
        {
            integer = negative ? "-9223372036854775809" : "9223372036854775808";
            return true;
        }

        var keptDigits = (int)Math.Min(integerLength, significant.Length);
        var digits = significant[..keptDigits] + new string('0', (int)(integerLength - keptDigits));
        integer = digits == "0" || !negative ? digits : $"-{digits}";
        return true;
    }

    private static void WriteSigned(DdmBufferWriter output, int value, int length)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); output.Write(bytes);
    }

    private static void WriteSigned(DdmBufferWriter output, long value, int length)
    {
        Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, value); output.Write(bytes);
    }

    private static void WriteLengthPrefixed(DdmBufferWriter output, byte[] bytes)
    {
        if (bytes.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(bytes), "DRDA parameter values cannot exceed 65535 bytes in this SQLDTA encoder.");
        Span<byte> length = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)bytes.Length); output.Write(length); output.Write(bytes);
    }

    private static void WriteStringParameter(DdmBufferWriter output, string value)
    {
        if (value.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value), "DRDA character parameters cannot exceed 65535 UTF-16 code units.");
        Span<byte> length = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)value.Length); output.Write(length);
        var count = Encoding.BigEndianUnicode.GetByteCount(value);
        var written = Encoding.BigEndianUnicode.GetBytes(value.AsSpan(), output.GetSpan(count));
        output.Advance(written);
    }

    private static void WritePackedDecimal(DdmBufferWriter output, object value, int precision, int scale)
    {
        if (precision is < 1 or > 31 || scale < 0 || scale > precision) throw new ArgumentOutOfRangeException(nameof(precision), "Invalid Db2 decimal precision or scale.");
        if (value is decimal number)
        {
            WriteDecimalBits(output, number, precision, scale);
            return;
        }
        var text = (value is bool boolean ? (boolean ? "1" : "0") : Convert.ToString(value, CultureInfo.InvariantCulture)!).Trim();
        var negative = text.StartsWith("-", StringComparison.Ordinal);
        if (negative || text.StartsWith("+", StringComparison.Ordinal)) text = text[1..];
        var parts = text.Split('.', 2);
        var fraction = parts.Length == 2 ? parts[1] : "";
        if (parts[0].Length == 0 && fraction.Length == 0) throw new FormatException($"Invalid Db2 decimal value '{value}'.");
        var integer = parts[0].Length == 0 ? "0" : parts[0];
        if (integer.Any(c => !char.IsAsciiDigit(c)) || fraction.Any(c => !char.IsAsciiDigit(c))) throw new FormatException($"Invalid Db2 decimal value '{value}'.");
        var integerDigits = precision - scale;
        if (integer.TrimStart('0').Length > integerDigits) throw new OverflowException($"Db2 decimal value '{value}' exceeds DECIMAL({precision},{scale}).");
        var digitNibbles = new byte[precision];
        for (var i = 0; i < precision; i++)
        {
            int digit;
            if (i < integerDigits)
            {
                var source = integer.Length - integerDigits + i;
                digit = source >= 0 && source < integer.Length ? integer[source] - '0' : 0;
            }
            else digit = i - integerDigits < fraction.Length ? fraction[i - integerDigits] - '0' : 0;
            digitNibbles[i] = checked((byte)digit);
        }
        var packed = new byte[(precision + 2) / 2];
        var firstNibble = packed.Length * 2 - 1 - precision;
        for (var i = 0; i < precision; i++) SetNibble(packed, firstNibble + i, digitNibbles[i]);
        SetNibble(packed, packed.Length * 2 - 1, negative ? (byte)0x0d : (byte)0x0c);
        output.Write(packed);
    }

    private static void WriteDecimalBits(DdmBufferWriter output, decimal value, int precision, int scale)
    {
        Span<int> bits = stackalloc int[4]; decimal.GetBits(value, bits);
        var magnitude = (UInt128)(uint)bits[0] | (UInt128)(uint)bits[1] << 32 | (UInt128)(uint)bits[2] << 64;
        var sourceScale = (bits[3] >> 16) & 255;
        var negative = bits[3] < 0 && magnitude != 0; // Decimal.ToString normalizes signed zero.
        if (magnitude / Power10(sourceScale) >= Power10(precision - scale))
            throw new OverflowException($"Db2 decimal value '{value}' exceeds DECIMAL({precision},{scale}).");
        magnitude = sourceScale > scale ? magnitude / Power10(sourceScale - scale) : magnitude * Power10(scale - sourceScale);
        Span<byte> packed = stackalloc byte[16];
        var length = (precision + 2) / 2;
        packed = packed[..length]; packed.Clear();
        packed[^1] = negative ? (byte)0x0d : (byte)0x0c;
        for (var nibble = length * 2 - 2; nibble >= 0; nibble--)
        {
            var digit = (byte)(magnitude % 10); magnitude /= 10;
            if ((nibble & 1) == 0) packed[nibble / 2] |= (byte)(digit << 4);
            else packed[nibble / 2] |= digit;
        }
        output.Write(packed);
        static UInt128 Power10(int exponent) { UInt128 value = 1; for (var i = 0; i < exponent; i++) value *= 10; return value; }
    }

    private static void SetNibble(byte[] bytes, int position, byte value)
    {
        var index = position / 2;
        if ((position & 1) == 0) bytes[index] |= (byte)(value << 4);
        else bytes[index] |= value;
    }

    private static bool ToBoolean(object value) => value switch
    {
        bool boolean => boolean,
        string text => ParseBooleanText(text),
        sbyte number => number != 0,
        byte number => number != 0,
        short number => number != 0,
        ushort number => number != 0,
        int number => number != 0,
        uint number => number != 0,
        long number => number != 0,
        ulong number => number != 0,
        _ => throw new FormatException($"Cannot convert '{value}' to Db2 BOOLEAN."),
    };

    private static bool ParseBooleanText(string value)
    {
        var text = value.Trim();
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || text.Equals("y", StringComparison.OrdinalIgnoreCase)
            || text.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase)
            || text.Equals("no", StringComparison.OrdinalIgnoreCase)
            || text.Equals("n", StringComparison.OrdinalIgnoreCase)
            || text.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;

        var start = text.Length > 0 && text[0] is '+' or '-' ? 1 : 0;
        if (start == text.Length) throw new FormatException($"Cannot convert '{value}' to Db2 BOOLEAN.");
        for (var index = start; index < text.Length; index++)
            if (text[index] is < '0' or > '9') throw new FormatException($"Cannot convert '{value}' to Db2 BOOLEAN.");
        for (var index = start; index < text.Length; index++)
            if (text[index] != '0') return true;
        return false;
    }

    private static byte[] ToBytes(object value) => value switch
    {
        byte[] bytes => bytes,
        ReadOnlyMemory<byte> memory => memory.ToArray(),
        Memory<byte> memory => memory.ToArray(),
        string text => Encoding.UTF8.GetBytes(text),
        _ => Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
    };

    private static void WriteTemporal(DdmBufferWriter output, object value, string kind)
    {
        Span<char> chars = stackalloc char[32];
        var format = kind == "date" ? "yyyy-MM-dd" : kind == "time" ? "HH:mm:ss" : "yyyy-MM-dd-HH.mm.ss.ffffff";
        int written;
        var formatted = value switch
        {
            DateTime date => date.TryFormat(chars, out written, format, CultureInfo.InvariantCulture),
            DateTimeOffset date => date.UtcDateTime.TryFormat(chars, out written, format, CultureInfo.InvariantCulture),
            DateOnly date when kind == "date" => date.TryFormat(chars, out written, format, CultureInfo.InvariantCulture),
            TimeOnly time when kind == "time" => time.TryFormat(chars, out written, format, CultureInfo.InvariantCulture),
            _ => SetUnformatted(out written)
        };
        if (!formatted)
        {
            var text = Temporal(value, kind);
            WriteAscii(output, kind == "timestamp" ? text.PadRight(32, ' ') : text);
            return;
        }
        if (kind == "timestamp") { chars[written..].Fill(' '); written = 32; }
        output.Advance(Encoding.ASCII.GetBytes(chars[..written], output.GetSpan(written)));
        static bool SetUnformatted(out int written) { written = 0; return false; }
    }

    private static string Temporal(object value, string kind)
    {
        if (value is DateOnly dateOnly) return kind == "date" ? dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : throw new FormatException($"Cannot convert DateOnly to Db2 {kind}.");
        if (value is TimeOnly timeOnly) return kind == "time" ? timeOnly.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : throw new FormatException($"Cannot convert TimeOnly to Db2 {kind}.");
        if (value is DateTime dateTime) return FormatTemporal(dateTime, kind);
        if (value is DateTimeOffset dateTimeOffset) return FormatTemporal(dateTimeOffset.UtcDateTime, kind);
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)!.Trim();
        if (kind == "date")
        {
            if (TryParseDb2Date(text, out var date)) return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (TryParseDb2Timestamp(text, out var dateTimestamp)) return dateTimestamp[..10];
        }
        if (kind == "time")
        {
            if (TryParseDb2Time(text, out var time)) return time;
            if (TryParseDb2Timestamp(text, out var timeTimestamp)) return timeTimestamp.Substring(11, 8).Replace('.', ':');
        }
        if (kind == "timestamp" && TryParseDb2Timestamp(text, out var timestamp)) return timestamp;
        throw new FormatException($"Cannot convert '{text}' to Db2 {kind}.");
    }

    private static bool TryParseDb2Date(ReadOnlySpan<char> text, out DateOnly date)
    {
        date = default;
        int year, month, day;
        if (text.Length >= 8 && text[4] == '-')
        {
            var separator = text[5..].IndexOf('-');
            if (separator < 0) return false;
            separator += 5;
            if (!DateDigits(text[..4], 4, 4, out year) || !DateDigits(text.Slice(5, separator - 5), 1, 2, out month)
                || !DateDigits(text[(separator + 1)..], 1, 2, out day)) return false;
        }
        else
        {
            var first = text.IndexOf('/'); if (first < 0) return false;
            var second = text[(first + 1)..].IndexOf('/'); if (second < 0) return false; second += first + 1;
            if (!DateDigits(text[..first], 1, 2, out month) || !DateDigits(text.Slice(first + 1, second - first - 1), 1, 2, out day)
                || !DateDigits(text[(second + 1)..], 4, 4, out year)) return false;
        }
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        date = new DateOnly(year, month, day); return true;
    }

    private static bool DateDigits(ReadOnlySpan<char> text, int minimum, int maximum, out int number)
    {
        number = 0; if (text.Length < minimum || text.Length > maximum) return false;
        foreach (var c in text) { if (c is < '0' or > '9') return false; number = number * 10 + c - '0'; }
        return true;
    }

    private static bool TimeParts(ReadOnlySpan<char> text, bool secondOptional, out int hour, out int minute, out int second)
    {
        hour = minute = second = 0;
        var first = text.IndexOfAny(':', '.');
        if (first is < 1 or > 2 || text.Length < first + 3
            || !DateDigits(text[..first], 1, 2, out hour) || !DateDigits(text.Slice(first + 1, 2), 2, 2, out minute)) return false;
        var rest = text[(first + 3)..];
        if (rest.Length == 0) { if (!secondOptional) return false; }
        else if (rest.Length != 3 || rest[0] is not (':' or '.') || !DateDigits(rest[1..], 2, 2, out second)) return false;
        return minute <= 59 && second <= 59 && hour <= 24 && (hour != 24 || minute == 0 && second == 0);
    }

    private static bool TryParseDb2Time(string text, out string time)
    {
        time = string.Empty;
        if (!TimeParts(text, true, out var hour, out var minute, out var second)) return false;
        time = $"{hour:00}:{minute:00}:{second:00}"; return true;
    }

    private static bool TryParseDb2Timestamp(string text, out string timestamp)
    {
        timestamp = string.Empty;
        if (TryParseDb2Date(text, out var dateOnly))
        { timestamp = string.Create(CultureInfo.InvariantCulture, $"{dateOnly:yyyy-MM-dd}-00.00.00.000000"); return true; }
        var span = text.AsSpan();
        if (span.Length < 16 || span[4] != '-') return false;
        var monthEnd = span[5..].IndexOf('-'); if (monthEnd < 0) return false; monthEnd += 5;
        var dateEnd = monthEnd + 1;
        while (dateEnd < span.Length && span[dateEnd] is >= '0' and <= '9') dateEnd++;
        if (dateEnd >= span.Length || span[dateEnd] is not (' ' or 'T' or '-') || !TryParseDb2Date(span[..dateEnd], out var date)) return false;
        var time = span[(dateEnd + 1)..]; var first = time.IndexOfAny(':', '.');
        if (first is < 1 or > 2) return false;
        var wholeLength = first + 6; if (time.Length < wholeLength) return false;
        if (!TimeParts(time[..wholeLength], false, out var hour, out var minute, out var second) || hour > 23) return false;
        ReadOnlySpan<char> fraction = "000000";
        if (time.Length > wholeLength)
        {
            if (time[wholeLength] != '.') return false;
            fraction = time[(wholeLength + 1)..];
            if (fraction.Length is < 1 or > 12) return false;
            foreach (var c in fraction) if (c is < '0' or > '9') return false;
        }
        timestamp = string.Create(CultureInfo.InvariantCulture, $"{date:yyyy-MM-dd}-{hour:00}.{minute:00}.{second:00}.{fraction}");
        return true;
    }

    private static string FormatTemporal(DateTime value, string kind) => kind switch
    {
        "date" => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "time" => value.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        _ => value.ToString("yyyy-MM-dd-HH.mm.ss.ffffff", CultureInfo.InvariantCulture),
    };

    private static void WriteAscii(DdmBufferWriter output, string value)
    {
        var count = Encoding.ASCII.GetByteCount(value);
        output.Advance(Encoding.ASCII.GetBytes(value.AsSpan(), output.GetSpan(count)));
    }
    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)]; var offset = 0;
        foreach (var part in parts) { part.CopyTo(result, offset); offset += part.Length; }
        return result;
    }
}
