using System.Text.Json;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private static string ResolvedCode<T>(T value) where T : Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static object? ResolvedProvenance(LoadProcedureSourceLocation? source) => source is null ? null : new
    {
        sourceOrigin = ResolvedCode(source.SourceOrigin), sourcePath = source.SourcePath,
        templateOrigin = source.TemplateOrigin is { } origin ? ResolvedCode(origin) : null,
        templatePath = source.TemplatePath, templateOrdinal = source.TemplateOrdinal,
        mergeId = source.MergeId, expansionOrdinal = source.ExpansionOrdinal,
        ancestorDecisionOrdinals = source.AncestorDecisionOrdinals,
    };

    private static object ResolvedApplicationBuildDiagnostic(Project project, DeviceInstance device, LoadProcedureRequest request)
    {
        const int controlLimit = 512, mergeLimit = 256, resourceLimit = 64, decisionLimit = 512;
        var plan = OfflineApplicationBuildPlan.PreviewResolved(project, device, request);
        var procedure = plan.Procedure;
        static object? Arena(ResourcePatchArena? arena) => arena is null ? null : new
        { kind = ResolvedCode(arena.Kind), loadStateMachine = arena.LoadStateMachine };
        return new
        {
            scope = plan.Scope, profileVersion = plan.ProfileVersion, available = plan.IsAvailable, readOnly = true,
            executionEligible = plan.ExecutionEligible, completeApplicationImage = plan.CompleteApplicationImage,
            downloadEligible = plan.DownloadEligible,
            request = new
            {
                subtype = request.NativeSubtype, access = "remote", full = request.Parts.HasFlag(LoadParts.Full),
                parameters = request.Parts.HasFlag(LoadParts.Parameters), groups = request.Parts.HasFlag(LoadParts.Groups),
                resolveConditions = true,
            },
            sources = ArchivedImageSourceDiagnostic(plan.Sources),
            binding = plan.Binding is { } binding ? new
            {
                profileVersion = binding.ProfileVersion, sourceFingerprint = binding.SourceFingerprint,
                procedureWitnessHash = binding.ProcedureWitnessHash, procedureGraphHash = binding.ProcedureGraphHash,
                evaluationContextHash = binding.EvaluationContextHash, projectInputHash = binding.ProjectInputHash,
                fingerprint = binding.Fingerprint,
            } : null,
            issues = plan.Issues.Select(issue => new
            { code = ResolvedCode(issue.Code), referenceId = issue.ReferenceId, sourceOrdinal = issue.SourceOrdinal }).ToArray(),
            procedure = procedure is null ? null : new
            {
                profileVersion = procedure.ProfileVersion, scope = procedure.Scope, available = procedure.IsAvailable,
                applicationId = procedure.ApplicationId, maskVersionId = procedure.MaskVersionId,
                style = procedure.Style is { } style ? ResolvedCode(style) : null,
                applicationDefinitionHash = procedure.ApplicationDefinitionHash, selectedProfileHash = procedure.SelectedProfileHash,
                witnessHash = procedure.WitnessHash, controlCount = procedure.Controls.Count, controlLimit,
                controlsTruncated = procedure.Controls.Count > controlLimit,
                controls = procedure.Controls.Take(controlLimit).Select(control => new
                {
                    ordinal = control.Ordinal, kind = ResolvedCode(control.Kind), origin = ResolvedCode(control.Origin),
                    sourceOrdinal = control.SourceOrdinal, templateOrdinal = control.TemplateOrdinal, mergeId = control.MergeId,
                    declaredAppliesTo = ResolvedCode(control.DeclaredAppliesTo), effectiveAppliesTo = ResolvedCode(control.EffectiveAppliesTo),
                    provenance = ResolvedProvenance(control.Provenance),
                    arguments = control.Arguments.Select(argument => new
                    {
                        name = argument.Name, kind = ResolvedCode(argument.Kind), unsignedValue = argument.UnsignedValue,
                        booleanValue = argument.BooleanValue, textValue = argument.TextValue,
                    }).ToArray(),
                    onError = control.OnError.Select(error => new
                    { cause = ResolvedCode(error.Cause), ignore = error.Ignore, messageRef = error.MessageRef }).ToArray(),
                    inlineDataLength = control.InlineData?.Length, inlineDataHash = control.InlineData?.Sha256,
                    inlineMaskLength = control.InlineMask?.Length, inlineMaskHash = control.InlineMask?.Sha256,
                }).ToArray(),
                conditionalSelection = procedure.ConditionalSelection is { } selection ? new
                {
                    profileVersion = selection.ProfileVersion, controllerWitnessHash = selection.ControllerWitnessHash,
                    decisionCount = selection.Decisions.Count, decisionLimit, decisionsTruncated = selection.Decisions.Count > decisionLimit,
                    decisions = selection.Decisions.Take(decisionLimit).Select(decision => new
                    {
                        ordinal = decision.Ordinal, source = ResolvedProvenance(decision.Source),
                        controllerRefId = decision.ControllerRefId, controllerActive = decision.ControllerActive,
                        controllerValue = decision.ControllerValue, selectedWhenOrdinals = decision.SelectedWhenOrdinals,
                        usedDefault = decision.UsedDefault,
                    }).ToArray(),
                } : null,
                mergeDecisionCount = procedure.MergeDecisions.Count, mergeLimit,
                mergesTruncated = procedure.MergeDecisions.Count > mergeLimit,
                mergeDecisions = procedure.MergeDecisions.Take(mergeLimit).Select(merge => new
                {
                    mergeId = merge.MergeId, templateOrdinal = merge.TemplateOrdinal,
                    decision = ResolvedCode(merge.Decision), emittedControls = merge.EmittedControls,
                    provenance = ResolvedProvenance(merge.Provenance),
                }).ToArray(),
                resourceCount = procedure.ResourceReferences.Count, resourceLimit,
                resourcesTruncated = procedure.ResourceReferences.Count > resourceLimit,
                resources = procedure.ResourceReferences.Take(resourceLimit).Select(resource => new
                {
                    id = resource.Id, arena = Arena(resource.Arena), baseOffsetInArena = resource.BaseOffset,
                    declaredSize = resource.DeclaredSize, dataLength = resource.DataLength, maskLength = resource.MaskLength,
                    dataHash = resource.DataHash, maskHash = resource.MaskHash,
                }).ToArray(),
                tableRequirements = procedure.TableRequirements.Select(table => new
                {
                    role = ResolvedCode(table.Role), codeSegmentId = table.CodeSegmentId, offsetInResource = table.Offset,
                    declaredMaxEntries = table.MaxEntries, arena = Arena(table.Arena), byteOffsetInArena = table.ByteOffsetInArena,
                    maskResourceFlavour = table.MaskResourceFlavour,
                    maskResourceLocation = table.MaskResourceLocation is { } location ? new
                    {
                        addressSpace = ResolvedCode(location.AddressSpace), byteOffset = location.Offset,
                        nativeObjectIndex = location.NativeObjectIndex, applicationObjectIndex = location.ApplicationObjectIndex,
                        objectType = location.ObjectType, occurrence = location.Occurrence, propertyId = location.PropertyId,
                        pointerResource = location.PointerResource, interfaceObjectRef = location.InterfaceObjectRef,
                    } : null,
                }).ToArray(),
                unresolvedRequirements = procedure.Requirements.Select(requirement => new
                { code = ResolvedCode(requirement.Code), referenceId = requirement.ReferenceId }).ToArray(),
                executionEligible = procedure.ExecutionEligible, completeApplicationImage = procedure.CompleteApplicationImage,
                downloadEligible = procedure.DownloadEligible,
            },
            interpretation = "Explicit procedure-only planning resolves bounded root conditions from current project values and checked reference activity. It executes no scripts or device actions and generates no tables or complete application image. Physical mapping, parameter images and finalization remain unresolved.",
        };
    }
}
