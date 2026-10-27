using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Linq;

namespace Knx;

internal static partial class GeneratedTableCompiler
{
    private const int BlobLimit = 1_048_576, TotalBytes = 4 * BlobLimit;
    private sealed class Rejected(GeneratedTableIssue issue) : Exception { internal GeneratedTableIssue Issue => issue; }
    private sealed record Resource(string Id, ResourcePatchArena Arena, uint Offset, uint? Size, byte[]? Data, byte[]? Mask)
    {
        internal ulong? Extent => Size is { } size ? Math.Max((ulong)size, (ulong)Math.Max(Data?.Length ?? 0, Mask?.Length ?? 0)) : null;
    }
    [DoesNotReturn]
    private static void Fail(GeneratedTableIssueCode code, string? resource = null)
        => throw new Rejected(new(code, ResourceId: Safe(resource) ? resource : null));
    private static bool Safe(string? value) => value is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    internal static GeneratedTablePreview Compile(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        LoadProcedurePlan procedure, GeneratedTableRequest request, IParameterScriptRuntime? scripts = null)
    {
        var format = request is not null && Enum.IsDefined(request.Format) ? (GeneratedTableFormat?)request.Format : null;
        try { return Create(project, device, inputs, procedure, request!, scripts); }
        catch (Rejected failure) { return GeneratedTablePreview.Unavailable(failure.Issue, format); }
        catch (Exception failure) when (failure is XmlException or InvalidDataException or FormatException or OverflowException or ArgumentException)
        { return GeneratedTablePreview.Unavailable(new(GeneratedTableIssueCode.InvalidTableDeclaration), format); }
    }

    internal static GeneratedTablePreview CompileFromContext(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        LoadProcedurePlan procedure, GeneratedTableRequest request, NativeApplicationEvaluationContext evaluation)
    {
        var format = request is not null && Enum.IsDefined(request.Format) ? (GeneratedTableFormat?)request.Format : null;
        try
        {
            if (project is null || device is null || inputs is null || inputs.Manifest is null || inputs.Application is null || evaluation is null
                || procedure is null || request is null || !Enum.IsDefined(request.Format)
                || !procedure.IsAvailable || procedure.Request is null || procedure.ProfileVersion != 1
                || procedure.ConditionalSelection is not null)
                Fail(GeneratedTableIssueCode.InvalidRequest);
            if (!evaluation.IsAvailable)
                return GeneratedTablePreview.Unavailable(evaluation.Issues.FirstOrDefault() ?? new(GeneratedTableIssueCode.IncompleteActivation), format);
            var sourceBinding = inputs.Manifest.Binding;
            if (sourceBinding is null || !sourceBinding.MatchesIdentity(project, device)
                || !evaluation.MatchesInputs(project, device, inputs)) Fail(GeneratedTableIssueCode.InputsChanged);
            // Existing binding replay creates the ordinary context with only structural call controllers.
            if (evaluation.Catalog is null || !evaluation.AdditionalControllerReferences.SetEquals(evaluation.Catalog.StructuralPlan.CallControllerReferences))
                Fail(GeneratedTableIssueCode.InvalidRequest);
            var paired = LoadProcedureCompiler.Compile(inputs.Application, inputs.Master, procedure.Request);
            if (!paired.IsAvailable || paired.ProfileVersion != 1 || paired.WitnessHash != procedure.WitnessHash
                || paired.ApplicationId != procedure.ApplicationId || paired.MaskVersionId != procedure.MaskVersionId
                || paired.Style != procedure.Style || paired.SelectedProfileHash != procedure.SelectedProfileHash || paired.Request != procedure.Request)
                Fail(GeneratedTableIssueCode.InputsChanged);
            var result = Create(project, device, inputs, paired, request, scripts: null, providedContext: evaluation);
            if (!sourceBinding.MatchesIdentity(project, device) || !evaluation.MatchesInputs(project, device, inputs))
                Fail(GeneratedTableIssueCode.InputsChanged);
            return result;
        }
        catch (Rejected failure) { return GeneratedTablePreview.Unavailable(failure.Issue, format); }
        catch (Exception failure) when (failure is XmlException or InvalidDataException or FormatException or OverflowException or ArgumentException)
        { return GeneratedTablePreview.Unavailable(new(GeneratedTableIssueCode.InvalidTableDeclaration), format); }
    }

    private static GeneratedTablePreview Create(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        LoadProcedurePlan procedure, GeneratedTableRequest request, IParameterScriptRuntime? scripts,
        NativeApplicationEvaluationContext? providedContext = null)
        => CreateEmission<GeneratedTablePreview>(project, device, inputs, procedure, request, scripts, providedContext, LegacyEmissionMaterializer.Instance);

    private static TResult CreateEmission<TResult>(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        LoadProcedurePlan procedure, GeneratedTableRequest request, IParameterScriptRuntime? scripts,
        NativeApplicationEvaluationContext? providedContext, IEmissionMaterializer<TResult> materializer)
    {
        if (request is null || !Enum.IsDefined(request.Format) || !procedure.IsAvailable || procedure.Request is null
            || (procedure.Request.Parts & (LoadParts.Full | LoadParts.Groups)) == 0) Fail(GeneratedTableIssueCode.InvalidRequest);
        var sources = inputs.Manifest; var context = sources.ResourceContext;
        if (sources.Status != ArchivedImageSourceStatus.Inspected || inputs.Application?.Root is null || sources.Binding is null)
            Fail(GeneratedTableIssueCode.SourceBindingUnavailable);
        if (!sources.Binding!.MatchesIdentity(project, device)) Fail(GeneratedTableIssueCode.StaleSource);
        if (context?.Status != ArchivedResourceContextStatus.Supported) Fail(GeneratedTableIssueCode.InvalidMaster);
        if (sources.Hardware?.LoadedImageDeclared == true) Fail(GeneratedTableIssueCode.UnsupportedFactoryImage);
        var small = request.Format == GeneratedTableFormat.M112;
        if (context!.NumericMask != (small ? 0x0705 : 0x07B0)) Fail(GeneratedTableIssueCode.UnsupportedTableFormat);
        var expected = small ? new[] { "AddressTable_Bcu1", "AssociationTable_M112", "GroupObjectTable_M112" }
            : new[] { "AddressTable_SystemB", "AssociationTable_SystemB", "GroupObjectTable_SystemB" };
        var names = new[] { "GroupAddressTable", "GroupAssociationTable", "GroupObjectTable" };
        for (var i = 0; i < names.Length; i++)
        {
            var resolution = context.ResolveRemote(names[i], "lsm");
            if (resolution.Status != ArchivedResourceResolutionStatus.Found || resolution.Descriptor?.Flavour != expected[i])
                Fail(GeneratedTableIssueCode.UnsupportedTableFormat, names[i]);
            if (!small && (resolution.Descriptor.Location is not { AddressSpace: ArchivedResourceAddressSpace.RelativeMemory, Offset: 0 } location
                || location.InterfaceObjectRef != i + 1)) Fail(GeneratedTableIssueCode.UnsupportedTableFormat, names[i]);
        }
        var ns = inputs.Application.Root.Name.Namespace;
        var app = Single(inputs.Application.Root.Descendants(ns + "ApplicationProgram"));
        if (small && (string?)app.Attribute("DynamicTableManagement") is { } management && XmlConvert.ToBoolean(management))
            Fail(GeneratedTableIssueCode.UnsupportedTableManagement);
        var stat = Single(app.Elements(ns + "Static")); var resources = ReadResources(stat);
        var declarations = new[] { Single(stat.Elements(ns + "AddressTable")), Single(stat.Elements(ns + "AssociationTable")), Single(stat.Elements(ns + "ComObjectTable")) };
        var evaluationContext = providedContext ?? NativeApplicationEvaluationContext.Create(project, device, inputs, scripts: scripts);
        if (!evaluationContext.IsAvailable)
            return materializer.Unavailable(evaluationContext.Issues.FirstOrDefault() ?? new(GeneratedTableIssueCode.IncompleteActivation), request.Format);
        var witness = NativeCommunicationWitness.CreateFromContext(project, device, inputs, evaluationContext);
        if (!witness.IsAvailable)
            return materializer.Unavailable(witness.Issues.FirstOrDefault() ?? new(GeneratedTableIssueCode.IncompleteActivation), request.Format);
        if (witness.SourceFingerprint != sources.Binding.Fingerprint || witness.ApplicationDefinitionHash != sources.ApplicationDefinitionHash)
            Fail(GeneratedTableIssueCode.InputsChanged);
        ValidateWitness(witness, request.Format);
        var lengths = GeneratedTableSerializer.Lengths(request.Format, witness);
        if (lengths.Address > BlobLimit || lengths.Association > BlobLimit || lengths.Objects > BlobLimit
            || (long)lengths.Address + lengths.Association + lengths.Objects > TotalBytes) Fail(GeneratedTableIssueCode.LimitExceeded);
        foreach (var (node, count) in new[] { (declarations[0], witness.Groups.Count), (declarations[1], witness.Associations.Count) })
        {
            var max = node.Attribute("MaxEntries");
            if (max is null) Fail(GeneratedTableIssueCode.InvalidTableDeclaration);
            if ((ulong)count > Number(max!.Value, uint.MaxValue)) Fail(GeneratedTableIssueCode.TableCapacityExceeded);
        }
        var sizes = new[] { lengths.Address, lengths.Association, lengths.Objects };
        var spans = new GeneratedTableSourceSpan[3];
        for (var i = 0; i < spans.Length; i++) spans[i] = Span(declarations[i], resources, small, i + 1, sizes[i]);
        if (small && spans[0].Offset != 0x4000) Fail(GeneratedTableIssueCode.UnsupportedTableFormat);
        for (var i = 0; i < spans.Length; i++)
            for (var j = i + 1; j < spans.Length; j++)
                if (spans[i].Arena == spans[j].Arena && (ulong)spans[i].Offset < (ulong)spans[j].Offset + (uint)sizes[j]
                    && (ulong)spans[j].Offset < (ulong)spans[i].Offset + (uint)sizes[i]) Fail(GeneratedTableIssueCode.AmbiguousResourceOwnership);
        var payloads = GeneratedTableSerializer.Encode(request.Format, witness, spans[0], spans[1], spans[2]);
        var adjustments = Adjustments(procedure, small, lengths);
        var requirements = new List<GeneratedTableRequirement>
        {
            new(GeneratedTableRequirementCode.CompleteParameterImage), new(GeneratedTableRequirementCode.ProcedureFinalization),
            new(GeneratedTableRequirementCode.SecurityAndRfTables), new(GeneratedTableRequirementCode.ProcedureAdjustmentsNotApplied),
        };
        if (!small) requirements.Add(new(GeneratedTableRequirementCode.PhysicalResourceMapping));
        else
        {
            requirements.Add(new(GeneratedTableRequirementCode.UnusedTailMaskInvalidation, spans[0].ResourceId));
            requirements.Add(new(GeneratedTableRequirementCode.UnusedTailMaskInvalidation, spans[1].ResourceId));
        }
        if (!sources.Binding.MatchesIdentity(project, device) || !evaluationContext.MatchesInputs(project, device, inputs))
            Fail(GeneratedTableIssueCode.InputsChanged);
        return materializer.Success(sources.Binding, procedure, request, witness, payloads, adjustments, requirements);
    }

    private static void ValidateWitness(NativeCommunicationWitness witness, GeneratedTableFormat format)
    {
        if (witness.Slots.Count > 65536 || witness.Groups.Count > 65535 || witness.Associations.Count > 65535)
            Fail(GeneratedTableIssueCode.LimitExceeded);
        if (format == GeneratedTableFormat.M112 && (witness.Slots.Count > 255 || witness.Groups.Count > 254 || witness.Associations.Count > 255))
            Fail(GeneratedTableIssueCode.TableCapacityExceeded);
        for (var i = 0; i < witness.Slots.Count; i++)
        {
            var slot = witness.Slots[i];
            if (slot.Number != i || slot.IsPlaceholder && slot.Active) Fail(GeneratedTableIssueCode.InvalidObjectDeclaration);
            if (!GeneratedTableSerializer.TrySizeCode(slot.SizeInBit, slot.IsPlaceholder, out _)) Fail(GeneratedTableIssueCode.UnsupportedObjectSize);
            if (!Enum.IsDefined(slot.Priority)) Fail(GeneratedTableIssueCode.UnsupportedObjectPriority);
        }
        if (format != GeneratedTableFormat.M112 && witness.Slots.Count > 0 && witness.Slots[0].Active)
            Fail(GeneratedTableIssueCode.InvalidObjectDeclaration);
        ushort previous = 0;
        foreach (var group in witness.Groups)
        {
            if (group.Address == 0 || group.Address <= previous) Fail(GeneratedTableIssueCode.AmbiguousGroupAddress);
            if (group.Acknowledge) Fail(GeneratedTableIssueCode.UnsupportedAcknowledgement);
            previous = group.Address;
        }
        var seen = new HashSet<(ushort, int)>(); var sending = new HashSet<ushort>();
        foreach (var association in witness.Associations)
        {
            if (association.ObjectNumber >= witness.Slots.Count || !witness.Slots[association.ObjectNumber].Active
                || association.GroupAddressIndex < 0 || association.GroupAddressIndex >= witness.Groups.Count)
                Fail(GeneratedTableIssueCode.InvalidObjectDeclaration);
            if (format != GeneratedTableFormat.SystemB4ByteAssociations && (association.ObjectNumber > 255 || association.GroupAddressIndex >= 255))
                Fail(GeneratedTableIssueCode.TableCapacityExceeded);
            if (!seen.Add((association.ObjectNumber, association.GroupAddressIndex))) Fail(GeneratedTableIssueCode.DuplicateAssociation);
            if (association.Sending && !sending.Add(association.ObjectNumber)) Fail(GeneratedTableIssueCode.MultipleSendingAssociations);
        }
    }

    private static GeneratedTableSourceSpan Span(XElement declaration, Resource[] resources, bool small, int lsm, int length)
    {
        var id = (string?)declaration.Attribute("CodeSegment");
        var arena = small ? new ResourcePatchArena(ResourcePatchArenaKind.AbsoluteMemory)
            : new ResourcePatchArena(ResourcePatchArenaKind.RelativeMemory, (byte)lsm);
        Resource? resource = null; uint offset;
        if (id is not null)
        {
            if (!Safe(id)) Fail(GeneratedTableIssueCode.InvalidTableDeclaration);
            resource = resources.SingleOrDefault(r => r.Id == id);
            if (resource is null || resource.Arena != arena)
                Fail(GeneratedTableIssueCode.MissingResource, id);
            var delta = declaration.Attribute("Offset");
            if (delta is null) Fail(GeneratedTableIssueCode.MissingResource, id);
            offset = checked(resource!.Offset + (uint)Number(delta!.Value, uint.MaxValue));
            if (!small && offset != 0) Fail(GeneratedTableIssueCode.UnsupportedTableFormat, id);
        }
        else
        {
            if (small || declaration.Attribute("Offset") is not null) Fail(GeneratedTableIssueCode.MissingResource);
            offset = 0;
        }
        if ((ulong)offset + (uint)length > 1UL << 32) Fail(GeneratedTableIssueCode.InvalidTableDeclaration);
        foreach (var other in resources.Where(r => r.Arena == arena && r.Id != id))
            if ((ulong)other.Offset < (ulong)offset + (uint)length && other.Extent != 0
                && (other.Extent is null || (ulong)offset < (ulong)other.Offset + other.Extent.Value))
                Fail(GeneratedTableIssueCode.AmbiguousResourceOwnership, other.Id);
        if (resource is null) return new(GeneratedTablePayloadOrigin.FreshGenerated, null, arena, offset);
        var index = checked((int)(offset - resource.Offset));
        if (resource.Data is null || index > resource.Data.Length || length > resource.Data.Length - index)
            Fail(GeneratedTableIssueCode.MissingInitialData, resource.Id);
        var data = resource.Data!.AsSpan(index, length).ToArray();
        var mask = resource.Mask?.AsSpan(index, length).ToArray();
        return new(GeneratedTablePayloadOrigin.RetainedCode, resource.Id, arena, offset, data, mask);
    }

    private static Resource[] ReadResources(XElement stat)
    {
        var code = Single(stat.Elements(stat.Name.Namespace + "Code")); var result = new List<Resource>();
        var ids = new HashSet<string>(StringComparer.Ordinal); var bytes = 0;
        foreach (var segment in code.Elements())
        {
            if (result.Count >= 1024) Fail(GeneratedTableIssueCode.LimitExceeded);
            var relative = segment.Name.LocalName == "RelativeSegment";
            if (!relative && segment.Name.LocalName != "AbsoluteSegment") Fail(GeneratedTableIssueCode.InvalidTableDeclaration);
            var allowed = relative ? new[] { "Id", "Offset", "Size", "LoadStateMachine" } : new[] { "Id", "Address", "Size", "MemoryType", "UserMemory" };
            if (segment.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || !allowed.Contains(a.Name.LocalName)))
                || segment.Elements().Any(e => e.Name != stat.Name.Namespace + "Data" && e.Name != stat.Name.Namespace + "Mask"))
                Fail(GeneratedTableIssueCode.InvalidTableDeclaration);
            var id = (string?)segment.Attribute("Id");
            if (!Safe(id) || !ids.Add(id!)) Fail(GeneratedTableIssueCode.AmbiguousResourceOwnership);
            if (segment.Attribute("UserMemory") is { } user && XmlConvert.ToBoolean(user.Value)) Fail(GeneratedTableIssueCode.UnsupportedTableFormat, id);
            var offset = (uint)Number((string?)segment.Attribute(relative ? "Offset" : "Address"), 1_048_575);
            var arena = relative ? new ResourcePatchArena(ResourcePatchArenaKind.RelativeMemory, (byte)Number((string?)segment.Attribute("LoadStateMachine"), 255))
                : new ResourcePatchArena(ResourcePatchArenaKind.AbsoluteMemory);
            uint? size = segment.Attribute("Size") is { } declared ? (uint)Number(declared.Value, 1_048_575) : null;
            var data = Decode(segment, "Data", ref bytes); var mask = Decode(segment, "Mask", ref bytes);
            if (mask is not null && (data is null || mask.Length != data.Length)) Fail(GeneratedTableIssueCode.InvalidTableDeclaration, id);
            result.Add(new(id!, arena, offset, size, data, mask));
        }
        return result.ToArray();
    }
    private static byte[]? Decode(XElement segment, string name, ref int total)
    {
        var nodes = segment.Elements(segment.Name.Namespace + name).Take(2).ToArray();
        if (nodes.Length > 1) Fail(GeneratedTableIssueCode.InvalidTableDeclaration);
        if (nodes.Length == 0) return null;
        var node = nodes[0]; var raw = node.Value;
        if (node.HasElements || node.Attributes().Any(a => !a.IsNamespaceDeclaration)) Fail(GeneratedTableIssueCode.InvalidTableDeclaration);
        if (raw.Length > 2 * BlobLimit) Fail(GeneratedTableIssueCode.LimitExceeded);
        var compact = new string(raw.Where(c => c is not (' ' or '\r' or '\n' or '\t')).ToArray());
        var padding = compact.EndsWith("==", StringComparison.Ordinal) ? 2 : compact.EndsWith('=') ? 1 : 0;
        var length = checked(compact.Length / 4 * 3 - padding);
        if (compact.Length % 4 != 0 || length < 0) Fail(GeneratedTableIssueCode.InvalidTableDeclaration);
        if (length > BlobLimit || (total = checked(total + length)) > TotalBytes) Fail(GeneratedTableIssueCode.LimitExceeded);
        return Convert.FromBase64String(compact);
    }
    private static XElement Single(IEnumerable<XElement> source)
    {
        var elements = source.Take(2).ToArray();
        if (elements.Length != 1) Fail(GeneratedTableIssueCode.InvalidTableDeclaration);
        return elements[0];
    }
    private static ulong Number(string? text, ulong max)
    {
        if (text is null || text.Length > 64) Fail(GeneratedTableIssueCode.InvalidTableDeclaration);
        var value = XmlConvert.ToUInt64(text!); if (value > max) Fail(GeneratedTableIssueCode.InvalidTableDeclaration); return value;
    }
    private static GeneratedTableProcedureAdjustment[] Adjustments(LoadProcedurePlan procedure, bool small,
        (int Address, int Association, int Objects) lengths)
    {
        if (small) return [];
        var result = new List<GeneratedTableProcedureAdjustment>();
        foreach (var (role, selector, length) in new[] { (GeneratedTableRole.GroupAddress, 1UL, lengths.Address),
            (GeneratedTableRole.Association, 2UL, lengths.Association), (GeneratedTableRole.CommunicationObject, 3UL, lengths.Objects) })
        {
            foreach (var kind in new[] { LoadControlKind.RelativeSegment, LoadControlKind.WriteRelativeMemory })
            {
                var name = kind == LoadControlKind.RelativeSegment ? "LsmIdx" : "ObjIdx";
                var controls = procedure.Controls.Where(c => c.Kind == kind && c.Arguments.Any(a => a.Name == name && a.UnsignedValue == selector)).ToArray();
                if (controls.Length > 1) Fail(GeneratedTableIssueCode.AmbiguousProcedureControl);
                if (controls.Length == 0) continue;
                var control = controls[0]; var sizes = control.Arguments.Where(a => a.Name == "Size" && a.UnsignedValue is not null).ToArray();
                if (sizes.Length != 1) Fail(GeneratedTableIssueCode.UnsupportedProcedureControl);
                var before = sizes[0].UnsignedValue!.Value;
                if (before != (ulong)length)
                    result.Add(new(control.Ordinal, kind, "Size", before, (ulong)length, role));
            }
        }
        return result.ToArray();
    }
}
