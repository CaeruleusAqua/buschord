using System.Security.Cryptography;

namespace Knx;

public sealed class OfflineApplicationBuildPreview
{
    public string Scope => "OfflineApplicationBuildPlan";
    public int ProfileVersion => 1;
    public bool IsAvailable => Binding is not null && Procedure?.IsAvailable == true && Issues.Count == 0
        && (GeneratedTables is null || GeneratedTables.IsAvailable);
    public bool ExecutionEligible => false;
    public bool CompleteApplicationImage => false;
    public bool DownloadEligible => false;
    public ArchivedImageSourceManifest Sources { get; }
    public LoadProcedurePlan? Procedure { get; }
    public ApplicationBuildSourceBinding? Binding { get; }
    public GeneratedTablePreview? GeneratedTables { get; }
    public IReadOnlyList<LoadProcedureIssue> Issues { get; }
    internal OfflineApplicationBuildPreview(ArchivedImageSourceManifest sources, LoadProcedureIssue issue)
    { Sources = sources; Issues = Array.AsReadOnly(new[] { issue }); }
    internal OfflineApplicationBuildPreview(ArchivedImageSourceManifest sources, LoadProcedurePlan procedure, ApplicationBuildSourceBinding? binding)
    { Sources = sources; Procedure = procedure; Binding = binding; Issues = procedure.Issues; }
    internal OfflineApplicationBuildPreview(ArchivedImageSourceManifest sources, LoadProcedurePlan procedure,
        ApplicationBuildSourceBinding? binding, GeneratedTablePreview tables)
        : this(sources, procedure, binding) { GeneratedTables = tables; }
}

/// <summary>Detects changes to source selection and declared project inputs; never authorizes execution.</summary>
public sealed class ApplicationBuildSourceBinding
{
    private readonly ArchivedImageSourceBinding _sources;
    private readonly LoadProcedureRequest _request;
    public int ProfileVersion => 1;
    public string SourceFingerprint { get; }
    public string ProcedureWitnessHash { get; }
    public string ProjectInputHash { get; }
    public string Fingerprint { get; }
    internal ApplicationBuildSourceBinding(ArchivedImageSourceBinding sources, LoadProcedurePlan procedure, string inputs, LoadProcedureRequest request)
    {
        _sources = sources; _request = request with { }; SourceFingerprint = sources.Fingerprint;
        ProcedureWitnessHash = procedure.WitnessHash!; ProjectInputHash = inputs;
        Fingerprint = ParameterPatchSourceBinding.Hash(new { ProfileVersion, SourceFingerprint, ProcedureWitnessHash, ProjectInputHash, request = _request });
    }
    public bool IsCurrent(Project project, DeviceInstance device)
    {
        if (!_sources.MatchesIdentity(project, device)) return false;
        return OfflineApplicationBuildPlan.Preview(project, device, _request).Binding?.Fingerprint == Fingerprint;
    }
}

/// <summary>Combines exact archive sources, primary-program controls and declared project inputs without scripts or transport.</summary>
public static partial class OfflineApplicationBuildPlan
{
    public static OfflineApplicationBuildPreview Preview(Project project, DeviceInstance device, LoadProcedureRequest request)
    {
        var inputs = ArchivedImageSources.ReadInputs(project, device);
        OfflineApplicationBuildPreview Error(LoadProcedureIssueCode code) => new(inputs.Manifest, new(code));
        if (request is null) return Error(LoadProcedureIssueCode.InvalidRequest);
        if (inputs.Manifest.Status != ArchivedImageSourceStatus.Inspected || inputs.Application is null)
            return Error(LoadProcedureIssueCode.InvalidApplication);
        if (device.Hardware2ProgramRefId is not null && inputs.Manifest.Hardware?.Status != ArchivedHardwareSourceStatus.Inspected)
            return Error(LoadProcedureIssueCode.InvalidApplication);
        var inputHash = InputHash(project, device);
        if (inputHash is null) return Error(LoadProcedureIssueCode.LimitExceeded);
        var revision = project.ApplicationDataRevision;
        var procedure = LoadProcedureCompiler.Compile(inputs.Application, inputs.Master, request);
        if (!procedure.IsAvailable) return new(inputs.Manifest, procedure, null);
        if (project.ApplicationDataRevision != revision || !inputs.Manifest.Binding!.MatchesIdentity(project, device)
            || InputHash(project, device) != inputHash)
            return Error(LoadProcedureIssueCode.InvalidRequest);
        var binding = new ApplicationBuildSourceBinding(inputs.Manifest.Binding!, procedure, inputHash, request);
        return new(inputs.Manifest, procedure, binding);
    }

    /// <summary>Explicit table generation remains an offline fragment preview with a separate source binding.</summary>
    public static OfflineApplicationBuildPreview Preview(Project project, DeviceInstance device, LoadProcedureRequest request,
        GeneratedTableRequest tables, IParameterScriptRuntime? scripts = null)
    {
        var inputs = ArchivedImageSources.ReadInputs(project, device);
        OfflineApplicationBuildPreview Error(LoadProcedureIssueCode code) => new(inputs.Manifest, new(code));
        if (request is null || tables is null || !Enum.IsDefined(tables.Format)
            || (request.Parts & (LoadParts.Full | LoadParts.Groups)) == LoadParts.None)
            return Error(LoadProcedureIssueCode.InvalidRequest);
        if (inputs.Manifest.Status != ArchivedImageSourceStatus.Inspected || inputs.Application is null
            || device.Hardware2ProgramRefId is not null && inputs.Manifest.Hardware?.Status != ArchivedHardwareSourceStatus.Inspected)
            return Error(LoadProcedureIssueCode.InvalidApplication);
        var inputHash = InputHash(project, device);
        if (inputHash is null) return Error(LoadProcedureIssueCode.LimitExceeded);
        var revision = project.ApplicationDataRevision;
        var procedure = LoadProcedureCompiler.Compile(inputs.Application, inputs.Master, request);
        if (!procedure.IsAvailable) return new(inputs.Manifest, procedure, null);
        var generated = GeneratedTableCompiler.Compile(project, device, inputs, procedure, tables, scripts);
        if (project.ApplicationDataRevision != revision || !inputs.Manifest.Binding!.MatchesIdentity(project, device)
            || InputHash(project, device) != inputHash)
            generated = GeneratedTablePreview.Unavailable(new(GeneratedTableIssueCode.InputsChanged));
        var binding = generated.IsAvailable ? new ApplicationBuildSourceBinding(inputs.Manifest.Binding!, procedure, inputHash, request) : null;
        return new(inputs.Manifest, procedure, binding, generated);
    }

    // These are declared inputs only. Workbench activity/visibility and connector metadata
    // are fingerprinted as facts, without becoming a proven active table or parameter set.
    private static string? InputHash(Project project, DeviceInstance device)
    {
        const int maxObjects = 100000, maxGroups = 65536, maxLinks = 200000, maxCharacters = 8 * 1024 * 1024;
        if (!ParameterPatchSourceBinding.ValuesWithinBudget(device) || device.ComObjectRefs.Count > maxObjects
            || project.Workbench.Objects.Count > 200000 || project.Workbench.Groups.Count > maxGroups || device.BinaryData.Count > 1024) return null;
        long characters = 0, links = 0, binaryBytes = 0;
        bool Text(string? value, int limit = 256)
        {
            if (value is null) return true;
            characters += value.Length;
            return value.Length <= limit && characters <= maxCharacters;
        }
        foreach (var value in device.ParameterValues)
            if (!Text(value.Key) || !Text(value.Value, 65536)) return null;
        foreach (var obj in device.ComObjectRefs)
        {
            if (!Text(obj.Id) || !Text(obj.ComObjectId) || !Text(obj.Device) || !Text(obj.ChannelId)
                || (links += obj.Links.Count) > maxLinks) return null;
            foreach (var id in obj.Links) if (!Text(id)) return null;
        }
        foreach (var part in device.BinaryData)
            if (!Text(part.Key) || part.Value is null || part.Value.Length > 1024 * 1024
                || (binaryBytes += part.Value.Length) > 4 * 1024 * 1024) return null;
        var prefix = new Workbench.ObjectKey(device.Id, "").ToString();
        var metadata = project.Workbench.Objects.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        if (metadata.Length > maxObjects) return null;
        foreach (var pair in metadata)
        {
            var value = pair.Value;
            if (!Text(pair.Key, 512) || value is null || value.Connectors is null || value.Connectors.Count > maxGroups
                || !Text(value.Dpt, 64) || !Text(value.Flags, 64) || !Text(value.ChannelId)
                || !Text(value.StructuralDeclaredChannelId) || !Text(value.SendingGroupId)
                || (links += value.Connectors.Count) > maxLinks) return null;
            foreach (var connector in value.Connectors)
                if (!Text(connector.Key) || connector.Value is null) return null;
        }
        var groups = new List<GroupAddressEntry>(); var ranges = new Stack<(GroupRange Range, int Depth)>();
        var seen = new HashSet<GroupRange>(ReferenceEqualityComparer.Instance);
        foreach (var installation in project.Installations)
            foreach (var range in installation.MainRanges)
            { if (ranges.Count >= maxGroups) return null; ranges.Push((range, 1)); }
        while (ranges.TryPop(out var item))
        {
            if (item.Depth > 128 || !seen.Add(item.Range) || seen.Count > maxGroups
                || item.Range.Addresses.Count > maxGroups - groups.Count) return null;
            foreach (var group in item.Range.Addresses)
            {
                if (!Text(group.Id) || !Text(group.DatapointType, 64)) return null;
                groups.Add(group);
            }
            foreach (var child in item.Range.Ranges)
            { if (ranges.Count >= maxGroups) return null; ranges.Push((child, item.Depth + 1)); }
        }
        var groupMetadata = project.Workbench.Groups.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        foreach (var pair in groupMetadata) if (!Text(pair.Key) || pair.Value is null) return null;
        return ParameterPatchSourceBinding.Hash(new
        {
            profileVersion = 1,
            values = device.ParameterValues.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { p.Key, p.Value }).ToArray(),
            objects = device.ComObjectRefs.Select(obj => new { obj.Id, obj.ComObjectId, obj.Number, obj.Device, obj.ChannelId, links = obj.Links.ToArray() }).ToArray(),
            metadata = metadata.Select(p => new
            {
                p.Key, p.Value.Dpt, p.Value.Bits, p.Value.Flags, p.Value.Active, p.Value.StructuralPreviousActive,
                p.Value.StructuralDeclaredNumber, p.Value.ChannelId, p.Value.ChannelSource, p.Value.StructuralDeclaredChannelId,
                p.Value.SendingGroupId,
                connectors = p.Value.Connectors.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new
                { c.Key, c.Value.Send, c.Value.Acknowledge, c.Value.Priority, c.Value.Slot }).ToArray(),
            }).ToArray(),
            groups = groups.Select(g => new { g.Id, address = g.Address.Raw, g.DatapointType, g.Puid }).ToArray(),
            groupMetadata = groupMetadata.Select(p => new { p.Key, p.Value.Bits, p.Value.Secure }).ToArray(),
            binaryData = device.BinaryData.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new
            { p.Key, hash = Convert.ToHexStringLower(SHA256.HashData(p.Value)) }).ToArray(), device.HasOpaqueBinaryData,
        });
    }
}
