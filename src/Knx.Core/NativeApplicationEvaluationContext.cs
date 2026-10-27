using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Knx.Catalog;

namespace Knx;

/// <summary>One owned application evaluation shared by offline consumers; no interpreter or mutable host object is published.</summary>
internal sealed class NativeApplicationEvaluationContext
{
    private readonly ArchivedImageSourceBinding? _sourceBinding;
    private readonly string? _applicationDocumentHash;
    private readonly string? _masterDocumentHash;
    private readonly string? _snapshotHash;
    private readonly string? _ownedSourceHash;
    internal bool IsAvailable => Fingerprint is not null && Issues.Count == 0;
    internal IReadOnlyList<GeneratedTableIssue> Issues { get; }
    internal string? ApplicationDefinitionHash { get; }
    internal string? SourceFingerprint { get; }
    internal string? ResourceContextFingerprint { get; }
    internal string? ProjectInputHash => CapturedInputs?.Fingerprint;
    internal string? Fingerprint { get; }
    internal bool RequiresScripts => ScriptCapabilityUsed;
    internal bool ScriptCapabilityUsed { get; }
    internal NativeCommunicationInputs? CapturedInputs { get; }
    internal NativeCommunicationOriginCatalog? Catalog { get; }
    internal XElement? OriginalApplication { get; }
    internal ApplicationSnapshot? Snapshot { get; }
    internal NativeActivationProof? Activation { get; }
    internal NativeStructuralActivationProof? Structure { get; }
    internal IReadOnlyDictionary<string, string?> EffectiveValues { get; }
    internal IReadOnlySet<string> AdditionalControllerReferences { get; }

    private NativeApplicationEvaluationContext(GeneratedTableIssue issue)
    {
        Issues = Array.AsReadOnly(new[] { issue });
        EffectiveValues = new Dictionary<string, string?>().ToFrozenDictionary(StringComparer.Ordinal);
        AdditionalControllerReferences = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    }
    private NativeApplicationEvaluationContext(ArchivedImageSources.Inputs inputs, NativeCommunicationInputs captured,
        NativeCommunicationOriginCatalog catalog, XElement application, ApplicationSnapshot snapshot, NativeActivationProof activation,
        NativeStructuralActivationProof structure, Dictionary<string, string?> values, string[] additional, bool scripts)
    {
        _sourceBinding = inputs.Manifest.Binding; _applicationDocumentHash = DocumentHash(inputs.Application!, 128 * 1024 * 1024);
        _masterDocumentHash = inputs.Master is null ? null : DocumentHash(inputs.Master, 16 * 1024 * 1024);
        ApplicationDefinitionHash = inputs.Manifest.ApplicationDefinitionHash; SourceFingerprint = _sourceBinding!.Fingerprint;
        ResourceContextFingerprint = inputs.Manifest.ResourceContext!.Fingerprint;
        CapturedInputs = captured; Catalog = catalog; OriginalApplication = application; Snapshot = snapshot; Activation = activation; Structure = structure;
        _ownedSourceHash = DocumentHash(catalog.Nodes[0], 128 * 1024 * 1024);
        ScriptCapabilityUsed = scripts; _snapshotHash = SnapshotHash(snapshot);
        EffectiveValues = values.ToFrozenDictionary(StringComparer.Ordinal); AdditionalControllerReferences = additional.ToFrozenSet(StringComparer.Ordinal);
        Issues = Array.AsReadOnly(Array.Empty<GeneratedTableIssue>());
        Fingerprint = NativeCommunicationGuard.Hash(new
        {
            version = 1, ApplicationDefinitionHash, SourceFingerprint, ResourceContextFingerprint, ProjectInputHash,
            application = _applicationDocumentHash, master = _masterDocumentHash, snapshot = _snapshotHash,
            activation = activation.Fingerprint, structure = structure.Fingerprint, additional, scriptCapability = scripts,
        });
    }
    internal static NativeApplicationEvaluationContext Create(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        IReadOnlyCollection<string>? additionalControllerRefs = null, IParameterScriptRuntime? scripts = null)
    {
        try { return Mint(project, device, inputs, additionalControllerRefs, scripts); }
        catch (NativeCommunicationRejected failure) { return new(failure.Issue); }
        catch (Exception failure) when (failure is XmlException or InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { return new(new(GeneratedTableIssueCode.InvalidApplication)); }
    }
    private static NativeApplicationEvaluationContext Mint(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        IReadOnlyCollection<string>? additionalRefs, IParameterScriptRuntime? scripts)
    {
        if (inputs.Manifest.Status != ArchivedImageSourceStatus.Inspected || inputs.Application is null || inputs.Manifest.Binding is null
            || !inputs.Manifest.Binding.MatchesIdentity(project, device) || inputs.Manifest.ApplicationDefinitionHash is null)
            NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidApplication);
        if (inputs.Manifest.ResourceContext?.Status != ArchivedResourceContextStatus.Supported)
            NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidMaster);
        if (additionalRefs?.Count > 4096) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
        var additional = additionalRefs?.Take(4097).ToArray() ?? [];
        if (additional.Length > 4096) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
        Array.Sort(additional, StringComparer.Ordinal);
        if (additional.Any(id => !NativeCommunicationGuard.Safe(id)) || additional.Distinct(StringComparer.Ordinal).Count() != additional.Length)
            NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidRequest);
        var documentHash = DocumentHash(inputs.Application, 128 * 1024 * 1024);
        if (documentHash != inputs.Manifest.ApplicationDefinitionHash) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.StaleSource);
        var masterHash = inputs.Master is null ? null : DocumentHash(inputs.Master, 16 * 1024 * 1024);
        var captured = NativeCommunicationInputs.Capture(project, device);
        var document = new XDocument(inputs.Application); var root = document.Root!; var ns = root.Name.Namespace;
        if (root.Name.LocalName != "KNX" || ns.NamespaceName is not ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23"))
            NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidApplication);
        var catalog = new NativeCommunicationOriginCatalog(root);
        if (!catalog.IsAvailable) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
        if (catalog.Nodes.Any(e => e.Name.Namespace != ns)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidApplication);
        var applications = catalog.Nodes.Where(e => e.Name == ns + "ApplicationProgram").Take(2).ToArray();
        if (applications.Length != 1 || (string?)applications[0].Attribute("Id") != inputs.Manifest.ApplicationId)
            NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidApplication);
        var application = applications[0]; var appId = (string)application.Attribute("Id")!;
        ProfileGate(root, appId);
        var profile = ArchivedResourceContext.Read(inputs.Master, (string?)application.Attribute("MaskVersion"), ns.NamespaceName);
        if (profile.Status != ArchivedResourceContextStatus.Supported || profile.Fingerprint != inputs.Manifest.ResourceContext.Fingerprint)
            NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidMaster);
        catalog.Stamp(root); root.AddAnnotation(catalog);
        var sourceScopes = NativeStructuralActivationProof.SourceScopeElements(catalog);
        additional = additional.Concat(catalog.StructuralPlan.CallControllerReferences).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(4097).ToArray();
        if (additional.Length > 4096 || additional.Any(id => !NativeCommunicationGuard.Safe(id))) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
        DependencyGate(sourceScopes, additional);
        NativeActivationProof.ValidateControllerDeclarations(sourceScopes, additional);
        var values = new Dictionary<string, string?>(captured.Values, StringComparer.Ordinal);
        foreach (var pair in captured.Values)
        {
            var full = pair.Key.StartsWith(appId + "_", StringComparison.Ordinal) ? pair.Key : appId + "_" + pair.Key;
            if (values.TryGetValue(full, out var previous) && previous != pair.Value) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidRequest, pair.Key);
            values[full] = pair.Value;
        }
        var snapshot = ApplicationSnapshot.Evaluate(document, values, scripts);
        if (snapshot.Evaluation.ScriptFailures != 0) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.ScriptEvaluationFailed);
        if (!snapshot.IsValid || !snapshot.Evaluation.ObjectActivityComplete) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.IncompleteActivation);
        var expanded = snapshot.Evaluation.ExpandedRoot!;
        var elements = expanded.DescendantsAndSelf().Where(e => !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef")).Take(500001).ToArray();
        if (elements.Length > 500000) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
        DependencyGate(elements, additional);
        var effective = new Dictionary<string, string?>(StringComparer.Ordinal); long characters = 0;
        foreach (var pair in snapshot.Evaluation.Parameters)
        {
            if (!NativeCommunicationGuard.Safe(pair.Key) || pair.Value.Value?.Length > 65536
                || (characters += pair.Key.Length + (pair.Value.Value?.Length ?? 0)) > 16 * 1024 * 1024 || effective.Count >= 100000)
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
            effective.Add(pair.Key, pair.Value.Value);
        }
        var activation = NativeActivationProof.Create(catalog, application, snapshot, effective, additional);
        if (!activation.IsAvailable) throw new NativeCommunicationRejected(activation.Issues[0]);
        var structure = NativeStructuralActivationProof.CreateResolved(catalog, snapshot, activation);
        if (!structure.IsAvailable) throw new NativeCommunicationRejected(structure.Issues[0]);
        if (!inputs.Manifest.Binding.MatchesIdentity(project, device) || DocumentHash(inputs.Application, 128 * 1024 * 1024) != documentHash
            || (inputs.Master is null ? null : DocumentHash(inputs.Master, 16 * 1024 * 1024)) != masterHash)
            NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InputsChanged);
        NativeCommunicationInputs after;
        try { after = NativeCommunicationInputs.Capture(project, device); }
        catch (NativeCommunicationRejected) { NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InputsChanged); throw; }
        if (after.Fingerprint != captured.Fingerprint) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InputsChanged);
        return new(inputs, captured, catalog, application, snapshot, activation, structure, effective, additional, scripts is not null);
    }

    internal bool MatchesInputs(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs)
    {
        if (!IsAvailable || _sourceBinding is null || Snapshot is null || inputs.Application is null || !_sourceBinding.MatchesIdentity(project, device)
            || inputs.Manifest.Binding?.Fingerprint != SourceFingerprint || inputs.Manifest.ApplicationDefinitionHash != ApplicationDefinitionHash
            || inputs.Manifest.ResourceContext?.Fingerprint != ResourceContextFingerprint) return false;
        try
        {
            return DocumentHash(inputs.Application, 128 * 1024 * 1024) == _applicationDocumentHash
                && (inputs.Master is null ? null : DocumentHash(inputs.Master, 16 * 1024 * 1024)) == _masterDocumentHash
                && Catalog is not null && DocumentHash(Catalog.Nodes[0], 128 * 1024 * 1024) == _ownedSourceHash
                && NativeCommunicationInputs.Capture(project, device).Fingerprint == ProjectInputHash && SnapshotHash(Snapshot) == _snapshotHash;
        }
        catch (Exception failure) when (failure is NativeCommunicationRejected or XmlException or InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { return false; }
    }
    private static string SnapshotHash(ApplicationSnapshot snapshot) => NativeCommunicationGuard.Hash(new
    {
        expanded = DocumentHash(snapshot.Evaluation.ExpandedRoot!, 128 * 1024 * 1024),
        values = snapshot.Evaluation.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { p.Key, p.Value.Value }).ToArray(),
        objects = snapshot.Evaluation.ActiveObjectReferences.Order(StringComparer.Ordinal).ToArray(),
        modules = snapshot.ModuleInstances.Select(m => new { m.Id, m.RefId, m.RepeatIndex, m.Arguments }).ToArray(),
        snapshot.Evaluation.ScriptExecutions, snapshot.Evaluation.ScriptFailures, snapshot.Evaluation.Converged, snapshot.Evaluation.ObjectActivityComplete,
        snapshot.Evaluation.StructureFailed, snapshot.Evaluation.DepthLimitReached, snapshot.Evaluation.InvalidAssignments,
    });
    private static string DocumentHash(XNode document, int maximum)
    {
        var nodes = 0; long characters = 0; var stack = new Stack<(XNode Node, int Depth)>(); stack.Push((document, 0));
        while (stack.TryPop(out var entry))
        {
            if (++nodes > 1000000 || entry.Depth > 128) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
            if (entry.Node is XContainer container)
                foreach (var child in container.Nodes())
                { if (stack.Count >= 1000000) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded); stack.Push((child, entry.Depth + 1)); }
            if (entry.Node is XElement element)
            {
                characters += element.Name.ToString().Length;
                foreach (var attribute in element.Attributes()) characters += attribute.Name.ToString().Length + attribute.Value.Length;
            }
            else if (entry.Node is XText text) characters += text.Value.Length;
            else if (entry.Node is XComment comment) characters += comment.Value.Length;
            else if (entry.Node is XProcessingInstruction instruction) characters += instruction.Target.Length + instruction.Data.Length;
            if (characters > maximum) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
        }
        using var digest = SHA256.Create(); using var stream = new CryptoStream(Stream.Null, digest, CryptoStreamMode.Write);
        using (var writer = XmlWriter.Create(stream, new() { Encoding = new UTF8Encoding(false), CloseOutput = false, Indent = false })) document.WriteTo(writer);
        stream.FlushFinalBlock(); return Convert.ToHexStringLower(digest.Hash!);
    }
    private static void ProfileGate(XElement root, string id)
    {
        if (KnownDcaRequirementsRegistry.RequiresDca(id)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.ExternalConfigurationUnaccounted);
        foreach (var element in root.DescendantsAndSelf())
            if (element.Attributes().Any(a => a.Name.LocalName is "Broken" or "DownloadInfoIncomplete" or "RequiresExternalSoftware"
                    && !string.IsNullOrWhiteSpace(a.Value) && a.Value is not ("false" or "False" or "0"))
                || element.Name.LocalName is "DCA" or "Dca" or "Plugin" or "EtsUiPlugin" or "EtsDataHandler"
                || element.Name.LocalName == "Extension" && (element.Attributes().Any(a => a.Name.LocalName.StartsWith("Ets", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.Value))
                    || element.Elements().Any(c => c.Name.LocalName.StartsWith("Ets", StringComparison.Ordinal)))
                || element.Name.LocalName is "Parameter" or "ParameterRef" or "ParameterType" && !string.IsNullOrWhiteSpace((string?)element.Attribute("Plugin")))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.ExternalConfigurationUnaccounted);
    }
    private static bool Dynamic(XElement element) => element.Ancestors().Any(a => a.Name.LocalName == "Dynamic");
    private static string Id(XElement element) => (string?)element.Attribute("Id") ?? "";
    private static void DependencyGate(IReadOnlyList<XElement> elements, IReadOnlyList<string> additional)
    {
        var refs = elements.Where(e => e.Name.LocalName == "ParameterRef").ToArray();
        if (refs.Length > 100000) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
        var pairs = refs.GroupBy(Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var aliases = refs.GroupBy(e => (string?)e.Attribute("RefId") ?? "", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(Id).ToArray(), StringComparer.Ordinal);
        var watched = elements.Where(e => e.Name.LocalName == "choose" && Dynamic(e)).Attributes("ParamRefId").Select(a => a.Value).Concat(additional).ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<string>(watched); var seenParameters = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var id))
        {
            var matches = pairs.GetValueOrDefault(id);
            if (watched.Count > 100000 || matches is null || matches.Length != 1)
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin, id);
            var parameter = (string?)matches[0].Attribute("RefId") ?? "";
            if (seenParameters.Add(parameter))
                foreach (var alias in aliases.GetValueOrDefault(parameter) ?? []) if (watched.Add(alias)) pending.Enqueue(alias);
        }
        var bindings = 0; var calculations = 0;
        foreach (var element in elements)
        {
            if (element.Name.LocalName == "Assign")
            {
                var target = (string?)element.Attribute("TargetParamRefRef"); var source = (string?)element.Attribute("SourceParamRefRef");
                if (target is null || !pairs.ContainsKey(target) || source is not null && !pairs.ContainsKey(source)
                    || source is null && element.Attribute("Value") is null) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin);
                if (watched.Contains(target!) || source is not null && watched.Contains(source))
                    NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationDependency, target);
            }
            if (element.Name.LocalName != "ParameterCalculation") continue;
            if (++calculations > 2048) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
            foreach (var binding in element.Elements().Where(e => e.Name.LocalName is "LParameters" or "RParameters").Elements())
            {
                if (++bindings > 100000) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.LimitExceeded);
                var id = (string?)binding.Attribute("RefId");
                if (id is null || !pairs.ContainsKey(id)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin);
                if (watched.Contains(id!)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedActivationDependency, id);
            }
        }
    }
}
