using System.Globalization;
using Knx.Workbench;

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
    /// <summary>Optional revision of the left project; rendered by the report renderer.</summary>
    public ProjectRevision? LeftRevision { get; init; }
    /// <summary>Optional revision of the right project; rendered by the report renderer.</summary>
    public ProjectRevision? RightRevision { get; init; }
    public List<DifferenceItem> Items { get; } = [];
    public bool HasRevisions => LeftRevision is not null || RightRevision is not null;
    /// <summary>True only when both sides carry a revision with the same content hash.</summary>
    public bool ContentUnchanged => LeftRevision is { } left && RightRevision is { } right &&
        string.Equals(left.HashAlgorithm, right.HashAlgorithm, StringComparison.Ordinal) &&
        string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);
    public bool SignatureUnchanged => LeftRevision is { } left && RightRevision is { } right &&
        left.SignatureStatus == right.SignatureStatus &&
        string.Equals(left.SignatureHash, right.SignatureHash, StringComparison.Ordinal);
    public int AddedCount => Items.Count(i => i.Modification == ModificationType.Added);
    public int RemovedCount => Items.Count(i => i.Modification == ModificationType.Removed);
    public int ChangedCount => Items.Count(i => i.Modification == ModificationType.Changed);
    public int UnchangedCount => Items.Count(i => i.Modification == ModificationType.Unchanged);

    /// <summary>Stable ordinal order, independent of dictionary or traversal order.</summary>
    public IReadOnlyList<DifferenceItem> OrderedItems()
    {
        var ordered = Items.ToList();
        ordered.Sort(DifferenceItemOrder.Compare);
        return ordered;
    }
}

internal static class DifferenceItemOrder
{
    public static int Compare(DifferenceItem? left, DifferenceItem? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        var result = string.CompareOrdinal(left.Path, right.Path);
        if (result != 0) return result;
        result = string.CompareOrdinal(left.ObjectType, right.ObjectType);
        if (result != 0) return result;
        result = string.CompareOrdinal(left.ObjectName, right.ObjectName);
        if (result != 0) return result;
        result = left.Modification.CompareTo(right.Modification);
        if (result != 0) return result;
        result = string.CompareOrdinal(left.OldValue, right.OldValue);
        if (result != 0) return result;
        result = string.CompareOrdinal(left.NewValue, right.NewValue);
        if (result != 0) return result;
        return string.CompareOrdinal(left.Details, right.Details);
    }
}

/// <summary>Vergleicht zwei Projekte und erzeugt einen DifferenceReport.</summary>
public static class ProjectComparer
{
    public static DifferenceReport Compare(Project left, Project right) =>
        Compare(left, right, null, null, null);

    /// <summary>Comparison with a caller-supplied timestamp, for reproducible reports.</summary>
    public static DifferenceReport Compare(Project left, Project right, DateTimeOffset generatedAt) =>
        Compare(left, right, null, null, generatedAt);

    /// <summary>
    /// Full comparison with optional document revisions. Revisions are metadata and are never
    /// read from disk here; use <see cref="ProjectRevisions.Latest"/> for loaded projects.
    /// </summary>
    public static DifferenceReport Compare(Project left, Project right, ProjectRevision? leftRevision,
        ProjectRevision? rightRevision, DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var report = new DifferenceReport
        {
            LeftProjectName = left.Name,
            RightProjectName = right.Name,
            GeneratedAt = generatedAt ?? DateTimeOffset.Now,
            LeftRevision = leftRevision,
            RightRevision = rightRevision,
        };

        CompareProject(left, right, report);
        CompareInstallations(left, right, report);
        CompareTopology(left, right, report);
        CompareDevices(left, right, report);
        CompareGroupAddresses(left, right, report);
        CompareLinks(left, right, report);
        CompareWorkbench(left, right, report);
        CompareLocations(left, right, report);

        report.Items.Sort(DifferenceItemOrder.Compare);
        return report;
    }

    /// <summary>Convenience overload comparing the latest recorded revision on each side.</summary>
    public static DifferenceReport CompareRevisions(Project left, Project right, DateTimeOffset? generatedAt = null) =>
        Compare(left, right, ProjectRevisions.Latest(left), ProjectRevisions.Latest(right), generatedAt);

    // ---------------------------------------------------------------- project and topology

    private static void CompareProject(Project left, Project right, DifferenceReport report)
    {
        var name = left.Name;
        CompareText(report, "Project/Name", "Project", name, left.Name, right.Name);
        CompareText(report, "Project/Comment", "Project", name, left.Comment, right.Comment);
        CompareText(report, "Project/Start", "Project", name, ValueText(left.ProjectStart), ValueText(right.ProjectStart));
        CompareText(report, "Project/Guid", "Project", name, left.Guid, right.Guid);
        CompareText(report, "Project/MasterData/Version", "Project", name,
            ValueText(left.MasterData.Version), ValueText(right.MasterData.Version));
        CompareText(report, "Project/MasterData/Signature", "Project", name,
            SignatureState(left), SignatureState(right), "preserved, not recomputed or verified");
    }

    private static void CompareInstallations(Project left, Project right, DifferenceReport report)
    {
        var leftInstallations = left.Installations.OrderBy(i => i.Id, StringComparer.Ordinal)
            .ToDictionary(i => i.Id, StringComparer.Ordinal);
        var rightInstallations = right.Installations.OrderBy(i => i.Id, StringComparer.Ordinal)
            .ToDictionary(i => i.Id, StringComparer.Ordinal);

        foreach (var (id, installation) in leftInstallations)
        {
            if (!rightInstallations.TryGetValue(id, out var other))
            {
                Add(report, $"Installation/{id}", "Installation", installation.Name ?? id,
                    ModificationType.Removed, oldValue: installation.Name);
                continue;
            }
            var name = installation.Name ?? id;
            var path = $"Installation/{id}";
            CompareText(report, $"{path}/Name", "Installation", name, installation.Name, other.Name);
            CompareText(report, $"{path}/Address", "Installation", name,
                ValueText(installation.Address), ValueText(other.Address));
            CompareText(report, $"{path}/Type", "Installation", name,
                installation.Type.ToString(), other.Type.ToString());
            CompareText(report, $"{path}/GroupAddressStyle", "Installation", name,
                installation.GroupAddressStyle.ToString(), other.GroupAddressStyle.ToString());
        }

        foreach (var (id, installation) in rightInstallations)
            if (!leftInstallations.ContainsKey(id))
                Add(report, $"Installation/{id}", "Installation", installation.Name ?? id,
                    ModificationType.Added, newValue: installation.Name);
    }

    private sealed record LineRef(Area Area, Line Line);
    private sealed record SegmentRef(Segment Segment);

    /// <summary>
    /// Line and segment medium/domain facts. These fields are used by the
    /// exported project XML and the P080/P081/P082 checks, so a change confined to them must
    /// still produce a difference item.
    /// </summary>
    private static void CompareTopology(Project left, Project right, DifferenceReport report)
    {
        var leftLines = TopologyLines(left);
        var rightLines = TopologyLines(right);
        foreach (var (key, line) in leftLines)
        {
            var path = $"Line/{key}";
            var name = line.Line.Name ?? line.Line.Id;
            if (!rightLines.TryGetValue(key, out var other))
            {
                Add(report, path, "Line", name, ModificationType.Removed, oldValue: LineSummary(line));
                continue;
            }
            CompareText(report, $"{path}/Name", "Line", name, line.Line.Name, other.Line.Name);
            CompareText(report, $"{path}/Address", "Line", name,
                ValueText(line.Line.Address), ValueText(other.Line.Address));
            CompareText(report, $"{path}/MediumTypeRefId", "Line", name,
                line.Line.MediumTypeRefId, other.Line.MediumTypeRefId);
            CompareText(report, $"{path}/DomainAddress", "Line", name,
                line.Line.DomainAddress, other.Line.DomainAddress);
        }
        foreach (var (key, line) in rightLines)
            if (!leftLines.ContainsKey(key))
                Add(report, $"Line/{key}", "Line", line.Line.Name ?? line.Line.Id,
                    ModificationType.Added, newValue: LineSummary(line));

        var leftSegments = TopologySegments(leftLines);
        var rightSegments = TopologySegments(rightLines);
        foreach (var (key, segment) in leftSegments)
        {
            var path = $"Segment/{key}";
            var name = segment.Segment.Name ?? segment.Segment.Id;
            if (!rightSegments.TryGetValue(key, out var other))
            {
                Add(report, path, "Segment", name, ModificationType.Removed, oldValue: SegmentSummary(segment.Segment));
                continue;
            }
            CompareText(report, $"{path}/Name", "Segment", name, segment.Segment.Name, other.Segment.Name);
            CompareText(report, $"{path}/Address", "Segment", name,
                ValueText(segment.Segment.Address), ValueText(other.Segment.Address));
            CompareText(report, $"{path}/MediumTypeRefId", "Segment", name,
                segment.Segment.MediumTypeRefId, other.Segment.MediumTypeRefId);
            CompareText(report, $"{path}/DomainAddress", "Segment", name,
                segment.Segment.DomainAddress, other.Segment.DomainAddress);
        }
        foreach (var (key, segment) in rightSegments)
            if (!leftSegments.ContainsKey(key))
                Add(report, $"Segment/{key}", "Segment", segment.Segment.Name ?? segment.Segment.Id,
                    ModificationType.Added, newValue: SegmentSummary(segment.Segment));
    }

    private static Dictionary<string, LineRef> TopologyLines(Project project) =>
        project.Installations.OrderBy(i => i.Id, StringComparer.Ordinal)
            .SelectMany(installation => installation.Areas.OrderBy(a => a.Id, StringComparer.Ordinal)
                .SelectMany(area => area.Lines.OrderBy(l => l.Id, StringComparer.Ordinal)
                    .Select(line => new KeyValuePair<string, LineRef>(
                        $"{installation.Id}/{area.Id}/{line.Id}", new LineRef(area, line)))))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static Dictionary<string, SegmentRef> TopologySegments(Dictionary<string, LineRef> lines) =>
        lines.SelectMany(pair => pair.Value.Line.Segments.OrderBy(s => s.Id, StringComparer.Ordinal)
                .Select(segment => new KeyValuePair<string, SegmentRef>(
                    $"{pair.Key}/{segment.Id}", new SegmentRef(segment))))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static string LineSummary(LineRef line) =>
        $"address={line.Area.Address}.{line.Line.Address} medium={line.Line.MediumTypeRefId} " +
        $"domain={OrDash(line.Line.DomainAddress ?? "")}";

    private static string SegmentSummary(Segment segment) =>
        $"address={segment.Address} medium={OrDash(segment.MediumTypeRefId ?? "")} " +
        $"domain={OrDash(segment.DomainAddress ?? "")}";

    private static void CompareDevices(Project left, Project right, DifferenceReport report)
    {
        var leftDevices = ProjectQuery.Devices(left).OrderBy(d => d.Device.Id, StringComparer.Ordinal)
            .ToDictionary(d => d.Device.Id, StringComparer.Ordinal);
        var rightDevices = ProjectQuery.Devices(right).OrderBy(d => d.Device.Id, StringComparer.Ordinal)
            .ToDictionary(d => d.Device.Id, StringComparer.Ordinal);

        foreach (var (id, entry) in leftDevices)
        {
            if (!rightDevices.TryGetValue(id, out var other))
            {
                Add(report, $"Device/{entry.Device.Address}", "Device", DeviceLabel(entry.Device),
                    ModificationType.Removed, oldValue: DeviceLabel(entry.Device));
                continue;
            }
            var path = $"Device/{entry.Device.Address}";
            var name = DeviceLabel(entry.Device);
            CompareText(report, $"{path}/Address", "Device", name,
                ValueText(entry.Device.Address), ValueText(other.Device.Address));
            CompareText(report, $"{path}/Name", "Device", name, entry.Device.Name, other.Device.Name);
            CompareText(report, $"{path}/ProductRefId", "Device", name,
                entry.Device.ProductRefId, other.Device.ProductRefId);
            CompareText(report, $"{path}/Hardware2ProgramRefId", "Device", name,
                entry.Device.Hardware2ProgramRefId, other.Device.Hardware2ProgramRefId);
            CompareText(report, $"{path}/Description", "Device", name,
                entry.Device.Description, other.Device.Description);
            CompareText(report, $"{path}/Comment", "Device", name,
                entry.Device.Comment, other.Device.Comment);
            CompareText(report, $"{path}/Topology", "Device", name, TopologyText(entry), TopologyText(other));
            CompareParameters(report, path, entry.Device, other.Device);
        }

        foreach (var (id, entry) in rightDevices)
            if (!leftDevices.ContainsKey(id))
                Add(report, $"Device/{entry.Device.Address}", "Device", DeviceLabel(entry.Device),
                    ModificationType.Added, newValue: DeviceLabel(entry.Device));
    }

    private static void CompareParameters(DifferenceReport report, string devicePath, DeviceInstance left, DeviceInstance right)
    {
        foreach (var key in left.ParameterValues.Keys.Concat(right.ParameterValues.Keys)
                     .Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            var hasLeft = left.ParameterValues.TryGetValue(key, out var leftValue);
            var hasRight = right.ParameterValues.TryGetValue(key, out var rightValue);
            var path = $"{devicePath}/Parameter/{key}";
            var name = DeviceLabel(left);
            if (hasLeft && hasRight)
                CompareText(report, path, "Parameter", name, leftValue, rightValue);
            else if (hasLeft)
                Add(report, path, "Parameter", name, ModificationType.Removed, oldValue: leftValue);
            else
                Add(report, path, "Parameter", name, ModificationType.Added, newValue: rightValue);
        }
    }

    private static void CompareGroupAddresses(Project left, Project right, DifferenceReport report)
    {
        var leftAddresses = ProjectQuery.GroupAddresses(left).OrderBy(a => a.Address.Raw).ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToDictionary(a => a.Address.Raw);
        var rightAddresses = ProjectQuery.GroupAddresses(right).OrderBy(a => a.Address.Raw).ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToDictionary(a => a.Address.Raw);

        foreach (var (raw, address) in leftAddresses)
        {
            if (!rightAddresses.TryGetValue(raw, out var other))
            {
                Add(report, $"GroupAddress/{address.Address}", "GroupAddress", address.Name,
                    ModificationType.Removed, oldValue: address.Name);
                continue;
            }
            var path = $"GroupAddress/{address.Address}";
            CompareText(report, $"{path}/Id", "GroupAddress", address.Name, address.Id, other.Id);
            CompareText(report, $"{path}/Name", "GroupAddress", address.Name, address.Name, other.Name);
            CompareText(report, $"{path}/DatapointType", "GroupAddress", address.Name,
                address.DatapointType, other.DatapointType);
            CompareText(report, $"{path}/Description", "GroupAddress", address.Name,
                address.Description, other.Description);
            CompareText(report, $"{path}/Comment", "GroupAddress", address.Name,
                address.Comment, other.Comment);
            CompareText(report, $"{path}/Puid", "GroupAddress", address.Name,
                ValueText(address.Puid), ValueText(other.Puid));
        }

        foreach (var (raw, address) in rightAddresses)
            if (!leftAddresses.ContainsKey(raw))
                Add(report, $"GroupAddress/{address.Address}", "GroupAddress", address.Name,
                    ModificationType.Added, newValue: address.Name);
    }

    // ---------------------------------------------------------------- links

    private sealed record Link(DeviceInstance Device, ComObjectInstanceRef Object, string GroupId, GroupAddressEntry? Group);

    private static void CompareLinks(Project left, Project right, DifferenceReport report)
    {
        var leftLinks = BuildLinks(left).ToDictionary(LinkKey, StringComparer.Ordinal);
        var rightLinks = BuildLinks(right).ToDictionary(LinkKey, StringComparer.Ordinal);

        foreach (var (key, link) in leftLinks)
        {
            var path = LinkPath(link);
            var name = LinkName(link);
            if (!rightLinks.TryGetValue(key, out var other))
            {
                Add(report, path, "GroupLink", name, ModificationType.Removed,
                    oldValue: ConnectorState(left, link), details: "link removed");
                continue;
            }
            CompareText(report, path, "GroupLink", name,
                ConnectorState(left, link), ConnectorState(right, other), "send/ack/priority/slot");
        }

        foreach (var (key, link) in rightLinks)
            if (!leftLinks.ContainsKey(key))
                Add(report, LinkPath(link), "GroupLink", LinkName(link), ModificationType.Added,
                    newValue: ConnectorState(right, link), details: "link added");
    }

    private static List<Link> BuildLinks(Project project)
    {
        var groups = ProjectQuery.GroupAddresses(project).GroupBy(g => g.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return ProjectQuery.ComObjectRefs(project).SelectMany(pair =>
            pair.Ref.Links.Distinct(StringComparer.Ordinal).Select(id =>
                new Link(pair.Device, pair.Ref, id, groups.GetValueOrDefault(id)))).ToList();
    }

    private static string LinkKey(Link link) =>
        $"{link.Device.Id}\u001F{link.Object.Id}\u001F{link.GroupId}";

    private static string LinkPath(Link link) =>
        $"Device/{link.Device.Address}/Link/{link.Object.Id}/{link.Group?.Address.ToString() ?? link.GroupId}";

    private static string LinkName(Link link) =>
        $"{DeviceLabel(link.Device)} {link.Object.Text ?? link.Object.Id} -> " +
        $"{link.Group?.Name ?? link.GroupId}";

    private static string ConnectorState(Project project, Link link)
    {
        var key = new ObjectKey(link.Device.Id, link.Object.Id).ToString();
        var connector = project.Workbench.Objects.GetValueOrDefault(key)?.Connector(link.GroupId)
            ?? new ConnectorMetadata();
        return $"send={ValueText(connector.Send)} ack={ValueText(connector.Acknowledge)} " +
               $"priority={OrDash(ValueText(connector.Priority))} slot={OrDash(ValueText(connector.Slot))}";
    }

    // ---------------------------------------------------------------- workbench metadata

    private static void CompareWorkbench(Project left, Project right, DifferenceReport report)
    {
        var leftMetadata = left.Workbench;
        var rightMetadata = right.Workbench;

        foreach (var key in UnionKeys(leftMetadata.Objects.Keys, rightMetadata.Objects.Keys))
        {
            var hasLeft = leftMetadata.Objects.TryGetValue(key, out var leftObject);
            var hasRight = rightMetadata.Objects.TryGetValue(key, out var rightObject);
            var path = $"Workbench/Object/{key}";
            if (hasLeft && hasRight)
            {
                CompareText(report, $"{path}/Dpt", "WorkbenchObject", key, leftObject!.Dpt, rightObject!.Dpt);
                CompareText(report, $"{path}/Bits", "WorkbenchObject", key,
                    ValueText(leftObject.Bits), ValueText(rightObject.Bits));
                CompareText(report, $"{path}/Flags", "WorkbenchObject", key, leftObject.Flags, rightObject.Flags);
                CompareText(report, $"{path}/Active", "WorkbenchObject", key,
                    ValueText(leftObject.Active), ValueText(rightObject.Active));
                CompareText(report, $"{path}/ChannelId", "WorkbenchObject", key,
                    leftObject.ChannelId, rightObject.ChannelId);
                CompareText(report, $"{path}/Role", "WorkbenchObject", key, leftObject.Role, rightObject.Role);
            }
            else if (hasLeft)
            {
                Add(report, path, "WorkbenchObject", key, ModificationType.Removed,
                    oldValue: ObjectSummary(leftObject!));
            }
            else
            {
                Add(report, path, "WorkbenchObject", key, ModificationType.Added,
                    newValue: ObjectSummary(rightObject!));
            }
        }

        foreach (var key in UnionKeys(leftMetadata.Devices.Keys, rightMetadata.Devices.Keys))
        {
            var hasLeft = leftMetadata.Devices.TryGetValue(key, out var leftDevice);
            var hasRight = rightMetadata.Devices.TryGetValue(key, out var rightDevice);
            var path = $"Workbench/Device/{key}";
            if (hasLeft && hasRight)
            {
                CompareText(report, $"{path}/Application", "WorkbenchDevice", key,
                    leftDevice!.Application, rightDevice!.Application);
                CompareText(report, $"{path}/SecureCapable", "WorkbenchDevice", key,
                    ValueText(leftDevice.SecureCapable), ValueText(rightDevice.SecureCapable));
                CompareText(report, $"{path}/Demo", "WorkbenchDevice", key,
                    ValueText(leftDevice.Demo), ValueText(rightDevice.Demo));
                CompareText(report, $"{path}/Revision", "WorkbenchDevice", key,
                    ValueText(leftDevice.Revision), ValueText(rightDevice.Revision));
                CompareText(report, $"{path}/DownloadedRevision", "WorkbenchDevice", key,
                    ValueText(leftDevice.DownloadedRevision), ValueText(rightDevice.DownloadedRevision));
            }
            else if (hasLeft)
            {
                Add(report, path, "WorkbenchDevice", key, ModificationType.Removed,
                    oldValue: DeviceMetadataSummary(leftDevice!));
            }
            else
            {
                Add(report, path, "WorkbenchDevice", key, ModificationType.Added,
                    newValue: DeviceMetadataSummary(rightDevice!));
            }
        }

        foreach (var key in UnionKeys(leftMetadata.Groups.Keys, rightMetadata.Groups.Keys))
        {
            var hasLeft = leftMetadata.Groups.TryGetValue(key, out var leftGroup);
            var hasRight = rightMetadata.Groups.TryGetValue(key, out var rightGroup);
            var path = $"Workbench/Group/{key}";
            if (hasLeft && hasRight)
            {
                CompareText(report, $"{path}/Bits", "WorkbenchGroup", key,
                    ValueText(leftGroup!.Bits), ValueText(rightGroup!.Bits));
                CompareText(report, $"{path}/Secure", "WorkbenchGroup", key,
                    ValueText(leftGroup.Secure), ValueText(rightGroup.Secure));
                CompareText(report, $"{path}/EffectRoomId", "WorkbenchGroup", key,
                    leftGroup.EffectRoomId, rightGroup.EffectRoomId);
            }
            else if (hasLeft)
            {
                Add(report, path, "WorkbenchGroup", key, ModificationType.Removed,
                    oldValue: GroupMetadataSummary(leftGroup!));
            }
            else
            {
                Add(report, path, "WorkbenchGroup", key, ModificationType.Added,
                    newValue: GroupMetadataSummary(rightGroup!));
            }
        }

        foreach (var key in UnionKeys(leftMetadata.FloorShortNames.Keys, rightMetadata.FloorShortNames.Keys))
        {
            var hasLeft = leftMetadata.FloorShortNames.TryGetValue(key, out var leftName);
            var hasRight = rightMetadata.FloorShortNames.TryGetValue(key, out var rightName);
            var path = $"Workbench/Floor/{key}";
            if (hasLeft && hasRight)
                CompareText(report, $"{path}/ShortName", "WorkbenchFloor", key, leftName, rightName);
            else if (hasLeft)
                Add(report, path, "WorkbenchFloor", key, ModificationType.Removed, oldValue: leftName);
            else
                Add(report, path, "WorkbenchFloor", key, ModificationType.Added, newValue: rightName);
        }
    }

    private static string ObjectSummary(ObjectMetadata metadata) =>
        $"dpt={OrDash(metadata.Dpt ?? "")} bits={OrDash(ValueText(metadata.Bits))} " +
        $"flags={OrDash(metadata.Flags ?? "")} active={ValueText(metadata.Active)} role={OrDash(metadata.Role ?? "")}";

    private static string DeviceMetadataSummary(DeviceMetadata metadata) =>
        $"application={OrDash(metadata.Application ?? "")} secure={ValueText(metadata.SecureCapable)} " +
        $"demo={ValueText(metadata.Demo)} revision={ValueText(metadata.Revision)} " +
        $"downloaded={ValueText(metadata.DownloadedRevision)}";

    private static string GroupMetadataSummary(GroupMetadata metadata) =>
        $"bits={OrDash(ValueText(metadata.Bits))} secure={ValueText(metadata.Secure)} " +
        $"room={OrDash(metadata.EffectRoomId ?? "")}";

    // ---------------------------------------------------------------- building tree

    private static void CompareLocations(Project left, Project right, DifferenceReport report)
    {
        var leftInstallations = left.Installations.OrderBy(i => i.Id, StringComparer.Ordinal)
            .ToDictionary(i => i.Id, StringComparer.Ordinal);
        var rightInstallations = right.Installations.OrderBy(i => i.Id, StringComparer.Ordinal)
            .ToDictionary(i => i.Id, StringComparer.Ordinal);

        foreach (var key in UnionKeys(leftInstallations.Keys, rightInstallations.Keys))
        {
            if (!leftInstallations.TryGetValue(key, out var leftInstallation) ||
                !rightInstallations.TryGetValue(key, out var rightInstallation)) continue;

            var leftNodes = leftInstallation.Locations.AllNodes.OrderBy(n => n.Id, StringComparer.Ordinal)
                .ToDictionary(n => n.Id, StringComparer.Ordinal);
            var rightNodes = rightInstallation.Locations.AllNodes.OrderBy(n => n.Id, StringComparer.Ordinal)
                .ToDictionary(n => n.Id, StringComparer.Ordinal);

            foreach (var nodeId in UnionKeys(leftNodes.Keys, rightNodes.Keys))
            {
                var hasLeft = leftNodes.TryGetValue(nodeId, out var leftNode);
                var hasRight = rightNodes.TryGetValue(nodeId, out var rightNode);
                var path = $"Location/{key}/{nodeId}";
                if (hasLeft && hasRight)
                {
                    CompareText(report, $"{path}/Name", "Location", leftNode!.Path, leftNode.Name, rightNode!.Name);
                    CompareText(report, $"{path}/Kind", "Location", leftNode.Path,
                        KindText(leftNode), KindText(rightNode));
                    CompareText(report, $"{path}/Number", "Location", leftNode.Path,
                        leftNode.Number, rightNode.Number);
                    CompareText(report, $"{path}/Description", "Location", leftNode.Path,
                        leftNode.Description, rightNode.Description);
                    CompareText(report, $"{path}/Comment", "Location", leftNode.Path,
                        leftNode.Comment, rightNode.Comment);
                    CompareText(report, $"{path}/Usage", "Location", leftNode.Path,
                        leftNode.Usage, rightNode.Usage);
                    CompareText(report, $"{path}/Puid", "Location", leftNode.Path,
                        ValueText(leftNode.Puid), ValueText(rightNode.Puid));
                    CompareText(report, $"{path}/Parent", "Location", leftNode.Path,
                        leftNode.Parent?.Id, rightNode.Parent?.Id);
                    CompareText(report, $"{path}/Devices", "Location", leftNode.Path,
                        DeviceRefs(leftNode), DeviceRefs(rightNode));
                }
                else if (hasLeft)
                {
                    Add(report, path, "Location", leftNode!.Path, ModificationType.Removed,
                        oldValue: leftNode.Path, details: KindText(leftNode));
                }
                else
                {
                    Add(report, path, "Location", rightNode!.Path, ModificationType.Added,
                        newValue: rightNode.Path, details: KindText(rightNode));
                }
            }
        }
    }

    private static string KindText(SpaceNode node) =>
        node.Kind == SpaceKind.Unknown && node.RawType is { Length: > 0 } raw ? raw : node.Kind.ToString();

    private static string DeviceRefs(SpaceNode node) =>
        string.Join(' ', node.DeviceRefs.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal));

    // ---------------------------------------------------------------- helpers

    private static void CompareText(DifferenceReport report, string path, string objectType, string objectName,
        string? oldValue, string? newValue, string? details = null)
    {
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) return;
        Add(report, path, objectType, objectName, ModificationType.Changed, oldValue, newValue, details);
    }

    private static void Add(DifferenceReport report, string path, string objectType, string objectName,
        ModificationType modification, string? oldValue = null, string? newValue = null, string? details = null)
    {
        report.Items.Add(new DifferenceItem
        {
            Path = path,
            ObjectType = objectType,
            ObjectName = objectName,
            Modification = modification,
            OldValue = oldValue,
            NewValue = newValue,
            Details = details,
        });
    }

    private static IEnumerable<string> UnionKeys(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Concat(right).Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal);

    private static string DeviceLabel(DeviceInstance device) => device.Name ?? device.Address.ToString();

    private static string TopologyText((Installation Installation, Area Area, Line? Line, Segment? Segment, DeviceInstance Device) entry) =>
        $"{entry.Installation.Id}/{entry.Area.Id}/{entry.Line?.Id ?? "-"}/{entry.Segment?.Id ?? "-"}";

    private static string SignatureState(Project project)
    {
        if (ProjectRevisions.SignatureStatus(project) == RevisionSignatureStatus.Absent) return "absent";
        var hash = ProjectRevisions.SignatureHash(project)!;
        return "unverified sha256:" + hash[..16];
    }

    private static string ValueText(object? value) => value switch
    {
        null => "",
        bool flag => flag ? "true" : "false",
        DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string OrDash(string value) => value.Length == 0 ? "-" : value;
}
