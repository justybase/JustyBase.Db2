using System.Runtime.InteropServices;

namespace JustyBase.Db2.Security.Kerberos;

/// <summary>
/// The one libkrb5 call the driver needs beyond GSS-API: destroying the private in-memory credential cache that
/// a keytab-based connection uses, so no ticket outlives its connection and nothing reaches the default cache.
/// </summary>
internal static partial class Krb5Native
{
    private const string Library = "libkrb5.so.3";

    [LibraryImport(Library)]
    private static partial int krb5_init_context(out nint context);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int krb5_cc_resolve(nint context, string name, out nint cache);

    [LibraryImport(Library)]
    private static partial int krb5_cc_destroy(nint context, nint cache);

    [LibraryImport(Library)]
    private static partial void krb5_free_context(nint context);

    /// <summary>Best effort: never throws, because it runs while a failed connection is being torn down.</summary>
    internal static bool TryDestroyCache(string name)
    {
        try
        {
            if (krb5_init_context(out var context) != 0) return false;
            try
            {
                if (krb5_cc_resolve(context, name, out var cache) != 0) return false;
                return krb5_cc_destroy(context, cache) == 0;
            }
            finally { krb5_free_context(context); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}
