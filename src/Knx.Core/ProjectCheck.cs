namespace Knx;

public enum CheckSeverity { Info, Warning, Error }

/// <summary>One finding of the project check (ETS "Projektprüfung").</summary>
public sealed record CheckFinding(CheckSeverity Severity, string Code, string Path, string Message);

/// <summary>
/// Structural project check: duplicate addresses, dangling links, missing datapoint
/// types and topology limits. Rules mirror what the ETS project check reports.
/// </summary>
public static class ProjectCheck
{
    public static IReadOnlyList<CheckFinding> Run(Project project)
    {
        var findings = new List<CheckFinding>();

        if (project.Installations.Count == 0)
            findings.Add(new(CheckSeverity.Error, "P001", project.Name, "Projekt enthält keine Installation."));

        CheckTopology(project, findings);
        CheckDevices(project, findings);
        CheckGroupAddresses(project, findings);
        CheckLinks(project, findings);
        return findings;
    }

    private static void CheckTopology(Project project, List<CheckFinding> findings)
    {
        foreach (var inst in project.Installations)
        {
            foreach (var area in inst.Areas)
            {
                if (area.Address > 15)
                    findings.Add(new(CheckSeverity.Error, "P010", $"Bereich {area.Address}",
                        "Bereichsadresse muss zwischen 0 und 15 liegen."));
                foreach (var line in area.Lines)
                {
                    if (line.Address > 15)
                        findings.Add(new(CheckSeverity.Error, "P011", $"Linie {area.Address}.{line.Address}",
                            "Linienadresse muss zwischen 0 und 15 liegen."));
                    if (line.Segments.Count > 1)
                        findings.Add(new(CheckSeverity.Warning, "P012", $"Linie {area.Address}.{line.Address}",
                            $"{line.Segments.Count} Segmente – nur das erste Segment ist im ETS-Standard zulässig."));
                }
            }
        }
    }

    private static void CheckDevices(Project project, List<CheckFinding> findings)
    {
        var byAddress = new Dictionary<ushort, List<DeviceInstance>>();
        foreach (var device in ProjectQuery.DeviceList(project))
        {
            if (!byAddress.TryGetValue(device.Address.Raw, out var list))
                byAddress[device.Address.Raw] = list = [];
            list.Add(device);

            if (device.Address.Device == 0)
                findings.Add(new(CheckSeverity.Error, "P020", device.Address.ToString(),
                    $"Gerät '{device.Name}' hat die ungültige Adresse {device.Address}."));
            if (string.IsNullOrWhiteSpace(device.ProductRefId))
                findings.Add(new(CheckSeverity.Warning, "P021", device.Address.ToString(),
                    $"Gerät '{device.Name}' hat keine Produktreferenz."));
            if (string.IsNullOrWhiteSpace(device.Name))
                findings.Add(new(CheckSeverity.Info, "P022", device.Address.ToString(),
                    "Gerät ohne Namen."));
            if (device.ComObjectRefs.Count == 0)
                findings.Add(new(CheckSeverity.Info, "P023", device.Address.ToString(),
                    $"Gerät '{device.Name}' hat keine Kommunikationsobjekte."));
        }

        foreach (var (raw, devices) in byAddress.Where(p => p.Value.Count > 1))
            findings.Add(new(CheckSeverity.Error, "P025", new IndividualAddress(raw).ToString(),
                $"Adresse {new IndividualAddress(raw)} ist {devices.Count}× vergeben: " +
                string.Join(", ", devices.Select(d => d.Name ?? "?"))));
    }

    private static void CheckGroupAddresses(Project project, List<CheckFinding> findings)
    {
        var byAddress = new Dictionary<ushort, List<GroupAddressEntry>>();
        foreach (var entry in ProjectQuery.GroupAddresses(project))
        {
            if (!byAddress.TryGetValue(entry.Address.Raw, out var list))
                byAddress[entry.Address.Raw] = list = [];
            list.Add(entry);

            if (string.IsNullOrWhiteSpace(entry.DatapointType))
                findings.Add(new(CheckSeverity.Warning, "P030", entry.Address.ToString(),
                    $"Gruppenadresse '{entry.Name}' hat keinen Datapoint-Typ."));
            if (string.IsNullOrWhiteSpace(entry.Name))
                findings.Add(new(CheckSeverity.Info, "P031", entry.Address.ToString(),
                    "Gruppenadresse ohne Namen."));
        }

        foreach (var (raw, entries) in byAddress.Where(p => p.Value.Count > 1))
            findings.Add(new(CheckSeverity.Error, "P032", new GroupAddress(raw).ToString(),
                $"Gruppenadresse {new GroupAddress(raw)} ist {entries.Count}× vergeben: " +
                string.Join(", ", entries.Select(e => e.Name))));

        // Address outside its range bounds (ETS keeps RangeStart/RangeEnd on every range)
        foreach (var inst in project.Installations)
            foreach (var range in ProjectQuery.AllRanges(inst))
            {
                foreach (var entry in range.Addresses)
                {
                    var sub = entry.Address.Raw & 0xFF;
                    if (range.RangeEnd > 0 && (sub < range.RangeStart || sub > range.RangeEnd))
                        findings.Add(new(CheckSeverity.Warning, "P033", entry.Address.ToString(),
                            $"Adresse liegt außerhalb des Bereichs '{range.Name}' ({range.RangeStart}..{range.RangeEnd})."));
                }
            }
    }

    private static void CheckLinks(Project project, List<CheckFinding> findings)
    {
        var known = ProjectQuery.GroupAddresses(project).Select(a => a.Id).ToHashSet();
        var linked = new HashSet<string>();

        foreach (var (device, comObject) in ProjectQuery.ComObjectRefs(project))
        {
            if (comObject.Links.Count == 0)
            {
                findings.Add(new(CheckSeverity.Info, "P040", device.Address.ToString(),
                    $"Kommunikationsobjekt '{comObject.Text ?? comObject.Id}' von '{device.Name}' ist nicht verknüpft."));
                continue;
            }
            foreach (var link in comObject.Links)
            {
                linked.Add(link);
                if (!known.Contains(link))
                    findings.Add(new(CheckSeverity.Error, "P041", device.Address.ToString(),
                        $"Verknüpfung '{link}' von '{device.Name}' zeigt auf keine existierende Gruppenadresse."));
            }
        }

        foreach (var entry in ProjectQuery.GroupAddresses(project))
            if (!linked.Contains(entry.Id))
                findings.Add(new(CheckSeverity.Info, "P042", entry.Address.ToString(),
                    $"Gruppenadresse '{entry.Name}' ist mit keinem Kommunikationsobjekt verknüpft."));
    }

    public static int Count(IEnumerable<CheckFinding> findings, CheckSeverity severity)
        => findings.Count(f => f.Severity == severity);
}
