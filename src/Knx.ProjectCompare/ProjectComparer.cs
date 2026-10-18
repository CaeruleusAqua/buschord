namespace Knx.ProjectCompare;

/// <summary>Typ einer Modifikation zwischen zwei Projekten.</summary>
public enum ModificationType { Added, Removed, Changed, Unchanged }

/// <summary>Ein einzelner Unterschied zwischen zwei Projekten.</summary>
public sealed class DifferenceItem
{
    public required string Path { get; init; }
    public required string ObjectType { get; init; }
    public required string ObjectName { get; init; }
    public required ModificationType Modification { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    public string? Details { get; init; }
}

/// <summary>Bericht über alle Unterschiede zwischen zwei Projekten.</summary>
public sealed class DifferenceReport
{
    public required string LeftProjectName { get; init; }
    public required string RightProjectName { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public List<DifferenceItem> Items { get; } = [];
    public int AddedCount => Items.Count(i => i.Modification == ModificationType.Added);
    public int RemovedCount => Items.Count(i => i.Modification == ModificationType.Removed);
    public int ChangedCount => Items.Count(i => i.Modification == ModificationType.Changed);
    public int UnchangedCount => Items.Count(i => i.Modification == ModificationType.Unchanged);
}

/// <summary>Vergleicht zwei Projekte und erzeugt einen DifferenceReport.</summary>
public static class ProjectComparer
{
    public static DifferenceReport Compare(Project left, Project right)
    {
        var report = new DifferenceReport
        {
            LeftProjectName = left.Name,
            RightProjectName = right.Name,
            GeneratedAt = DateTimeOffset.Now,
        };

        // Projekt-Metadaten vergleichen
        if (left.Name != right.Name)
            report.Items.Add(new DifferenceItem
            {
                Path = "Project/Name",
                ObjectType = "Project",
                ObjectName = left.Name,
                Modification = ModificationType.Changed,
                OldValue = left.Name,
                NewValue = right.Name,
            });

        if (left.Comment != right.Comment)
            report.Items.Add(new DifferenceItem
            {
                Path = "Project/Comment",
                ObjectType = "Project",
                ObjectName = left.Name,
                Modification = ModificationType.Changed,
                OldValue = left.Comment,
                NewValue = right.Comment,
            });

        // Installationen vergleichen
        var leftInst = left.Installations.ToDictionary(i => i.Id);
        var rightInst = right.Installations.ToDictionary(i => i.Id);

        foreach (var (id, inst) in leftInst)
        {
            if (!rightInst.ContainsKey(id))
            {
                report.Items.Add(new DifferenceItem
                {
                    Path = $"Installation/{id}",
                    ObjectType = "Installation",
                    ObjectName = inst.Name ?? id,
                    Modification = ModificationType.Removed,
                });
            }
            else
            {
                CompareInstallation(inst, rightInst[id], report);
            }
        }

        foreach (var (id, inst) in rightInst)
        {
            if (!leftInst.ContainsKey(id))
            {
                report.Items.Add(new DifferenceItem
                {
                    Path = $"Installation/{id}",
                    ObjectType = "Installation",
                    ObjectName = inst.Name ?? id,
                    Modification = ModificationType.Added,
                });
            }
        }

        // Gruppenadressen vergleichen
        var leftGas = FlattenGroupAddresses(left).ToDictionary(g => g.Address.Raw);
        var rightGas = FlattenGroupAddresses(right).ToDictionary(g => g.Address.Raw);

        foreach (var (raw, ga) in leftGas)
        {
            if (!rightGas.ContainsKey(raw))
            {
                report.Items.Add(new DifferenceItem
                {
                    Path = $"GroupAddress/{ga.Address}",
                    ObjectType = "GroupAddress",
                    ObjectName = ga.Name,
                    Modification = ModificationType.Removed,
                    OldValue = ga.Name,
                });
            }
            else
            {
                var rightGa = rightGas[raw];
                if (ga.Name != rightGa.Name || ga.DatapointType != rightGa.DatapointType ||
                    ga.Description != rightGa.Description)
                {
                    report.Items.Add(new DifferenceItem
                    {
                        Path = $"GroupAddress/{ga.Address}",
                        ObjectType = "GroupAddress",
                        ObjectName = ga.Name,
                        Modification = ModificationType.Changed,
                        OldValue = $"{ga.Name} ({ga.DatapointType})",
                        NewValue = $"{rightGa.Name} ({rightGa.DatapointType})",
                    });
                }
            }
        }

        foreach (var (raw, ga) in rightGas)
        {
            if (!leftGas.ContainsKey(raw))
            {
                report.Items.Add(new DifferenceItem
                {
                    Path = $"GroupAddress/{ga.Address}",
                    ObjectType = "GroupAddress",
                    ObjectName = ga.Name,
                    Modification = ModificationType.Added,
                    NewValue = ga.Name,
                });
            }
        }

        return report;
    }

    private static void CompareInstallation(Installation left, Installation right, DifferenceReport report)
    {
        if (left.Name != right.Name)
            report.Items.Add(new DifferenceItem
            {
                Path = $"Installation/{left.Id}/Name",
                ObjectType = "Installation",
                ObjectName = left.Name ?? left.Id,
                Modification = ModificationType.Changed,
                OldValue = left.Name,
                NewValue = right.Name,
            });

        // Geräte vergleichen
        var leftDevices = FlattenDevices(left).ToDictionary(d => d.Address.Raw);
        var rightDevices = FlattenDevices(right).ToDictionary(d => d.Address.Raw);

        foreach (var (addr, dev) in leftDevices)
        {
            if (!rightDevices.ContainsKey(addr))
            {
                report.Items.Add(new DifferenceItem
                {
                    Path = $"Device/{dev.Address}",
                    ObjectType = "Device",
                    ObjectName = dev.Name ?? dev.Address.ToString(),
                    Modification = ModificationType.Removed,
                });
            }
            else
            {
                var rightDev = rightDevices[addr];
                if (dev.Name != rightDev.Name || dev.ProductRefId != rightDev.ProductRefId)
                {
                    report.Items.Add(new DifferenceItem
                    {
                        Path = $"Device/{dev.Address}",
                        ObjectType = "Device",
                        ObjectName = dev.Name ?? dev.Address.ToString(),
                        Modification = ModificationType.Changed,
                        OldValue = $"{dev.Name} ({dev.ProductRefId})",
                        NewValue = $"{rightDev.Name} ({rightDev.ProductRefId})",
                    });
                }
            }
        }

        foreach (var (addr, dev) in rightDevices)
        {
            if (!leftDevices.ContainsKey(addr))
            {
                report.Items.Add(new DifferenceItem
                {
                    Path = $"Device/{dev.Address}",
                    ObjectType = "Device",
                    ObjectName = dev.Name ?? dev.Address.ToString(),
                    Modification = ModificationType.Added,
                });
            }
        }
    }

    private static List<GroupAddressEntry> FlattenGroupAddresses(Project project)
    {
        var result = new List<GroupAddressEntry>();
        foreach (var inst in project.Installations)
            foreach (var range in inst.MainRanges)
                CollectGroupAddresses(range, result);
        return result;
    }

    private static void CollectGroupAddresses(GroupRange range, List<GroupAddressEntry> result)
    {
        result.AddRange(range.Addresses);
        foreach (var child in range.Ranges) CollectGroupAddresses(child, result);
    }

    private static List<DeviceInstance> FlattenDevices(Installation inst)
    {
        var result = new List<DeviceInstance>();
        foreach (var area in inst.Areas)
        {
            result.AddRange(area.Devices);
            foreach (var line in area.Lines)
            {
                result.AddRange(line.Devices);
                foreach (var seg in line.Segments) result.AddRange(seg.Devices);
            }
        }
        return result;
    }
}
