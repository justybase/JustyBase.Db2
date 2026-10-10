using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace JustyBase.Db2.AdoNet;

/// <summary>A positional Db2 command parameter. Parameters are sent in collection order.</summary>
public sealed class Db2AdoParameter : DbParameter
{
    private DbType _dbType = DbType.String;
    private ParameterDirection _direction = ParameterDirection.Input;
    private string _parameterName = "";
    private string _sourceColumn = "";
    private object? _value;

    public override DbType DbType { get => _dbType; set => _dbType = value; }
    public override ParameterDirection Direction { get => _direction; set => _direction = value; }
    public override bool IsNullable { get; set; }
    [AllowNull]
    public override string ParameterName { get => _parameterName; set => _parameterName = value ?? ""; }
    [AllowNull]
    public override string SourceColumn { get => _sourceColumn; set => _sourceColumn = value ?? ""; }
    public override object? Value { get => _value; set => _value = value; }
    public override bool SourceColumnNullMapping { get; set; }
    public override int Size { get; set; }
    public override byte Precision { get; set; }
    public override byte Scale { get; set; }

    /// <summary>Controls whether a supplied LOB stream remains open after command execution.</summary>
    public bool LeaveOpen { get; set; } = true;

    public override DataRowVersion SourceVersion { get; set; } = DataRowVersion.Current;

    public override void ResetDbType() => _dbType = DbType.String;
}
