using System.Buffers;
using System.Buffers.Binary;

namespace JustyBase.Db2.Protocol;

internal sealed record DssHeader(ushort Length, byte Type, bool Chained, bool SameId, bool HasError, ushort CorrelationId);
internal sealed record DssPacket(DssHeader Header, ushort CodePoint, byte[] Payload, bool MoreData = false);
internal sealed record DssWriteResult(byte[] Bytes, ushort NextCorrelationId);

internal static class DssCodec
{
    public const byte Magic = 0xD0;
    public const byte Request = 1;
    public const byte Reply = 2;
    public const byte Object = 3;
    public const byte Comm = 4;
    public const byte ChainedFlag = 0x40;
    public const byte ErrorFlag = 0x20;
    public const byte SameIdFlag = 0x10;
    private const ushort ContinuationFlag = 0x8000;
    private const int MaximumSegment = 0x7fff;

    public static byte[] BuildHeader(int payloadLength, byte type, bool chained, bool sameId, bool hasError, ushort correlationId)
    {
        var result = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(result, checked((ushort)(payloadLength + 6)));
        result[2] = Magic;
        var flags = (byte)(type & 0x0f);
        if (chained) flags |= ChainedFlag;
        if (sameId) flags |= SameIdFlag;
        if (hasError) flags |= ErrorFlag;
        result[3] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4), correlationId);
        return result;
    }

    public static DssWriteResult EncodeRequest(ReadOnlySpan<byte> payload, ushort currentId, bool nextHasSameId, bool lastPacket)
    {
        if (payload.Length < 4) throw new ArgumentException("Payload too short for DDM object.", nameof(payload));
        var bytes = new byte[GetEncodedLength(payload.Length)];
        var next = WriteRequest(payload, bytes, currentId, nextHasSameId, lastPacket);
        return new DssWriteResult(bytes, next);
    }

    internal static int GetEncodedLength(int payloadLength)
    {
        if (payloadLength == 0) return 0;
        var continuations = Math.Max(0L, ((long)payloadLength - (MaximumSegment - 6) + (MaximumSegment - 3)) / (MaximumSegment - 2));
        return checked((int)(payloadLength + 6L + continuations * 2));
    }

    internal static ushort WriteRequest(ReadOnlySpan<byte> payload, Span<byte> destination, ushort currentId, bool nextHasSameId, bool lastPacket)
    {
        if (payload.Length < 4) throw new ArgumentException("Payload too short for DDM object.", nameof(payload));
        var codePoint = BinaryPrimitives.ReadUInt16BigEndian(payload[2..]);
        var type = codePoint is (ushort)CodePoint.SQLSTT or (ushort)CodePoint.SQLATTR or (ushort)CodePoint.SQLDTA or (ushort)CodePoint.EXTDTA or (ushort)CodePoint.SECTKN ? Object : Request;
        WriteSegments(payload, destination, (byte)(type | (!lastPacket ? ChainedFlag : 0) | (nextHasSameId ? SameIdFlag : 0)), currentId);
        return nextHasSameId ? currentId : unchecked((ushort)(currentId + 1));
    }

    private static void WriteSegments(ReadOnlySpan<byte> payload, Span<byte> destination, byte flags, ushort correlationId)
    {
        if (destination.Length < GetEncodedLength(payload.Length)) throw new ArgumentException("DSS destination is too short.", nameof(destination));
        var offset = 0;
        var written = 0;
        var first = true;
        while (offset < payload.Length)
        {
            var headerLength = first ? 6 : 2;
            var count = Math.Min(MaximumSegment - headerLength, payload.Length - offset);
            var more = offset + count < payload.Length;
            BinaryPrimitives.WriteUInt16BigEndian(destination[written..], (ushort)(headerLength + count | (more ? ContinuationFlag : 0)));
            if (first)
            {
                destination[written + 2] = Magic;
                destination[written + 3] = flags;
                BinaryPrimitives.WriteUInt16BigEndian(destination[(written + 4)..], correlationId);
            }
            payload.Slice(offset, count).CopyTo(destination[(written + headerLength)..]);
            offset += count;
            written += headerLength + count;
            first = false;
        }
    }

    /// <summary>Writes an EXTDTA LOB DDM object from a forward-only source without buffering its content.</summary>
    public static Task<ushort> WriteExternalBlobAsync(Stream destination, Stream source, long dataLength, ushort currentId, bool nextHasSameId, bool lastPacket, CancellationToken cancellationToken = default) =>
        WriteExternalLobAsync(destination, source, dataLength, currentId, nextHasSameId, lastPacket, cancellationToken);

    /// <summary>Writes an EXTDTA LOB DDM object from a forward-only source without buffering its content.</summary>
    public static Task<ushort> WriteExternalLobAsync(Stream destination, Stream source, long dataLength, ushort currentId, bool nextHasSameId, bool lastPacket, CancellationToken cancellationToken = default) =>
        WriteExternalLobAsync(destination, source, dataLength, currentId, nextHasSameId, lastPacket, cancellationToken, cancellationToken);

    internal static async Task<ushort> WriteExternalLobAsync(Stream destination, Stream source, long dataLength, ushort currentId, bool nextHasSameId, bool lastPacket, CancellationToken cancellationToken, CancellationToken sourceReadCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);
        if (dataLength < 0) throw new ArgumentOutOfRangeException(nameof(dataLength));
        if (!destination.CanWrite) throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        if (!source.CanRead) throw new ArgumentException("The LOB source stream must be readable.", nameof(source));

        var bodyLength = checked((ulong)dataLength + 1);
        byte[] ddmHeader;
        if (bodyLength + 4 <= 0x7fff)
        {
            ddmHeader = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(ddmHeader, checked((ushort)(bodyLength + 4)));
            BinaryPrimitives.WriteUInt16BigEndian(ddmHeader.AsSpan(2), (ushort)CodePoint.EXTDTA);
        }
        else if (bodyLength <= int.MaxValue)
        {
            ddmHeader = new byte[8];
            BinaryPrimitives.WriteUInt16BigEndian(ddmHeader, 0x8008);
            BinaryPrimitives.WriteUInt16BigEndian(ddmHeader.AsSpan(2), (ushort)CodePoint.EXTDTA);
            BinaryPrimitives.WriteUInt32BigEndian(ddmHeader.AsSpan(4), (uint)bodyLength);
        }
        else
        {
            ddmHeader = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(ddmHeader, 0x800c);
            BinaryPrimitives.WriteUInt16BigEndian(ddmHeader.AsSpan(2), (ushort)CodePoint.EXTDTA);
            BinaryPrimitives.WriteUInt64BigEndian(ddmHeader.AsSpan(4), bodyLength);
        }
        var prefix = new byte[ddmHeader.Length + 1];
        ddmHeader.CopyTo(prefix, 0); // EXTDTA status byte is zero.
        ulong remaining = (ulong)prefix.Length + (ulong)dataLength;
        var prefixOffset = 0;
        var first = true;
        while (remaining > 0)
        {
            var dssHeaderLength = first ? 6 : 2;
            var count = (int)Math.Min((ulong)(MaximumSegment - dssHeaderLength), remaining);
            var more = (ulong)count < remaining;
            var frame = new byte[dssHeaderLength + count];
            BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)(frame.Length | (more ? ContinuationFlag : 0)));
            if (first)
            {
                frame[2] = Magic;
                frame[3] = (byte)(Object | (!lastPacket ? ChainedFlag : 0) | (nextHasSameId ? SameIdFlag : 0));
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), currentId);
            }

            var written = 0;
            if (prefixOffset < prefix.Length)
            {
                var prefixCount = Math.Min(count, prefix.Length - prefixOffset);
                prefix.AsSpan(prefixOffset, prefixCount).CopyTo(frame.AsSpan(dssHeaderLength));
                prefixOffset += prefixCount;
                written += prefixCount;
            }
            if (written < count)
                await source.ReadExactlyAsync(frame.AsMemory(dssHeaderLength + written, count - written), sourceReadCancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            remaining -= (ulong)count;
            first = false;
        }
        return nextHasSameId ? currentId : unchecked((ushort)(currentId + 1));
    }

    public static (ushort CodePoint, byte[] Payload) ReadDdmObject(ReadOnlyMemory<byte> body)
        => ReadDdmObject(body.Span);

    private static (ushort CodePoint, byte[] Payload) ReadDdmObject(ReadOnlySpan<byte> data)
    {
        var slice = ReadDdmSlice(data);
        return (slice.CodePoint, data.Slice(slice.Offset, slice.Length).ToArray());
    }

    internal static (ushort CodePoint, int Offset, int Length) ReadDdmSlice(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) throw new InvalidDataException($"Invalid DDM object length: DSS body holds {data.Length} bytes.");
        var encodedLength = BinaryPrimitives.ReadUInt16BigEndian(data);
        var codePoint = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if ((encodedLength & 0x8000) == 0)
        {
            if (encodedLength < 4) throw new InvalidDataException($"Invalid DDM object length: {encodedLength}.");
            if (encodedLength > data.Length) throw new InvalidDataException($"DDM object length {encodedLength} exceeds DSS frame payload capacity {data.Length}.");
            return (codePoint, 4, encodedLength - 4);
        }
        var extensionLength = (encodedLength & 0x7fff) - 4;
        if (extensionLength == 0) return (codePoint, 4, data.Length - 4);
        if (extensionLength < 0 || extensionLength > 8 || 4 + extensionLength > data.Length)
            throw new InvalidDataException($"Invalid DDM extended length field 0x{encodedLength:X4}.");
        ulong contentLength = 0;
        for (var i = 0; i < extensionLength; i++) contentLength = (contentLength << 8) | data[4 + i];
        var available = data.Length - 4 - extensionLength;
        if (contentLength > (ulong)available) throw new InvalidDataException($"DDM extended length {contentLength} exceeds DSS payload {available}.");
        return (codePoint, 4 + extensionLength, checked((int)contentLength));
    }

    public static async ValueTask<DssPacket> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
#if DB2_PERF_COUNTERS
        Interlocked.Increment(ref Db2PerformanceCounters.DssFrames);
#endif
        var rawHeader = new byte[6];
        await stream.ReadExactlyAsync(rawHeader, cancellationToken).ConfigureAwait(false);
        if (rawHeader[2] != Magic) throw new InvalidDataException($"Invalid DSS packet: missing 0xD0 magic identifier (got 0x{rawHeader[2]:X2}).");
        var length = BinaryPrimitives.ReadUInt16BigEndian(rawHeader);
        var flags = rawHeader[3];
        var header = new DssHeader(length, (byte)(flags & 0x0f), (flags & ChainedFlag) != 0, (flags & SameIdFlag) != 0, (flags & ErrorFlag) != 0, BinaryPrimitives.ReadUInt16BigEndian(rawHeader.AsSpan(4)));
        var firstLength = length & ~ContinuationFlag;
        if (firstLength < 6) throw new InvalidDataException($"Invalid DSS frame length {firstLength}: less than header size 6.");
        var bodyLength = firstLength - 6;
        var buffer = ArrayPool<byte>.Shared.Rent(bodyLength);
        try
        {
            await stream.ReadExactlyAsync(buffer.AsMemory(0, bodyLength), cancellationToken).ConfigureAwait(false);
            var continued = (length & ContinuationFlag) != 0;
            while (continued)
            {
#if DB2_PERF_COUNTERS
                Interlocked.Increment(ref Db2PerformanceCounters.DssFrames);
                Interlocked.Increment(ref Db2PerformanceCounters.Continuations);
#endif
                var continuationHeader = new byte[2];
                await stream.ReadExactlyAsync(continuationHeader, cancellationToken).ConfigureAwait(false);
                var continuationLength = BinaryPrimitives.ReadUInt16BigEndian(continuationHeader);
                continued = (continuationLength & ContinuationFlag) != 0;
                var count = continuationLength & ~ContinuationFlag;
                if (count < 2) throw new InvalidDataException($"Invalid DSS continuation length {count}.");
                var segmentLength = count - 2;
                int needed;
                try { needed = checked(bodyLength + segmentLength); }
                catch (OverflowException error) { throw new InvalidDataException("DSS continued object exceeds the supported buffer length.", error); }
                if (needed > Array.MaxLength)
                    throw new InvalidDataException("DSS continued object exceeds the supported buffer length.");
                if (needed > buffer.Length)
                {
                    var grown = ArrayPool<byte>.Shared.Rent(needed);
#if DB2_PERF_COUNTERS
                    Interlocked.Add(ref Db2PerformanceCounters.TransportCopyBytes, bodyLength);
#endif
                    buffer.AsSpan(0, bodyLength).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }
                await stream.ReadExactlyAsync(buffer.AsMemory(bodyLength, segmentLength), cancellationToken).ConfigureAwait(false);
                bodyLength = needed;
            }
            var (codePoint, payload) = ReadDdmObject(buffer.AsSpan(0, bodyLength));
#if DB2_PERF_COUNTERS
            if (codePoint == (ushort)CodePoint.QRYDTA) Interlocked.Increment(ref Db2PerformanceCounters.QueryDataPackets);
#endif
            return new DssPacket(header, codePoint, payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static byte[] EncodeReply(ReadOnlySpan<byte> ddmObject, bool chained, ushort correlationId)
    {
        var bytes = new byte[GetEncodedLength(ddmObject.Length)];
        WriteSegments(ddmObject, bytes, (byte)(Reply | (chained ? ChainedFlag : 0)), correlationId);
        return bytes;
    }
}
