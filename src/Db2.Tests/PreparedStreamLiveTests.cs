using System.Data;
using JustyBase.Db2.AdoNet;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class PreparedStreamLiveTests
{
    [Db2LiveFact]
    [Trait("Category", "Live")]
    public async Task AlternatingPreparedReadersKeepTheirOwnSqlParametersAndMetadata()
    {
        await using var connection = new Db2AdoConnection(Environment.GetEnvironmentVariable("DB2_TEST_DSN")!);
        await connection.OpenAsync();
        await using var first = new Db2AdoCommand { Connection = connection, CommandText = "VALUES CAST(? AS INTEGER)" };
        first.Parameters.Add(new Db2AdoParameter { DbType = DbType.Int32, Value = 42 });
        await first.PrepareAsync();
        await using var second = new Db2AdoCommand { Connection = connection, CommandText = "VALUES (CAST(? AS BIGINT), CAST(? AS INTEGER))" };
        second.Parameters.Add(new Db2AdoParameter { DbType = DbType.Int64, Value = 84L });
        second.Parameters.Add(new Db2AdoParameter { DbType = DbType.Int32, Value = 85 });
        await second.PrepareAsync();

        foreach (var command in new[] { first, second, first, second })
        {
            await using var reader = await command.ExecuteReaderAsync();
            Assert.Equal(command == first ? 1 : 2, reader.FieldCount);
            Assert.True(await reader.ReadAsync());
            if (command == first)
            {
                Assert.Equal(typeof(int), reader.GetFieldType(0));
                Assert.Equal(42, reader.GetInt32(0));
            }
            else
            {
                Assert.Equal(typeof(long), reader.GetFieldType(0));
                Assert.Equal(84L, reader.GetInt64(0));
                Assert.Equal(85, reader.GetInt32(1));
            }
            Assert.False(await reader.ReadAsync());
        }
    }
}
