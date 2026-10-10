using System.Data;
using System.Text;
using JustyBase.Db2;
using JustyBase.Db2.AdoNet;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;
using Xunit;

namespace JustyBase.Db2.Tests;

public sealed class ProviderFactoryCompatibilityTests
{
    [Fact]
    public void ProviderFactoryWrapperObjectsWorkWithNativeCommandAndParameter()
    {
        var factory = Db2AdoProviderFactory.Instance;
        using var connection = factory.CreateConnection();
        var builder = factory.CreateConnectionStringBuilder();
        Assert.IsType<Db2AdoConnection>(connection);
        Assert.IsType<Db2AdoConnectionStringBuilder>(builder);
        builder.ConnectionString = "Server=localhost:50000;Database=TESTDB;UID=test;PWD=not-used;Pooling=false";
        connection.ConnectionString = builder.ConnectionString;
        Assert.Equal("TESTDB", connection.Database);
        using var command = factory.CreateCommand();
        command.Connection = connection;
        var parameter = factory.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.DbType = DbType.Int32;
        parameter.Value = 42;
        command.Parameters.Add(parameter);
        Assert.Same(connection, command.Connection);
        Assert.Same(parameter, command.Parameters["id"]);
        using var nativeCommand = connection.CreateCommand();
        Assert.IsType<Db2AdoCommand>(nativeCommand);
    }
}
