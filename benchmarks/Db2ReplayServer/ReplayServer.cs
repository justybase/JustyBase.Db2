using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JustyBase.Db2.Protocol;

namespace Db2Replay;

public sealed record ReplayOptions(int FragmentSize = int.MaxValue, int LateSqlCode = 0,
    string LateSqlState = "00000", bool TruncateLastRow = false, bool EndWithSqlCode100 = false,
    int LastRowSqlCode = 0, bool MalformedLastRow = false,
    Func<CancellationToken, Task>? BeforeContinue = null, bool NoProgressOnce = false);

public sealed record ReplayCounters(long Queries, long Fetches, long BytesSent, long DssPackets,
    long QrydtaPackets, long ContinuedPackets, long Commits, long ActiveQueries);

/// <summary>A deliberately limited, stateful, synthetic DRDA peer. Unexpected requests fail the run.</summary>
public sealed class ReplayServer : IAsyncDisposable
{
    private const string PoolProbeSql = "VALUES (CAST(RTRIM(SESSION_USER) AS VARCHAR(255)), " +
        "CAST(CURRENT SCHEMA AS VARCHAR(255)), CAST(CURRENT ISOLATION AS VARCHAR(255)), " +
        "CAST(COALESCE(CURRENT CLIENT_APPLNAME, '') AS VARCHAR(255)), " +
        "CAST(COALESCE(CURRENT CLIENT_WRKSTNNAME, '') AS VARCHAR(255)), " +
        "CAST(COALESCE(CURRENT CLIENT_USERID, '') AS VARCHAR(255)), " +
        "CAST(COALESCE(CURRENT CLIENT_ACCTNG, '') AS VARCHAR(255)))";
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Task, byte> _clients = new();
    private readonly ConcurrentQueue<Exception> _failures = [];
    private readonly ConcurrentDictionary<TcpClient, byte> _sockets = new();
    private readonly string _sql;
    private readonly byte[] _metadata, _descriptor;
    private readonly ReplayOptions _options;
    private readonly Dictionary<int, byte[][]> _blocks = [];
    private readonly Task _accept;
    private long _queries, _fetches, _bytes, _packets, _qrydta, _continued, _commits, _activeQueries;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public ReplayCounters Counters => new(Interlocked.Read(ref _queries), Interlocked.Read(ref _fetches),
        Interlocked.Read(ref _bytes), Interlocked.Read(ref _packets), Interlocked.Read(ref _qrydta),
        Interlocked.Read(ref _continued), Interlocked.Read(ref _commits), Interlocked.Read(ref _activeQueries));

    public ReplayServer(ReplayFixture fixture, ReplayOptions? options = null, IReadOnlyList<int>? blockSizes = null)
    {
        _sql = fixture.Sql; _metadata = fixture.Metadata; _descriptor = fixture.Descriptor;
        _options = options ?? new();
        if (_options.FragmentSize < 1) throw new ArgumentOutOfRangeException(nameof(options));
        var data = fixture.Data;
        if (_options.LastRowSqlCode != 0 || _options.MalformedLastRow)
        {
            if (fixture.Columns != 1) throw new ArgumentException("Row SQLCA injection requires an INT fixture.", nameof(options));
            // Minimal row SQLCA: absent SQLCAXGRP and SQLDIAGGRP, then row indicator.
            var injected = new byte[data.Length + 19];
            data.AsSpan(0, data.Length - 6).CopyTo(injected);
            var at = injected.AsSpan(data.Length - 6);
            BinaryPrimitives.WriteInt32LittleEndian(at[1..], _options.LastRowSqlCode);
            Encoding.ASCII.GetBytes(_options.LastRowSqlCode < 0 ? "21000" : "01003").CopyTo(at[5..]);
            at[18] = _options.MalformedLastRow ? (byte)0x7e : (byte)0xff;
            at[19] = 0xff;
            data.AsSpan(data.Length - 4).CopyTo(at[21..]);
            data = injected;
        }
        // Precompute all supported experimental fetch shapes; generation is never timed.
        foreach (var size in blockSizes ?? new[] { 16384, 32767, 65535 })
        {
            if (size is not (16384 or 32767 or 65535)) throw new ArgumentOutOfRangeException(nameof(blockSizes));
            var length = data.Length - (_options.TruncateLastRow ? 1 : 0);
            var blocks = new List<byte[]>();
            for (var offset = 0; offset < length; offset += size)
                blocks.Add(Packet(CodePoint.QRYDTA, data.AsSpan(offset, Math.Min(size, length - offset)), true));
            _blocks.Add(size, blocks.ToArray());
        }
        _listener.Start(16);
        _accept = AcceptAsync();
    }

    public void ThrowIfFailed()
    {
        if (_failures.TryPeek(out var error)) throw new InvalidOperationException("Replay conversation failed.", error);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (_sockets.Count >= 8)
                {
                    client.Dispose();
                    _failures.Enqueue(new InvalidDataException("Replay connection limit exceeded."));
                    continue;
                }
                client.NoDelay = true;
                _sockets.TryAdd(client, 0);
                var task = ServeAsync(client);
                _clients.TryAdd(task, 0);
                _ = task.ContinueWith(completed => _clients.TryRemove(completed, out _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var phase = 0;
            var active = false;
            var next = 0;
            var size = 65535;
            var noProgressSent = false;
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var chain = await ReadChainAsync(stream, _stop.Token);
                    if (chain is null) break;
                    var codes = chain.Select(p => (CodePoint)p.CodePoint).ToArray();
                    if (phase == 0)
                    {
                        Require(codes.SequenceEqual(new[] { CodePoint.EXCSAT, CodePoint.ACCSEC }), "negotiation");
                        await SendAsync(stream, Packet(CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3)));
                        phase++;
                    }
                    else if (phase == 1)
                    {
                        Require(codes.SequenceEqual(new[] { CodePoint.SECCHK, CodePoint.ACCRDB }), "authentication");
                        // Never inspect or retain SECCHK authentication payloads.
                        await SendAsync(stream, Packet(CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true));
                        await SendAsync(stream, Packet(CodePoint.ACCRDBRM, []));
                        phase++;
                    }
                    else if (phase == 2)
                    {
                        Require(codes.SequenceEqual(new[] { CodePoint.EXCSAT, CodePoint.EXCSQLSET, CodePoint.SQLSTT, CodePoint.RDBCMM }), "initialization");
                        await SendAsync(stream, Packet(CodePoint.SQLCARD, SqlCard()));
                        phase++;
                    }
                    else if (codes.SequenceEqual(new[] { CodePoint.PRPSQLSTT, CodePoint.SQLSTT, CodePoint.OPNQRY }))
                    {
                        Require(!active, "open while a query is active");
                        var sqlBytes = chain[1].Payload;
                        Require(sqlBytes.Length >= 5, "SQLSTT length");
                        var sql = Encoding.UTF8.GetString(sqlBytes, 5, checked((int)BinaryPrimitives.ReadUInt32BigEndian(sqlBytes.AsSpan(1))));
                        if (sql == PoolProbeSql)
                        {
                            await PoolProbeAsync(stream);
                            continue;
                        }
                        Require(sql == _sql, "fixture SQL");
                        Require(DdmCodec.TryGetParameter(chain[2].Payload, (ushort)CodePoint.QRYBLKSZ, out var block) && block.Length == 4, "QRYBLKSZ");
                        size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(block));
                        Require(_blocks.ContainsKey(size), "unsupported QRYBLKSZ");
                        active = true; next = 0;
                        Interlocked.Increment(ref _activeQueries);
                        Interlocked.Increment(ref _queries);
                        await SendAsync(stream, Packet(CodePoint.SQLDARD, _metadata, true));
                        await SendAsync(stream, Packet(CodePoint.QRYDSC, _descriptor, true));
                        await SendAsync(stream, Packet(CodePoint.OPNQRYRM, DdmCodec.PackUInt64(CodePoint.QRYINSID, 1), true));
                        await FetchAsync();
                    }
                    else if (codes.SequenceEqual(new[] { CodePoint.CNTQRY }))
                    {
                        Require(active, "fetch without active query");
                        Require(DdmCodec.TryGetParameter(chain[0].Payload, (ushort)CodePoint.QRYINSID, out var id)
                            && id.Length == 8 && BinaryPrimitives.ReadUInt64BigEndian(id) == 1, "query ID");
                        Interlocked.Increment(ref _fetches);
                        if (_options.BeforeContinue is { } beforeContinue) await beforeContinue(_stop.Token);
                        await FetchAsync();
                    }
                    else if (codes.Length == 1 && codes[0] is CodePoint.RDBCMM or CodePoint.RDBRLLBCK)
                    {
                        Require(!active, "transaction completion with active query");
                        if (codes[0] == CodePoint.RDBCMM) Interlocked.Increment(ref _commits);
                        await SendAsync(stream, Packet(CodePoint.SQLCARD, SqlCard()));
                    }
                    else throw new InvalidDataException("Unexpected replay request code points: " + string.Join(",", codes));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error) when (_stop.IsCancellationRequested && error is IOException or ObjectDisposedException or SocketException) { }
            catch (Exception error) { _failures.Enqueue(error); }
            finally { _sockets.TryRemove(client, out _); }

            async Task FetchAsync()
            {
                if (next > 0 && _options.NoProgressOnce && !noProgressSent)
                {
                    noProgressSent = true;
                    await SendAsync(stream, Packet(CodePoint.SQLCARD, SqlCard()));
                    return;
                }
                var block = _blocks[size][next++];
                await SendAsync(stream, block);
                Interlocked.Increment(ref _qrydta);
                if ((BinaryPrimitives.ReadUInt16BigEndian(block) & 0x8000) != 0)
                    Interlocked.Increment(ref _continued);
                if (next == _blocks[size].Length)
                {
                    active = false;
                    Interlocked.Decrement(ref _activeQueries);
                    if (_options.LateSqlCode != 0)
                    {
                        await SendAsync(stream, Packet(CodePoint.SQLCARD, SqlCard(_options.LateSqlCode, _options.LateSqlState), _options.LateSqlCode > 0));
                        if (_options.LateSqlCode > 0) await SendAsync(stream, Packet(CodePoint.ENDQRYRM, []));
                    }
                    else if (_options.EndWithSqlCode100)
                        await SendAsync(stream, Packet(CodePoint.SQLCARD, SqlCard(100, "02000")));
                    else await SendAsync(stream, Packet(CodePoint.ENDQRYRM, []));
                }
                else await SendAsync(stream, Packet(CodePoint.SQLCARD, SqlCard()));
            }
        }
    }

    private static void Require(bool condition, string context)
    {
        if (!condition) throw new InvalidDataException("Replay state violation: " + context);
    }

    private async Task PoolProbeAsync(NetworkStream stream)
    {
        var descriptor = new byte[24]; descriptor[0] = 24; descriptor[1] = 0x76; descriptor[2] = 0xd0;
        using var row = new MemoryStream(); row.Write([0xff, 0]);
        var values = new[] { "test", "REPLAY", "CS", "", Environment.MachineName, "", "" };
        for (var c = 0; c < values.Length; c++)
        {
            descriptor[3 + 3 * c] = 0x3e; descriptor[5 + 3 * c] = 0xff;
            var value = Encoding.UTF8.GetBytes(values[c]);
            row.WriteByte((byte)(value.Length >> 8)); row.WriteByte((byte)value.Length); row.Write(value);
        }
        await SendAsync(stream, Packet(CodePoint.QRYDSC, descriptor, true));
        await SendAsync(stream, Packet(CodePoint.OPNQRYRM, DdmCodec.PackUInt64(CodePoint.QRYINSID, 1), true));
        await SendAsync(stream, Packet(CodePoint.QRYDTA, row.ToArray(), true));
        await SendAsync(stream, Packet(CodePoint.ENDQRYRM, []));
    }

    private static async Task<List<DssPacket>?> ReadChainAsync(NetworkStream stream, CancellationToken token)
    {
        // Peek permits normal EOF only between request chains, never inside one.
        var first = new byte[1];
        var count = await stream.ReadAsync(first, token);
        if (count == 0) return null;
        using var prefix = new PrefixStream(first[0], stream);
        var packets = new List<DssPacket>();
        do
        {
            if (packets.Count == 16) throw new InvalidDataException("Replay request chain is too large.");
            packets.Add(await DssCodec.ReadAsync(prefix, token));
        } while (packets[^1].Header.Chained);
        return packets;
    }

    private async Task SendAsync(NetworkStream stream, byte[] bytes)
    {
        for (var offset = 0; offset < bytes.Length;)
        {
            var count = Math.Min(_options.FragmentSize, bytes.Length - offset);
            await stream.WriteAsync(bytes.AsMemory(offset, count), _stop.Token);
            offset += count;
        }
        Interlocked.Add(ref _bytes, bytes.Length);
        var position = 0;
        do
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position));
            position += length & 0x7fff;
            Interlocked.Increment(ref _packets);
        } while (position < bytes.Length);
    }

    internal static byte[] Packet(CodePoint code, ReadOnlySpan<byte> payload, bool chained = false) =>
        DssCodec.EncodeReply(DdmCodec.PackObject(code, payload), chained, 1);
    internal static byte[] SqlCard(int code = 0, string state = "00000")
    {
        var bytes = new byte[54];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), code);
        Encoding.ASCII.GetBytes(state).CopyTo(bytes, 5);
        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        foreach (var client in _sockets.Keys) client.Dispose();
        await _accept.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(_clients.Keys).WaitAsync(TimeSpan.FromSeconds(5));
        _stop.Dispose();
        ThrowIfFailed();
    }

    private sealed class PrefixStream(byte first, Stream inner) : Stream
    {
        private bool _first = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_first && buffer.Length > 0) { _first = false; buffer.Span[0] = first; return ValueTask.FromResult(1); }
            return inner.ReadAsync(buffer, token);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
