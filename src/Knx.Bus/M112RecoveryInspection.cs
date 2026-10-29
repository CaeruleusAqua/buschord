namespace Knx.Bus;

public sealed record M112RecoverySegmentSummary(byte ObjectIndex, byte MemoryType, ushort Address, ushort Size, string OriginalSha256);
/// <summary>Validated local recovery metadata; contains no captured memory bytes or executable recipe.</summary>
public sealed record M112RecoveryInspection(string RecoveryPath, string FileSha256, ManagementDeviceIdentity Identity,
    string ImageFingerprint, string HardwareSha256, IReadOnlyList<M112RecoverySegmentSummary> Segments)
{
    public string RecoveryScope => "CapturedConfigurationWithSourceLoadRecipe";
}
