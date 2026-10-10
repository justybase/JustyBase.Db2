namespace JustyBase.Db2.AdoNet;

/// <summary>One import owns this cache; progress and commit boundaries still choose each shape.</summary>
internal sealed class Db2InsertStatementCache(string table, IReadOnlyList<string> columns)
{
    private readonly Dictionary<int, string> _statements = [];
    internal string Get(int rows)
    {
        if (_statements.TryGetValue(rows, out var sql)) return sql;
        sql = Db2AdoImporter.BuildInsertStatement(table, columns, rows);
        // Irregular byte-limited batches can otherwise keep thousands of SQL strings.
        if (_statements.Count == 64) _statements.Clear();
        _statements.Add(rows, sql);
        return sql;
    }
}
