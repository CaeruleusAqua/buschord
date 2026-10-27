using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Knx;

public enum ArchivedImageSourceStatus { Inspected, InvalidRequest, ApplicationUnavailable, UnsupportedApplicationNamespace, InvalidDeclaration, LimitExceeded }
public enum ArchivedHardwareSourceStatus
{
    Inspected, ReferenceMissing, PartMissing, ReferenceNotFound, AmbiguousReference, ApplicationMismatch,
    MultipleApplications, UnsupportedNamespace, InvalidDeclaration, LimitExceeded, Unreadable,
}
public enum ArchivedChecksumSourceStatus { Missing, Present, InvalidData, LimitExceeded }

/// <summary>Sources on the exact linked hardware record. Checksum bytes are retained without verification.</summary>
public sealed class ArchivedHardwareImageSource
{
    public ArchivedHardwareSourceStatus Status { get; }
    public string? Hardware2ProgramId { get; }
    public string? DefinitionHash { get; }
    public bool LoadedImageDeclared { get; }
    public ArchivedFactoryImageSource FactoryImage { get; }
    public ArchivedChecksumSourceStatus ChecksumStatus { get; }
    public ImmutablePatchBytes? ChecksumData { get; }
    internal ArchivedHardwareImageSource(ArchivedHardwareSourceStatus status, string? id,
        string? definition = null, bool declared = false, ArchivedFactoryImageSource? factory = null,
        ArchivedChecksumSourceStatus checksumStatus = ArchivedChecksumSourceStatus.Missing, ImmutablePatchBytes? checksum = null)
    {
        Status = status; Hardware2ProgramId = id; DefinitionHash = definition; LoadedImageDeclared = declared;
        FactoryImage = factory ?? ArchivedFactoryImage.Read([]); ChecksumStatus = checksumStatus; ChecksumData = checksum;
    }
}

/// <summary>Source inventory with its own binding; no active patch set, complete image or device-state claim.</summary>
public sealed class ArchivedImageSourceManifest
{
    public string Scope => "ArchivedImageSources";
    public int ProfileVersion => 1;
    public ArchivedImageSourceStatus Status { get; }
    public string? ApplicationId { get; }
    public string? ApplicationNamespace { get; }
    public string? ApplicationDefinitionHash { get; }
    public ArchivedResourceContext? ResourceContext { get; }
    public ArchivedHardwareImageSource? Hardware { get; }
    public ArchivedImageSourceBinding? Binding { get; }
    public bool CompleteApplicationImage => false;
    public bool DownloadEligible => false;
    internal ArchivedImageSourceManifest(ArchivedImageSourceStatus status, string? app = null, string? ns = null,
        string? definition = null, ArchivedResourceContext? context = null, ArchivedHardwareImageSource? hardware = null,
        ArchivedImageSourceBinding? binding = null)
    { Status = status; ApplicationId = app; ApplicationNamespace = ns; ApplicationDefinitionHash = definition; ResourceContext = context; Hardware = hardware; Binding = binding; }
}

public sealed class ArchivedImageSourceBinding
{
    private readonly Project _project;
    private readonly DeviceInstance _device;
    private readonly string _projectId, _deviceId, _product;
    private readonly string? _projectGuid, _hardware;
    private readonly ushort _address;
    private readonly long _revision;
    public int ProfileVersion => 1;
    public string Fingerprint { get; }
    public string ResourceContextFingerprint { get; }
    internal ArchivedImageSourceBinding(Project project, DeviceInstance device, string appHash,
        ArchivedResourceContext context, ArchivedHardwareImageSource hardware)
    {
        _project = project; _device = device; _projectId = project.Id; _projectGuid = project.Guid;
        _deviceId = device.Id; _product = device.ProductRefId; _hardware = device.Hardware2ProgramRefId;
        _address = device.Address.Raw; _revision = project.ApplicationDataRevision;
        ResourceContextFingerprint = context.Fingerprint;
        Fingerprint = ParameterPatchSourceBinding.Hash(new
        {
            ProfileVersion, projectId = _projectId, projectGuid = _projectGuid, deviceId = _deviceId,
            product = _product, hardware = _hardware, address = _address, revision = _revision,
            applicationDefinitionHash = appHash, ResourceContextFingerprint,
            hardwareStatus = hardware.Status, hardwareDefinitionHash = hardware.DefinitionHash,
            factoryStatus = hardware.FactoryImage.Status, factoryHash = hardware.FactoryImage.BlobHash,
            checksumStatus = hardware.ChecksumStatus, checksumHash = hardware.ChecksumData?.Sha256,
        });
    }
    /// <summary>Rechecks immutable source selection without scripts, a device connection or any mutation.</summary>
    public bool IsCurrent(Project project, DeviceInstance device)
    {
        if (!MatchesIdentity(project, device)) return false;
        return ArchivedImageSources.Read(project, device).Binding?.Fingerprint == Fingerprint;
    }
    internal bool MatchesIdentity(Project project, DeviceInstance device)
        => ReferenceEquals(project, _project) && ReferenceEquals(device, _device) && project.Id == _projectId
            && project.Guid == _projectGuid && device.Id == _deviceId && device.ProductRefId == _product
            && device.Hardware2ProgramRefId == _hardware && device.Address.Raw == _address
            && project.ApplicationDataRevision == _revision && ProjectQuery.DeviceList(project).Any(d => ReferenceEquals(d, device));
}

/// <summary>Reads exact archive inputs for later image stages. Missing sources never become zero buffers.</summary>
public static class ArchivedImageSources
{
    private const int MaxBlob = 1024 * 1024;
    public static ArchivedImageSourceManifest Read(Project project, DeviceInstance device)
        => ReadInputs(project, device).Manifest;

    // Detached documents stay inside one Core inspection; public results retain no mutable XML.
    internal sealed record Inputs(ArchivedImageSourceManifest Manifest, XDocument? Application = null, XDocument? Master = null);
    internal static Inputs ReadInputs(Project project, DeviceInstance device)
    {
        if (!ProjectQuery.DeviceList(project).Any(d => ReferenceEquals(d, device))
            || !Safe(device.Id) || !Safe(device.ProductRefId)
            || device.Hardware2ProgramRefId is { } hardwareRef && !Safe(hardwareRef)) return new(new(ArchivedImageSourceStatus.InvalidRequest));
        try
        {
            var app = Knxproj.LoadApplicationProgram(project, device.Hardware2ProgramRefId ?? device.ProductRefId);
            if (app is null) return new(new(ArchivedImageSourceStatus.ApplicationUnavailable));
            var manifest = ReadDocuments(project, device, app, out var master);
            return new(manifest, app, master);
        }
        catch (SourceFailure failure) { return new(new(failure.Status)); }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or FormatException or NotSupportedException)
        { return new(new(ArchivedImageSourceStatus.ApplicationUnavailable)); }
    }

    private static ArchivedImageSourceManifest ReadDocuments(Project project, DeviceInstance device, XDocument app, out XDocument? master)
    {
        master = null;
        if (!WithinBudget(app, 128 * 1024 * 1024)) throw new SourceFailure(ArchivedImageSourceStatus.LimitExceeded);
        if (app.Root is not { Name.LocalName: "KNX" } root) throw new SourceFailure(ArchivedImageSourceStatus.InvalidDeclaration);
        var ns = root.Name.Namespace;
        if (!Supported(ns.NamespaceName)) throw new SourceFailure(ArchivedImageSourceStatus.UnsupportedApplicationNamespace);
        var programs = root.Descendants().Where(e => e.Name.LocalName == "ApplicationProgram").Take(2).ToArray();
        if (programs.Length != 1 || programs[0].Name != ns + "ApplicationProgram"
            || !Safe((string?)programs[0].Attribute("Id"))) throw new SourceFailure(ArchivedImageSourceStatus.InvalidDeclaration);
        var id = (string)programs[0].Attribute("Id")!;
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^M-[0-9a-fA-F]{4}_A-[A-Za-z0-9_.-]+$")
            || app.Annotation<ArchivePartIdentity>()?.EntryPath != Knxproj.ApplicationProgramEntryPath(id)
            || !Path(programs[0], ns, "ApplicationProgram", "ApplicationPrograms", "Manufacturer", "ManufacturerData", "KNX")
            || !Manufacturer(programs[0].Parent!.Parent!, ns, id[..6]))
            throw new SourceFailure(ArchivedImageSourceStatus.InvalidDeclaration);
        try { master = Knxproj.LoadMasterData(project); }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or FormatException or NotSupportedException) { }
        var context = ArchivedResourceContext.Read(master, (string?)programs[0].Attribute("MaskVersion"), ns.NamespaceName);
        var hardware = ReadHardware(project, device.Hardware2ProgramRefId, id);
        var definition = HashDocument(app);
        var binding = new ArchivedImageSourceBinding(project, device, definition, context, hardware);
        return new(ArchivedImageSourceStatus.Inspected, id, ns.NamespaceName, definition, context, hardware, binding);
    }

    private static ArchivedHardwareImageSource ReadHardware(Project project, string? reference, string appId)
    {
        if (reference is null) return new(ArchivedHardwareSourceStatus.ReferenceMissing, null);
        try
        {
            var document = Knxproj.LoadHardwareData(project, reference);
            if (document is null) return new(ArchivedHardwareSourceStatus.PartMissing, reference);
            if (!WithinBudget(document, 16 * 1024 * 1024)) return new(ArchivedHardwareSourceStatus.LimitExceeded, reference);
            if (document.Root is not { Name.LocalName: "KNX" } root) return new(ArchivedHardwareSourceStatus.InvalidDeclaration, reference);
            if (!Supported(root.Name.NamespaceName)) return new(ArchivedHardwareSourceStatus.UnsupportedNamespace, reference);
            var ns = root.Name.Namespace;
            var records = root.Descendants().Where(e => e.Name.LocalName == "Hardware2Program" && (string?)e.Attribute("Id") == reference).Take(2).ToArray();
            if (records.Length != 1) return new(records.Length == 0 ? ArchivedHardwareSourceStatus.ReferenceNotFound : ArchivedHardwareSourceStatus.AmbiguousReference, reference);
            var selected = records[0];
            if (selected.Name != ns + "Hardware2Program") return new(ArchivedHardwareSourceStatus.InvalidDeclaration, reference);
            if (!Path(selected, ns, "Hardware2Program", "Hardware2Programs", "Hardware", "Hardware", "Manufacturer", "ManufacturerData", "KNX")
                || !Manufacturer(selected.Parent!.Parent!.Parent!.Parent!, ns, reference[..6])
                || selected.Elements().Any(e => e.Name != ns + "ApplicationProgramRef" && e.Name != ns + "RegistrationInfo"))
                return new(ArchivedHardwareSourceStatus.InvalidDeclaration, reference);
            var refElements = selected.Elements().Where(e => e.Name.LocalName == "ApplicationProgramRef").Take(3).ToArray();
            if (refElements.Any(e => e.Name != ns + "ApplicationProgramRef")) return new(ArchivedHardwareSourceStatus.InvalidDeclaration, reference);
            var refs = refElements.Select(e => (string?)e.Attribute("RefId")).ToArray();
            var definition = HashText(selected.ToString(SaveOptions.DisableFormatting));
            if (refs.Length == 0) return new(ArchivedHardwareSourceStatus.ApplicationMismatch, reference, definition);
            if (refs.Length != 1) return new(ArchivedHardwareSourceStatus.MultipleApplications, reference, definition);
            if (refs[0] != appId) return new(ArchivedHardwareSourceStatus.ApplicationMismatch, reference, definition);
            var imageText = (string?)selected.Attribute("LoadedImage");
            var imageBytes = Decode(imageText, out var imageStatus);
            var factory = imageStatus switch
            {
                ArchivedChecksumSourceStatus.InvalidData => new ArchivedFactoryImageSource(ArchivedFactoryImageStatus.InvalidData),
                ArchivedChecksumSourceStatus.LimitExceeded => new ArchivedFactoryImageSource(ArchivedFactoryImageStatus.LimitExceeded),
                _ => ArchivedFactoryImage.Read(imageBytes ?? []),
            };
            var stamp = Decode((string?)selected.Attribute("CheckSums"), out var stampStatus);
            return new(ArchivedHardwareSourceStatus.Inspected, reference, definition, imageText is not null, factory,
                stampStatus, stamp is null ? null : new ImmutablePatchBytes(stamp));
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or FormatException or NotSupportedException)
        { return new(ArchivedHardwareSourceStatus.Unreadable, reference); }
    }

    private static byte[]? Decode(string? text, out ArchivedChecksumSourceStatus status)
    {
        status = ArchivedChecksumSourceStatus.Missing;
        if (text is null) return null;
        if (text.Length > MaxBlob * 4 / 3 + 4096) { status = ArchivedChecksumSourceStatus.LimitExceeded; return null; }
        var clean = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is ' ' or '\t' or '\r' or '\n') continue;
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/' or '=')) { status = ArchivedChecksumSourceStatus.InvalidData; return null; }
            clean.Append(ch);
        }
        if (clean.Length % 4 != 0) { status = ArchivedChecksumSourceStatus.InvalidData; return null; }
        var maximum = clean.Length / 4 * 3;
        if (clean.Length > 0 && clean[^1] == '=') maximum--;
        if (clean.Length > 1 && clean[^2] == '=') maximum--;
        if (maximum > MaxBlob) { status = ArchivedChecksumSourceStatus.LimitExceeded; return null; }
        try { var bytes = Convert.FromBase64String(clean.ToString()); status = ArchivedChecksumSourceStatus.Present; return bytes; }
        catch (FormatException) { status = ArchivedChecksumSourceStatus.InvalidData; return null; }
    }

    private static bool Safe(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl) && !string.IsNullOrWhiteSpace(value);
    private static bool Path(XElement leaf, XNamespace ns, params string[] names)
    {
        XElement? current = leaf;
        foreach (var name in names)
        { if (current?.Name != ns + name) return false; current = current.Parent; }
        return current is null;
    }
    private static bool Manufacturer(XElement manufacturer, XNamespace ns, string id)
        => (string?)manufacturer.Attribute("RefId") == id
            && manufacturer.Parent!.Elements().Count(e => e.Name.LocalName == "Manufacturer" && (string?)e.Attribute("RefId") == id) == 1
            && manufacturer.Parent.Parent!.Elements(ns + "ManufacturerData").Count() == 1;
    private static bool Supported(string value) => value is "http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23";
    private static string HashText(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string HashDocument(XDocument document)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new HashStream(digest);
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, CloseOutput = false })) document.WriteTo(writer);
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }
    private static bool WithinBudget(XDocument document, int maxCharacters)
    {
        if (document.DocumentType is not null || document.Root is null) return false;
        var stack = new Stack<(XNode Node, int Depth)>();
        foreach (var node in document.Nodes())
        { if (stack.Count >= 1000000) return false; stack.Push((node, 1)); }
        long characters = 0; var nodes = 0;
        while (stack.TryPop(out var item))
        {
            if (++nodes > 1000000 || item.Depth > 128) return false;
            if (item.Node is XElement element)
            {
                characters += element.Name.ToString().Length;
                foreach (var attribute in element.Attributes()) characters += attribute.Name.ToString().Length + attribute.Value.Length;
                foreach (var child in element.Nodes())
                { if (stack.Count >= 1000000) return false; stack.Push((child, item.Depth + 1)); }
            }
            else if (item.Node is XText text) characters += text.Value.Length;
            else if (item.Node is XComment comment) characters += comment.Value.Length;
            else if (item.Node is XProcessingInstruction pi) characters += pi.Target.Length + pi.Data.Length;
            if (characters > maxCharacters) return false;
        }
        return true;
    }
    private sealed class SourceFailure(ArchivedImageSourceStatus status) : Exception
    { internal ArchivedImageSourceStatus Status { get; } = status; }
    private sealed class HashStream(IncrementalHash digest) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => digest.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => digest.AppendData(buffer);
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
