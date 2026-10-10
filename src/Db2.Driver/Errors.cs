using System.Globalization;
using System.Text.RegularExpressions;

namespace JustyBase.Db2;

/// <summary>Stable classification for errors reported by the native Db2 driver.</summary>
public enum Db2ErrorKind
{
    Configuration,
    ConnectionClosed,
    DatabaseNotFound,
    Authentication,
    Authorization,
    SqlError,
    ServerError,
    ParameterConversion,
    BatchExecution,
    Unsupported,
    PoolTimeout,
    PoolExhausted,
    Transport,
    Protocol,
    ResourceLimit,
}

/// <summary>Confirmation of the failing operation, independent of retry policy.</summary>
public enum Db2OperationOutcome { NotStarted, Confirmed, Unknown }

/// <summary>Common diagnostic properties exposed by classified Db2 errors.</summary>
public interface IDb2Error
{
    Db2ErrorKind ErrorKind { get; }
    Db2OperationOutcome Outcome => ErrorKind is Db2ErrorKind.Configuration or Db2ErrorKind.PoolTimeout or Db2ErrorKind.PoolExhausted
        ? Db2OperationOutcome.NotStarted : SqlCode.HasValue ? Db2OperationOutcome.Confirmed : Db2OperationOutcome.Unknown;
    int? SqlCode { get; }
    string? SqlState { get; }
}

/// <summary>A successful Db2 operation that also returned warning diagnostics.</summary>
public sealed record Db2Warning(int SqlCode, string SqlState, string Message, string WarningFlags);

/// <summary>Event payload for a warning reported by a Db2 session.</summary>
public sealed class Db2WarningEventArgs(Db2Warning warning) : EventArgs
{
    public Db2Warning Warning { get; } = warning;
}

/// <summary>A closed or disposed Db2 session was used.</summary>
public sealed class Db2ConnectionClosedException(string message = "DB2 connection is closed.")
    : InvalidOperationException(message), IDb2Error
{
    public Db2ErrorKind ErrorKind => Db2ErrorKind.ConnectionClosed;
    public int? SqlCode => null;
    public string? SqlState => null;
}

/// <summary>A pooled callback returned with an unfinished transaction, so its session was discarded.</summary>
public sealed class Db2AbandonedTransactionException(Exception? rollbackError = null)
    : InvalidOperationException(
        rollbackError is null
            ? "A pooled DB2 callback returned with an unfinished transaction; the transaction was rolled back and the session was discarded."
            : "A pooled DB2 callback returned with an unfinished transaction; rollback failed and the session was discarded.",
        rollbackError)
{
    /// <summary>The server error returned while attempting rollback, if rollback failed.</summary>
    public Exception? RollbackError { get; } = rollbackError;
}

/// <summary>The requested relational database was not found by the server.</summary>
public sealed class Db2DatabaseNotFoundException(string message = "DB2 relational database was not found.")
    : InvalidOperationException(message), IDb2Error
{
    public Db2ErrorKind ErrorKind => Db2ErrorKind.DatabaseNotFound;
    public int? SqlCode => null;
    public string? SqlState => null;
}

/// <summary>Db2 rejected the credentials during security checking.</summary>
public sealed class Db2AuthenticationException(byte securityCheckCode)
    : UnauthorizedAccessException($"DB2 authentication failed (SECCHKCD={securityCheckCode})."), IDb2Error
{
    public Db2ErrorKind ErrorKind => Db2ErrorKind.Authentication;
    public byte SecurityCheckCode { get; } = securityCheckCode;
    public int? SqlCode => null;
    public string? SqlState => null;
}

/// <summary>
/// Kerberos/GSSAPI credential acquisition or security-context establishment failed (no or expired ticket,
/// unknown realm or service principal, unreachable KDC, rejected server token). Messages carry GSS-API status text
/// only and never contain ticket, keytab or token bytes.
/// </summary>
public sealed class Db2KerberosException(string message, Exception? innerException = null)
    : UnauthorizedAccessException(message, innerException), IDb2Error
{
    public Db2ErrorKind ErrorKind => Db2ErrorKind.Authentication;
    public int? SqlCode => null;
    public string? SqlState => null;
}

/// <summary>The authenticated identity is not authorized to connect to the requested database.</summary>
public sealed class Db2AuthorizationException(string message = "DB2 authorization failed while connecting to the database.")
    : UnauthorizedAccessException(message), IDb2Error
{
    public Db2ErrorKind ErrorKind => Db2ErrorKind.Authorization;
    public int? SqlCode => null;
    public string? SqlState => null;
}

/// <summary>A SQLCARD reported a negative SQLCODE.</summary>
public sealed class Db2SqlException(int sqlCode, string sqlState, string message)
    : InvalidOperationException(message), IDb2Error
{
    public Db2ErrorKind ErrorKind => Db2ErrorKind.SqlError;
    public int? SqlCode { get; } = sqlCode;
    public string? SqlState { get; } = sqlState;
}

/// <summary>A DRDA SQLERRRM response, including any server diagnostic text.</summary>
public sealed class Db2ServerErrorException(string message)
    : InvalidOperationException(message), IDb2Error
{
    private static readonly Regex SqlCodePattern = new(@"\bSQLCODE\s*[:=]\s*(-?\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SqlStatePattern = new(@"\bSQLSTATE\s*[:=]\s*([0-9A-Z]{5})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public Db2ErrorKind ErrorKind => SqlCode.HasValue || SqlState is not null ? Db2ErrorKind.SqlError : Db2ErrorKind.ServerError;
    public int? SqlCode { get; } = ParseSqlCode(message);
    public string? SqlState { get; } = ParseSqlState(message);

    private static int? ParseSqlCode(string text)
    {
        var match = SqlCodePattern.Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)
            ? code
            : null;
    }

    private static string? ParseSqlState(string text)
    {
        var match = SqlStatePattern.Match(text);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }
}

/// <summary>The driver does not implement a requested Db2 feature, protocol layout, or field type.</summary>
public sealed class Db2UnsupportedFeatureException(string message)
    : NotSupportedException(message), IDb2Error
{
    public Db2ErrorKind ErrorKind => Db2ErrorKind.Unsupported;
    public int? SqlCode => null;
    public string? SqlState => null;
}

/// <summary>A command deadline elapsed. The operation was interrupted with SQLINTR; if DB2 could not confirm cancellation, the session is discarded.</summary>
public sealed class Db2CommandTimeoutException(TimeSpan timeout, Exception? innerException = null)
    : TimeoutException($"DB2 command exceeded its {timeout} deadline.", innerException);

/// <summary>A batch parameter set failed after earlier sets may already have committed.</summary>
public sealed class Db2BatchExecutionException(
    int? parameterSetIndex,
    int completedParameterSets,
    long rowsAffected,
    Exception innerException)
    : InvalidOperationException(
        parameterSetIndex is { } index
            ? $"DB2 batch parameter set {index} failed after {completedParameterSets} completed sets and {rowsAffected} affected rows: {innerException.Message}"
            : $"DB2 batch setup failed before a parameter set completed: {innerException.Message}",
        innerException), IDb2Error
{
    /// <summary>Zero-based index of the parameter set that failed, or null if setup failed first.</summary>
    public int? ParameterSetIndex { get; } = parameterSetIndex;
    /// <summary>Number of parameter sets whose executions completed successfully.</summary>
    public int CompletedParameterSets { get; } = completedParameterSets;
    /// <summary>Rows affected by completed sets, based only on acknowledgements received from Db2.</summary>
    public long RowsAffected { get; } = rowsAffected;
    public Db2ErrorKind ErrorKind => Db2ErrorKind.BatchExecution;
    public int? SqlCode => (InnerException as IDb2Error)?.SqlCode;
    public string? SqlState => (InnerException as IDb2Error)?.SqlState;
}

/// <summary>A sequential batch was cancelled after some earlier parameter sets were confirmed.</summary>
public sealed class Db2BatchCancellationException(
    CancellationToken cancellationToken,
    int completedParameterSets,
    int? inFlightParameterSetIndex,
    long rowsAffected)
    : OperationCanceledException("DB2 batch execution was cancelled.", cancellationToken)
{
    /// <summary>Number of parameter sets whose execution completed successfully before cancellation.</summary>
    public int CompletedParameterSets { get; } = completedParameterSets;
    /// <summary>Zero-based set active when cancellation was observed; its final server effect is not included.</summary>
    public int? InFlightParameterSetIndex { get; } = inFlightParameterSetIndex;
    /// <summary>Rows affected by confirmed completed sets only.</summary>
    public long RowsAffected { get; } = rowsAffected;
}

public sealed class Db2PoolTimeoutException : TimeoutException, IDb2Error
{
    public Db2PoolTimeoutException() : base("DB2 pool acquisition timed out.") { }
    public Db2ErrorKind ErrorKind => Db2ErrorKind.PoolTimeout;
    public int? SqlCode => null;
    public string? SqlState => null;
}
public sealed class Db2PoolExhaustedException : InvalidOperationException, IDb2Error
{
    public Db2PoolExhaustedException() : base("DB2 pool waiting queue is full.") { }
    public Db2ErrorKind ErrorKind => Db2ErrorKind.PoolExhausted;
    public int? SqlCode => null;
    public string? SqlState => null;
}
public sealed class Db2ResourceLimitException(string message) : IOException(message), IDb2Error
{
    public Db2ErrorKind ErrorKind => Db2ErrorKind.ResourceLimit;
    public int? SqlCode => null;
    public string? SqlState => null;
}
