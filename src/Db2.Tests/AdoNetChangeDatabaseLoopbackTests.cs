using System.Buffers.Binary;
using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Converters;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class AdoNetChangeDatabaseLoopbackTests
{
    [Fact]
    public async Task ChangeDatabaseReconnectsWithNewPoolKeyAndKeepsOldPoolSessionIsolated()
    {
        await Db2AdoConnection.ClearAllPoolsAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var originalDatabase = "FIRSTDB";
        var changedDatabase = "NEXTDB";
        var originalDsn = $"Server=127.0.0.1:{port};Database={originalDatabase};UID=user;PWD=password;Pooling=true;MaxPoolSize=1;Block_Size=32768;Client_ApplName=change-database-test";
        var serverTask = ServeChangeDatabaseAsync(listener, originalDatabase, changedDatabase);

        try
        {
            await using (var connection = new Db2AdoConnection(originalDsn))
            {
                await connection.OpenAsync();
                Assert.Equal(ConnectionState.Open, connection.State);
                Assert.Equal(originalDatabase, connection.Database);

                await connection.ChangeDatabaseAsync(changedDatabase);

                Assert.Equal(ConnectionState.Open, connection.State);
                Assert.Equal(changedDatabase, connection.Database);
                Assert.Contains("Pooling=True", connection.ConnectionString, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("MaxPoolSize=1", connection.ConnectionString, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Block_Size=32768", connection.ConnectionString, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Client_ApplName=change-database-test", connection.ConnectionString, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(29, Assert.IsType<int>(await ScalarAsync(connection, "VALUES 29")));
            }

            // The original pool must still contain its original-database session.
            await using (var originalConnection = new Db2AdoConnection(originalDsn))
            {
                await originalConnection.OpenAsync();
                Assert.Equal(originalDatabase, originalConnection.Database);
                Assert.Equal(41, Assert.IsType<int>(await ScalarAsync(originalConnection, "VALUES 41")));
            }

            await Db2AdoConnection.ClearAllPoolsAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            listener.Stop();
            await Db2AdoConnection.ClearAllPoolsAsync();
        }
    }

    private static async Task ServeChangeDatabaseAsync(TcpListener listener, string firstDatabase, string secondDatabase)
    {
        using var firstClient = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await using var firstStream = firstClient.GetStream();
        await AcceptHandshakeAsync(firstStream, firstDatabase);
        await ServeRegisterReadsAsync(firstStream, "MOCKSCHEMA", "CS", applicationName: "change-database-test");
        // ChangeDatabaseAsync returns the first lease to its pool before opening the replacement.
        // No operation was executed on the first lease: its return needs no probe.

        using var secondClient = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await using var secondStream = secondClient.GetStream();
        await AcceptHandshakeAsync(secondStream, secondDatabase);
        await ServeRegisterReadsAsync(secondStream, "MOCKSCHEMA", "CS", applicationName: "change-database-test");

        await ServeScalarQueryAsync(secondStream, secondDatabase, 29);
        await ServeRegisterReadsAsync(secondStream, "MOCKSCHEMA", "CS", applicationName: "change-database-test");

        await ServeScalarQueryAsync(firstStream, firstDatabase, 41);
        await ServeRegisterReadsAsync(firstStream, "MOCKSCHEMA", "CS", applicationName: "change-database-test");

        var buffer = new byte[1];
        Assert.Equal(0, await firstStream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, await secondStream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task AcceptHandshakeAsync(Stream stream, string expectedDatabase)
    {
        var startup = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC }, startup.Select(packet => packet.CodePoint));
        AssertDatabase(startup.Single(packet => packet.CodePoint == (ushort)CodePoint.ACCSEC), expectedDatabase);
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));

        var authentication = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB }, authentication.Select(packet => packet.CodePoint));
        AssertDatabase(authentication.Single(packet => packet.CodePoint == (ushort)CodePoint.ACCRDB), expectedDatabase);
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), chained: true);
        await SendAsync(stream, CodePoint.ACCRDBRM, ReadOnlyMemory<byte>.Empty);

        var setup = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, setup.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        // The configured application register is applied after basic handshake.
        var registers = await ReadChainAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, registers.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
    }

    private static byte[] PoolStateDescriptor()
    {
        var descriptor = new byte[3 + 7 * 3];
        descriptor[0] = checked((byte)descriptor.Length);
        descriptor[1] = 0x76;
        descriptor[2] = 0xd0;
        for (var index = 0; index < 7; index++)
        {
            descriptor[3 + index * 3] = 0x3e;
            descriptor[4 + index * 3] = 0;
            descriptor[5 + index * 3] = 0xff;
        }
        return descriptor;
    }

    private static byte[] PoolStateRow(params string[] values)
    {
        Assert.Equal(7, values.Length);
        using var row = new MemoryStream();
        row.Write([0xff, 0]);
        Span<byte> length = stackalloc byte[2];
        foreach (var value in values)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
            row.Write(length);
            row.Write(bytes);
        }
        return row.ToArray();
    }

    /// <summary>Serves the combined actual-state probe used for pooled baselines and every lease return.</summary>
    private static async Task ServeRegisterReadsAsync(Stream stream, string schema, string isolation,
        string applicationName = "", string workstationName = "", string userId = "", string accounting = "")
    {
        var query = await ReadChainAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY }, query.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.QRYDSC, PoolStateDescriptor(), chained: true);
        var queryId = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(queryId, 1);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, queryId), chained: true);
        await SendAsync(stream, CodePoint.QRYDTA, PoolStateRow("USER", schema, isolation, applicationName,
            string.IsNullOrEmpty(workstationName) ? Environment.MachineName : workstationName, userId, accounting), chained: true);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
    }

    private static async Task ServeScalarQueryAsync(Stream stream, string expectedDatabase, int value)
    {
        var query = await ReadChainAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY }, query.Select(packet => packet.CodePoint));
        AssertDatabase(query[^1], expectedDatabase);
        await SendAsync(stream, CodePoint.SQLDARD, IntegerSqlDard(), chained: true);

        var queryId = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(queryId, 1);
        await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0x02, 0, 4 }, chained: true);
        await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, queryId), chained: true);
        await SendAsync(stream, CodePoint.QRYDTA, new byte[] { 0xff, 0, (byte)value, 0, 0, 0 }, chained: true);
        await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
    }

    private static void AssertDatabase(DssPacket packet, string expectedDatabase)
    {
        var parameters = DdmCodec.ParseParameters(packet.Payload);
        var actual = parameters.TryGetValue((ushort)CodePoint.RDBNAM, out var rdbnam)
            ? Ebcdic.DecodeCp500(rdbnam).TrimEnd()
            : Encoding.UTF8.GetString(parameters[(ushort)CodePoint.PKGNAMCSN].AsSpan(0, 18)).TrimEnd();
        Assert.Equal(expectedDatabase, actual);
    }

    private static byte[] IntegerSqlDard()
    {
        byte[] descriptor = [0xff, 0xff, 1, 0, .. new byte[16]];
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(4), 10);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(8), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(16), (ushort)SqlType.NInteger);
        var output = new byte[60 + descriptor.Length];
        SqlCard().CopyTo(output, 0);
        descriptor.CopyTo(output, 60);
        return output;
    }

    private static async Task<object?> ScalarAsync(Db2AdoConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<List<DssPacket>> ReadChainAsync(Stream stream)
    {
        var result = new List<DssPacket>();
        bool chained;
        do
        {
            var packet = await DssCodec.ReadAsync(stream);
            result.Add(packet);
            chained = packet.Header.Chained;
        } while (chained);
        return result;
    }

    private static async Task SendAsync(Stream stream, CodePoint codePoint, ReadOnlyMemory<byte> payload, bool chained = false)
    {
        var ddm = DdmCodec.PackObject(codePoint, payload.Span);
        await stream.WriteAsync(DssCodec.EncodeReply(ddm, chained, 1));
    }

    private static byte[] SqlCard()
    {
        byte[] result = new byte[54];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1), 0);
        Encoding.ASCII.GetBytes("00000").CopyTo(result, 5);
        return result;
    }
}
