using System.Data;
using Db2Replay;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class ReplayInfrastructureTests
{
    [Theory]
    [InlineData("Int", 16384)]
    [InlineData("Int", 32767)]
    [InlineData("Int", 65535)]
    [InlineData("Mixed", 32767)]
    [InlineData("Wide", 65535)]
    public async Task LoopbackReturnsCompleteDeterministicValues(string name, int blockSize)
    {
        var fixture = new ReplayFixture(name, 5000);
        await using var server = new ReplayServer(fixture);
        await using var connection = new Db2AdoConnection(Dsn(server.Port, blockSize));
        await connection.OpenAsync();
        for (var pass = 0; pass < 2; pass++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = fixture.Sql;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.Equal(fixture.Columns, reader.FieldCount);
            var row = 0;
            long sum = 0;
            while (await reader.ReadAsync())
            {
                row++; sum += reader.GetInt32(0);
                var values = new object[fixture.Columns];
                Assert.Equal(fixture.Columns, reader.GetValues(values));
                for (var c = 0; c < fixture.Columns; c++)
                {
                    Assert.Equal(c, reader.GetOrdinal(reader.GetName(c)));
                    var expected = fixture.Value(row, c) ?? DBNull.Value;
                    Assert.Equal(expected, reader.GetValue(c));
                    Assert.Equal(expected, values[c]);
                    Assert.Equal(expected is DBNull, reader.IsDBNull(c));
                }
                Assert.Equal(row, reader.GetFieldValue<int>(0));
                if (fixture.Columns > 1)
                {
                    Assert.Equal(fixture.Value(row, 1), reader.GetInt64(1));
                    Assert.Equal(fixture.Value(row, 2), reader.GetDecimal(2));
                    Assert.Equal(fixture.Value(row, 3), reader.GetDateTime(3));
                    Assert.Equal(fixture.Value(row, 4), reader.GetString(4));
                }
            }
            Assert.Equal(fixture.Rows, row);
            Assert.Equal(fixture.IdChecksum, sum);
        }
        Assert.Equal(2, server.Counters.Queries);
        Assert.Equal(2L * ((fixture.DataLength + blockSize - 1) / blockSize - 1), server.Counters.Fetches);
        server.ThrowIfFailed();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(10, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    public async Task EarlyDisposeDrainsAndAllowsNextQuery(int take, bool synchronous)
    {
        var fixture = new ReplayFixture("Discard", 5000);
        await using var server = new ReplayServer(fixture);
        await using var connection = new Db2AdoConnection(Dsn(server.Port));
        await connection.OpenAsync();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = fixture.Sql;
            var reader = synchronous ? command.ExecuteReader() : await command.ExecuteReaderAsync();
            for (var n = 1; n <= take; n++)
            {
                Assert.True(synchronous ? reader.Read() : await reader.ReadAsync());
                Assert.Equal(n, reader.GetInt32(0));
            }
            if (synchronous) reader.Dispose(); else await reader.DisposeAsync();
        }
        await AssertNextQuery(connection, fixture);
        server.ThrowIfFailed();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScalarAndSingleRowDrainAndAllowNextQuery(bool scalar)
    {
        var fixture = new ReplayFixture("Discard", 5000);
        await using var server = new ReplayServer(fixture);
        await using var connection = new Db2AdoConnection(Dsn(server.Port));
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = fixture.Sql;
            if (scalar) Assert.Equal(1, await command.ExecuteScalarAsync());
            else
            {
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow);
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1, reader.GetInt32(0));
                Assert.False(await reader.ReadAsync());
            }
        }
        await AssertNextQuery(connection, fixture);
    }

    [Fact]
    public async Task NativeRowsRemainOwnedAfterAdvancingAndDisposal()
    {
        var fixture = new ReplayFixture("Mixed", 5000);
        await using var server = new ReplayServer(fixture);
        await using var session = await Db2Session.ConnectAsync(Dsn(server.Port));
        var rows = new List<Db2StreamRow>();
        await foreach (var row in session.QueryStreamAsync(fixture.Sql)) rows.Add(row);
        Assert.Equal(fixture.Rows, rows.Count);
        // List scratch storage may be reused between fetches, but never row
        // arrays or metadata exposed by a completed enumeration.
        await foreach (var _ in session.QueryStreamAsync(fixture.Sql)) { }
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < fixture.Columns; c++) Assert.Equal(fixture.NativeValue(r + 1, c), rows[r].Values[c]);
        Assert.NotSame(rows[0].Values, rows[1].Values);
    }

    [Fact]
    public async Task FragmentedTransportAndSqlCode100ReturnEveryRow()
    {
        var fixture = new ReplayFixture("Int", 3000);
        await using var server = new ReplayServer(fixture, new(FragmentSize: 7, EndWithSqlCode100: true));
        await using var session = await Db2Session.ConnectAsync(Dsn(server.Port, 16384));
        long sum = 0; var count = 0;
        await foreach (var row in session.QueryStreamAsync(fixture.Sql)) { count++; sum += (int)row.Values[0]!; }
        Assert.Equal(fixture.Rows, count); Assert.Equal(fixture.IdChecksum, sum);
    }

    internal static string Dsn(int port, int blockSize = 65535) =>
        $"Host=127.0.0.1;Port={port};Database=REPLAY;User=test;Pooling=false;SecurityMechanism=3;BlockSize={blockSize}";

    private static async Task AssertNextQuery(Db2AdoConnection connection, ReplayFixture fixture)
    {
        await using var command = connection.CreateCommand(); command.CommandText = fixture.Sql;
        await using var reader = await command.ExecuteReaderAsync();
        long sum = 0; var count = 0;
        while (await reader.ReadAsync()) { count++; sum += reader.GetInt32(0); }
        Assert.Equal(fixture.Rows, count); Assert.Equal(fixture.IdChecksum, sum);
    }
}
