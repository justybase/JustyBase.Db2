using System.Buffers.Binary;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using JustyBase.Db2;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;
using Xunit;

namespace JustyBase.Db2.Tests;

/// <summary>Stateful protocol replay independent of a listening socket or a Db2 installation.</summary>
public sealed class PreparedStreamReplayTests
{
    [Theory]
    [InlineData(true, 6)]
    [InlineData(false, 3)]
    public async Task AlternatingPreparedStreamsRestoreTheSqlAndResultShapeOfSection65(bool alternate, int expectedPrepares)
    {
        using var transport = new PreparedSectionStream();
        // Replay never uses the client transport: EnsureOpen only tests Client != null.
        // Uninitialized shells avoid opening an OS socket in restricted test runners.
        // Clear the shell before disposing the session; no native socket is owned.
        using var client = (TcpClient)RuntimeHelpers.GetUninitializedObject(typeof(TcpClient));
        client.Client = (Socket)RuntimeHelpers.GetUninitializedObject(typeof(Socket));
        var session = new Db2Session(client, transport, new Db2Config { Database = "TESTDB", User = "test" });
        try
        {
            var first = await session.PrepareAsync("VALUES CAST(? AS INTEGER)");
            var second = await session.PrepareAsync("VALUES (CAST(? AS INTEGER), 85)");
            var statements = alternate ? new[] { first, second, first, second } : new[] { first, first, first, first };
            foreach (var statement in statements)
            {
                var rows = new List<Db2StreamRow>();
                await foreach (var row in session.QueryStreamAsync(statement, new object?[] { 42 })) rows.Add(row);
                var values = Assert.Single(rows).Values;
                Assert.Equal(statement == first ? 1 : 2, values.Length);
                Assert.Equal(statement == first ? 42 : 84, values[0]);
                if (statement == second) Assert.Equal(85, values[1]);
            }
            Assert.Equal(expectedPrepares, transport.PrepareCount);
            Assert.Equal(4, transport.OpenCount);
            Assert.Equal(4, transport.CommitCount);
        }
        finally
        {
            // Setting Client=null invokes TcpClient's socket initialization path.
            // Clear the backing slot directly instead; the replay shell has no handle.
            typeof(TcpClient).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.FieldType == typeof(Socket)).SetValue(client, null);
            await session.DisposeAsync();
        }
    }

    private sealed class PreparedSectionStream : Stream
    {
        private readonly Queue<MemoryStream> _replies = new();
        private readonly List<DssPacket> _requestChain = new();
        private string _activeSql = "";
        public int PrepareCount { get; private set; }
        public int OpenCount { get; private set; }
        public int CommitCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_replies.Count == 0) throw new InvalidOperationException("Driver read before completing its request chain.");
            var reply = _replies.Peek();
            var count = reply.Read(buffer[..Math.Min(buffer.Length, 5)]);
            if (reply.Position == reply.Length) _replies.Dequeue().Dispose();
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new MemoryStream(buffer.ToArray());
            while (request.Position < request.Length)
            {
                var packet = await DssCodec.ReadAsync(request, cancellationToken);
                _requestChain.Add(packet);
                if (!packet.Header.Chained) CompleteRequest();
            }
        }

        private void CompleteRequest()
        {
            if (_requestChain.Any(p => p.CodePoint == (ushort)CodePoint.PRPSQLSTT))
            {
                var sql = Assert.Single(_requestChain, p => p.CodePoint == (ushort)CodePoint.SQLSTT).Payload;
                _activeSql = Encoding.UTF8.GetString(sql, 5, checked((int)BinaryPrimitives.ReadUInt32BigEndian(sql.AsSpan(1))));
                PrepareCount++;
                if (_requestChain.Any(p => p.CodePoint == (ushort)CodePoint.DSCSQLSTT))
                {
                    // Existing legacy SQLDARD layout: one nullable INTEGER parameter.
                    var descriptor = new byte[49];
                    descriptor[0] = 0xff;
                    descriptor[1] = 1;
                    descriptor[2] = 1;
                    BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(8), 4);
                    BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(16), (ushort)SqlType.NInteger);
                    Reply(CodePoint.SQLDARD, descriptor);
                }
            }
            if (_requestChain.Any(p => p.CodePoint == (ushort)CodePoint.OPNQRY))
            {
                OpenCount++;
                var second = _activeSql.Contains(", 85", StringComparison.Ordinal);
                Reply(CodePoint.QRYDSC, second ? [9, 0x76, 0xd0, 0x02, 0, 4, 0x02, 0, 4] : [6, 0x76, 0xd0, 0x02, 0, 4], true);
                Reply(CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, new byte[8]), true);
                var row = new byte[second ? 10 : 6];
                row[0] = 0xff;
                BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(2), second ? 84 : 42);
                if (second) BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(6), 85);
                Reply(CodePoint.QRYDTA, row, true);
                Reply(CodePoint.ENDQRYRM, []);
            }
            if (_requestChain.Any(p => p.CodePoint == (ushort)CodePoint.RDBCMM))
            {
                CommitCount++;
                var card = new byte[54];
                Encoding.ASCII.GetBytes("00000").CopyTo(card, 5);
                Reply(CodePoint.SQLCARD, card);
            }
            _requestChain.Clear();
        }

        private void Reply(CodePoint codePoint, byte[] payload, bool chained = false) =>
            _replies.Enqueue(new MemoryStream(DssCodec.EncodeReply(DdmCodec.PackObject(codePoint, payload), chained, 1)));

        protected override void Dispose(bool disposing)
        {
            if (disposing) while (_replies.TryDequeue(out var reply)) reply.Dispose();
            base.Dispose(disposing);
        }
    }
}
