using BenchmarkDotNet.Attributes;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Converters;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;

namespace ReaderBench;

[MemoryDiagnoser]
public class ParameterCollectionBenchmarks
{
    private Db2AdoParameterCollection _parameters = null!;
    [GlobalSetup]
    public void Setup()
    {
        _parameters = new Db2AdoParameterCollection();
        for (var i = 0; i < 16; i++) _parameters.Add(new Db2AdoParameter { ParameterName = "@parameter" + i, Value = i });
    }
    [Benchmark] public int Lookup() => _parameters.IndexOf(":PARAMETER15");
    [Benchmark] public Db2AdoParameter[] Snapshot() => _parameters.Snapshot();
}

[MemoryDiagnoser]
public class ParameterEncoderBenchmarks
{
    private readonly Db2ColumnMetadata[] _columns =
    [new("ID", SqlType.NInteger, 4, 10, 0, true),
     new("TEXT", SqlType.NVarChar, 100, 0, 0, true),
     new("D", SqlType.NDecimal, 8, 12, 2, true),
     new("T", SqlType.NTimestamp, 26, 0, 6, true)];
    private readonly object?[] _values = [42, "café 東京", 123.45m, new DateTime(2026, 1, 1, 12, 34, 56)];
    [Benchmark] public byte[] EncodeParameters() => SqlDtaEncoder.Build(_columns, _values);
}

[MemoryDiagnoser]
public class DssWriterBenchmarks
{
    private byte[] _payload = null!;
    [Params(32, 32761, 65535)] public int Size { get; set; }
    [GlobalSetup] public void Setup() => _payload = DdmCodec.PackObject(CodePoint.QRYDTA, new byte[Size]);
    private DssWriteResult? _requestSink;
    [Benchmark] public void Request() => _requestSink = DssCodec.EncodeRequest(_payload, 1, false, true);
    [Benchmark] public byte[] Reply() => DssCodec.EncodeReply(_payload, false, 1);
}

[MemoryDiagnoser]
public class DdmParameterBenchmarks
{
    private byte[] _data = null!;
    [GlobalSetup]
    public void Setup()
    {
        using var memory = new MemoryStream();
        for (var i = 0; i < 8; i++) memory.Write(DdmCodec.PackUInt64((CodePoint)(0x2100 + i), (ulong)i));
        _data = memory.ToArray();
    }
    [Benchmark(Baseline = true)] public Dictionary<ushort, byte[]> ParseDictionary() => DdmCodec.ParseParameters(_data);
    [Benchmark] public ulong ReadParameterView() => DdmCodec.TryGetParameter(_data, 0x2107, out var value)
        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(value) : 0;
}

[MemoryDiagnoser]
public class RowLayoutBenchmarks
{
    private readonly FieldDescriptor[] _fields = [new(0x02, 0, 4)];
    private Db2ColumnDecoder[] _plans = null!;
    private byte[] _data = null!;
    [GlobalSetup]
    public void Setup()
    {
        _plans = Db2ColumnDecoder.Create(_fields);
        _data = new byte[60000];
        for (var i = 0; i < 10000; i++)
        {
            _data[i * 6] = 0xff;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(_data.AsSpan(i * 6 + 2), 42);
        }
    }
    [Benchmark(Baseline = true)]
    public long MaterializeRows()
    {
        long sum = 0;
        foreach (var row in JustyBase.Db2.Db2Session.DecodeRowsIncremental(_fields, _data).Rows) sum += (int)row[0]!;
        return sum;
    }
    [Benchmark]
    public long MeasureRows()
    {
        long sum = 0;
        var offset = 0;
        while (JustyBase.Db2.Db2Session.TryMeasureRow(_plans, _data, ref offset, out var fields, out _))
            sum += System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(fields));
        return sum;
    }
}

[MemoryDiagnoser]
public class BufferedRowsBenchmarks
{
    private byte[] _data = null!;
    private Db2BufferedRows _rows = null!;
    [GlobalSetup]
    public void Setup()
    {
        _rows = new Db2BufferedRows(Db2ColumnDecoder.Create([new FieldDescriptor(0x02, 0, 4)]));
        _data = new byte[60000];
        for (var i = 0; i < 10000; i++)
        {
            _data[i * 6] = 0xff;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(_data.AsSpan(i * 6 + 2), 42);
        }
    }
    [GlobalCleanup] public void Cleanup() => _rows.Dispose();
    [Benchmark]
    public long LoadAndReadIntRows()
    {
        _rows.BeginFetch(); _rows.Append(_data); _rows.Validate(static _ => { });
        long sum = 0;
        while (_rows.TryRead()) sum += System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(_rows.GetSpan(0));
        return sum;
    }
}

[MemoryDiagnoser]
public class RowValueParserBenchmarks
{
    private delegate string PackedParser(ReadOnlySpan<byte> bytes, int scale);
    private PackedParser _legacy = null!;
    [GlobalSetup] public void Setup() => _legacy = typeof(JustyBase.Db2.Db2Session).GetMethod("DecodePackedDecimal", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.CreateDelegate<PackedParser>();
    [Benchmark] public decimal StringPackedDecimal() => decimal.Parse(_legacy(_decimal, 2), System.Globalization.CultureInfo.InvariantCulture);
    private readonly byte[] _decimal = [0, 0, 0x12, 0x34, 0x5c];
    private readonly byte[] _date = System.Text.Encoding.ASCII.GetBytes("2026-01-01");
    [Benchmark] public decimal PackedDecimal() => JustyBase.Db2.Converters.RowValueParser.TryDecimal(_decimal, 2, out var value) ? value : 0;
    [Benchmark] public DateTime Utf8Date() => JustyBase.Db2.Converters.RowValueParser.TryDate(_date, out var value) ? value : default;
    [Benchmark] public DateTime StringDate() => DateTime.ParseExact(System.Text.Encoding.UTF8.GetString(_date), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

[MemoryDiagnoser]
public class MetadataPlanBenchmarks
{
    private byte[] _descriptor = null!;
    private Db2ResultMetadataCache _cache = null!;
    [Params(1, 33)] public int Columns { get; set; }
    [GlobalSetup] public void Setup()
    {
        _descriptor = new byte[(Columns + 1) * 3];
        _descriptor[0] = (byte)_descriptor.Length; _descriptor[1] = 0x76; _descriptor[2] = 0xd0;
        for (var i = 0; i < Columns; i++) { _descriptor[3 + i * 3] = 3; _descriptor[5 + i * 3] = 4; }
        _cache = new Db2ResultMetadataCache(); _cache.ReadFields(_descriptor);
    }
    [Benchmark(Baseline = true)] public object ParsePlans() => Db2ColumnDecoder.Create(DdmCodec.ParseQueryDescriptor(_descriptor));
    [Benchmark] public object ReusePlans() { _cache.ReadFields(_descriptor); return _cache.Decoders; }
}


[MemoryDiagnoser]
public class ImporterStatementBenchmarks
{
    private readonly string[] _columns = ["\"ID\"", "\"TEXT\""];
    private Db2InsertStatementCache _cache = null!;
    [GlobalSetup] public void Setup() { _cache = new Db2InsertStatementCache("\"T\"", _columns); _cache.Get(128); }
    [Benchmark(Baseline = true)] public string BuildSql() => Db2AdoImporter.BuildInsertStatement("\"T\"", _columns, 128);
    [Benchmark] public string ReuseSql() => _cache.Get(128);
}


[MemoryDiagnoser]
public class EbcdicBenchmarks
{
    private readonly byte[] _bytes = System.Text.Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz0123456789");
    private const string Text = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    [Benchmark] public string Decode() => Ebcdic.DecodeCp500(_bytes);
    [Benchmark] public byte[] Encode() => Ebcdic.EncodeCp500(Text);
}

[MemoryDiagnoser]
public class DecfloatBenchmarks
{
    private byte[] _value = null!;
    [Params(8, 16)] public int Width { get; set; }
    [GlobalSetup] public void Setup() => _value = Decfloat.Encode("-123456.789", Width);
    [Benchmark] public string Decode() => Decfloat.Decode(_value);
    [Benchmark] public byte[] Encode() => Decfloat.Encode("-123456.789", Width);
}

[MemoryDiagnoser]
public class TemporalTextParameterBenchmarks
{
    private readonly Db2ColumnMetadata[] _columns = [new("D", SqlType.NDate, 10, 0, 0, true)];
    private readonly object?[] _values = ["2026-1-2"];
    [Benchmark] public byte[] EncodeDateText() => SqlDtaEncoder.Build(_columns, _values);
}

[MemoryDiagnoser]
public class LobMaterializationBenchmarks
{
    private byte[] _bytes = null!;
    [Params(8192, 1048576)] public int Size { get; set; }
    [GlobalSetup] public void Setup() => _bytes = new byte[Size];
    [Benchmark] public object Materialize() => new JustyBase.Db2.Db2LobValue(Size, null, Open).Materialize();
    private Stream Open() => new MemoryStream(_bytes, writable: false);
}


[MemoryDiagnoser]
public class ParameterDescriptorCacheBenchmarks
{
    [Params(4, 33, 256)] public int Count { get; set; }
    private Db2ColumnMetadata[] _columns = null!;
    private object?[] _values = null!;
    private readonly SqlDtaEncoder.DescriptorCache _cache = new();
    [GlobalSetup] public void Setup()
    {
        _columns = new Db2ColumnMetadata[Count]; _values = new object?[Count];
        for (var i = 0; i < Count; i++) { _columns[i] = new("I", SqlType.NInteger, 4, 10, 0, true); _values[i] = i; }
        SqlDtaEncoder.BuildWithExternalData(_columns, _values, _cache);
    }
    [Benchmark(Baseline = true)] public byte[] Encode() => SqlDtaEncoder.Build(_columns, _values);
    [Benchmark] public byte[] CachedEncode() => SqlDtaEncoder.BuildWithExternalData(_columns, _values, _cache).SqlDta;
}


[MemoryDiagnoser]
public class ProgressiveLobBufferedBenchmarks
{
    private readonly byte[] _chunk = new byte[1048576];
    private readonly byte[] _destination = new byte[16384];
    private ValueTask<(byte[] Data, bool IsFinal)> Fetch(CancellationToken _) => ValueTask.FromResult((_chunk, true));
    [Benchmark]
    public long ReadSync()
    {
        using var stream = new JustyBase.Db2.Db2ProgressiveLobStream(_chunk.Length, Fetch);
        long count = 0; int read; while ((read = stream.Read(_destination, 0, _destination.Length)) != 0) count += read;
        return count;
    }
}


[MemoryDiagnoser]
public class ProcedureRowBufferBenchmarks
{
    private readonly byte[] _data = new byte[60000];
    private readonly FieldDescriptor[] _fields = [new(2, 0, 4)];
    private readonly Db2RowAccumulator _buffer = new();
    private MemoryStream _rowData = null!;
    [GlobalSetup] public void Setup()
    {
        for (var i = 0; i < 10000; i++) { _data[i * 6] = 0xff; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(_data.AsSpan(i * 6 + 2), i); }
        _rowData = new MemoryStream(); _rowData.Write(_data);
    }
    [GlobalCleanup] public void Cleanup() { _buffer.Dispose(); _rowData.Dispose(); }
    [Benchmark(Baseline = true)] public int OwnedCopy() => JustyBase.Db2.Db2Session.DecodeRowsWithWarnings(_fields, _rowData.ToArray()).Rows.Count;
    [Benchmark] public int OwnedAccumulator()
    {
        _buffer.Append(_rowData.GetBuffer().AsSpan(0, (int)_rowData.Length));
        var result = JustyBase.Db2.Db2Session.DecodeRowsWithWarnings(_fields, _buffer.Data);
        _buffer.Consume(result.Consumed); return result.Rows.Count;
    }
}

[MemoryDiagnoser]
public class DdmRequestWriterBenchmarks
{
    public enum Request { Package, Prepare, Open, Continue, SqlShort, SqlExtended }
    [ParamsAllValues] public Request Kind { get; set; }
    private string _sql = null!;
    [GlobalSetup] public void Setup() => _sql = Kind == Request.SqlExtended ? new string('x', 40000) : "SELECT ID FROM P1BENCH.NARROW_1M";
    [Benchmark] public byte[] Encode() => Kind switch
    {
        Request.Package => DdmCodec.PackPackageNameCsn("PERFDB2", "SYSSH200", "SYSLVL01", 65),
        Request.Prepare => DdmCodec.PackPrepareSqlStatement("SYSSH200", "SYSLVL01", 65, "PERFDB2"),
        Request.Open => DdmCodec.PackOpenQuery("SYSSH200", "SYSLVL01", 65, "PERFDB2", 65535),
        Request.Continue => DdmCodec.PackContinueQuery("SYSSH200", "SYSLVL01", 65, "PERFDB2", 65535, 123),
        _ => DdmCodec.PackSqlStatement(_sql),
    };
}

[MemoryDiagnoser]
public class DssLargeOwnedBenchmarks
{
    private MemoryStream _stream = null!;
    private DssReadBuffer _reader = null!;
    [GlobalSetup] public void Setup()
    {
        var wire = DssCodec.EncodeReply(DdmCodec.PackObject(CodePoint.EXTDTA, new byte[8 * 1024 * 1024]), false, 1);
        _stream = new MemoryStream(wire); _reader = new DssReadBuffer(_stream);
    }
    [GlobalCleanup] public void Cleanup() { _reader.Dispose(); _stream.Dispose(); }
    private DssPacket? _packetSink;
    // MemoryStream-backed reads complete synchronously; the sink keeps the decoded packet alive.
    [Benchmark(Baseline = true)] public void OriginalSegmentedCodec()
    { _stream.Position = 0; _packetSink = DssCodec.ReadAsync(_stream).GetAwaiter().GetResult(); }
    [Benchmark] public void BufferedOwnedDecoder()
    { _stream.Position = 0; _packetSink = _reader.ReadAsync().GetAwaiter().GetResult(); }
}
