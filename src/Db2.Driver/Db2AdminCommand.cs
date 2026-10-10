namespace JustyBase.Db2;

internal static class Db2AdminCommand
{
    internal const string CallSql = "CALL SYSPROC.ADMIN_CMD(?)";

    internal static string Normalize(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Contains('\0'))
            throw new ArgumentException("Db2 admin command cannot contain a NUL character.", nameof(command));

        var normalized = command.Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("Db2 admin command cannot be empty.", nameof(command));
        return normalized;
    }
}
