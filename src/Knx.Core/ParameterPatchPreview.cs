using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Knx;

public enum ParameterPatchByteOrder { BigEndian, LittleEndian }
public enum ParameterPatchIssueCode
{
    InvalidRequest, ApplicationUnavailable, InvalidDeclaration, UnsupportedApplication, UnsupportedEvaluation,
    UnknownParameter, UnsupportedParameter, UnsupportedDependency, UnsupportedLayout, InvalidValue,
    UnknownInitialData, InvalidInitialMask, AmbiguousOverlap, ResourceLimitExceeded,
}
public sealed record ParameterPatchValue(string RefId, string RawValue);
public sealed record ParameterPatchIssue(ParameterPatchIssueCode Code, string? RefId = null, string? ResourceId = null);
public sealed record ParameterPatchField(string RefId, string ParameterId, string ResourceId, string RawValue,
    uint StartAddress, byte BitOffset, uint BitLength);

/// <summary>Owned immutable bytes. Both incoming and outgoing arrays are copied.</summary>
public sealed class ImmutablePatchBytes
{
    private readonly byte[] _bytes;
    internal ImmutablePatchBytes(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();
    public int Length => _bytes.Length;
    public byte this[int index] => _bytes[index];
    public byte[] ToArray() => (byte[])_bytes.Clone();
    public string ToHexString() => Convert.ToHexString(_bytes);
    public string Sha256 => Convert.ToHexStringLower(SHA256.HashData(_bytes));
}

/// <summary>Original supplied bytes only. Missing mask remains null; size does not create initial bytes.</summary>
public sealed class InitialMemoryResource
{
    public string Id { get; }
    public uint StartAddress { get; }
    public uint? DeclaredSize { get; }
    public ImmutablePatchBytes Data { get; }
    public ImmutablePatchBytes? Mask { get; }
    internal InitialMemoryResource(string id, uint address, uint? size, byte[] data, byte[]? mask)
    { Id = id; StartAddress = address; DeclaredSize = size; Data = new(data); Mask = mask is null ? null : new(mask); }
}

public sealed record ParameterPatchRange(string ResourceId, uint StartAddress, ImmutablePatchBytes BeforeData,
    ImmutablePatchBytes AfterData, ImmutablePatchBytes FieldMask, ImmutablePatchBytes? BeforeMask, ImmutablePatchBytes? AfterMask)
{
    public int Length => BeforeData.Length;
}

/// <summary>Read-only live-source binding, never a commit token. Private identities are never exposed.</summary>
public sealed class ParameterPatchSourceBinding
{
    private readonly Project _project;
    private readonly DeviceInstance _device;
    private readonly string? _product, _hardware;
    private readonly ushort _address;
    public string ProjectId { get; }
    public string? ProjectGuid { get; }
    public string DeviceId { get; }
    public long ApplicationDataRevision { get; }
    public string ApplicationId { get; }
    public string DefinitionHash { get; }
    public string SavedValuesHash { get; }
    public string SubsetHash { get; }
    public ParameterPatchByteOrder ByteOrder { get; }
    public int CodecProfileVersion { get; }
    /// <summary>Exact integer words, optionally with the explicitly bounded .NET10 XML Double-to-Single parser profile.</summary>
    public string NumericParseProfile { get; }
    /// <summary>Selected native fixed-byte Time encoding; null when no supported packed Time field was selected.</summary>
    public string? PackedTimeEncodingProfile { get; }
    /// <summary>Nontruncating raw signed Time counts without duration scaling; null for legacy codecs.</summary>
    public string? DenseTimeEncodingProfile { get; }
    /// <summary>Checked direct literal module scope; a witness is metadata, never an activation or commit token.</summary>
    public int? ModuleScopeProfileVersion { get; }
    public string? ModuleScopeWitnessHash { get; }
    public string Fingerprint { get; }

    internal ParameterPatchSourceBinding(Project project, DeviceInstance device, string application, string definitionHash,
        ParameterPatchValue[] subset, ParameterPatchByteOrder order, IReadOnlyList<InitialMemoryResource> resources, bool includesSingle, bool includesPackedTime, bool includesDenseTime,
        string? moduleScopeWitnessHash, int? moduleScopeProfileVersion)
    {
        _project = project; _device = device; _product = device.ProductRefId; _hardware = device.Hardware2ProgramRefId; _address = device.Address.Raw;
        ProjectId = project.Id; ProjectGuid = project.Guid; DeviceId = device.Id; ApplicationDataRevision = project.ApplicationDataRevision;
        ApplicationId = application; DefinitionHash = definitionHash; SavedValuesHash = ValuesHash(device);
        SubsetHash = Hash(subset.OrderBy(v => v.RefId, StringComparer.Ordinal).ToArray()); ByteOrder = order;
        NumericParseProfile = includesSingle ? "ExactIntegerAndDotNet10XmlDoubleToSingle" : "ExactInteger";
        PackedTimeEncodingProfile = includesPackedTime ? "PackedDaysHoursMinutesAndSeconds24BigEndianOnly" : null;
        CodecProfileVersion = includesDenseTime ? 4 : 3;
        DenseTimeEncodingProfile = includesDenseTime ? "SignedRawOrdinaryTime8To32" : null;
        ModuleScopeWitnessHash = moduleScopeWitnessHash; ModuleScopeProfileVersion = moduleScopeWitnessHash is null ? null : moduleScopeProfileVersion;
        // Preserve the legacy envelope's EXACT member set/order: even a new null
        // member would change every previously published fingerprint.
        Fingerprint = includesDenseTime ? Hash(new { version = 5, CodecProfileVersion, NumericParseProfile, PackedTimeEncodingProfile, DenseTimeEncodingProfile, ModuleScopeProfileVersion, ModuleScopeWitnessHash,
            ProjectId, ProjectGuid, DeviceId, ApplicationDataRevision, ApplicationId,
            DefinitionHash, SavedValuesHash, SubsetHash, ByteOrder, resources = resources.OrderBy(r => r.Id, StringComparer.Ordinal)
                .Select(r => new { r.Id, r.StartAddress, r.DeclaredSize, data = r.Data.Sha256, mask = r.Mask?.Sha256 }).ToArray() })
            : Hash(new { version = 4, CodecProfileVersion, NumericParseProfile, PackedTimeEncodingProfile, ModuleScopeProfileVersion, ModuleScopeWitnessHash,
            ProjectId, ProjectGuid, DeviceId, ApplicationDataRevision, ApplicationId,
            DefinitionHash, SavedValuesHash, SubsetHash, ByteOrder, resources = resources.OrderBy(r => r.Id, StringComparer.Ordinal)
                .Select(r => new { r.Id, r.StartAddress, r.DeclaredSize, data = r.Data.Sha256, mask = r.Mask?.Sha256 }).ToArray() });
    }

    public bool IsCurrent(Project project, DeviceInstance device)
    {
        if (!ReferenceEquals(project, _project) || !ReferenceEquals(device, _device) || project.Id != ProjectId || project.Guid != ProjectGuid
            || device.Id != DeviceId || project.ApplicationDataRevision != ApplicationDataRevision || device.Address.Raw != _address
            || device.ProductRefId != _product || device.Hardware2ProgramRefId != _hardware
            || !ProjectQuery.DeviceList(project).Any(d => ReferenceEquals(d, device)) || !ValuesWithinBudget(device)) return false;
        return ValuesHash(device) == SavedValuesHash;
    }

    internal static bool ValuesWithinBudget(DeviceInstance device) => device.ParameterValues.Count <= 100000
        && device.ParameterValues.All(p => p.Value is not null)
        && device.ParameterValues.Sum(p => (long)p.Key.Length + p.Value.Length) <= 16 * 1024 * 1024;
    internal static string ValuesHash(DeviceInstance device) => Hash(device.ParameterValues.OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => new { p.Key, p.Value }).ToArray());
    internal static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    internal static string DefinitionFingerprint(XDocument document)
    {
        using var algorithm = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write);
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { CloseOutput = false, Indent = false })) document.WriteTo(writer);
        stream.FlushFinalBlock();
        return Convert.ToHexStringLower(algorithm.Hash!);
    }
}

/// <summary>An explicit subset byte preview; never an application image or download eligibility.</summary>
public sealed class ParameterPatchPreview
{
    public string Scope => "ExplicitSubsetPreview";
    public bool CompleteApplicationImage => false;
    public bool DownloadEligible => false;
    public bool IsAvailable => Binding is not null && Issues.Count == 0;
    public ParameterPatchSourceBinding? Binding { get; }
    public IReadOnlyList<ParameterPatchIssue> Issues { get; }
    public IReadOnlyList<ParameterPatchField> Fields { get; }
    public IReadOnlyList<ParameterPatchRange> Ranges { get; }
    public IReadOnlyList<InitialMemoryResource> Resources { get; }
    internal ParameterPatchPreview(ParameterPatchIssue issue)
    { Issues = Array.AsReadOnly(new[] { issue }); Fields = Array.AsReadOnly(Array.Empty<ParameterPatchField>()); Ranges = Array.AsReadOnly(Array.Empty<ParameterPatchRange>()); Resources = Array.AsReadOnly(Array.Empty<InitialMemoryResource>()); }
    internal ParameterPatchPreview(ParameterPatchSourceBinding binding, List<ParameterPatchField> fields,
        List<ParameterPatchRange> ranges, List<InitialMemoryResource> resources)
    { Binding = binding; Issues = Array.AsReadOnly(Array.Empty<ParameterPatchIssue>()); Fields = fields.AsReadOnly(); Ranges = ranges.AsReadOnly(); Resources = resources.AsReadOnly(); }
}
