using System.Text.Json;
using Knx.Workbench;

namespace Knx.App.Workbench;

/// <summary>Immutable, enqueue-time payload; a domain provider can supply richer supported configuration.</summary>
public sealed record FrozenConfiguration(string Signature, string PayloadJson);

public static class DeviceConfigurationSnapshot
{
    private const int PayloadVersion = 2;
    /// <summary>
    /// Known native fields only, not a downloadable application image. Names/building assignments are
    /// deliberately excluded. Inject the domain signature provider for extension metadata and security.
    /// </summary>
    public static FrozenConfiguration Freeze(Project project, DeviceInstance device)
    {
        var location = ProjectQuery.Devices(project).FirstOrDefault(x => x.Device.Id == device.Id);
        var groups = ProjectQuery.GroupAddresses(project).ToDictionary(g => g.Id, StringComparer.Ordinal);
        var payload = JsonSerializer.Serialize(new
        {
            Version = PayloadVersion,
            device.ProductRefId, device.Hardware2ProgramRefId,
            Address = device.Address.ToString(),
            Area = location.Area?.Id, Line = location.Line?.Id, Segment = location.Segment?.Id,
            AreaAddress = location.Area?.Address, LineAddress = location.Line?.Address,
            SegmentAddress = location.Segment?.Address, Medium = location.Line?.MediumTypeRefId,
            SegmentMedium = location.Segment?.MediumTypeRefId,
            LineDomainAddress = location.Line?.DomainAddress, SegmentDomainAddress = location.Segment?.DomainAddress,
            Parameters = device.ParameterValues.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(),
            Objects = device.ComObjectRefs.OrderBy(o => o.Id, StringComparer.Ordinal).Select(o => new
            {
                o.Id, o.ComObjectId, o.Number, o.Device,
                Links = o.Links.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id =>
                {
                    var ga = groups.GetValueOrDefault(id);
                    return new { Id = id, Address = ga?.Address.ToString(), ga?.DatapointType };
                }).ToArray(),
            }).ToArray(),
        });
        return new(WorkbenchProject.ConfigurationSignature(project, device.Id), payload);
    }

    /// <summary>
    /// Deterministic classification of two frozen payloads for <c>ProgrammingSession.ResetForChange</c>.
    /// Name/room edits are absent from the payload and therefore classify as <see cref="DeviceChangeKind.None"/>.
    /// An unknown payload difference falls back to <see cref="DeviceChangeKind.Configuration"/> rather than
    /// claiming that nothing bus-relevant changed. Legacy or unknown payload versions cannot prove
    /// equality and conservatively invalidate all load flags, even when their opaque hashes match.
    /// </summary>
    public static DeviceChangeKind ClassifyChange(FrozenConfiguration before, FrozenConfiguration after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        using var previous = JsonDocument.Parse(before.PayloadJson);
        using var current = JsonDocument.Parse(after.PayloadJson);
        if (!HasCurrentVersion(previous.RootElement) || !HasCurrentVersion(current.RootElement))
            return DeviceChangeKind.IndividualAddress | DeviceChangeKind.Product | DeviceChangeKind.Configuration;
        if (before.Signature == after.Signature) return DeviceChangeKind.None;
        var change = DeviceChangeKind.None;
        if (!Same("ProductRefId") || !Same("Hardware2ProgramRefId")) change |= DeviceChangeKind.Product;
        if (!Same("Address")) change |= DeviceChangeKind.IndividualAddress;
        if (!Same("Area") || !Same("Line") || !Same("Segment") ||
            !Same("AreaAddress") || !Same("LineAddress") || !Same("SegmentAddress"))
            change |= DeviceChangeKind.Topology;
        if (!Same("Medium") || !Same("SegmentMedium") || !Same("LineDomainAddress") || !Same("SegmentDomainAddress"))
            change |= DeviceChangeKind.Configuration;
        if (!Same("Parameters")) change |= DeviceChangeKind.Parameters;
        if (!Same("Objects")) change |= DeviceChangeKind.GroupAddressLink;
        return change == DeviceChangeKind.None ? DeviceChangeKind.Configuration : change;

        bool Same(string name) => previous.RootElement.TryGetProperty(name, out var left) &&
            current.RootElement.TryGetProperty(name, out var right) &&
            left.GetRawText() == right.GetRawText();
    }

    private static bool HasCurrentVersion(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.Number
        && version.TryGetInt32(out var value) && value == PayloadVersion;
}
