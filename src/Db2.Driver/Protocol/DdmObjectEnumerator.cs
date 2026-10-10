using System.Buffers.Binary;

namespace JustyBase.Db2.Protocol;

internal readonly ref struct DdmObjectView(ushort codePoint, ReadOnlySpan<byte> payload)
{
    internal ushort CodePoint { get; } = codePoint;
    internal ReadOnlySpan<byte> Payload { get; } = payload;
}

/// <summary>Borrowed views, valid only while the input span remains owned by the caller.</summary>
internal ref struct DdmObjectEnumerator(ReadOnlySpan<byte> data, bool extended = false)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private readonly bool _extended = extended;
    private int _offset;
    public DdmObjectView Current { get; private set; }
    public DdmObjectEnumerator GetEnumerator() => this;

    public bool MoveNext()
    {
        if (_offset == _data.Length) return false;
        if (_data.Length - _offset < 4)
            throw new InvalidDataException(_extended ? $"Truncated FDOCA result-set descriptor at offset {_offset}: need 4, have {_data.Length - _offset}." : "Incomplete DDM parameter header in reply.");
        var encoded = BinaryPrimitives.ReadUInt16BigEndian(_data[_offset..]);
        var codePoint = BinaryPrimitives.ReadUInt16BigEndian(_data[(_offset + 2)..]);
        var header = 4;
        ulong total = encoded;
        if (_extended && (encoded & 0x8000) != 0)
        {
            header = encoded & 0x7fff;
            if (header is not (8 or 12)) throw new InvalidDataException($"Unsupported nested DDM header length {header}.");
            if (_data.Length - _offset < header) throw new InvalidDataException($"Truncated FDOCA result-set descriptor at offset {_offset}: need {header}, have {_data.Length - _offset}.");
            var content = header == 8 ? BinaryPrimitives.ReadUInt32BigEndian(_data[(_offset + 4)..]) : BinaryPrimitives.ReadUInt64BigEndian(_data[(_offset + 4)..]);
            // Check content before addition: an eight-byte length can otherwise wrap.
            if (content > (ulong)(_data.Length - _offset - header)) throw new InvalidDataException("Invalid nested DDM object length.");
            total = content + (uint)header;
        }
        if (total < (ulong)header || total > (ulong)(_data.Length - _offset))
        {
            if (_extended) throw new InvalidDataException("Invalid nested DDM object length.");
            throw new InvalidDataException($"Invalid DDM parameter length {encoded} at offset {_offset}.");
        }
        Current = new DdmObjectView(codePoint, _data.Slice(_offset + header, (int)total - header));
        _offset += (int)total;
        return true;
    }
}
