using System.Data.Common;

namespace JustyBase.Db2.AdoNet;

/// <summary>Creates connections, commands, and parameters for the native Db2 ADO.NET provider.</summary>
public sealed class Db2AdoProviderFactory : DbProviderFactory
{
    public static Db2AdoProviderFactory Instance { get; } = new();

    private Db2AdoProviderFactory() { }

    public override DbConnection CreateConnection() => new Db2AdoConnection();
    public override DbCommand CreateCommand() => new Db2AdoCommand();
    public override DbParameter CreateParameter() => new Db2AdoParameter();
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new Db2AdoConnectionStringBuilder();
}
