using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private static NativeImageBuildRequest? ReadNativeImageBuildOptions(JsonElement args)
    {
        const string name = "native_image_build_preview";
        if (args.ValueKind != JsonValueKind.Object) return null;
        var occurrences = args.EnumerateObject().Where(p => p.NameEquals(name)).Take(2).ToArray();
        if (occurrences.Length == 0) return null;
        if (occurrences.Length != 1) throw new ToolException(name + " must occur exactly once.");
        static HashSet<string> Closed(JsonElement value, string label, params string[] allowed)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new ToolException(label + " must be an object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
                if (!seen.Add(field.Name) || !allowed.Contains(field.Name, StringComparer.Ordinal))
                    throw new ToolException(label + " has duplicate or unsupported fields.");
            return seen;
        }
        var option = occurrences[0].Value; var fields = Closed(option, name, "option_words", "tables");
        if (!fields.Contains("option_words")) throw new ToolException(name + " requires option_words.");
        var rawWords = option.GetProperty("option_words");
        if (rawWords.ValueKind != JsonValueKind.Array || rawWords.GetArrayLength() is < 1 or > 16)
            throw new ToolException(name + ".option_words must contain 1..16 distinct uint32 integers.");
        var words = new List<uint>(rawWords.GetArrayLength()); var unique = new HashSet<uint>();
        foreach (var raw in rawWords.EnumerateArray())
        {
            if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetUInt32(out var word) || !unique.Add(word))
                throw new ToolException(name + ".option_words must contain 1..16 distinct uint32 integers.");
            words.Add(word);
        }
        GeneratedTableRequest? tables = null;
        if (fields.Contains("tables"))
        {
            var raw = option.GetProperty("tables"); var names = Closed(raw, name + ".tables", "format");
            if (names.Count != 1 || raw.GetProperty("format").ValueKind != JsonValueKind.String)
                throw new ToolException(name + ".tables requires one format string.");
            tables = new(raw.GetProperty("format").GetString() switch
            {
                "m112" => GeneratedTableFormat.M112,
                "system_b_2" => GeneratedTableFormat.SystemB2ByteAssociations,
                "system_b_4" => GeneratedTableFormat.SystemB4ByteAssociations,
                _ => throw new ToolException(name + ".tables format must be m112, system_b_2 or system_b_4."),
            });
        }
        foreach (var backend in new[] { "resource_preview", "property_preview" })
        {
            var flags = args.EnumerateObject().Where(p => p.NameEquals(backend)).Take(2).ToArray();
            if (flags.Length > 1 || flags.Length == 1 && flags[0].Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ToolException(name + " requires at most one boolean " + backend + ".");
            if (flags.Length == 1 && flags[0].Value.GetBoolean()) throw new ToolException(name + " cannot be combined with " + backend + "=true.");
        }
        if (args.EnumerateObject().Any(p => p.Name is "parameter_subset" or "byte_order" or "load_procedure_preview"
            or "image_composition_preview" or "root_image_build_preview" or "root_resolved_image_build_preview" or "root_property_image_build_preview"))
            throw new ToolException(name + " cannot be combined with another image, procedure or subset preview.");
        return new(Array.AsReadOnly(words.ToArray()), tables);
    }

    private static JsonObject NativeImageBuildSchema() => JsonNode.Parse("""
        {
          "type":"object", "additionalProperties":false, "required":["option_words"],
          "description":"Combined logical working alternatives under explicit native option-word assumptions; never selects actual connected-device options or enables writes.",
          "properties":{
            "option_words":{"type":"array","minItems":1,"maxItems":16,"uniqueItems":true,
              "items":{"type":"integer","minimum":0,"maximum":4294967295}},
            "tables":{"type":"object","additionalProperties":false,"required":["format"],
              "properties":{"format":{"type":"string","enum":["m112","system_b_2","system_b_4"]}}}
          }
        }
        """)!.AsObject();

    private static string NativeImageDiagnosticHash<T>(T value)
    {
        using var hash = SHA256.Create(); using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        JsonSerializer.Serialize(stream, value); stream.FlushFinalBlock(); return Convert.ToHexStringLower(hash.Hash!);
    }
    private static object NativeImageBuildDiagnostic(Project project, DeviceInstance device, NativeImageBuildRequest request)
    {
        const int itemLimit = 128, requirementLimit = 256;
        var recordBudget = itemLimit; var sourceBudget = itemLimit; var chunkBudget = itemLimit; var countBudget = itemLimit;
        var controlBudget = itemLimit; var payloadBudget = itemLimit; var adjustmentBudget = itemLimit; var sourceIdBudget = itemLimit;
        var requirementsBudget = requirementLimit; var issuesBudget = requirementLimit;
        static int TakeCount(int count, ref int budget) { var take = Math.Min(count, budget); budget -= take; return take; }
        var hashes = new Dictionary<ImmutablePatchBytes, string>(ReferenceEqualityComparer.Instance);
        string? Digest(ImmutablePatchBytes? bytes)
        {
            if (bytes is null) return null;
            if (!hashes.TryGetValue(bytes, out var hash)) hashes.Add(bytes, hash = bytes.Sha256);
            return hash;
        }
        static string Code(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
        static object? Selector(PropertySourceSelector? selector) => selector is null ? null : new
        { kind = Code(selector.Kind), propertyId = selector.PropertyId, nativeObjectIndex = selector.NativeObjectIndex,
            applicationObjectIndex = selector.ApplicationObjectIndex, objectType = selector.ObjectType, occurrence = selector.Occurrence };
        static object Target(NativeImageBuildTarget target) => new
        { space = Code(target.Space), loadStateMachine = target.LoadStateMachine, property = Selector(target.Property) };
        static object? Pdt(PropertyPdtLayout? layout) => layout is null ? null : new
        { profileVersion = layout.ProfileVersion, selectionFingerprint = layout.SelectionFingerprint, declaredPdt = layout.DeclaredPdt,
            readElementBytes = layout.ReadElementBytes, writeElementBytes = layout.WriteElementBytes, status = Code(layout.Status) };
        static object Issue(NativeImageBuildIssue issue) => new
        { code = Code(issue.Code), refId = issue.RefId, resourceId = issue.ResourceId, variantOrdinal = issue.VariantOrdinal,
            parameterIssue = issue.ParameterIssue is { } p ? Code(p.Code) : null,
            activationIssue = issue.ActivationIssue is { } a ? Code(a.Code) : null,
            procedureIssue = issue.ProcedureIssue is { } l ? new { code = Code(l.Code), referenceId = l.ReferenceId, sourceOrdinal = l.SourceOrdinal } : null };
        object SourceFacts(NativeImageBuildSource s) => new { s.Kind, s.Order, s.SourceId, s.DeclarationHash, s.Target, s.ByteOffset,
            s.Length, s.DeclaredLength, s.RequestedLength, s.RawSelector, dataHash = Digest(s.RawData), maskHash = Digest(s.RawMask), s.MappingHash, s.PdtLayout, s.FactorySourceOrdinal };
        object ChunkFacts(NativeImageBuildChunk c) => new { c.Key, c.Target, c.ByteOffset, c.Length, c.SourceIds,
            initial = Digest(c.InitialData), initialValidity = Digest(c.InitialValidity), working = Digest(c.WorkingData),
            validity = Digest(c.DesiredValidity), canonical = Digest(c.CanonicalDesired), writes = Digest(c.ParameterWriteBits) };
        object CountFacts(NativeImageBuildCount c) => new { c.SourceOrdinal, c.RawLocation, dataHash = Digest(c.Data), maskHash = Digest(c.Mask), c.EncodedCount, c.Status, c.NotAdopted };
        object ControlFacts(LoadProcedureControl c) => new { c.Ordinal, c.Kind, c.Origin, c.SourceOrdinal, c.TemplateOrdinal, c.MergeId,
            c.DeclaredAppliesTo, c.EffectiveAppliesTo, c.Provenance, c.Arguments, c.OnError, dataHash = Digest(c.InlineData), maskHash = Digest(c.InlineMask) };
        object PayloadFacts(NativeImageBuildTablePayload p) => new { p.Role, p.Target, p.ByteOffset, p.ResourceId, p.Length,
            dataHash = Digest(p.Data), writesHash = Digest(p.WriteMask), beforeWorkingHash = Digest(p.BeforeWorkingData), beforeValidityHash = Digest(p.BeforeValidity), afterValidityHash = Digest(p.AfterValidity) };
        var plan = OfflineNativeImageBuild.Preview(project, device, request);
        var variants = plan.Variants.Select(variant =>
        {
            var population = variant.Population;
            var recordTake = TakeCount(population?.Records.Count ?? 0, ref recordBudget);
            var sourceTake = TakeCount(variant.Sources.Count, ref sourceBudget); var chunkTake = TakeCount(variant.Chunks.Count, ref chunkBudget);
            var countTake = TakeCount(variant.ObservedCounts.Count, ref countBudget);
            var controlTake = TakeCount(variant.Procedure?.EffectiveControls.Count ?? 0, ref controlBudget);
            var requirementTake = TakeCount(variant.Requirements.Count, ref requirementsBudget);
            var issueTake = TakeCount(variant.Issues.Count, ref issuesBudget);
            var records = population?.Records.Take(recordTake).Select(record => new
            {
                refId = record.RefId, parameterId = record.ParameterId, parameterTypeId = record.ParameterTypeId,
                disposition = Code(record.Disposition), active = record.Active, normalReferenceCount = record.NormalReferenceCount,
                legacyPatchAlways = record.LegacyPatchAlways, customerAdjustable = record.CustomerAdjustable, customerExcluded = record.CustomerExcluded,
                currentValueOrigin = Code(record.CurrentValueOrigin), currentValueHash = record.CurrentValueHash, recordValueHash = record.RecordValueHash,
                nativeRecordEmitted = record.NativeRecordEmitted, allocates = record.Allocates, writesData = record.WritesData,
                field = record.Field is { } field ? new { target = Target(field.Target), byteOffset = field.ByteOffset, bitOffset = field.BitOffset, bitLength = field.BitLength } : null,
            }).ToArray();
            var sources = variant.Sources.Take(sourceTake).Select(source => new
            {
                kind = Code(source.Kind), order = source.Order, sourceId = source.SourceId, declarationHash = source.DeclarationHash,
                target = Target(source.Target), byteOffset = source.ByteOffset, byteLength = source.Length, declaredLength = source.DeclaredLength,
                requestedLength = source.RequestedLength, rawSelector = Selector(source.RawSelector), mappingHash = source.MappingHash,
                suppliedDataLength = source.RawData?.Length, rawDataHash = Digest(source.RawData),
                hasExplicitRawMask = source.RawMask is not null, suppliedMaskLength = source.RawMask?.Length, rawMaskHash = Digest(source.RawMask),
                pdtLayout = Pdt(source.PdtLayout), factorySourceOrdinal = source.FactorySourceOrdinal,
            }).ToArray();
            var chunks = variant.Chunks.Take(chunkTake).Select(chunk =>
            {
                var sourceIdsTake = TakeCount(chunk.SourceIds.Count, ref sourceIdBudget);
                return new {
                key = chunk.Key, target = Target(chunk.Target), byteOffset = chunk.ByteOffset, byteLength = chunk.Length,
                sourceIdsTotal = chunk.SourceIds.Count, sourceIdsShown = sourceIdsTake,
                sourceIdsTruncated = chunk.SourceIds.Count > sourceIdsTake, sourceIdsHash = NativeImageDiagnosticHash(chunk.SourceIds), sourceIds = chunk.SourceIds.Take(sourceIdsTake).ToArray(),
                initialDataHash = Digest(chunk.InitialData), hasInitialValidity = chunk.InitialValidity is not null, initialValidityHash = Digest(chunk.InitialValidity),
                workingDataHash = Digest(chunk.WorkingData), hasDesiredValidity = chunk.DesiredValidity is not null, desiredValidityHash = Digest(chunk.DesiredValidity),
                canonicalDesiredHash = Digest(chunk.CanonicalDesired), parameterWriteBitsHash = Digest(chunk.ParameterWriteBits),
                };
            }).ToArray();
            var counts = variant.ObservedCounts.Take(countTake).Select(count => new
            {
                sourceOrdinal = count.SourceOrdinal, rawLocation = new { addressSpace = Code(count.RawLocation.AddressSpace), component = Code(count.RawLocation.Component),
                    count.RawLocation.ByteOffset, count.RawLocation.NativeObjectIndex, count.RawLocation.ApplicationObjectIndex,
                    count.RawLocation.ObjectType, count.RawLocation.Occurrence, count.RawLocation.PropertyId },
                byteLength = count.Data.Length, dataHash = Digest(count.Data), hasExplicitMask = count.Mask is not null,
                maskHash = Digest(count.Mask), encodedCount = count.EncodedCount, status = Code(count.Status), notAdopted = count.NotAdopted,
            }).ToArray();
            var controls = variant.Procedure?.EffectiveControls.Take(controlTake).Select(control => new
            {
                ordinal = control.Ordinal, kind = Code(control.Kind), origin = Code(control.Origin), sourceOrdinal = control.SourceOrdinal,
                templateOrdinal = control.TemplateOrdinal, mergeId = control.MergeId, declaredAppliesTo = Code(control.DeclaredAppliesTo),
                effectiveAppliesTo = Code(control.EffectiveAppliesTo), provenanceHash = NativeImageDiagnosticHash(control.Provenance),
                argumentsTotal = control.Arguments.Count, argumentsHash = NativeImageDiagnosticHash(control.Arguments),
                onErrorTotal = control.OnError.Count, onErrorHash = NativeImageDiagnosticHash(control.OnError),
                inlineDataLength = control.InlineData?.Length, inlineDataHash = Digest(control.InlineData),
                inlineMaskLength = control.InlineMask?.Length, inlineMaskHash = Digest(control.InlineMask),
            }).ToArray();
            var tables = variant.Tables;
            var payloadTake = TakeCount(tables?.Payloads.Count ?? 0, ref payloadBudget);
            var adjustmentTake = TakeCount(tables?.ProcedureAdjustments.Count ?? 0, ref adjustmentBudget);
            var tableRequirementTake = TakeCount(tables?.Requirements.Count ?? 0, ref requirementsBudget);
            var payloads = tables?.Payloads.Take(payloadTake).Select(payload => new
            {
                role = Code(payload.Role), target = Target(payload.Target), byteOffset = payload.ByteOffset, byteLength = payload.Length,
                resourceId = payload.ResourceId, dataHash = Digest(payload.Data), writeMaskHash = Digest(payload.WriteMask),
                beforeWorkingDataHash = Digest(payload.BeforeWorkingData), beforeValidityHash = Digest(payload.BeforeValidity),
                afterValidityHash = Digest(payload.AfterValidity),
            }).ToArray();
            return new
            {
                ordinal = variant.Ordinal, assumedOptions = variant.AssumedOptions, available = variant.IsAvailable,
                toolManagedIp = variant.ToolManagedIp is { } ip ? new { value = ip.Value, sourceProven = ip.SourceProven } : null,
                tasks = new { basicProcedure = variant.Tasks.BasicProcedure, addCode = variant.Tasks.AddCode, parameters = variant.Tasks.Parameters, tables = variant.Tasks.Tables },
                fingerprint = variant.Fingerprint, initializationHash = variant.InitializationHash, coverageHash = variant.CoverageHash,
                population = population is null ? null : new
                {
                    parameterTaskScheduled = population.ParameterTaskScheduled, completeAdmittedRootPopulation = population.CompleteAdmittedRootPopulation,
                    customerPolicy = population.CustomerPolicy, referenceCount = population.ReferenceCount, ownerCount = population.OwnerCount,
                    physicalRecordCount = population.PhysicalRecordCount, noStorageCount = population.NoStorageCount, activeCount = population.ActiveCount,
                    alwaysCount = population.AlwaysCount, inactiveCount = population.InactiveCount, customerExcludedCount = population.CustomerExcludedCount,
                    populationHash = population.PopulationHash, geometryHash = population.GeometryHash, maskInteractionHash = population.MaskInteractionHash,
                    recordsHash = NativeImageDiagnosticHash(population.Records),
                    recordsTotal = population.Records.Count, recordsShown = records?.Length ?? 0, recordsTruncated = population.Records.Count > recordTake, records,
                },
                procedure = variant.Procedure is { } procedure ? new
                {
                    state = Code(procedure.State), nativeSubtype = procedure.NativeSubtype, selectedSourceOrdinal = procedure.SelectedSourceOrdinal,
                    selectedSourceHash = procedure.SelectedSourceHash, rank = procedure.Rank, declarationWitnessHash = procedure.DeclarationWitnessHash,
                    fingerprint = procedure.Fingerprint, controlsTotal = procedure.EffectiveControls.Count, controlsShown = controls?.Length ?? 0,
                    controlsHash = NativeImageDiagnosticHash(procedure.EffectiveControls.Select(ControlFacts)), controlsTruncated = procedure.EffectiveControls.Count > controlTake, controls,
                } : null,
                tables = tables is null ? null : new
                {
                    format = tables.Format switch { GeneratedTableFormat.M112 => "m112", GeneratedTableFormat.SystemB2ByteAssociations => "system_b_2", GeneratedTableFormat.SystemB4ByteAssociations => "system_b_4", _ => null },
                    fingerprint = tables.Fingerprint, currentSeedHash = tables.CurrentSeedHash, nativeWitnessHash = tables.NativeWitnessHash,
                    payloadsHash = NativeImageDiagnosticHash(tables.Payloads.Select(PayloadFacts)),
                    payloadsTotal = tables.Payloads.Count, payloadsShown = payloads?.Length ?? 0, payloadsTruncated = tables.Payloads.Count > payloadTake, payloads,
                    procedureAdjustmentsTotal = tables.ProcedureAdjustments.Count, procedureAdjustmentsShown = adjustmentTake,
                    procedureAdjustmentsTruncated = tables.ProcedureAdjustments.Count > adjustmentTake, procedureAdjustmentsHash = NativeImageDiagnosticHash(tables.ProcedureAdjustments),
                    procedureAdjustments = tables.ProcedureAdjustments.Take(adjustmentTake).Select(a => new
                    { controlOrdinal = a.ControlOrdinal, kind = Code(a.Kind), argumentName = a.ArgumentName, beforeValue = a.BeforeValue, afterValue = a.AfterValue, role = Code(a.Role) }).ToArray(),
                    requirementsTotal = tables.Requirements.Count, requirementsShown = tableRequirementTake,
                    requirementsTruncated = tables.Requirements.Count > tableRequirementTake, requirementsHash = NativeImageDiagnosticHash(tables.Requirements), requirements = tables.Requirements.Take(tableRequirementTake).ToArray(),
                },
                issuesTotal = variant.Issues.Count, issuesShown = issueTake, issuesTruncated = variant.Issues.Count > issueTake,
                issuesHash = NativeImageDiagnosticHash(variant.Issues), issues = variant.Issues.Take(issueTake).Select(Issue).ToArray(),
                sourcesHash = NativeImageDiagnosticHash(variant.Sources.Select(SourceFacts)), sourcesTotal = variant.Sources.Count, sourcesShown = sources.Length, sourcesTruncated = variant.Sources.Count > sourceTake, sources,
                chunksHash = NativeImageDiagnosticHash(variant.Chunks.Select(ChunkFacts)), chunksTotal = variant.Chunks.Count, chunksShown = chunks.Length, chunksTruncated = variant.Chunks.Count > chunkTake, chunks,
                observedCountsHash = NativeImageDiagnosticHash(variant.ObservedCounts.Select(CountFacts)), observedCountsTotal = variant.ObservedCounts.Count, observedCountsShown = counts.Length, observedCountsTruncated = variant.ObservedCounts.Count > countTake, observedCounts = counts,
                requirementsTotal = variant.Requirements.Count, requirementsShown = requirementTake,
                requirementsTruncated = variant.Requirements.Count > requirementTake, requirementsHash = NativeImageDiagnosticHash(variant.Requirements), requirements = variant.Requirements.Take(requirementTake).ToArray(),
            };
        }).ToArray();
        return new
        {
            available = plan.IsAvailable, readOnly = true, scope = plan.Scope, profileVersion = plan.ProfileVersion,
            hasGlobalFailure = plan.HasGlobalFailure, allRequestedAssumptionsAvailable = plan.AllRequestedAssumptionsAvailable,
            actualOptionsKnown = plan.ActualOptionsKnown, completeApplicationImage = plan.CompleteApplicationImage,
            executionEligible = plan.ExecutionEligible, downloadEligible = plan.DownloadEligible,
            sourceFingerprint = plan.SourceFingerprint, sourceHash = plan.SourceHash, applicationId = plan.ApplicationId, maskVersionId = plan.MaskVersionId,
            applicationDefinitionHash = plan.ApplicationDefinitionHash, hardwareDefinitionHash = plan.HardwareDefinitionHash,
            preparationHash = plan.PreparationHash, sharedContextHash = plan.SharedContextHash,
            policy = plan.Policy is not { } policy ? null : new
            {
                byteOrder = Code(policy.ByteOrder), downloadBehavior = Code(policy.DownloadBehavior), maskPolicy = Code(policy.MaskPolicy),
                minEtsVersion = policy.MinEtsVersion, packedMinEtsVersion = policy.PackedMinEtsVersion, optionsDeclared = policy.OptionsDeclared,
                declaredDownloadBehavior = policy.DeclaredDownloadBehavior, declaredByteOrder = policy.DeclaredByteOrder,
                codecProfileVersion = policy.CodecProfileVersion, numericParseProfile = policy.NumericParseProfile,
                packedTimeEncodingProfile = policy.PackedTimeEncodingProfile, denseTimeEncodingProfile = policy.DenseTimeEncodingProfile,
                physicalFieldCount = policy.PhysicalFieldCount, singleFieldCount = policy.SingleFieldCount,
                packedTimeFieldCount = policy.PackedTimeFieldCount, denseTimeFieldCount = policy.DenseTimeFieldCount,
                rawDataFieldCount = policy.RawDataFieldCount, rawDataEncodingProfile = policy.RawDataEncodingProfile,
                sourcePolicyFingerprint = policy.SourcePolicyFingerprint, codecFactsFingerprint = policy.CodecFactsFingerprint, fingerprint = policy.Fingerprint,
            },
            declaredReferenceCount = plan.DeclaredReferenceCount, declaredOwnerCount = plan.DeclaredOwnerCount,
            allocatedImagePayloadBytes = plan.AllocatedImagePayloadBytes, reservedWorstCaseImagePayloadBytes = plan.ReservedWorstCaseImagePayloadBytes,
            reservedProcedurePayloadBytes = plan.ReservedProcedurePayloadBytes,
            assumedOptions = plan.AssumedOptions, binding = plan.Binding is { } binding ? new { fingerprint = binding.Fingerprint } : null,
            issuesTotal = plan.Issues.Count, issuesShown = Math.Min(plan.Issues.Count, requirementLimit), issuesTruncated = plan.Issues.Count > requirementLimit,
            issuesHash = NativeImageDiagnosticHash(plan.Issues), issues = plan.Issues.Take(requirementLimit).Select(Issue).ToArray(), variantsTotal = plan.Variants.Count, variantsShown = variants.Length,
            variantsTruncated = false, variants,
            presentationLimits = new { records = itemLimit, sources = itemLimit, chunks = itemLimit, observedCounts = itemLimit,
                controls = itemLimit, tablePayloads = itemLimit, procedureAdjustments = itemLimit, sourceIds = itemLimit, requirements = requirementLimit,
                detailBudgetSharedAcrossVariants = true },
            interpretation = "Conditional logical working alternatives, including unavailable assumptions. Sources, initial data, working data and validity remain separate. Table inputs are current working bytes. Actual device options, physical transfer mapping, count capacity and finalization are unverified; no device operation is authorized.",
        };
    }
}
