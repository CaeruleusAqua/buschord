using System.Buffers.Binary;
using System.Xml;

namespace Knx;

/// <summary>Exact immutable property sources from a linked archived factory buffer. No missing bytes are supplied.</summary>
public static class ArchivedPropertySources
{
    public static ArchivedPropertySourceIndex Read(Project project, DeviceInstance device)
    {
        if (!IdentityWithinBudget(project, device))
            return Read(new(new(ArchivedImageSourceStatus.InvalidRequest)));
        return Read(ArchivedImageSources.ReadInputs(project, device));
    }

    internal static bool IdentityWithinBudget(Project project, DeviceInstance device)
    {
        static bool Safe(string? value) => value is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
        if (project is null || device is null || !Safe(project.Id) || project.Guid is { } guid && !Safe(guid) || !Safe(device.Id)
            || !Safe(device.ProductRefId) || device.Hardware2ProgramRefId is { } hardware && !Safe(hardware)) return false;
        var count = 0; var idMatches = 0; var member = false;
        foreach (var candidate in ProjectQuery.DeviceList(project))
        {
            if (++count > 100000) return false;
            if (candidate.Id == device.Id) idMatches++;
            if (ReferenceEquals(candidate, device)) member = true;
        }
        return member && idMatches == 1;
    }

    internal static ArchivedPropertySourceIndex Read(ArchivedImageSources.Inputs inputs)
    {
        var sources = inputs.Manifest;
        var mask = sources.ResourceContext?.MaskVersionId;
        var profile = ParameterProfileContext.Read(inputs.Master, mask).ForApplication(mask, sources.ApplicationNamespace);
        ArchivedPropertySourceIndex Error(PropertySourceStatus status) => new(status, sources, profile.Selection);
        if (sources.Status == ArchivedImageSourceStatus.UnsupportedApplicationNamespace) return Error(PropertySourceStatus.UnsupportedApplication);
        if (sources.Status == ArchivedImageSourceStatus.LimitExceeded) return Error(PropertySourceStatus.LimitExceeded);
        if (sources.Status is ArchivedImageSourceStatus.InvalidDeclaration or ArchivedImageSourceStatus.InvalidRequest) return Error(PropertySourceStatus.InvalidDeclaration);
        if (sources.Status != ArchivedImageSourceStatus.Inspected || inputs.Application is null || sources.Binding is null)
            return Error(PropertySourceStatus.ApplicationUnavailable);
        if (sources.ApplicationNamespace is not ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23"))
            return Error(PropertySourceStatus.UnsupportedApplication);
        var factory = sources.Hardware?.FactoryImage;
        if (sources.Hardware?.Status != ArchivedHardwareSourceStatus.Inspected || factory?.Status != ArchivedFactoryImageStatus.Decoded)
            return Error(PropertySourceStatus.FactoryUnavailable);
        try
        {
            var groups = new Dictionary<string, (PropertySourceSelector Selector, bool Canonical, List<PropertySourceFragment> Fragments)>(StringComparer.Ordinal);
            for (var ordinal = 0; ordinal < factory.Segments.Count; ordinal++)
            {
                var segment = factory.Segments[ordinal]; var location = segment.Location;
                PropertySourceSelector? raw = location.AddressSpace switch
                {
                    FactoryImageAddressSpace.SystemProperty when location.NativeObjectIndex is { } native && location.PropertyId is { } pid
                        => new(PropertySourceKind.SystemProperty, pid, NativeObjectIndex: native),
                    FactoryImageAddressSpace.AppProperty when location.ApplicationObjectIndex is { } app && location.PropertyId is { } pid
                        => new(PropertySourceKind.AppProperty, pid, ApplicationObjectIndex: app),
                    FactoryImageAddressSpace.PropertyByType when location.ObjectType is { } type && location.Occurrence is { } occurrence && location.PropertyId is { } pid
                        => new(PropertySourceKind.ByType, pid, ObjectType: type, Occurrence: occurrence),
                    _ => null,
                };
                if (raw is null) continue;
                var (selector, canonical) = Normalize(raw, profile.Selection);
                var key = Key(selector, canonical);
                if (!groups.TryGetValue(key, out var group))
                    groups.Add(key, group = (selector, canonical, []));
                group.Fragments.Add(new(ordinal, raw, location.Component, location.ByteOffset, segment.Data, segment.Mask));
            }
            var entries = new List<PropertySourceEntry>();
            foreach (var pair in groups.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var group = pair.Value;
                var counts = group.Fragments.Where(f => f.Component == FactoryImageComponent.PropertyElementCount).Take(2).ToArray();
                if (counts.Length > 1) return Error(PropertySourceStatus.AmbiguousLayout);
                var data = group.Fragments.Where(f => f.Component == FactoryImageComponent.Data).OrderBy(f => f.ByteOffset).ToArray();
                ulong previousEnd = 0; var first = true;
                foreach (var fragment in data)
                {
                    if (fragment.ByteOffset is not { } offset) return Error(PropertySourceStatus.InvalidDeclaration);
                    var end = (ulong)offset + (uint)fragment.Data.Length;
                    if (end > (1UL << 32)) return Error(PropertySourceStatus.InvalidDeclaration);
                    if (!first && offset < previousEnd) return Error(PropertySourceStatus.AmbiguousLayout);
                    previousEnd = end; first = false;
                }
                var count = counts.FirstOrDefault();
                if (count is not null && (count.ByteOffset is not null || count.Data.Length != 2 || count.Mask is { Length: not 2 }))
                    return Error(PropertySourceStatus.InvalidDeclaration);
                ushort? encoded = count is null ? null : BinaryPrimitives.ReadUInt16BigEndian(count.Data.ToArray());
                var status = count is null ? PropertyCountStatus.Missing : count.Mask is null ? PropertyCountStatus.MaskMissing
                    : FullMask(count.Mask, 2) ? PropertyCountStatus.FullyValid : PropertyCountStatus.MaskPartial;
                var layout = Layout(profile, mask, group.Selector, group.Canonical);
                entries.Add(new(pair.Key, group.Selector, group.Canonical, data, count, encoded, status, layout));
            }
            var fingerprint = ParameterPatchSourceBinding.Hash(new
            {
                version = 1, source = sources.Binding.Fingerprint, profile = profile.Selection.Fingerprint, pdtRoleVersion = 1,
                factory = factory.BlobHash, entries = entries.Select(e => new
                {
                    e.PropertyKey, e.Selector, e.SelectorIsCanonical, e.PdtLayout, e.CountStatus,
                    fragments = e.DataFragments.Concat(e.CountFragment is { } count ? new[] { count } : []).Select(f => new
                    { f.SourceOrdinal, f.RawSelector, f.Component, f.ByteOffset, data = f.Data.Sha256, mask = f.Mask?.Sha256 }).ToArray(),
                }).ToArray(),
            });
            var binding = new PropertySourceBinding(sources.Binding, profile.Selection.Fingerprint, fingerprint);
            return new(PropertySourceStatus.Inspected, sources, profile.Selection, entries.ToArray(), fingerprint, binding);
        }
        catch (Exception failure) when (failure is XmlException or InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { return Error(PropertySourceStatus.InvalidDeclaration); }
    }

    internal static (PropertySourceSelector Selector, bool Canonical) Normalize(PropertySourceSelector selector, MaskProfileSelection profile)
    {
        if (selector.Kind == PropertySourceKind.ByType) return (selector, true);
        if (profile.Status != MaskProfileStatus.Supported || profile.FirstAppObjectIndex is not { } first) return (selector, false);
        if (selector.Kind == PropertySourceKind.SystemProperty && selector.NativeObjectIndex is { } native && native >= first)
            return (new(PropertySourceKind.AppProperty, selector.PropertyId, ApplicationObjectIndex: checked((ushort)(native - first))), true);
        return (selector, true);
    }
    internal static string Key(PropertySourceSelector selector, bool canonical = true)
        => "property-" + ParameterPatchSourceBinding.Hash(new { canonical, selector });
    internal static bool FullMask(ImmutablePatchBytes? mask, int length)
    {
        if (mask is null || mask.Length != length) return false;
        for (var i = 0; i < length; i++) if (mask[i] != byte.MaxValue) return false;
        return true;
    }
    private static PropertyPdtLayout Layout(ParameterProfileContext profile, string? mask, PropertySourceSelector selector, bool canonical)
    {
        ParameterStorageLocation? location = null;
        if (canonical && selector.PropertyId <= byte.MaxValue)
        {
            if (selector.Kind == PropertySourceKind.SystemProperty && selector.NativeObjectIndex is <= byte.MaxValue)
                location = new(ParameterStorageStatus.Supported, ParameterStorageKind.PropertyByIndex, ObjectIndex: (byte)selector.NativeObjectIndex.Value, PropertyId: (byte)selector.PropertyId);
            else if (selector.Kind == PropertySourceKind.AppProperty && selector.ApplicationObjectIndex is { } app && profile.Selection.FirstAppObjectIndex is { } first
                && (uint)app + first <= byte.MaxValue)
                location = new(ParameterStorageStatus.Supported, ParameterStorageKind.PropertyByIndex, ObjectIndex: (byte)(app + first), PropertyId: (byte)selector.PropertyId);
            else if (selector.Kind == PropertySourceKind.ByType && selector.ObjectType is { } type && selector.Occurrence is <= byte.MaxValue)
                location = new(ParameterStorageStatus.Supported, ParameterStorageKind.PropertyByType, ObjectType: type, Occurrence: (byte)selector.Occurrence.Value, PropertyId: (byte)selector.PropertyId);
        }
        return location is null ? new(profile.Selection.Fingerprint, null, null, null, PropertyPdtStatus.Unknown)
            : PropertyPdtRoles.Read(profile.Interpret(location, mask));
    }
}
