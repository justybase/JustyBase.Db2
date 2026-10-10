using JustyBase.Db2;
using JustyBase.Db2.AdoNet;

namespace JustyBase.Db2.Tests;

internal static class LiveFixtureSchema
{
    internal static Task<string> ForSessionAsync(Db2Session session) => ResolveAsync(session);
    internal static Task<string> ForAdoConnectionAsync(Db2AdoConnection connection) => ResolveAsync(connection.RequireSession());
    private static async Task<string> ResolveAsync(Db2Session session)
    {
        var rows = (await session.QueryAsync("VALUES CURRENT SCHEMA").ConfigureAwait(false)).Rows;
        var schema = ((string)rows[0][0]!).TrimEnd();
        return "\"" + schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
