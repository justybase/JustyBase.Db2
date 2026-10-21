using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JustyBase.Db2;

/// <summary>Connection and DRDA session settings parsed from a Db2 DSN.</summary>
public sealed class Db2Config
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 50000;
    public string Database { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseSsl { get; set; }
    public string SslRootCaPath { get; set; } = "";
    public string SslClientCertPath { get; set; } = "";
    public string SslClientKeyPath { get; set; } = "";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public int BlockSize { get; set; } = 65535;
    public ushort SecurityMechanism { get; set; }
    public string KerberosSpn { get; set; } = "";
    public string KerberosRealm { get; set; } = "";
    /// <summary>Client Kerberos principal (user@REALM). Required with <see cref="Krb5KeytabFile"/>; optional with a ccache.</summary>
    public string KerberosPrincipal { get; set; } = "";
    public string Krb5ConfigFile { get; set; } = "";
    public string Krb5KeytabFile { get; set; } = "";
    public string Krb5CcacheFile { get; set; } = "";
    public string GssapiTokenFile { get; set; } = "";
    public string ClientApplName { get; set; } = "";
    public string ClientWrkstnName { get; set; } = "";
    public string ClientUserid { get; set; } = "";
    public string ClientAcctng { get; set; } = "";
    public string ClientCorrToken { get; set; } = "";
    public Dictionary<string, string> Parameters { get; } = new(StringComparer.Ordinal);

    public static Db2Config Parse(string dsn)
    {
        ArgumentNullException.ThrowIfNull(dsn);
        dsn = dsn.Trim();
        if (dsn.Length == 0) throw new Db2ConfigurationException("Invalid connection string: empty DSN.");
        if (dsn.StartsWith("db2://", StringComparison.OrdinalIgnoreCase) || dsn.StartsWith("db2s://", StringComparison.OrdinalIgnoreCase))
            return ParseUrl(dsn);
        return ParseKeyValue(dsn);
    }

    private static Db2Config ParseUrl(string dsn)
    {
        // Uri normalizes a stray '%' by re-escaping it, so validate the raw URL first
        // (Go's url.Parse and the other ports reject malformed percent escapes).
        for (var index = 0; index < dsn.Length; index++)
        {
            if (dsn[index] == '%' && (index + 2 >= dsn.Length || !Uri.IsHexDigit(dsn[index + 1]) || !Uri.IsHexDigit(dsn[index + 2])))
                throw new Db2ConfigurationException("Invalid connection string: malformed percent escape.");
        }
        Uri uri;
        try { uri = new Uri(dsn, UriKind.Absolute); }
        catch (UriFormatException ex) { throw new Db2ConfigurationException($"Invalid connection string: {RedactDsn(ex.Message)}", ex); }
        if (uri.Scheme is not ("db2" or "db2s"))
            throw new Db2ConfigurationException("Invalid connection string: expected scheme 'db2' or 'db2s'.");

        var config = new Db2Config { UseSsl = uri.Scheme == "db2s" };
        // Uri.Host serializes an IPv6 literal with brackets; store the bare address
        // that a socket can use (the key/value Server= path already does this).
        if (uri.Host.Length > 0) config.Host = StripHostBrackets(uri.Host);
        if (!uri.IsDefaultPort)
        {
            if (uri.Port is < 1 or > 65535) throw new Db2ConfigurationException($"Invalid connection string: invalid port {uri.Port}.");
            config.Port = uri.Port;
        }

        var rawPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        var database = rawPath.TrimStart('/');
        try { database = DecodeUriComponent(database, plusAsSpace: false); }
        catch (FormatException ex) { throw new Db2ConfigurationException("Invalid connection string: malformed database escape.", ex); }
        if (database.Length == 0) throw new Db2ConfigurationException("Invalid connection string: missing database name.");
        config.Database = database;

        var userInfo = uri.GetComponents(UriComponents.UserInfo, UriFormat.UriEscaped);
        if (userInfo.Length > 0)
        {
            var separator = userInfo.IndexOf(':');
            try
            {
                config.User = DecodeUriComponent(separator < 0 ? userInfo : userInfo[..separator], plusAsSpace: false);
                if (separator >= 0) config.Password = DecodeUriComponent(userInfo[(separator + 1)..], plusAsSpace: false);
            }
            catch (FormatException ex) { throw new Db2ConfigurationException("Invalid connection string: malformed user-info escape.", ex); }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var query = uri.GetComponents(UriComponents.Query, UriFormat.UriEscaped);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = DecodeUriComponent(equals < 0 ? pair : pair[..equals], plusAsSpace: true);
            if (!seen.Add(key)) continue;
            var value = DecodeUriComponent(equals < 0 ? "" : pair[(equals + 1)..], plusAsSpace: true);
            ApplyParam(config, key, value);
        }
        return config;
    }

    private static Db2Config ParseKeyValue(string dsn)
    {
        var config = new Db2Config();
        foreach (var item in dsn.Split(';'))
        {
            var pair = item.Trim();
            if (pair.Length == 0) continue;
            var equals = pair.IndexOf('=');
            if (equals < 0) continue;
            ApplyParam(config, pair[..equals].Trim(), pair[(equals + 1)..].Trim());
        }
        return config;
    }

    private static void ApplyParam(Db2Config c, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "host": case "hostname": c.Host = value; break;
            case "server":
                var (serverHost, serverPort) = ParseServerEndpoint(value);
                c.Host = serverHost;
                if (serverPort.HasValue) c.Port = serverPort.Value;
                break;
            case "port": if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535) c.Port = port; break;
            case "database": case "dbname": case "db": c.Database = value; break;
            case "user": case "uid": case "username": c.User = value; break;
            case "password": case "pwd": c.Password = value; break;
            case "authentication":
                c.SecurityMechanism = value.Trim().ToUpperInvariant() switch
                {
                    "SERVER" => 3,
                    "SERVER_ENCRYPT" => 9,
                    "KERBEROS" or "GSSPLUGIN" => 11,
                    _ => throw UnsupportedAuthentication(value),
                };
                break;
            case "security":
                if (!value.Equals("SSL", StringComparison.OrdinalIgnoreCase))
                    throw new Db2ConfigurationException("Only Security=SSL is supported; use a TLS connection or omit the Security keyword.");
                c.UseSsl = true;
                break;
            case "ssl": case "use_ssl": case "usessl":
                c.UseSsl = IsTrue(value); if (c.UseSsl && c.Port == 50000) c.Port = 50001; break;
            case "ssl_root_ca": case "ssl_ca": case "sslrootcapath": case "ssl_ca_path":
            case "sslservercertificate": case "ssl_server_certificate":
                c.SslRootCaPath = value; c.UseSsl = true; break;
            case "ssl_client_cert_path": case "ssl_client_cert": case "sslcert": case "cert": c.SslClientCertPath = value; c.UseSsl = true; break;
            case "ssl_client_key_path": case "ssl_client_key": case "sslkey": case "key": c.SslClientKeyPath = value; c.UseSsl = true; break;
            case "timeout": case "connect_timeout": if (TryParseDuration(value, out var timeout)) c.Timeout = timeout; break;
            case "block_size": case "blocksize": case "qryblksz": if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size is >= 1024 and <= 1048576) c.BlockSize = size; break;
            case "security_mechanism": case "secmec": case "auth_mechanism": case "auth":
                if (!TryParseSecurityMechanism(value, out var secmec)) throw UnsupportedAuthentication(value);
                c.SecurityMechanism = secmec;
                break;
            case "spn": case "service_principal": case "server_principal": case "kerberos_spn": case "kerberosspn": c.KerberosSpn = value; break;
            case "realm": case "krb5_realm": case "krb5realm": case "kerberos_realm": case "kerberosrealm": c.KerberosRealm = value; break;
            case "krb5_principal": case "krb5principal": case "kerberos_principal": case "kerberosprincipal": case "client_principal": c.KerberosPrincipal = value; break;
            case "krb5_config": case "krb5config": case "krb5_conf": c.Krb5ConfigFile = value; break;
            case "krb5_keytab": case "krb5keytab": case "keytab": c.Krb5KeytabFile = value; break;
            case "krb5_ccache": case "krb5ccache": case "ccache": c.Krb5CcacheFile = value; break;
            case "gssapi_token_file": case "gssapitokenfile": case "gssapi_token": c.GssapiTokenFile = value; break;
            case "client_applname": case "applname": case "application_name": case "appname": c.ClientApplName = value; break;
            case "client_wrkstnname": case "wrkstnname": case "workstation_name": case "workstation": c.ClientWrkstnName = value; break;
            case "client_userid": case "client_user": case "end_user": c.ClientUserid = value; break;
            case "client_acctng": case "acctng": case "accounting": c.ClientAcctng = value; break;
            case "client_corr_token": case "corr_token": case "correlation_token": c.ClientCorrToken = value; break;
            default:
                // A misspelled Kerberos option must not be ignored: that could silently change which credentials are used.
                if (key.StartsWith("krb5", StringComparison.OrdinalIgnoreCase) || key.StartsWith("kerberos", StringComparison.OrdinalIgnoreCase) || key.StartsWith("gssapi", StringComparison.OrdinalIgnoreCase))
                    throw new Db2ConfigurationException($"Unknown Kerberos option '{key}'. Supported: spn, realm, krb5_principal, krb5_keytab, krb5_ccache, krb5_config, gssapi_token_file.");
                c.Parameters[key] = value;
                break;
        }
    }

    private static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static string StripHostBrackets(string host) =>
        host.Length >= 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    private static (string Host, int? Port) ParseServerEndpoint(string server)
    {
        var text = server.Trim();
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            var closing = text.IndexOf(']');
            if (closing < 0) throw new Db2ConfigurationException("Invalid bracketed IPv6 Server value.");
            var host = text[1..closing];
            var suffix = text[(closing + 1)..];
            if (suffix.Length == 0) return (host, null);
            if (suffix[0] != ':' || !int.TryParse(suffix[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ipv6Port) || ipv6Port is < 1 or > 65535)
                throw new Db2ConfigurationException("Invalid port in Server value.");
            return (host, ipv6Port);
        }
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            if (port is < 1 or > 65535) throw new Db2ConfigurationException("Server port must be between 1 and 65535.");
            return (text[..colon], port);
        }
        return (text, null);
    }

    private static bool TryParseSecurityMechanism(string value, out ushort mechanism)
    {
        mechanism = value.Trim().ToLowerInvariant() switch
        {
            "server" or "usridpwd" or "plaintext" or "secmec3" or "3" => 3,
            "usrencpwd" or "encrypted_password_security" or "secmec7" or "7" => 7,
            "server_encrypt" or "eusridpwd" or "dh_encrypted_password" or "secmec9" or "9" => 9,
            "kerberos" or "gssapi" or "gssplugin" or "secmec11" or "11" => 11,
            "0" => 0,
            _ => ushort.MaxValue,
        };
        if (mechanism != ushort.MaxValue) return true;
        if (!ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out mechanism)) return false;
        return mechanism is 0 or 3 or 7 or 9 or 11;
    }

    private static Db2ConfigurationException UnsupportedAuthentication(string value) =>
        new($"Unsupported DB2 authentication mode '{value}'. Supported modes are Authentication=SERVER (3), security_mechanism=7 (USRENCPWD), Authentication=SERVER_ENCRYPT / security_mechanism=9 (EUSRIDPWD), and security_mechanism=11 (Kerberos/GSSAPI with krb5_ccache, krb5_keytab or gssapi_token_file).");

    private static bool TryParseDuration(string input, out TimeSpan timeout)
    {
        timeout = default;
        var value = input.Trim();
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            try { timeout = TimeSpan.FromSeconds(seconds); return true; } catch (OverflowException) { return false; }
        }
        if (value.Length == 0) return false;
        var sign = 1d;
        if (value[0] is '+' or '-') { if (value[0] == '-') sign = -1; value = value[1..]; }
        var totalMilliseconds = 0d;
        var position = 0;
        while (position < value.Length)
        {
            var numberStart = position;
            var decimalSeen = false;
            while (position < value.Length && (char.IsAsciiDigit(value[position]) || value[position] == '.' && !decimalSeen))
            {
                if (value[position] == '.') decimalSeen = true;
                position++;
            }
            if (position == numberStart || value[numberStart..position] == "." || !double.TryParse(value[numberStart..position], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
            var unitStart = position;
            while (position < value.Length && char.IsAsciiLetter(value[position])) position++;
            if (position < value.Length && value[position] is 'µ' or 'μ') position++;
            var multiplier = value[unitStart..position] switch
            {
                "h" => 3_600_000d, "m" => 60_000d, "s" => 1_000d, "ms" => 1d,
                "us" or "µs" or "μs" => 0.001d, "ns" => 0.000001d, _ => double.NaN,
            };
            if (double.IsNaN(multiplier)) return false;
            totalMilliseconds += number * multiplier;
        }
        totalMilliseconds *= sign;
        if (!double.IsFinite(totalMilliseconds) || Math.Abs(totalMilliseconds) > TimeSpan.MaxValue.TotalMilliseconds) return false;
        timeout = TimeSpan.FromMilliseconds(Math.Truncate(totalMilliseconds));
        return true;
    }

    private static string DecodeUriComponent(string value, bool plusAsSpace)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])))
                throw new FormatException("Malformed percent escape.");
        }
        return Uri.UnescapeDataString(plusAsSpace ? value.Replace('+', ' ') : value);
    }

    /// <summary>Masks credentials in a URL or key-value connection string for safe diagnostics.</summary>
    public static string RedactDsn(string input)
    {
        var redacted = MaskUrlCredential(input);
        return Regex.Replace(redacted, @"(\b(?:password|pwd)\s*=\s*)[^;]*", "$1******", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string MaskUrlCredential(string input)
    {
        // Take the last '@' inside the authority and the last ':' before it, so a password
        // that itself contains '@' is masked completely instead of only up to the first one.
        var scheme = input.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return input;
        var authorityStart = scheme + 3;
        var authorityEnd = input.Length;
        foreach (var terminator in new[] { '/', '?', '#' })
        {
            var index = input.IndexOf(terminator, authorityStart);
            if (index >= 0 && index < authorityEnd) authorityEnd = index;
        }
        var authority = input[authorityStart..authorityEnd];
        var at = authority.LastIndexOf('@');
        if (at < 0) return input;
        var colon = authority.LastIndexOf(':', at);
        if (colon < 0) return input;
        return string.Concat(input.AsSpan(0, authorityStart + colon + 1), "******", input.AsSpan(authorityStart + at));
    }

    private static string SanitizeLogString(string value)
    {
        var firstControl = -1;
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch < ' ' || ch == '\u007f')
            {
                firstControl = index;
                break;
            }
        }
        if (firstControl < 0) return value;

        var builder = new StringBuilder(value.Length);
        builder.Append(value.AsSpan(0, firstControl));
        for (var index = firstControl; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch >= ' ' && ch != '\u007f') builder.Append(ch);
        }
        return builder.ToString();
    }

    public override string ToString() => $"Config{{Host:{SanitizeLogString(Host)}, Port:{Port}, Database:{SanitizeLogString(Database)}, User:{SanitizeLogString(User)}, Password:\"******\", UseSSL:{UseSsl.ToString().ToLowerInvariant()}, Timeout:{FormatDuration(Timeout)}}}";

    private static string FormatDuration(TimeSpan value)
    {
        if (value.Ticks % TimeSpan.TicksPerSecond == 0) return $"{value.Ticks / TimeSpan.TicksPerSecond}s";
        if (value.Ticks % TimeSpan.TicksPerMillisecond == 0) return $"{value.Ticks / TimeSpan.TicksPerMillisecond}ms";
        return $"{value.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)}ms";
    }
}

public sealed class Db2ConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException), IDb2Error
{
    public Db2ErrorKind ErrorKind => Db2ErrorKind.Configuration;
    public int? SqlCode => null;
    public string? SqlState => null;
}
