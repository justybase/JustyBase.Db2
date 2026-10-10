using System.Buffers.Binary;

namespace JustyBase.Db2.Protocol;

/// <summary>Incremental DSS/DDM payload. Drain before reading the next packet on this stream.</summary>
internal sealed class Db2DssStream
{
    private readonly Stream _stream;
    private readonly long _limit;
    private int _segmentRemaining;
    private bool _continued;
    private int _segments = 1;
    private ulong? _remaining;
    private long _total;
    private bool _finished;
    public DssHeader Header { get; }
    public ushort CodePoint { get; private set; }

    private Db2DssStream(Stream stream, DssHeader header, int remaining, bool continued, long limit)
        => (_stream, Header, _segmentRemaining, _continued, _limit) = (stream, header, remaining, continued, limit);

    public static async Task<Db2DssStream> OpenAsync(Stream stream, long maxPayloadBytes = 1024L * 1024 * 1024, CancellationToken token = default)
    {
        if (maxPayloadBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
        var raw = new byte[6]; await stream.ReadExactlyAsync(raw, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16BigEndian(raw);
        if (raw[2] != 0xd0 || (length & 0x7fff) < 6) throw new InvalidDataException("Invalid DSS header.");
        var flags = raw[3];
        var packet = new Db2DssStream(stream, new DssHeader(length, (byte)(flags & 15), (flags & 64) != 0,
            (flags & 16) != 0, (flags & 32) != 0, BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(4))),
            (length & 0x7fff) - 6, (length & 0x8000) != 0, maxPayloadBytes);
        var ddm = await packet.ReadHeaderBytesAsync(4, token).ConfigureAwait(false);
        packet.CodePoint = BinaryPrimitives.ReadUInt16BigEndian(ddm.AsSpan(2));
        var encoded = BinaryPrimitives.ReadUInt16BigEndian(ddm);
        if ((encoded & 0x8000) == 0)
        {
            if (encoded < 4) throw new InvalidDataException("Invalid DDM length.");
            packet._remaining = (ulong)(encoded - 4);
        }
        else
        {
            var width = (encoded & 0x7fff) - 4;
            if (width < 0 || width > 8) throw new InvalidDataException("Invalid extended DDM length.");
            if (width > 0)
            {
                ulong size = 0;
                foreach (var value in await packet.ReadHeaderBytesAsync(width, token).ConfigureAwait(false)) size = (size << 8) | value;
                packet._remaining = size;
            }
        }
        if (packet._remaining > (ulong)maxPayloadBytes) throw new Db2ResourceLimitException("DDM payload exceeds streamed limit.");
        return packet;
    }
    private async Task<bool> NextSegmentAsync(CancellationToken token)
    {
        if (_segmentRemaining > 0) return true;
        if (!_continued) return false;
        if (++_segments > 65536) throw new Db2ResourceLimitException("DSS continuation count exceeds limit.");
        var raw = new byte[2]; await _stream.ReadExactlyAsync(raw, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16BigEndian(raw);
        if ((length & 0x7fff) <= 2) throw new InvalidDataException("Empty or invalid DSS continuation.");
        _continued = (length & 0x8000) != 0; _segmentRemaining = (length & 0x7fff) - 2;
        return true;
    }
    private async Task<byte[]> ReadHeaderBytesAsync(int count, CancellationToken token)
    {
        var bytes = new byte[count]; var offset = 0;
        while (offset < count)
        {
            if (!await NextSegmentAsync(token).ConfigureAwait(false)) throw new InvalidDataException("Truncated DDM header.");
            var take = Math.Min(count - offset, _segmentRemaining);
            await _stream.ReadExactlyAsync(bytes.AsMemory(offset, take), token).ConfigureAwait(false);
            _segmentRemaining -= take; offset += take;
        }
        return bytes;
    }
    public async Task<byte[]?> NextChunkAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (_finished) return null;
        if (_remaining == 0)
        {
            if (await NextSegmentAsync(token).ConfigureAwait(false)) throw new InvalidDataException("Trailing DSS bytes after DDM payload.");
            _finished = true; return null;
        }
        if (!await NextSegmentAsync(token).ConfigureAwait(false))
        {
            if (_remaining is > 0) throw new InvalidDataException("Truncated DDM payload.");
            _finished = true; return null;
        }
        var count = (int)Math.Min((ulong)Math.Min(_segmentRemaining, 64 * 1024), _remaining ?? ulong.MaxValue);
        if (count > _limit - _total) throw new Db2ResourceLimitException("DSS payload exceeds streamed limit.");
        var chunk = new byte[count]; await _stream.ReadExactlyAsync(chunk, token).ConfigureAwait(false);
        _segmentRemaining -= count; _total += count;
        if (_remaining.HasValue) _remaining -= (ulong)count;
        return chunk;
    }
}
