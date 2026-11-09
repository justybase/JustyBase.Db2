extern alias IBMOfficial;

using System.Data.Common;
using System.Globalization;
using JustyBase.Db2.AdoNet;
using IBMConnection = IBMOfficial::IBM.Data.Db2.DB2Connection;
using IBMException = IBMOfficial::IBM.Data.Db2.DB2Exception;
using Xunit;

namespace JustyBase.Db2.IBMParity.Tests;

public sealed class IBMParityTests
{
    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task ScalarValuesAndTypesMatchOfficialProvider()
    {
        await AssertCaseAsync(CaseScalars);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task TypedNullMatrixMatchesOfficialProvider()
    {
        await AssertCaseAsync(CaseTypedNulls);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task BinaryAndLobValuesMatchOfficialProvider()
    {
        await AssertCaseAsync(CaseBinaryLobs);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task PositionalParametersMatchOfficialProvider()
    {
        await AssertCaseAsync(CasePositional);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task IntegerBoundariesMatchOfficialProvider()
    {
        await AssertCaseAsync(CaseIntegerBoundaries);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task FloatsAndBooleansMatchOfficialProvider()
    {
        await AssertCaseAsync(CaseFloatsBooleans);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task DecimalsAndFiniteDecfloatsMatchOfficialProvider()
    {
        await AssertCaseAsync(CaseDecimals);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task DecfloatSpecialValuesMatchOfficialProvider()
    {
        await AssertCaseAsync(CaseDecfloatSpecials);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task TextUnicodeAndBinaryMatchOfficialProvider()
    {
        await AssertCaseAsync(CaseTextUnicode);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task TemporalBoundariesMatchOfficialProvider()
    {
        await AssertCaseAsync(CaseTemporalBoundaries);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task EmptyAndNullLobsStayDistinct()
    {
        await AssertCaseAsync(CaseEmptyNullLobs);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task TypedParameterMatrixMatchesOfficialProvider()
    {
        await AssertCaseAsync(CaseParameterTypes);
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task ParameterConversionMatrixMatchesOfficialProvider()
    {
        await AssertCaseAsync(CaseBooleanConversions);
        await AssertCaseAsync(CaseIntegerConversions);
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
    public async Task LobParameterRoundTripMatchesOfficialProvider()
    {
        const string ibmTable = "JB_PARITY_LOB_CS_IBM";
        const string nativeTable = "JB_PARITY_LOB_CS_NATIVE";
        await SetupTablesAsync(new Dictionary<string, string>
        {
            [ibmTable] = "(ID INTEGER, B BLOB(1M), C CLOB(1M), X XML)",
            [nativeTable] = "(ID INTEGER, B BLOB(1M), C CLOB(1M), X XML)",
        });
        try
        {
            object?[] payload = [1, new byte[] { 0x00, 0xFF, 0x68, 0x65, 0x6C, 0x6C, 0x6F }, "CLOB café 東京 🚀", "<root><v>42</v></root>"];
            await ExecuteAsync(OpenIbm, $"INSERT INTO {ibmTable} VALUES (?, ?, ?, ?)", payload);
            await ExecuteAsync(OpenNativeAsync, $"INSERT INTO {nativeTable} VALUES (?, ?, ?, ?)", payload);

            string[] kinds = ["lob-binary", "lob-text", "xml"];
            var expected = await ReadRowAsync(OpenIbm(), $"SELECT B, C, X FROM {ibmTable} WHERE ID = 1", null, kinds);
            var actual = await ReadRowAsync(await OpenNativeAsync(), $"SELECT B, C, X FROM {nativeTable} WHERE ID = 1", null, kinds);
            AssertSnapshotsEqual(expected, actual);
        }
        finally
        {
            await DropTablesAsync([ibmTable, nativeTable]);
        }
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task ErrorMatrixMatchesOfficialProvider()
    {
        await using var ibm = OpenIbm();
        await using var native = await OpenNativeAsync();

        foreach (var sql in new[]
        {
            "SELEC 1 FROM SYSIBM.SYSDUMMY1",
            "SELECT * FROM TABLE_THAT_MUST_NOT_EXIST_7D3A91",
            "VALUES 1/0",
        })
        {
            Assert.Equal(await ErrorNativeAsync(native, sql), await ErrorIbmAsync(ibm, sql));
        }

        const string ibmTable = "JB_PARITY_ERR_CS_IBM";
        const string nativeTable = "JB_PARITY_ERR_CS_NATIVE";
        await SetupTablesAsync(new Dictionary<string, string>
        {
            [ibmTable] = "(ID INTEGER NOT NULL PRIMARY KEY, V VARCHAR(3))",
            [nativeTable] = "(ID INTEGER NOT NULL PRIMARY KEY, V VARCHAR(3))",
        });
        try
        {
            await ExecuteAsync(OpenIbm, $"INSERT INTO {ibmTable} VALUES (1, 'a')");
            await ExecuteAsync(OpenNativeAsync, $"INSERT INTO {nativeTable} VALUES (1, 'a')");

            var cases = new (string Label, string IbmSql, string NativeSql)[]
            {
                ("duplicate key", $"INSERT INTO {ibmTable} VALUES (1, 'b')", $"INSERT INTO {nativeTable} VALUES (1, 'b')"),
                ("not null", $"INSERT INTO {ibmTable} VALUES (NULL, 'c')", $"INSERT INTO {nativeTable} VALUES (NULL, 'c')"),
                ("truncation", $"INSERT INTO {ibmTable} VALUES (2, 'abcd')", $"INSERT INTO {nativeTable} VALUES (2, 'abcd')"),
            };
            await using var ibmConnection = OpenIbm();
            await using var nativeConnection = await OpenNativeAsync();
            foreach (var (label, ibmSql, nativeSql) in cases)
            {
                var expected = await ErrorIbmAsync(ibmConnection, ibmSql);
                var actual = await ErrorNativeAsync(nativeConnection, nativeSql);
                Assert.True(actual == expected, $"{label}: native {actual} != oracle {expected}");
            }
        }
        finally
        {
            await DropTablesAsync([ibmTable, nativeTable]);
        }
    }

    [Fact]
    [Trait("Category", "IBMParity")]
    public async Task SyncApiCorpusMatchesOfficialProvider()
    {
        await AssertCaseSyncAsync(CaseScalars);
        await AssertCaseSyncAsync(CaseIntegerBoundaries);
        await AssertCaseSyncAsync(CaseFloatsBooleans);
        await AssertCaseSyncAsync(CaseTextUnicode);
        await AssertCaseSyncAsync(CaseTemporalBoundaries);
        await AssertCaseSyncAsync(CaseParameterTypes);
    }

    private static readonly Case CaseScalars = new(
        "SELECT CAST(42 AS INTEGER) AS I, " +
        "CAST(9223372036854775807 AS BIGINT) AS BI, " +
        "CAST(12345678901.1234567890 AS DECIMAL(31,10)) AS D, " +
        "CAST(1.25 AS DOUBLE) AS F, " +
        "CAST('Zażółć 東京' AS VARCHAR(64)) AS S, " +
        "DATE('2026-10-10') AS DT, " +
        "TIME('12:34:56') AS TM, " +
        "TIMESTAMP('2026-10-10-12.34.56.123456') AS TS " +
        "FROM SYSIBM.SYSDUMMY1",
        ["integer", "integer", "numeric", "numeric", "text", "temporal", "temporal", "temporal"]);

    private static readonly Case CaseTypedNulls = new(
        "SELECT CAST(NULL AS SMALLINT) AS C0, CAST(NULL AS INTEGER) AS C1, " +
        "CAST(NULL AS BIGINT) AS C2, CAST(NULL AS DECIMAL(9,3)) AS C3, " +
        "CAST(NULL AS DECFLOAT(16)) AS C4, CAST(NULL AS DATE) AS C5, " +
        "CAST(NULL AS TIME) AS C6, CAST(NULL AS TIMESTAMP(6)) AS C7, " +
        "CAST(NULL AS VARCHAR(20)) AS C8, CAST(NULL AS VARGRAPHIC(10)) AS C9, " +
        "CAST(NULL AS VARBINARY(10)) AS C10, CAST(NULL AS BLOB(100)) AS C11, " +
        "CAST(NULL AS CLOB(100)) AS C12, CAST(NULL AS DBCLOB(100)) AS C13, " +
        "CAST(NULL AS XML) AS C14, CAST(NULL AS BOOLEAN) AS C15 " +
        "FROM SYSIBM.SYSDUMMY1",
        ["integer", "integer", "integer", "numeric", "numeric", "temporal", "temporal", "temporal",
         "text", "text", "binary", "lob-binary", "lob-text", "lob-text", "xml", "boolean"]);

    private static readonly Case CaseBinaryLobs = new(
        "SELECT CAST(X'00010203FF' AS VARBINARY(16)) AS VB, " +
        "BLOB(X'48656C6C6F00FF') AS BL, " +
        "CAST('CLOB Zażółć' AS CLOB(1K)) AS CL, " +
        "CAST('DBCLOB 東京' AS DBCLOB(1K)) AS DCL, " +
        "XMLPARSE(DOCUMENT '<root><v>42</v></root>') AS X " +
        "FROM SYSIBM.SYSDUMMY1",
        ["binary", "lob-binary", "lob-text", "lob-text", "xml"]);

    private static readonly Case CaseIntegerBoundaries = new(
        "SELECT CAST(NULL AS INTEGER) AS C0, " +
        "CAST(-2147483648 AS INTEGER) AS C1, CAST(2147483647 AS INTEGER) AS C2, " +
        "CAST(9223372036854775807 AS BIGINT) AS C3, " +
        "CAST(-9223372036854775808 AS BIGINT) AS C4, " +
        "CAST(-32768 AS SMALLINT) AS C5, CAST(32767 AS SMALLINT) AS C6 " +
        "FROM SYSIBM.SYSDUMMY1",
        ["integer", "integer", "integer", "integer", "integer", "integer", "integer"]);

    private static readonly Case CaseFloatsBooleans = new(
        "SELECT CAST(1.25 AS REAL) AS R, CAST(-2.5 AS DOUBLE) AS D, " +
        "CAST(TRUE AS BOOLEAN) AS BT, CAST(FALSE AS BOOLEAN) AS BF " +
        "FROM SYSIBM.SYSDUMMY1",
        ["numeric", "numeric", "boolean", "boolean"]);

    private static readonly Case CaseDecimals = new(
        "SELECT CAST(125 AS DECIMAL(5,2)) AS DEC, " +
        "CAST('123.456' AS DECFLOAT(16)) AS DF1, " +
        "CAST('-987.654' AS DECFLOAT(16)) AS DF2 " +
        "FROM SYSIBM.SYSDUMMY1",
        ["numeric", "numeric", "numeric"]);

    private static readonly Case CaseDecfloatSpecials = new(
        "SELECT CAST(CAST('Infinity' AS DECFLOAT(16)) AS VARCHAR(20)) AS PINF, " +
        "CAST(CAST('-Infinity' AS DECFLOAT(16)) AS VARCHAR(20)) AS NINF, " +
        "CAST(CAST('NaN' AS DECFLOAT(16)) AS VARCHAR(20)) AS NAN, " +
        "CAST(CAST('sNaN' AS DECFLOAT(16)) AS VARCHAR(20)) AS SNAN " +
        "FROM SYSIBM.SYSDUMMY1",
        ["text", "text", "text", "text"]);

    private static readonly Case CaseTextUnicode = new(
        "SELECT CAST('Zażółć 東京 🚀' AS VARCHAR(50)) AS S, " +
        "CAST(X'00FF7F' AS VARBINARY(10)) AS VB, " +
        "CAST('abc' AS CHAR(5)) AS C, " +
        "CAST('abc' AS VARCHAR(5)) AS V, " +
        "CAST('abc' AS GRAPHIC(5)) AS G " +
        "FROM SYSIBM.SYSDUMMY1",
        ["text", "binary", "text", "text", "text"]);

    private static readonly Case CaseTemporalBoundaries = new(
        "SELECT CAST('2024-02-29' AS DATE) AS D1, " +
        "CAST('23:59:58' AS TIME) AS T1, " +
        "CAST('2024-02-29 23:59:58.123456' AS TIMESTAMP) AS TS1, " +
        "CAST('0001-01-01' AS DATE) AS D2, " +
        "CAST('9999-12-31' AS DATE) AS D3, " +
        "CAST('00:00:00' AS TIME) AS T2, " +
        "CAST('23:59:59' AS TIME) AS T3 " +
        "FROM SYSIBM.SYSDUMMY1",
        ["temporal", "temporal", "temporal", "temporal", "temporal", "temporal", "temporal"]);

    private static readonly Case CaseEmptyNullLobs = new(
        "SELECT CAST(NULL AS BLOB(1M)) AS BN, CAST(X'' AS BLOB(1M)) AS BE, " +
        "CAST(NULL AS CLOB(1M)) AS CN, CAST('' AS CLOB(1M)) AS CE " +
        "FROM SYSIBM.SYSDUMMY1",
        ["lob-binary", "lob-binary", "lob-text", "lob-text"]);

    private static readonly Case CasePositional = new(
        "SELECT CAST(? AS INTEGER) AS I, CAST(? AS VARCHAR(64)) AS S, " +
        "CAST(? AS DECIMAL(12,4)) AS D FROM SYSIBM.SYSDUMMY1",
        ["integer", "text", "numeric"],
        [123456, "Zażółć 東京 🚀", "98765.4321"]);

    private static readonly Case CaseParameterTypes = new(
        "SELECT CAST(? AS INTEGER) AS I, CAST(? AS BIGINT) AS BI, " +
        "CAST(? AS SMALLINT) AS SI, CAST(? AS DECIMAL(12,4)) AS D, " +
        "CAST(? AS REAL) AS R, CAST(? AS DOUBLE) AS F, " +
        "CAST(? AS BOOLEAN) AS B, CAST(? AS VARCHAR(64)) AS S, " +
        "CAST(? AS VARBINARY(16)) AS VB, CAST(? AS DATE) AS DT, " +
        "CAST(? AS TIME) AS TM, CAST(? AS TIMESTAMP) AS TS " +
        "FROM SYSIBM.SYSDUMMY1",
        ["integer", "integer", "integer", "numeric", "numeric", "numeric", "boolean", "text", "binary", "temporal", "temporal", "temporal"],
        [null, long.MinValue, (short)32767, 98765.4321m, 1.25f, -2.5d, true, "Zażółć 東京 🚀", new byte[] { 0x00, 0xFF, 0x7F }, new DateTime(2024, 2, 29), new TimeSpan(23, 59, 58), new DateTime(2024, 2, 29, 23, 59, 58, 123).AddTicks(4560)]);

    private static readonly Case CaseBooleanConversions = new(
        "SELECT CAST(? AS BOOLEAN) AS B1, CAST(? AS BOOLEAN) AS B2, " +
        "CAST(? AS BOOLEAN) AS B3, CAST(? AS BOOLEAN) AS B4, " +
        "CAST(? AS BOOLEAN) AS B5, CAST(? AS BOOLEAN) AS B6 " +
        "FROM SYSIBM.SYSDUMMY1",
        ["boolean", "boolean", "boolean", "boolean", "boolean", "boolean"],
        ["true", " FALSE ", "YES", "no", 1, true]);

    private static readonly Case CaseIntegerConversions = new(
        "SELECT CAST(? AS SMALLINT) AS S1, CAST(? AS SMALLINT) AS S2, " +
        "CAST(? AS SMALLINT) AS S3, CAST(? AS SMALLINT) AS S4, " +
        "CAST(? AS SMALLINT) AS S5 FROM SYSIBM.SYSDUMMY1",
        ["integer", "integer", "integer", "integer", "integer"],
        [-3.75, " 1.5 ", true, false, "1e0"]);

    private static async Task AssertCaseAsync(Case item)
    {
        await using var ibm = OpenIbm();
        await using var native = await OpenNativeAsync();
        var expected = await ReadRowAsync(ibm, item.Sql, item.Parameters, item.Kinds);
        var actual = await ReadRowAsync(native, item.Sql, item.Parameters, item.Kinds);
        AssertSnapshotsEqual(expected, actual);
    }

    private static async Task AssertCaseSyncAsync(Case item)
    {
        using var ibm = OpenIbm();
        using var native = OpenNativeSync();
        var expected = ReadRowSync(ibm, item.Sql, item.Parameters, item.Kinds);
        var actual = ReadRowSync(native, item.Sql, item.Parameters, item.Kinds);
        AssertSnapshotsEqual(expected, actual);
    }

    private static async Task<RowSnapshot> ReadRowAsync(
        DbConnection connection,
        string sql,
        IReadOnlyList<object?>? parameters = null,
        string[]? kinds = null)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var names = new string[reader.FieldCount];
        var types = new string[reader.FieldCount];
        var values = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var kind = KindAt(kinds, i);
            var value = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            names[i] = reader.GetName(i).ToUpperInvariant();
            types[i] = NormalizeType(reader.GetDataTypeName(i), value?.GetType(), kind);
            values[i] = await ReadCanonicalValueAsync(reader, i, types[i], kind, value);
        }

        Assert.False(await reader.ReadAsync());
        return new RowSnapshot(names, types, values);
    }

    private static RowSnapshot ReadRowSync(
        DbConnection connection,
        string sql,
        IReadOnlyList<object?>? parameters = null,
        string[]? kinds = null)
    {
        using var command = CreateCommand(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());

        var names = new string[reader.FieldCount];
        var types = new string[reader.FieldCount];
        var values = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var kind = KindAt(kinds, i);
            var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
            names[i] = reader.GetName(i).ToUpperInvariant();
            types[i] = NormalizeType(reader.GetDataTypeName(i), value?.GetType(), kind);
            values[i] = ReadCanonicalValue(reader, i, types[i], kind, value);
        }

        Assert.False(reader.Read());
        return new RowSnapshot(names, types, values);
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        string sql,
        IReadOnlyList<object?>? parameters)
    {
        var command = connection.CreateCommand();
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
        return command;
    }

    private static async Task<string> ReadCanonicalValueAsync(
        DbDataReader reader,
        int ordinal,
        string normalizedType,
        string kind,
        object? value)
    {
        if (value is null) return "<NULL>";
        if (kind is "binary" or "lob-binary")
        {
            var bytes = value as byte[] ?? throw new InvalidOperationException("Parity binary value is not a byte array.");
            return "0x" + Convert.ToHexString(bytes);
        }

        if (kind == "lob-text")
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (kind == "xml")
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return StripXml(text);
        }

        return Canonical(normalizedType, kind, value);
    }

    private static string ReadCanonicalValue(
        DbDataReader reader,
        int ordinal,
        string normalizedType,
        string kind,
        object? value)
    {
        if (value is null) return "<NULL>";
        if (kind is "binary" or "lob-binary")
        {
            var bytes = value as byte[] ?? throw new InvalidOperationException("Parity binary value is not a byte array.");
            return "0x" + Convert.ToHexString(bytes);
        }

        if (kind == "lob-text")
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (kind == "xml")
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return StripXml(text);
        }

        return Canonical(normalizedType, kind, value);
    }

    private static string Canonical(string normalizedType, string kind, object value) => kind switch
    {
        "boolean" => value is bool flag ? (flag ? "true" : "false") : value.ToString()!.Trim().ToLowerInvariant(),
        "numeric" => NormalizeNumeric(value),
        "temporal" => NormalizeTemporal(normalizedType, value),
        "text" or "lob-text" => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => Normalize(value),
    };

    private static string NormalizeNumeric(object value)
    {
        if (value is decimal number) return number.ToString(CultureInfo.InvariantCulture);
        if (value is float single) return single.ToString("R", CultureInfo.InvariantCulture);
        if (value is double doubleValue) return doubleValue.ToString("R", CultureInfo.InvariantCulture);
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        return text.ToUpperInvariant() switch
        {
            "INF" or "INFINITY" => "Infinity",
            "-INF" or "-INFINITY" => "-Infinity",
            "SNAN" => "NaN",
            "NAN" => "NaN",
            _ => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed.ToString(CultureInfo.InvariantCulture)
                : text,
        };
    }

    private static string NormalizeTemporal(string normalizedType, object value)
    {
        if (normalizedType == "DATE" && value is DateTime date)
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (normalizedType == "TIME" && value is TimeSpan time)
            return time.ToString("c", CultureInfo.InvariantCulture);
        if (normalizedType == "TIMESTAMP" && value is DateTime timestamp)
            return timestamp.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return Normalize(value);
    }

    private static string StripXml(string text)
    {
        text = text.TrimStart('\ufeff');
        var start = text.IndexOf("?>", StringComparison.Ordinal);
        if (text.StartsWith("<?xml", StringComparison.Ordinal) && start >= 0)
            text = text[(start + 2)..];
        return text.Trim();
    }

    private static string NormalizeType(string type, Type? valueType, string kind)
    {
        var normalized = type.Trim().ToUpperInvariant();
        return kind switch
        {
            "boolean" => "BOOLEAN",
            "binary" => "BINARY",
            "lob-binary" => "BLOB",
            "lob-text" => normalized.Contains("DBCLOB", StringComparison.Ordinal) ? "DBCLOB" : "CLOB",
            "text" => "STRING",
            "xml" => "XML",
            "temporal" => normalized,
            "integer" => normalized,
            "numeric" => NormalizeNumericType(normalized, valueType),
            _ => normalized,
        };
    }

    private static string NormalizeNumericType(string type, Type? valueType)
    {
        if (type.Contains("DECIMAL", StringComparison.Ordinal)) return "DECIMAL";
        if (type.Contains("DECFLOAT", StringComparison.Ordinal)) return "DECFLOAT";
        if (valueType == typeof(float)) return "REAL";
        if (valueType == typeof(double)) return "DOUBLE";
        return type switch
        {
            "REAL" => "REAL",
            "DOUBLE" or "DOUBLE PRECISION" or "FLOAT" => "DOUBLE",
            _ => type,
        };
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

    private static string KindAt(string[]? kinds, int index) =>
        kinds is not null && index < kinds.Length ? kinds[index] : "strict";

    private static void AssertSnapshotsEqual(RowSnapshot expected, RowSnapshot actual)
    {
        Assert.Equal(expected.Names, actual.Names);
        Assert.Equal(expected.Types, actual.Types);
        Assert.Equal(expected.Values, actual.Values);
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

    private static async Task ExecuteAsync(
        Func<Task<Db2AdoConnection>> openNative,
        string sql,
        IReadOnlyList<object?>? parameters = null)
    {
        await using var connection = await openNative();
        await using var command = CreateCommand(connection, sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(
        Func<IBMConnection> openIbm,
        string sql,
        IReadOnlyList<object?>? parameters = null)
    {
        await using var connection = openIbm();
        await using var command = CreateCommand(connection, sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetupTablesAsync(IReadOnlyDictionary<string, string> definitions)
    {
        await using var connection = await OpenNativeAsync();
        foreach (var (name, definition) in definitions)
        {
            await DropTableAsync(connection, name);
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE {name} {definition}";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task DropTablesAsync(IReadOnlyList<string> names)
    {
        await using var connection = await OpenNativeAsync();
        foreach (var name in names) await DropTableAsync(connection, name);
    }

    private static async Task DropTableAsync(DbConnection connection, string name)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE {name}";
            await command.ExecuteNonQueryAsync();
        }
        catch (Db2AdoException)
        {
            // The table did not exist.
        }
    }

    private static async Task<(int? Code, string? State)> ErrorIbmAsync(
        DbConnection connection,
        string sql)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (IsQueryLike(sql))
            {
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) { }
            }
            else
            {
                await command.ExecuteNonQueryAsync();
            }
            Assert.Fail($"oracle did not raise for {sql}");
            return (null, null);
        }
        catch (Exception error)
        {
            var ibmError = FindIbmError(error);
            if (ibmError is null) throw;
            if (ibmError.Errors.Count == 0) return (null, ibmError.SqlState);
            var first = ibmError.Errors[0];
            return (first.NativeError, first.SQLState);
        }
    }

    private static async Task<(int? Code, string? State)> ErrorNativeAsync(
        DbConnection connection,
        string sql)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (IsQueryLike(sql))
            {
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) { }
            }
            else
            {
                await command.ExecuteNonQueryAsync();
            }
            Assert.Fail($"native driver did not raise for {sql}");
            return (null, null);
        }
        catch (Exception error)
        {
            var nativeError = FindNativeError(error);
            if (nativeError is null) throw;
            return (nativeError.SqlCode, nativeError.SqlState);
        }
    }

    private static IBMException? FindIbmError(Exception error)
    {
        if (error is IBMException direct) return direct;
        if (error is AggregateException aggregate)
            return aggregate.Flatten().InnerExceptions.OfType<IBMException>().FirstOrDefault();
        return error.InnerException as IBMException;
    }

    private static Db2AdoException? FindNativeError(Exception error)
    {
        if (error is Db2AdoException direct) return direct;
        if (error is AggregateException aggregate)
            return aggregate.Flatten().InnerExceptions.OfType<Db2AdoException>().FirstOrDefault();
        return error.InnerException as Db2AdoException;
    }

    private static bool IsQueryLike(string sql)
    {
        var keyword = sql.TrimStart().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
        return keyword is not ("INSERT" or "UPDATE" or "DELETE" or "MERGE");
    }

    private static IBMConnection OpenIbm()
    {
        var connection = new IBMConnection(
            $"Server={Required("DB2_LIVE_TEST_HOST")}:{Required("DB2_LIVE_TEST_PORT")};" +
            $"Database={Required("DB2_LIVE_TEST_DATABASE")};" +
            $"UID={Required("DB2_LIVE_TEST_USER")};PWD={Required("DB2_LIVE_TEST_PASSWORD")};Pooling=false;");
        connection.Open();
        return connection;
    }

    private static async Task<Db2AdoConnection> OpenNativeAsync()
    {
        var connection = new Db2AdoConnection(Required("DB2_TEST_DSN"));
        await connection.OpenAsync();
        return connection;
    }

    private static Db2AdoConnection OpenNativeSync()
    {
        var connection = new Db2AdoConnection(Required("DB2_TEST_DSN"));
        connection.Open();
        return connection;
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Set {name}.");

    private sealed record Case(string Sql, string[] Kinds, object?[]? Parameters = null);

    private sealed record RowSnapshot(string[] Names, string[] Types, string[] Values);
}
