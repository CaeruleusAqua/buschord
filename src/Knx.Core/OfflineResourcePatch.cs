namespace Knx;

/// <summary>Pure, bounded source-resource preview. No script, device-image, archive or bus operation is performed.</summary>
public static class OfflineResourcePatch
{
    public static OfflineResourcePlan Preview(Project project, DeviceInstance device,
        IReadOnlyList<ParameterPatchValue> subset, ParameterPatchByteOrder byteOrder)
        => OfflineParameterPatch.PreviewResources(project, device, subset, byteOrder);
}
