using System.Collections.Frozen;
using System.Xml;
using System.Xml.Linq;

namespace Knx;

internal sealed partial class NativeProcedureEvaluationContext
{
    private readonly NativeApplicationEvaluationContext? _sharedApplication;
    internal bool IsSharedApplicationProof => _sharedApplication is not null;
    internal string? SharedApplicationFingerprint { get; }
    internal string? SharedControllerSetHash { get; }
    internal string? SharedPlacementInventoryHash { get; }
    internal bool UsesSharedApplicationContext(NativeApplicationEvaluationContext application)
        => IsAvailable && ReferenceEquals(_sharedApplication, application);

    private NativeProcedureEvaluationContext(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        PreparedConditionalLoadProcedure prepared, NativeApplicationEvaluationContext application, string savedHash,
        string applicationHash, string? masterHash, string controllerSetHash, SharedPreparation projection,
        Dictionary<string, NativeProcedureControllerFact> controllers)
    {
        _project = project; _device = device; _inputs = inputs; _prepared = prepared;
        _savedHash = savedHash; _applicationHash = applicationHash; _masterHash = masterHash; _sharedApplication = application;
        SourceFingerprint = inputs.Manifest.Binding!.Fingerprint;
        SharedApplicationFingerprint = application.Fingerprint; SharedControllerSetHash = controllerSetHash;
        SharedPlacementInventoryHash = projection.InventoryHash;
        Controllers = controllers.ToFrozenDictionary(StringComparer.Ordinal); Issues = Array.AsReadOnly(Array.Empty<LoadProcedureIssue>());
        Fingerprint = Hash(new
        {
            version = 2, proof = "SharedApplication", SourceFingerprint, applicationHash, masterHash, savedHash,
            prepared.GraphFingerprint, prepared.SelectedProfileHash, prepared.Request, SharedApplicationFingerprint,
            SharedControllerSetHash, SharedPlacementInventoryHash, preparation = projection.Fingerprint,
            controllers = controllers.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray(),
        });
    }

    internal static NativeProcedureEvaluationContext FromApplicationContext(Project project, DeviceInstance device,
        ArchivedImageSources.Inputs inputs, PreparedConditionalLoadProcedure prepared, NativeApplicationEvaluationContext application)
    {
        try
        {
            if (project is null || device is null || inputs is null || prepared is null || application is null
                || !prepared.IsAvailable || !application.IsAvailable || inputs.Application is null || inputs.Manifest.Binding is null
                || !prepared.MatchesSources(inputs) || prepared.SourceFingerprint != application.SourceFingerprint
                || prepared.ApplicationDefinitionHash != application.ApplicationDefinitionHash)
                Fail(LoadProcedureIssueCode.ContextMismatch);
            if (!inputs.Manifest.Binding!.MatchesIdentity(project, device) || !application.MatchesInputs(project, device, inputs))
                Fail(LoadProcedureIssueCode.InputsChanged);
            if (application.ScriptCapabilityUsed || application.Snapshot is null || application.Catalog is null || application.Activation is null
                || application.CapturedInputs is null || application.Snapshot.ModuleInstances.Count != 0
                || !application.Activation.BelongsTo(application.Catalog, application.Snapshot))
                Fail(LoadProcedureIssueCode.ContextMismatch);
            var expected = prepared.ControllerReferences.Concat(application.Catalog!.StructuralPlan.CallControllerReferences)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(ControllerLimit + 1).ToArray();
            if (expected.Length > ControllerLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
            if (!application.AdditionalControllerReferences.SetEquals(expected)) Fail(LoadProcedureIssueCode.ContextMismatch);
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
                var inventory = projection.Placements.ToDictionary(p => p.SourceKey);
                foreach (var owner in application.Activation!.ActiveParameterOwners)
                {
                    SharedPlacement? placement = null;
                    if (owner.ModuleInstanceId.Length != 0 || !activeKeys.Add(owner.SourceKey)
                        || !inventory.TryGetValue(owner.SourceKey, out placement)) Fail(LoadProcedureIssueCode.ContextMismatch);
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
            if (!result.MatchesCurrent(prepared)) Fail(LoadProcedureIssueCode.InputsChanged);
            return result;
        }
        catch (Rejected failure) { return new(failure.Issue); }
        catch (Exception failure) when (failure is XmlException or FormatException or OverflowException or InvalidDataException or ArgumentException or InvalidOperationException)
        { return new(new(LoadProcedureIssueCode.ContextMismatch)); }
    }

    private static Dictionary<string, string> SharedSaved(IReadOnlyDictionary<string, string?> captured)
    {
        if (captured.Count > 100000) Fail(LoadProcedureIssueCode.LimitExceeded);
        var result = new Dictionary<string, string>(StringComparer.Ordinal); long characters = 0;
        foreach (var (key, value) in captured)
        {
            if (!Safe(key) || value is null || value.Length > 65536
                || (characters += key.Length + value.Length) > 4 * 1024 * 1024) Fail(LoadProcedureIssueCode.LimitExceeded);
            result.Add(key, value!);
        }
        return result;
    }

    private sealed record SharedPlacement(int SourceKey, string RefId, string Hash);
    private sealed record SharedController(string RefId, string ParameterId, string TypeId, int OwnerCount, long Value,
        string DeclarationHash, string PlacementHash);
    private sealed record SharedPreparation(IReadOnlyList<SharedPlacement> Placements, IReadOnlyList<SharedController> Controllers,
        string InventoryHash, string Fingerprint)
    {
        internal static SharedPreparation Empty() => new(Array.Empty<SharedPlacement>(), Array.Empty<SharedController>(), Hash(Array.Empty<object>()), Hash(Array.Empty<object>()));
    }
    private sealed partial class Source
    {
        internal static bool SharedCanonical(string? raw, out long value) => Canonical(raw, out value);
        internal SharedPreparation PrepareShared(IReadOnlyList<string> seeds, IReadOnlyDictionary<string, string> saved,
            NativeCommunicationOriginCatalog catalog)
        {
            var prepared = PrepareActivity(seeds, saved);
            if (catalog.Nodes.Count != _nodes.Length || !XNode.DeepEquals(_nodes[0], catalog.Nodes[0])) Fail(LoadProcedureIssueCode.ContextMismatch);
            var placements = new List<SharedPlacement>(); var placementByState = new Dictionary<int, SharedPlacement>();
            foreach (var (reference, owners) in prepared.Owners)
                foreach (var owner in owners)
                {
                    var element = prepared.States[owner].Element!; var key = _keys[element]; var original = catalog.At(key);
                    var origin = original?.Annotation<NativeCommunicationElementOrigin>();
                    if (original?.Name != _ns + "ParameterRefRef" || (string?)original.Attribute("RefId") != reference
                        || origin is not { Complete: true, Frames.Count: 0 } || origin.Key != key
                        || !XNode.DeepEquals(element, original)) Fail(LoadProcedureIssueCode.ContextMismatch, reference);
                    var placement = new SharedPlacement(key, reference, DocumentHash(element, 128 * 1024 * 1024));
                    placements.Add(placement); placementByState.Add(owner, placement);
                }
            var controllers = new List<SharedController>();
            foreach (var id in prepared.Relevant.Order(StringComparer.Ordinal))
            {
                var declaration = Declaration(id); var owners = prepared.Owners[id];
                var hashes = owners.Select(k => placementByState[k]).ToArray();
                controllers.Add(new(id, declaration.ParameterId, declaration.TypeId, owners.Count, prepared.Values[id], declaration.Hash, Hash(hashes)));
            }
            var ordered = placements.OrderBy(p => p.SourceKey).ToArray(); var inventoryHash = Hash(ordered);
            var fingerprint = Hash(new
            {
                inventoryHash, controllers,
                states = prepared.Used.Order().Select(k => new
                {
                    key = k, prepared.States[k].Kind, prepared.States[k].RefId, prepared.States[k].Parent, prepared.States[k].Controller,
                    prepared.States[k].Default, prepared.States[k].Matches, prepared.States[k].Dependencies,
                    sourceKey = prepared.States[k].Element is null ? (int?)null : _keys[prepared.States[k].Element!],
                }).ToArray(),
            });
            return new(Array.AsReadOnly(ordered), Array.AsReadOnly(controllers.ToArray()), inventoryHash, fingerprint);
        }
    }
}
