using System.Collections;
using System.Data.Common;

namespace JustyBase.Db2.AdoNet;

/// <summary>Ordered parameter collection used by <see cref="Db2AdoCommand"/>.</summary>
public sealed class Db2AdoParameterCollection : DbParameterCollection
{
    private readonly List<Db2AdoParameter> _items = [];
    private readonly object _syncRoot = new();
    private Db2AdoParameter[]? _snapshot;

    public override int Count => _items.Count;
    public override object SyncRoot => _syncRoot;
    public override bool IsFixedSize => false;
    public override bool IsReadOnly => false;
    public override bool IsSynchronized => false;

    public override int Add(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is not Db2AdoParameter parameter)
            throw new ArgumentException($"Expected {nameof(Db2AdoParameter)}.", nameof(value));
        _items.Add(parameter);
        _snapshot = null;
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values) Add(value!);
    }

    public override void Clear() { _items.Clear(); _snapshot = null; }
    public override bool Contains(object value) => value is Db2AdoParameter parameter && _items.Contains(parameter);
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => value is Db2AdoParameter parameter ? _items.IndexOf(parameter) : -1;

    public override int IndexOf(string parameterName)
    {
        var normalized = NormalizeName(parameterName);
        for (var i = 0; i < _items.Count; i++)
            if (NormalizeName(_items[i].ParameterName).Equals(normalized, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public override void Insert(int index, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is not Db2AdoParameter parameter)
            throw new ArgumentException($"Expected {nameof(Db2AdoParameter)}.", nameof(value));
        _items.Insert(index, parameter);
        _snapshot = null;
    }

    public override void Remove(object value)
    {
        if (value is Db2AdoParameter parameter && _items.Remove(parameter)) _snapshot = null;
    }

    public override void RemoveAt(int index) { _items.RemoveAt(index); _snapshot = null; }

    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index < 0) throw new IndexOutOfRangeException($"Parameter '{parameterName}' was not found.");
        RemoveAt(index);
    }

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index < 0) throw new IndexOutOfRangeException($"Parameter '{parameterName}' was not found.");
        return _items[index];
    }

    protected override void SetParameter(int index, DbParameter value) { _items[index] = RequireParameter(value); _snapshot = null; }

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index < 0) _items.Add(RequireParameter(value));
        else _items[index] = RequireParameter(value);
        _snapshot = null;
    }

    // Internal consumers treat the array as read-only; parameters themselves remain mutable.
    internal Db2AdoParameter[] Snapshot() => _snapshot ??= [.. _items];

    private static Db2AdoParameter RequireParameter(DbParameter value) => value as Db2AdoParameter
        ?? throw new ArgumentException($"Expected {nameof(Db2AdoParameter)}.", nameof(value));

    private static ReadOnlySpan<char> NormalizeName(string? value)
    {
        var name = value.AsSpan();
        return name.Length > 0 && name[0] is '@' or ':' or '?' ? name[1..] : name;
    }
}
