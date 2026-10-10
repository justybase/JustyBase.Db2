using System.Buffers.Binary;

namespace JustyBase.Db2.Protocol;

internal enum Db2FieldLayout : byte { Fixed, Variable, VariableGraphic, Lob, Unsupported }

/// <summary>Immutable result-set plan. Wire length and nullable handling are resolved once.</summary>
internal readonly struct Db2ColumnDecoder
{
    internal readonly FieldDescriptor Field;
    internal readonly Db2FieldLayout Layout;
    internal readonly int Length;
    internal readonly bool Nullable;

    internal Db2ColumnDecoder(FieldDescriptor field)
    {
        Field = field;
        Nullable = Db2Session.IsNullableType(field.Type);
        Length = (field.Precision << 8) | field.Scale;
        Layout = Db2FieldLayout.Fixed;
        switch (field.Type)
        {
            case 0x02: case 0x03: Length = 4; break;
            case 0x04: case 0x05: Length = 2; break;
            case 0x16: case 0x17: case 0x0a: case 0x0b: Length = 8; break;
            case 0x06: case 0x07: Length = 1; break;
            case 0x0c: case 0x0d: Length = 4; break;
            case 0x0e: case 0x0f: Length = (field.Precision + 2) / 2; break;
            case 0xba: case 0xbb: Length = field.Precision == 16 || field.Scale == 16 ? 16 : 8; break;
            case 0x36: case 0x37: Length *= 2; break;
            case 0x26: case 0x27: case 0x1e: case 0x1f: case 0xc0: case 0xc1: Length &= 0x7fff; break;
            case 0x30: case 0x31: case 0x3c: case 0x3d:
            case 0x20: case 0x21: case 0x22: case 0x23: case 0x24: case 0x25:
            case 0xbe: case 0xbf: break;
            case 0x32: case 0x33: case 0x34: case 0x35: case 0x3e: case 0x3f: case 0x40: case 0x41:
            case 0x28: case 0x29: case 0x2a: case 0x2b: case 0xc2: case 0xc3:
                Layout = Db2FieldLayout.Variable; break;
            case 0x38: case 0x39: case 0x3a: case 0x3b:
                Layout = Db2FieldLayout.VariableGraphic; break;
            default:
                Layout = Db2Session.IsLobType(field.Type) ? Db2FieldLayout.Lob : Db2FieldLayout.Unsupported;
                Length &= 0x7fff;
                if (Layout == Db2FieldLayout.Lob && Length == 0) Length = 4;
                break;
        }
    }

    internal static Db2ColumnDecoder[] Create(IReadOnlyList<FieldDescriptor> fields)
    {
        var result = new Db2ColumnDecoder[fields.Count];
        for (var i = 0; i < result.Length; i++) result[i] = new Db2ColumnDecoder(fields[i]);
        return result;
    }

    internal bool TrySkip(ReadOnlySpan<byte> data, ref int offset, out int valueStart, out int valueLength)
    {
        valueStart = offset;
        valueLength = -1;
        if (Nullable)
        {
            if (offset >= data.Length) return false;
            if (data[offset++] is 0xff or 0x80 or 0x7f) return true;
        }
        var count = Length;
        if (Layout is Db2FieldLayout.Variable or Db2FieldLayout.VariableGraphic)
        {
            if (data.Length - offset < 2) return false;
            count = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            if (Layout == Db2FieldLayout.VariableGraphic) count *= 2;
            offset += 2;
        }
        else if (Layout == Db2FieldLayout.Unsupported)
            throw new NotSupportedException($"Unsupported DB2 query field type 0x{Field.Type:X2}.");
        valueStart = offset;
        if (data.Length - offset < count) return false;
        offset += count;
        valueLength = count;
        if (Layout != Db2FieldLayout.Lob || (Field.Precision & 0x80) == 0 || Field.Scale != 9 || count != 9) return true;
        var locator = data.Slice(valueStart, count);
        if (locator[0] == 3)
        {
            if (data.Length - offset < 8) return false;
            offset += 8;
        }
        else if (locator[0] == 1)
        {
            var inlineLength = BinaryPrimitives.ReadUInt64BigEndian(locator[1..]);
            if (Db2Session.IsDbClobType(Field.Type)) inlineLength = checked(inlineLength * 2);
            if (inlineLength > int.MaxValue) throw new InvalidDataException("DB2 inline LOB length exceeds this runtime's array limit.");
            if ((ulong)(data.Length - offset) < inlineLength) return false;
            offset += (int)inlineLength;
        }
        return true;
    }
}
