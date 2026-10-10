using System.Data;
using Db2Replay;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class PlatformSmokeTests
{
    [Fact]
    [Trait("Category", "PlatformSmoke")]
    public async Task AdoNetCanOpenExecuteAndReadMixedRowsOverLoopback()
    {
        var fixture = new ReplayFixture("Mixed", 128);
        await using var server = new ReplayServer(fixture);
        await using var connection = new Db2AdoConnection(ReplayInfrastructureTests.Dsn(server.Port, 32767));

        Assert.Equal(ConnectionState.Closed, connection.State);
        await connection.OpenAsync();
        Assert.Equal(ConnectionState.Open, connection.State);

        await using var command = connection.CreateCommand();
        command.CommandText = fixture.Sql;
        await using var reader = await command.ExecuteReaderAsync();

        Assert.Equal(fixture.Columns, reader.FieldCount);
        Assert.False(string.IsNullOrWhiteSpace(reader.GetName(0)));
        Assert.Equal(0, reader.GetOrdinal(reader.GetName(0)));

        var rows = 0;
        long checksum = 0;
        while (await reader.ReadAsync())
        {
            rows++;
            checksum += reader.GetInt32(0);

            Assert.Equal(fixture.Value(rows, 0), reader.GetInt32(0));
            Assert.Equal(fixture.Value(rows, 1), reader.GetInt64(1));
            Assert.Equal(fixture.Value(rows, 2), reader.GetDecimal(2));
            Assert.Equal(fixture.Value(rows, 3), reader.GetDateTime(3));
            Assert.Equal(fixture.Value(rows, 4), reader.GetString(4));

            var expectedNullable = fixture.Value(rows, 5);
            Assert.Equal(expectedNullable is null, reader.IsDBNull(5));
            if (expectedNullable is not null)
                Assert.Equal(expectedNullable, reader.GetString(5));
        }

        Assert.Equal(fixture.Rows, rows);
        Assert.Equal(fixture.IdChecksum, checksum);
        server.ThrowIfFailed();
    }

    [Fact]
    [Trait("Category", "PlatformSmoke")]
    public async Task NativeSessionCanConnectAndStreamRowsOverLoopback()
    {
        var fixture = new ReplayFixture("Int", 256);
        await using var server = new ReplayServer(fixture, new(FragmentSize: 11, EndWithSqlCode100: true));
        await using var session = await Db2Session.ConnectAsync(ReplayInfrastructureTests.Dsn(server.Port, 16384));

        var rows = 0;
        long checksum = 0;
        await foreach (var row in session.QueryStreamAsync(fixture.Sql))
        {
            rows++;
            Assert.Single(row.Values);
            Assert.Equal(rows, row.Values[0]);
            checksum += (int)row.Values[0]!;
        }

        Assert.Equal(fixture.Rows, rows);
        Assert.Equal(fixture.IdChecksum, checksum);
        server.ThrowIfFailed();
    }

    [Fact]
    [Trait("Category", "PlatformSmoke")]
    public async Task ExecuteScalarAndEarlyReaderDisposalLeaveConnectionReusable()
    {
        var fixture = new ReplayFixture("Discard", 256);
        await using var server = new ReplayServer(fixture);
        await using var connection = new Db2AdoConnection(ReplayInfrastructureTests.Dsn(server.Port));
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = fixture.Sql;
            Assert.Equal(1, await command.ExecuteScalarAsync());
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = fixture.Sql;
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.False(await reader.ReadAsync());
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = fixture.Sql;
            await using var reader = await command.ExecuteReaderAsync();
            var rows = 0;
            while (await reader.ReadAsync()) rows++;
            Assert.Equal(fixture.Rows, rows);
        }

        server.ThrowIfFailed();
    }
}
