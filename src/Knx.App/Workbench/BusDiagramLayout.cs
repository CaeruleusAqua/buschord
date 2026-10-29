using Knx.App.Localization;

namespace Knx.App.Workbench;

/// <summary>Disposable read projection of the actual containment tree, never a second project.</summary>
public sealed record BusDiagramNode(string Key, string? ParentKey, string Kind, string Title,
    string Detail, WorkbenchTarget? Target, double X, double Y)
{
    public const double Width = 210, Height = 54;
}

public sealed record BusDiagramLayout(IReadOnlyList<BusDiagramNode> Nodes, double Width, double Height)
{
    public static BusDiagramLayout Create(Project project)
    {
        var nodes = new List<BusDiagramNode>();
        var row = 0;
        string W(string key) => WorkbenchStrings.Get(key);
        string Name(string? name, string fallback) => string.IsNullOrWhiteSpace(name) ? fallback : name;
        string Medium(string id) => id switch { "MT-0" => "TP", "MT-1" => "PL", "MT-2" => "RF", "MT-5" => "IP", _ => id };
        void Branch(string key, string? parent, string kind, string title, string detail,
            WorkbenchTarget? target, int column, Action children)
        {
            var first = row;
            var index = nodes.Count;
            nodes.Add(new(key, parent, kind, title, detail, target, 24 + column * 254, 0));
            children();
            if (row == first) row++;
            nodes[index] = nodes[index] with { Y = 24 + (first + row - 1) * 36 };
        }
        void Device(DeviceInstance device, string parent, int column)
        {
            var hardware = device.Hardware2ProgramRefId is { } program && project.Hardware.TryGetValue(program, out var hp)
                ? hp : project.Hardware.GetValueOrDefault(device.ProductRefId);
            nodes.Add(new("device:" + device.Id, parent, "device", WorkbenchProjection.DeviceName(project, device),
                device.Address + (hardware?.IsCoupler == true ? " · " + W("DiagramCoupler") : ""),
                new("device", device.Id), 24 + column * 254, 24 + row++ * 72));
        }
        foreach (var installation in project.Installations)
        {
            Branch("installation:" + installation.Id, null, "installation", Name(installation.Name, W("DiagramInstallation")),
                W("DiagramInstallation"), null, 0, () =>
            {
                foreach (var area in installation.Areas.OrderBy(a => a.Address))
                {
                    Branch("area:" + area.Id, "installation:" + installation.Id, "area", Name(area.Name, W("DiagramArea")),
                        $"{W("DiagramArea")} {area.Address}", null, 1, () =>
                    {
                        foreach (var device in area.Devices.OrderBy(d => d.Address.Raw)) Device(device, "area:" + area.Id, 4);
                        foreach (var line in area.Lines.OrderBy(l => l.Address))
                        {
                            Branch("line:" + line.Id, "area:" + area.Id, "line", Name(line.Name, W("Line")),
                                $"{area.Address}.{line.Address} · {Medium(line.MediumTypeRefId)}", new("line", line.Id), 2, () =>
                            {
                                foreach (var device in line.Devices.OrderBy(d => d.Address.Raw)) Device(device, "line:" + line.Id, 4);
                                foreach (var segment in line.Segments.OrderBy(s => s.Address))
                                    Branch("segment:" + segment.Id, "line:" + line.Id, "segment", Name(segment.Name, W("DiagramSegment")),
                                        $"{W("DiagramSegment")} {segment.Address} · {Medium(segment.MediumTypeRefId ?? line.MediumTypeRefId)}",
                                        new("line", line.Id), 3, () =>
                                        {
                                            foreach (var device in segment.Devices.OrderBy(d => d.Address.Raw)) Device(device, "segment:" + segment.Id, 4);
                                        });
                            });
                        }
                    });
                }
            });
            row++;
        }
        return new(nodes, nodes.Count == 0 ? 500 : nodes.Max(n => n.X) + BusDiagramNode.Width + 24,
            Math.Max(200, row * 72 + 24));
    }
}
