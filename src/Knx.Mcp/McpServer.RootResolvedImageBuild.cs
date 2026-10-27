using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private static RootResolvedImageBuildRequest? ReadRootResolvedImageBuildOptions(JsonElement args, ApplicationBuildOptions? build,
        ParameterPatchOptions? patch, ImageCompositionRequest? composition, RootImageBuildRequest? ordinaryRoot)
    {
        const string name = "root_resolved_image_build_preview";
        if (args.ValueKind != JsonValueKind.Object) return null;
        var count = args.EnumerateObject().Count(property => property.NameEquals(name));
        if (count == 0) return null;
        if (count != 1) throw new ToolException(name + " must occur exactly once.");
        static HashSet<string> Closed(JsonElement value, string label, params string[] allowed)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new ToolException(label + " must be an object.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                    throw new ToolException(label + " has duplicate or unsupported fields.");
            return names;
        }
        var option = args.GetProperty(name); var fields = Closed(option, name, "mask_policy", "procedure", "generated_tables");
        if (!fields.Contains("mask_policy") || option.GetProperty("mask_policy").ValueKind != JsonValueKind.String)
            throw new ToolException(name + " requires an explicit mask_policy string.");
        var policy = option.GetProperty("mask_policy").GetString() switch
        {
            "source_defined_bits" => StaticCodeMaskPolicy.SourceDefinedBits,
            "no_static_source_bits" => StaticCodeMaskPolicy.NoStaticSourceBits,
            _ => throw new ToolException(name + " mask_policy must be source_defined_bits or no_static_source_bits."),
        };
        if (!fields.Contains("procedure")) throw new ToolException(name + " requires an explicit procedure selection.");
        var procedure = option.GetProperty("procedure");
        var procedureFields = Closed(procedure, name + ".procedure", "subtype", "access", "full", "parameters", "groups");
        if (procedureFields.Count != 5) throw new ToolException(name + ".procedure requires subtype, access, full, parameters and groups.");
        string TextField(string field)
        {
            if (procedure.GetProperty(field).ValueKind != JsonValueKind.String)
                throw new ToolException(name + ".procedure " + field + " must be a string.");
            return procedure.GetProperty(field).GetString()!;
        }
        bool Flag(string field)
        {
            if (procedure.GetProperty(field).ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ToolException(name + ".procedure " + field + " must be a boolean.");
            return procedure.GetProperty(field).GetBoolean();
        }
        var subtype = TextField("subtype");
        if (subtype is not ("all" or "par" or "grp" or "par,grp" or "ap1" or "cfg")) throw new ToolException(name + ".procedure has an unsupported subtype.");
        if (TextField("access") != "remote") throw new ToolException(name + ".procedure access must be remote.");
        var parts = (Flag("full") ? LoadParts.Full : LoadParts.None) | (Flag("parameters") ? LoadParts.Parameters : LoadParts.None) | (Flag("groups") ? LoadParts.Groups : LoadParts.None);
        if ((parts & (LoadParts.Full | LoadParts.Parameters)) == LoadParts.None)
            throw new ToolException(name + " requires full or parameters.");
        GeneratedTableRequest? tables = null;
        if (fields.Contains("generated_tables"))
        {
            var table = option.GetProperty("generated_tables"); var tableFields = Closed(table, name + ".generated_tables", "format");
            if (tableFields.Count != 1 || table.GetProperty("format").ValueKind != JsonValueKind.String)
                throw new ToolException(name + ".generated_tables requires one format string.");
            tables = new(table.GetProperty("format").GetString() switch
            {
                "m112" => GeneratedTableFormat.M112,
                "system_b_2" => GeneratedTableFormat.SystemB2ByteAssociations,
                "system_b_4" => GeneratedTableFormat.SystemB4ByteAssociations,
                _ => throw new ToolException(name + ".generated_tables format must be m112, system_b_2 or system_b_4."),
            });
            if ((parts & (LoadParts.Full | LoadParts.Groups)) == LoadParts.None)
                throw new ToolException(name + " tables require full or groups.");
        }
        if (build is not null || patch is not null || composition is not null || ordinaryRoot is not null)
            throw new ToolException(name + " cannot be combined with load_procedure_preview, parameter_subset, image_composition_preview or root_image_build_preview.");
        return new(policy, new(subtype, LoadProcedureAccess.Remote, parts), tables);
    }

    private static object RootResolvedImageBuildDiagnostic(Project project, DeviceInstance device, RootResolvedImageBuildRequest request)
    {
        const int sourceLimit = 128, recordLimit = 128, claimLimit = 128, chunkLimit = 128, spanLimit = 256, requirementLimit = 256, controlLimit = 512;
        static string Code(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
        static object? Arena(ResourcePatchArena? arena) => arena is null ? null : new { kind = Code(arena.Kind), loadStateMachine = arena.LoadStateMachine };
        var plan = OfflineRootImageBuild.PreviewResolved(project, device, request);
        var records = plan.Population?.Records.Take(recordLimit).Select(record => new
        {
            refId = record.RefId, parameterId = record.ParameterId, parameterTypeId = record.ParameterTypeId,
            disposition = Code(record.Disposition), active = record.Active, normalReferenceCount = record.NormalReferenceCount,
            legacyPatchAlways = record.LegacyPatchAlways, customerAdjustable = record.CustomerAdjustable,
            currentValueOrigin = Code(record.CurrentValueOrigin), currentValueHash = record.CurrentValueHash, recordValueHash = record.RecordValueHash,
            nativeRecordEmitted = record.NativeRecordEmitted, allocates = record.Allocates, writesData = record.WritesData,
            resourceId = record.ResourceId, arena = Arena(record.Arena), byteOffsetInArena = record.ByteOffsetInArena,
            bitOffset = record.BitOffset, bitLength = record.BitLength,
        }).ToArray();
        var sources = plan.SourceSegments.Take(sourceLimit).Select(source => new
        {
            sourceId = source.SourceId, arena = Arena(source.Arena), byteOffsetInArena = source.ByteOffsetInArena,
            declaredSize = source.DeclaredSize, allocationLength = source.AllocationLength,
            rawDataLength = source.RawData?.Length, rawDataHash = source.RawData?.Sha256,
            rawMaskLength = source.RawMask?.Length, rawMaskHash = source.RawMask?.Sha256,
        }).ToArray();
        var claims = plan.Claims.Take(claimLimit).Select(claim => new
        {
            refId = claim.RefId, resourceId = claim.ResourceId, arena = Arena(claim.Arena), byteOffsetInArena = claim.ByteOffsetInArena,
            bitOffset = claim.BitOffset, bitLength = claim.BitLength, writesData = claim.WritesData, maskEffect = Code(claim.MaskEffect),
        }).ToArray();
        var chunks = plan.Chunks.Take(chunkLimit).Select(chunk => new
        {
            key = chunk.Key, arena = Arena(chunk.Arena), byteOffsetInArena = chunk.ByteOffsetInArena, byteLength = chunk.Length,
            workingDataHash = chunk.WorkingData.Sha256, desiredValidityHash = chunk.DesiredValidity.Sha256,
            canonicalDesiredHash = chunk.CanonicalDesired.Sha256,
            sourceIdsTotal = chunk.SourceIds.Count, sourceIdsShown = Math.Min(chunk.SourceIds.Count, sourceLimit),
            sourceIdsTruncated = chunk.SourceIds.Count > sourceLimit, sourceIds = chunk.SourceIds.Take(sourceLimit).ToArray(),
        }).ToArray();
        var spans = plan.UnspecifiedSpans.Take(spanLimit).Select(span => new
        {
            chunkKey = span.ChunkKey, arena = Arena(span.Arena), byteOffsetInArena = span.ByteOffsetInArena,
            byteLength = span.Length, unspecifiedBitCount = span.UnspecifiedBitCount,
        }).ToArray();
        var requirements = plan.UnresolvedRequirements.Take(requirementLimit).Select(requirement => new
        { code = Code(requirement.Code), resourceId = requirement.ResourceId }).ToArray();
        return new
        {
            available = plan.IsAvailable, scope = plan.Scope, profileVersion = plan.ProfileVersion, readOnly = true,
            codeMaskPolicy = Code(request.CodeMaskPolicy), completeFixedRootPopulation = plan.CompleteFixedRootPopulation,
            completeApplicationImage = plan.CompleteApplicationImage, executionEligible = plan.ExecutionEligible, downloadEligible = plan.DownloadEligible,
            imageSources = ArchivedImageSourceDiagnostic(plan.Sources),
            binding = plan.Binding is { } binding ? new
            {
                profileVersion = binding.ProfileVersion, projectId = binding.ProjectId, projectGuid = binding.ProjectGuid,
                deviceId = binding.DeviceId, applicationDataRevision = binding.ApplicationDataRevision, applicationId = binding.ApplicationId,
                sourceFingerprint = binding.SourceFingerprint, carrierHash = binding.CarrierHash, projectInputHash = binding.ProjectInputHash,
                contextFingerprint = binding.ContextFingerprint, populationHash = binding.PopulationHash, valuesHash = binding.ValuesHash,
                policyHash = binding.PolicyHash, geometryHash = binding.GeometryHash, requirementsHash = binding.RequirementsHash,
                claimsHash = binding.ClaimsHash, workingHash = binding.WorkingHash,
                validityHash = binding.ValidityHash, canonicalHash = binding.CanonicalHash, procedureWitnessHash = binding.ProcedureWitnessHash,
                procedureGraphHash = binding.ProcedureGraphHash, selectedProcedureProfileHash = binding.SelectedProcedureProfileHash,
                controllerSetHash = binding.ControllerSetHash, sharedProcedureProofHash = binding.SharedProcedureProofHash,
                sharedPlacementInventoryHash = binding.SharedPlacementInventoryHash,
                tableEmissionHash = binding.TableEmissionHash, tableCompatibilityHash = binding.TableCompatibilityHash, fingerprint = binding.Fingerprint,
            } : null,
            procedureProof = plan.ProcedureProof is { } proof ? new
            {
                fingerprint = proof.Fingerprint, applicationContextFingerprint = proof.ApplicationContextFingerprint,
                controllerSetHash = proof.ControllerSetHash, placementInventoryHash = proof.PlacementInventoryHash, savedValuesHash = proof.SavedValuesHash,
                controllersTotal = proof.Controllers.Count, controllersShown = Math.Min(proof.Controllers.Count, controlLimit),
                controllersTruncated = proof.Controllers.Count > controlLimit,
                controllers = proof.Controllers.Take(controlLimit).Select(controller => new
                {
                    refId = controller.RefId, parameterId = controller.ParameterId, parameterTypeId = controller.ParameterTypeId,
                    everDeclaredOwnerCount = controller.EverDeclaredOwnerCount, activeNormalOwnerCount = controller.ActiveNormalOwnerCount,
                    active = controller.Active, activeValueHash = controller.ActiveValueHash, declarationHash = controller.DeclarationHash, placementHash = controller.PlacementHash,
                }).ToArray(),
            } : null,
            policy = plan.Policy is { } policy ? new
            {
                byteOrder = Code(policy.ByteOrder), downloadBehavior = Code(policy.DownloadBehavior), maskPolicy = Code(policy.MaskPolicy),
                customerPolicy = policy.CustomerPolicy, minEtsVersion = policy.MinEtsVersion, packedMinEtsVersion = policy.PackedMinEtsVersion,
                optionsDeclared = policy.OptionsDeclared, declaredDownloadBehavior = policy.DeclaredDownloadBehavior,
                declaredByteOrder = policy.DeclaredByteOrder, fingerprint = policy.Fingerprint,
            } : null,
            population = plan.Population is { } population ? new
            {
                declaredParameterCount = population.DeclaredParameterCount, referenceCount = population.ReferenceCount, ownerCount = population.OwnerCount,
                physicalRecordCount = population.PhysicalRecordCount, noStorageCount = population.NoStorageCount, activeCount = population.ActiveCount,
                alwaysCount = population.AlwaysCount, inactiveDefaultCount = population.InactiveDefaultCount, inactiveNullCount = population.InactiveNullCount,
                populationHash = population.PopulationHash, valuesHash = population.ValuesHash, geometryHash = population.GeometryHash,
                recordsTotal = population.Records.Count, recordsShown = records?.Length ?? 0,
                recordsTruncated = population.Records.Count > (records?.Length ?? 0), records,
            } : null,
            procedure = plan.Procedure is { } procedure ? new
            {
                available = procedure.IsAvailable, profileVersion = procedure.ProfileVersion, scope = procedure.Scope,
                applicationId = procedure.ApplicationId, maskVersionId = procedure.MaskVersionId, witnessHash = procedure.WitnessHash,
                controlsTotal = procedure.Controls.Count, controlsShown = Math.Min(procedure.Controls.Count, controlLimit),
                controlsTruncated = procedure.Controls.Count > controlLimit,
                conditionalSelection = procedure.ConditionalSelection is { } selection ? new
                {
                    profileVersion = selection.ProfileVersion, controllerWitnessHash = selection.ControllerWitnessHash,
                    decisionsTotal = selection.Decisions.Count, decisionsShown = Math.Min(selection.Decisions.Count, controlLimit),
                    decisionsTruncated = selection.Decisions.Count > controlLimit,
                    decisions = selection.Decisions.Take(controlLimit).Select(decision => new
                    {
                        ordinal = decision.Ordinal, source = ResolvedProvenance(decision.Source), controllerRefId = decision.ControllerRefId,
                        controllerActive = decision.ControllerActive,
                        controllerValueHash = decision.ControllerValue is { } value ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture)))) : null,
                        selectedWhenOrdinalsTotal = decision.SelectedWhenOrdinals.Count,
                        selectedWhenOrdinalsShown = Math.Min(decision.SelectedWhenOrdinals.Count, controlLimit),
                        selectedWhenOrdinalsTruncated = decision.SelectedWhenOrdinals.Count > controlLimit,
                        selectedWhenOrdinals = decision.SelectedWhenOrdinals.Take(controlLimit).ToArray(), usedDefault = decision.UsedDefault,
                    }).ToArray(),
                } : null,
                controls = procedure.Controls.Take(controlLimit).Select(control => new
                {
                    ordinal = control.Ordinal, kind = Code(control.Kind), origin = Code(control.Origin),
                    sourceOrdinal = control.SourceOrdinal, templateOrdinal = control.TemplateOrdinal, mergeId = control.MergeId,
                    provenance = ResolvedProvenance(control.Provenance),
                    arguments = control.Arguments.Select(argument => new
                    { name = argument.Name, kind = Code(argument.Kind), unsignedValue = argument.UnsignedValue, booleanValue = argument.BooleanValue, textValue = argument.TextValue }).ToArray(),
                    inlineDataLength = control.InlineData?.Length, inlineDataHash = control.InlineData?.Sha256,
                    inlineMaskLength = control.InlineMask?.Length, inlineMaskHash = control.InlineMask?.Sha256,
                }).ToArray(),
            } : null,
            tables = plan.Tables is { } tables ? new
            {
                format = tables.Format switch
                { GeneratedTableFormat.M112 => "m112", GeneratedTableFormat.SystemB2ByteAssociations => "system_b_2", GeneratedTableFormat.SystemB4ByteAssociations => "system_b_4", _ => null },
                profileVersion = tables.ProfileVersion, nativeWitnessHash = tables.NativeWitnessHash, emissionHash = tables.EmissionHash,
                payloadHash = tables.PayloadHash, actualProcedureWitnessHash = tables.ActualProcedureWitnessHash, compatibilityHash = tables.CompatibilityHash,
                procedureSizeAdjustmentsTotal = tables.ProcedureSizeAdjustments.Count,
                procedureSizeAdjustmentsShown = Math.Min(tables.ProcedureSizeAdjustments.Count, controlLimit),
                procedureSizeAdjustmentsTruncated = tables.ProcedureSizeAdjustments.Count > controlLimit,
                procedureSizeAdjustments = tables.ProcedureSizeAdjustments.Take(controlLimit).Select(adjustment => new
                { controlOrdinal = adjustment.ControlOrdinal, role = Code(adjustment.Role), beforeValue = adjustment.BeforeValue, afterValue = adjustment.AfterValue }).ToArray(),
                payloads = tables.Payloads.Select(payload => new
                { role = Code(payload.Role), arena = Arena(payload.Arena), byteOffsetInArena = payload.ByteOffsetInArena, byteLength = payload.Length,
                    dataHash = payload.DataHash, writeMaskHash = payload.WriteMaskHash, inputCompatibilityHash = payload.InputCompatibilityHash }).ToArray(),
                unresolvedRequirements = tables.UnresolvedRequirements.Select(requirement => new { code = Code(requirement.Code), resourceId = requirement.ResourceId }).ToArray(),
            } : null,
            issues = plan.Issues.Select(issue => new
            {
                code = Code(issue.Code), refId = issue.RefId, resourceId = issue.ResourceId,
                parameterIssue = issue.ParameterIssue is { } parameterIssue ? Code(parameterIssue.Code) : null,
                tableIssue = issue.TableIssue is { } tableIssue ? Code(tableIssue.Code) : null,
                procedureIssue = issue.ProcedureIssue is { } procedureIssue ? Code(procedureIssue.Code) : null,
            }).ToArray(),
            desiredBitCount = plan.DesiredBitCount, unspecifiedBitCount = plan.UnspecifiedBitCount,
            sourceSegmentsTotal = plan.SourceSegments.Count, sourceSegmentsShown = sources.Length, sourceSegmentsTruncated = plan.SourceSegments.Count > sources.Length, sourceSegments = sources,
            claimsTotal = plan.Claims.Count, claimsShown = claims.Length, claimsTruncated = plan.Claims.Count > claims.Length, claims,
            chunksTotal = plan.Chunks.Count, chunksShown = chunks.Length, chunksTruncated = plan.Chunks.Count > chunks.Length, chunks,
            unspecifiedSpansTotal = plan.UnspecifiedSpans.Count, unspecifiedSpansShown = spans.Length, unspecifiedSpansTruncated = plan.UnspecifiedSpans.Count > spans.Length, unspecifiedSpans = spans,
            requirementsTotal = plan.UnresolvedRequirements.Count, requirementsShown = requirements.Length, requirementsTruncated = plan.UnresolvedRequirements.Count > requirements.Length, unresolvedRequirements = requirements,
            interpretation = "A complete admitted fixed-root reference population and resolved procedure in one bounded working stage. The same application evaluation supplies ordinary parameter activity and strict procedure-controller provenance. Native source options determine parameter byte order and inactive behavior. Raw code, working bytes and desired validity stay distinct; optional tables require compatible working inputs. Device options, other writers and finalization remain unresolved.",
        };
    }
}
