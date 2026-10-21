using Knx.Catalog;

namespace Knx;

public enum CheckSeverity { Info, Warning, Error }

/// <summary>One finding of the project check (ETS "Projektprüfung").</summary>
public sealed record CheckFinding(CheckSeverity Severity, string Code, string Path, string Message);

/// <summary>
/// Structural project check over topology, group addresses and links. Every rule is a pure,
/// deterministic function of the project model and report-only; the check never mutates or repairs.
/// <para>
/// Rules mirror what the ETS project check reports where this model carries the evidence
/// (analysis/ets/commissioning.md §7, analysis/ets/addresses.md §13). Limits: the model has no bus
/// current/power-supply, repeater, coupler-capability, battery/broker or router fields, so
/// PowerConsumptionCheck, RepeaterCheck, SegmentCouplerCheck, RouterTopologyCheck,
/// BackboneLineIpCheck and BrokerForBatteryPoweredDevicesCheck stay silent instead of guessing; only
/// the existence of a second segment or an IP line is reported as an explicit <see cref="CheckSeverity.Info"/>
/// limitation. Identifiers are checked for emptiness and uniqueness, not for the ETS3 name/number
/// length limits (those need master-data constants). Application-program status (broken, download
/// information incomplete) needs catalogue data and is only checked by the overload that receives
/// it; the BusChord workbench metadata carries no such status.
/// </para>
/// </summary>
public static class ProjectCheck
{
    /// <summary>KNX medium reference of IP lines (same mapping as the workbench JSON reader).</summary>
    private const string IpMedium = "MT-5";

    public static IReadOnlyList<CheckFinding> Run(Project project) => Run(project, null);

    /// <summary>
    /// Runs the check and additionally reports the catalogue status of each device's application
    /// program. <paramref name="applications"/> is keyed by application-program reference id
    /// (<see cref="DeviceInstance.Hardware2ProgramRefId"/>). Pass NULL when no catalogue is loaded:
    /// an absent catalogue says nothing about the project and must not produce findings.
    /// </summary>
    public static IReadOnlyList<CheckFinding> Run(Project project, IReadOnlyDictionary<string, CatalogApplicationProgram>? applications)
    {
        ArgumentNullException.ThrowIfNull(project);
        var findings = new List<CheckFinding>();

        if (project.Installations.Count == 0)
            findings.Add(new(CheckSeverity.Error, "P001", project.Name, "Projekt enthält keine Installation."));

        CheckIdentifiers(project, findings);
        CheckTopology(project, findings);
        CheckDevices(project, findings);
        CheckGroupAddresses(project, findings);
        CheckLinks(project, findings);
        CheckLocations(project, findings);
        CheckWorkbenchMetadata(project, findings);
        if (applications is not null) CheckApplications(project, applications, findings);
        return findings;
    }

    /// <summary>
    /// Empty identifiers break every id-based reference (Error); repeated identifiers only make
    /// references ambiguous, because topology and links still resolve by position (Warning).
    /// </summary>
    private static void CheckIdentifiers(Project project, List<CheckFinding> findings)
    {
        var counts = new Dictionary<(string Kind, string Id), int>();
        foreach (var (kind, id) in Identifiers(project))
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                findings.Add(new(CheckSeverity.Error, "P051", kind, $"{kind} hat keinen Bezeichner."));
                continue;
            }
            var key = (kind, id);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        foreach (var ((kind, id), count) in counts.Where(p => p.Value > 1)
                     .OrderBy(p => p.Key.Kind, StringComparer.Ordinal).ThenBy(p => p.Key.Id, StringComparer.Ordinal))
            findings.Add(new(CheckSeverity.Warning, "P050", $"{kind} {id}",
                $"{kind}-Bezeichner '{id}' ist {count}-mal vergeben; Referenzen sind nicht eindeutig."));
    }

    private static IEnumerable<(string Kind, string Id)> Identifiers(Project project)
    {
        foreach (var installation in project.Installations)
        {
            yield return ("Installation", installation.Id);
            foreach (var node in installation.Locations.AllNodes) yield return ("Gebäudeknoten", node.Id);
            foreach (var range in ProjectQuery.AllRanges(installation)) yield return ("Gruppenbereich", range.Id);
            foreach (var entry in installation.MainRanges.SelectMany(ProjectQuery.AllAddresses))
                yield return ("Gruppenadresse", entry.Id);
            foreach (var area in installation.Areas)
            {
                yield return ("Bereich", area.Id);
                foreach (var device in area.Devices) yield return ("Gerät", device.Id);
                foreach (var line in area.Lines)
                {
                    yield return ("Linie", line.Id);
                    foreach (var device in line.Devices) yield return ("Gerät", device.Id);
                    foreach (var segment in line.Segments)
                    {
                        yield return ("Segment", segment.Id);
                        foreach (var device in segment.Devices) yield return ("Gerät", device.Id);
                    }
                }
            }
        }
    }

    private static void CheckTopology(Project project, List<CheckFinding> findings)
    {
        foreach (var inst in project.Installations)
        {
            var areaAddresses = new HashSet<byte>();
            foreach (var area in inst.Areas)
            {
                if (area.Address > 15)
                    findings.Add(new(CheckSeverity.Error, "P010", $"Bereich {area.Address}",
                        "Bereichsadresse muss zwischen 0 und 15 liegen."));
                else if (!areaAddresses.Add(area.Address))
                    findings.Add(new(CheckSeverity.Error, "P015", $"Bereich {area.Address}",
                        $"Bereichsadresse {area.Address} ist in der Installation {inst.Id} mehrfach vergeben."));

                var lineAddresses = new HashSet<byte>();
                foreach (var line in area.Lines)
                {
                    if (line.Address > 15)
                        findings.Add(new(CheckSeverity.Error, "P011", $"Linie {area.Address}.{line.Address}",
                            "Linienadresse muss zwischen 0 und 15 liegen."));
                    else if (!lineAddresses.Add(line.Address))
                        findings.Add(new(CheckSeverity.Error, "P016", $"Linie {area.Address}.{line.Address}",
                            $"Linienadresse {area.Address}.{line.Address} ist im Bereich {area.Address} mehrfach vergeben."));

                    if (line.Segments.Count > 1)
                        findings.Add(new(CheckSeverity.Warning, "P012", $"Linie {area.Address}.{line.Address}",
                            $"Linie hat {line.Segments.Count} Segmente; die Kopplerbestückung ist nicht modelliert."));
                    if (line.Segments.Count > 1)
                        findings.Add(new(CheckSeverity.Info, "P014", $"Linie {area.Address}.{line.Address}",
                            $"Linie hat {line.Segments.Count} Segmente; zwischen Segmenten sind Segmentkoppler nötig. " +
                            "Das Projektmodell führt keine Kopplerbestückung, die Prüfung ist nicht möglich."));

                    var segmentAddresses = new HashSet<byte>();
                    foreach (var segment in line.Segments)
                    {
                        if (segment.Address > 15)
                            findings.Add(new(CheckSeverity.Error, "P013", $"Segment {area.Address}.{line.Address}.{segment.Address}",
                                "Segmentadresse muss zwischen 0 und 15 liegen."));
                        else if (!segmentAddresses.Add(segment.Address))
                            findings.Add(new(CheckSeverity.Warning, "P073", $"Segment {area.Address}.{line.Address}.{segment.Address}",
                                $"Segmentadresse {segment.Address} ist in der Linie {area.Address}.{line.Address} mehrfach vergeben."));
                    }

                    if (line.MediumTypeRefId == IpMedium)
                        findings.Add(new(CheckSeverity.Info, "P060", $"Linie {area.Address}.{line.Address}",
                            "Linie nutzt das IP-Medium. Router- und Backbone-Kopplung sind im Projektmodell nicht " +
                            "abgebildet; ihre Anwesenheit ist nicht geprüft."));
                }
            }
        }
    }

    private static void CheckDevices(Project project, List<CheckFinding> findings)
    {
        var byAddress = new Dictionary<ushort, List<DeviceInstance>>();
        foreach (var (installation, area, line, _, device) in ProjectQuery.Devices(project))
        {
            if (!byAddress.TryGetValue(device.Address.Raw, out var list))
                byAddress[device.Address.Raw] = list = [];
            list.Add(device);

            if (device.Address.Device == 0)
                findings.Add(new(CheckSeverity.Error, "P020", device.Address.ToString(),
                    $"Gerät '{device.Name}' hat die ungültige Adresse {device.Address}."));
            else if (area.Address <= 15 && (line is null || line.Address <= 15) && Misplaced(installation, area, line, device))
                findings.Add(new(CheckSeverity.Error, "P024", device.Address.ToString(),
                    $"Gerät '{device.Name}' hängt in Bereich {area.Address}" +
                    (line is null ? "" : $"/Linie {line.Address}") +
                    $", seine Adresse {device.Address} gehört nicht dazu."));
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

    /// <summary>
    /// A device address belongs to the line that hosts the device. A claimed area that the
    /// installation does not model at all is not judged: partial installations may reference
    /// topology they do not contain (analysis/ets/addresses.md §3, InstallationType.Partial).
    /// </summary>
    private static bool Misplaced(Installation installation, Area area, Line? line, DeviceInstance device)
        => (device.Address.Area == area.Address || installation.Areas.Any(a => a.Address == device.Address.Area))
           && (device.Address.Area != area.Address || line is not null && device.Address.Line != line.Address);

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

        foreach (var installation in project.Installations)
            foreach (var root in installation.MainRanges)
                CheckRange(installation, root, depth: 1, findings);
    }

    private static void CheckRange(Installation installation, GroupRange range, int depth, List<CheckFinding> findings)
    {
        var path = string.IsNullOrWhiteSpace(range.Name) ? $"Bereich {range.Id}" : range.Name;
        if (range.RangeStart < 0 || range.RangeEnd > 0xFFFF)
            findings.Add(new(CheckSeverity.Warning, "P035", path,
                $"Bereichsgrenzen {range.RangeStart}..{range.RangeEnd} liegen außerhalb des 16-Bit-Adressraums."));
        else if (range.RangeEnd > 0 && range.RangeEnd < range.RangeStart)
            findings.Add(new(CheckSeverity.Warning, "P035", path,
                $"Bereichsgrenzen sind verdreht: {range.RangeStart}..{range.RangeEnd}."));

        // ETS stores range bounds either as absolute 16-bit values (knxproj, workbench JSON) or as
        // offsets inside the parent. Only a value outside both readings is an inconsistency.
        foreach (var entry in range.Addresses)
            if (range.RangeEnd > 0 && !Covers(range, entry.Address))
                findings.Add(new(CheckSeverity.Warning, "P033", entry.Address.ToString(),
                    $"Adresse liegt außerhalb des Bereichs '{path}' ({range.RangeStart}..{range.RangeEnd})."));

        switch (installation.GroupAddressStyle)
        {
            case GroupAddressStyle.ThreeLevel when depth == 1 && range.Addresses.Count > 0:
                findings.Add(new(CheckSeverity.Warning, "P034", path,
                    "Gruppenadressen liegen direkt im Hauptbereich; im dreistufigen Stil gehören sie in einen Mittelbereich."));
                break;
            case GroupAddressStyle.ThreeLevel when depth > 2:
                findings.Add(new(CheckSeverity.Warning, "P034", path,
                    "Bereich ist tiefer verschachtelt als der dreistufige Stil erlaubt (Haupt-/Mittelbereich)."));
                break;
            case GroupAddressStyle.TwoLevel when depth > 1:
                findings.Add(new(CheckSeverity.Warning, "P034", path,
                    "Bereich ist verschachtelt; im zweistufigen Stil liegen Adressen direkt im Hauptbereich."));
                break;
        }

        foreach (var child in range.Ranges) CheckRange(installation, child, depth + 1, findings);
    }

    private static bool Covers(GroupRange range, GroupAddress address)
    {
        // ETS and the native importer store bounds both as absolute 16-bit values and as
        // parent-relative main/middle numbers. A warning is only justified when none of the
        // plausible readings matches; false negatives are preferable to noisy P033 findings.
        bool InRange(int value) => value >= range.RangeStart && value <= range.RangeEnd;
        return InRange(address.Raw) || InRange(address.Main) || InRange(address.Middle) || InRange(address.Sub);
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

    /// <summary>Building-tree references to devices that no longer exist break the room assignment.</summary>
    private static void CheckLocations(Project project, List<CheckFinding> findings)
    {
        var known = ProjectQuery.Devices(project).Select(d => d.Device.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var installation in project.Installations)
        {
            foreach (var node in installation.Locations.AllNodes)
                foreach (var id in node.DeviceRefs)
                    if (!known.Contains(id))
                        findings.Add(new(CheckSeverity.Error, "P043", $"{node.Path} [{id}]",
                            $"Gebäudeknoten '{node.Path}' verweist auf das Gerät '{id}', das im Projekt nicht existiert."));
        }
    }

    /// <summary>
    /// BusChord workbench metadata is keyed by device/object/group ids. Entries whose target was
    /// deleted or renamed refer to nothing (Warning: no bus effect, but the metadata is stale).
    /// </summary>
    private static void CheckWorkbenchMetadata(Project project, List<CheckFinding> findings)
    {
        var workbench = project.Workbench;
        if (workbench.IsEmpty) return;

        var devices = ProjectQuery.DeviceList(project).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var objects = ProjectQuery.ComObjectRefs(project)
            .Select(p => new Knx.Workbench.ObjectKey(p.Device.Id, p.Ref.Id).ToString()).ToHashSet(StringComparer.Ordinal);
        var groups = ProjectQuery.GroupAddresses(project).Select(g => g.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var id in workbench.Devices.Keys.Where(id => !devices.Contains(id)))
            findings.Add(new(CheckSeverity.Warning, "P052", id,
                $"Arbeitsbereichs-Metadaten verweisen auf das unbekannte Gerät '{id}'."));
        foreach (var key in workbench.Objects.Keys.Where(key => !objects.Contains(key)))
            findings.Add(new(CheckSeverity.Warning, "P052", key,
                "Arbeitsbereichs-Metadaten verweisen auf ein unbekanntes Kommunikationsobjekt."));
        foreach (var id in workbench.Groups.Keys.Where(id => !groups.Contains(id)))
            findings.Add(new(CheckSeverity.Warning, "P052", id,
                $"Arbeitsbereichs-Metadaten verweisen auf die unbekannte Gruppenadresse '{id}'."));
    }

    /// <summary>
    /// Application-program status from catalogue data. A broken program blocks editing and download
    /// (Error); incomplete download information only warns; an unresolved reference stays Info
    /// because the loaded catalogue may simply be incomplete.
    /// </summary>
    private static void CheckApplications(Project project,
        IReadOnlyDictionary<string, CatalogApplicationProgram> applications, List<CheckFinding> findings)
    {
        foreach (var device in ProjectQuery.DeviceList(project))
        {
            if (string.IsNullOrWhiteSpace(device.Hardware2ProgramRefId)) continue;
            var path = device.Address.ToString();
            if (!applications.TryGetValue(device.Hardware2ProgramRefId, out var program) || program is null)
            {
                findings.Add(new(CheckSeverity.Info, "P072", path,
                    $"Applikationsprogramm '{device.Hardware2ProgramRefId}' von '{device.Name}' ist im geladenen " +
                    "Katalog nicht vorhanden; sein Status ist nicht prüfbar."));
                continue;
            }
            if (program.Broken)
                findings.Add(new(CheckSeverity.Error, "P070", path,
                    $"Applikationsprogramm von '{device.Name}' ist als defekt markiert; Bearbeiten und Download sind gesperrt."));
            if (program.DownloadInfoIncomplete)
                findings.Add(new(CheckSeverity.Error, "P071", path,
                    $"Download-Information des Applikationsprogramms von '{device.Name}' ist unvollständig; " +
                    "Einfügen, Bearbeiten und Download sind gesperrt."));
        }
    }

    public static int Count(IEnumerable<CheckFinding> findings, CheckSeverity severity)
        => findings.Count(f => f.Severity == severity);
}
