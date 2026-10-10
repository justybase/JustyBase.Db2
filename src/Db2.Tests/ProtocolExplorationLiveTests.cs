using System.Reflection;
using System.Text;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Converters;
using Xunit;
using Xunit.Abstractions;
namespace JustyBase.Db2.Tests;

/// <summary>Local LUW experiments. These do not enable speculative production optimizations.</summary>
public sealed class ProtocolExplorationLiveTests(ITestOutputHelper output)
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static string Database(Db2Session session) => (string)typeof(Db2Session).GetField("_database", PrivateInstance)!.GetValue(session)!;
    private static void ResetCorrelation(Db2Session session) => typeof(Db2Session).GetField("_correlationId", PrivateInstance)!.SetValue(session, (ushort)1);
    private static Task Send(Db2Session session, byte[] bytes, bool same, bool last) => (Task)typeof(Db2Session).GetMethod("SendAsync", PrivateInstance)!.Invoke(session, [bytes, same, last, CancellationToken.None, null])!;
    private static Task<IReadOnlyList<DssPacket>> Read(Db2Session session) => ((Task<IReadOnlyList<DssPacket>>)typeof(Db2Session).GetMethod("ReadReplyChainAsync", PrivateInstance)!.Invoke(session, [CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(20));
    private static void Check(IReadOnlyList<DssPacket> replies)
    {
        foreach (var reply in replies)
        {
            if ((CodePoint)reply.CodePoint == CodePoint.SQLCARD)
            {
                var card = DdmCodec.ParseSqlCard(reply.Payload);
                Assert.True(card.SqlCode >= 0, $"SQLCODE={card.SqlCode}, SQLSTATE={card.SqlState}");
            }
            Assert.NotEqual((ushort)CodePoint.SQLERRRM, reply.CodePoint);
        }
    }

    [Db2LiveFact, Trait("Category", "Live")]
    public async Task LuwProbeCountsWritesAndChecksCompleteRowsForWorkingBlockSizes()
    {
        var digits = "(VALUES 0,1,2,3,4,5,6,7,8,9)";
        var sql = $"SELECT d0.d + 10*d1.d + 100*d2.d + 1000*d3.d + 10000*d4.d + 1 FROM {digits} d0(d), {digits} d1(d), {digits} d2(d), {digits} d3(d), {digits} d4(d)";
        foreach (var size in new[] { 32767, 65535 })
        {
            var config = Db2Config.Parse(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
            var builder = new JustyBase.Db2.AdoNet.Db2AdoConnectionStringBuilder { Server = config.Host + ":" + config.Port, Database = config.Database, UserID = config.User, Password = config.Password };
            builder["BlockSize"] = size;
            await using var connection = new JustyBase.Db2.AdoNet.Db2AdoConnection(builder.ConnectionString); await connection.OpenAsync();
            var session = connection.RequireSession(); var field = typeof(Db2Session).GetField("_stream", PrivateInstance)!;
            var counter = new WriteCounter((Stream)field.GetValue(session)!); field.SetValue(session, counter);
            await using var command = connection.CreateCommand(); command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            long sum = 0; var rows = 0;
            while (await reader.ReadAsync()) { rows++; sum += reader.GetInt32(0); }
            Assert.Equal(100000, rows); Assert.Equal(5000050000L, sum);
            output.WriteLine($"Block {size}: complete rows={rows}, checksum={sum}, WriteAsync calls including open and commit={counter.Writes}");
        }
    }

    private sealed class WriteCounter(Stream inner) : Stream
    {
        internal int Writes;
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => inner.CanSeek;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) { Writes++; inner.Write(buffer, offset, count); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) { Writes++; return inner.WriteAsync(buffer, token); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) { Writes++; return inner.WriteAsync(buffer, offset, count, token); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    [Db2LiveFact, Trait("Category", "Live")]
    public async Task LuwProbeReportsLargeBlockResultShapes()
    {
        foreach (var size in new[] { 65535, 131070, 262140 })
        {
            var config = Db2Config.Parse(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!); config.BlockSize = size;
            await using var session = await Db2Session.ConnectAsync(config);
            var result = await session.QueryAsync("VALUES ('a', 'b', 'c', 'd', 'e', 'f', 'g')", new Db2CommandOptions(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));
            output.WriteLine($"Block {size}: columns={result.Columns.Count}, rows={result.Rows.Count}; field counts={string.Join(',', result.Rows.Select(r => r.Length))}");
            if (size == 65535) Assert.Equal(7, Assert.Single(result.Rows).Length);
        }
    }

    [Db2LiveFact, Trait("Category", "Live")]
    public async Task LuwProbeReportsWhichSectionsCanCoexistWith65()
    {
        await using var session = await Db2Session.ConnectAsync(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        var first = await session.PrepareAsync("VALUES CAST(? AS INTEGER)");
        foreach (var section in new ushort[] { 1, 2, 64, 66, 67, 128, 129, 200 })
            await ProbeSection(session, first, section);
    }

    private async Task ProbeSection(Db2Session session, Db2Statement first, ushort section)
    {
        ResetCorrelation(session);
        await Send(session, DdmCodec.PackPrepareSqlStatement("SYSSH200", "SYSLVL01", section, Database(session)), true, false);
        await Send(session, DdmCodec.PackSqlStatement("VALUES CAST(? AS BIGINT) + 100"), false, false);
        await Send(session, DdmCodec.PackDescribeSqlStatement("SYSSH200", "SYSLVL01", section, Database(session)), false, true);
        var described = await Read(session);
        foreach (var reply in described)
            output.WriteLine($"Section {section} reply: {(CodePoint)reply.CodePoint}@{reply.Header.CorrelationId}");
        var rejection = described.FirstOrDefault(r => (CodePoint)r.CodePoint == CodePoint.SQLERRRM);
        if (rejection is not null)
        {
            output.WriteLine(Db2Session.ServerError(rejection.Payload).Message);
            output.WriteLine( $"Section {section} rejected; keeping production section 65.");
            Assert.Equal(7, Assert.Single((await first.QueryAsync([7])).Rows)[0]);
            return;
        }
        Check(described);
        var parameters = DdmCodec.ParseSqlDard(Assert.Single(described, r => (CodePoint)r.CodePoint == CodePoint.SQLDARD && r.Payload[0] == 0xff).Payload);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(i, Assert.Single((await first.QueryAsync([i])).Rows)[0]);
            ResetCorrelation(session);
            await Send(session, DdmCodec.PackOpenQueryWithParams("SYSSH200", "SYSLVL01", section, Database(session), 65535), true, false);
            await Send(session, SqlDtaEncoder.Build(parameters, [i]), false, true);
            var replies = await Read(session); Check(replies);
            var fields = DdmCodec.ParseQueryDescriptor(Assert.Single(replies, r => (CodePoint)r.CodePoint == CodePoint.QRYDSC).Payload);
            var data = replies.Where(r => (CodePoint)r.CodePoint == CodePoint.QRYDTA).SelectMany(r => r.Payload).ToArray();
            Assert.Equal(100L + i, Assert.Single(Db2Session.DecodeRowsIncremental(fields, data).Rows)[0]);
            await session.CommitAsync();
        }
        output.WriteLine($"LUW 11.5: section 65 and {section} coexist in SYSSH200; z/OS and iSeries remain unverified.");
    }

    [Db2LiveFact, Trait("Category", "Live")]
    public async Task ChainedDmlProbeRecordsFailureAndCommitBoundaries()
    {
        var table = "PERF_PIPE_" + Guid.NewGuid().ToString("N")[..12];
        var dsn = Environment.GetEnvironmentVariable("DB2_TEST_DSN")!;
        await using var setup = await Db2Session.ConnectAsync(dsn);
        var schema = await LiveFixtureSchema.ForSessionAsync(setup);
        await setup.ExecuteAsync($"CREATE TABLE {schema}.{table} (ID INTEGER NOT NULL PRIMARY KEY)");
        try
        {
            await using (var session = await Db2Session.ConnectAsync(dsn))
            {
                var statement = await session.PrepareAsync($"INSERT INTO {table} VALUES (?)");
                ResetCorrelation(session);
                for (var i = 0; i < 3; i++)
                {
                    await Send(session, DdmCodec.PackExecuteSqlStatement("SYSSH200", "SYSLVL01", 65, Database(session)), true, false);
                    await Send(session, SqlDtaEncoder.Build(statement.ParameterColumns, [i == 2 ? 2 : 1]), false, false);
                    await Send(session, DdmCodec.PackCommit(), false, i == 2);
                }
                var replies = (await Read(session)).ToList();
                if (replies.Count > 0 && replies.All(r => (CodePoint)r.CodePoint == CodePoint.SQLDARD)) replies.AddRange(await Read(session));
                output.WriteLine("Chained replies: " + string.Join(", ", replies.Select(r => $"{(CodePoint)r.CodePoint}@{r.Header.CorrelationId}")));
                foreach (var reply in replies.Where(r => (CodePoint)r.CodePoint == CodePoint.SQLCARD))
                {
                    var card = DdmCodec.ParseSqlCard(reply.Payload);
                    output.WriteLine($"SQLCARD@{reply.Header.CorrelationId}: SQLCODE={card.SqlCode}, SQLSTATE={card.SqlState}, affected={card.RowsAffected}");
                }
            }
            var values = (await setup.QueryAsync($"SELECT ID FROM {table} ORDER BY ID")).Rows.Select(r => (int)r[0]!).ToArray();
            output.WriteLine("Persisted IDs after a duplicate in the middle: " + string.Join(",", values));
            // This records the server behavior without treating protocol acceptance as
            // proof that public batch cancellation/progress semantics are preserved.
            Assert.Equal(new[] { 1, 2 }, values);
        }
        finally { await setup.ExecuteAsync($"DROP TABLE {schema}.{table}"); }
    }
}
