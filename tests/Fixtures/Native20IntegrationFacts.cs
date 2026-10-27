using System.Xml.Linq;

namespace Knx.TestFixtures;

internal static class Native20IntegrationFacts
{
    public static bool Matches(Project project, string groupName)
    {
        if (project.Installations.Count != 1 || project.Installations[0].Areas.Count != 1
            || project.Installations[0].Areas[0].Lines.Count != 1) return false;
        var line = project.Installations[0].Areas[0].Lines[0];
        if (line.Devices.Count != 1 || line.Segments.Count != 0) return false;
        var device = line.Devices[0];
        return project.Id == Native20Fixture.ProjectId && project.Name == "Synthetic native 20"
            && project.Guid == "00000020-1111-2222-3333-444444444444" && project.Comment == "Synthetic metadata"
            && project.Installations[0].GroupAddressStyle == GroupAddressStyle.ThreeLevel
            && line.MediumTypeRefId == "MT-0" && line.DomainAddress == "42"
            && device.Id == Native20Fixture.DeviceId && device.Address.ToString() == "1.1.7"
            && device.Name == "Synthetic device" && device.ParameterValues.Count == 1
            && device.ParameterValues.TryGetValue("R-1", out var value) && value == "17"
            && device.Channels.Count == 1 && device.Channels[0].Id == "C-1" && device.Channels[0].RefId == "CH-1"
            && device.Channels[0].Name == "Synthetic channel" && device.ComObjectRefs.Count == 1
            && device.ComObjectRefs[0].Id == "O-1" && device.ComObjectRefs[0].ChannelId == "C-1"
            && device.ComObjectRefs[0].Links.SequenceEqual([Native20Fixture.GroupId])
            && ProjectQuery.GroupAddresses(project).Select(a => (a.Id, a.Name, Address: a.Address.ToString()))
                .OrderBy(a => a.Id, StringComparer.Ordinal).SequenceEqual(new[]
                {
                    (Native20Fixture.GroupId, groupName, "1/1/1"),
                    (Native20Fixture.SecondGroupId, "Status", "1/1/2"),
                });
    }

    public static bool ArchivePartsMatch(string path, string layout)
    {
        var parts = Native20Fixture.ProjectParts(path, layout);
        var prefix = Native20Fixture.Prefix(layout);
        XNamespace ns = Native20Fixture.Namespace;
        var info = Native20Fixture.Xml(parts[prefix + "project.xml"]);
        var project = info.Root?.Element(ns + "Project");
        return info.Root?.Name == ns + "KNX" && (string?)project?.Attribute("Id") == Native20Fixture.ProjectId
            && (string?)project?.Element(ns + "ProjectInformation")?.Attribute("Guid") == "00000020-1111-2222-3333-444444444444"
            && parts[prefix + "opaque.bin"].SequenceEqual(new byte[] { 0, 255, 17, 128 })
            && (layout != "nested" || Native20Fixture.ReadZip(File.ReadAllBytes(path))["outer.bin"].SequenceEqual(new byte[] { 255, 7, 0 }));
    }
}
