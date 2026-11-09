namespace JustyBase.Db2.Protocol;

/// <summary>
/// JDC v1 resource defaults. The driver does not expose protocol-limit DSN keys;
/// these fixed values are the documented defaults and hard ceilings (LIM-01/LIM-02).
/// </summary>
internal static class Db2ProtocolLimits
{
    internal const int MaxDssSize = 64 * 1024 * 1024;
    internal const int MaxDdmObjectSize = 64 * 1024 * 1024;
    internal const int MaxReplyChainPackets = 4096;
    internal const long MaxReplyChainBytes = 256L * 1024 * 1024;
    internal const int MaxMaterializedLobBytes = 16 * 1024 * 1024;
    internal const long MaxMaterializedResultBytes = 32L * 1024 * 1024;
    internal const long MaxStreamedLobBytes = 1024L * 1024 * 1024;
    internal const int LobChunkBytes = 64 * 1024;
}
