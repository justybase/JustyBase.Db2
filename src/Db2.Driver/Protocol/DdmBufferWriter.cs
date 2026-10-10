using System.Buffers;
using System.Buffers.Binary;

namespace JustyBase.Db2.Protocol;

/// <summary>Owned pooled writer. Completed nested objects backpatch their DRDA lengths.</summary>
internal sealed class DdmBufferWriter : Stream, IBufferWriter<byte>
{
    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(512);
    private int _written;
    internal int BeginObject(CodePoint codePoint)
    {
        var start = _written;
        var header = GetSpan(4)[..4];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)codePoint);
        Advance(4);
        return start;
    }
    internal void EndObject(int start)
    {
        var bodyLength = _written - start - 4;
        if (bodyLength <= 0x7fff - 4)
            BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(start), (ushort)(bodyLength + 4));
        else
        {
            Ensure(4);
            // Only the current object's body moves; outer header offsets remain valid.
            _buffer.AsSpan(start + 4, bodyLength).CopyTo(_buffer.AsSpan(start + 8));
            _written += 4;
            BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(start), 0x8008);
            BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(start + 4), (uint)bodyLength);
        }
    }
    internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);
    internal byte[] ToArray() => _buffer.AsSpan(0, _written).ToArray();
    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (count < 0 || count > _buffer.Length - _written) throw new ArgumentOutOfRangeException(nameof(count));
        _written += count;
    }
    public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsMemory(_written); }
    public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsSpan(_written); }
    private void Ensure(int count)
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        count = Math.Max(count, 1);
        var required = checked(_written + count);
        if (required <= _buffer.Length) return;
        var next = ArrayPool<byte>.Shared.Rent((int)Math.Min(Array.MaxLength, Math.Max((long)required, _buffer.Length * 2L)));
        _buffer.AsSpan(0, _written).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = next;
    }
    public override void Write(ReadOnlySpan<byte> buffer) { buffer.CopyTo(GetSpan(buffer.Length)); Advance(buffer.Length); }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void WriteByte(byte value) { GetSpan(1)[0] = value; Advance(1); }
    protected override void Dispose(bool disposing)
    {
        var buffer = _buffer; _buffer = null;
        if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        base.Dispose(disposing);
    }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => _buffer is not null;
    public override long Length => _written;
    public override long Position { get => _written; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
