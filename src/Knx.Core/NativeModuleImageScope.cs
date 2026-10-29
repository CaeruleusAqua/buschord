using System.Xml.Linq;

namespace Knx;

internal sealed record NativeModuleImageMember(string InstanceId, string NativeInstanceId, string CallId,
    string DefinitionId, string OriginalParameterId, string ParameterId, string? OriginalReferenceId, string? ReferenceId,
    int SourceKey, string DeclarationHash);
internal sealed record NativeModuleImageAllocation(string InstanceId, string ArgumentId, string AllocatorId,
    long Value, long Allocates, long Alignment, long PoolStart, long PoolMaximumInclusive, string Usage);

/// <summary>Complete flat literal module declarations paired with original structural occurrences.</summary>
internal sealed class NativeModuleImageScope
{
    private const long MaximumValidationWork=5000000;
    private readonly NativeStructuralActivationProof.SourcePlan _plan;
    private readonly NativeCommunicationOriginCatalog _catalog;
    private readonly string _expandedDeclarationsHash;
    internal IReadOnlyList<XElement> Parameters { get; }
    internal IReadOnlyList<XElement> References { get; }
    internal IReadOnlySet<XElement> SourceDeclarations { get; }
    internal IReadOnlyList<NativeModuleImageMember> Members { get; }
    internal IReadOnlyList<ApplicationModuleInstance> Instances { get; }
    internal IReadOnlyList<NativeModuleImageAllocation> Allocations { get; }
    internal IReadOnlyList<NativeRootOwner> ReservationOwners { get; private set; } = Array.Empty<NativeRootOwner>();
    internal string Fingerprint { get; }
    internal bool IsConditional => _plan.ConditionalWhenKeys.Count != 0;
    private readonly Dictionary<(string Instance, string Reference), string> _references = new();
    private readonly Dictionary<string,string> _referenceInstances = new(StringComparer.Ordinal);

    internal static NativeModuleImageScope? Read(XElement root)
        => root.DescendantsAndSelf().Any(e => e.Name.LocalName is "Module" or "ModuleDef") ? new(root) : null;
    private NativeModuleImageScope(XElement root)
    {
        var ns = root.Name.Namespace;
        var catalog = _catalog = new NativeCommunicationOriginCatalog(root, true);
        _plan = catalog.StructuralPlan;
        if (_plan.Expected.Count > 256)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource);
        if (IsConditional && catalog.Nodes.Any(n=>n.Name.LocalName is "Repeat" or "Allocator" || n.Attribute("AllocatorRefId") is not null
            || n.Name.LocalName=="Parameter" && n.Ancestors().Any(a=>a.Name.LocalName=="ModuleDef") && (string?)n.Attribute("LegacyPatchAlways") is not (null or "false" or "0")))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource);
        var definitions = catalog.Nodes.Where(e => e.Name == ns + "ModuleDef").ToArray();
        var definitionsContainers=_plan.Application.Elements(ns+"ModuleDefs").ToArray();
        if(definitionsContainers.Length!=1||definitionsContainers[0].Attributes().Any(a=>!a.IsNamespaceDeclaration)
            ||definitionsContainers[0].Elements().Any(e=>e.Name!=ns+"ModuleDef")
            ||definitionsContainers[0].Nodes().OfType<XText>().Any(t=>!string.IsNullOrWhiteSpace(t.Value)))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource);
        var used = catalog.Nodes.Where(n=>n.Name==ns+"Module").Select(n=>(string?)n.Attribute("RefId")).ToHashSet(StringComparer.Ordinal);
        if (definitions.Length != used.Count || definitions.Any(d => !used.Contains((string?)d.Attribute("Id"))))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource);
        long plannedNodes=catalog.Nodes.Count,plannedParameters=0,plannedReferences=0,plannedAllocations=0;
        foreach(var occurrence in _plan.Expected)
        {
            var definition=catalog.At(occurrence.Frame!.DefinitionKey)!;
            plannedNodes=checked(plannedNodes+definition.Descendants().LongCount()+32);
            plannedParameters=checked(plannedParameters+definition.Descendants(ns+"Parameter").LongCount());
            plannedReferences=checked(plannedReferences+definition.Descendants(ns+"ParameterRef").LongCount());
            plannedAllocations+=catalog.At(occurrence.Frame.CallKey)!.Elements(ns+"NumericArg").LongCount(a=>a.Attribute("AllocatorRefId") is not null);
            if(plannedNodes>500000||plannedParameters>100000||plannedReferences>100000)
                NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
        }
        // Storage matching and reservation claims each inspect the complete physical population.
        if(plannedAllocations*plannedParameters*2>MaximumValidationWork)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
        var known = new HashSet<XElement>();
        foreach (var definition in definitions)
        {
            if(definition.Elements().Any(e=>e.Name.LocalName is not ("Arguments" or "Static" or "Dynamic")))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource);
            var statics = definition.Elements(ns + "Static").ToArray();
            if (statics.Length != 1 || statics[0].Elements().Any(e => e.Name.LocalName is not ("Parameters" or "ParameterRefs" or "ComObjects" or "ComObjectRefs")))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource);
            if(statics[0].Attributes().Any(a=>!a.IsNamespaceDeclaration)||statics[0].Nodes().OfType<XText>().Any(t=>!string.IsNullOrWhiteSpace(t.Value))
                ||statics[0].Elements().Any(c=>c.Attributes().Any(a=>!a.IsNamespaceDeclaration)||c.Nodes().OfType<XText>().Any(t=>!string.IsNullOrWhiteSpace(t.Value))))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource);
            foreach (var parameter in statics[0].Elements(ns + "Parameters").SelectMany(e => e.Elements()))
            {
                if (parameter.Name != ns + "Parameter" || parameter.Attribute("BaseValue") is not null
                    || parameter.Elements().Count() > 1 || parameter.Elements().Any(e => e.Name != ns + "Memory"))
                    NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource);
                known.Add(parameter); foreach (var leaf in parameter.Elements()) known.Add(leaf);
            }
            foreach (var reference in statics[0].Elements(ns + "ParameterRefs").SelectMany(e => e.Elements()))
            { if (reference.Name != ns + "ParameterRef") NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedSource); known.Add(reference); }
        }
        if (_plan.ImageModuleProfile) known.UnionWith(catalog.Nodes.Where(n=>n.Name.LocalName is "Repeat" or "Allocator" or "NumericArg"));
        SourceDeclarations = known;
        var clone = new XElement(root); catalog.Stamp(clone); clone.AddAnnotation(catalog);
        var expanded = IsConditional ? ApplicationModules.ExpandPotentialImageGeometry(clone, new ModuleOriginCatalog(root))
            : ApplicationModules.Expand(clone, null, origins: new ModuleOriginCatalog(root));
        var manifest = expanded.Annotation<NativeCommunicationOccurrenceManifest>();
        if (manifest is null || !manifest.BelongsTo(catalog) || !OccurrencesMatch(manifest.Occurrences))
            NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
        Instances = Array.AsReadOnly(ApplicationModules.ReadInstances(expanded).ToArray());
        var allocations = new List<NativeModuleImageAllocation>();
        var poolsById = catalog.Nodes.Where(n=>n.Name==ns+"Allocator").ToDictionary(Id,StringComparer.Ordinal);
        foreach (var occurrence in _plan.Expected)
        {
            var frame = occurrence.Frame!; var call = catalog.At(frame.CallKey)!; var definition = catalog.At(frame.DefinitionKey)!;
            foreach (var argument in call.Elements(ns+"NumericArg").Where(a=>a.Attribute("AllocatorRefId") is not null))
            {
                var id = (string)argument.Attribute("RefId")!; var allocatorId = (string)argument.Attribute("AllocatorRefId")!;
                var declaration = definition.Elements(ns+"Arguments").Elements(ns+"Argument").Single(a=>(string?)a.Attribute("Id")==id);
                var pool = poolsById[allocatorId];
                var memory = definition.Descendants(ns+"Memory").Any(m=>(string?)m.Attribute("BaseOffset")==id);
                var objects = definition.Descendants(ns+"ComObject").Any(o=>(string?)o.Attribute("BaseNumber")==id);
                if (!memory || objects) NativeGeneralHash.Fail(NativeGeneralIssueCode.UnsupportedMapping, id);
                allocations.Add(new(frame.InstanceId,id,allocatorId,frame.Arguments.Single(a=>a.Id==id).Value,
                    (long)declaration.Attribute("Allocates")!, (long?)declaration.Attribute("Alignment")??1,
                    (long)pool.Attribute("Start")!,(long)pool.Attribute("maxInclusive")!,memory?"MemoryByteOffset":"ObjectNumber"));
            }
        }
        Allocations = Array.AsReadOnly(allocations.ToArray());
        var parameters = new List<XElement>(); var references = new List<XElement>(); var members = new List<NativeModuleImageMember>();
        foreach (var node in expanded.DescendantsAndSelf())
        {
            if (node.Annotation<NativeCommunicationElementOrigin>() is not { Frames.Count: 1 } origin) continue;
            if (node.Name.LocalName is not ("Parameter" or "ParameterRef")) continue;
            var frame = origin.Frames[0]; var source = catalog.At(origin.Key);
            if (!origin.Complete || origin.ExpandedId != (string?)node.Attribute("Id") || source is null
                || !known.Contains(source)) NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
            var instance = Instances.SingleOrDefault(i => i.Id == frame.NativeInstanceId);
            if (instance is null) NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
            if (node.Name.LocalName == "Parameter") parameters.Add(node); else references.Add(node);
            members.Add(new(frame.InstanceId, frame.NativeInstanceId, (string?)catalog.At(frame.CallKey)?.Attribute("Id") ?? "",
                frame.DefinitionId, node.Name.LocalName == "Parameter" ? origin.OriginalId : (string?)source!.Attribute("RefId") ?? "",
                node.Name.LocalName == "Parameter" ? origin.ExpandedId : (string?)node.Attribute("RefId") ?? "",
                node.Name.LocalName == "ParameterRef" ? origin.OriginalId : null, node.Name.LocalName == "ParameterRef" ? origin.ExpandedId : null,
                origin.Key, NativeGeneralHash.Document(node)));
            if (node.Name.LocalName == "ParameterRef" && !_references.TryAdd((frame.InstanceId, origin.OriginalId), origin.ExpandedId))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership);
            if (node.Name.LocalName == "ParameterRef" && !_referenceInstances.TryAdd(origin.ExpandedId, frame.InstanceId))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership);
        }
        if (parameters.Count > 100000 || references.Count > 100000 || parameters.Select(Id).Distinct(StringComparer.Ordinal).Count() != parameters.Count
            || references.Select(Id).Distinct(StringComparer.Ordinal).Count() != references.Count)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
        Parameters = Array.AsReadOnly(parameters.ToArray()); References = Array.AsReadOnly(references.ToArray()); Members = Array.AsReadOnly(members.ToArray());
        _expandedDeclarationsHash = DeclarationsHash(expanded);
        Fingerprint = NativeGeneralHash.Of(new { profile = IsConditional ? "ConditionalDirectMemoryModules1" : _plan.ImageModuleProfile ? "LiteralRepeatRootAllocatorModuleImage1" : "FlatLiteralMemoryModules1", source = _plan.SourceHash,
            occurrences = _plan.Expected, Instances, Members, Allocations, expanded = _expandedDeclarationsHash });
    }
    private static string Id(XElement node) => (string?)node.Attribute("Id") ?? "";
    private static string DeclarationsHash(XElement root) => NativeGeneralHash.Of(root.DescendantsAndSelf()
        .Where(e => e.Name.LocalName is "Parameter" or "ParameterRef" && e.Annotation<NativeCommunicationElementOrigin>() is { Frames.Count: 1 })
        .Select(e => new { id = Id(e), declaration = NativeGeneralHash.Document(e) }).ToArray());
    private bool OccurrencesMatch(IReadOnlyList<NativeCommunicationOccurrence> actual)
        => actual.Count == _plan.Expected.Count && actual.Select((item, i) => item.Frame is not null
            && NativeGeneralHash.Of(item.Frame) == NativeGeneralHash.Of(_plan.Expected[i].Frame)
            && item.Arguments.SequenceEqual(_plan.Expected[i].Arguments)).All(match => match);
    internal bool Matches(NativeApplicationEvaluationContext context)
        => context.Snapshot?.Evaluation.ExpandedRoot is { } expanded && context.Catalog is { } catalog
            && catalog.CurrentHash(0) == _plan.SourceHash
            && expanded.Annotation<NativeCommunicationOccurrenceManifest>() is { } manifest && manifest.BelongsTo(catalog)
            && (IsConditional ? MatchesSelected(context, manifest, expanded) : OccurrencesMatch(manifest.Occurrences)
                && NativeGeneralHash.Of(context.Snapshot.ModuleInstances) == NativeGeneralHash.Of(Instances)
                && DeclarationsHash(expanded) == _expandedDeclarationsHash);
    private bool MatchesSelected(NativeApplicationEvaluationContext context, NativeCommunicationOccurrenceManifest manifest, XElement expanded)
    {
        if(context.Structure is not {IsAvailable:true} structure || structure.Occurrences.Count!=manifest.Occurrences.Count
            || !structure.Occurrences.SequenceEqual(manifest.Occurrences))return false;
        var expected=_plan.Expected.ToDictionary(o=>o.Frame!.InstanceId,StringComparer.Ordinal);var selected=new HashSet<string>(StringComparer.Ordinal);
        foreach(var actual in manifest.Occurrences)
            if(actual.Frame is null || !selected.Add(actual.Frame.InstanceId) || !expected.TryGetValue(actual.Frame.InstanceId,out var candidate)
                || NativeGeneralHash.Of(actual)!=NativeGeneralHash.Of(candidate))return false;
        var declared=Members.ToDictionary(m=>(m.InstanceId,m.SourceKey,m.ReferenceId is not null));
        var actualMembers=expanded.DescendantsAndSelf().Where(n=>n.Name.LocalName is "Parameter" or "ParameterRef"
            && n.Annotation<NativeCommunicationElementOrigin>() is {Frames.Count:1}).ToArray();
        if(actualMembers.Length!=Members.Count(m=>selected.Contains(m.InstanceId)))return false;
        foreach(var node in actualMembers)
        {
            var origin=node.Annotation<NativeCommunicationElementOrigin>()!;
            if(!declared.TryGetValue((origin.Frames[0].InstanceId,origin.Key,node.Name.LocalName=="ParameterRef"),out var member)
                || member.DeclarationHash!=NativeGeneralHash.Document(node))return false;
        }
        return true;
    }
    internal bool IsUninstantiatedReference(string reference, IReadOnlySet<string> selected)
        => IsConditional && _referenceInstances.TryGetValue(reference,out var instance)
            && !selected.Contains(instance);
    internal string ResolveReference(string instanceId, string originalReference)
        => _references.TryGetValue((instanceId, originalReference), out var id) ? id
            : throw new NativeGeneralRejected(new(NativeGeneralIssueCode.ContextMismatch, originalReference));
    internal void ValidateStorage(XElement parameter, ParameterStorageLocation location)
    {
        if (parameter.Annotation<NativeCommunicationElementOrigin>() is not {Frames.Count:1} origin) return;
        var source = _catalog.At(origin.Key)!; var memory = source.Element(source.Name.Namespace+"Memory");
        if (memory?.Attribute("BaseOffset") is not {} basis) return;
        var allocation = Allocations.SingleOrDefault(a=>a.InstanceId==origin.Frames[0].InstanceId&&a.ArgumentId==basis.Value);
        if (allocation is null) return;
        var offset=(long?)memory.Attribute("Offset")??0; var bit=(long?)memory.Attribute("BitOffset")??0;
        if (allocation.Usage!="MemoryByteOffset"||location.Footprint?.BitLength is not {} width
            ||checked(offset+(bit+width+7)/8)>allocation.Allocates)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.MissingAllocation,(string?)parameter.Attribute("Id"));
    }
    internal void ValidateAllocationRanges(IReadOnlyList<NativeRootOwner> owners)
    {
        var byParameter = owners.ToDictionary(o=>o.ParameterId,StringComparer.Ordinal);
        var ranges = new List<(NativeImageTarget Target,ulong Start,ulong End,HashSet<string> Members)>();
        foreach (var allocation in Allocations)
        {
            var claims = new Dictionary<(NativeImageTarget Target,ulong Start),HashSet<string>>();
            foreach (var parameter in Parameters)
            {
                var origin = parameter.Annotation<NativeCommunicationElementOrigin>()!;
                var source = _catalog.At(origin.Key)!; var memory = source.Element(source.Name.Namespace+"Memory");
                if(origin.Frames[0].InstanceId!=allocation.InstanceId || (string?)memory?.Attribute("BaseOffset")!=allocation.ArgumentId)continue;
                var id=(string)parameter.Attribute("Id")!;
                if(!byParameter.TryGetValue(id,out var owner))NativeGeneralHash.Fail(NativeGeneralIssueCode.MissingAllocation,id);
                var offset=(ulong?)memory!.Attribute("Offset")??0;var start=checked((ulong)owner!.ByteOffset-offset);
                var key=(owner.Target,start);
                if(!claims.TryGetValue(key,out var members)){members=new(StringComparer.Ordinal);claims.Add(key,members);}members.Add(id);
            }
            if(claims.Count==0)NativeGeneralHash.Fail(NativeGeneralIssueCode.MissingAllocation,allocation.ArgumentId);
            foreach(var claim in claims)ranges.Add((claim.Key.Target,claim.Key.Start,checked(claim.Key.Start+(ulong)allocation.Allocates),claim.Value));
        }
        if((long)Allocations.Count*Parameters.Count*2+(long)ranges.Count*owners.Count>MaximumValidationWork)
            NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
        foreach(var group in ranges.GroupBy(r=>r.Target))
        {
            ulong previousEnd=0;var first=true;
            foreach(var range in group.OrderBy(r=>r.Start))
            {
                if(!first&&range.Start<previousEnd)NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership);
                previousEnd=range.End;first=false;
                foreach(var owner in owners.Where(o=>o.Target==range.Target&&!range.Members.Contains(o.ParameterId)))
                {
                    var start=(ulong)owner.ByteOffset*8+owner.BitOffset;var end=checked(start+owner.BitLength);
                    if(start<range.End*8&&range.Start*8<end)NativeGeneralHash.Fail(NativeGeneralIssueCode.AmbiguousOwnership,owner.ParameterId);
                }
            }
        }
        ReservationOwners = Array.AsReadOnly(ranges.Select((range,index)=>
        {
            var hash=NativeGeneralHash.Of(new{profile="ModuleAllocationReservation1",Fingerprint,index,range.Target,range.Start,range.End});
            return new NativeRootOwner("MODULE-ALLOC-"+index.ToString(System.Globalization.CultureInfo.InvariantCulture),index,hash,
                range.Target,checked((uint)range.Start),0,checked((uint)((range.End-range.Start)*8)),false,null,null,hash,hash);
        }).ToArray());
    }
}
