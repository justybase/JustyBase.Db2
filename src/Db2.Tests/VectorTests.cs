using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Protocol;
using Xunit;

namespace JustyBase.Db2.Tests;

/// <summary>
/// Executes the self-contained JDC v1 vector copy that ships with the driver.
/// The harness publishes the same file to every driver; this test never needs
/// the private harness.
/// </summary>
public sealed class VectorTests
{
    private static readonly string VectorPath = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "JDC", "JDC-v1.vectors.json");

    [Fact]
    public void VectorCopyIsSelfConsistent()
    {
        var text = ReadVectorText();
        var bytes = Encoding.UTF8.GetBytes(text);
        var recorded = File.ReadAllText(VectorPath + ".sha256").Trim();
        Assert.Equal(recorded, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.DoesNotContain("db2-conformance", text, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        Assert.Equal("jdc-vectors/v1", root.GetProperty("schema").GetString());
        Assert.Equal("v1", root.GetProperty("contract").GetProperty("version").GetString());

        var contract = File.ReadAllText(FindUp("docs", "DRIVER-CONTRACT.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var newline = contract.IndexOf('\n');
        Assert.True(newline > 0, "contract has no header line");
        var contractHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contract[(newline + 1)..]))).ToLowerInvariant();
        Assert.Equal(contractHash, root.GetProperty("contract").GetProperty("sha256").GetString());
    }

    [Fact]
    public void DsnVectorsMatchTheParser()
    {
        using var document = JsonDocument.Parse(ReadVectorText());
        var dsn = document.RootElement.GetProperty("dsn");

        foreach (var entry in dsn.GetProperty("accept").EnumerateArray())
        {
            var id = entry.GetProperty("id").GetString()!;
            var config = Db2Config.Parse(entry.GetProperty("input").GetString()!);
            foreach (var property in entry.GetProperty("expect").EnumerateObject())
                AssertDsnValue(config, property, id);
        }

        foreach (var entry in dsn.GetProperty("reject").EnumerateArray())
        {
            var input = entry.GetProperty("input").GetString()!;
            var error = Assert.Throws<Db2ConfigurationException>(() => Db2Config.Parse(input));
            Assert.Equal(Db2ErrorKind.Configuration, Assert.IsAssignableFrom<IDb2Error>(error).ErrorKind);
        }
    }

    [Fact]
    public void ErrorVectorsMatchClassificationAndOutcome()
    {
        using var document = JsonDocument.Parse(ReadVectorText());
        foreach (var entry in document.RootElement.GetProperty("errors").EnumerateArray())
        {
            var input = entry.GetProperty("input");
            var expect = entry.GetProperty("expect");
            var error = CreateError(input);
            var db2 = Assert.IsAssignableFrom<IDb2Error>(error);

            Assert.Equal(expect.GetProperty("category").GetString(), Canonical(db2.ErrorKind));
            Assert.Equal(expect.GetProperty("outcome").GetString(), Outcome(db2.Outcome));
            if (expect.TryGetProperty("sql_code", out var sqlCode))
                Assert.Equal(sqlCode.GetInt32(), db2.SqlCode);
            if (expect.TryGetProperty("sql_state", out var sqlState))
                Assert.Equal(sqlState.GetString(), db2.SqlState);
            if (expect.TryGetProperty("message_contains", out var fragments))
                foreach (var fragment in fragments.EnumerateArray())
                    Assert.Contains(fragment.GetString()!, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LimitVectorsMatchTheFixedDefaults()
    {
        using var document = JsonDocument.Parse(ReadVectorText());
        foreach (var entry in document.RootElement.GetProperty("limits").EnumerateArray())
        {
            var field = entry.GetProperty("field").GetString()!;
            var expected = entry.GetProperty("value").GetInt64();
            var actual = field switch
            {
                "max_dss_size" => Db2ProtocolLimits.MaxDssSize,
                "max_ddm_object_size" => Db2ProtocolLimits.MaxDdmObjectSize,
                "max_reply_chain_bytes" => Db2ProtocolLimits.MaxReplyChainBytes,
                "max_reply_chain_packets" => Db2ProtocolLimits.MaxReplyChainPackets,
                "max_materialized_lob_size" => Db2ProtocolLimits.MaxMaterializedLobBytes,
                "max_materialized_result_bytes" => Db2ProtocolLimits.MaxMaterializedResultBytes,
                "max_streamed_lob_bytes" => Db2ProtocolLimits.MaxStreamedLobBytes,
                "lob_chunk_bytes" => Db2ProtocolLimits.LobChunkBytes,
                _ => throw new InvalidOperationException($"unmapped limit field {field}"),
            };
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void BoundVectorsMatchOptionValidation()
    {
        using var document = JsonDocument.Parse(ReadVectorText());
        foreach (var entry in document.RootElement.GetProperty("bounds").EnumerateArray())
        {
            var surface = entry.GetProperty("surface").GetString()!;
            var field = entry.GetProperty("field").GetString()!;
            var min = entry.GetProperty("min").GetInt32();
            if (min > 0)
                Assert.ThrowsAny<ArgumentException>(() => ValidateBound(surface, field, min - 1));
            ValidateBound(surface, field, min);
            if (entry.TryGetProperty("max", out var max))
            {
                Assert.ThrowsAny<ArgumentException>(() => ValidateBound(surface, field, max.GetInt32() + 1));
                ValidateBound(surface, field, max.GetInt32());
            }
        }
    }

    [Fact]
    public void DefaultVectorsMatchOptionTypes()
    {
        using var document = JsonDocument.Parse(ReadVectorText());
        var pool = new Db2PoolOptions();
        var batch = new Db2BatchOptions();
        var import = new Db2AdoImportOptions();

        foreach (var entry in document.RootElement.GetProperty("defaults").EnumerateArray())
        {
            var surface = entry.GetProperty("surface").GetString()!;
            var field = entry.GetProperty("field").GetString()!;
            var value = entry.GetProperty("value");
            switch (surface, field)
            {
                case ("pool", "max_connections"): Assert.Equal(value.GetInt32(), pool.MaxConnections); break;
                case ("pool", "max_waiters"): Assert.Equal(value.GetInt32(), pool.MaxWaiters); break;
                case ("pool", "acquire_timeout_ms"): Assert.Equal(value.GetInt64(), (long)pool.AcquireTimeout.TotalMilliseconds); break;
                case ("pool", "connect_timeout_ms"): Assert.Equal(value.GetInt64(), (long)pool.ConnectTimeout.TotalMilliseconds); break;
                case ("pool", "idle_timeout_ms"): Assert.Equal(value.GetInt64(), (long)pool.IdleTimeout.TotalMilliseconds); break;
                case ("pool", "max_lifetime_ms"): Assert.Equal(value.GetInt64(), (long)pool.MaxLifetime.TotalMilliseconds); break;
                case ("pool", "reap_interval_ms"): Assert.Equal(value.GetInt64(), (long)pool.ReapInterval.TotalMilliseconds); break;
                case ("batch", "max_rows"): Assert.Equal(value.GetInt32(), batch.MaxRows); break;
                case ("batch", "max_encoded_bytes"): Assert.Equal(value.GetInt64(), batch.MaxEncodedBytes); break;
                case ("import", "max_batch_rows"): Assert.Equal(value.GetInt32(), import.BatchSize); break;
                case ("import", "max_batch_bytes"): Assert.Equal(value.GetInt64(), import.MaxBatchBytes); break;
                case ("import", "notify_after_rows"): Assert.Equal(value.GetInt32(), import.NotifyAfterRows); break;
                case ("import", "table_lock"): Assert.Equal(value.GetBoolean(), import.TableLock); break;
                case ("import", "policy"):
                    Assert.Equal(value.GetString(), import.CommitEveryRows == 0 ? "atomic" : "commit-every-rows");
                    break;
                default: throw new InvalidOperationException($"unmapped default {surface}.{field}");
            }
        }
    }

    private static void ValidateBound(string surface, string field, int value)
    {
        switch (surface)
        {
            case "pool":
                new Db2PoolOptions(field == "max_connections" ? value : 10)
                {
                    MaxWaiters = field == "max_waiters" ? value : 100,
                }.Validate();
                break;
            case "batch":
                new Db2BatchOptions
                {
                    MaxRows = field == "max_rows" ? value : 256,
                    MaxEncodedBytes = field == "max_encoded_bytes" ? value : 8 * 1024 * 1024,
                }.Validate();
                break;
            case "import":
                new Db2AdoImportOptions
                {
                    BatchSize = field == "max_batch_rows" ? value : 256,
                    MaxBatchBytes = field == "max_batch_bytes" ? value : 8 * 1024 * 1024,
                    NotifyAfterRows = field == "notify_after_rows" ? value : 10_000,
                }.Validate();
                break;
            default:
                throw new InvalidOperationException($"unmapped bound surface {surface}");
        }
    }

    private static void AssertDsnValue(Db2Config config, JsonProperty property, string id)
    {
        switch (property.Name)
        {
            case "host": Assert.Equal(property.Value.GetString(), config.Host); break;
            case "port": Assert.Equal(property.Value.GetInt32(), config.Port); break;
            case "database": Assert.Equal(property.Value.GetString(), config.Database); break;
            case "user": Assert.Equal(property.Value.GetString(), config.User); break;
            case "password": Assert.Equal(property.Value.GetString(), config.Password); break;
            case "use_ssl": Assert.Equal(property.Value.GetBoolean(), config.UseSsl); break;
            case "timeout_ms": Assert.Equal(property.Value.GetInt64(), (long)config.Timeout.TotalMilliseconds); break;
            case "block_size": Assert.Equal(property.Value.GetInt32(), config.BlockSize); break;
            case "security_mechanism": Assert.Equal(property.Value.GetInt32(), (int)config.SecurityMechanism); break;
            case "current_schema": Assert.Equal(property.Value.GetString(), config.CurrentSchema); break;
            case "ssl_root_ca_path": Assert.Equal(property.Value.GetString(), config.SslRootCaPath); break;
            case "kerberos_spn": Assert.Equal(property.Value.GetString(), config.KerberosSpn); break;
            case "kerberos_realm": Assert.Equal(property.Value.GetString(), config.KerberosRealm); break;
            case "krb5_principal": Assert.Equal(property.Value.GetString(), config.KerberosPrincipal); break;
            case "krb5_config_file": Assert.Equal(property.Value.GetString(), config.Krb5ConfigFile); break;
            case "krb5_keytab_file": Assert.Equal(property.Value.GetString(), config.Krb5KeytabFile); break;
            case "krb5_ccache_file": Assert.Equal(property.Value.GetString(), config.Krb5CcacheFile); break;
            case "gssapi_token_file": Assert.Equal(property.Value.GetString(), config.GssapiTokenFile); break;
            case "client_appl_name": Assert.Equal(property.Value.GetString(), config.ClientApplName); break;
            case "client_corr_token": Assert.Equal(property.Value.GetString(), config.ClientCorrToken); break;
            case "params":
                foreach (var parameter in property.Value.EnumerateObject())
                {
                    Assert.True(config.Parameters.TryGetValue(parameter.Name, out var actual),
                        $"{id}: missing parameter {parameter.Name}");
                    Assert.Equal(parameter.Value.GetString(), actual);
                }
                break;
            default: throw new InvalidOperationException($"{id}: unmapped DSN field {property.Name}");
        }
    }

    private static Exception CreateError(JsonElement input)
    {
        var message = input.GetProperty("message").GetString()!;
        return input.GetProperty("kind").GetString() switch
        {
            "sql" => new Db2ServerErrorException(
                $"SQLCODE={input.GetProperty("sql_code").GetInt32()} SQLSTATE={input.GetProperty("sql_state").GetString()} {message}"),
            "configuration" => new Db2ConfigurationException(message),
            "pool-timeout" => new Db2PoolTimeoutException(),
            "pool-exhausted" => new Db2PoolExhaustedException(),
            "timeout" => new Db2CommandTimeoutException(TimeSpan.FromSeconds(30)),
            "server-response" => new Db2ServerErrorException(message),
            var kind => throw new InvalidOperationException($"unmapped error kind {kind}"),
        };
    }

    private static string Canonical(Db2ErrorKind kind) => kind switch
    {
        Db2ErrorKind.Configuration => "configuration",
        Db2ErrorKind.ConnectionClosed => "connection-closed",
        Db2ErrorKind.DatabaseNotFound => "database-not-found",
        Db2ErrorKind.Authentication => "authentication",
        Db2ErrorKind.Authorization => "authorization",
        Db2ErrorKind.SqlError => "sql-server",
        Db2ErrorKind.ServerError => "server-response",
        Db2ErrorKind.ParameterConversion => "parameter-conversion",
        Db2ErrorKind.BatchExecution => "batch-execution",
        Db2ErrorKind.Unsupported => "unsupported",
        Db2ErrorKind.PoolTimeout => "pool-timeout",
        Db2ErrorKind.PoolExhausted => "pool-exhausted",
        Db2ErrorKind.Transport => "transport",
        Db2ErrorKind.Protocol => "protocol",
        Db2ErrorKind.ResourceLimit => "resource-limit",
        Db2ErrorKind.OperationCancelled => "operation-cancelled",
        Db2ErrorKind.Timeout => "timeout",
        _ => "other",
    };

    private static string Outcome(Db2OperationOutcome outcome) => outcome switch
    {
        Db2OperationOutcome.NotStarted => "not-started",
        Db2OperationOutcome.Confirmed => "confirmed",
        _ => "unknown",
    };

    private static string ReadVectorText()
    {
        Assert.True(File.Exists(VectorPath), $"missing vector copy at {VectorPath}");
        return File.ReadAllText(VectorPath).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string FindUp(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Could not locate {Path.Combine(parts)} above the test output directory.");
    }
}
