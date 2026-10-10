using System.Data.Common;
using System.Globalization;

namespace JustyBase.Db2.AdoNet;

/// <summary>Builder for connection strings accepted by <see cref="Db2AdoConnection"/>.</summary>
public class Db2AdoConnectionStringBuilder : DbConnectionStringBuilder
{
    public string Server { get => GetString("Server"); set => this["Server"] = value; }
    public string Database { get => GetString("Database"); set => this["Database"] = value; }
    public string UserID { get => GetString("UID"); set => this["UID"] = value; }
    public string Password { get => GetString("PWD"); set => this["PWD"] = value; }
    public bool Pooling { get => GetBoolean("Pooling", true); set => this["Pooling"] = value; }
    public int MaxPoolSize { get => GetInt32("MaxPoolSize", 10); set => this["MaxPoolSize"] = value; }
    public int ConnectTimeout { get => GetInt32("Connect_Timeout", 30); set => SetConnectTimeout(value); }
    public bool UseSSL { get => GetBoolean("SSL", false); set => this["SSL"] = value; }

    public int MaxWaiters { get => GetInt32("max_waiters", 100); set => this["max_waiters"] = value; }
    public int PoolAcquireTimeout { get => GetInt32("pool_acquire_timeout", 30); set => this["pool_acquire_timeout"] = value; }
    public int PoolConnectTimeout { get => GetInt32("pool_connect_timeout", 10); set => this["pool_connect_timeout"] = value; }
    public int PoolIdleTimeout { get => GetInt32("pool_idle_timeout", 300); set => this["pool_idle_timeout"] = value; }
    public int PoolMaxLifetime { get => GetInt32("pool_max_lifetime", 1800); set => this["pool_max_lifetime"] = value; }

    private string GetString(string key) => TryGetValue(key, out var value)
        ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        : "";

    private bool GetBoolean(string key, bool fallback) => TryGetValue(key, out var value)
        ? Convert.ToBoolean(value, CultureInfo.InvariantCulture)
        : fallback;

    private int GetInt32(string key, int fallback) => TryGetValue(key, out var value)
        ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
        : fallback;

    private void SetConnectTimeout(int seconds)
    {
        if (seconds is < 0 or > 32_767) throw new ArgumentOutOfRangeException(nameof(seconds), "Connect_Timeout must be from 0 through 32767 seconds.");
        this["Connect_Timeout"] = seconds;
    }
}
