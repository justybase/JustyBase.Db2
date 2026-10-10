using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JustyBase.Db2;
using JustyBase.Db2.Protocol;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed partial class SessionTests
{
    private static async Task ServeExecuteStatementAsync(Stream stream, string expectedSql, CodePoint codePoint = CodePoint.EXCSQLIMM)
    {
        var chain = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)codePoint, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, chain.Select(packet => packet.CodePoint));
        var statement = Assert.Single(chain, packet => packet.CodePoint == (ushort)CodePoint.SQLSTT);
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(statement.Payload.AsSpan(1, 4)));
        Assert.Equal(expectedSql, Encoding.UTF8.GetString(statement.Payload, 5, length));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
    }

    [Fact]
    public async Task PerCommandUserOptionSwitchesIdentityOnlyWhenItDiffers()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ServeConnectionSetupAsync(stream);
            await ServeExecuteStatementAsync(stream, "SET SESSION AUTHORIZATION = \"BORROWER\"");
            await ServeExecuteStatementAsync(stream, "VALUES 1");
            // The second command targets the same user, so no switch statement is sent.
            await ServeExecuteStatementAsync(stream, "VALUES 1");
        });

        await using var session = await Db2Session.ConnectAsync(TestConfig(port));
        var options = Db2CommandOptions.Default with { User = "BORROWER" };
        await session.ExecuteAsync("VALUES 1", options);
        await session.ExecuteAsync("VALUES 1", options);
        await serverTask;
    }

    [Fact]
    public async Task PerCommandClientInfoOptionAppliesOnlyDifferingRegisters()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ServeConnectionSetupAsync(stream);
            await ServeExecuteStatementAsync(stream, "SET CLIENT APPLNAME 'svc'", CodePoint.EXCSQLSET);
            await ServeExecuteStatementAsync(stream, "VALUES 1");
            // The register is already set, so the second command sends nothing extra.
            await ServeExecuteStatementAsync(stream, "VALUES 1");
        });

        await using var session = await Db2Session.ConnectAsync(TestConfig(port));
        var options = Db2CommandOptions.Default with { ClientInfo = new Db2ClientInfo(ApplicationName: "svc") };
        await session.ExecuteAsync("VALUES 1", options);
        await session.ExecuteAsync("VALUES 1", options);
        await serverTask;
    }

    [Fact]
    public async Task DeniedPerCommandRegisterSurfacesTheServerError()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ServeConnectionSetupAsync(stream);
            var chain = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, chain.Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard(-551, "42501"));
        });

        await using var session = await Db2Session.ConnectAsync(TestConfig(port));
        var options = Db2CommandOptions.Default with { ClientInfo = new Db2ClientInfo(ApplicationName: "svc") };
        var error = await Assert.ThrowsAsync<Db2SqlException>(() => session.ExecuteAsync("VALUES 1", options));
        Assert.Equal(-551, error.SqlCode);
        await serverTask;
    }

    [Fact]
    public async Task PerCommandUserOptionRejectsAnInvalidIdentifierBeforeTheNetwork()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ServeConnectionSetupAsync(stream);
        });

        await using var session = await Db2Session.ConnectAsync(TestConfig(port));
        var options = Db2CommandOptions.Default with { User = "bad\"; DROP TABLE T" };
        await Assert.ThrowsAsync<ArgumentException>(() => session.ExecuteAsync("VALUES 1", options));
        await serverTask;
    }
}
