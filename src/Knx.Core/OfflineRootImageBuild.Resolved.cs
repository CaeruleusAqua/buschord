using System.Xml;

namespace Knx;

public static partial class OfflineRootImageBuild
{
    /// <summary>Resolves root procedure conditions and composes all admitted fixed writers from one shared evaluation.</summary>
    public static RootResolvedImageBuildPlan PreviewResolved(Project project, DeviceInstance device, RootResolvedImageBuildRequest request)
    {
        var manifest = new ArchivedImageSourceManifest(ArchivedImageSourceStatus.InvalidRequest);
        try
        {
            if (!WithinBudget(project, device) || request is null || request.Procedure is null
                || request.CodeMaskPolicy is not (StaticCodeMaskPolicy.SourceDefinedBits or StaticCodeMaskPolicy.NoStaticSourceBits)
                || !RootBuildGuard.Safe(request.Procedure.NativeSubtype) || !Enum.IsDefined(request.Procedure.Access)
                || (request.Procedure.Parts & ~(LoadParts.Full | LoadParts.Parameters | LoadParts.Groups)) != 0
                || (request.Procedure.Parts & (LoadParts.Full | LoadParts.Parameters)) == 0
                || request.Tables is not null && (!Enum.IsDefined(request.Tables.Format)
                    || (request.Procedure.Parts & (LoadParts.Full | LoadParts.Groups)) == 0))
                RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidRequest);
            var owned = RootResolvedImageBuildBinding.Copy(request);
            if (device.HasOpaqueBinaryData || device.BinaryData.Count != 0) RootBuildGuard.Fail(RootImageBuildIssueCode.UnaccountedBinaryData);
            var inputs = ArchivedImageSources.ReadInputs(project, device); manifest = inputs.Manifest;
            if (manifest.Binding is null || !manifest.Binding.MatchesIdentity(project, device)) RootBuildGuard.Fail(RootImageBuildIssueCode.SourceUnavailable);
            var sources = new RootParameterWriterSources(project, device, inputs);
            var prepared = LoadProcedureCompiler.PrepareResolved(inputs, owned.Procedure);
            if (!prepared.IsAvailable) RootBuildGuard.Fail(RootImageBuildIssueCode.ProcedureUnavailable, procedure: prepared.Issues.FirstOrDefault());
            // Preparation validates all declared controller types; only reachable controllers seed the shared evaluation.
            var references = prepared.ControllerReferences.Take(4097).ToArray();
            if (references.Length > 4096) RootBuildGuard.Fail(RootImageBuildIssueCode.LimitExceeded);
            var context = NativeApplicationEvaluationContext.Create(project, device, inputs, references);
            if (!context.IsAvailable)
            {
                var issue = context.Issues.FirstOrDefault();
                RootBuildGuard.Fail(issue?.Code switch
                {
                    GeneratedTableIssueCode.ExternalConfigurationUnaccounted => RootImageBuildIssueCode.ExternalConfigurationUnaccounted,
                    GeneratedTableIssueCode.UnaccountedBinaryData => RootImageBuildIssueCode.UnaccountedBinaryData,
                    GeneratedTableIssueCode.LimitExceeded => RootImageBuildIssueCode.LimitExceeded,
                    GeneratedTableIssueCode.InputsChanged or GeneratedTableIssueCode.StaleSource => RootImageBuildIssueCode.InputsChanged,
                    _ => RootImageBuildIssueCode.ActivationUnavailable,
                }, table: issue);
            }
            var writers = new RootParameterWriterSet(inputs, sources, context);
            var view = NativeProcedureEvaluationContext.FromApplicationContext(project, device, inputs, prepared, context);
            if (!view.IsAvailable) RootBuildGuard.Fail(RootImageBuildIssueCode.ProcedureUnavailable, procedure: view.Issues.FirstOrDefault());
            var procedure = LoadProcedureCompiler.CompileResolved(prepared, view);
            if (!procedure.IsAvailable) RootBuildGuard.Fail(RootImageBuildIssueCode.ProcedureUnavailable, procedure: procedure.Issues.FirstOrDefault());
            SharedGeneratedTableEmission? emission = null;
            if (owned.Tables is not null)
            {
                emission = GeneratedTableCompiler.CompileResolvedFromContext(project, device, inputs, prepared, view, owned.Tables, context);
                if (!emission.IsAvailable || emission.ActualProcedure is not { IsAvailable: true })
                    RootBuildGuard.Fail(RootImageBuildIssueCode.TableUnavailable, table: emission.Issues.FirstOrDefault());
                procedure = emission.ActualProcedure!;
            }
            var buffers = AllocatePayloads(sources.Segments, emission?.Payloads ?? []);
            Seed(buffers, sources.Segments, owned.CodeMaskPolicy); ApplyParameters(buffers, writers.Claims, sources.Policy.ByteOrder);
            var tableSummary = emission is null ? null : new RootResolvedImageTableSummary(emission, ApplyTablePayloads(buffers, emission.Payloads));
            var chunks = buffers.Select(b => new RootImageChunk(b.Arena, b.Offset, b.Data, b.Validity,
                b.SourceIds.Order(StringComparer.Ordinal).ToArray())).ToArray();
            var spans = Missing(chunks, out var unspecified, out var desired);
            var requirements = RequirementsForTables(emission?.Requirements, unspecified);
            if (!WithinBudget(project, device) || !manifest.Binding.MatchesIdentity(project, device)
                || !context.MatchesInputs(project, device, inputs) || !prepared.MatchesSources(inputs) || !view.MatchesCurrent(prepared)
                || !view.UsesSharedApplicationContext(context) || sources.CaptureCarriers(project, device, manifest.ApplicationId!) != sources.CarrierHash)
                RootBuildGuard.Fail(RootImageBuildIssueCode.InputsChanged);
            var proof = new RootSharedProcedureProof(view);
            var binding = new RootResolvedImageBuildBinding(project, device, inputs, owned, sources.CarrierHash, context, prepared,
                writers.Population, sources.Policy, proof, writers.Claims, chunks, spans, procedure, tableSummary, requirements);
            return new(manifest, binding, sources.Policy, writers.Population, procedure, proof, tableSummary, sources.Segments.ToArray(),
                writers.Claims, chunks, spans, requirements, desired, unspecified);
        }
        catch (RootBuildRejected failure) { return new(manifest, failure.Issue); }
        catch (Exception failure) when (failure is IOException or InvalidDataException or XmlException or FormatException or OverflowException or ArgumentException)
        { return new(manifest, new(RootImageBuildIssueCode.InvalidDeclaration)); }
        catch (InvalidOperationException) { return new(manifest, new(RootImageBuildIssueCode.InputsChanged)); }
    }
}
