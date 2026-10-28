using System.Xml;

namespace Knx;

internal static partial class GeneratedTableCompiler
{
    internal static SharedGeneratedTableEmission CompileResolvedFromContext(Project project, DeviceInstance device,
        ArchivedImageSources.Inputs inputs, PreparedConditionalLoadProcedure prepared, NativeProcedureEvaluationContext procedureView,
        GeneratedTableRequest request, NativeApplicationEvaluationContext application)
    {
        var format = request is not null && Enum.IsDefined(request.Format) ? (GeneratedTableFormat?)request.Format : null;
        try
        {
            ValidateResolvedPair(project, device, inputs, prepared, procedureView, request!, application);
            var ownedRequest = request! with { };
            var procedure = LoadProcedureCompiler.CompileResolved(prepared, procedureView);
            if (!procedure.IsAvailable)
            {
                ValidateResolvedPair(project, device, inputs, prepared, procedureView, ownedRequest, application);
                return SharedEmissionMaterializer.Instance.Unavailable(new(procedure.Issues.FirstOrDefault()?.Code switch
                {
                    LoadProcedureIssueCode.InputsChanged or LoadProcedureIssueCode.ContextMismatch => GeneratedTableIssueCode.InputsChanged,
                    LoadProcedureIssueCode.LimitExceeded => GeneratedTableIssueCode.LimitExceeded,
                    _ => GeneratedTableIssueCode.UnsupportedProcedureControl,
                }), format);
            }
            if (procedure.Request != prepared.Request || procedure.ApplicationId != prepared.ApplicationId
                || procedure.MaskVersionId != prepared.MaskVersionId
                || procedure.SelectedProfileHash != prepared.SelectedProfileHash || procedure.Style != prepared.Style
                || procedure.ProfileVersion != (prepared.RequiresExtendedProfile ? 2 : 1)
                || prepared.HasConditions && procedure.ConditionalSelection?.ControllerWitnessHash != procedureView.Fingerprint)
                Fail(GeneratedTableIssueCode.InputsChanged);
            if (procedure.ProfileVersion == 2)
            {
                if (procedure.ApplicationDefinitionHash != prepared.ApplicationDefinitionHash) Fail(GeneratedTableIssueCode.InputsChanged);
            }
            else
            {
                var sourceProcedure = LoadProcedureCompiler.Compile(inputs.Application!, inputs.Master, prepared.Request!);
                if (!sourceProcedure.IsAvailable || sourceProcedure.ProfileVersion != 1 || sourceProcedure.WitnessHash != procedure.WitnessHash
                    || sourceProcedure.ApplicationDefinitionHash != procedure.ApplicationDefinitionHash
                    || ResolvedProcedureContents(sourceProcedure) != ResolvedProcedureContents(procedure))
                    Fail(GeneratedTableIssueCode.InputsChanged);
            }
            var result = CreateEmission<SharedGeneratedTableEmission>(project, device, inputs, procedure, ownedRequest,
                scripts: null, providedContext: application, materializer: SharedEmissionMaterializer.Instance);
            ValidateResolvedPair(project, device, inputs, prepared, procedureView, ownedRequest, application);
            return result;
        }
        catch (Rejected failure) { return SharedEmissionMaterializer.Instance.Unavailable(failure.Issue, format); }
        catch (InvalidOperationException) { return SharedEmissionMaterializer.Instance.Unavailable(new(GeneratedTableIssueCode.InputsChanged), format); }
        catch (Exception failure) when (failure is XmlException or InvalidDataException or IOException or FormatException or OverflowException or ArgumentException or System.Text.Json.JsonException)
        { return SharedEmissionMaterializer.Instance.Unavailable(new(GeneratedTableIssueCode.InvalidTableDeclaration), format); }
    }

    private static string ResolvedProcedureContents(LoadProcedurePlan procedure) => ParameterPatchSourceBinding.Hash(new
    {
        procedure.ProfileVersion, procedure.ApplicationId, procedure.MaskVersionId, procedure.Style, procedure.Request,
        procedure.ApplicationDefinitionHash, procedure.SelectedProfileHash, procedure.WitnessHash,
        controls = procedure.Controls.Select(c => new
        {
            c.Ordinal, c.Kind, c.Origin, c.SourceOrdinal, c.TemplateOrdinal, c.MergeId, c.DeclaredAppliesTo, c.EffectiveAppliesTo,
            c.Arguments, c.OnError, data = c.InlineData?.Sha256, mask = c.InlineMask?.Sha256, c.Provenance,
        }).ToArray(),
        procedure.MergeDecisions, procedure.ResourceReferences, procedure.TableRequirements, procedure.Requirements, procedure.ConditionalSelection,
    });

    private static void ValidateResolvedPair(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        PreparedConditionalLoadProcedure prepared, NativeProcedureEvaluationContext procedureView,
        GeneratedTableRequest request, NativeApplicationEvaluationContext application)
    {
        if (project is null || device is null || inputs is null || prepared is null || procedureView is null || application is null
            || request is null || !Enum.IsDefined(request.Format) || !prepared.IsAvailable || prepared.Request is null
            || !procedureView.IsAvailable || !application.IsAvailable
            || !Safe(prepared.Request.NativeSubtype) || !Enum.IsDefined(prepared.Request.Access)
            || (prepared.Request.Parts & ~(LoadParts.Full | LoadParts.Parameters | LoadParts.Groups)) != 0
            || (prepared.Request.Parts & (LoadParts.Full | LoadParts.Groups)) == 0
            || !procedureView.UsesSharedApplicationContext(application))
            Fail(GeneratedTableIssueCode.InvalidRequest);
        var sources = inputs.Manifest;
        if (sources is null || sources.Binding is null || sources.Status != ArchivedImageSourceStatus.Inspected || inputs.Application is null)
            Fail(GeneratedTableIssueCode.SourceBindingUnavailable);
        if (!sources.Binding!.MatchesIdentity(project, device) || !prepared.MatchesSources(inputs)
            || prepared.SourceFingerprint != sources.Binding.Fingerprint || prepared.SourceFingerprint != application.SourceFingerprint
            || prepared.ApplicationId != sources.ApplicationId || prepared.ApplicationDefinitionHash != sources.ApplicationDefinitionHash
            || prepared.ApplicationDefinitionHash != application.ApplicationDefinitionHash || !procedureView.MatchesPrepared(prepared)
            || !procedureView.MatchesInputs(project, device, inputs, prepared) || !application.MatchesInputs(project, device, inputs))
            Fail(GeneratedTableIssueCode.InputsChanged);
        if (application.ScriptCapabilityUsed || application.Snapshot is null || application.Catalog is null || application.Activation is null
            || application.CapturedInputs is null || application.Snapshot.ModuleInstances.Count != 0
            || !application.Activation.BelongsTo(application.Catalog, application.Snapshot))
            Fail(GeneratedTableIssueCode.InvalidRequest);
        var expected = prepared.ControllerReferences.Concat(application.Catalog!.StructuralPlan.CallControllerReferences)
            .Distinct(StringComparer.Ordinal).Take(4097).ToArray();
        if (expected.Length > 4096) Fail(GeneratedTableIssueCode.LimitExceeded);
        if (!application.AdditionalControllerReferences.SetEquals(expected)) Fail(GeneratedTableIssueCode.InvalidRequest);
    }
}
