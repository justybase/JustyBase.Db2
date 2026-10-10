using System.Runtime.InteropServices;
using System.Text;

namespace JustyBase.Db2.Security.Kerberos;

/// <summary>
/// Minimal P/Invoke surface for the system MIT <c>libgssapi_krb5</c>. Nothing here is loaded
/// until a Kerberos connection actually needs it, and only on Linux. The credential-store
/// variant <c>gss_acquire_cred_from</c> lets one connection pick its own ccache or client
/// keytab, so the driver never has to mutate KRB5CCNAME/KRB5_KTNAME/KRB5_CONFIG.
/// </summary>
internal static partial class GssapiNative
{
    private const string Library = "libgssapi_krb5.so.2";

    internal const uint Complete = 0;
    internal const uint ContinueNeeded = 1;
    internal const uint ErrorMask = 0xFFFF0000;
    internal const int CredUsageInitiate = 1;
    internal const uint MutualFlag = 2;
    private const int StatusCodeGss = 1;
    private const int StatusCodeMech = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct GssBuffer
    {
        public nuint Length;
        public nint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GssOid
    {
        public uint Length;
        public nint Elements;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GssOidSet
    {
        public nuint Count;
        public nint Elements;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GssKeyValueElement
    {
        public nint Key;
        public nint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GssKeyValueSet
    {
        public uint Count;
        public nint Elements;
    }

    [LibraryImport(Library)]
    private static partial uint gss_import_name(out uint minor, ref GssBuffer nameBuffer, nint nameType, out nint outputName);

    [LibraryImport(Library)]
    internal static partial uint gss_release_name(out uint minor, ref nint name);

    [LibraryImport(Library)]
    private static partial uint gss_acquire_cred_from(out uint minor, nint desiredName, uint timeRequired, nint desiredMechs, int credUsage,
        nint credStore, out nint outputCred, nint actualMechs, nint timeRec);

    [LibraryImport(Library)]
    internal static partial uint gss_release_cred(out uint minor, ref nint cred);

    [LibraryImport(Library)]
    private static partial uint gss_init_sec_context(out uint minor, nint initiatorCred, ref nint context, nint targetName, nint mechType,
        uint requestFlags, uint timeRequired, nint channelBindings, nint inputToken, nint actualMech, ref GssBuffer outputToken,
        out uint returnedFlags, nint timeRec);

    [LibraryImport(Library)]
    private static partial uint gss_init_sec_context(out uint minor, nint initiatorCred, ref nint context, nint targetName, nint mechType,
        uint requestFlags, uint timeRequired, nint channelBindings, ref GssBuffer inputToken, nint actualMech, ref GssBuffer outputToken,
        out uint returnedFlags, nint timeRec);

    [LibraryImport(Library)]
    internal static partial uint gss_delete_sec_context(out uint minor, ref nint context, nint outputToken);

    [LibraryImport(Library)]
    private static partial uint gss_release_buffer(out uint minor, ref GssBuffer buffer);

    [LibraryImport(Library)]
    private static partial uint gss_display_status(out uint minor, uint statusValue, int statusType, nint mechType,
        ref uint messageContext, ref GssBuffer statusString);

    // OID descriptors live for the whole process: they are immutable and a few bytes each.
    private static readonly Lazy<nint> KerberosMechanism = new(() => AllocateOid([0x2a, 0x86, 0x48, 0x86, 0xf7, 0x12, 0x01, 0x02, 0x02]));
    private static readonly Lazy<nint> KerberosPrincipalName = new(() => AllocateOid([0x2a, 0x86, 0x48, 0x86, 0xf7, 0x12, 0x01, 0x02, 0x02, 0x01]));
    private static readonly Lazy<nint> KerberosMechanismSet = new(() =>
    {
        var set = new GssOidSet { Count = 1, Elements = KerberosMechanism.Value };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<GssOidSet>());
        Marshal.StructureToPtr(set, memory, false);
        return memory;
    });

    internal static nint MechanismOid => KerberosMechanism.Value;

    private static nint AllocateOid(byte[] contents)
    {
        var elements = Marshal.AllocHGlobal(contents.Length);
        Marshal.Copy(contents, 0, elements, contents.Length);
        var descriptor = new GssOid { Length = (uint)contents.Length, Elements = elements };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<GssOid>());
        Marshal.StructureToPtr(descriptor, memory, false);
        return memory;
    }

    /// <summary>Imports a Kerberos principal name such as <c>db2inst1/db2.example.com@EXAMPLE.COM</c>.</summary>
    internal static uint ImportPrincipal(string principal, out uint minor, out nint name)
    {
        var bytes = Encoding.UTF8.GetBytes(principal);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var buffer = new GssBuffer { Length = (nuint)bytes.Length, Value = pinned.AddrOfPinnedObject() };
            return gss_import_name(out minor, ref buffer, KerberosPrincipalName.Value, out name);
        }
        finally { pinned.Free(); }
    }

    /// <summary>
    /// Acquires initiator credentials for the Kerberos mechanism. <paramref name="ccache"/> and
    /// <paramref name="clientKeytab"/> are per-call credential-store entries (MIT ≥ 1.11), not process state.
    /// </summary>
    internal static uint AcquireInitiatorCredential(nint desiredName, string? ccache, string? clientKeytab, out uint minor, out nint credential)
    {
        var allocations = new List<nint>();
        try
        {
            var entries = new List<(string Key, string Value)>();
            if (ccache is not null) entries.Add(("ccache", ccache));
            if (clientKeytab is not null) entries.Add(("client_keytab", clientKeytab));
            nint storePointer = 0;
            if (entries.Count > 0)
            {
                var elementSize = Marshal.SizeOf<GssKeyValueElement>();
                var elements = Marshal.AllocHGlobal(elementSize * entries.Count);
                allocations.Add(elements);
                for (var i = 0; i < entries.Count; i++)
                {
                    var key = AllocateUtf8(entries[i].Key);
                    var value = AllocateUtf8(entries[i].Value);
                    allocations.Add(key);
                    allocations.Add(value);
                    Marshal.StructureToPtr(new GssKeyValueElement { Key = key, Value = value }, elements + i * elementSize, false);
                }
                storePointer = Marshal.AllocHGlobal(Marshal.SizeOf<GssKeyValueSet>());
                allocations.Add(storePointer);
                Marshal.StructureToPtr(new GssKeyValueSet { Count = (uint)entries.Count, Elements = elements }, storePointer, false);
            }
            return gss_acquire_cred_from(out minor, desiredName, 0, KerberosMechanismSet.Value, CredUsageInitiate,
                storePointer, out credential, 0, 0);
        }
        finally
        {
            foreach (var allocation in allocations) Marshal.FreeHGlobal(allocation);
        }
    }

    private static nint AllocateUtf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var memory = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, memory, bytes.Length);
        Marshal.WriteByte(memory, bytes.Length, 0);
        return memory;
    }

    /// <summary>One <c>gss_init_sec_context</c> step. <paramref name="serverToken"/> is empty for the first call.</summary>
    internal static uint InitSecurityContext(nint credential, ref nint context, nint targetName, ReadOnlySpan<byte> serverToken,
        uint flags, out uint minor, out byte[] outputToken)
    {
        var output = new GssBuffer();
        var input = serverToken.ToArray();
        var pinned = input.Length == 0 ? default : GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            uint major;
            if (input.Length == 0)
            {
                major = gss_init_sec_context(out minor, credential, ref context, targetName, KerberosMechanism.Value, flags, 0, 0, 0, 0, ref output, out _, 0);
            }
            else
            {
                var inputBuffer = new GssBuffer { Length = (nuint)input.Length, Value = pinned.AddrOfPinnedObject() };
                major = gss_init_sec_context(out minor, credential, ref context, targetName, KerberosMechanism.Value, flags, 0, 0, ref inputBuffer, 0, ref output, out _, 0);
            }
            outputToken = CopyAndRelease(ref output);
            return major;
        }
        finally
        {
            if (input.Length != 0) pinned.Free();
            // The first call may fail after allocating an output buffer; make sure it is released.
            if (output.Value != 0) _ = gss_release_buffer(out _, ref output);
        }
    }

    private static byte[] CopyAndRelease(ref GssBuffer buffer)
    {
        if (buffer.Value == 0 || buffer.Length == 0) return [];
        var copy = new byte[(int)buffer.Length];
        Marshal.Copy(buffer.Value, copy, 0, copy.Length);
        _ = gss_release_buffer(out _, ref buffer);
        buffer = default;
        return copy;
    }

    /// <summary>Human-readable GSS-API / Kerberos status text. Never contains token or key material.</summary>
    internal static string DescribeStatus(uint major, uint minor)
    {
        var builder = new StringBuilder();
        AppendStatus(builder, major, StatusCodeGss, 0);
        if (minor != 0) AppendStatus(builder, minor, StatusCodeMech, KerberosMechanism.Value);
        return builder.Length == 0 ? $"GSS-API status {major}/{minor}" : builder.ToString();
    }

    private static void AppendStatus(StringBuilder builder, uint status, int type, nint mechanism)
    {
        uint context = 0;
        // A runaway message loop is bounded: GSS-API status chains are a handful of entries.
        for (var guard = 0; guard < 8; guard++)
        {
            var text = new GssBuffer();
            var result = gss_display_status(out _, status, type, mechanism, ref context, ref text);
            if (result != Complete) return;
            var piece = text.Value == 0 ? "" : Marshal.PtrToStringUTF8(text.Value, (int)text.Length) ?? "";
            if (text.Value != 0) _ = gss_release_buffer(out _, ref text);
            if (piece.Length > 0)
            {
                if (builder.Length > 0) builder.Append("; ");
                builder.Append(piece);
            }
            if (context == 0) return;
        }
    }
}
