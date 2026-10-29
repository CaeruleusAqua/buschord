using System.Xml;

namespace Knx;

internal sealed class NativeGeneralImageAlternative
{
    internal NativeImageVariant Variant { get; }
    internal bool IsAvailable => Fingerprint is not null && Issues.Count == 0;
    internal IReadOnlyList<NativeGeneralIssue> Issues { get; }
    internal NativeImageInitializationPlan? Initialization { get; }
    internal NativeRootVariantPopulation? Population { get; }
    internal NativeGeneralTableEmission? Tables { get; }
    internal IReadOnlyList<NativeGeneralWorkingChunk> Chunks { get; }
    internal IReadOnlyList<string> Requirements { get; }
    internal string? Fingerprint { get; }
    internal NativeGeneralImageAlternative(NativeImageVariant variant, NativeGeneralIssue issue)
    {
        Variant = variant; Issues = Array.AsReadOnly(new[] { issue });
        Chunks = Array.AsReadOnly(Array.Empty<NativeGeneralWorkingChunk>());
        Requirements = Array.AsReadOnly(Array.Empty<string>());
    }
    internal NativeGeneralImageAlternative(NativeImageInitializationPlan initialization,
        NativeRootVariantPopulation population, NativeGeneralTableEmission? tables,
        IReadOnlyList<NativeGeneralWorkingChunk> chunks, string[] requirements)
    {
        Variant = initialization.Variant; Initialization = initialization; Population = population;
        Tables = tables;
        Chunks = chunks; Requirements = Array.AsReadOnly(requirements); Issues = Array.AsReadOnly(Array.Empty<NativeGeneralIssue>());
        Fingerprint = NativeGeneralHash.Of(new { version = 1, variant = Variant.Fingerprint,
            initialization = initialization.Fingerprint, population = population.PopulationHash, tables = tables?.Fingerprint,
            population.GeometryHash, population.MaskInteractionHash,
            chunks = chunks.Select(c => new { c.Range, c.Sources, initial = c.InitialData.Sha256,
                initialValidity = c.InitialValidity?.Sha256, working = c.WorkingData.Sha256,
                validity = c.DesiredValidity?.Sha256, canonical = c.CanonicalDesired?.Sha256,
                parameterWrites = c.ParameterWriteBits.Sha256 }).ToArray(), Requirements });
    }
}

internal sealed class NativeGeneralImageBuildPlan
{
    internal bool HasGlobalFailure { get; }
    internal bool IsAvailable => !HasGlobalFailure && Binding is not null && Variants.Any(v => v.IsAvailable);
    internal bool AllAssumptionsComplete => IsAvailable && Variants.All(v => v.IsAvailable);
    internal bool HasActualRoute => false;
    internal bool CompleteApplicationImage => false;
    internal bool DownloadEligible => false;
    internal bool ExecutionEligible => false;
    internal IReadOnlyList<NativeGeneralIssue> Issues { get; }
    internal IReadOnlyList<NativeGeneralImageAlternative> Variants { get; }
    internal NativeGeneralImageBinding? Binding { get; }
    internal string? Fingerprint { get; }
    internal long AllocatedImagePayloadBytes { get; }
    internal long ReservedWorstCaseImagePayloadBytes { get; }
    internal long ReservedProcedurePayloadBytes { get; }
    internal string? SourceFingerprint { get; }
    internal string? SourceHash { get; }
    internal string? ApplicationId { get; }
    internal string? MaskVersionId { get; }
    internal string? ApplicationDefinitionHash { get; }
    internal string? HardwareDefinitionHash { get; }
    internal string? PreparationHash { get; }
    internal string? SharedContextHash { get; }
    internal string? ProjectInputHash { get; }
    internal NativeRootSourcePolicy? SourcePolicy { get; }
    internal NativeRootCodecFacts? CodecFacts { get; }
    internal int DeclaredReferenceCount { get; }
    internal int DeclaredOwnerCount { get; }
    private NativeGeneralImageBuildPlan(NativeGeneralIssue issue)
    {
        HasGlobalFailure = true; Issues = Array.AsReadOnly(new[] { issue });
        Variants = Array.AsReadOnly(Array.Empty<NativeGeneralImageAlternative>());
    }
    private NativeGeneralImageBuildPlan(Project project, DeviceInstance device, uint[] options, GeneratedTableRequest? tables,
        NativeRootImageInventory inventory, NativeImageVariantPreparation preparation, NativeApplicationEvaluationContext application,
        NativeImageInitializationSet initialization, NativeGeneralImageAlternative[] variants, long allocated, long reserved)
    {
        Issues = Array.AsReadOnly(Array.Empty<NativeGeneralIssue>()); Variants = Array.AsReadOnly(variants);
        SourceFingerprint = inventory.SourceFingerprint; SourceHash = inventory.SourceHash; ApplicationId = inventory.ApplicationId;
        MaskVersionId = inventory.MaskVersionId;
        ApplicationDefinitionHash = inventory.ApplicationDefinitionHash; HardwareDefinitionHash = inventory.HardwareDefinitionHash;
        PreparationHash = preparation.Fingerprint; SharedContextHash = application.Fingerprint;
        ProjectInputHash = application.ProjectInputHash;
        SourcePolicy = inventory.Policy; CodecFacts = inventory.CodecFacts;
        DeclaredReferenceCount = inventory.References.Count; DeclaredOwnerCount = inventory.Owners.Count;
        AllocatedImagePayloadBytes = allocated; ReservedWorstCaseImagePayloadBytes = reserved;
        ReservedProcedurePayloadBytes = preparation.ReservedProcedurePayloadBytes;
        Fingerprint = NativeGeneralHash.Of(new { profile = 1, source = inventory.SourceHash,
            inventory.GeometryHash, sourcePolicy = inventory.Policy!.Fingerprint, preparation = preparation.Fingerprint,
            application = application.Fingerprint, initialization = initialization.Fingerprint, options, tables,
            variants = variants.Select(v => new { v.Variant.Ordinal, variant = v.Variant.Fingerprint,
                v.IsAvailable, v.Issues, result = v.Fingerprint }).ToArray(),
            allocated, reserved, ReservedProcedurePayloadBytes, HasActualRoute, CompleteApplicationImage });
        if (variants.Any(v => v.IsAvailable)) Binding = new(project, device, options, tables, Fingerprint, inventory.AdmitRootUnions);
    }
    internal static NativeGeneralImageBuildPlan Unavailable(NativeGeneralIssue issue) => new(issue);
    internal static NativeGeneralImageBuildPlan Seal(Project project, DeviceInstance device, uint[] options, GeneratedTableRequest? tables,
        NativeRootImageInventory inventory, NativeImageVariantPreparation preparation, NativeApplicationEvaluationContext application,
        NativeImageInitializationSet initialization, NativeGeneralImageAlternative[] variants, long allocated, long reserved)
        => new(project, device, options, tables, inventory, preparation, application, initialization, variants, allocated, reserved);
}

internal sealed class NativeGeneralImageBinding
{
    private readonly Project _project;
    private readonly DeviceInstance _device;
    private readonly uint[] _options;
    private readonly GeneratedTableRequest? _tables;
    private readonly bool _admitRootUnions;
    internal string Fingerprint { get; }
    internal NativeGeneralImageBinding(Project project, DeviceInstance device, uint[] options, GeneratedTableRequest? tables, string fingerprint, bool admitRootUnions = false)
    { _project = project; _device = device; _options = (uint[])options.Clone(); _tables = tables is null ? null : tables with { }; Fingerprint = fingerprint; _admitRootUnions = admitRootUnions; }
    internal bool IsCurrent(Project project, DeviceInstance device) => ReferenceEquals(project, _project) && ReferenceEquals(device, _device)
        && NativeGeneralImageBuild.BuildProfile(project, device, _options, _tables, _admitRootUnions) is { IsAvailable: true } current && current.Fingerprint == Fingerprint;
}

/// <summary>One source capture and evaluation for all explicitly conditional option alternatives.</summary>
internal static class NativeGeneralImageBuild
{
    internal static NativeGeneralImageBuildPlan Build(Project project, DeviceInstance device,
        IReadOnlyList<uint> optionAssumptions, GeneratedTableRequest? tables = null)
        => BuildProfile(project, device, optionAssumptions, tables, false);

    internal static NativeGeneralImageBuildPlan BuildProfile(Project project, DeviceInstance device,
        IReadOnlyList<uint> optionAssumptions, GeneratedTableRequest? tables, bool admitRootUnions)
    {
        try
        {
            if (project is null || device is null || optionAssumptions is null || optionAssumptions.Count is < 1 or > 16
                || tables is not null && !Enum.IsDefined(tables.Format))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidRequest);
            var options = optionAssumptions!.Take(17).ToArray();
            if (options.Length is < 1 or > 16 || options.Distinct().Count() != options.Length)
                NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidRequest);
            if (!OfflineRootImageBuild.WithinBudget(project!, device!))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidRequest);
            if (device!.HasOpaqueBinaryData || device.BinaryData.Count != 0)
                NativeGeneralHash.Fail(NativeGeneralIssueCode.UnaccountedBinaryData);
            var request = tables is null ? null : tables with { };
            var inputs = ArchivedImageSources.ReadInputs(project!, device!);
            if (admitRootUnions && (options.Length != 1 || options[0] != 255 || request?.Format != GeneratedTableFormat.M112))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.InvalidRequest);
            var inventory = NativeRootImageInventory.ReadProfile(project!, device!, inputs, admitRootUnions);
            if (!inventory.IsAvailable) throw new NativeGeneralRejected(inventory.Issues.First());
            var ledger = new NativeImagePayloadLedger();
            ledger.Reserve(inventory.PeakPayloadBytes);
            var preparation = NativeImagePreparation.Prepare(project!, device!, inputs, inventory, options);
            if (!preparation.IsAvailable) throw new NativeGeneralRejected(preparation.Issues.First());
            var application = NativeApplicationEvaluationContext.CreateProfile(project!, device!, inputs,
                preparation.ControllerReferences.ToArray(), null, admitRootUnions);
            if (!application.IsAvailable) throw new NativeGeneralRejected(new(NativeGeneralIssueCode.ContextMismatch,
                ActivationIssue: application.Issues.FirstOrDefault()));
            var views = NativeImageVariantViewSet.Create(project!, device!, inputs, inventory, preparation, application);
            if (!views.IsAvailable) throw new NativeGeneralRejected(views.Issues.First());
            var population = NativeRootWriterPopulation.Create(inventory, preparation, application);
            if (!population.IsAvailable) throw new NativeGeneralRejected(population.Issues.First());
            var initialization = NativeImageInitializer.CompileAll(project!, device!, inputs, inventory, preparation, views, application, population);
            if (initialization.HasGlobalFailure) throw new NativeGeneralRejected(initialization.Issues.First());
            if (initialization.Variants.Count != preparation.Variants.Count
                || initialization.Variants.Where((p, i) => !ReferenceEquals(p.Variant, preparation.Variants[i])).Any())
                NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
            var tablePreparation = request is not null && preparation.Variants.Any(v => v.Tasks.Tables)
                ? GeneratedTableCompiler.PrepareGeneral(project!, device!, inputs, inventory, application, request) : null;
            var reserved = checked(ledger.AllocatedBytes + initialization.Variants.Where(p => p.IsAvailable)
                .Sum(p => checked(p.ReservedMaterializationPayloadBytes
                    + (p.Variant.Tasks.Tables && tablePreparation?.IsAvailable == true
                        ? p.EstimatedMaterializedPayloadBytes * 2 + tablePreparation.Spans.Sum(s => (long)s.Length) * 10 : 0))));
            if (reserved > NativeImagePayloadLedger.Limit) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
            var alternatives = new List<NativeGeneralImageAlternative>();
            foreach (var plan in initialization.Variants)
            {
                if (!plan.IsAvailable) { alternatives.Add(new(plan.Variant, plan.Issues.First())); continue; }
                try
                {
                    if (!plan.BelongsTo(inventory, preparation, plan.Variant, views, application, population) || !plan.MatchesCurrent())
                        NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
                    var variantPopulation = population.ForVariant(plan.Variant);
                    if (!variantPopulation.IsAvailable) throw new NativeGeneralRejected(variantPopulation.Issues.First());
                    var kernel = NativeImageWorkingKernel.Initialize(plan, ledger);
                    kernel.Apply(variantPopulation, inventory.Policy!.ByteOrder);
                    NativeGeneralTableEmission? emittedTables = null;
                    if (request is not null && plan.Variant.Tasks.Tables)
                    {
                        if (tablePreparation?.IsAvailable != true)
                            throw new NativeGeneralRejected(tablePreparation?.Issues.FirstOrDefault() ?? new(NativeGeneralIssueCode.TableUnavailable));
                        var seed = NativeImageWorkingKernel.WorkingSeed.Capture(kernel, plan);
                        emittedTables = GeneratedTableCompiler.EmitGeneral(project!, device!, inputs, inventory, preparation, views,
                            application, population, tablePreparation!, plan, seed, ledger);
                        if (!emittedTables.IsAvailable) throw new NativeGeneralRejected(emittedTables.Issues.First());
                        kernel.ApplyTables(emittedTables, seed);
                    }
                    var requirements = plan.Requirements
                        .Concat(plan.Variant.Tasks.Tables && emittedTables is null ? new[] { "CommunicationTablesNotApplied" } : [])
                        .Concat(emittedTables?.Requirements ?? [])
                        .Append("ActualOptionsUnverified").Distinct(StringComparer.Ordinal).ToArray();
                    var chunks = kernel.Complete();
                    if (!plan.MatchesCurrent()) NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
                    alternatives.Add(new(plan, variantPopulation, emittedTables, chunks, requirements));
                }
                catch (NativeGeneralRejected failure) when (failure.Issue.Code is not (NativeGeneralIssueCode.InputsChanged or NativeGeneralIssueCode.LimitExceeded or NativeGeneralIssueCode.ContextMismatch))
                { alternatives.Add(new(plan.Variant, failure.Issue with { VariantOrdinal = plan.Variant.Ordinal })); }
            }
            if (!inventory.MatchesCurrent(project!, device!, inputs) || !preparation.MatchesCurrent()
                || !views.MatchesCurrent() || !population.MatchesCurrent()) NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
            return NativeGeneralImageBuildPlan.Seal(project!, device!, options, request, inventory, preparation, application,
                initialization, alternatives.ToArray(), ledger.AllocatedBytes, reserved);
        }
        catch (NativeGeneralRejected failure) { return NativeGeneralImageBuildPlan.Unavailable(failure.Issue); }
        catch (Exception failure) when (failure is XmlException or IOException or InvalidDataException or ArgumentException
            or InvalidOperationException or FormatException or OverflowException)
        { return NativeGeneralImageBuildPlan.Unavailable(new(NativeGeneralIssueCode.InvalidDeclaration)); }
    }
}
