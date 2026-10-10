using JustyBase.Db2.Types;

namespace JustyBase.Db2;

/// <summary>
/// Describes one column of a query result or of a prepared statement's parameter list.
/// </summary>
/// <param name="Name">The column name as reported by Db2 (for example the select-list alias).</param>
/// <param name="SqlType">
/// The Db2 SQL type code, including the nullable variant. Nullable types use the odd code of the
/// pair (for example <see cref="SqlType.NInteger"/> rather than <see cref="SqlType.Integer"/>);
/// use <see cref="SqlTypeNames.BaseType"/> to obtain the non-nullable type.
/// </param>
/// <param name="Length">The declared length in bytes for character, binary and LOB types.</param>
/// <param name="Precision">The precision for decimal and temporal types.</param>
/// <param name="Scale">The scale for decimal types and the fractional-second digits for timestamps.</param>
/// <param name="Nullable">Whether the column may contain SQL NULL.</param>
public sealed record Db2ColumnMetadata(string Name, SqlType SqlType, ulong Length, ushort Precision, ushort Scale, bool Nullable);
