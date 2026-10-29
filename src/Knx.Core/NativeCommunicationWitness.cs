using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;

namespace Knx;

internal enum NativeCommunicationPriority { Low, High, Alert }
internal sealed record NativeCommunicationSlot(ushort Number, bool IsPlaceholder, bool Active, uint SizeInBit,
    bool CommunicationEnabled, bool ReadEnabled, bool WriteEnabled, bool TransmitEnabled, bool UpdateEnabled,
    bool ReadOnInitEnabled, NativeCommunicationPriority Priority);
internal sealed record NativeCommunicationGroup(ushort Address, bool Acknowledge);
internal sealed record NativeCommunicationAssociation(ushort ObjectNumber, int GroupAddressIndex, bool Sending);

/// <summary>Privately minted native communication facts from one fresh, complete declared-activity evaluation.</summary>
internal sealed class NativeCommunicationWitness
{
    internal bool IsAvailable => Fingerprint is not null && Issues.Count == 0;
    internal IReadOnlyList<GeneratedTableIssue> Issues { get; }
    internal IReadOnlyList<NativeCommunicationSlot> Slots { get; }
    internal IReadOnlyList<NativeCommunicationGroup> Groups { get; }
    internal IReadOnlyList<NativeCommunicationAssociation> Associations { get; }
    internal string? ApplicationDefinitionHash { get; }
    internal string? SourceFingerprint { get; }
    internal string? ProjectInputHash { get; }
    internal string? Fingerprint { get; }
    internal bool RequiresScripts { get; }
    private NativeCommunicationWitness(GeneratedTableIssue issue)
    {
        Issues = Array.AsReadOnly(new[] { issue }); Slots = Array.AsReadOnly(Array.Empty<NativeCommunicationSlot>());
        Groups = Array.AsReadOnly(Array.Empty<NativeCommunicationGroup>()); Associations = Array.AsReadOnly(Array.Empty<NativeCommunicationAssociation>());
    }
    private NativeCommunicationWitness(string application, string source, string inputs, NativeCommunicationSlot[] slots,
        NativeCommunicationGroup[] groups, NativeCommunicationAssociation[] associations, bool requiredScripts, object proof)
    {
        ApplicationDefinitionHash = application; SourceFingerprint = source; ProjectInputHash = inputs; RequiresScripts = requiredScripts;
        Slots = Array.AsReadOnly(slots); Groups = Array.AsReadOnly(groups); Associations = Array.AsReadOnly(associations);
        Issues = Array.AsReadOnly(Array.Empty<GeneratedTableIssue>());
        Fingerprint = NativeCommunicationGuard.Hash(new { version = 1, application, source, inputs, slots, groups, associations, requiredScripts, proof });
    }

    internal static NativeCommunicationWitness Create(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        IParameterScriptRuntime? scripts = null)
        => CreateFromContext(project, device, inputs, NativeApplicationEvaluationContext.Create(project, device, inputs, scripts: scripts));

    internal static NativeCommunicationWitness CreateFromContext(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        NativeApplicationEvaluationContext context)
    {
        try
        {
            if (!context.IsAvailable) return new(context.Issues.FirstOrDefault() ?? new(GeneratedTableIssueCode.IncompleteActivation));
            if (!context.MatchesInputs(project, device, inputs)) return new(new(GeneratedTableIssueCode.InputsChanged));
            return Build(project, device, inputs, context);
        }
        catch (NativeCommunicationRejected failure) { return new(failure.Issue); }
        catch (Exception failure) when (failure is InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { return new(new(GeneratedTableIssueCode.InvalidApplication)); }
    }

    private sealed record Resolved(XElement Definition, XElement? Reference, NativeCommunicationSlot Slot, string Security, bool IoT);
    private sealed record Association(ushort ObjectNumber, string GroupId, ushort Address, bool Sending, bool Acknowledge);
    [DoesNotReturn]
    private static void Fail(GeneratedTableIssueCode code, string? id = null) => NativeCommunicationGuard.Fail(code, id);
    private static NativeCommunicationWitness Build(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs, NativeApplicationEvaluationContext context)
    {
        var capture = context.CapturedInputs!; var catalog = context.Catalog!; var application = context.OriginalApplication!;
        var snapshot = context.Snapshot!; var evaluation = snapshot.Evaluation; var expanded = evaluation.ExpandedRoot!;
        var appId = (string)application.Attribute("Id")!;
        var sourceObjects = catalog.Nodes.Where(e => e.Name.LocalName is "ComObject" or "ComObjectRef" or "ComObjectRefRef").Take(100001).ToArray();
        if (sourceObjects.Length > 100000) Fail(GeneratedTableIssueCode.LimitExceeded);
        foreach (var group in sourceObjects.Where(e => e.Name.LocalName is "ComObject" or "ComObjectRef").GroupBy(Id, StringComparer.Ordinal))
            if (!NativeCommunicationGuard.Safe(group.Key) || group.Count() != 1) Fail(GeneratedTableIssueCode.AmbiguousObjectIdentity, group.Key);

        var all = expanded.DescendantsAndSelf().Where(e => !InsideDefinition(e)).ToArray();
        var proof = new OriginProof(catalog, application, snapshot);
        var definitions = Index(all, "ComObject"); var references = Index(all, "ComObjectRef");
        var placements = all.Where(e => e.Name.LocalName == "ComObjectRefRef").ToArray();
        if ((long)definitions.Count + references.Count + placements.Length > 100000) Fail(GeneratedTableIssueCode.LimitExceeded);
        foreach (var definition in definitions.Values) proof.Validate(definition, "ComObjectTable");
        foreach (var reference in references.Values)
        {
            proof.Validate(reference, "ComObjectRefs");
            if (reference.Attribute("Number") is not null || reference.Attribute("BaseNumber") is not null) Fail(GeneratedTableIssueCode.InvalidObjectDeclaration, Id(reference));
            if (!definitions.ContainsKey((string?)reference.Attribute("RefId") ?? "")) Fail(GeneratedTableIssueCode.InvalidObjectDeclaration, Id(reference));
        }
        foreach (var placement in placements)
        {
            proof.Placement(placement);
            if (!references.ContainsKey((string?)placement.Attribute("RefId") ?? "")) Fail(GeneratedTableIssueCode.IncompleteActivation);
        }
        string Local(string id) => id.StartsWith(appId + "_", StringComparison.Ordinal) ? id[(appId.Length + 1)..] : id;
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in references.Keys)
            if (!aliases.TryAdd(Local(id), id)) Fail(GeneratedTableIssueCode.AmbiguousObjectIdentity, id);
        var saved = new Dictionary<string, NativeObjectInput>(StringComparer.Ordinal);
        foreach (var obj in capture.Objects)
        {
            if (!aliases.TryGetValue(Local(obj.Id), out var id)) Fail(GeneratedTableIssueCode.UnknownSavedObject, obj.Id);
            if (!saved.TryAdd(id!, obj)) Fail(GeneratedTableIssueCode.AmbiguousObjectIdentity, obj.Id);
        }
        var nativeOverrides = new Dictionary<string, NativeObjectOverride>(StringComparer.Ordinal);
        foreach (var pair in capture.Overrides)
            if (aliases.TryGetValue(Local(pair.Key), out var id) && saved.ContainsKey(id) && !nativeOverrides.TryAdd(id, pair.Value))
                Fail(GeneratedTableIssueCode.AmbiguousObjectIdentity, pair.Key);
        var slotMap = new SortedDictionary<ushort, Resolved>();
        foreach (var definition in definitions.Values)
        {
            var resolved = Resolve(definition, null, null, null, false);
            if (!slotMap.TryAdd(resolved.Slot.Number, resolved)) Fail(GeneratedTableIssueCode.AmbiguousObjectNumber, Id(definition));
        }
        var activeNumbers = new HashSet<ushort>();
        var associations = new List<Association>();
        foreach (var id in context.Activation!.ActiveObjectReferences.Order(StringComparer.Ordinal))
        {
            if (!references.TryGetValue(id, out var reference)) Fail(GeneratedTableIssueCode.IncompleteActivation, id);
            var definition = definitions[(string)reference!.Attribute("RefId")!];
            saved.TryGetValue(id, out var obj); nativeOverrides.TryGetValue(id, out var native);
            var resolved = Resolve(definition, reference, obj, native, true); var slot = resolved.Slot;
            if (!activeNumbers.Add(slot.Number)) Fail(GeneratedTableIssueCode.AmbiguousObjectNumber, id);
            slotMap[slot.Number] = resolved;
            if (obj is null) continue;
            if (obj.Number != slot.Number || Local(obj.DefinitionId) != Local(Id(definition)) && Local(obj.DefinitionId) != Local(id))
                Fail(GeneratedTableIssueCode.SavedNumberMismatch, obj.Id);
            var links = obj.Links;
            var linkSet = links.ToHashSet(StringComparer.Ordinal);
            var connectorByGroup = obj.Connectors.ToDictionary(c => c.Group, StringComparer.Ordinal);
            if (obj.Connectors.Any(c => c.Priority is not null || c.Slot is not null)) Fail(GeneratedTableIssueCode.UnsupportedConnectorMetadata, obj.Id);
            if (obj.Connectors.Any(c => !linkSet.Contains(c.Group))) Fail(GeneratedTableIssueCode.AmbiguousConnectorOverride, obj.Id);
            var senders = obj.Connectors.Where(c => c.Send).Select(c => c.Group).Concat(obj.Sender is null ? [] : new[] { obj.Sender }).Distinct(StringComparer.Ordinal).ToArray();
            if (senders.Length > 1) Fail(GeneratedTableIssueCode.MultipleSendingAssociations, obj.Id);
            if (senders.Length == 1 && !linkSet.Contains(senders[0])) Fail(GeneratedTableIssueCode.AmbiguousConnectorOverride, obj.Id);
            // A current explicit sending choice precedes the retained first-link convention.
            var sender = senders.Length == 1 ? senders[0] : links.FirstOrDefault();
            var acks = (native?.Acknowledges ?? Array.Empty<string>()).ToHashSet(StringComparer.Ordinal);
            if (acks.Any(a => !linkSet.Contains(a))) Fail(GeneratedTableIssueCode.AmbiguousConnectorOverride, obj.Id);
            foreach (var link in links)
            {
                if (associations.Count >= 65535) Fail(GeneratedTableIssueCode.LimitExceeded);
                if (!capture.Groups.TryGetValue(link, out var group)) Fail(GeneratedTableIssueCode.UnknownGroupAddress, link);
                var connector = connectorByGroup.GetValueOrDefault(link);
                var nativeAck = acks.Contains(link);
                if (connector is not null && (link == sender && !connector.Send && obj.Sender != sender || nativeAck && !connector.Acknowledge))
                    Fail(GeneratedTableIssueCode.AmbiguousConnectorOverride, obj.Id);
                if (group!.Secure || resolved.Security != "None") Fail(GeneratedTableIssueCode.SecureAssociationUnsupported, obj.Id);
                if (resolved.IoT || capture.Medium is not ("MT-0" or "MT-5")) Fail(GeneratedTableIssueCode.UnsupportedMedium, obj.Id);
                associations.Add(new(slot.Number, link, group.Address, link == sender, nativeAck || connector?.Acknowledge == true));
            }
        }
        foreach (var pair in saved)
        {
            if (!context.Activation!.ActiveObjectReferences.Contains(pair.Key) && pair.Value.Links.Count != 0) Fail(GeneratedTableIssueCode.InactiveSavedObjectHasLinks, pair.Value.Id);
            var definition = definitions[(string)references[pair.Key].Attribute("RefId")!];
            var expected = Number(definition);
            if (pair.Value.Number != expected) Fail(GeneratedTableIssueCode.SavedNumberMismatch, pair.Value.Id);
        }
        var groupRows = associations.GroupBy(a => a.Address).OrderBy(g => g.Key).ToArray();
        foreach (var group in groupRows)
            if (group.Select(a => a.GroupId).Distinct(StringComparer.Ordinal).Count() != 1) Fail(GeneratedTableIssueCode.AmbiguousGroupAddress);
        var groups = groupRows.Select(g => new NativeCommunicationGroup(g.Key, g.Any(a => a.Acknowledge))).ToArray();
        var groupIndices = groups.Select((g, index) => (g.Address, index)).ToDictionary(p => p.Address, p => p.index);
        var tableAssociations = associations.Select(a => new NativeCommunicationAssociation(a.ObjectNumber, groupIndices[a.Address], a.Sending)).ToArray();
        if (tableAssociations.Select(a => (a.ObjectNumber, a.GroupAddressIndex)).Distinct().Count() != tableAssociations.Length) Fail(GeneratedTableIssueCode.DuplicateAssociation);
        var slots = new NativeCommunicationSlot[slotMap.Count == 0 ? 0 : slotMap.Keys.Max() + 1];
        for (var i = 0; i < slots.Length; i++) slots[i] = slotMap.TryGetValue((ushort)i, out var slot) ? slot.Slot
            : new((ushort)i, true, false, 0, false, false, false, false, false, false, NativeCommunicationPriority.Low);
        if (!context.MatchesInputs(project, device, inputs)) Fail(GeneratedTableIssueCode.InputsChanged);
        return new(inputs.Manifest.ApplicationDefinitionHash!, inputs.Manifest.Binding!.Fingerprint, capture.Fingerprint, slots, groups, tableAssociations,
            context.RequiresScripts, new
            {
                scriptCapability = context.ScriptCapabilityUsed, evaluation.ScriptExecutions,
                activation = context.Activation!.Fingerprint, structure = context.Structure!.Fingerprint, context = context.Fingerprint,
                activeReferences = context.Activation!.ActiveObjectReferences.Order(StringComparer.Ordinal).ToArray(),
                modules = snapshot.ModuleInstances.Select(m => new { m.Id, m.RefId, m.RepeatIndex }).ToArray(),
                origins = definitions.Values.Concat(references.Values).Concat(placements).Select(e => e.Annotation<NativeCommunicationElementOrigin>()).ToArray(),
                controllers = context.Activation.Controllers.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray(),
            });
    }

    private static Resolved Resolve(XElement definition, XElement? reference, NativeObjectInput? saved, NativeObjectOverride? instance, bool active)
    {
        string? Pick(string name) => name switch
        {
            "Priority" => instance?.Priority ?? (string?)reference?.Attribute(name) ?? (string?)definition.Attribute(name) ?? "Low",
            "CommunicationFlag" => instance?.Communication ?? (string?)reference?.Attribute(name) ?? (string?)definition.Attribute(name),
            "ReadFlag" => instance?.Read ?? (string?)reference?.Attribute(name) ?? (string?)definition.Attribute(name),
            "WriteFlag" => instance?.Write ?? (string?)reference?.Attribute(name) ?? (string?)definition.Attribute(name),
            "TransmitFlag" => instance?.Transmit ?? (string?)reference?.Attribute(name) ?? (string?)definition.Attribute(name),
            "UpdateFlag" => instance?.Update ?? (string?)reference?.Attribute(name) ?? (string?)definition.Attribute(name),
            "ReadOnInitFlag" => instance?.ReadOnInit ?? (string?)reference?.Attribute(name) ?? (string?)definition.Attribute(name),
            _ => (string?)reference?.Attribute(name) ?? (string?)definition.Attribute(name),
        };
        bool Flag(string name) => Pick(name) switch
        { "Enabled" => true, "Disabled" => false, _ => throw new NativeCommunicationRejected(new(GeneratedTableIssueCode.InvalidObjectDeclaration, Id(reference ?? definition))) };
        var sizeText = Pick("ObjectSize"); var size = Ksxml.ParseObjectSize(sizeText);
        var canonicalSize = size is >= 1 and <= 7 ? $"{size} Bit" : size == 8 ? "1 Byte" : size is >= 16 and <= 400 && size % 8 == 0 ? $"{size / 8} Bytes" : null;
        if (canonicalSize is null || sizeText != canonicalSize)
            NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnsupportedObjectSize, Id(reference ?? definition));
        var priority = Pick("Priority") switch
        {
            "Low" => NativeCommunicationPriority.Low, "High" => NativeCommunicationPriority.High, "Alert" => NativeCommunicationPriority.Alert,
            _ => throw new NativeCommunicationRejected(new(GeneratedTableIssueCode.UnsupportedObjectPriority, Id(reference ?? definition))),
        };
        var slot = new NativeCommunicationSlot(Number(definition), false, active, (uint)size, Flag("CommunicationFlag"), Flag("ReadFlag"),
            Flag("WriteFlag"), Flag("TransmitFlag"), Flag("UpdateFlag"), Flag("ReadOnInitFlag"), priority);
        var canonicalFlags = (slot.CommunicationEnabled ? "C" : "") + (slot.ReadEnabled ? "R" : "") + (slot.WriteEnabled ? "W" : "")
            + (slot.TransmitEnabled ? "T" : "") + (slot.UpdateEnabled ? "U" : "") + (slot.ReadOnInitEnabled ? "I" : "");
        if (saved is not null && (saved.Flags is not null && saved.Flags != canonicalFlags || saved.Bits is { } bits && bits != size))
            NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidNativeOverride, saved.Id);
        return new(definition, reference, slot, Pick("SecurityRequired") ?? "None", !string.IsNullOrEmpty(Pick("IoTPointReference")));
    }
    private static ushort Number(XElement definition) => ushort.TryParse((string?)definition.Attribute("Number"), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        ? value : throw new NativeCommunicationRejected(new(GeneratedTableIssueCode.InvalidObjectDeclaration, Id(definition)));
    private static string Id(XElement element) => (string?)element.Attribute("Id") ?? "";
    private static bool InsideDefinition(XElement element) => element.Ancestors().Any(a => a.Name.LocalName == "ModuleDef");
    private static Dictionary<string, XElement> Index(XElement[] all, string name)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in all.Where(e => e.Name.LocalName == name))
            if (!NativeCommunicationGuard.Safe(Id(element)) || !result.TryAdd(Id(element), element)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.AmbiguousObjectIdentity, Id(element));
        return result;
    }
    private static bool NativePath(XElement element, string container, XElement owner) => element.Parent?.Name == owner.Name.Namespace + container
        && element.Parent.Parent?.Name == owner.Name.Namespace + "Static" && ReferenceEquals(element.Parent.Parent.Parent, owner)
        && owner.Elements(owner.Name.Namespace + "Static").Count() == 1
        && element.Parent.Parent.Elements(owner.Name.Namespace + container).Count() == 1;
    internal sealed class OriginProof
    {
        private readonly NativeCommunicationOriginCatalog _catalog;
        private readonly XElement _application;
        private readonly Dictionary<(string Kind, string Id), int> _identities;
        private readonly Dictionary<(string Id, string RefId, string Repeat), int> _instances;
        private readonly HashSet<NativeCommunicationModuleFrame> _verifiedFrames = [];
        private readonly bool _admitRootUnions;
        internal OriginProof(NativeCommunicationOriginCatalog catalog, XElement application, ApplicationSnapshot snapshot, bool admitRootUnions = false)
        {
            _admitRootUnions = admitRootUnions;
            _catalog = catalog; _application = application;
            _identities = catalog.Nodes.Where(e => e.Name.LocalName is "Module" or "ModuleDef" or "ParameterType" or "Parameter" or "ParameterRef" or "ComObject" or "ComObjectRef")
                .GroupBy(e => (e.Name.LocalName, Id(e))).ToDictionary(g => g.Key, g => g.Count());
            _instances = snapshot.ModuleInstances.GroupBy(m => (m.Id, m.RefId, m.RepeatIndex)).ToDictionary(g => g.Key, g => g.Count());
        }
        private bool Definition(XElement definition)
        {
            var ns = _application.Name.Namespace;
            for (var depth = 0; depth < 8; depth++)
            {
                if (definition.Name != ns + "ModuleDef" || !Unique(definition)) return false;
                if (definition.Parent?.Name == ns + "ModuleDefs" && ReferenceEquals(definition.Parent.Parent, _application))
                    return _application.Elements(ns + "ModuleDefs").Count() == 1;
                if (definition.Parent?.Name != ns + "SubModuleDefs" || definition.Parent.Parent is not { } parent
                    || parent.Name != ns + "ModuleDef" || parent.Elements(ns + "SubModuleDefs").Count() != 1) return false;
                definition = parent;
            }
            return false;
        }
        private bool Unique(XElement element) => NativeCommunicationGuard.Safe(Id(element))
            && _identities.GetValueOrDefault((element.Name.LocalName, Id(element))) == 1;
        private XElement? DynamicOwner(XElement element, bool expanded = false)
        {
            var ns = _application.Name.Namespace; var depth = 0;
            foreach (var parent in element.Ancestors())
            {
                if (++depth > 128 || parent.Name.Namespace != ns) return null;
                if (parent.Name == ns + "Dynamic")
                {
                    var owner = parent.Parent;
                    return owner is not null && owner.Elements(ns + "Dynamic").Count() == 1
                        && (expanded ? owner.Name == _application.Name && Id(owner) == Id(_application)
                            : ReferenceEquals(owner, _application) || Definition(owner)) ? owner : null;
                }
                if (parent.Name.LocalName is not ("Channel" or "ChannelIndependentBlock" or "ParameterBlock" or "choose" or "when" or "Repeat")) return null;
            }
            return null;
        }
        private void Frames(NativeCommunicationElementOrigin origin, XElement owner)
        {
            if (origin.Frames.Count == 0)
            {
                if (!ReferenceEquals(owner, _application)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownObjectOrigin);
                return;
            }
            if (origin.Frames.Count > 8 || !Definition(owner) || origin.Frames[^1].DefinitionKey != owner.Annotation<NativeCommunicationSourceNode>()?.Key)
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownObjectOrigin);
            for (var i = 0; i < origin.Frames.Count; i++)
            {
                var frame = origin.Frames[i];
                if (_verifiedFrames.Contains(frame)) continue;
                var call = _catalog.At(frame.CallKey); var definition = _catalog.At(frame.DefinitionKey);
                var callOwner = i == 0 ? _application : _catalog.At(origin.Frames[i - 1].DefinitionKey);
                if (call is null || definition is null || call.Name != _application.Name.Namespace + "Module" || !Unique(call)
                    || !Definition(definition) || !ReferenceEquals(DynamicOwner(call), callOwner)
                    || (string?)call.Attribute("RefId") != Id(definition)
                    || _catalog.Hash(frame.CallKey) != frame.CallHash || _catalog.Hash(frame.DefinitionKey) != frame.DefinitionHash
                    || _instances.GetValueOrDefault((frame.NativeInstanceId, frame.NativeReferenceId, frame.RepeatIndex)) != 1)
                    NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownObjectOrigin);
                var definitionId = Id(definition);
                for (var prior = 0; prior < i; prior++)
                    definitionId = ApplicationModules.ScopeId(origin.Frames[prior].DefinitionId, origin.Frames[prior].InstanceId, definitionId);
                if (definitionId != frame.DefinitionId || !NativeCommunicationGuard.Safe(frame.InstanceId))
                    NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownObjectOrigin);
                _verifiedFrames.Add(frame);
            }
        }
        private XElement Original(XElement element)
        {
            var origin = element.Annotation<NativeCommunicationElementOrigin>();
            var original = origin is null ? null : _catalog.At(origin.Key);
            if (origin is not { Complete: true } || original is null || original.Name != element.Name
                || original.Name.Namespace != _application.Name.Namespace || _catalog.Hash(origin.Key) != origin.OriginalHash
                || Id(original) != origin.OriginalId || Id(element) != origin.ExpandedId)
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownObjectOrigin, Id(element));
            return original;
        }
        internal void Validate(XElement element, string container)
        {
            var original = Original(element); var origin = element.Annotation<NativeCommunicationElementOrigin>()!;
            var owner = original.Parent?.Parent?.Parent;
            if (_admitRootUnions && element.Name.LocalName == "Parameter" && original.Parent?.Name == _application.Name.Namespace + "Union")
                owner = original.Parent.Parent?.Parent?.Parent;
            if (element.Name.LocalName == "ComObject" && owner?.Name == _application.Name.Namespace + "ModuleDef") container = "ComObjects";
            var unionPath = _admitRootUnions && element.Name.LocalName == "Parameter" && container == "Parameters"
                && original.Parent?.Name == _application.Name.Namespace + "Union" && ReferenceEquals(owner, _application)
                && NativePath(original.Parent, container, owner);
            if (!Unique(original) || owner is null || !(unionPath || NativePath(original, container, owner)))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidObjectDeclaration, Id(element));
            if (element.Name.LocalName == "ParameterType" && !ReferenceEquals(owner, _application))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidObjectDeclaration, Id(element));
            if (element.Name.LocalName is "ComObject" or "ComObjectRef" && original.HasElements)
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidObjectDeclaration, Id(element));
            Frames(origin, owner);
            if (origin.Frames.Count == 0 && !XNode.DeepEquals(original, element))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownObjectOrigin, Id(element));
            var expectedId = Id(original); var expectedTarget = (string?)original.Attribute("RefId");
            foreach (var frame in origin.Frames)
            {
                expectedId = ApplicationModules.ScopeId(frame.DefinitionId, frame.InstanceId, expectedId);
                if (expectedTarget is not null) expectedTarget = ApplicationModules.ScopeId(frame.DefinitionId, frame.InstanceId, expectedTarget);
            }
            if (expectedId != Id(element) || expectedTarget != (string?)element.Attribute("RefId")
                || original.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.Namespace != XNamespace.None && a.Name.Namespace != XNamespace.Xml))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownObjectOrigin, Id(element));
            if (element.Name.LocalName == "ParameterType")
            {
                var expected = new XElement(original);
                foreach (var attribute in expected.DescendantsAndSelf().Attributes("Id"))
                    foreach (var frame in origin.Frames) attribute.Value = ApplicationModules.ScopeId(frame.DefinitionId, frame.InstanceId, attribute.Value);
                if (!XNode.DeepEquals(expected, element)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.IncompleteActivation, Id(element));
            }
            if (element.Name.LocalName == "ComObject")
            {
                if (!uint.TryParse((string?)original.Attribute("Number"), NumberStyles.None, CultureInfo.InvariantCulture, out var literal))
                    NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidObjectDeclaration, Id(element));
                if (checked((long)literal + (origin.NumberBase ?? 0)) != Number(element))
                    NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownObjectOrigin, Id(element));
                var baseId = (string?)original.Attribute("BaseNumber");
                foreach (var prior in origin.Frames.Take(Math.Max(0, origin.Frames.Count - 1)))
                    if (baseId is not null) baseId = ApplicationModules.ScopeId(prior.DefinitionId, prior.InstanceId, baseId);
                if (origin.Frames.Count == 0 ? baseId is not null : baseId != origin.NumberBaseId
                    || baseId is not null && !origin.Frames[^1].Arguments.Any(a => a.Id == baseId && a.Value == origin.NumberBase))
                    NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownObjectOrigin, Id(element));
            }
            if (element.Name.LocalName == "ComObjectRef" && (original.Attribute("Number") is not null || original.Attribute("BaseNumber") is not null))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidObjectDeclaration, Id(element));
            foreach (var name in new[] { "ObjectSize", "Priority", "ReadFlag", "WriteFlag", "CommunicationFlag", "TransmitFlag", "UpdateFlag", "ReadOnInitFlag", "SecurityRequired", "IoTPointReference" })
                if ((string?)original.Attribute(name) != (string?)element.Attribute(name)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownObjectOrigin, Id(element));
        }
        internal void Placement(XElement element)
        {
            var original = Original(element); var origin = element.Annotation<NativeCommunicationElementOrigin>()!;
            var owner = DynamicOwner(original);
            if (owner is null || element.HasElements || original.HasElements
                || element.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name != "RefId")
                || original.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name != "RefId"))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.InvalidObjectDeclaration);
            Frames(origin, owner);
            var expected = (string?)original.Attribute("RefId");
            foreach (var frame in origin.Frames)
                if (expected is not null) expected = ApplicationModules.ScopeId(frame.DefinitionId, frame.InstanceId, expected);
            var expandedOwner = DynamicOwner(element, expanded: true);
            if (expected is null || expected != (string?)element.Attribute("RefId") || expandedOwner?.Name != _application.Name
                || Id(expandedOwner!) != Id(_application)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.IncompleteActivation);
        }
        internal XElement State(XElement element)
        {
            var original = Original(element); var origin = element.Annotation<NativeCommunicationElementOrigin>()!;
            var owner = DynamicOwner(original);
            if (owner is null || DynamicOwner(element, expanded: true) is not { } expandedOwner
                || expandedOwner.Name != _application.Name || Id(expandedOwner) != Id(_application))
                NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin);
            Frames(origin, owner);
            foreach (var name in new[] { "RefId", "ParamRefId", "TargetParamRefRef", "SourceParamRefRef" })
            {
                var expected = (string?)original.Attribute(name);
                foreach (var frame in origin.Frames)
                    if (expected is not null) expected = ApplicationModules.ScopeId(frame.DefinitionId, frame.InstanceId, expected);
                if (expected != (string?)element.Attribute(name)) NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin);
            }
            foreach (var name in new[] { "test", "default", "Value" })
                if ((string?)original.Attribute(name) != (string?)element.Attribute(name))
                    NativeCommunicationGuard.Fail(GeneratedTableIssueCode.UnknownParameterOrigin);
            return original;
        }
    }

}
