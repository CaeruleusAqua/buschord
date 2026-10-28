using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Linq;

namespace Knx;

internal static class NativePropertyInitializer
{
    private const int MaxProperties = 128, MaxPropertyBytes = 1024 * 1024;
    private const long MaxOwnedBytes = 16 * 1024 * 1024;
    private static readonly string[] PointerNames = ["GroupAddressTablePtr", "GroupAssociationTablePtr", "GroupObjectTablePtr", "ApplicationDataPtr", "PeiprogDataPtr"];
    private sealed record GroupKey(PropertySourceSelector Selector, ushort Start, ushort Count);
    private sealed record AllocationSpec(PropertySourceSelector Raw, PropertySourceSelector Native, string Key,
        PropertyPdtLayout Pdt, NativePropertyAllocationControl[] Controls, uint Extent, int ControlBytes,
        int Length, int? FactoryOrdinal, FactoryImageSegment? Factory);

    internal static NativePropertyInitializationPlan Compile(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        PreparedConditionalLoadProcedure prepared, NativeProcedureEvaluationContext view, NativeApplicationEvaluationContext application,
        RootPropertyWriterSources sources)
    {
        try
        {
            ValidatePair(project, device, inputs, prepared, view, application, sources);
            var procedure = LoadProcedureCompiler.CompileResolved(prepared, view);
            if (!procedure.IsAvailable)
                throw new Rejected(new(PropertyInitializationIssueCode.ProcedureUnavailable, ProcedureIssue: procedure.Issues.FirstOrDefault()));
            ValidateProcedure(inputs, prepared, view, procedure);
            var closure = SourceClosure(inputs, sources);
            var specs = AllocationSpecs(procedure, sources);
            var factory = inputs.Manifest.Hardware!.FactoryImage;
            var counts = new List<NativePropertyCountObservation>();
            var enriched = new List<AllocationSpec>();
            foreach (var spec in specs)
            {
                var selected = FactorySource(factory, spec.Native, sources.PropertyProfile.Selection, out var ordinal);
                var length = Math.Max(spec.ControlBytes, selected?.Data.Length ?? 0);
                if (length > MaxPropertyBytes) Fail(PropertyInitializationIssueCode.LimitExceeded, key: spec.Key);
                enriched.Add(spec with { Length = length, FactoryOrdinal = ordinal, Factory = selected });
            }
            // Reserve initial temporary/copy pairs and all subsequent writer/final copies together.
            var reserved = checked(enriched.Sum(s => (long)s.Length) * 11);
            if (reserved > MaxOwnedBytes) Fail(PropertyInitializationIssueCode.LimitExceeded);
            var bySelector = enriched.ToDictionary(s => s.Native);
            foreach (var owner in sources.Owners)
            {
                if (!bySelector.TryGetValue(owner.NativeSelector, out var allocation))
                    Fail(PropertyInitializationIssueCode.MissingAllocation, owner.ParameterId, owner.PropertyKey);
                if (owner.PdtLayout != allocation.Pdt || owner.End > (ulong)allocation.Length * 8)
                    Fail(PropertyInitializationIssueCode.UncoveredOwner, owner.ParameterId, owner.PropertyKey);
            }
            for (var i = 0; i < factory.Segments.Count; i++)
            {
                var segment = factory.Segments[i];
                if (segment.Location.Component != FactoryImageComponent.PropertyElementCount) continue;
                var raw = Selector(segment.Location);
                if (raw is null) Fail(PropertyInitializationIssueCode.InvalidDeclaration);
                var normalized = ArchivedPropertySources.Normalize(raw, sources.PropertyProfile.Selection);
                if (!normalized.Canonical || segment.Data.Length != 2 || segment.Mask is { Length: not 2 })
                    Fail(PropertyInitializationIssueCode.InvalidDeclaration);
                var count = (ushort)((segment.Data[0] << 8) | segment.Data[1]);
                counts.Add(new(i, raw, normalized.Selector, ArchivedPropertySources.Key(normalized.Selector), segment.Data, segment.Mask, count,
                    segment.Mask is null ? PropertyCountStatus.MaskMissing : ArchivedPropertySources.FullMask(segment.Mask, 2)
                        ? PropertyCountStatus.FullyValid : PropertyCountStatus.MaskPartial));
            }
            var allocations = enriched.Select(spec =>
            {
                var data = new byte[spec.Length]; var mask = new byte[spec.Length];
                if (spec.Factory is { } initial)
                    for (var i = 0; i < initial.Data.Length; i++) data[i] = initial.Data[i];
                return new NativePropertyAllocation(spec.Raw, spec.Native, spec.Key, spec.Pdt, spec.Controls, spec.Extent,
                    spec.ControlBytes, spec.FactoryOrdinal, spec.Factory, new(data), new(mask));
            }).ToArray();
            var allocationHash = Hash(allocations.Select(a => new
            {
                a.RawSelector, a.NativeSelector, a.PropertyKey, a.PdtLayout, a.Controls, a.ElementExtent, a.ControlBytes,
                a.FactorySourceOrdinal, a.FactoryLocation, a.FactoryDataLength, a.FactoryDataHash, a.FactoryMaskHash,
                a.Length, data = a.InitialData.Sha256, validity = a.InitialValidity.Sha256,
            }).ToArray());
            var coverageHash = Hash(new
            {
                owners = sources.Owners, physical = sources.PhysicalReferences,
                allocations = enriched.Select(s => new { s.Native, s.Length }).ToArray(),
            });
            var fingerprint = Hash(new
            {
                profile = 1, source = inputs.Manifest.Binding!.Fingerprint, sources.SourceHash,
                prepared.GraphFingerprint, procedure.WitnessHash, procedure.Request, view.Fingerprint,
                application = application.Fingerprint, closure, allocationHash, coverageHash, reserved,
                counts = counts.Select(c => new { c.SourceOrdinal, c.RawSelector, c.NativeSelector, c.PropertyKey,
                    data = c.Data.Sha256, mask = c.Mask?.Sha256, c.EncodedCount, c.Status }).ToArray(),
            });
            ValidatePair(project, device, inputs, prepared, view, application, sources);
            return NativePropertyInitializationPlan.Success(inputs, prepared, view, application, sources, procedure,
                allocations, counts.ToArray(), closure, allocationHash, coverageHash, fingerprint, reserved);
        }
        catch (Rejected error) { return NativePropertyInitializationPlan.Unavailable(error.Issue); }
        catch (RootPropertyBuildRejected) { return NativePropertyInitializationPlan.Unavailable(new(PropertyInitializationIssueCode.InputsChanged)); }
        catch (RootBuildRejected) { return NativePropertyInitializationPlan.Unavailable(new(PropertyInitializationIssueCode.InputsChanged)); }
        catch (Exception error) when (error is InvalidOperationException)
        { return NativePropertyInitializationPlan.Unavailable(new(PropertyInitializationIssueCode.InputsChanged)); }
        catch (Exception error) when (error is XmlException or InvalidDataException or IOException or FormatException or OverflowException or ArgumentException or System.Text.Json.JsonException)
        { return NativePropertyInitializationPlan.Unavailable(new(PropertyInitializationIssueCode.InvalidDeclaration)); }
    }

    private static void ValidatePair(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        PreparedConditionalLoadProcedure prepared, NativeProcedureEvaluationContext view, NativeApplicationEvaluationContext application,
        RootPropertyWriterSources sources)
    {
        if (project is null || device is null || inputs is null || prepared is null || view is null || application is null || sources is null
            || !prepared.IsAvailable || prepared.Request is null || !view.IsAvailable || !application.IsAvailable
            || prepared.Request.NativeSubtype is not ("all" or "par" or "grp" or "par,grp" or "ap1" or "cfg")
            || !Enum.IsDefined(prepared.Request.Access)
            || (prepared.Request.Parts & ~(LoadParts.Full | LoadParts.Parameters | LoadParts.Groups)) != 0
            || (prepared.Request.Parts & (LoadParts.Full | LoadParts.Parameters)) == 0)
            Fail(PropertyInitializationIssueCode.InvalidRequest);
        if (!view.UsesSharedApplicationContext(application) || !sources.BelongsTo(inputs)) Fail(PropertyInitializationIssueCode.ContextMismatch);
        var manifest = inputs.Manifest;
        if (manifest.Status != ArchivedImageSourceStatus.Inspected || manifest.Binding is null || inputs.Application is null)
            Fail(PropertyInitializationIssueCode.SourceUnavailable);
        if (!manifest.Binding.MatchesIdentity(project, device) || !prepared.MatchesSources(inputs) || !sources.MatchesSources(inputs)
            || !view.MatchesPrepared(prepared) || !view.MatchesCurrent(prepared) || !view.MatchesInputs(project, device, inputs, prepared)
            || !application.MatchesInputs(project, device, inputs) || prepared.SourceFingerprint != manifest.Binding.Fingerprint
            || prepared.SourceFingerprint != application.SourceFingerprint || sources.ApplicationId != prepared.ApplicationId
            || sources.ApplicationDefinitionHash != prepared.ApplicationDefinitionHash || sources.ApplicationDefinitionHash != manifest.ApplicationDefinitionHash
            || application.ApplicationDefinitionHash != prepared.ApplicationDefinitionHash || application.CapturedInputs is null
            || !sources.MatchesSaved(application.CapturedInputs.Values, sources.ApplicationId)
            || sources.CarrierHash != sources.CaptureCarriers(project, device, sources.ApplicationId))
            Fail(PropertyInitializationIssueCode.InputsChanged);
        if (application.ScriptCapabilityUsed || application.Catalog is null || application.Snapshot is null || application.Activation is null
            || application.Snapshot.ModuleInstances.Count != 0 || !application.Activation.BelongsTo(application.Catalog, application.Snapshot))
            Fail(PropertyInitializationIssueCode.ContextMismatch);
        var seeds = prepared.ControllerReferences.Concat(application.Catalog.StructuralPlan.CallControllerReferences)
            .Distinct(StringComparer.Ordinal).Take(4097).ToArray();
        if (seeds.Length > 4096) Fail(PropertyInitializationIssueCode.LimitExceeded);
        if (!application.AdditionalControllerReferences.SetEquals(seeds)) Fail(PropertyInitializationIssueCode.ContextMismatch);
    }

    private static void ValidateProcedure(ArchivedImageSources.Inputs inputs, PreparedConditionalLoadProcedure prepared,
        NativeProcedureEvaluationContext view, LoadProcedurePlan procedure)
    {
        if (procedure.Request != prepared.Request || procedure.ApplicationId != prepared.ApplicationId || procedure.MaskVersionId != prepared.MaskVersionId
            || procedure.SelectedProfileHash != prepared.SelectedProfileHash || procedure.Style != prepared.Style
            || procedure.ProfileVersion != (prepared.RequiresExtendedProfile ? 2 : 1)
            || prepared.HasConditions && procedure.ConditionalSelection?.ControllerWitnessHash != view.Fingerprint)
            Fail(PropertyInitializationIssueCode.InputsChanged);
        if (procedure.ProfileVersion == 2)
        { if (procedure.ApplicationDefinitionHash != prepared.ApplicationDefinitionHash) Fail(PropertyInitializationIssueCode.InputsChanged); }
        else
        {
            var fresh = LoadProcedureCompiler.Compile(inputs.Application!, inputs.Master, prepared.Request!);
            if (!fresh.IsAvailable || fresh.ProfileVersion != 1 || fresh.WitnessHash != procedure.WitnessHash
                || ProcedureContents(fresh) != ProcedureContents(procedure)) Fail(PropertyInitializationIssueCode.InputsChanged);
        }
    }

    private static string SourceClosure(ArchivedImageSources.Inputs inputs, RootPropertyWriterSources sources)
    {
        var context = sources.ResourceContext;
        if (context.Status != ArchivedResourceContextStatus.Supported || context.MemoryRegions.Count != 0
            || sources.PropertyProfile.Selection.Status != MaskProfileStatus.Supported)
            Fail(PropertyInitializationIssueCode.UnsupportedInitialSource);
        var app = sources.Application; var ns = app.Name.Namespace;
        foreach (var element in app.Descendants())
            if (element.Name.LocalName is "AbsoluteSegment" or "RelativeSegment" or "LdCtrlSegmentSource"
                || element.Name.LocalName == "Code" && (element.Name != ns + "Code" || element.Parent?.Name != ns + "Static" || element.Elements().Any()))
                Fail(PropertyInitializationIssueCode.UnsupportedInitialSource);
        var pointers = context.Resources.Where(r => PointerNames.Contains(r.Name, StringComparer.Ordinal) && r.Access.Contains("remote", StringComparer.Ordinal)).ToArray();
        foreach (var pointer in pointers)
        {
            if (pointer.Location is null || pointer.DeclaredLength is null
                || pointer.Location.AddressSpace == ArchivedResourceAddressSpace.SystemProperty && pointer.DeclaredLength == 4
                || pointer.Location.AddressSpace is ArchivedResourceAddressSpace.Pointer or ArchivedResourceAddressSpace.None)
                Fail(PropertyInitializationIssueCode.UnsupportedInitialSource, resource: pointer.Name);
        }
        var hardware = inputs.Manifest.Hardware;
        if (hardware?.Status != ArchivedHardwareSourceStatus.Inspected
            || hardware.LoadedImageDeclared && hardware.FactoryImage.Status != ArchivedFactoryImageStatus.Decoded
            || hardware.LoadedImageDeclared && hardware.FactoryImage.Segments.Count == 0
            || !hardware.LoadedImageDeclared && hardware.FactoryImage.Status != ArchivedFactoryImageStatus.Missing)
            throw new Rejected(new(PropertyInitializationIssueCode.FactoryUnavailable,
                FactoryStatus: hardware?.FactoryImage.Status, FactoryBlobHash: hardware?.FactoryImage.BlobHash));
        return Hash(new
        {
            profile = 1, context.Fingerprint, property = sources.PropertyProfile.Selection.Fingerprint,
            memoryRegions = context.MemoryRegions, pointers, noCode = true, explicitZeroValidity = true,
            hardware.LoadedImageDeclared, hardware.FactoryImage.Status, hardware.FactoryImage.BlobHash, hardware.FactoryImage.DecodedHash,
            factory = hardware.FactoryImage.Segments.Select(s => new { s.Location, data = s.Data.Sha256, mask = s.Mask?.Sha256 }).ToArray(),
        });
    }

    private static AllocationSpec[] AllocationSpecs(LoadProcedurePlan procedure, RootPropertyWriterSources sources)
    {
        if (procedure.Controls.Count > 1024) Fail(PropertyInitializationIssueCode.LimitExceeded);
        var tuples = new HashSet<GroupKey>();
        var groups = new Dictionary<PropertySourceSelector, List<(PropertySourceSelector Raw, NativePropertyAllocationControl Control) >>();
        foreach (var control in procedure.Controls)
        {
            switch (control.Kind)
            {
                case LoadControlKind.AbsoluteSegment: case LoadControlKind.RelativeSegment: case LoadControlKind.TaskSegment:
                case LoadControlKind.WriteRelativeMemory: case LoadControlKind.LoadImageProperty: case LoadControlKind.Merge:
                    Fail(PropertyInitializationIssueCode.UnsupportedControl); break;
                case LoadControlKind.Connect: case LoadControlKind.Disconnect: case LoadControlKind.Load: case LoadControlKind.Unload:
                case LoadControlKind.LoadCompleted: case LoadControlKind.CompareProperty: case LoadControlKind.CompareRelativeMemory:
                case LoadControlKind.Restart: case LoadControlKind.MapError: continue;
                case LoadControlKind.WriteProperty: break;
                default: Fail(PropertyInitializationIssueCode.UnsupportedControl); break;
            }
            var raw = ControlSelector(control);
            var start = checked((ushort)Unsigned(control, "StartElement")); var count = checked((ushort)Unsigned(control, "Count"));
            if (start == 0 || count == 0 || raw.PropertyId == 27) Fail(PropertyInitializationIssueCode.UnsupportedControl);
            if (!tuples.Add(new(raw, start, count))) Fail(PropertyInitializationIssueCode.DuplicateControlGroup);
            var native = NativeSelector(raw, sources.PropertyProfile.Selection);
            if (!groups.TryGetValue(native, out var entries)) groups.Add(native, entries = []);
            entries.Add((raw, new(control.Ordinal, start, count, control.Provenance)));
        }
        if (groups.Count > MaxProperties) Fail(PropertyInitializationIssueCode.LimitExceeded);
        if (groups.Count != 0 && procedure.Style == LoadProcedureStyle.DefaultProcedure) Fail(PropertyInitializationIssueCode.UnsupportedControl);
        var result = new List<AllocationSpec>();
        foreach (var group in groups)
        {
            if (groups.Keys.Any(other => other.PropertyId == group.Key.PropertyId && (other.Kind == PropertySourceKind.ByType) != (group.Key.Kind == PropertySourceKind.ByType)))
                Fail(PropertyInitializationIssueCode.UnsupportedSelector);
            var pdt = Layout(sources, group.Key);
            if (pdt.Status != PropertyPdtStatus.KnownSymmetric || pdt.WriteElementBytes is not (> 0) || pdt.ReadElementBytes != pdt.WriteElementBytes)
                Fail(PropertyInitializationIssueCode.UnsupportedPdt);
            var extent = group.Value.Max(c => checked((uint)c.Control.StartElement + c.Control.Count - 1));
            var bytes = checked((ulong)extent * pdt.WriteElementBytes.Value);
            if (bytes > MaxPropertyBytes) Fail(PropertyInitializationIssueCode.LimitExceeded);
            var key = ArchivedPropertySources.Key(group.Key);
            result.Add(new(group.Value[0].Raw, group.Key, key, pdt, group.Value.Select(c => c.Control).ToArray(), extent, (int)bytes, (int)bytes, null, null));
        }
        return result.ToArray();
    }

    private static PropertySourceSelector ControlSelector(LoadProcedureControl control)
    {
        var pid = Unsigned(control, "PropId"); var index = OptionalUnsigned(control, "ObjIdx");
        var type = OptionalUnsigned(control, "ObjType"); var occurrence = OptionalUnsigned(control, "Occurrence") ?? 0;
        if (pid > byte.MaxValue || occurrence > byte.MaxValue || index is > byte.MaxValue || type is > ushort.MaxValue || (index is null) == (type is null))
            Fail(PropertyInitializationIssueCode.UnsupportedSelector);
        return index is { } value ? new(PropertySourceKind.SystemProperty, (ushort)pid, NativeObjectIndex: (ushort)value)
            : new(PropertySourceKind.ByType, (ushort)pid, ObjectType: (ushort)type!.Value, Occurrence: (ushort)occurrence);
    }
    private static PropertySourceSelector NativeSelector(PropertySourceSelector raw, MaskProfileSelection profile)
    {
        var normalized = ArchivedPropertySources.Normalize(raw, profile);
        if (!normalized.Canonical) Fail(PropertyInitializationIssueCode.UnsupportedSelector);
        return normalized.Selector;
    }
    private static PropertyPdtLayout Layout(RootPropertyWriterSources sources, PropertySourceSelector selector)
    {
        ParameterStorageLocation location;
        if (selector.Kind == PropertySourceKind.ByType && selector.Occurrence is <= byte.MaxValue && selector.ObjectType is { } type)
            location = new(ParameterStorageStatus.Supported, ParameterStorageKind.PropertyByType,
                ObjectType: type, Occurrence: (byte)selector.Occurrence.Value, PropertyId: (byte)selector.PropertyId);
        else
        {
            uint? native = selector.Kind == PropertySourceKind.SystemProperty ? selector.NativeObjectIndex
                : selector.ApplicationObjectIndex is { } app && sources.PropertyProfile.Selection.FirstAppObjectIndex is { } first ? (uint)app + first : null;
            if (native is null or > byte.MaxValue) Fail(PropertyInitializationIssueCode.UnsupportedSelector);
            location = new(ParameterStorageStatus.Supported, ParameterStorageKind.PropertyByIndex, ObjectIndex: (byte)native.Value, PropertyId: (byte)selector.PropertyId);
        }
        return PropertyPdtRoles.Read(sources.PropertyProfile.Interpret(location, sources.MaskVersionId));
    }

    private static FactoryImageSegment? FactorySource(ArchivedFactoryImageSource factory, PropertySourceSelector native,
        MaskProfileSelection profile, out int? ordinal)
    {
        ordinal = null; FactoryImageSegment? selected = null; var counts = 0;
        for (var i = 0; i < factory.Segments.Count; i++)
        {
            var segment = factory.Segments[i]; var raw = Selector(segment.Location);
            if (raw is null || raw.PropertyId != native.PropertyId) continue;
            if ((raw.Kind == PropertySourceKind.ByType) != (native.Kind == PropertySourceKind.ByType))
                Fail(PropertyInitializationIssueCode.AmbiguousFactorySource);
            var normalized = NativeSelector(raw, profile);
            if (normalized != native) continue;
            if (raw != native) Fail(PropertyInitializationIssueCode.AmbiguousFactorySource);
            if (segment.Location.Component == FactoryImageComponent.PropertyElementCount)
            { if (++counts > 1) Fail(PropertyInitializationIssueCode.AmbiguousFactorySource); continue; }
            if (segment.Location.Component != FactoryImageComponent.Data || segment.Location.ByteOffset != 0 || selected is not null)
                Fail(PropertyInitializationIssueCode.AmbiguousFactorySource);
            selected = segment; ordinal = i;
        }
        return selected;
    }
    private static PropertySourceSelector? Selector(FactoryImageLocation location) => location.AddressSpace switch
    {
        FactoryImageAddressSpace.SystemProperty when location.NativeObjectIndex is { } index && location.PropertyId is { } pid
            => new(PropertySourceKind.SystemProperty, pid, NativeObjectIndex: index),
        FactoryImageAddressSpace.AppProperty when location.ApplicationObjectIndex is { } index && location.PropertyId is { } pid
            => new(PropertySourceKind.AppProperty, pid, ApplicationObjectIndex: index),
        FactoryImageAddressSpace.PropertyByType when location.ObjectType is { } type && location.Occurrence is { } occurrence && location.PropertyId is { } pid
            => new(PropertySourceKind.ByType, pid, ObjectType: type, Occurrence: occurrence),
        _ => null,
    };
    private static ulong Unsigned(LoadProcedureControl control, string name) => OptionalUnsigned(control, name)
        ?? throw new Rejected(new(PropertyInitializationIssueCode.InvalidControl));
    private static ulong? OptionalUnsigned(LoadProcedureControl control, string name)
    {
        var matches = control.Arguments.Where(a => a.Name == name).Take(2).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1 || matches[0].Kind != LoadControlArgumentKind.Unsigned || matches[0].UnsignedValue is null)
            Fail(PropertyInitializationIssueCode.InvalidControl);
        return matches[0].UnsignedValue;
    }
    private static string ProcedureContents(LoadProcedurePlan procedure) => Hash(new
    {
        procedure.ProfileVersion, procedure.ApplicationId, procedure.MaskVersionId, procedure.Style, procedure.Request,
        procedure.ApplicationDefinitionHash, procedure.SelectedProfileHash, procedure.WitnessHash,
        controls = procedure.Controls.Select(c => new { c.Ordinal, c.Kind, c.Origin, c.SourceOrdinal, c.TemplateOrdinal, c.MergeId,
            c.DeclaredAppliesTo, c.EffectiveAppliesTo, c.Arguments, c.OnError, data = c.InlineData?.Sha256, mask = c.InlineMask?.Sha256, c.Provenance }).ToArray(),
        procedure.MergeDecisions, procedure.ResourceReferences, procedure.TableRequirements, procedure.Requirements, procedure.ConditionalSelection,
    });
    private static string Hash<T>(T facts) => RootPropertyBuildGuard.Hash(facts);
    [DoesNotReturn] private static void Fail(PropertyInitializationIssueCode code, string? reference = null, string? key = null, string? resource = null)
        => throw new Rejected(new(code, reference, key, resource));
    private sealed class Rejected(PropertyInitializationIssue issue) : Exception
    { internal PropertyInitializationIssue Issue { get; } = issue; }
}
