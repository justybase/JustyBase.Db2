using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using JustyBase.Db2;
using JustyBase.Db2.Converters;
using JustyBase.Db2.Protocol;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed partial class SessionTests
{
    private static async Task ServeConnectionSetupAsync(Stream stream)
    {
        var initial = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC }, initial.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 3));
        var auth = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB }, auth.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
        await SendAsync(stream, CodePoint.ACCRDBRM, DdmCodec.PackBytes(CodePoint.RDBINTTKN, [4, 28, 167, 50]));
        var initialize = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, initialize.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
    }

    private static async Task ServeFailingSessionSetAsync(Stream stream)
    {
        var chain = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLIMM, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, chain.Select(packet => packet.CodePoint));
        await SendAsync(stream, CodePoint.SQLCARD, SqlCard(-551, "42501"));
    }

    private static async Task<(ushort Mechanism, string User, byte[]? Password)> ServeSwitchSecchkAsync(Stream stream)
    {
        var chain = await ReadChainAsync(stream);
        Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK }, chain.Select(packet => packet.CodePoint));
        var fields = DdmCodec.ParseParameters(chain[0].Payload);
        return (
            BinaryPrimitives.ReadUInt16BigEndian(fields[(ushort)CodePoint.SECMEC]),
            Ebcdic.DecodeCp500(fields[(ushort)CodePoint.USRID]),
            fields.TryGetValue((ushort)CodePoint.PASSWORD, out var password) ? password : null);
    }

    [Fact]
    public async Task SwitchUserFallsBackToStandaloneSecchkWithTheSuppliedPassword()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ServeConnectionSetupAsync(stream);
            await ServeFailingSessionSetAsync(stream);
            await ServeFailingSessionSetAsync(stream);
            var (mechanism, user, password) = await ServeSwitchSecchkAsync(stream);
            Assert.Equal((ushort)3, mechanism);
            Assert.Equal("db2test2", user);
            Assert.Equal("secret", Ebcdic.DecodeCp500(password!));
            await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]));
        });

        await using var session = await Db2Session.ConnectAsync(TestConfig(port));
        await session.SwitchUserAsync("db2test2", "secret");
        await serverTask;
    }

    [Fact]
    public async Task SwitchUserWithoutPasswordAttemptsTheFallbackAndSurfacesSecchkcd()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ServeConnectionSetupAsync(stream);
            await ServeFailingSessionSetAsync(stream);
            await ServeFailingSessionSetAsync(stream);
            var (_, _, password) = await ServeSwitchSecchkAsync(stream);
            Assert.Null(password);
            await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [4]));
        });

        await using var session = await Db2Session.ConnectAsync(TestConfig(port));
        var error = await Assert.ThrowsAsync<Db2AuthenticationException>(() => session.SwitchUserAsync("db2test2"));
        Assert.Equal(4, error.SecurityCheckCode);
        await serverTask;
    }

    [Fact]
    public async Task SwitchUserKeepsTheSqlPathWhenSessionAuthorizationSucceeds()
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
            var statement = Assert.Single(chain, packet => packet.CodePoint == (ushort)CodePoint.SQLSTT);
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(statement.Payload.AsSpan(1, 4)));
            Assert.Equal("SET SESSION AUTHORIZATION = \"db2test2\"", Encoding.UTF8.GetString(statement.Payload, 5, length));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
            var buffer = new byte[1];
            try
            {
                var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Equal(0, read);
            }
            catch (TimeoutException)
            {
                // Idle connection: no SECCHK fallback was sent, which is the expected path.
            }
        });

        await using var session = await Db2Session.ConnectAsync(TestConfig(port));
        await session.SwitchUserAsync("db2test2", "secret");
        await serverTask;
    }
}
