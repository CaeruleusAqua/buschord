using System.Xml.Linq;

namespace Knx.TestFixtures;

internal static class Native22IntegrationFacts
{
    public static bool Matches(Project project, string groupName)
    {
        if (project.Installations.Count != 1 || project.Installations[0].Areas.Count != 1
            || project.Installations[0].Areas[0].Lines.Count != 1) return false;
        var line = project.Installations[0].Areas[0].Lines[0];
        if (line.Devices.Count != 0 || line.Segments.Count != 1 || line.Segments[0].Devices.Count != 1) return false;
        var segment = line.Segments[0];
        var device = segment.Devices[0];
        return project.Id == Native22Fixture.ProjectId && project.Name == "Synthetic native 22"
            && project.Guid == "00000022-1111-2222-3333-444444444444" && project.Comment == "Synthetic metadata 22"
            && project.Installations[0].GroupAddressStyle == GroupAddressStyle.ThreeLevel
            && segment.MediumTypeRefId == "MT-0" && segment.DomainAddress == "42"
            && device.Id == Native22Fixture.DeviceId && device.Address.ToString() == "1.1.7"
            && device.Name == "Synthetic device 22" && device.ParameterValues.Count == 1
            && device.ParameterValues.TryGetValue("R-22", out var value) && value == "17"
            && device.Channels.Count == 1 && device.Channels[0].Id == "C-22" && device.Channels[0].RefId == "CH-22"
            && device.Channels[0].Name == "Channel 22" && device.ComObjectRefs.Count == 1
            && device.ComObjectRefs[0].Id == "O-22" && device.ComObjectRefs[0].ChannelId == "C-22"
            && device.ComObjectRefs[0].Links.SequenceEqual([Native22Fixture.GroupId])
            && ProjectQuery.GroupAddresses(project).Select(a => (a.Id, a.Name, Address: a.Address.ToString()))
                .OrderBy(a => a.Id, StringComparer.Ordinal).SequenceEqual(new[]
                {
                    (Native22Fixture.GroupId, groupName, "1/1/1"),
                    (Native22Fixture.SecondGroupId, "Status 22", "1/1/2"),
                });
    }

    public static bool ArchivePartsMatch(string path, string layout)
    {
        var parts = Native22Fixture.ProjectParts(path, layout);
        var prefix = Native22Fixture.Prefix(layout);
        XNamespace ns = Native22Fixture.Namespace;
        var info = Native22Fixture.Xml(parts[prefix + "project.xml"]);
        var project = info.Root?.Element(ns + "Project");
        var data = Native22Fixture.Xml(parts[prefix + "0.xml"]);
        var device = data.Descendants(ns + "DeviceInstance").Single();
        return info.Root?.Name == ns + "KNX" && (string?)project?.Attribute("Id") == Native22Fixture.ProjectId
            && (string?)project?.Element(ns + "ProjectInformation")?.Attribute("Guid") == "00000022-1111-2222-3333-444444444444"
            && (string?)data.Descendants(ns + "Installation").Single().Attribute("Ipv6InstallationId") == "18446744073709551615"
            && (string?)device.Attribute("IsSlowResender") == "true"
            && device.Element(ns + "AdditionalAddresses")?.Elements(ns + "Address").Single() is { } additional
            && additional.Attribute("Address") is null && (string?)additional.Attribute("Name") == "Unassigned secondary"
            && parts[prefix + "opaque.bin"].SequenceEqual(new byte[] { 0, 255, 22, 128 })
            && (layout != "nested" || Native22Fixture.ReadZip(File.ReadAllBytes(path))["outer.bin"].SequenceEqual(new byte[] { 255, 22, 0 }));
    }
}
