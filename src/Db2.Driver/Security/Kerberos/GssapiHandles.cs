using Microsoft.Win32.SafeHandles;

namespace JustyBase.Db2.Security.Kerberos;

/// <summary>Owns a <c>gss_name_t</c>. Released deterministically; the finalizer is only a safety net.</summary>
internal sealed class GssNameHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    internal static GssNameHandle FromNative(nint native)
    {
        var handle = new GssNameHandle();
        handle.SetHandle(native);
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        var native = handle;
        return GssapiNative.gss_release_name(out _, ref native) == GssapiNative.Complete;
    }
}

/// <summary>Owns a <c>gss_cred_id_t</c>. MIT also destroys any private memory ccache it created for it.</summary>
internal sealed class GssCredentialHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    internal static GssCredentialHandle FromNative(nint native)
    {
        var handle = new GssCredentialHandle();
        handle.SetHandle(native);
        return handle;
    }

    protected override bool ReleaseHandle()
    {
        var native = handle;
        return GssapiNative.gss_release_cred(out _, ref native) == GssapiNative.Complete;
    }
}

/// <summary>Owns a <c>gss_ctx_id_t</c> and deletes the security context on release.</summary>
internal sealed class GssContextHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
{
    internal void Assign(nint native)
    {
        if (native != 0 && IsInvalid) SetHandle(native);
    }

    protected override bool ReleaseHandle()
    {
        var native = handle;
        return GssapiNative.gss_delete_sec_context(out _, ref native, 0) == GssapiNative.Complete;
    }
}
