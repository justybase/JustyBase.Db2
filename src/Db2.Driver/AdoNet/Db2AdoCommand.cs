using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using JustyBase.Db2.Protocol;
using JustyBase.Db2.Types;

namespace JustyBase.Db2.AdoNet;

/// <summary>A parameterized command executed on the session owned by its connection.</summary>
public sealed class Db2AdoCommand : DbCommand
{
    private string _commandText = "";
    private int _commandTimeout = 30;
    private CommandType _commandType = CommandType.Text;
    private UpdateRowSource _updatedRowSource = UpdateRowSource.None;
    private Db2AdoConnection? _connection;
    private Db2AdoTransaction? _transaction;
    private readonly Db2AdoParameterCollection _parameters = new();
    private Db2Statement? _preparedStatement;
    private Db2Session? _preparedSession;
    private CancellationTokenSource? _activeCancellation;
    private TaskCompletionSource? _operationCompletion;
    private Db2AdoDataReader? _activeReader;
    private readonly object _operationGate = new();
    private int _cancelRequested;
    private int _connectionCloseCancellationRequested;

    // Internal protocol trace hook used by focused DB2 regression tests. Messages contain
    // descriptor shapes and DDM sizes only; never payload bytes, SQL values, or credentials.
    internal Action<string>? ProcedureProtocolTrace { get; set; }

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set
        {
            value ??= "";
            if (!StringComparer.Ordinal.Equals(_commandText, value)) ClosePreparedStatement();
            _commandText = value;
        }
    }

    public override int CommandTimeout
    {
        get => _commandTimeout;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "CommandTimeout cannot be negative.");
            _commandTimeout = value;
        }
    }

    public override CommandType CommandType
    {
        get => _commandType;
        set => _commandType = value is CommandType.Text or CommandType.StoredProcedure
            ? value
            : throw new NotSupportedException($"CommandType {value} is not supported.");
    }

    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get => _updatedRowSource; set => _updatedRowSource = value; }
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            if (value is not null and not Db2AdoConnection) throw new ArgumentException("The command requires a Db2AdoConnection.", nameof(value));
            if (!ReferenceEquals(_connection, value)) ClosePreparedStatement();
            _connection = (Db2AdoConnection?)value;
        }
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = value switch
        {
            null => null,
            Db2AdoTransaction transaction => transaction,
            _ => throw new ArgumentException("The command requires a Db2AdoTransaction.", nameof(value)),
        };
    }

    public new Db2AdoParameterCollection Parameters => _parameters;
    internal bool HasActiveReader => _activeReader is not null;
    internal bool HasActiveOperation => _activeCancellation is not null;
    public new Db2AdoConnection? Connection { get => _connection; set => DbConnection = value; }
    public new Db2AdoTransaction? Transaction { get => _transaction; set => DbTransaction = value; }

    public override void Cancel()
    {
        if (_activeCancellation is null) return;
        Interlocked.Exchange(ref _cancelRequested, 1);
        try { _activeCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal void CancelFromConnectionClose()
    {
        if (_activeCancellation is null) return;
        Interlocked.Exchange(ref _connectionCloseCancellationRequested, 1);
        try { _activeCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        EnsureExecutable();
        using var operation = BeginOperation(cancellationToken);
        try
        {
            var session = _connection!.RequireSession();
            _connection.ValidateCommandTransaction(_transaction);
            var parameters = _parameters.Snapshot();
            if (NeedsProcedureExecution(parameters))
            {
                var statement = await EnsurePreparedAsync(session, operation.Token).ConfigureAwait(false);
                var values = await BuildProcedureParametersAsync(parameters, statement, operation.Token).ConfigureAwait(false);
                if (values.Any(parameter => parameter.Direction != Db2ParameterDirection.Input))
                {
                    var result = await session.ExecuteWithOutputsAsync(statement, values, operation.Token).ConfigureAwait(false);
                    ApplyOutputValues(parameters, result.OutputParameters);
                    return ToAffectedRows(result.RowsAffected);
                }

                // ExecuteWithOutputsAsync intentionally requires an OUT/INOUT argument.
                // A valid no-argument or input-only CALL still needs the procedure path
                // so its returned cursors and affected-row count are consumed correctly.
                return ToAffectedRows(await session.ExecuteAsync(
                    statement,
                    values.Select(parameter => parameter.Value).ToArray(),
                    operation.Token).ConfigureAwait(false));
            }

            if (parameters.Length == 0)
                return ToAffectedRows(await session.ExecuteAsync(_commandText, operation.Token).ConfigureAwait(false));
            var prepared = await EnsurePreparedAsync(session, operation.Token).ConfigureAwait(false);
            var inputValues = await BuildInputValuesAsync(parameters, prepared, operation.Token).ConfigureAwait(false);
            var affected = await session.ExecuteAsync(prepared, inputValues, operation.Token).ConfigureAwait(false);
            return ToAffectedRows(affected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref _cancelRequested) != 0) { throw; }
        catch (Exception error) { throw TranslateError(error, cancellationToken, operation); }
        finally { EndOperation(operation); }
    }

    public override object? ExecuteScalar() => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.SingleRow | CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.FieldCount == 0) return null;
        return reader.GetValue(0);
    }

    public override void Prepare() => PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        EnsureExecutable();
        using var operation = BeginOperation(cancellationToken);
        try
        {
            var session = _connection!.RequireSession();
            await EnsurePreparedAsync(session, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw TranslateError(error, cancellationToken, operation); }
        finally { EndOperation(operation); }
    }

    protected override DbParameter CreateDbParameter() => new Db2AdoParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        EnsureExecutable();
        const CommandBehavior supportedBehavior = CommandBehavior.Default
            | CommandBehavior.SingleResult
            | CommandBehavior.SchemaOnly
            | CommandBehavior.KeyInfo
            | CommandBehavior.SingleRow
            | CommandBehavior.SequentialAccess
            | CommandBehavior.CloseConnection;
        if ((behavior & ~supportedBehavior) != 0)
            throw new ArgumentOutOfRangeException(nameof(behavior), behavior, "The command behavior contains unknown flags.");
        if ((behavior & CommandBehavior.KeyInfo) != 0)
            throw new NotSupportedException("CommandBehavior.KeyInfo is not supported because Db2 result descriptors do not currently provide reliable source-key metadata.");
        var operation = BeginOperation(cancellationToken);
        var parameters = _parameters.Snapshot();
        try
        {
            var session = _connection!.RequireSession();
            _connection.ValidateCommandTransaction(_transaction);
            if ((behavior & CommandBehavior.SchemaOnly) != 0)
            {
                // PREPARE describes result columns without executing the statement. In
                // particular, SchemaOnly must never perform DML or invoke a procedure.
                var described = await EnsurePreparedAsync(session, operation.Token).ConfigureAwait(false);
                var schemaReader = new Db2AdoDataReader(
                    this, _connection,
                    [new Db2ResultSet(described.OutputColumns, Array.Empty<object?[]>())],
                    recordsAffected: -1,
                    behavior,
                    error => TranslateError(error, cancellationToken, operation),
                    operation);
                _activeReader = schemaReader;
                _connection.RegisterReader(schemaReader);
                return schemaReader;
            }

            if (NeedsProcedureExecution(parameters))
            {
                var statement = await EnsurePreparedAsync(session, operation.Token).ConfigureAwait(false);
                var procedureParameters = await BuildProcedureParametersAsync(parameters, statement, operation.Token).ConfigureAwait(false);
                var directions = procedureParameters.Select(parameter => parameter.Direction).ToArray();
                IReadOnlyList<object?>? completedOutputs = null;
                var procedureEvents = session.QueryProcedureStreamAsync(
                    statement,
                    procedureParameters.Select(parameter => parameter.Value).ToArray(),
                    directions,
                    operation.Token,
                    outputValues => completedOutputs = outputValues,
                    ProcedureProtocolTrace).GetAsyncEnumerator(operation.Token);
                var procedureReader = new Db2AdoDataReader(
                    this, _connection, procedureEvents, behavior,
                    error => TranslateError(error, cancellationToken, operation),
                    operation,
                    () => { if (completedOutputs is not null) ApplyOutputValues(parameters, completedOutputs); });
                _activeReader = procedureReader;
                _connection.RegisterReader(procedureReader);
                await procedureReader.InitializeAsync().ConfigureAwait(false);
                return procedureReader;
            }

            if (parameters.Length == 0)
            {
                // PRPSQLSTT requests RTNSQLDA in the OPNQRY chain. Pass its result
                // metadata even when there are no rows, rather than preparing twice.
                Db2AdoDataReader? directReader = null;
                var directStream = session.QueryReaderBlocksAsync(_commandText, null, null,
                    columns => directReader!.SetStreamMetadata(columns), operation.Token).GetAsyncEnumerator(operation.Token);
                directReader = new Db2AdoDataReader(this, _connection, Array.Empty<Db2ColumnMetadata>(), directStream,
                    behavior, error => TranslateError(error, cancellationToken, operation), operation);
                _activeReader = directReader;
                _connection.RegisterReader(directReader);
                await directReader.InitializeAsync().ConfigureAwait(false);
                return directReader;
            }

            var prepared = await EnsurePreparedAsync(session, operation.Token).ConfigureAwait(false);
            var values = await BuildInputValuesAsync(parameters, prepared, operation.Token).ConfigureAwait(false);
            Db2AdoDataReader? reader = null;
            var enumerator = session.QueryReaderBlocksAsync(prepared.Sql, prepared, values,
                columns => reader!.SetStreamMetadata(columns), operation.Token).GetAsyncEnumerator(operation.Token);
            reader = new Db2AdoDataReader(
                this, _connection, prepared.OutputColumns, enumerator, behavior,
                error => TranslateError(error, cancellationToken, operation), operation);
            _activeReader = reader;
            _connection.RegisterReader(reader);
            await reader.InitializeAsync().ConfigureAwait(false);
            return reader;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref _cancelRequested) != 0)
        {
            EndOperation(operation);
            throw;
        }
        catch (Exception error)
        {
            EndOperation(operation);
            throw TranslateError(error, cancellationToken, operation);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Cancel();
            _activeReader?.Close();
            ClosePreparedStatement();
        }
        base.Dispose(disposing);
    }

    internal void ReaderCompleted(Db2AdoDataReader reader, CancellationTokenSource? operation)
    {
        if (ReferenceEquals(_activeReader, reader)) _activeReader = null;
        if (operation is not null) EndOperation(operation);
    }

    internal Task WaitForOperationCompletionAsync()
    {
        lock (_operationGate) return _operationCompletion?.Task ?? Task.CompletedTask;
    }

    private async Task<Db2Statement> EnsurePreparedAsync(Db2Session session, CancellationToken cancellationToken)
    {
        if (_preparedStatement is not null && !_preparedStatement.IsClosed && ReferenceEquals(_preparedSession, session)) return _preparedStatement;
        ClosePreparedStatement();
        _preparedStatement = await session.PrepareAsync(GetExecutableSql(), cancellationToken).ConfigureAwait(false);
        _preparedSession = session;
        return _preparedStatement;
    }

    private string GetExecutableSql()
    {
        if (_commandType == CommandType.Text) return _commandText;
        var name = _commandText.Trim();
        if (name.Length == 0) throw new InvalidOperationException("A stored procedure name is required.");
        var parts = SplitProcedureName(name);
        var quoted = string.Join('.', parts.Select(QuoteIdentifier));
        return $"CALL {quoted}({string.Join(",", Enumerable.Repeat("?", _parameters.Count))})";
    }

    private bool NeedsProcedureExecution(Db2AdoParameter[] parameters) =>
        _commandType == CommandType.StoredProcedure
        || StartsWithCall(_commandText)
        || parameters.Any(parameter => parameter.Direction is ParameterDirection.Output or ParameterDirection.InputOutput or ParameterDirection.ReturnValue);

    private static bool StartsWithCall(string sql)
    {
        var text = sql.AsSpan().TrimStart();
        return text.StartsWith("CALL", StringComparison.OrdinalIgnoreCase)
            && (text.Length == 4 || char.IsWhiteSpace(text[4]) || text[4] is '"' or '`');
    }

    private static IReadOnlyList<string> SplitProcedureName(string name)
    {
        var parts = new List<string>();
        var builder = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (current == '"')
            {
                if (quoted && index + 1 < name.Length && name[index + 1] == '"') { builder.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (current == '.' && !quoted)
            {
                AddPart();
            }
            else builder.Append(current);
        }
        if (quoted) throw new ArgumentException("The stored procedure name contains an unterminated quoted identifier.", nameof(name));
        AddPart();
        if (parts.Count is < 1 or > 2) throw new ArgumentException("A stored procedure name must be a procedure or schema-qualified procedure identifier.", nameof(name));
        return parts;

        void AddPart()
        {
            var part = builder.ToString().Trim();
            if (part.Length == 0) throw new ArgumentException("Stored procedure identifiers cannot be empty.", nameof(name));
            parts.Add(part);
            builder.Clear();
        }
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private async Task<object?[]> BuildInputValuesAsync(Db2AdoParameter[] parameters, Db2Statement statement, CancellationToken cancellationToken)
    {
        var values = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            if (parameters[index].Direction != ParameterDirection.Input)
                throw new InvalidOperationException("Output parameters are supported for CALL and StoredProcedure commands only.");
            values[index] = await ConvertValueAsync(parameters[index], statement.ParameterColumns, index, cancellationToken).ConfigureAwait(false);
        }
        return values;
    }

    private async Task<IReadOnlyList<Db2Parameter>> BuildProcedureParametersAsync(Db2AdoParameter[] parameters, Db2Statement statement, CancellationToken cancellationToken)
    {
        var values = new Db2Parameter[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            if (parameter.Direction == ParameterDirection.ReturnValue)
                throw new NotSupportedException("Db2 LUW CALL does not expose an ADO.NET ReturnValue parameter.");
            var direction = parameter.Direction switch
            {
                ParameterDirection.Input => Db2ParameterDirection.Input,
                ParameterDirection.Output => Db2ParameterDirection.Output,
                ParameterDirection.InputOutput => Db2ParameterDirection.InputOutput,
                _ => throw new NotSupportedException($"ParameterDirection {parameter.Direction} is not supported."),
            };
            var value = direction == Db2ParameterDirection.Output && parameter.Value is null or DBNull
                ? OutputPlaceholder(parameter)
                : await ConvertValueAsync(parameter, statement.ParameterColumns, index, cancellationToken).ConfigureAwait(false);
            values[index] = new Db2Parameter(value, direction);
        }
        return values;
    }

    private static object OutputPlaceholder(Db2AdoParameter parameter) => parameter.DbType switch
    {
        DbType.Int16 or DbType.SByte or DbType.Byte => (short)0,
        DbType.Int32 => 0,
        DbType.Int64 => 0L,
        DbType.Single => 0f,
        DbType.Double => 0d,
        DbType.Decimal or DbType.Currency or DbType.VarNumeric => 0m,
        DbType.Boolean => false,
        DbType.Date or DbType.DateTime or DbType.DateTime2 => new DateTime(2000, 1, 1),
        DbType.Time => TimeSpan.Zero,
        DbType.Binary or DbType.AnsiStringFixedLength or DbType.StringFixedLength => Array.Empty<byte>(),
        _ => "",
    };

    private static async ValueTask<object?> ConvertValueAsync(Db2AdoParameter parameter, IReadOnlyList<Db2ColumnMetadata> parameterColumns, int index, CancellationToken cancellationToken)
    {
        var value = parameter.Value;
        if (value is null or DBNull) return null;
        var sqlType = index < parameterColumns.Count ? (SqlType)((ushort)parameterColumns[index].SqlType & 0xfffe) : default;
        if (value is Stream stream)
        {
            var length = GetStreamLength(stream, parameter.Size);
            return sqlType switch
            {
                SqlType.Blob => new Db2BlobStream(stream, length, parameter.LeaveOpen),
                SqlType.Clob => new Db2ClobStream(stream, length, parameter.LeaveOpen),
                SqlType.DbClob => new Db2DbClobStream(stream, length, parameter.LeaveOpen),
                SqlType.XML => new Db2XmlStream(stream, length, parameter.LeaveOpen),
                _ => throw new NotSupportedException($"A Stream parameter requires a BLOB, CLOB, DBCLOB, or XML target; the prepared Db2 type is {sqlType}."),
            };
        }
        if (value is TextReader textReader)
        {
            var text = await textReader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return text;
        }
        return value;
    }

    private static long GetStreamLength(Stream stream, int declaredSize)
    {
        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            if (declaredSize > 0 && declaredSize != remaining)
                throw new ArgumentException($"Parameter Size ({declaredSize}) does not match the remaining stream length ({remaining}).");
            return remaining;
        }
        if (declaredSize <= 0) throw new ArgumentException("A non-seekable LOB stream requires Parameter.Size to declare its byte length.");
        return declaredSize;
    }

    private static void ApplyOutputValues(Db2AdoParameter[] parameters, IReadOnlyList<object?> outputValues)
    {
        for (var index = 0; index < parameters.Length && index < outputValues.Count; index++)
            if (parameters[index].Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
                parameters[index].Value = outputValues[index] ?? DBNull.Value;
    }

    private CancellationTokenSource BeginOperation(CancellationToken cancellationToken)
    {
        lock (_operationGate)
        {
            if (_activeCancellation is not null) throw new InvalidOperationException("This command already has an active operation.");
            Interlocked.Exchange(ref _cancelRequested, 0);
            Interlocked.Exchange(ref _connectionCloseCancellationRequested, 0);
            var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_commandTimeout > 0) operation.CancelAfter(TimeSpan.FromSeconds(_commandTimeout));
            _activeCancellation = operation;
            _operationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try { _connection!.RegisterOperation(this); }
            catch
            {
                _activeCancellation = null;
                _operationCompletion.TrySetResult();
                operation.Dispose();
                throw;
            }
            return operation;
        }
    }

    private Exception TranslateError(Exception error, CancellationToken cancellationToken, CancellationTokenSource operation)
    {
        if (operation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested
            && Volatile.Read(ref _cancelRequested) == 0
            && Volatile.Read(ref _connectionCloseCancellationRequested) == 0
            && _commandTimeout > 0)
            return new TimeoutException($"Db2 command exceeded CommandTimeout ({_commandTimeout} seconds).", error);

        var cancellationRequested = operation.IsCancellationRequested
            && (cancellationToken.IsCancellationRequested
                || Volatile.Read(ref _cancelRequested) != 0
                || Volatile.Read(ref _connectionCloseCancellationRequested) != 0);
        if (error is OperationCanceledException)
            return _connection?.TranslateOperationCancellation(error) ?? error;
        if (cancellationRequested && error is Db2ConnectionClosedException)
        {
            var token = cancellationToken.IsCancellationRequested ? cancellationToken : operation.Token;
            return new OperationCanceledException(
                "The Db2 operation was canceled; the session was discarded because cancellation could not be confirmed.",
                error,
                token);
        }
        if (error is ArgumentException or InvalidCastException or FormatException or OverflowException or NotSupportedException)
            return error;
        return _connection?.TranslateProviderError(error) ?? Db2AdoException.From(error);
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        TaskCompletionSource? completion;
        lock (_operationGate)
        {
            if (!ReferenceEquals(_activeCancellation, operation)) return;
            _activeCancellation = null;
            completion = _operationCompletion;
        }
        _connection?.UnregisterOperation(this);
        operation.Dispose();
        completion?.TrySetResult();
    }

    private void EnsureExecutable()
    {
        if (string.IsNullOrWhiteSpace(_commandText)) throw new InvalidOperationException("CommandText is required.");
        if (_commandType is not (CommandType.Text or CommandType.StoredProcedure)) throw new NotSupportedException($"CommandType {_commandType} is not supported.");
        if (_connection is null) throw new InvalidOperationException("The command has no connection.");
    }

    private static int ToAffectedRows(long rowsAffected) => rowsAffected > int.MaxValue ? int.MaxValue : rowsAffected < int.MinValue ? int.MinValue : (int)rowsAffected;

    private void ClosePreparedStatement()
    {
        _preparedStatement?.Close();
        _preparedStatement = null;
        _preparedSession = null;
    }
}
