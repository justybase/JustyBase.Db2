namespace JustyBase.Db2.Protocol;

/// <summary>Private per-statement cache; a byte mismatch always runs the original parser.</summary>
internal sealed class Db2ResultMetadataCache
{
    private byte[]? _sqlDard;
    private byte[]? _queryDescriptor;
    private Db2ColumnMetadata[] _columns = [];
    private FieldDescriptor[] _fields = [];
    internal Db2ColumnDecoder[] Decoders { get; private set; } = [];

    internal Db2ColumnMetadata[] ReadColumns(ReadOnlySpan<byte> bytes)
    {
        if (_sqlDard is not null && bytes.SequenceEqual(_sqlDard)) return _columns;
        var columns = DdmCodec.ParseSqlDard(bytes);
        _sqlDard = bytes.ToArray(); _columns = columns;
        return columns;
    }
    internal FieldDescriptor[] ReadFields(ReadOnlySpan<byte> bytes)
    {
        if (_queryDescriptor is not null && bytes.SequenceEqual(_queryDescriptor)) return _fields;
        var fields = DdmCodec.ParseQueryDescriptor(bytes);
        var decoders = Db2ColumnDecoder.Create(fields);
        _queryDescriptor = bytes.ToArray(); _fields = fields; Decoders = decoders;
        return fields;
    }
}
