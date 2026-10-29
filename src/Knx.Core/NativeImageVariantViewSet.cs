using System.Collections.ObjectModel;

namespace Knx;

/// <summary>Sealed per-procedure views of one complete, paired application evaluation.</summary>
internal sealed class NativeImageVariantViewSet
{
    private readonly Project? _project;
    private readonly DeviceInstance? _device;
    private readonly ArchivedImageSources.Inputs? _inputs;
    private readonly NativeRootImageInventory? _inventory;
    private readonly NativeImageVariantPreparation? _preparation;
    private readonly NativeApplicationEvaluationContext? _application;
    private readonly IReadOnlyDictionary<NativeImageVariant, NativeProcedureEvaluationContext> _views;
    internal bool IsAvailable => Fingerprint is not null && Issues.Count == 0;
    internal IReadOnlyList<NativeGeneralIssue> Issues { get; }
    internal string? Fingerprint { get; }

    private NativeImageVariantViewSet(NativeGeneralIssue issue)
    {
        Issues = Array.AsReadOnly(new[] { issue });
        _views = new ReadOnlyDictionary<NativeImageVariant, NativeProcedureEvaluationContext>(
            new Dictionary<NativeImageVariant, NativeProcedureEvaluationContext>(ReferenceEqualityComparer.Instance));
    }
    private NativeImageVariantViewSet(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        NativeRootImageInventory inventory, NativeImageVariantPreparation preparation,
        NativeApplicationEvaluationContext application, Dictionary<NativeImageVariant, NativeProcedureEvaluationContext> views)
    {
        _project = project; _device = device; _inputs = inputs; _inventory = inventory;
        _preparation = preparation; _application = application;
        _views = new ReadOnlyDictionary<NativeImageVariant, NativeProcedureEvaluationContext>(views);
        Issues = Array.AsReadOnly(Array.Empty<NativeGeneralIssue>());
        Fingerprint = NativeGeneralHash.Of(new { version = 1, inventory = inventory.SourceHash,
            preparation = preparation.Fingerprint, application = application.Fingerprint,
            views = preparation.Variants.Select(v => new { v.Ordinal, v.Fingerprint,
                context = views.GetValueOrDefault(v)?.Fingerprint }).ToArray() });
    }
    internal static NativeImageVariantViewSet Create(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        NativeRootImageInventory inventory, NativeImageVariantPreparation preparation, NativeApplicationEvaluationContext application)
    {
        try
        {
            if (!inventory.BelongsTo(project, device, inputs) || !preparation.BelongsTo(project, device, inputs, inventory)
                || !application.IsAvailable || !application.MatchesInputs(project, device, inputs)
                || application.ScriptCapabilityUsed || application.Catalog is null || application.Activation is null
                || application.Snapshot is null || !inventory.MatchesModules(application)
                || !application.Activation.BelongsTo(application.Catalog, application.Snapshot))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
            var expected = preparation.ControllerReferences.Concat(application.Catalog!.StructuralPlan.CallControllerReferences)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(4097).ToArray();
            if (expected.Length > 4096) NativeGeneralHash.Fail(NativeGeneralIssueCode.LimitExceeded);
            if (!application.AdditionalControllerReferences.SetEquals(expected))
                NativeGeneralHash.Fail(NativeGeneralIssueCode.ContextMismatch);
            var views = new Dictionary<NativeImageVariant, NativeProcedureEvaluationContext>(ReferenceEqualityComparer.Instance);
            var shared = new Dictionary<PreparedConditionalLoadProcedure, NativeProcedureEvaluationContext>(ReferenceEqualityComparer.Instance);
            foreach (var variant in preparation.Variants)
            {
                var prepared = preparation.GetProcedurePreparation(variant);
                if (prepared is null) continue;
                if (!shared.TryGetValue(prepared, out var view))
                {
                    view = NativeProcedureEvaluationContext.FromGeneralImageContext(project, device, inputs, inventory,
                        preparation, prepared, application);
                    if (!view.IsAvailable)
                        throw new NativeGeneralRejected(new(NativeGeneralIssueCode.ContextMismatch,
                            VariantOrdinal: variant.Ordinal, ProcedureIssue: view.Issues.FirstOrDefault()));
                    shared.Add(prepared, view);
                }
                views.Add(variant, view);
            }
            var result = new NativeImageVariantViewSet(project, device, inputs, inventory, preparation, application, views);
            if (!result.MatchesCurrent()) NativeGeneralHash.Fail(NativeGeneralIssueCode.InputsChanged);
            return result;
        }
        catch (NativeGeneralRejected failure) { return new(failure.Issue); }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or OverflowException)
        { return new(new(NativeGeneralIssueCode.ContextMismatch)); }
    }
    internal NativeProcedureEvaluationContext? GetView(NativeImageVariant variant)
        => IsAvailable && _preparation!.Variants.Any(v => ReferenceEquals(v, variant))
            ? _views.GetValueOrDefault(variant) : null;
    internal bool BelongsTo(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        NativeRootImageInventory inventory, NativeImageVariantPreparation preparation, NativeApplicationEvaluationContext application)
        => IsAvailable && ReferenceEquals(_project, project) && ReferenceEquals(_device, device)
            && ReferenceEquals(_inputs, inputs) && ReferenceEquals(_inventory, inventory)
            && ReferenceEquals(_preparation, preparation) && ReferenceEquals(_application, application);
    internal bool MatchesCurrent() => IsAvailable && _inventory!.MatchesCurrent(_project!, _device!, _inputs!) && _preparation!.MatchesCurrent()
        && _application!.MatchesInputs(_project!, _device!, _inputs!)
        && _preparation.Variants.All(v => _preparation.GetProcedurePreparation(v) is not { } prepared
            || _views.TryGetValue(v, out var view) && view.MatchesCurrent(prepared));
}
