using System.Text.Json;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private static RootImageBuildRequest? ReadRootImageBuildOptions(JsonElement args, ApplicationBuildOptions? build,
        ParameterPatchOptions? patch, ImageCompositionRequest? composition)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        var count = args.EnumerateObject().Count(property => property.NameEquals("root_image_build_preview"));
        if (count == 0) return null;
        if (count != 1) throw new ToolException("root_image_build_preview must occur exactly once.");
        var option = args.GetProperty("root_image_build_preview");
        if (option.ValueKind != JsonValueKind.Object)
            throw new ToolException("root_image_build_preview must be an object with one explicit mask_policy.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in option.EnumerateObject())
            if (!names.Add(property.Name) || property.Name != "mask_policy")
                throw new ToolException("root_image_build_preview allows exactly one mask_policy, without other fields.");
        if (names.Count != 1 || option.GetProperty("mask_policy").ValueKind != JsonValueKind.String)
            throw new ToolException("root_image_build_preview requires an explicit mask_policy string.");
        var policy = option.GetProperty("mask_policy").GetString() switch
        {
            "source_defined_bits" => StaticCodeMaskPolicy.SourceDefinedBits,
            "no_static_source_bits" => StaticCodeMaskPolicy.NoStaticSourceBits,
            _ => throw new ToolException("root_image_build_preview mask_policy must be source_defined_bits or no_static_source_bits."),
        };
        if (build is null)
            throw new ToolException("root_image_build_preview requires an explicit load_procedure_preview selection.");
        if (build.ResolveConditions)
            throw new ToolException("root_image_build_preview requires an ordinary procedure without resolve_conditions=true.");
        if ((build.Procedure.Parts & (LoadParts.Full | LoadParts.Parameters)) == LoadParts.None)
            throw new ToolException("root_image_build_preview requires full or parameters to be selected.");
        if (patch is not null || composition is not null)
            throw new ToolException("root_image_build_preview cannot be combined with parameter_subset or image_composition_preview.");
        return new(policy, build.Procedure, build.Tables);
    }

    private static object RootImageBuildDiagnostic(Project project, DeviceInstance device, RootImageBuildRequest request)
    {
        const int sourceLimit = 128, recordLimit = 128, claimLimit = 128, chunkLimit = 128, spanLimit = 256, requirementLimit = 256, controlLimit = 512;
        static string Code(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
        static object? Arena(ResourcePatchArena? arena) => arena is null ? null : new { kind = Code(arena.Kind), loadStateMachine = arena.LoadStateMachine };
        var plan = OfflineRootImageBuild.Preview(project, device, request);
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
                tableBindingHash = binding.TableBindingHash, tableCompatibilityHash = binding.TableCompatibilityHash, fingerprint = binding.Fingerprint,
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
                controls = procedure.Controls.Take(controlLimit).Select(control => new
                {
                    ordinal = control.Ordinal, kind = Code(control.Kind), origin = Code(control.Origin),
                    sourceOrdinal = control.SourceOrdinal, templateOrdinal = control.TemplateOrdinal, mergeId = control.MergeId,
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
                bindingFingerprint = tables.Binding.Fingerprint, nativeWitnessHash = tables.NativeWitnessHash, compatibilityHash = tables.CompatibilityHash,
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
            interpretation = "A complete admitted fixed-root reference population in a bounded working stage. Native source options determine parameter byte order and inactive behavior. Raw code, working bytes and desired validity stay distinct; optional tables require compatible working inputs. Device options, other writers and finalization remain unresolved.",
        };
    }
}
