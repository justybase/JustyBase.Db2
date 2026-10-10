#if DB2_PERF_COUNTERS
namespace JustyBase.Db2.Protocol;

// Compile only with -p:Db2PerformanceCounters=true. Diagnostic timings include
// instrumentation overhead and must not be used as optimization acceptance data.
internal static class Db2PerformanceCounters
{
    internal static long ReadCalls, WriteCalls, ReadBytes, WriteBytes;
    internal static long Fetches, DssFrames, QueryDataPackets, Continuations;
    internal static long TransportCopyBytes, RowBufferCopyBytes, DiscardCopyBytes;

    internal static object Take() => new
    {
        ReadCalls = Interlocked.Exchange(ref ReadCalls, 0),
        WriteCalls = Interlocked.Exchange(ref WriteCalls, 0),
        ReadBytes = Interlocked.Exchange(ref ReadBytes, 0),
        WriteBytes = Interlocked.Exchange(ref WriteBytes, 0),
        Fetches = Interlocked.Exchange(ref Fetches, 0),
        DssFrames = Interlocked.Exchange(ref DssFrames, 0),
        QueryDataPackets = Interlocked.Exchange(ref QueryDataPackets, 0),
        Continuations = Interlocked.Exchange(ref Continuations, 0),
        TransportCopyBytes = Interlocked.Exchange(ref TransportCopyBytes, 0),
        RowBufferCopyBytes = Interlocked.Exchange(ref RowBufferCopyBytes, 0),
        DiscardCopyBytes = Interlocked.Exchange(ref DiscardCopyBytes, 0),
    };

    internal sealed class CountingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Interlocked.Increment(ref ReadCalls);
            var count = await inner.ReadAsync(buffer, token).ConfigureAwait(false);
            Interlocked.Add(ref ReadBytes, count);
            return count;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Interlocked.Increment(ref WriteCalls);
            await inner.WriteAsync(buffer, token).ConfigureAwait(false);
            Interlocked.Add(ref WriteBytes, buffer.Length);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
#endif
