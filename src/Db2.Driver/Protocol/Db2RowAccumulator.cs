using System.Buffers;
namespace JustyBase.Db2.Protocol;

/// <summary>Owned materializing-cursor buffer; incomplete rows stay inside it.</summary>
internal sealed class Db2RowAccumulator : IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(65536);
    private int _length;
    internal ReadOnlySpan<byte> Data => _buffer.AsSpan(0, _length);
    internal int PendingBytes => _length;
    internal void Write(ReadOnlySpan<byte> data) => Append(data);
    internal void Append(ReadOnlySpan<byte> data)
    {
        var needed = checked(_length + data.Length);
        if (needed > _buffer.Length)
        {
            var grown = ArrayPool<byte>.Shared.Rent((int)Math.Min(Array.MaxLength, Math.Max((long)needed, _buffer.Length * 2L)));
            _buffer.AsSpan(0, _length).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true); _buffer = grown;
        }
        data.CopyTo(_buffer.AsSpan(_length)); _length = needed;
    }
    internal void Consume(int count)
    {
        if ((uint)count > (uint)_length) throw new ArgumentOutOfRangeException(nameof(count));
        _buffer.AsSpan(count, _length - count).CopyTo(_buffer); _length -= count;
    }
    public void Dispose()
    {
        var buffer = _buffer; _buffer = []; _length = 0;
        if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
    }
}
