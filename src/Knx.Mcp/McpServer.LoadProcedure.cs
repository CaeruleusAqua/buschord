using System.Text.Json;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private sealed record ApplicationBuildOptions(LoadProcedureRequest Procedure, GeneratedTableRequest? Tables, bool ResolveConditions = false);

    private static ApplicationBuildOptions? ReadApplicationBuildOptions(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        var count = args.EnumerateObject().Count(p => p.NameEquals("load_procedure_preview"));
        if (count == 0) return null;
        if (count != 1 || args.GetProperty("load_procedure_preview") is not { ValueKind: JsonValueKind.Object } option)
            throw new ToolException("load_procedure_preview must be one explicit object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in option.EnumerateObject())
            if (property.Name is not ("subtype" or "access" or "full" or "parameters" or "groups" or "generated_tables" or "resolve_conditions") || !names.Add(property.Name))
                throw new ToolException("load_procedure_preview contains an unknown or duplicate field.");
        if (!new[] { "subtype", "access", "full", "parameters", "groups" }.All(names.Contains)
            || option.GetProperty("subtype").ValueKind != JsonValueKind.String
            || option.GetProperty("subtype").GetString() is not ("all" or "par" or "grp" or "par,grp" or "ap1" or "cfg")
            || option.GetProperty("access").ValueKind != JsonValueKind.String || option.GetProperty("access").GetString() != "remote")
            throw new ToolException("load_procedure_preview requires explicit subtype, remote access and three boolean load parts.");
        LoadParts parts = LoadParts.None;
        foreach (var (name, part) in new[] { ("full", LoadParts.Full), ("parameters", LoadParts.Parameters), ("groups", LoadParts.Groups) })
        {
            var value = option.GetProperty(name);
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ToolException("load_procedure_preview load parts must be explicit booleans.");
            if (value.GetBoolean()) parts |= part;
        }
        var resolveConditions = false;
        if (option.TryGetProperty("resolve_conditions", out var resolveOption))
        {
            if (resolveOption.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ToolException("load_procedure_preview.resolve_conditions must be one explicit boolean.");
            resolveConditions = resolveOption.GetBoolean();
        }
        if (resolveConditions && option.TryGetProperty("generated_tables", out _))
            throw new ToolException("Resolved load conditions currently require a procedure-only preview without generated_tables.");
        GeneratedTableRequest? tables = null;
        if (option.TryGetProperty("generated_tables", out var tableOption))
        {
            if (tableOption.ValueKind != JsonValueKind.Object || tableOption.EnumerateObject().Count() != 1
                || !tableOption.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String)
                throw new ToolException("load_procedure_preview.generated_tables requires exactly one explicit format.");
            var selected = format.GetString() switch
            {
                "m112" => GeneratedTableFormat.M112,
                "system_b_2" => GeneratedTableFormat.SystemB2ByteAssociations,
                "system_b_4" => GeneratedTableFormat.SystemB4ByteAssociations,
                _ => throw new ToolException("load_procedure_preview.generated_tables format must be m112, system_b_2 or system_b_4."),
            };
            if ((parts & (LoadParts.Full | LoadParts.Groups)) == LoadParts.None)
                throw new ToolException("load_procedure_preview.generated_tables requires full or groups to be selected.");
            tables = new(selected);
        }
        return new(new(option.GetProperty("subtype").GetString()!, LoadProcedureAccess.Remote, parts), tables, resolveConditions);
    }

    private static object ApplicationBuildDiagnostic(Project project, DeviceInstance device, ApplicationBuildOptions options)
    {
        if (options.ResolveConditions) return ResolvedApplicationBuildDiagnostic(project, device, options.Procedure);
        var request = options.Procedure;
        const int controlLimit = 512, mergeLimit = 256, resourceLimit = 64;
        static string Code<T>(T value) where T : Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
        static object? Arena(ResourcePatchArena? arena) => arena is null ? null : new { kind = Code(arena.Kind), loadStateMachine = arena.LoadStateMachine };
        static object? Location(ArchivedResourceLocation? location) => location is null ? null : new
        {
            addressSpace = Code(location.AddressSpace), byteOffset = location.Offset,
            nativeObjectIndex = location.NativeObjectIndex, applicationObjectIndex = location.ApplicationObjectIndex,
            objectType = location.ObjectType, occurrence = location.Occurrence, propertyId = location.PropertyId,
            pointerResource = location.PointerResource, interfaceObjectRef = location.InterfaceObjectRef,
        };
        var plan = options.Tables is null ? OfflineApplicationBuildPlan.Preview(project, device, request)
            : OfflineApplicationBuildPlan.Preview(project, device, request, options.Tables);
        var procedure = plan.Procedure;
        return new
        {
            scope = plan.Scope, profileVersion = plan.ProfileVersion, available = plan.IsAvailable, readOnly = true,
            executionEligible = plan.ExecutionEligible, completeApplicationImage = plan.CompleteApplicationImage,
            downloadEligible = plan.DownloadEligible,
            request = new
            {
                subtype = request.NativeSubtype, access = "remote", full = request.Parts.HasFlag(LoadParts.Full),
                parameters = request.Parts.HasFlag(LoadParts.Parameters), groups = request.Parts.HasFlag(LoadParts.Groups),
            },
            sources = ArchivedImageSourceDiagnostic(plan.Sources),
            binding = plan.Binding is { } binding ? new
            {
                profileVersion = binding.ProfileVersion, sourceFingerprint = binding.SourceFingerprint,
                procedureWitnessHash = binding.ProcedureWitnessHash, projectInputHash = binding.ProjectInputHash, fingerprint = binding.Fingerprint,
            } : null,
            issues = plan.Issues.Select(issue => new { code = Code(issue.Code), referenceId = issue.ReferenceId, sourceOrdinal = issue.SourceOrdinal }).ToArray(),
            procedure = procedure is null ? null : new
            {
                profileVersion = procedure.ProfileVersion, scope = procedure.Scope, available = procedure.IsAvailable,
                applicationId = procedure.ApplicationId, maskVersionId = procedure.MaskVersionId,
                style = procedure.Style is { } style ? Code(style) : null,
                applicationDefinitionHash = procedure.ApplicationDefinitionHash, selectedProfileHash = procedure.SelectedProfileHash,
                witnessHash = procedure.WitnessHash, controlCount = procedure.Controls.Count, controlLimit,
                controlsTruncated = procedure.Controls.Count > controlLimit,
                controls = procedure.Controls.Take(controlLimit).Select(control => new
                {
                    ordinal = control.Ordinal, kind = Code(control.Kind), origin = Code(control.Origin),
                    sourceOrdinal = control.SourceOrdinal, templateOrdinal = control.TemplateOrdinal, mergeId = control.MergeId,
                    declaredAppliesTo = Code(control.DeclaredAppliesTo), effectiveAppliesTo = Code(control.EffectiveAppliesTo),
                    arguments = control.Arguments.Select(argument => new
                    {
                        name = argument.Name, kind = Code(argument.Kind), unsignedValue = argument.UnsignedValue,
                        booleanValue = argument.BooleanValue, textValue = argument.TextValue,
                    }).ToArray(),
                    onError = control.OnError.Select(error => new { cause = Code(error.Cause), ignore = error.Ignore, messageRef = error.MessageRef }).ToArray(),
                    inlineDataLength = control.InlineData?.Length, inlineDataHash = control.InlineData?.Sha256,
                    inlineMaskLength = control.InlineMask?.Length, inlineMaskHash = control.InlineMask?.Sha256,
                }).ToArray(),
                mergeDecisionCount = procedure.MergeDecisions.Count, mergeLimit, mergesTruncated = procedure.MergeDecisions.Count > mergeLimit,
                mergeDecisions = procedure.MergeDecisions.Take(mergeLimit).Select(merge => new
                { mergeId = merge.MergeId, templateOrdinal = merge.TemplateOrdinal, decision = Code(merge.Decision), emittedControls = merge.EmittedControls }).ToArray(),
                resourceCount = procedure.ResourceReferences.Count, resourceLimit, resourcesTruncated = procedure.ResourceReferences.Count > resourceLimit,
                resources = procedure.ResourceReferences.Take(resourceLimit).Select(resource => new
                {
                    id = resource.Id, arena = Arena(resource.Arena), baseOffsetInArena = resource.BaseOffset,
                    declaredSize = resource.DeclaredSize, dataLength = resource.DataLength, maskLength = resource.MaskLength,
                    dataHash = resource.DataHash, maskHash = resource.MaskHash,
                }).ToArray(),
                tableRequirements = procedure.TableRequirements.Select(table => new
                {
                    role = Code(table.Role), codeSegmentId = table.CodeSegmentId, offsetInResource = table.Offset,
                    declaredMaxEntries = table.MaxEntries, arena = Arena(table.Arena), byteOffsetInArena = table.ByteOffsetInArena,
                    maskResourceFlavour = table.MaskResourceFlavour, maskResourceLocation = Location(table.MaskResourceLocation),
                }).ToArray(),
                unresolvedRequirements = procedure.Requirements.Select(requirement => new
                { code = Code(requirement.Code), referenceId = requirement.ReferenceId }).ToArray(),
                executionEligible = procedure.ExecutionEligible, completeApplicationImage = procedure.CompleteApplicationImage,
                downloadEligible = procedure.DownloadEligible,
            },
            generatedTables = plan.GeneratedTables is { } generated ? GeneratedTableDiagnostic(generated) : null,
            interpretation = options.Tables is null
                ? "Explicit primary-program control compilation with declared project inputs. Controls remain offline; generated tables, activation, fixups and finalization are unresolved. No device commands or complete application image are produced."
                : "Explicit offline table fragments use fresh object activity, declared formats and separate source bindings. The primary procedure retains its original stage requirements; table size adjustments are separate. Fixups, finalization and a complete application image remain unresolved. No device commands are produced.",
        };
    }

    private static object GeneratedTableDiagnostic(GeneratedTablePreview preview)
    {
        const int payloadLimit = 4, adjustmentLimit = 512;
        static string Code<T>(T value) where T : Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
        return new
        {
            scope = preview.Scope, profileVersion = preview.ProfileVersion, available = preview.IsAvailable, readOnly = true,
            format = preview.Format switch
            {
                GeneratedTableFormat.M112 => "m112", GeneratedTableFormat.SystemB2ByteAssociations => "system_b_2",
                GeneratedTableFormat.SystemB4ByteAssociations => "system_b_4", _ => null,
            },
            nativeWitnessHash = preview.NativeWitnessHash,
            binding = preview.Binding is { } binding ? new
            {
                profileVersion = binding.ProfileVersion, sourceFingerprint = binding.SourceFingerprint,
                procedureWitnessHash = binding.ProcedureWitnessHash, nativeWitnessHash = binding.NativeWitnessHash,
                payloadHash = binding.PayloadHash, requiresScripts = binding.RequiresScripts, fingerprint = binding.Fingerprint,
            } : null,
            issues = preview.Issues.Select(issue => new { code = Code(issue.Code), referenceId = issue.RefId, resourceId = issue.ResourceId }).ToArray(),
            unresolvedRequirements = preview.Requirements.Select(requirement => new { code = Code(requirement.Code), resourceId = requirement.ResourceId }).ToArray(),
            payloadCount = preview.Payloads.Count, payloadLimit, payloadsTruncated = preview.Payloads.Count > payloadLimit,
            payloads = preview.Payloads.Take(payloadLimit).Select(payload => new
            {
                role = Code(payload.Role), arena = new { kind = Code(payload.Arena.Kind), loadStateMachine = payload.Arena.LoadStateMachine },
                byteOffsetInArena = payload.ByteOffset, origin = Code(payload.Origin), resourceId = payload.ResourceId,
                length = payload.Length, dataHash = payload.Data.Sha256, writeMaskHash = payload.WriteMask.Sha256,
                writeMaskLength = payload.WriteMask.Length,
                beforeDataLength = payload.BeforeData?.Length, beforeDataHash = payload.BeforeData?.Sha256,
                beforeMaskLength = payload.BeforeMask?.Length, beforeMaskHash = payload.BeforeMask?.Sha256,
                afterMaskLength = payload.AfterMask?.Length, afterMaskHash = payload.AfterMask?.Sha256,
            }).ToArray(),
            procedureSizeAdjustmentCount = preview.ProcedureSizeAdjustments.Count, adjustmentLimit,
            adjustmentsTruncated = preview.ProcedureSizeAdjustments.Count > adjustmentLimit,
            procedureSizeAdjustments = preview.ProcedureSizeAdjustments.Take(adjustmentLimit).Select(adjustment => new
            {
                controlOrdinal = adjustment.ControlOrdinal, kind = Code(adjustment.Kind), argumentName = adjustment.ArgumentName,
                beforeValue = adjustment.BeforeValue, afterValue = adjustment.AfterValue, role = Code(adjustment.Role),
            }).ToArray(),
            executionEligible = preview.ExecutionEligible, completeApplicationImage = preview.CompleteApplicationImage,
            downloadEligible = preview.DownloadEligible,
        };
    }
}
