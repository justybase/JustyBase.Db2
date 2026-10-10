using System.Data.Common;

namespace JustyBase.Db2.AdoNet;

/// <summary>An error reported by the native Db2 ADO.NET provider.</summary>
public sealed class Db2AdoException : DbException, IDb2Error
{
    public Db2AdoException(string message, Db2ErrorKind errorKind, int? sqlCode = null, string? sqlState = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorKind = errorKind;
        SqlCode = sqlCode;
        SqlState = sqlState;
    }

    public Db2ErrorKind ErrorKind { get; }
    public int? SqlCode { get; }
    public override string? SqlState { get; }

    internal static Db2AdoException From(Exception error)
    {
        if (error is Db2AdoException adoException) return adoException;
        if (error is IDb2Error db2Error)
            return new Db2AdoException(error.Message, db2Error.ErrorKind, db2Error.SqlCode, db2Error.SqlState, error);
        return new Db2AdoException(error.Message, Db2ErrorKind.ServerError, innerException: error);
    }
}
