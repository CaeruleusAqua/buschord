using System.Xml;
using System.Xml.Linq;

namespace Knx;

internal sealed partial class NativeProcedureEvaluationContext
{
    internal static NativeProcedureEvaluationContext FromGeneralImageContext(Project project, DeviceInstance device,
        ArchivedImageSources.Inputs inputs, NativeRootImageInventory inventory, NativeImageVariantPreparation preparation,
        PreparedConditionalLoadProcedure prepared, NativeApplicationEvaluationContext application)
    {
        try
        {
            if (project is null || device is null || inputs is null || prepared is null || application is null
                || inventory is null || preparation is null || !inventory.BelongsTo(project, device, inputs)
                || !preparation.BelongsTo(project, device, inputs, inventory)
                || !preparation.Variants.Any(v => ReferenceEquals(preparation.GetProcedurePreparation(v), prepared))
                || !prepared.IsAvailable || !application.IsAvailable || inputs.Application is null || inputs.Manifest.Binding is null
                || !prepared.MatchesSources(inputs) || prepared.SourceFingerprint != application.SourceFingerprint
                || prepared.ApplicationDefinitionHash != application.ApplicationDefinitionHash)
                Fail(LoadProcedureIssueCode.ContextMismatch);
            if (!inputs.Manifest.Binding!.MatchesIdentity(project, device) || !application.MatchesInputs(project, device, inputs))
                Fail(LoadProcedureIssueCode.InputsChanged);
            if (application.ScriptCapabilityUsed || application.Snapshot is null || application.Catalog is null || application.Activation is null
                || application.CapturedInputs is null || !inventory.MatchesModules(application)
                || !application.Activation.BelongsTo(application.Catalog, application.Snapshot))
                Fail(LoadProcedureIssueCode.ContextMismatch);
            var expected = preparation.ControllerReferences.Concat(application.Catalog!.StructuralPlan.CallControllerReferences)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(ControllerLimit + 1).ToArray();
            if (expected.Length > ControllerLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
            if (!application.AdditionalControllerReferences.SetEquals(expected)
                || !prepared.ControllerReferences.All(preparation.ControllerReferences.Contains)) Fail(LoadProcedureIssueCode.ContextMismatch);
            var saved = SharedSaved(application.CapturedInputs!.Values);
            var savedHash = Hash(saved.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray());
            var applicationHash = DocumentHash(inputs.Application!, 128 * 1024 * 1024);
            var masterHash = inputs.Master is null ? null : DocumentHash(inputs.Master, 16 * 1024 * 1024);
            var source = prepared.DeclaredControllerReferences.Count != 0 || prepared.ControllerReferences.Count != 0
                ? new Source(new XDocument(inputs.Application!)) : null;
            foreach (var id in prepared.DeclaredControllerReferences) source!.Declaration(id);
            var projection = prepared.ControllerReferences.Count == 0 ? SharedPreparation.Empty()
                : source!.PrepareShared(prepared.ControllerReferences, saved, application.Catalog!);
            var controllers = new Dictionary<string, NativeProcedureControllerFact>(StringComparer.Ordinal);
            var activeKeys = new HashSet<int>(); var activeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (prepared.ControllerReferences.Count != 0)
            {
                var placements = projection.Placements.ToDictionary(p => p.SourceKey);
                foreach (var owner in application.Activation!.ActiveParameterOwners.Where(owner => owner.ModuleInstanceId.Length == 0))
                {
                    SharedPlacement? placement = null;
                    if (owner.ModuleInstanceId.Length != 0 || !activeKeys.Add(owner.SourceKey)
                        || !placements.TryGetValue(owner.SourceKey, out placement)) Fail(LoadProcedureIssueCode.ContextMismatch);
                    activeCounts[placement!.RefId] = checked(activeCounts.GetValueOrDefault(placement.RefId) + 1);
                }
                foreach (var declaration in projection.Controllers)
                {
                    NativeControllerActivity? activity = null; string? raw = null;
                    if (!application.Activation.Controllers.TryGetValue(declaration.RefId, out activity)
                        || !application.EffectiveValues.TryGetValue(declaration.RefId, out raw)) Fail(LoadProcedureIssueCode.ContextMismatch, declaration.RefId);
                    var count = activeCounts.GetValueOrDefault(declaration.RefId);
                    if (activity!.RefId != declaration.RefId || activity.NormalReferenceCount != count || activity.ValueAssignmentReferenceCount != 0
                        || activity.Active != (count > 0) || activity.Value != declaration.Value
                        || !Source.SharedCanonical(raw, out var value) || value != declaration.Value)
                        Fail(LoadProcedureIssueCode.ContextMismatch, declaration.RefId);
                    controllers.Add(declaration.RefId, new(declaration.RefId, declaration.ParameterId, declaration.TypeId,
                        declaration.OwnerCount, count, count != 0, count == 0 ? null : declaration.Value,
                        declaration.DeclarationHash, declaration.PlacementHash));
                }
            }
            var result = new NativeProcedureEvaluationContext(project, device, inputs, prepared, application, savedHash,
                applicationHash, masterHash, Hash(expected), projection, controllers);
            if (!result.MatchesCurrent(prepared) || !inventory.MatchesCurrent(project, device, inputs) || !preparation.MatchesCurrent())
                Fail(LoadProcedureIssueCode.InputsChanged);
            return result;
        }
        catch (Rejected failure) { return new(failure.Issue); }
        catch (Exception failure) when (failure is XmlException or FormatException or OverflowException or InvalidDataException or ArgumentException or InvalidOperationException)
        { return new(new(LoadProcedureIssueCode.ContextMismatch)); }
    }

}
