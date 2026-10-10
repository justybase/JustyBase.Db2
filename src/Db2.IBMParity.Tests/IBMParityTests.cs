extern alias IBMOfficial;

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using JustyBase.Db2.AdoNet;
using IBMConnection = IBMOfficial::IBM.Data.Db2.DB2Connection;
using Xunit;

namespace JustyBase.Db2.IBMParity.Tests;

public sealed class IBMParityTests
{
    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task ScalarValuesAndTypesMatchOfficialProvider()
    {
        const string sql = "SELECT " +
            "CAST(42 AS INTEGER) AS I, " +
            "CAST(9223372036854775807 AS BIGINT) AS BI, " +
            "CAST(12345678901.1234567890 AS DECIMAL(31,10)) AS D, " +
            "CAST(1.25 AS DOUBLE) AS F, " +
            "CAST('Zażółć 東京' AS VARCHAR(64)) AS S, " +
            "DATE('2026-10-10') AS DT, " +
            "TIME('12:34:56') AS TM, " +
            "TIMESTAMP('2026-10-10-12.34.56.123456') AS TS " +
            "FROM SYSIBM.SYSDUMMY1";

        await using var ibm = OpenIbm();
        await using var native = await OpenNativeAsync();
        var expected = await ReadSingleRowAsync(ibm, sql);
        var actual = await ReadSingleRowAsync(native, sql);

        Assert.Equal(expected.Names, actual.Names);
        Assert.Equal(expected.Types, actual.Types);
        Assert.Equal(expected.Values, actual.Values);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task TypedNullMatrixMatchesOfficialProvider()
    {
        const string sql = "SELECT " +
            "CAST(NULL AS SMALLINT) AS C0, CAST(NULL AS INTEGER) AS C1, " +
            "CAST(NULL AS BIGINT) AS C2, CAST(NULL AS DECIMAL(9,3)) AS C3, " +
            "CAST(NULL AS DECFLOAT(16)) AS C4, CAST(NULL AS DATE) AS C5, " +
            "CAST(NULL AS TIME) AS C6, CAST(NULL AS TIMESTAMP(6)) AS C7, " +
            "CAST(NULL AS VARCHAR(20)) AS C8, CAST(NULL AS VARGRAPHIC(10)) AS C9, " +
            "CAST(NULL AS VARBINARY(10)) AS C10, CAST(NULL AS BLOB(100)) AS C11, " +
            "CAST(NULL AS CLOB(100)) AS C12, CAST(NULL AS DBCLOB(100)) AS C13, " +
            "CAST(NULL AS XML) AS C14 FROM SYSIBM.SYSDUMMY1";

        await using var ibm = OpenIbm();
        await using var native = await OpenNativeAsync();
        var expected = await ReadSingleRowAsync(ibm, sql);
        var actual = await ReadSingleRowAsync(native, sql);

        Assert.Equal(expected.Names, actual.Names);
        Assert.Equal(expected.Types, actual.Types);
        Assert.All(expected.Values, value => Assert.Equal("<NULL>", value));
        Assert.Equal(expected.Values, actual.Values);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task BinaryAndLobValuesMatchOfficialProvider()
    {
        const string sql = "SELECT " +
            "CAST(X'00010203FF' AS VARBINARY(16)) AS VB, " +
            "BLOB(X'48656C6C6F00FF') AS BL, " +
            "CAST('CLOB Zażółć' AS CLOB(1K)) AS CL, " +
            "CAST('DBCLOB 東京' AS DBCLOB(1K)) AS DCL, " +
            "XMLPARSE(DOCUMENT '<root><v>42</v></root>') AS X " +
            "FROM SYSIBM.SYSDUMMY1";

        await using var ibm = OpenIbm();
        await using var native = await OpenNativeAsync();
        var expected = await ReadSingleRowAsync(ibm, sql);
        var actual = await ReadSingleRowAsync(native, sql);

        Assert.Equal(expected.Names, actual.Names);
        Assert.Equal(expected.Types, actual.Types);
        Assert.Equal(expected.Values, actual.Values);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task PositionalParametersMatchOfficialProvider()
    {
        const string sql = "SELECT CAST(? AS INTEGER) AS I, CAST(? AS VARCHAR(64)) AS S, CAST(? AS DECIMAL(12,4)) AS D FROM SYSIBM.SYSDUMMY1";
        object?[] values = [123456, "Zażółć 東京", 98765.4321m];

        await using var ibm = OpenIbm();
        await using var native = await OpenNativeAsync();
        var expected = await ReadSingleRowAsync(ibm, sql, values);
        var actual = await ReadSingleRowAsync(native, sql, values);

        Assert.Equal(expected.Names, actual.Names);
        Assert.Equal(expected.Types, actual.Types);
        Assert.Equal(expected.Values, actual.Values);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task EmptyResultAndExecuteScalarMatchOfficialProvider()
    {
        const string emptySql = "SELECT CAST(1 AS INTEGER) AS I FROM SYSIBM.SYSDUMMY1 WHERE 1 = 0";
        const string scalarSql = "VALUES CAST(42 AS INTEGER)";

        await using var ibm = OpenIbm();
        await using var native = await OpenNativeAsync();

        Assert.Equal(await CountRowsAsync(ibm, emptySql), await CountRowsAsync(native, emptySql));
        Assert.Equal(
            Normalize(await ExecuteScalarAsync(ibm, scalarSql)),
            Normalize(await ExecuteScalarAsync(native, scalarSql)));
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task InvalidObjectFailsOnBothProviders()
    {
        const string sql = "SELECT * FROM TABLE_THAT_MUST_NOT_EXIST_7D3A91";
        await using var ibm = OpenIbm();
        await using var native = await OpenNativeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => ExecuteReaderToEndAsync(ibm, sql));
        await Assert.ThrowsAnyAsync<Exception>(() => ExecuteReaderToEndAsync(native, sql));
    }

    private static IBMConnection OpenIbm()
    {
        var host = Required("DB2_LIVE_TEST_HOST");
        var port = Required("DB2_LIVE_TEST_PORT");
        var database = Required("DB2_LIVE_TEST_DATABASE");
        var user = Required("DB2_LIVE_TEST_USER");
        var password = Required("DB2_LIVE_TEST_PASSWORD");
        var connection = new IBMConnection($"Server={host}:{port};Database={database};UID={user};PWD={password};Pooling=false;");
        connection.Open();
        return connection;
    }

    private static async Task<Db2AdoConnection> OpenNativeAsync()
    {
        var connection = new Db2AdoConnection(Required("DB2_TEST_DSN"));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<RowSnapshot> ReadSingleRowAsync(DbConnection connection, string sql, IReadOnlyList<object?>? parameters = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var value in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
        }

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var names = new string[reader.FieldCount];
        var types = new string[reader.FieldCount];
        var values = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            names[i] = reader.GetName(i).ToUpperInvariant();
            types[i] = NormalizeType(reader.GetDataTypeName(i));
            values[i] = await ReadCanonicalValueAsync(reader, i, types[i]);
        }

        Assert.False(await reader.ReadAsync());
        return new RowSnapshot(names, types, values);
    }

    private static async Task<string> ReadCanonicalValueAsync(DbDataReader reader, int ordinal, string normalizedType)
    {
        if (await reader.IsDBNullAsync(ordinal)) return "<NULL>";

        if (normalizedType is "BINARY" or "VARBINARY" or "BLOB")
        {
            var length = reader.GetBytes(ordinal, 0, null, 0, 0);
            if (length > int.MaxValue) throw new InvalidOperationException("Parity value is too large for this focused smoke test.");
            var bytes = new byte[(int)length];
            if (bytes.Length > 0)
            {
                var read = reader.GetBytes(ordinal, 0, bytes, 0, bytes.Length);
                Assert.Equal(bytes.Length, read);
            }
            return "0x" + Convert.ToHexString(bytes);
        }

        if (normalizedType is "CLOB" or "DBCLOB")
        {
            var length = reader.GetChars(ordinal, 0, null, 0, 0);
            if (length > int.MaxValue) throw new InvalidOperationException("Parity text is too large for this focused smoke test.");
            var chars = new char[(int)length];
            if (chars.Length > 0)
            {
                var read = reader.GetChars(ordinal, 0, chars, 0, chars.Length);
                Assert.Equal(chars.Length, read);
            }
            return new string(chars);
        }

        if (normalizedType == "XML")
        {
            var value = reader.GetValue(ordinal);
            if (value is string text) return text;
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return Normalize(reader.GetValue(ordinal));
    }

    private static async Task<int> CountRowsAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync()) count++;
        return count;
    }

    private static async Task<object?> ExecuteScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteReaderToEndAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { }
    }

    private static string NormalizeType(string type)
    {
        var normalized = type.Trim().ToUpperInvariant();
        if (normalized is "FLOAT" or "DOUBLE" or "DOUBLE PRECISION") return "DOUBLE";
        if (normalized.StartsWith("DECIMAL", StringComparison.Ordinal)) return "DECIMAL";
        if (normalized.StartsWith("DECFLOAT", StringComparison.Ordinal)) return "DECFLOAT";
        if (normalized.StartsWith("TIMESTAMP", StringComparison.Ordinal)) return "TIMESTAMP";
        if (normalized.StartsWith("VARCHAR", StringComparison.Ordinal) || normalized == "CHARACTER VARYING") return "VARCHAR";
        if (normalized.StartsWith("VARGRAPHIC", StringComparison.Ordinal)) return "VARGRAPHIC";
        if (normalized.StartsWith("BINARY", StringComparison.Ordinal)) return "BINARY";
        if (normalized.StartsWith("VARBINARY", StringComparison.Ordinal)) return "VARBINARY";
        if (normalized.StartsWith("BLOB", StringComparison.Ordinal)) return "BLOB";
        if (normalized.StartsWith("CLOB", StringComparison.Ordinal)) return "CLOB";
        if (normalized.StartsWith("DBCLOB", StringComparison.Ordinal)) return "DBCLOB";
        if (normalized.StartsWith("XML", StringComparison.Ordinal)) return "XML";
        return normalized;
    }

    private static string Normalize(object? value) => value switch
    {
        null or DBNull => "<NULL>",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime timestamp => timestamp.TimeOfDay == TimeSpan.Zero
            ? timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : timestamp.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.'),
        TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Set {name}.");

    private sealed record RowSnapshot(string[] Names, string[] Types, string[] Values);
}
