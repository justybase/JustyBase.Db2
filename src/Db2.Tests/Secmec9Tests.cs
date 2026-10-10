using System.Buffers.Binary;
using System.Numerics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using JustyBase.Db2;
using JustyBase.Db2.Converters;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Security;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed partial class SessionTests
{
    private const string Secmec9ClientPrivateHex = "7f3a91c4d2e5b80716a4c9f0d3b8e2176a5c4d3e2f1908a7b6c5d4e3f2a1b0c9";
    private const string Secmec9ServerPrivateHex = "1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90a";
    private const string Secmec9ClientPublicHex = "7bed3dc4a1117944e7c7e85f820aab5fe0ce78ea0c03f95dfbd4189ed855b88a";
    private const string Secmec9ServerPublicHex = "2da57ee6ce7b212ededc6d07c1cdb28f6ed1ed5cbcade212e30d8ee51d69f3f1";
    private const string Secmec9SharedHex = "303a7d3ca869a26d920662ac3b57d8960aca6c8f6b69f01e8bfc66474e09af53";

    private static BigInteger Secmec9FromHex(string hex) => new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);

    private static List<byte[]> SectknFields(ReadOnlyMemory<byte> payload)
    {
        var tokens = new List<byte[]>();
        var position = 0;
        while (position + 4 <= payload.Length)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(payload.Span.Slice(position, 2));
            if (length < 4 || position + length > payload.Length) break;
            if (BinaryPrimitives.ReadUInt16BigEndian(payload.Span.Slice(position + 2, 2)) == (ushort)CodePoint.SECTKN)
                tokens.Add(payload.Slice(position + 4, length - 4).ToArray());
            position += length;
        }
        return tokens;
    }

    private static byte[] Secmec9Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> ciphertext)
    {
        using var des = DES.Create();
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.PKCS7;
        des.Key = key.ToArray();
        des.IV = iv.ToArray();
        using var decryptor = des.CreateDecryptor();
        return decryptor.TransformFinalBlock(ciphertext.ToArray(), 0, ciphertext.Length);
    }

    [Fact]
    public void Secmec9MatchesIndependentDerbyAndOpensslVectors()
    {
        var clientPrivate = Secmec9FromHex(Secmec9ClientPrivateHex);
        var serverPrivate = Secmec9FromHex(Secmec9ServerPrivateHex);
        var clientPublic = Convert.FromHexString(Secmec9ClientPublicHex);
        var serverPublic = Convert.FromHexString(Secmec9ServerPublicHex);
        var shared = Convert.FromHexString(Secmec9SharedHex);

        Assert.Equal(clientPublic, Secmec9.CalculatePublicKey(clientPrivate));
        Assert.Equal(serverPublic, Secmec9.CalculatePublicKey(serverPrivate));
        Assert.Equal(shared, Secmec9.CalculateSessionKey(serverPublic, clientPrivate));

        var encryptedPassword = Secmec9.Encrypt("SecretPass123"u8, serverPublic, clientPrivate);
        Assert.Equal("a64a5e61af1a35a65d2bc8ec7ae550f4", Convert.ToHexString(encryptedPassword).ToLowerInvariant());
        var encryptedUserid = Secmec9.Encrypt("DB2INST1"u8, serverPublic, clientPrivate);
        Assert.Equal("e3241d6a67a9646ce5fafe9f9bae7acc", Convert.ToHexString(encryptedUserid).ToLowerInvariant());

        var serverKey = Secmec9.CalculateSessionKey(clientPublic, serverPrivate)[12..20];
        var iv = serverPublic[12..20];
        Assert.Equal("SecretPass123", System.Text.Encoding.UTF8.GetString(Secmec9Decrypt(serverKey, iv, encryptedPassword)));
        Assert.Equal("DB2INST1", System.Text.Encoding.UTF8.GetString(Secmec9Decrypt(serverKey, iv, encryptedUserid)));
    }

    [Fact]
    public async Task Secmec9HandshakeExchangesKeysInAccsecAndEncryptsUseridAndPassword()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverPrivate = Secmec9FromHex(Secmec9ServerPrivateHex);
        var serverPublic = Convert.FromHexString(Secmec9ServerPublicHex);
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var opening = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC }, opening.Select(packet => packet.CodePoint));
            var accsec = DdmCodec.ParseParameters(opening[1].Payload);
            Assert.Equal((ushort)9, BinaryPrimitives.ReadUInt16BigEndian(accsec[(ushort)CodePoint.SECMEC]));
            var clientPublic = accsec[(ushort)CodePoint.SECTKN];
            Assert.Equal(32, clientPublic.Length);

            var accsecRd = DdmCodec.PackUInt16(CodePoint.SECMEC, 9).Concat(DdmCodec.PackBytes(CodePoint.SECTKN, serverPublic)).ToArray();
            await SendAsync(stream, CodePoint.ACCSECRD, accsecRd);

            var check = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.ACCRDB }, check.Select(packet => packet.CodePoint));
            var secchk = DdmCodec.ParseParameters(check[0].Payload);
            Assert.Equal((ushort)9, BinaryPrimitives.ReadUInt16BigEndian(secchk[(ushort)CodePoint.SECMEC]));
            // Db2 LUW carries both encrypted values as SECTKN fields, not USRID/PASSWORD.
            var tokens = SectknFields(check[0].Payload);
            Assert.Equal(2, tokens.Count);

            var serverKey = Secmec9.CalculateSessionKey(clientPublic, serverPrivate)[12..20];
            var iv = serverPublic[12..20];
            Assert.Equal("db2inst1", Ebcdic.DecodeCp500(Secmec9Decrypt(serverKey, iv, tokens[0])));
            Assert.Equal("secret", Ebcdic.DecodeCp500(Secmec9Decrypt(serverKey, iv, tokens[1])));

            await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
            await SendAsync(stream, CodePoint.ACCRDBRM, DdmCodec.PackBytes(CodePoint.RDBINTTKN, [4, 28, 167, 50]));

            var initialize = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, initialize.Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
        });

        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "secret",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 9,
        };
        await using (var session = await Db2Session.ConnectAsync(config))
        {
            Assert.False(session.IsClosed);
        }
        await serverTask;
    }

    [Fact]
    public async Task SwitchUserFallbackFailsClosedForMechanism9()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverPrivate = Secmec9FromHex(Secmec9ServerPrivateHex);
        var serverPublic = Convert.FromHexString(Secmec9ServerPublicHex);
        var secchkSeen = false;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var opening = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC }, opening.Select(packet => packet.CodePoint));
            var clientPublic = DdmCodec.ParseParameters(opening[1].Payload)[(ushort)CodePoint.SECTKN];
            var accsecRd = DdmCodec.PackUInt16(CodePoint.SECMEC, 9).Concat(DdmCodec.PackBytes(CodePoint.SECTKN, serverPublic)).ToArray();
            await SendAsync(stream, CodePoint.ACCSECRD, accsecRd);
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
            await SendAsync(stream, CodePoint.ACCRDBRM, DdmCodec.PackBytes(CodePoint.RDBINTTKN, [4, 28, 167, 50]));
            var initialize = await ReadChainAsync(stream);
            Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, initialize.Select(packet => packet.CodePoint));
            await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var chain = await ReadChainAsync(stream);
                Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSQLIMM, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, chain.Select(packet => packet.CodePoint));
                await SendAsync(stream, CodePoint.SQLCARD, SqlCard(-551, "42501"));
            }
            var buffer = new byte[1];
            try
            {
                var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
                secchkSeen = read > 0;
            }
            catch (TimeoutException) { }
            _ = clientPublic;
        });

        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "secret",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 9,
        };
        await using var session = await Db2Session.ConnectAsync(config);
        await Assert.ThrowsAsync<Db2UnsupportedFeatureException>(() => session.SwitchUserAsync("db2test2", "secret"));
        await serverTask;
        Assert.False(secchkSeen, "mechanism 9 must not send a plaintext-password SECCHK fallback");
    }

    [Fact]
    public async Task Secmec9RejectsMissingServerSecurityTokenBeforeSecchk()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            await ReadChainAsync(stream);
            await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 9));
            await Task.Delay(100);
        });

        var config = new Db2Config
        {
            Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "secret",
            Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 9,
        };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Db2Session.ConnectAsync(config));
        Assert.Contains("server security token", error.Message, StringComparison.Ordinal);
        await serverTask;
    }

    [Fact]
    public void SecchkKerberosBuilderCarriesTokenWithoutPassword()
    {
        var fields = DdmCodec.ParseParameters(DdmCodec.PackSecchkKerberos("TESTDB", [0x60, 0x82, 1, 2, 3], "db2inst1").AsSpan(4));
        Assert.Equal((ushort)11, BinaryPrimitives.ReadUInt16BigEndian(fields[(ushort)CodePoint.SECMEC]));
        Assert.Equal(new byte[] { 0x60, 0x82, 1, 2, 3 }, fields[(ushort)CodePoint.SECTKN]);
        Assert.Equal("db2inst1", Ebcdic.DecodeCp500(fields[(ushort)CodePoint.USRID]));
        Assert.DoesNotContain((ushort)CodePoint.PASSWORD, fields.Keys);
    }

    [Fact]
    public async Task KerberosHandshakeSendsTokenFileBytesInSecchk()
    {
        var token = new byte[] { 0x60, 0x82, 1, 2, 3, 4, 5, 6, 7, 8 };
        var tokenFile = Path.GetTempFileName();
        await File.WriteAllBytesAsync(tokenFile, token);
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var opening = await ReadChainAsync(stream);
                Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.ACCSEC }, opening.Select(packet => packet.CodePoint));
                var accsec = DdmCodec.ParseParameters(opening[1].Payload);
                Assert.Equal((ushort)11, BinaryPrimitives.ReadUInt16BigEndian(accsec[(ushort)CodePoint.SECMEC]));
                Assert.DoesNotContain((ushort)CodePoint.SECTKN, accsec.Keys);

                await SendAsync(stream, CodePoint.ACCSECRD, DdmCodec.PackUInt16(CodePoint.SECMEC, 11));

                // Layout matches IBM's own clients on the wire (CLP/JCC captured against Db2 11.5.8):
                // SECCHK without USRID/PASSWORD, then the token as a separate SECTKN OBJECT DSS.
                var check = await ReadChainAsync(stream);
                Assert.Equal(new ushort[] { (ushort)CodePoint.SECCHK, (ushort)CodePoint.SECTKN, (ushort)CodePoint.ACCRDB }, check.Select(packet => packet.CodePoint));
                var secchk = DdmCodec.ParseParameters(check[0].Payload);
                Assert.Equal(token, check[1].Payload);
                Assert.DoesNotContain((ushort)CodePoint.USRID, secchk.Keys);
                Assert.DoesNotContain((ushort)CodePoint.PASSWORD, secchk.Keys);

                await SendAsync(stream, CodePoint.SECCHKRM, DdmCodec.PackBytes(CodePoint.SECCHKCD, [0]), true);
                await SendAsync(stream, CodePoint.ACCRDBRM, DdmCodec.PackBytes(CodePoint.RDBINTTKN, [4, 28, 167, 50]));

                var initialize = await ReadChainAsync(stream);
                Assert.Equal(new ushort[] { (ushort)CodePoint.EXCSAT, (ushort)CodePoint.EXCSQLSET, (ushort)CodePoint.SQLSTT, (ushort)CodePoint.RDBCMM }, initialize.Select(packet => packet.CodePoint));
                await SendAsync(stream, CodePoint.SQLCARD, SqlCard());
            });

            var config = new Db2Config
            {
                Host = "127.0.0.1", Port = port, Database = "TESTDB", User = "db2inst1", Password = "secret",
                Timeout = TimeSpan.FromSeconds(3), BlockSize = 65535, SecurityMechanism = 11, GssapiTokenFile = tokenFile,
            };
            await using (var session = await Db2Session.ConnectAsync(config))
            {
                Assert.False(session.IsClosed);
            }
            await serverTask;
        }
        finally { File.Delete(tokenFile); }
    }

    [Fact]
    public async Task KerberosMechanismRequiresATokenFileBeforeConnecting()
    {
        var error = await Assert.ThrowsAsync<Db2ConfigurationException>(() => Db2Session.ConnectAsync(new Db2Config
        {
            Host = "127.0.0.1", Port = 1, Database = "TESTDB", User = "db2inst1", Password = "secret", SecurityMechanism = 11,
        }));
        Assert.Contains("gssapi_token_file", error.Message, StringComparison.Ordinal);
    }
}
