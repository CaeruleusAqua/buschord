using System.Xml;
using System.Xml.Linq;

namespace Knx;

internal sealed record NativeGeneralTableSpan(GeneratedTableRole Role, NativeImageTarget Target,
    uint Offset, int Length, string? ResourceId, string DeclarationHash);

/// <summary>Source geometry and one shared native witness; no retained-code buffers are adopted.</summary>
internal sealed class NativeGeneralTablePreparation
{
    private readonly Project? _project;
    private readonly DeviceInstance? _device;
    private readonly ArchivedImageSources.Inputs? _inputs;
    private readonly NativeRootImageInventory? _inventory;
    private readonly NativeApplicationEvaluationContext? _application;
    internal bool IsAvailable => Fingerprint is not null && Issues.Count == 0;
    internal IReadOnlyList<NativeGeneralIssue> Issues { get; }
    internal GeneratedTableFormat Format { get; }
    internal IReadOnlyList<NativeGeneralTableSpan> Spans { get; }
    internal NativeCommunicationWitness? Witness { get; }
    internal string? Fingerprint { get; }
    internal long EncoderPayloadReservation => checked(Spans.Sum(s => (long)s.Length) * 8);
    private NativeGeneralTablePreparation(NativeGeneralIssue issue, GeneratedTableFormat format)
    { Issues = Array.AsReadOnly(new[] { issue }); Spans = Array.AsReadOnly(Array.Empty<NativeGeneralTableSpan>()); Format = format; }
    private NativeGeneralTablePreparation(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        NativeRootImageInventory inventory, NativeApplicationEvaluationContext application, GeneratedTableFormat format,
        NativeCommunicationWitness witness, NativeGeneralTableSpan[] spans)
    {
        _project = project; _device = device; _inputs = inputs; _inventory = inventory; _application = application;
        Format = format; Witness = witness; Spans = Array.AsReadOnly(spans); Issues = Array.AsReadOnly(Array.Empty<NativeGeneralIssue>());
        Fingerprint = NativeGeneralHash.Of(new { version = 1, source = inventory.SourceHash, application = application.Fingerprint,
            witness = witness.Fingerprint, format, spans, EncoderPayloadReservation });
    }
    internal static NativeGeneralTablePreparation Failure(NativeGeneralIssue issue, GeneratedTableFormat format) => new(issue, format);
    internal static NativeGeneralTablePreparation Seal(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        NativeRootImageInventory inventory, NativeApplicationEvaluationContext application, GeneratedTableFormat format,
        NativeCommunicationWitness witness, NativeGeneralTableSpan[] spans)
        => new(project, device, inputs, inventory, application, format, witness, spans);
    internal bool BelongsTo(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        NativeRootImageInventory inventory, NativeApplicationEvaluationContext application)
        => IsAvailable && ReferenceEquals(_project, project) && ReferenceEquals(_device, device) && ReferenceEquals(_inputs, inputs)
            && ReferenceEquals(_inventory, inventory) && ReferenceEquals(_application, application);
    internal bool MatchesCurrent() => IsAvailable && _inventory!.MatchesCurrent(_project!, _device!, _inputs!)
        && _application!.MatchesInputs(_project!, _device!, _inputs!);
}

internal sealed class NativeGeneralTableEmission
{
    private readonly NativeImageInitializationPlan? _plan;
    private readonly NativeImageWorkingKernel.WorkingSeed? _seed;
    internal bool IsAvailable => Fingerprint is not null && Issues.Count == 0;
    internal IReadOnlyList<NativeGeneralIssue> Issues { get; }
    internal IReadOnlyList<GeneratedTablePayload> Payloads { get; }
    internal IReadOnlyList<GeneratedTableProcedureAdjustment> ProcedureAdjustments { get; }
    internal IReadOnlyList<string> Requirements { get; }
    internal string? Fingerprint { get; }
    internal GeneratedTableFormat Format { get; }
    internal string? CurrentSeedHash { get; }
    internal string? NativeWitnessHash { get; }
    private NativeGeneralTableEmission(NativeGeneralIssue issue)
    {
        Issues = Array.AsReadOnly(new[] { issue }); Payloads = Array.AsReadOnly(Array.Empty<GeneratedTablePayload>());
        ProcedureAdjustments = Array.AsReadOnly(Array.Empty<GeneratedTableProcedureAdjustment>());
        Requirements = Array.AsReadOnly(Array.Empty<string>());
    }
    private NativeGeneralTableEmission(NativeGeneralTablePreparation preparation, NativeImageInitializationPlan plan,
        NativeImageWorkingKernel.WorkingSeed seed, GeneratedTablePayload[] payloads, GeneratedTableProcedureAdjustment[] adjustments)
    {
        _plan = plan; _seed = seed; Issues = Array.AsReadOnly(Array.Empty<NativeGeneralIssue>());
        Format = preparation.Format; CurrentSeedHash = seed.Fingerprint; NativeWitnessHash = preparation.Witness!.Fingerprint;
        Payloads = Array.AsReadOnly(payloads); ProcedureAdjustments = Array.AsReadOnly(adjustments);
        Requirements = Array.AsReadOnly(new[] { "TablePhysicalMappingUnverified", "TableProcedureAdjustmentsNotApplied",
            "TableUnusedTailMaskUnresolved", "TableSecurityAndRfNotApplied" });
        Fingerprint = NativeGeneralHash.Of(new { version = 1, preparation = preparation.Fingerprint, plan = plan.Fingerprint,
            seed = seed.Fingerprint, adjustments, Requirements,
            payloads = payloads.Select(p => new { p.Role, p.Arena, p.ByteOffset, p.ResourceId, p.Length,
                data = p.Data.Sha256, writeMask = p.WriteMask.Sha256, before = p.BeforeData?.Sha256,
                beforeMask = p.BeforeMask?.Sha256, afterMask = p.AfterMask?.Sha256 }).ToArray() });
    }
    internal static NativeGeneralTableEmission Failure(NativeGeneralIssue issue) => new(issue);
    internal static NativeGeneralTableEmission Seal(NativeGeneralTablePreparation preparation, NativeImageInitializationPlan plan,
        NativeImageWorkingKernel.WorkingSeed seed, GeneratedTablePayload[] payloads, GeneratedTableProcedureAdjustment[] adjustments)
        => new(preparation, plan, seed, payloads, adjustments);
    internal bool BelongsTo(NativeImageInitializationPlan plan, NativeImageWorkingKernel.WorkingSeed seed)
        => IsAvailable && ReferenceEquals(plan, _plan) && ReferenceEquals(seed, _seed) && seed.MatchesPlan(plan);
}

internal static partial class GeneratedTableCompiler
{
    internal static NativeGeneralTablePreparation PrepareGeneral(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        NativeRootImageInventory inventory, NativeApplicationEvaluationContext application, GeneratedTableRequest request)
    {
        var format = request?.Format ?? GeneratedTableFormat.M112;
        try
        {
            if (request is null || !Enum.IsDefined(request.Format) || !inventory.BelongsTo(project, device, inputs)
                || !inventory.MatchesCurrent(project, device, inputs) || !application.MatchesInputs(project, device, inputs))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
            var small = request!.Format == GeneratedTableFormat.M112;
            var context = inventory.ResourceContext;
            if (context?.NumericMask != (small ? 0x0705 : 0x07B0)
                && !(small && inventory.AdmitRootUnions && context?.NumericMask == 0x0701))
                Fail(GeneratedTableIssueCode.UnsupportedTableFormat);
            var expected = small ? new[] { "AddressTable_Bcu1", "AssociationTable_M112", "GroupObjectTable_M112" }
                : new[] { "AddressTable_SystemB", "AssociationTable_SystemB", "GroupObjectTable_SystemB" };
            var names = new[] { "GroupAddressTable", "GroupAssociationTable", "GroupObjectTable" };
            for (var i = 0; i < names.Length; i++)
            {
                var resolution = context!.ResolveRemote(names[i], "lsm");
                if (resolution.Status != ArchivedResourceResolutionStatus.Found || resolution.Descriptor?.Flavour != expected[i])
                    Fail(GeneratedTableIssueCode.UnsupportedTableFormat, names[i]);
                if (!small && (resolution.Descriptor.Location is not { AddressSpace: ArchivedResourceAddressSpace.RelativeMemory, Offset: 0 } location
                    || location.InterfaceObjectRef != i + 1)) Fail(GeneratedTableIssueCode.UnsupportedTableFormat, names[i]);
            }
            var root = inputs.Application!.Root!; var ns = root.Name.Namespace;
            var app = Single(root.Descendants(ns + "ApplicationProgram"));
            if (small && (string?)app.Attribute("DynamicTableManagement") is { } management && XmlConvert.ToBoolean(management))
                Fail(GeneratedTableIssueCode.UnsupportedTableManagement);
            var stat = Single(app.Elements(ns + "Static"));
            var declarations = new[] { Single(stat.Elements(ns + "AddressTable")), Single(stat.Elements(ns + "AssociationTable")),
                Single(stat.Elements(ns + "ComObjectTable")) };
            var witness = NativeCommunicationWitness.CreateFromContext(project, device, inputs, application);
            if (!witness.IsAvailable) throw new NativeGeneralRejected(new(NativeGeneralIssueCode.TableUnavailable,
                ActivationIssue: witness.Issues.FirstOrDefault()));
            ValidateWitness(witness, request.Format);
            var lengths = GeneratedTableSerializer.Lengths(request.Format, witness);
            var sizes = new[] { lengths.Address, lengths.Association, lengths.Objects };
            if (sizes.Any(n => n > BlobLimit) || sizes.Sum(n => (long)n) > TotalBytes) Fail(GeneratedTableIssueCode.LimitExceeded);
            foreach (var (node, count) in new[] { (declarations[0], witness.Groups.Count), (declarations[1], witness.Associations.Count) })
                if (node.Attribute("MaxEntries") is not { } maximum || (ulong)count > Number(maximum.Value, uint.MaxValue))
                    Fail(GeneratedTableIssueCode.TableCapacityExceeded);
            var roles = new[] { GeneratedTableRole.GroupAddress, GeneratedTableRole.Association, GeneratedTableRole.CommunicationObject };
            var spans = declarations.Select((declaration, i) => GeneralSpan(declaration, inventory, small, i + 1, sizes[i], roles[i])).ToArray();
            if (small && spans[0].Offset != 0x4000) Fail(GeneratedTableIssueCode.UnsupportedTableFormat);
            for (var i = 0; i < spans.Length; i++)
                for (var j = i + 1; j < spans.Length; j++)
                    if (spans[i].Target == spans[j].Target && (ulong)spans[i].Offset < (ulong)spans[j].Offset + (uint)spans[j].Length
                        && (ulong)spans[j].Offset < (ulong)spans[i].Offset + (uint)spans[i].Length)
                        Fail(GeneratedTableIssueCode.AmbiguousResourceOwnership);
            if (!inventory.MatchesCurrent(project, device, inputs) || !application.MatchesInputs(project, device, inputs))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
            return NativeGeneralTablePreparation.Seal(project, device, inputs, inventory, application, request.Format, witness, spans);
        }
        catch (Rejected failure) { return NativeGeneralTablePreparation.Failure(new(NativeGeneralIssueCode.TableUnavailable, ActivationIssue: failure.Issue), format); }
        catch (NativeGeneralRejected failure) { return NativeGeneralTablePreparation.Failure(failure.Issue, format); }
        catch (Exception failure) when (failure is XmlException or ArgumentException or InvalidOperationException or OverflowException or FormatException)
        { return NativeGeneralTablePreparation.Failure(new(NativeGeneralIssueCode.TableUnavailable), format); }
    }

    private static NativeGeneralTableSpan GeneralSpan(XElement declaration, NativeRootImageInventory inventory,
        bool small, int lsm, int length, GeneratedTableRole role)
    {
        var id = (string?)declaration.Attribute("CodeSegment");
        var target = small ? new NativeImageTarget(NativeImageSpace.StandardMemory)
            : new NativeImageTarget(NativeImageSpace.RelativeMemory, checked((byte)lsm));
        ImageCompositionSourceSegment? resource = null; uint offset;
        if (id is not null)
        {
            if (!Safe(id)) Fail(GeneratedTableIssueCode.InvalidTableDeclaration);
            resource = inventory.CodeSources.SingleOrDefault(s => s.SourceId == id);
            if (resource is null || NativeImageWorkingKernel.Target(resource.Arena) != target) Fail(GeneratedTableIssueCode.MissingResource, id);
            if (declaration.Attribute("Offset") is not { } delta) Fail(GeneratedTableIssueCode.InvalidTableDeclaration);
            offset = checked(resource!.ByteOffsetInArena + (uint)Number(declaration.Attribute("Offset")!.Value, uint.MaxValue));
            if ((ulong)offset + (uint)length > (ulong)resource.ByteOffsetInArena + (uint)resource.AllocationLength)
                Fail(GeneratedTableIssueCode.MissingInitialData, id);
            if (!small && offset != 0) Fail(GeneratedTableIssueCode.UnsupportedTableFormat, id);
        }
        else
        {
            if (small || declaration.Attribute("Offset") is not null) Fail(GeneratedTableIssueCode.MissingResource);
            offset = 0;
        }
        if ((ulong)offset + (uint)length > 1UL << 32) Fail(GeneratedTableIssueCode.InvalidTableDeclaration);
        foreach (var other in inventory.CodeSources.Where(s => s.SourceId != id && NativeImageWorkingKernel.Target(s.Arena) == target))
            if ((ulong)other.ByteOffsetInArena < (ulong)offset + (uint)length
                && (ulong)offset < (ulong)other.ByteOffsetInArena + (uint)other.AllocationLength)
                Fail(GeneratedTableIssueCode.AmbiguousResourceOwnership, other.SourceId);
        return new(role, target, offset, length, id, NativeGeneralHash.Document(declaration));
    }

    internal static NativeGeneralTableEmission EmitGeneral(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        NativeRootImageInventory inventory, NativeImageVariantPreparation preparation, NativeImageVariantViewSet views,
        NativeApplicationEvaluationContext application, NativeRootWriterPopulation population,
        NativeGeneralTablePreparation tables, NativeImageInitializationPlan plan, NativeImageWorkingKernel.WorkingSeed seed,
        NativeImagePayloadLedger ledger)
    {
        try
        {
            if (!tables.BelongsTo(project, device, inputs, inventory, application) || !tables.MatchesCurrent()
                || !plan.BelongsTo(inventory, preparation, plan.Variant, views, application, population)
                || !plan.MatchesCurrent() || !plan.Variant.Tasks.Tables || !seed.MatchesPlan(plan))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
            var spans = tables.Spans.Select(s => seed.Read(s.Target, s.Offset, s.Length, s.ResourceId)).ToArray();
            if (spans.Any(s => s.Mask is null)) NativeGeneralHash.Fail(NativeGeneralIssueCode.MissingAllocation);
            // Serializer: Seed clone + writes + result-mask clone + five immutable role copies = eight bytes per input byte.
            ledger.Reserve(tables.EncoderPayloadReservation);
            var payloads = GeneratedTableSerializer.Encode(tables.Format, tables.Witness!, spans[0], spans[1], spans[2]);
            var procedure = plan.CompiledProcedure?.DeclarationPlan;
            var adjustments = procedure is null ? [] : Adjustments(procedure, tables.Format == GeneratedTableFormat.M112,
                GeneratedTableSerializer.Lengths(tables.Format, tables.Witness!));
            if (!tables.MatchesCurrent() || !plan.MatchesCurrent() || !seed.MatchesPlan(plan))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
            return NativeGeneralTableEmission.Seal(tables, plan, seed, payloads, adjustments);
        }
        catch (NativeGeneralRejected failure) { return NativeGeneralTableEmission.Failure(failure.Issue); }
        catch (Rejected failure) { return NativeGeneralTableEmission.Failure(new(NativeGeneralIssueCode.TableUnavailable, ActivationIssue: failure.Issue)); }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or InvalidDataException or OverflowException)
        { return NativeGeneralTableEmission.Failure(new(NativeGeneralIssueCode.TableUnavailable)); }
    }
}
