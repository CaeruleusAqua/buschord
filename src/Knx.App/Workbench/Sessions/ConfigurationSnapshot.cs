using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Knx.App.Workbench;

/// <summary>Immutable, enqueue-time payload; a domain provider can supply richer supported configuration.</summary>
public sealed record FrozenConfiguration(string Signature, string PayloadJson);

public static class DeviceConfigurationSnapshot
{
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
            device.ProductRefId, device.Hardware2ProgramRefId,
            Address = device.Address.ToString(),
            Area = location.Area?.Id, Line = location.Line?.Id, Segment = location.Segment?.Id,
            AreaAddress = location.Area?.Address, LineAddress = location.Line?.Address,
            SegmentAddress = location.Segment?.Address, Medium = location.Line?.MediumTypeRefId,
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
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), payload);
    }
}
