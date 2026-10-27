using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knx.Workbench;

namespace Knx;

/// <summary>
/// State of the project signature when a revision was captured. BusChord preserves the
/// native signature but never recomputes or verifies it, so "Unverified" is the honest state
/// for every project that carries one.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RevisionSignatureStatus>))]
public enum RevisionSignatureStatus
{
    /// <summary>The project carries no native signature.</summary>
    Absent,
    /// <summary>A native signature is present; it is preserved, not verified.</summary>
    Unverified,
}

/// <summary>
/// One durable document revision. Stored in the BusChord project extension, so it survives
/// save/load and never touches native project elements. Only the source file name is kept, never
/// its directory, so shared JSON and the project archive carry no customer path.
/// </summary>
public sealed class ProjectRevision
{
    private string? _sourcePath;

    /// <summary>Stable revision id, derived from <see cref="Number"/> ("REV-0001").</summary>
    public required string Id { get; init; }
    /// <summary>Monotonic revision number within the project, starting at 1.</summary>
    public long Number { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    /// <summary>
    /// Base name of the file the revision was captured from; null for an unsaved project.
    /// The setter discards every directory component, so deserialized legacy logs and direct
    /// assignments cannot put an absolute customer path into shared JSON. <see cref="SourceHash"/>
    /// still verifies the captured file bytes.
    /// </summary>
    public string? SourcePath
    {
        get => _sourcePath;
        set => _sourcePath = ProjectRevisions.SourceName(value);
    }
    /// <summary>SHA-256 of the source file at capture time; null when no readable file existed.</summary>
    public string? SourceHash { get; set; }
    /// <summary>SHA-256 of the canonical project content (independent of ZIP packaging and file order).</summary>
    public required string ContentHash { get; set; }
    /// <summary>Algorithm used by <see cref="ContentHash"/> and <see cref="SourceHash"/>.</summary>
    public string HashAlgorithm { get; set; } = ProjectRevisions.HashAlgorithm;
    public string? Description { get; set; }
    public RevisionSignatureStatus SignatureStatus { get; set; }
    /// <summary>SHA-256 of the native project signature text; null when the project has none.</summary>
    public string? SignatureHash { get; set; }
}

/// <summary>Capture, validation and canonical hashing for the durable project revision log.</summary>
public static class ProjectRevisions
{
    public const string HashAlgorithm = "SHA-256";
    private const string IdPrefix = "REV-";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static IReadOnlyList<ProjectRevision> All(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Workbench.Revisions;
    }

    /// <summary>Highest-numbered revision, or null for a project without revisions.</summary>
    public static ProjectRevision? Latest(Project project) => All(project)
        .OrderByDescending(r => r.Number)
        .ThenByDescending(r => r.Timestamp)
        .ThenBy(r => r.Id, StringComparer.Ordinal)
        .FirstOrDefault();

    /// <summary>
    /// File name of a host path, without any directory. Used wherever a revision source is
    /// stored or serialized: reports, V4 JSON and the project archive must not leak the
    /// customer's local layout, while the name still tells the installer which file it was.
    /// </summary>
    public static string? SourceName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // Windows-style paths are also sanitized on Linux CI; Path.GetFileName would keep
        // the backslashes there and leak "C:\Kunde Geheim\haus.knxproj".
        var normalized = path.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var name = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Appends one revision capturing the current modelled project state. Nothing is written to
    /// disk; call <see cref="Knxproj.Save"/> afterwards to persist the record.
    /// </summary>
    public static ProjectRevision Capture(Project project, string? sourcePath = null, string? description = null,
        DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var revisions = project.Workbench.Revisions
            ?? throw new InvalidDataException("Project revision log is null.");
        var number = revisions.Count == 0 ? 1 : revisions.Max(r => r.Number) + 1;
        var revision = new ProjectRevision
        {
            Id = IdPrefix + number.ToString("D4", CultureInfo.InvariantCulture),
            Number = number,
            Timestamp = timestamp ?? DateTimeOffset.Now,
            SourcePath = sourcePath,
            SourceHash = SourceHash(sourcePath),
            ContentHash = ContentHash(project),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            SignatureStatus = SignatureStatus(project),
            SignatureHash = SignatureHash(project),
        };
        revisions.Add(revision);
        return revision;
    }

    /// <summary>Canonical SHA-256 over the modelled project; revisions themselves are excluded.</summary>
    public static string ContentHash(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Sha256(JsonSerializer.Serialize(Snapshot(project), JsonOptions));
    }

    /// <summary>Signature state of the current project.</summary>
    public static RevisionSignatureStatus SignatureStatus(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return string.IsNullOrWhiteSpace(project.MasterData.Signature)
            ? RevisionSignatureStatus.Absent : RevisionSignatureStatus.Unverified;
    }

    /// <summary>SHA-256 of the native project signature text, or null when the project has none.</summary>
    public static string? SignatureHash(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var signature = project.MasterData.Signature;
        return string.IsNullOrWhiteSpace(signature) ? null : Sha256(signature);
    }

    /// <summary>Rejects malformed revision logs before they become project state.</summary>
    public static void Validate(IReadOnlyList<ProjectRevision> revisions)
    {
        if (revisions is null) throw new InvalidDataException("Project revision log is null.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var numbers = new HashSet<long>();
        foreach (var revision in revisions)
        {
            if (revision is null) throw new InvalidDataException("Project revision log contains a null entry.");
            if (string.IsNullOrWhiteSpace(revision.Id))
                throw new InvalidDataException("Project revision has an empty id.");
            if (!ids.Add(revision.Id))
                throw new InvalidDataException($"Duplicate project revision id '{revision.Id}'.");
            if (revision.Number <= 0)
                throw new InvalidDataException($"Project revision '{revision.Id}' has a non-positive number.");
            if (!numbers.Add(revision.Number))
                throw new InvalidDataException($"Duplicate project revision number {revision.Number}.");
            if (string.IsNullOrWhiteSpace(revision.ContentHash))
                throw new InvalidDataException($"Project revision '{revision.Id}' has no content hash.");
            if (string.IsNullOrWhiteSpace(revision.HashAlgorithm))
                throw new InvalidDataException($"Project revision '{revision.Id}' has no hash algorithm.");
            if (revision.SignatureStatus == RevisionSignatureStatus.Unverified &&
                string.IsNullOrWhiteSpace(revision.SignatureHash))
                throw new InvalidDataException($"Project revision '{revision.Id}' claims a signature without a signature hash.");
            if (revision.SignatureStatus == RevisionSignatureStatus.Absent && revision.SignatureHash is not null)
                throw new InvalidDataException($"Project revision '{revision.Id}' has a signature hash but no signature status.");
        }
    }

    private static string? SourceHash(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // Canonical projections: every collection is sorted, every value is ordinal, and the revision
    // log is deliberately absent so logging a revision does not change its own content hash.
    private static object Snapshot(Project project) => new
    {
        Project = new
        {
            project.Id,
            project.Name,
            project.Comment,
            ProjectStart = project.ProjectStart?.ToString("O", CultureInfo.InvariantCulture),
            project.Guid,
            MasterData = new { project.MasterData.Id, project.MasterData.Version, project.MasterData.Signature },
        },
        Installations = project.Installations.OrderBy(i => i.Id, StringComparer.Ordinal).Select(installation => new
        {
            installation.Id,
            installation.Name,
            Address = installation.Address?.ToString(),
            Type = installation.Type.ToString(),
            GroupAddressStyle = installation.GroupAddressStyle.ToString(),
            Topology = installation.Areas.OrderBy(a => a.Id, StringComparer.Ordinal).Select(area => new
            {
                area.Id,
                area.Address,
                area.Name,
                Devices = area.Devices.OrderBy(d => d.Id, StringComparer.Ordinal).Select(DeviceSnapshot).ToArray(),
                Lines = area.Lines.OrderBy(l => l.Id, StringComparer.Ordinal).Select(line => new
                {
                    line.Id,
                    line.Address,
                    line.Name,
                    line.MediumTypeRefId,
                    line.DomainAddress,
                    Devices = line.Devices.OrderBy(d => d.Id, StringComparer.Ordinal).Select(DeviceSnapshot).ToArray(),
                    Segments = line.Segments.OrderBy(s => s.Id, StringComparer.Ordinal).Select(segment => new
                    {
                        segment.Id,
                        segment.Address,
                        segment.Name,
                        segment.MediumTypeRefId,
                        segment.DomainAddress,
                        Devices = segment.Devices.OrderBy(d => d.Id, StringComparer.Ordinal).Select(DeviceSnapshot).ToArray(),
                    }).ToArray(),
                }).ToArray(),
            }).ToArray(),
            Ranges = installation.MainRanges.OrderBy(r => r.Id, StringComparer.Ordinal).Select(RangeSnapshot).ToArray(),
            Locations = installation.Locations.Roots.OrderBy(r => r.Id, StringComparer.Ordinal).Select(SpaceSnapshot).ToArray(),
        }).ToArray(),
        Devices = ProjectQuery.Devices(project)
            .OrderBy(d => d.Device.Id, StringComparer.Ordinal)
            .ThenBy(d => d.Installation.Id, StringComparer.Ordinal)
            .ThenBy(d => d.Area.Id, StringComparer.Ordinal)
            .Select(d => new
            {
                Installation = d.Installation.Id,
                Area = d.Area.Id,
                Line = d.Line?.Id,
                Segment = d.Segment?.Id,
                Device = DeviceSnapshot(d.Device),
            }).ToArray(),
        Workbench = new
        {
            Objects = project.Workbench.Objects.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new
            {
                p.Key,
                p.Value.Dpt,
                p.Value.Bits,
                p.Value.Flags,
                p.Value.Active,
                p.Value.ChannelId,
                p.Value.Role,
                p.Value.SendingGroupId,
                Connectors = p.Value.Connectors.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new
                {
                    c.Key,
                    c.Value.Send,
                    c.Value.Acknowledge,
                    c.Value.Priority,
                    c.Value.Slot,
                }).ToArray(),
            }).ToArray(),
            Devices = project.Workbench.Devices.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new
            {
                p.Key,
                p.Value.SecureCapable,
                p.Value.Demo,
                p.Value.Application,
                p.Value.Revision,
                p.Value.DownloadedRevision,
            }).ToArray(),
            Groups = project.Workbench.Groups.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new
            {
                p.Key,
                p.Value.Bits,
                p.Value.Secure,
                p.Value.EffectRoomId,
            }).ToArray(),
            Floors = project.Workbench.FloorShortNames.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new { p.Key, p.Value }).ToArray(),
        },
    };

    private static object DeviceSnapshot(DeviceInstance device) => new
    {
        device.Id,
        Address = device.Address.ToString(),
        device.ProductRefId,
        device.Hardware2ProgramRefId,
        device.Name,
        device.Description,
        device.Comment,
        Channels = device.Channels.OrderBy(c => c.Id, StringComparer.Ordinal).Select(ChannelSnapshot).ToArray(),
        ComObjectRefs = device.ComObjectRefs.OrderBy(r => r.Id, StringComparer.Ordinal)
            .ThenBy(r => r.ComObjectId, StringComparer.Ordinal).Select(r => new
            {
                r.Id,
                r.ComObjectId,
                r.Number,
                r.Text,
                r.FunctionText,
                r.Device,
                Links = r.Links.OrderBy(l => l, StringComparer.Ordinal).ToArray(),
            }).ToArray(),
        Parameters = device.ParameterValues.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new { p.Key, p.Value }).ToArray(),
    };

    private static object ChannelSnapshot(ChannelInstance channel) => new
    {
        channel.Id,
        channel.Name,
        Channels = channel.Channels.OrderBy(c => c.Id, StringComparer.Ordinal).Select(ChannelSnapshot).ToArray(),
    };

    private static object RangeSnapshot(GroupRange range) => new
    {
        range.Id,
        range.Name,
        range.RangeStart,
        range.RangeEnd,
        Addresses = range.Addresses.OrderBy(a => a.Address.Raw).ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new
            {
                a.Id,
                Address = a.Address.ToString(),
                a.Name,
                a.Description,
                a.Comment,
                a.DatapointType,
                a.Puid,
            }).ToArray(),
        Ranges = range.Ranges.OrderBy(r => r.Id, StringComparer.Ordinal).Select(RangeSnapshot).ToArray(),
    };

    private static object SpaceSnapshot(SpaceNode node) => new
    {
        node.Id,
        node.Name,
        Kind = node.Kind.ToString(),
        node.RawType,
        node.Number,
        node.Description,
        node.Comment,
        node.Usage,
        node.Puid,
        Parent = node.Parent?.Id,
        DeviceRefs = node.DeviceRefs.Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToArray(),
        Children = node.Children.OrderBy(c => c.Id, StringComparer.Ordinal).Select(SpaceSnapshot).ToArray(),
    };
}
