using System.Buffers;
using System.Buffers.Binary;

namespace JustyBase.Db2.Protocol;

/// <summary>Serialized session reader. Packet adapters own their payload; buffer slices do not escape.</summary>
internal sealed class DssReadBuffer : IDisposable
{
    // Bounded transport state, matching the TS/Rust guards with superset values so
    // any chain those ports accept is accepted here too (TS 2048/512 MiB, Rust 4096/256 MiB).
    internal const int MaxReplyChainPackets = 4096;
    internal const long MaxReplyChainBytes = 512L * 1024 * 1024;

    private readonly Stream _stream;
    private readonly object _lifetimeGate = new();
    private byte[]? _buffer;
    private int _position;
    private int _filled;
    private bool _active;
    private bool _disposed;
    private bool _retaining;
    private int _generation;
    private OwnedPacketStream? _ownedStream;
    private DssBufferedPacket[] _packets = new DssBufferedPacket[16];

    internal DssReadBuffer(Stream stream) => _stream = stream;

    internal async ValueTask<DssPacket> ReadAsync(CancellationToken token = default)
    {
        var generation = BeginRead(retaining: false);
        try
        {
            await EnsureAsync(6, token).ConfigureAwait(false);
            // The compatibility path needs owned payloads anyway. Reuse the
            // existing segmented codec for continued packets instead of growing,
            // compacting and clearing a second multi-megabyte session buffer.
            // The projection consumes existing read-ahead before touching TCP;
            // it cannot escape this read lease or bypass the session gate.
            if ((BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(_position)) & 0x8000) != 0)
                return await DssCodec.ReadAsync(_ownedStream ??= new OwnedPacketStream(this), token).ConfigureAwait(false);
            return (await ReadPacketCoreAsync(token).ConfigureAwait(false)).ToOwned(this);
        }
        finally { Release(generation); }
    }

    internal async ValueTask<DssReplyChain> ReadChainAsync(CancellationToken token = default)
    {
        var generation = BeginRead(retaining: true);
        try
        {
            var count = 0;
            long bytes = 0;
            DssBufferedPacket packet;
            do
            {
                packet = await ReadPacketCoreAsync(token).ConfigureAwait(false);
                if (count >= MaxReplyChainPackets || bytes + packet.Length > MaxReplyChainBytes)
                    throw new InvalidDataException($"DB2 reply chain exceeds configured limits ({MaxReplyChainPackets} packets, {MaxReplyChainBytes} bytes).");
                bytes += packet.Length;
                if (count == _packets.Length) Array.Resize(ref _packets, checked(count * 2));
                _packets[count++] = packet;
            } while (packet.Chained);
            return new DssReplyChain(this, generation, count);
        }
        catch { Release(generation); throw; }
    }

    internal async ValueTask<IReadOnlyList<DssPacket>> ReadStreamedChainAsync(
        int maxPayloadBytes,
        CancellationToken token = default)
    {
        if (maxPayloadBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
        var generation = BeginRead(retaining: false);
        try
        {
            var packets = new List<DssPacket>();
            var totalBytes = 0L;
            bool chained;
            do
            {
                if (packets.Count >= MaxReplyChainPackets)
                    throw new InvalidDataException($"DB2 reply chain exceeds configured packet limit ({MaxReplyChainPackets}).");
                var streamed = await Db2DssStream.OpenAsync(
                    _ownedStream ??= new OwnedPacketStream(this),
                    maxPayloadBytes,
                    token).ConfigureAwait(false);
                using var payload = new MemoryStream(capacity: Math.Min(maxPayloadBytes, 64 * 1024));
                while (await streamed.NextChunkAsync(token).ConfigureAwait(false) is { } chunk)
                {
                    if (chunk.Length > maxPayloadBytes - payload.Length)
                        throw new Db2ResourceLimitException("DB2 streamed DSS payload exceeds the receive-buffer limit.");
                    await payload.WriteAsync(chunk, token).ConfigureAwait(false);
                }
                totalBytes = checked(totalBytes + payload.Length + 6);
                if (totalBytes > maxPayloadBytes)
                    throw new Db2ResourceLimitException("DB2 streamed DSS reply chain exceeds the receive-buffer limit.");
                packets.Add(new DssPacket(streamed.Header, streamed.CodePoint, payload.ToArray()));
                chained = streamed.Header.Chained;
            } while (chained);
            if (packets.Any(packet => packet.CodePoint == (ushort)CodePoint.DTAMCHRM))
                throw new InvalidDataException("DB2 server returned DTAMCHRM (SQLDTA descriptor and parameter data do not match).");
            return packets;
        }
        finally { Release(generation); }
    }

    private sealed class OwnedPacketStream(DssReadBuffer owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var available = Math.Min(destination.Length, owner._filled - owner._position);
            if (available == 0) return owner._stream.ReadAsync(destination, token);
#if DB2_PERF_COUNTERS
            Interlocked.Add(ref Db2PerformanceCounters.TransportCopyBytes, available);
#endif
            owner._buffer.AsMemory(owner._position, available).CopyTo(destination);
            owner._position += available;
            return new ValueTask<int>(available);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private int BeginRead(bool retaining)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active) throw new InvalidOperationException("Concurrent reads on a serialized DSS session.");
            _buffer ??= ArrayPool<byte>.Shared.Rent(65536);
            if (retaining && _position > 0)
            {
#if DB2_PERF_COUNTERS
                Interlocked.Add(ref Db2PerformanceCounters.TransportCopyBytes, _filled - _position);
#endif
                _buffer.AsSpan(_position, _filled - _position).CopyTo(_buffer);
                _filled -= _position; _position = 0;
            }
            _active = true; _retaining = retaining;
            return ++_generation;
        }
    }

    internal DssReplyView GetReply(int generation, int ordinal)
    {
        if (!_active || _generation != generation || _disposed) throw new ObjectDisposedException(nameof(DssReplyChain));
        var packet = _packets[ordinal];
        return new DssReplyView(packet, GetPayload(packet));
    }

    internal ReadOnlyMemory<byte> PayloadForOwned(DssBufferedPacket packet) => GetPayload(packet);

    private ReadOnlyMemory<byte> GetPayload(DssBufferedPacket packet) => _buffer.AsMemory(packet.Offset, packet.Length);

    internal void Release(int generation)
    {
        lock (_lifetimeGate)
        {
            if (_generation != generation || !_active) return; // A stale copied lease cannot release a newer chain.
            _active = _retaining = false;
            if (_disposed) ReturnBuffer();
            else TrimOversizedBuffer();
        }
    }

    private void TrimOversizedBuffer()
    {
        // A multi-megabyte LOB packet must not permanently monopolize the array
        // needed by the materializing compatibility decoder. Preserve read-ahead.
        var remaining = _filled - _position;
        if (_buffer is null || _buffer.Length <= 131072 || remaining > 65536) return;
        var smaller = ArrayPool<byte>.Shared.Rent(65536);
#if DB2_PERF_COUNTERS
        Interlocked.Add(ref Db2PerformanceCounters.TransportCopyBytes, remaining);
#endif
        _buffer.AsSpan(_position, remaining).CopyTo(smaller);
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = smaller; _position = 0; _filled = remaining;
    }

    private async ValueTask<DssBufferedPacket> ReadPacketCoreAsync(CancellationToken token)
    {
        await EnsureAsync(6, token).ConfigureAwait(false);
        var raw = _buffer.AsSpan(_position, 6);
        if (raw[2] != DssCodec.Magic)
            throw new InvalidDataException($"Invalid DSS packet: missing 0xD0 magic identifier (got 0x{raw[2]:X2}).");
        var length = BinaryPrimitives.ReadUInt16BigEndian(raw);
        var flags = raw[3];
        var correlationId = BinaryPrimitives.ReadUInt16BigEndian(raw[4..]);
        var firstLength = length & 0x7fff;
        if (firstLength < 6) throw new InvalidDataException($"Invalid DSS frame length {firstLength}: less than header size 6.");
        _position += 6;
        var bodyLength = firstLength - 6;
        await EnsureAsync(bodyLength, token).ConfigureAwait(false);
        var continued = (length & 0x8000) != 0;
#if DB2_PERF_COUNTERS
        Interlocked.Increment(ref Db2PerformanceCounters.DssFrames);
#endif
        while (continued)
        {
#if DB2_PERF_COUNTERS
            Interlocked.Increment(ref Db2PerformanceCounters.DssFrames);
            Interlocked.Increment(ref Db2PerformanceCounters.Continuations);
#endif
            await EnsureAsync(CheckedLength(bodyLength, 2), token).ConfigureAwait(false);
            var next = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(_position + bodyLength));
            continued = (next & 0x8000) != 0;
            var segment = next & 0x7fff;
            if (segment < 2) throw new InvalidDataException($"Invalid DSS continuation length {segment}.");
            await EnsureAsync(CheckedLength(bodyLength, segment), token).ConfigureAwait(false);
            // Compact the two-byte continuation header in place. CopyTo supports
            // overlap; all later buffered packets move with the segment payload.
#if DB2_PERF_COUNTERS
            Interlocked.Add(ref Db2PerformanceCounters.TransportCopyBytes, _filled - _position - bodyLength - 2);
#endif
            _buffer.AsSpan(_position + bodyLength + 2, _filled - _position - bodyLength - 2)
                .CopyTo(_buffer.AsSpan(_position + bodyLength));
            _filled -= 2;
            bodyLength = CheckedLength(bodyLength, segment - 2);
        }
        var slice = DssCodec.ReadDdmSlice(_buffer.AsSpan(_position, bodyLength));
#if DB2_PERF_COUNTERS
        if (slice.CodePoint == (ushort)CodePoint.QRYDTA) Interlocked.Increment(ref Db2PerformanceCounters.QueryDataPackets);
#endif
        var packet = new DssBufferedPacket(length, flags, correlationId, slice.CodePoint, _position + slice.Offset, slice.Length);
        _position += bodyLength;
        return packet;
    }

    private ValueTask EnsureAsync(int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return _filled - _position >= count ? ValueTask.CompletedTask : EnsureCoreAsync(count, token);
    }

    private async ValueTask EnsureCoreAsync(int count, CancellationToken token)
    {
        if (count > Array.MaxLength) throw new InvalidDataException("DSS continued object exceeds the supported buffer length.");
        var required = _retaining ? CheckedLength(_position, count) : count;
        var source = _retaining ? 0 : _position;
        var remaining = _filled - source;
        if (required > _buffer!.Length)
        {
#if DB2_PERF_COUNTERS
            Interlocked.Add(ref Db2PerformanceCounters.TransportCopyBytes, remaining);
#endif
            var size = (int)Math.Min(Array.MaxLength, Math.Max((long)required, _buffer.Length * 2L));
            var grown = ArrayPool<byte>.Shared.Rent(size);
            _buffer.AsSpan(source, remaining).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = grown;
        }
        else if (source > 0)
        {
#if DB2_PERF_COUNTERS
            Interlocked.Add(ref Db2PerformanceCounters.TransportCopyBytes, remaining);
#endif
            _buffer.AsSpan(source, remaining).CopyTo(_buffer);
        }
        _position -= source;
        _filled = remaining;
        count = required;

        while (_filled < count)
        {
            var read = await _stream.ReadAsync(_buffer.AsMemory(_filled, Math.Min(65536, _buffer.Length - _filled)), token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Unable to read beyond the end of the stream.");
            _filled += read;
        }
    }

    private static int CheckedLength(int current, int addition)
    {
        var length = (long)current + addition;
        if (length > Array.MaxLength) throw new InvalidDataException("DSS continued object exceeds the supported buffer length.");
        return (int)length;
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            _disposed = true;
            if (!_active) ReturnBuffer();
        }
    }

    private void ReturnBuffer()
    {
        if (_buffer is null) return;
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = null;
        _position = _filled = 0;
    }
}

internal readonly record struct DssBufferedPacket(ushort RawLength, byte Flags, ushort CorrelationId, ushort CodePoint, int Offset, int Length)
{
    internal bool Chained => (Flags & DssCodec.ChainedFlag) != 0;
    internal DssHeader ToHeader() => new(RawLength, (byte)(Flags & 15), Chained,
        (Flags & DssCodec.SameIdFlag) != 0, (Flags & DssCodec.ErrorFlag) != 0, CorrelationId);
    internal DssPacket ToOwned(DssReadBuffer owner) => new(ToHeader(), CodePoint, owner.PayloadForOwned(this).ToArray());
}

internal readonly struct DssReplyView(DssBufferedPacket packet, ReadOnlyMemory<byte> payload)
{
    internal DssBufferedPacket Header { get; } = packet;
    internal ushort CodePoint => Header.CodePoint;
    internal ReadOnlyMemory<byte> Payload { get; } = payload;
    internal DssPacket ToOwned() => new(Header.ToHeader(), CodePoint, Payload.ToArray());
}

/// <summary>Lease holds the read buffer until synchronous parsing has finished.</summary>
internal readonly struct DssReplyChain(DssReadBuffer owner, int generation, int count) : IDisposable
{
    internal int Count { get; } = count;
    internal DssReplyView this[int ordinal] => (uint)ordinal < (uint)Count
        ? owner.GetReply(generation, ordinal) : throw new ArgumentOutOfRangeException(nameof(ordinal));
    public void Dispose() => owner.Release(generation);
}
