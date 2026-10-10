using System.Buffers;
using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using BenchmarkDotNet.Attributes;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Protocol;

namespace ReaderBench;

[MemoryDiagnoser]
public class Db2ReaderBenchmarks
{
    private Db2AdoConnection _connection = null!;
    private const string IntSql = "SELECT ID FROM P1BENCH.NARROW_1M";
    private const string DecimalTimestampSql = "SELECT ID, D, H FROM P1BENCH.MIXED_10K";
    private const string LobSql = "SELECT ID, 'varchar-value' AS V, C, B FROM P1BENCH.LOB_4K";
    private const string LargeLobSql = "SELECT ID, B FROM P1BENCH.LOB_8M";
    private const string NarrowFiveSql = "SELECT ID, A, B, ID, A FROM P1BENCH.NARROW_100K";
    private const string SmallSql = "SELECT 1 FROM SYSIBM.SYSDUMMY1";
    private const string NarrowValuesSql = "SELECT ID, A, CAST(ID AS BIGINT), CAST(A AS DECIMAL(12,2)), TIMESTAMP('2026-01-01-12.34.56.123456') FROM P1BENCH.NARROW_100K";
    private static readonly string WideNullSql = BuildWideNullSql();
    private static readonly string WideValuesSql = BuildWideValuesSql();

    [GlobalSetup(Target = nameof(SmallResultRepeated))]
    public async Task Setup()
    {
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")
            ?? throw new InvalidOperationException("Set DB2_TEST_DSN before running the live reader benchmarks.");
        _connection = new Db2AdoConnection(dsn);
        await _connection.OpenAsync();
    }

    // Checks are outside timed iterations and only need the selected fixture.
    [GlobalSetup(Target = nameof(Int1M))] public Task SetupInt() => SetupChecked(IntSql, 1_000_000, 1);
    [GlobalSetup(Target = nameof(NarrowFiveColumns))] public Task SetupNarrow() => SetupChecked(NarrowFiveSql, 100_000, 5);
    [GlobalSetup(Target = nameof(WideColumnsWithNulls))] public Task SetupWideNull() => SetupChecked(WideNullSql, 100_000, 33);
    [GlobalSetup(Target = nameof(NarrowValueColumns))] public Task SetupNarrowValue() => SetupChecked(NarrowValuesSql, 100_000, 5);
    [GlobalSetup(Target = nameof(WideValueColumns))] public Task SetupWideValue() => SetupChecked(WideValuesSql, 100_000, 33);
    [GlobalSetup(Target = nameof(DecimalTimestamp))] public Task SetupTemporal() => SetupChecked(DecimalTimestampSql, 10_000, 3);
    [GlobalSetup(Target = nameof(VarcharClobAndBlob))] public Task SetupLob() => SetupChecked(LobSql, 1_000, 4);
    [GlobalSetup(Target = nameof(SequentialLargeLob))] public Task SetupLargeLob() => SetupChecked(LargeLobSql, 1, 2);
    private async Task SetupChecked(string sql, int rows, int columns)
    {
        await Setup();
        await VerifyShape(sql, rows, columns);
    }

    private async Task VerifyShape(string sql, int expectedRows, int expectedColumns)
    {
        await using var command = CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();
        if (reader.FieldCount != expectedColumns) throw new InvalidOperationException("Benchmark fixture has an unexpected column count.");
        long sum = 0; var rows = 0;
        while (await reader.ReadAsync()) { rows++; sum += reader.GetInt32(0); }
        if (rows != expectedRows || sum != (long)expectedRows * (expectedRows + 1) / 2)
            throw new InvalidOperationException($"Benchmark fixture returned {rows} rows / ID checksum {sum}; expected {expectedRows} complete rows.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        Db2AdoConnection.ClearAllPools();
    }

    [Benchmark(Description = "INT x 1M, GetInt32")]
    public async Task<long> Int1M()
    {
        await using var command = CreateCommand(IntSql);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess).ConfigureAwait(false);
        long sum = 0;
        while (await reader.ReadAsync().ConfigureAwait(false)) sum += reader.GetInt32(0);
        return sum;
    }

    [Benchmark(Description = "Narrow 5 columns x 100K")]
    public async Task<long> NarrowFiveColumns()
    {
        await using var command = CreateCommand(NarrowFiveSql);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        long sum = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            sum += reader.GetInt32(0) + reader.GetInt32(1) + reader.GetString(2).Length
                + reader.GetInt32(3) + reader.GetInt32(4);
        }
        return sum;
    }

    [Benchmark(Description = "Wide 33 columns with NULLs x 100K")]
    public async Task<long> WideColumnsWithNulls()
    {
        await using var command = CreateCommand(WideNullSql);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        long sum = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            sum += reader.GetInt32(0);
            for (var ordinal = 2; ordinal < reader.FieldCount; ordinal++)
                if (reader.IsDBNull(ordinal)) sum++;
        }
        return sum;
    }

    [Benchmark(Description = "Narrow 5 value columns x 100K, typed getters")]
    public async Task<decimal> NarrowValueColumns()
    {
        await using var command = CreateCommand(NarrowValuesSql);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        decimal sum = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
            sum += reader.GetInt32(0) + reader.GetInt32(1) + reader.GetInt64(2)
                + reader.GetDecimal(3) + reader.GetDateTime(4).Ticks % 1000;
        return sum;
    }

    [Benchmark(Description = "Wide 33 value columns/NULLs x 100K, typed getters")]
    public async Task<long> WideValueColumns()
    {
        await using var command = CreateCommand(WideValuesSql);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        long sum = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                if (!reader.IsDBNull(ordinal)) sum += reader.GetInt32(ordinal);
        return sum;
    }

    [Benchmark(Description = "VARCHAR/CLOB/BLOB x 1K")]
    public async Task<long> VarcharClobAndBlob()
    {
        await using var command = CreateCommand(LobSql);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var byteBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        var charBuffer = ArrayPool<char>.Shared.Rent(8 * 1024);
        try
        {
            long consumed = 0;
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                consumed += reader.GetInt32(0);
                consumed += reader.GetString(1).Length;
                using (var clob = reader.GetTextReader(2))
                {
                    int chars;
                    while ((chars = await clob.ReadAsync(charBuffer.AsMemory()).ConfigureAwait(false)) != 0) consumed += chars;
                }
                await using var blob = reader.GetStream(3);
                int read;
                while ((read = await blob.ReadAsync(byteBuffer.AsMemory()).ConfigureAwait(false)) != 0) consumed += read;
            }
            return consumed;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(byteBuffer);
            ArrayPool<char>.Shared.Return(charBuffer);
        }
    }

    [Benchmark(Description = "DECIMAL/TIMESTAMP x 10K")]
    public async Task<decimal> DecimalTimestamp()
    {
        await using var command = CreateCommand(DecimalTimestampSql);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        decimal sum = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
            sum += reader.GetInt32(0) + reader.GetDecimal(1) + reader.GetDateTime(2).Ticks % 1000;
        return sum;
    }

    [Benchmark(Description = "SequentialAccess BLOB x 8M")]
    public async Task<long> SequentialLargeLob()
    {
        await using var command = CreateCommand(LargeLobSql);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            long consumed = 0;
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                consumed += reader.GetInt32(0);
                await using var blob = reader.GetStream(1);
                int read;
                while ((read = await blob.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0) consumed += read;
            }
            return consumed;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    [Benchmark(Description = "Small result ExecuteReader + Close")]
    public async Task<int> SmallResultRepeated()
    {
        await using var command = CreateCommand(SmallSql);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return await reader.ReadAsync().ConfigureAwait(false) ? reader.GetInt32(0) : 0;
    }

    private Db2AdoCommand CreateCommand(string sql)
    {
        var command = (Db2AdoCommand)_connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static string BuildWideNullSql()
    {
        var select = new System.Text.StringBuilder("SELECT ID, A");
        for (var index = 0; index < 31; index++)
            select.Append(", CAST(NULL AS INTEGER) AS N").Append(index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
        return select.Append(" FROM P1BENCH.NARROW_100K").ToString();
    }

    private static string BuildWideValuesSql()
    {
        var select = new System.Text.StringBuilder("SELECT ID");
        for (var ordinal = 1; ordinal < 33; ordinal++)
            select.Append(ordinal % 2 == 0 ? ", CAST(NULL AS INTEGER)" : ", A")
                .Append(" AS C").Append(ordinal.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
        return select.Append(" FROM P1BENCH.NARROW_100K").ToString();
    }
}

[MemoryDiagnoser]
public class OfflineDssFrameBenchmarks
{
    private const ushort ContinuationFlag = 0x8000;
    private MemoryStream _stream = null!;
    private byte[] _fixture = null!;
    private JustyBase.Db2.Protocol.DssReadBuffer _reader = null!;
    private JustyBase.Db2.Protocol.DssPacket? _packetSink;

    [GlobalSetup]
    public void Setup()
    {
        // Deterministic QRYDTA-shaped fixture: one DDM object wrapped in one DSS reply.
        // This is an offline codec benchmark, not a capture from a live Db2 session.
        var payload = new byte[32 * 1024];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index * 31);
        var ddm = new byte[payload.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(ddm, checked((ushort)ddm.Length));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(ddm.AsSpan(2), 0x2412);
        payload.CopyTo(ddm, 4);
        _fixture = JustyBase.Db2.Protocol.DssCodec.EncodeReply(ddm, chained: false, correlationId: 1);
        _stream = new MemoryStream(_fixture);
        _reader = new JustyBase.Db2.Protocol.DssReadBuffer(_stream);
    }

    [GlobalCleanup]
    public void Cleanup() { _reader.Dispose(); _stream.Dispose(); }

    [Benchmark(Baseline = true, Description = "Before: MemoryStream + copies")]
    public void ReadFrameBeforePooling()
    {
        _stream.Position = 0;
        _packetSink = ReadLegacyAsync(_stream).GetAwaiter().GetResult();
    }

    [Benchmark(Description = "After: pooled DSS body")]
    public void ReadFramePooled()
    {
        _stream.Position = 0;
        _packetSink = JustyBase.Db2.Protocol.DssCodec.ReadAsync(_stream).GetAwaiter().GetResult();
    }

    [Benchmark(Description = "Session: reusable read buffer")]
    public void ReadFrameBuffered()
    {
        _stream.Position = 0;
        _packetSink = _reader.ReadAsync().GetAwaiter().GetResult();
    }

    [Benchmark(Description = "Session: borrowed reply chain")]
    public async ValueTask<int> ReadFrameBorrowed()
    {
        _stream.Position = 0;
        using var chain = await _reader.ReadChainAsync();
        return chain[0].Payload.Length;
    }

    private static async ValueTask<JustyBase.Db2.Protocol.DssPacket> ReadLegacyAsync(Stream stream)
    {
        var rawHeader = new byte[6];
        await stream.ReadExactlyAsync(rawHeader).ConfigureAwait(false);
        if (rawHeader[2] != JustyBase.Db2.Protocol.DssCodec.Magic)
            throw new InvalidDataException("Invalid DSS packet header.");
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(rawHeader);
        var flags = rawHeader[3];
        var header = new JustyBase.Db2.Protocol.DssHeader(
            length,
            (byte)(flags & 0x0f),
            (flags & JustyBase.Db2.Protocol.DssCodec.ChainedFlag) != 0,
            (flags & JustyBase.Db2.Protocol.DssCodec.SameIdFlag) != 0,
            (flags & JustyBase.Db2.Protocol.DssCodec.ErrorFlag) != 0,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(rawHeader.AsSpan(4)));
        var firstLength = length & ~ContinuationFlag;
        if (firstLength < 6) throw new InvalidDataException("Invalid DSS frame length.");
        using var body = new MemoryStream();
        var firstSegment = new byte[firstLength - 6];
        await stream.ReadExactlyAsync(firstSegment).ConfigureAwait(false);
        body.Write(firstSegment);
        var continued = (length & ContinuationFlag) != 0;
        while (continued)
        {
            var continuationHeader = new byte[2];
            await stream.ReadExactlyAsync(continuationHeader).ConfigureAwait(false);
            var continuationLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(continuationHeader);
            continued = (continuationLength & ContinuationFlag) != 0;
            var count = continuationLength & ~ContinuationFlag;
            if (count < 2) throw new InvalidDataException("Invalid DSS continuation length.");
            var continuation = new byte[count - 2];
            await stream.ReadExactlyAsync(continuation).ConfigureAwait(false);
            body.Write(continuation);
        }
        var (codePoint, payload) = JustyBase.Db2.Protocol.DssCodec.ReadDdmObject(body.ToArray());
        return new JustyBase.Db2.Protocol.DssPacket(header, codePoint, payload);
    }
}

[MemoryDiagnoser]
public class OfflineQrydtaDecodeBenchmarks
{
    private const int RowCount = 10_000;
    private readonly FieldDescriptor[] _fields = [new(0x02, 0, 4)];
    private byte[] _rowBlock = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Synthetic row shape from the protocol tests: SQLCA absent (FF 00), followed by INT32 LE.
        // A live QRYDTA capture is intentionally kept separate from this deterministic fixture.
        _rowBlock = new byte[RowCount * 6];
        for (var row = 0; row < RowCount; row++)
        {
            var offset = row * 6;
            _rowBlock[offset] = 0xff;
            _rowBlock[offset + 1] = 0;
            BinaryPrimitives.WriteInt32LittleEndian(_rowBlock.AsSpan(offset + 2, 4), row + 1);
        }
    }

    [Benchmark(Description = "Decode 10K QRYDTA-shaped INT rows")]
    public long DecodeRows()
    {
        var rows = Db2Session.DecodeRowsIncremental(_fields, _rowBlock).Rows;
        long sum = 0;
        foreach (var row in rows) sum += (int)row[0]!;
        return sum;
    }
}

[MemoryDiagnoser]
public class Db2BlockSizeBenchmarks
{
    private Db2AdoConnection _connection = null!;
    [Params(16384, 32767, 65535)] public int BlockSize { get; set; }
    [GlobalSetup]
    public async Task Setup()
    {
        var config = Db2Config.Parse(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        // Use the ADO spelling accepted by Db2Config; pooling identity includes block size.
        var builder = new Db2AdoConnectionStringBuilder { Server = config.Host + ":" + config.Port,
            Database = config.Database, UserID = config.User, Password = config.Password };
        builder["BlockSize"] = BlockSize;
        _connection = new Db2AdoConnection(builder.ConnectionString); await _connection.OpenAsync();
        if (await ReadInt1M() != 500000500000L)
            throw new InvalidOperationException("Block-size benchmark did not return the complete 1M-row ID checksum.");
    }
    [GlobalCleanup] public async Task Cleanup() { await _connection.DisposeAsync(); Db2AdoConnection.ClearAllPools(); }
    [Benchmark]
    public async Task<long> ReadInt1M()
    {
        await using var command = _connection.CreateCommand(); command.CommandText = "SELECT ID FROM P1BENCH.NARROW_1M";
        await using var reader = await command.ExecuteReaderAsync();
        long sum = 0; while (await reader.ReadAsync()) sum += reader.GetInt32(0);
        return sum;
    }
}
