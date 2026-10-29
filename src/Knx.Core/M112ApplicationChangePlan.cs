namespace Knx;

/// <summary>A proposed EEPROM change. Unknown source bits still require a read from the confirmed device.</summary>
public sealed record M112ConfigurationChange(ushort Address, ImmutablePatchBytes BaselineData,
    ImmutablePatchBytes BaselineValidity, ImmutablePatchBytes DesiredData, ImmutablePatchBytes DesiredValidity)
{
    public int Length => DesiredData.Length;
    public bool RequiresDeviceRead => Enumerable.Range(0, Length).Any(i => (DesiredValidity[i] & ~BaselineValidity[i]) != 0);
}

/// <summary>Two independently bound project states with the same application definition and load geometry.</summary>
public sealed class M112ApplicationChangePlan
{
    private readonly Project _baselineProject, _desiredProject;
    private readonly DeviceInstance _baselineDevice, _desiredDevice;
    public M112ApplicationImage Baseline { get; }
    public M112ApplicationImage Desired { get; }
    public IReadOnlyList<M112ConfigurationChange> Changes { get; }
    public string Fingerprint { get; }
    public bool HasChanges => Changes.Count != 0;

    private M112ApplicationChangePlan(Project baselineProject, DeviceInstance baselineDevice,
        Project desiredProject, DeviceInstance desiredDevice)
    {
        _baselineProject = baselineProject; _baselineDevice = baselineDevice;
        _desiredProject = desiredProject; _desiredDevice = desiredDevice;
        Baseline = OfflineM112ApplicationBuild.Build(baselineProject, baselineDevice);
        Desired = OfflineM112ApplicationBuild.BuildTarget(desiredProject, desiredDevice);
        if (Baseline.Address != Desired.Address || Baseline.LayoutFingerprint != Desired.LayoutFingerprint)
            throw new InvalidOperationException("Configuration changes require the same physical target, application definition and load geometry.");
        if (Baseline.Objects.Count != Desired.Objects.Count || Baseline.Objects.Where((o, i) => o.ReadOnInitEnabled != Desired.Objects[i].ReadOnInitEnabled).Any())
            throw new NotSupportedException("Read-on-initialization changes require an additional finalized M112 writer.");
        var changes = new List<M112ConfigurationChange>();
        foreach (var target in Desired.Segments.Where(s => s.MemoryType == 3))
        {
            var source = Baseline.Segments.Single(s => s.ObjectIndex == target.ObjectIndex && s.Address == target.Address);
            var sourceData = source.Data.ToArray(); var sourceValidity = source.Validity.ToArray();
            var targetData = target.Data.ToArray(); var targetValidity = target.Validity.ToArray();
            for (var offset = 0; offset < target.Size;)
            {
                if (!Changed(offset)) { offset++; continue; }
                var start = offset++;
                while (offset < target.Size && Changed(offset)) offset++;
                changes.Add(new(checked((ushort)(target.Address + start)), Slice(sourceData), Slice(sourceValidity),
                    Slice(targetData), Slice(targetValidity)));
                ImmutablePatchBytes Slice(byte[] bytes) => new(bytes.AsSpan(start, offset - start));
            }
            bool Changed(int i) => (((sourceData[i] ^ targetData[i]) | ~sourceValidity[i]) & targetValidity[i]) != 0;
        }
        Changes = Array.AsReadOnly(changes.ToArray());
        Fingerprint = NativeGeneralHash.Of(new { profile = "M112ConfigurationChange1", baseline = Baseline.Fingerprint,
            desired = Desired.Fingerprint, Desired.LayoutFingerprint,
            changes = Changes.Select(c => new { c.Address, baseline = c.BaselineData.Sha256, baselineValidity = c.BaselineValidity.Sha256,
                desired = c.DesiredData.Sha256, desiredValidity = c.DesiredValidity.Sha256 }) });
    }

    public bool IsCurrent() => Baseline.IsCurrent(_baselineProject, _baselineDevice) && Desired.IsCurrent(_desiredProject, _desiredDevice);
    public bool AreInputsCurrent() => Baseline.AreInputsCurrent(_baselineProject, _baselineDevice) && Desired.AreInputsCurrent(_desiredProject, _desiredDevice);

    public static M112ApplicationChangePlan Prepare(Project baselineProject, DeviceInstance baselineDevice,
        Project desiredProject, DeviceInstance desiredDevice)
        => new(baselineProject, baselineDevice, desiredProject, desiredDevice);
}
