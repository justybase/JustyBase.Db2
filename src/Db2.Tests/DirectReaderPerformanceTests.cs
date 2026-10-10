using System.Data;
using System.Net;
using System.Net.Sockets;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Protocol;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed partial class SessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectReaderReceivesMetadataWithoutAnExtraPrepareEvenForEmptyResults(bool hasRow)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var client = await AcceptInitializedPoolClientAsync(listener);
            var stream = client.GetStream();
            var chain = await ReadChainAsync(stream).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new ushort[] { (ushort)CodePoint.PRPSQLSTT, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.OPNQRY },
                chain.Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SQLDARD, NamedIntegerSqlDard("ANSWER"), true);
            await SendAsync(stream, CodePoint.QRYDSC, new byte[] { 6, 0x76, 0xd0, 0x02, 0, 4 }, true);
            await SendAsync(stream, CodePoint.OPNQRYRM, DdmCodec.PackBytes(CodePoint.QRYINSID, new byte[8]), true);
            if (hasRow) await SendAsync(stream, CodePoint.QRYDTA, new byte[] { 0xff, 0, 42, 0, 0, 0 }, true);
            await SendAsync(stream, CodePoint.ENDQRYRM, ReadOnlyMemory<byte>.Empty);
            await ServeRegisterReadsAsync(stream, "MOCKSCHEMA", "CS");
            Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        });
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using (var connection = new Db2AdoConnection($"Server=127.0.0.1:{port};Database=TESTDB;UID=db2inst1;PWD=password;Pooling=true;MaxPoolSize=1;Block_Size=65535"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 42 AS ANSWER FROM SYSIBM.SYSDUMMY1";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.Equal(1, reader.FieldCount);
            Assert.Equal("ANSWER", reader.GetName(0));
            Assert.Equal(typeof(int), reader.GetFieldType(0));
            Assert.Equal(hasRow, reader.HasRows);
            Assert.Equal(hasRow, await reader.ReadAsync());
            if (hasRow) Assert.Equal(42, reader.GetInt32(0));
            Assert.False(await reader.ReadAsync());
        }
        await Db2AdoConnection.ClearAllPoolsAsync();
        await server;
    }
}
