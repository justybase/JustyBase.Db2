using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;

namespace Db2Replay;

/// <summary>Synthetic data only; these bytes are not a Db2 capture.</summary>
public sealed class ReplayFixture
{
    public string Name { get; }
    public int Rows { get; }
    public int Columns { get; }
    internal byte[] Data { get; }
    internal byte[] Descriptor { get; }
    internal byte[] Metadata { get; }
    public int DataLength => Data.Length;
    public string Sha256 => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Data)).ToLowerInvariant();
    public long IdChecksum => (long)Rows * (Rows + 1) / 2;
    public string Sql => "SELECT * FROM REPLAY." + Name;

    public ReplayFixture(string name, int? rows = null, bool generateData = true)
    {
        Name = name;
        (Rows, Columns) = name switch
        {
            "Int" => (rows ?? 1_000_000, 1),
            "Mixed" => (rows ?? 100_000, 6),
            "Wide" => (rows ?? 100_000, 40),
            "Discard" => (rows ?? 250_000, 6),
            "Small" => (rows ?? 1, 1),
            _ => throw new ArgumentException("Unknown replay fixture.", nameof(name))
        };
        if (Rows is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(rows));
        Descriptor = new byte[3 + Columns * 3];
        Descriptor[0] = checked((byte)Descriptor.Length);
        Descriptor[1] = 0x76; Descriptor[2] = 0xd0;
        Metadata = new byte[4 + 45 * Columns];
        Metadata[0] = 0xff; Metadata[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(Metadata.AsSpan(2), (ushort)Columns);
        for (var c = 0; c < Columns; c++)
        {
            var (type, precision, scale, sqlType, length) = Shape(c);
            Descriptor[3 + c * 3] = type;
            Descriptor[4 + c * 3] = precision;
            Descriptor[5 + c * 3] = scale;
            var at = Metadata.AsSpan(4 + 45 * c);
            BinaryPrimitives.WriteUInt16LittleEndian(at, precision);
            BinaryPrimitives.WriteUInt16LittleEndian(at[2..], sqlType == SqlType.Timestamp ? (ushort)6 : scale);
            BinaryPrimitives.WriteUInt64LittleEndian(at[4..], length);
            BinaryPrimitives.WriteUInt16LittleEndian(at[12..], (ushort)sqlType);
        }
        if (!generateData) { Data = []; return; }
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data, Encoding.UTF8, leaveOpen: true);
        for (var row = 1; row <= Rows; row++)
        {
            writer.Write((byte)0xff); writer.Write((byte)0);
            for (var c = 0; c < Columns; c++)
            {
                var value = Value(row, c);
                var kind = Kind(c);
                if (kind == 5)
                {
                    writer.Write(value is null ? (byte)0xff : (byte)0);
                    if (value is null) continue;
                }
                switch (kind)
                {
                    case 0: writer.Write((int)value!); break;
                    case 1: writer.Write((long)value!); break;
                    case 2:
                        var digits = ((long)((decimal)value! * 100)).ToString("D13", CultureInfo.InvariantCulture) + "C";
                        writer.Write(Convert.FromHexString(digits));
                        break;
                    case 3: writer.Write(Encoding.ASCII.GetBytes(((DateTime)value!).ToString("yyyy-MM-dd-HH.mm.ss.ffffff", CultureInfo.InvariantCulture))); break;
                    default:
                        var bytes = Encoding.UTF8.GetBytes((string)value!);
                        writer.Write((byte)(bytes.Length >> 8)); writer.Write((byte)bytes.Length); writer.Write(bytes);
                        break;
                }
            }
        }
        Data = data.ToArray();
    }

    private int Kind(int column) => Columns == 1 ? 0 : column % 6;
    public object? NativeValue(int row, int column) => Value(row, column) switch
    {
        decimal value => value.ToString("F2", CultureInfo.InvariantCulture),
        DateTime value => value.ToString("yyyy-MM-dd-HH.mm.ss.ffffff", CultureInfo.InvariantCulture),
        var value => value
    };
    public object? Value(int row, int column) => Kind(column) switch
    {
        0 => row + column,
        1 => (long)row * 10 + column,
        2 => row / 100m + column,
        3 => new DateTime(2026, 1, 1).AddTicks(row * 10L),
        4 => "value-" + row.ToString(CultureInfo.InvariantCulture),
        _ => row % 3 == 0 ? null : "nullable-" + row.ToString(CultureInfo.InvariantCulture)
    };

    private (byte Type, byte Precision, byte Scale, SqlType SqlType, ulong Length) Shape(int c) => Kind(c) switch
    {
        0 => (0x02, 0, 4, SqlType.Integer, 4),
        1 => (0x16, 0, 8, SqlType.BigInt, 8),
        2 => (0x0e, 12, 2, SqlType.Decimal, 7),
        3 => (0x24, 0, 26, SqlType.Timestamp, 26),
        4 => (0x32, 0, 32, SqlType.VarChar, 32),
        _ => (0x33, 0, 32, SqlType.NVarChar, 32)
    };
}
