using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private static RootPropertyBuildRequest? ReadRootPropertyBuildOptions(JsonElement args)
    {
        const string name = "root_property_image_build_preview";
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
        var option = args.GetProperty(name);
        var fields = Closed(option, name, "procedure");
        if (fields.Count != 1) throw new ToolException(name + " requires its own explicit procedure selection.");
        var procedure = option.GetProperty("procedure");
        var selections = Closed(procedure, name + ".procedure", "subtype", "access", "full", "parameters", "groups");
        if (selections.Count != 5) throw new ToolException(name + ".procedure requires subtype, access, full, parameters and groups.");
        string Text(string field)
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
        var subtype = Text("subtype");
        if (subtype is not ("all" or "par" or "grp" or "par,grp" or "ap1" or "cfg"))
            throw new ToolException(name + ".procedure has an unsupported subtype.");
        if (Text("access") != "remote") throw new ToolException(name + ".procedure access must be remote.");
        var parts = (Flag("full") ? LoadParts.Full : LoadParts.None) | (Flag("parameters") ? LoadParts.Parameters : LoadParts.None)
            | (Flag("groups") ? LoadParts.Groups : LoadParts.None);
        if ((parts & (LoadParts.Full | LoadParts.Parameters)) == LoadParts.None)
            throw new ToolException(name + " requires full or parameters.");
        foreach (var backend in new[] { "resource_preview", "property_preview" })
        {
            var flags = args.EnumerateObject().Where(property => property.NameEquals(backend)).Take(2).ToArray();
            if (flags.Length > 1 || flags.Length == 1 && flags[0].Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ToolException(name + " requires at most one boolean " + backend + ".");
        }
        if (args.EnumerateObject().Any(property => property.Name is "load_procedure_preview" or "parameter_subset" or "byte_order"
                or "image_composition_preview" or "root_image_build_preview" or "root_resolved_image_build_preview"
                || (property.Name is "resource_preview" or "property_preview") && property.Value.ValueKind == JsonValueKind.True))
            throw new ToolException(name + " cannot be combined with another image, procedure or subset preview.");
        return new(new(subtype, LoadProcedureAccess.Remote, parts));
    }
    private static JsonObject RootPropertyBuildSchema() => JsonNode.Parse("""
        {
          "type": "object", "additionalProperties": false, "required": ["procedure"],
          "description": "Complete admitted fixed-root property initialization and formatting from one shared evaluation. Source controls select allocations; all device-write gates remain closed.",
          "properties": {
            "procedure": {
              "type": "object", "additionalProperties": false,
              "required": ["subtype", "access", "full", "parameters", "groups"],
              "properties": {
                "subtype": {"type": "string", "enum": ["all", "par", "grp", "par,grp", "ap1", "cfg"]},
                "access": {"type": "string", "enum": ["remote"]},
                "full": {"type": "boolean"},
                "parameters": {"type": "boolean"},
                "groups": {"type": "boolean"}
              },
              "anyOf": [
                {"properties": {"full": {"const": true}}},
                {"properties": {"parameters": {"const": true}}}
              ]
            }
          }
        }
        """)!.AsObject();

    private static object RootPropertyBuildDiagnostic(Project project, DeviceInstance device, RootPropertyBuildRequest request)
    {
        const int recordLimit = 128, propertyLimit = 128, spanLimit = 256, requirementLimit = 256, controlLimit = 512;
        static string Code(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
        static object? Selector(PropertySourceSelector? selector) => selector is null ? null : new
        {
            kind = Code(selector.Kind), propertyId = selector.PropertyId, nativeObjectIndex = selector.NativeObjectIndex,
            applicationObjectIndex = selector.ApplicationObjectIndex, objectType = selector.ObjectType, occurrence = selector.Occurrence,
        };
        static object Pdt(PropertyPdtLayout layout) => new
        {
            profileVersion = layout.ProfileVersion, selectionFingerprint = layout.SelectionFingerprint,
            declaredPdt = layout.DeclaredPdt, readElementBytes = layout.ReadElementBytes, writeElementBytes = layout.WriteElementBytes,
            status = Code(layout.Status),
        };
        var plan = OfflineRootPropertyBuild.Preview(project, device, request);
        var records = plan.Population?.Records.Take(recordLimit).Select(record => new
        {
            refId = record.RefId, parameterId = record.ParameterId, parameterTypeId = record.ParameterTypeId,
            disposition = Code(record.Disposition), active = record.Active, normalReferenceCount = record.NormalReferenceCount,
            legacyPatchAlways = record.LegacyPatchAlways, customerAdjustable = record.CustomerAdjustable,
            currentValueOrigin = Code(record.CurrentValueOrigin), currentValueHash = record.CurrentValueHash, recordValueHash = record.RecordValueHash,
            nativeRecordEmitted = record.NativeRecordEmitted, allocates = record.Allocates, writesData = record.WritesData,
            propertyKey = record.PropertyKey, rawSelector = Selector(record.RawSelector), nativeSelector = Selector(record.NativeSelector),
            byteOffset = record.ByteOffset, bitOffset = record.BitOffset, bitLength = record.BitLength,
        }).ToArray();
        var claims = plan.Claims.Take(recordLimit).Select(claim => new
        {
            refId = claim.RefId, propertyKey = claim.PropertyKey, rawSelector = Selector(claim.RawSelector), nativeSelector = Selector(claim.NativeSelector),
            byteOffset = claim.ByteOffset, bitOffset = claim.BitOffset, bitLength = claim.BitLength,
            writesData = claim.WritesData, maskEffect = Code(claim.MaskEffect),
        }).ToArray();
        var allocations = plan.Allocations.Take(propertyLimit).Select(allocation => new
        {
            propertyKey = allocation.PropertyKey, rawSelector = Selector(allocation.RawSelector), nativeSelector = Selector(allocation.NativeSelector),
            pdtLayout = Pdt(allocation.PdtLayout), requestedLength = allocation.RequestedLength, byteLength = allocation.Length,
            initialDataHash = allocation.InitialDataHash, initialValidityHash = allocation.InitialValidityHash, factoryDataHash = allocation.FactoryDataHash,
            controlOrdinalsTotal = allocation.ControlOrdinals.Count, controlOrdinalsShown = Math.Min(allocation.ControlOrdinals.Count, controlLimit),
            controlOrdinalsTruncated = allocation.ControlOrdinals.Count > controlLimit, controlOrdinals = allocation.ControlOrdinals.Take(controlLimit).ToArray(),
        }).ToArray();
        var chunks = plan.Chunks.Take(propertyLimit).Select(chunk => new
        {
            propertyKey = chunk.PropertyKey, rawSelector = Selector(chunk.RawSelector), nativeSelector = Selector(chunk.NativeSelector),
            pdtLayout = Pdt(chunk.PdtLayout), byteLength = chunk.Length,
            initialDataHash = chunk.InitialData.Sha256, initialValidityHash = chunk.InitialValidity.Sha256,
            workingDataHash = chunk.WorkingData.Sha256, desiredValidityHash = chunk.DesiredValidity.Sha256, canonicalDesiredHash = chunk.CanonicalDesired.Sha256,
        }).ToArray();
        var counts = plan.ObservedCounts.Take(propertyLimit).Select(count => new
        {
            propertyKey = count.PropertyKey, rawSelector = Selector(count.RawSelector), nativeSelector = Selector(count.NativeSelector),
            sourceOrdinal = count.SourceOrdinal, byteLength = count.Data.Length, dataHash = count.Data.Sha256, maskHash = count.Mask?.Sha256,
            encodedCount = count.EncodedCount, status = Code(count.Status), disposition = count.Disposition,
        }).ToArray();
        var spans = plan.UnspecifiedSpans.Take(spanLimit).Select(span => new
        { propertyKey = span.PropertyKey, byteOffset = span.ByteOffset, byteLength = span.Length, bitMask = span.BitMask }).ToArray();
        var requirements = plan.UnresolvedRequirements.Take(requirementLimit).Select(requirement => new
        { code = Code(requirement.Code), propertyKey = requirement.PropertyKey }).ToArray();
        return new
        {
            available = plan.IsAvailable, scope = plan.Scope, profileVersion = plan.ProfileVersion, readOnly = true,
            completeFixedRootPopulation = plan.CompleteFixedRootPopulation, completeApplicationImage = plan.CompleteApplicationImage,
            executionEligible = plan.ExecutionEligible, downloadEligible = plan.DownloadEligible,
            imageSources = ArchivedImageSourceDiagnostic(plan.Sources),
            binding = plan.Binding is { } binding ? new
            {
                profileVersion = binding.ProfileVersion, numericParseProfile = binding.NumericParseProfile,
                projectId = binding.ProjectId, projectGuid = binding.ProjectGuid, deviceId = binding.DeviceId,
                applicationDataRevision = binding.ApplicationDataRevision, applicationId = binding.ApplicationId,
                definitionHash = binding.DefinitionHash, savedValuesHash = binding.SavedValuesHash,
                sourceFingerprint = binding.SourceFingerprint, propertyProfileHash = binding.PropertyProfileHash,
                carrierHash = binding.CarrierHash, projectInputHash = binding.ProjectInputHash, contextFingerprint = binding.ContextFingerprint,
                sourceHash = binding.SourceHash, populationHash = binding.PopulationHash, populationValuesHash = binding.PopulationValuesHash,
                policyHash = binding.PolicyHash, geometryHash = binding.GeometryHash, initializationHash = binding.InitializationHash,
                allocationHash = binding.AllocationHash, initialDataHash = binding.InitialDataHash, observedCountsHash = binding.ObservedCountsHash,
                requirementsHash = binding.RequirementsHash, claimsHash = binding.ClaimsHash, workingHash = binding.WorkingHash,
                validityHash = binding.ValidityHash, canonicalHash = binding.CanonicalHash, procedureGraphHash = binding.ProcedureGraphHash,
                selectedProcedureProfileHash = binding.SelectedProcedureProfileHash, controllerSetHash = binding.ControllerSetHash,
                sharedProcedureProofHash = binding.SharedProcedureProofHash, sharedPlacementInventoryHash = binding.SharedPlacementInventoryHash,
                procedureWitnessHash = binding.ProcedureWitnessHash, fingerprint = binding.Fingerprint,
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
                recordsTotal = population.Records.Count, recordsShown = records?.Length ?? 0, recordsTruncated = population.Records.Count > (records?.Length ?? 0), records,
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
            issues = plan.Issues.Select(issue => new
            {
                code = Code(issue.Code), refId = issue.RefId, propertyKey = issue.PropertyKey, resourceId = issue.ResourceId,
                parameterIssue = issue.ParameterIssue is { } parameter ? Code(parameter.Code) : null,
                activationIssue = issue.ActivationIssue is { } activation ? Code(activation.Code) : null,
                procedureIssue = issue.ProcedureIssue is { } procedureIssue ? Code(procedureIssue.Code) : null,
                initializationIssue = issue.InitializationIssue is { } initialization ? new
                {
                    code = Code(initialization.Code), refId = initialization.RefId, propertyKey = initialization.PropertyKey, resourceId = initialization.ResourceId,
                    procedureIssue = initialization.ProcedureIssue is { } nested ? Code(nested.Code) : null,
                    factoryStatus = initialization.FactoryStatus is { } status ? Code(status) : null, factoryBlobHash = initialization.FactoryBlobHash,
                } : null,
            }).ToArray(),
            desiredBitCount = plan.DesiredBitCount, unspecifiedBitCount = plan.UnspecifiedBitCount,
            allocationsTotal = plan.Allocations.Count, allocationsShown = allocations.Length, allocationsTruncated = plan.Allocations.Count > allocations.Length, allocations,
            claimsTotal = plan.Claims.Count, claimsShown = claims.Length, claimsTruncated = plan.Claims.Count > claims.Length, claims,
            chunksTotal = plan.Chunks.Count, chunksShown = chunks.Length, chunksTruncated = plan.Chunks.Count > chunks.Length, chunks,
            observedCountsTotal = plan.ObservedCounts.Count, observedCountsShown = counts.Length, observedCountsTruncated = plan.ObservedCounts.Count > counts.Length, observedCounts = counts,
            unspecifiedSpansTotal = plan.UnspecifiedSpans.Count, unspecifiedSpansShown = spans.Length, unspecifiedSpansTruncated = plan.UnspecifiedSpans.Count > spans.Length, unspecifiedSpans = spans,
            requirementsTotal = plan.UnresolvedRequirements.Count, requirementsShown = requirements.Length, requirementsTruncated = plan.UnresolvedRequirements.Count > requirements.Length, unresolvedRequirements = requirements,
            interpretation = "A complete admitted fixed-root property working stage from source-owned allocation controls and one application evaluation. Initial data and zero validity are separate from desired bytes; inline actions are not seeds. Archived counts are observed without adoption. Current device state, capacity, physical mapping, other writers and finalization remain unresolved.",
        };
    }
}
