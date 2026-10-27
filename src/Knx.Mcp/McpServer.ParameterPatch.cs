using System.Text.Json;
using System.Text.Json.Nodes;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private sealed record ParameterPatchOptions(IReadOnlyList<ParameterPatchValue> Values, ParameterPatchByteOrder ByteOrder,
        bool ResourcePreview = false, bool PropertyPreview = false);

    private static JsonObject DownloadPlanSchema()
    {
        var schema = Schema(
            StrProp("device_address", "Individual address of a project device, e.g. 1.1.5.", true),
            StrProp("gateway", "Optional gateway to read the descriptor from; omit for a purely offline plan."),
            IntProp("timeout_ms", "Gateway search duration in ms (100..10000, default 1500)."),
            IntProp("response_timeout_ms", "Descriptor response timeout in ms (100..5000, default 1000)."),
            EnumProp("byte_order", "Explicit byte order for the optional subset preview; requires parameter_subset.", ["big_endian", "little_endian"]),
            BoolProp("resource_preview", "Explicitly select the typed offline resource backend for parameter_subset and byte_order; default false. Offsets are logical arena coordinates, never download authority."),
            BoolProp("property_preview", "Explicitly preview bounded root numeric property fields using complete archived data/count buffers and declared PDT widths; requires parameter_subset and byte_order, excludes resource_preview=true. Default false."));
        schema["properties"]!["parameter_subset"] = new JsonObject
        {
            ["type"] = "array", ["description"] = "Optional explicit numeric subset preview (Subset-Vorschau), not an application image; requires byte_order. Never changes saved values or download gates.",
            ["minItems"] = 1, ["maxItems"] = 64,
            ["items"] = new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = new JsonArray("refId", "rawValue"),
                ["properties"] = new JsonObject
                {
                    ["refId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 256 },
                    ["rawValue"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 64 },
                },
            },
        };
        schema["dependentRequired"] = new JsonObject
        {
            ["parameter_subset"] = new JsonArray("byte_order"), ["byte_order"] = new JsonArray("parameter_subset"),
        };
        schema["allOf"] = new JsonArray(new JsonObject
        {
            ["if"] = new JsonObject
            {
                ["required"] = new JsonArray("resource_preview"),
                ["properties"] = new JsonObject { ["resource_preview"] = new JsonObject { ["const"] = true } },
            },
            ["then"] = new JsonObject { ["required"] = new JsonArray("parameter_subset", "byte_order") },
        }, new JsonObject
        {
            ["if"] = new JsonObject
            {
                ["required"] = new JsonArray("property_preview"),
                ["properties"] = new JsonObject { ["property_preview"] = new JsonObject { ["const"] = true } },
            },
            ["then"] = new JsonObject
            {
                ["required"] = new JsonArray("parameter_subset", "byte_order"),
                ["not"] = new JsonObject
                {
                    ["required"] = new JsonArray("resource_preview"),
                    ["properties"] = new JsonObject { ["resource_preview"] = new JsonObject { ["const"] = true } },
                },
            },
        });
        schema["properties"]!["load_procedure_preview"] = new JsonObject
        {
            ["type"] = "object", ["description"] = "Explicit offline primary-program procedure and load-parts selection. Does not execute controls or build complete images.",
            ["properties"] = new JsonObject
            {
                ["subtype"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("all", "par", "grp", "par,grp", "ap1", "cfg") },
                ["access"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("remote") },
                ["full"] = new JsonObject { ["type"] = "boolean" },
                ["parameters"] = new JsonObject { ["type"] = "boolean" },
                ["groups"] = new JsonObject { ["type"] = "boolean" },
                ["resolve_conditions"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Explicitly resolve bounded root load conditions without scripts; procedure-only, cannot be combined with generated_tables. Default false preserves the existing preview.",
                },
                ["generated_tables"] = new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("format"),
                    ["description"] = "Explicit offline communication-table format; requires full or groups. Does not enable scripts or device writes.",
                    ["properties"] = new JsonObject
                    {
                        ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("m112", "system_b_2", "system_b_4") },
                    },
                },
            },
            ["required"] = new JsonArray("subtype", "access", "full", "parameters", "groups"), ["additionalProperties"] = false,
            ["allOf"] = new JsonArray(new JsonObject
            {
                ["if"] = new JsonObject { ["required"] = new JsonArray("generated_tables") },
                ["then"] = new JsonObject
                {
                    ["anyOf"] = new JsonArray(
                        new JsonObject { ["properties"] = new JsonObject { ["full"] = new JsonObject { ["const"] = true } } },
                        new JsonObject { ["properties"] = new JsonObject { ["groups"] = new JsonObject { ["const"] = true } } }),
                },
            }, new JsonObject
            {
                ["if"] = new JsonObject
                {
                    ["required"] = new JsonArray("resolve_conditions"),
                    ["properties"] = new JsonObject { ["resolve_conditions"] = new JsonObject { ["const"] = true } },
                },
                ["then"] = new JsonObject { ["not"] = new JsonObject { ["required"] = new JsonArray("generated_tables") } },
            }),
        };
        schema["properties"]!["image_composition_preview"] = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["description"] = "Explicit root static-code working-set composition. Does not apply parameter/table writers, choose a device load route or supply a complete image.",
            ["required"] = new JsonArray("mask_policy"),
            ["properties"] = new JsonObject
            {
                ["mask_policy"] = new JsonObject
                {
                    ["type"] = "string", ["enum"] = new JsonArray("source_defined_bits", "no_static_source_bits"),
                    ["description"] = "Explicit source-contribution policy; independent of load-procedure Full/Parameters/Groups flags.",
                },
            },
        };
        schema["properties"]!["root_image_build_preview"] = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["description"] = "Build a bounded complete fixed-root parameter population with static code and optional tables from one evaluation. Requires load_procedure_preview; native byte order comes from the application. No complete image or device-write authority.",
            ["required"] = new JsonArray("mask_policy"),
            ["properties"] = new JsonObject
            {
                ["mask_policy"] = new JsonObject
                { ["type"] = "string", ["enum"] = new JsonArray("source_defined_bits", "no_static_source_bits") },
            },
        };
        schema["dependentRequired"]!["root_image_build_preview"] = new JsonArray("load_procedure_preview");
        schema["allOf"]!.AsArray().Add(new JsonObject
        {
            ["if"] = new JsonObject { ["required"] = new JsonArray("root_image_build_preview") },
            ["then"] = new JsonObject
            {
                ["not"] = new JsonObject
                {
                    ["anyOf"] = new JsonArray(
                        new JsonObject { ["required"] = new JsonArray("parameter_subset") },
                        new JsonObject { ["required"] = new JsonArray("byte_order") },
                        new JsonObject { ["required"] = new JsonArray("image_composition_preview") }),
                },
                ["properties"] = new JsonObject
                {
                    ["load_procedure_preview"] = new JsonObject
                    {
                        ["anyOf"] = new JsonArray(
                            new JsonObject { ["properties"] = new JsonObject { ["full"] = new JsonObject { ["const"] = true } } },
                            new JsonObject { ["properties"] = new JsonObject { ["parameters"] = new JsonObject { ["const"] = true } } }),
                        ["not"] = new JsonObject
                        {
                            ["required"] = new JsonArray("resolve_conditions"),
                            ["properties"] = new JsonObject { ["resolve_conditions"] = new JsonObject { ["const"] = true } },
                        },
                    },
                },
            },
        });
        schema["properties"]!["root_resolved_image_build_preview"] = new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["description"] = "Explicit shared resolved-root working set with its own procedure selection and optional tables. One application evaluation; no device-write authority.",
            ["required"] = new JsonArray("mask_policy", "procedure"),
            ["properties"] = new JsonObject
            {
                ["mask_policy"] = new JsonObject
                { ["type"] = "string", ["enum"] = new JsonArray("source_defined_bits", "no_static_source_bits") },
                ["procedure"] = new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false,
                    ["required"] = new JsonArray("subtype", "access", "full", "parameters", "groups"),
                    ["properties"] = new JsonObject
                    {
                        ["subtype"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("all", "par", "grp", "par,grp", "ap1", "cfg") },
                        ["access"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("remote") },
                        ["full"] = new JsonObject { ["type"] = "boolean" },
                        ["parameters"] = new JsonObject { ["type"] = "boolean" },
                        ["groups"] = new JsonObject { ["type"] = "boolean" },
                    },
                    ["anyOf"] = new JsonArray(
                        new JsonObject { ["properties"] = new JsonObject { ["full"] = new JsonObject { ["const"] = true } } },
                        new JsonObject { ["properties"] = new JsonObject { ["parameters"] = new JsonObject { ["const"] = true } } }),
                },
                ["generated_tables"] = new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("format"),
                    ["properties"] = new JsonObject
                    { ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("m112", "system_b_2", "system_b_4") } },
                },
            },
            ["allOf"] = new JsonArray(new JsonObject
            {
                ["if"] = new JsonObject { ["required"] = new JsonArray("generated_tables") },
                ["then"] = new JsonObject
                {
                    ["properties"] = new JsonObject
                    {
                        ["procedure"] = new JsonObject
                        {
                            ["anyOf"] = new JsonArray(
                                new JsonObject { ["properties"] = new JsonObject { ["full"] = new JsonObject { ["const"] = true } } },
                                new JsonObject { ["properties"] = new JsonObject { ["groups"] = new JsonObject { ["const"] = true } } }),
                        },
                    },
                },
            }),
        };
        schema["allOf"]!.AsArray().Add(new JsonObject
        {
            ["if"] = new JsonObject { ["required"] = new JsonArray("root_resolved_image_build_preview") },
            ["then"] = new JsonObject
            {
                ["not"] = new JsonObject
                {
                    ["anyOf"] = new JsonArray(
                        new JsonObject { ["required"] = new JsonArray("parameter_subset") },
                        new JsonObject { ["required"] = new JsonArray("byte_order") },
                        new JsonObject { ["required"] = new JsonArray("image_composition_preview") },
                        new JsonObject { ["required"] = new JsonArray("root_image_build_preview") },
                        new JsonObject { ["required"] = new JsonArray("load_procedure_preview") }),
                },
            },
        });
        schema["properties"]!["root_property_image_build_preview"] = RootPropertyBuildSchema();
        schema["allOf"]!.AsArray().Add(new JsonObject
        {
            ["if"] = new JsonObject { ["required"] = new JsonArray("root_property_image_build_preview") },
            ["then"] = new JsonObject
            {
                ["not"] = new JsonObject
                {
                    ["anyOf"] = new JsonArray(
                        new JsonObject { ["required"] = new JsonArray("parameter_subset") },
                        new JsonObject { ["required"] = new JsonArray("byte_order") },
                        new JsonObject { ["required"] = new JsonArray("image_composition_preview") },
                        new JsonObject { ["required"] = new JsonArray("root_image_build_preview") },
                        new JsonObject { ["required"] = new JsonArray("root_resolved_image_build_preview") },
                        new JsonObject { ["required"] = new JsonArray("load_procedure_preview") },
                        new JsonObject
                        {
                            ["required"] = new JsonArray("resource_preview"),
                            ["properties"] = new JsonObject { ["resource_preview"] = new JsonObject { ["const"] = true } },
                        },
                        new JsonObject
                        {
                            ["required"] = new JsonArray("property_preview"),
                            ["properties"] = new JsonObject { ["property_preview"] = new JsonObject { ["const"] = true } },
                        }),
                },
            },
        });
        schema["properties"]!["native_image_build_preview"] = NativeImageBuildSchema();
        schema["allOf"]!.AsArray().Add(new JsonObject
        {
            ["if"] = new JsonObject { ["required"] = new JsonArray("native_image_build_preview") },
            ["then"] = new JsonObject
            {
                ["not"] = new JsonObject
                {
                    ["anyOf"] = new JsonArray(
                        new JsonObject { ["required"] = new JsonArray("parameter_subset") },
                        new JsonObject { ["required"] = new JsonArray("byte_order") },
                        new JsonObject { ["required"] = new JsonArray("load_procedure_preview") },
                        new JsonObject { ["required"] = new JsonArray("image_composition_preview") },
                        new JsonObject { ["required"] = new JsonArray("root_image_build_preview") },
                        new JsonObject { ["required"] = new JsonArray("root_resolved_image_build_preview") },
                        new JsonObject { ["required"] = new JsonArray("root_property_image_build_preview") },
                        new JsonObject { ["required"] = new JsonArray("resource_preview"), ["properties"] = new JsonObject { ["resource_preview"] = new JsonObject { ["const"] = true } } },
                        new JsonObject { ["required"] = new JsonArray("property_preview"), ["properties"] = new JsonObject { ["property_preview"] = new JsonObject { ["const"] = true } } }),
                },
            },
        });
        return schema;
    }

    private static ParameterPatchOptions? ReadParameterPatchOptions(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        var subsetCount = 0; var orderCount = 0; var resourceCount = 0; var propertyCount = 0;
        foreach (var property in args.EnumerateObject())
        {
            if (property.NameEquals("parameter_subset")) subsetCount++;
            if (property.NameEquals("byte_order")) orderCount++;
            if (property.NameEquals("resource_preview")) resourceCount++;
            if (property.NameEquals("property_preview")) propertyCount++;
        }
        if (resourceCount > 1 || resourceCount == 1 && args.GetProperty("resource_preview").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ToolException("resource_preview must be exactly one explicit boolean when supplied.");
        var resourcePreview = resourceCount == 1 && args.GetProperty("resource_preview").GetBoolean();
        if (propertyCount > 1 || propertyCount == 1 && args.GetProperty("property_preview").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ToolException("property_preview must be exactly one explicit boolean when supplied.");
        var propertyPreview = propertyCount == 1 && args.GetProperty("property_preview").GetBoolean();
        if (propertyPreview && resourcePreview) throw new ToolException("property_preview and resource_preview cannot both be true.");
        if (subsetCount == 0 && orderCount == 0 && !resourcePreview && !propertyPreview) return null;
        if (subsetCount != 1 || orderCount != 1)
            throw new ToolException("Subset-Vorschau requires exactly one parameter_subset and one explicit byte_order.");
        var order = args.GetProperty("byte_order");
        if (order.ValueKind != JsonValueKind.String) throw new ToolException("byte_order must be big_endian or little_endian.");
        var byteOrder = order.GetString() switch
        {
            "big_endian" => ParameterPatchByteOrder.BigEndian,
            "little_endian" => ParameterPatchByteOrder.LittleEndian,
            _ => throw new ToolException("byte_order must be big_endian or little_endian."),
        };
        var subset = args.GetProperty("parameter_subset");
        if (subset.ValueKind != JsonValueKind.Array || subset.GetArrayLength() is < 1 or > 64)
            throw new ToolException("parameter_subset must contain 1..64 explicit {refId,rawValue} items.");
        var values = new List<ParameterPatchValue>(subset.GetArrayLength()); var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in subset.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new ToolException("Each parameter_subset item must be an object with refId and rawValue strings.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
                if (!names.Add(property.Name) || property.Name is not ("refId" or "rawValue"))
                    throw new ToolException("Subset items allow exactly one refId and one rawValue, without other fields.");
            if (names.Count != 2 || item.GetProperty("refId").ValueKind != JsonValueKind.String || item.GetProperty("rawValue").ValueKind != JsonValueKind.String)
                throw new ToolException("Subset refId and rawValue must both be explicit strings.");
            var reference = item.GetProperty("refId").GetString()!; var raw = item.GetProperty("rawValue").GetString()!;
            if (reference.Length is < 1 or > 256 || string.IsNullOrWhiteSpace(reference) || reference.Any(char.IsControl)
                || raw.Length is < 1 or > 64 || string.IsNullOrWhiteSpace(raw) || raw.Any(char.IsControl))
                throw new ToolException("Subset refId must contain 1..256 and rawValue 1..64 non-control characters.");
            if (!refs.Add(reference)) throw new ToolException("parameter_subset contains duplicate reference ids.");
            values.Add(new(reference, raw));
        }
        return new(values.AsReadOnly(), byteOrder, resourcePreview, propertyPreview);
    }

    private static object? OfflineParameterPatchDiagnostic(Project project, DeviceInstance device, ParameterPatchOptions? options)
    {
        // The absence of this explicit options pair must do no patch/resource decoding at all.
        if (options is null) return null;
        if (options.PropertyPreview) return OfflinePropertyPatchDiagnostic(project, device, options);
        if (options.ResourcePreview) return OfflineResourcePatchDiagnostic(project, device, options);
        var preview = OfflineParameterPatch.Preview(project, device, options.Values, options.ByteOrder);
        return new
        {
            available = preview.IsAvailable,
            scope = preview.Scope,
            completeApplicationImage = preview.CompleteApplicationImage,
            downloadEligible = preview.DownloadEligible,
            readOnly = true,
            byteOrder = options.ByteOrder == ParameterPatchByteOrder.BigEndian ? "big_endian" : "little_endian",
            binding = preview.Binding is { } binding ? new
            {
                projectId = binding.ProjectId, projectGuid = binding.ProjectGuid, deviceId = binding.DeviceId,
                applicationDataRevision = binding.ApplicationDataRevision, applicationId = binding.ApplicationId,
                definitionHash = binding.DefinitionHash, savedValuesHash = binding.SavedValuesHash,
                codecProfileVersion = binding.CodecProfileVersion, numericParseProfile = binding.NumericParseProfile,
                packedTimeEncodingProfile = binding.PackedTimeEncodingProfile,
                denseTimeEncodingProfile = binding.DenseTimeEncodingProfile,
                moduleScopeProfileVersion = binding.ModuleScopeProfileVersion, moduleScopeWitnessHash = binding.ModuleScopeWitnessHash,
                subsetHash = binding.SubsetHash, fingerprint = binding.Fingerprint,
            } : null,
            issues = preview.Issues.Select(issue => new
            {
                code = JsonNamingPolicy.SnakeCaseLower.ConvertName(issue.Code.ToString()), refId = issue.RefId, resourceId = issue.ResourceId,
            }).ToArray(),
            fields = preview.Fields.Select(field => new
            {
                refId = field.RefId, parameterId = field.ParameterId, resourceId = field.ResourceId,
                rawValue = field.RawValue, startAddress = $"0x{field.StartAddress:X8}", bitOffset = field.BitOffset, bitLength = field.BitLength,
            }).ToArray(),
            ranges = preview.Ranges.Select(range => new
            {
                resourceId = range.ResourceId, startAddress = $"0x{range.StartAddress:X8}", byteLength = range.Length,
                beforeData = range.BeforeData.ToHexString(), afterData = range.AfterData.ToHexString(), fieldMask = range.FieldMask.ToHexString(),
                beforeMask = range.BeforeMask?.ToHexString(), afterMask = range.AfterMask?.ToHexString(),
            }).ToArray(),
            interpretation = "Subset-Vorschau: explicit numeric subset preview only. Byte ranges are offline diagnostics, not a complete application image, active patch set, write address authorization or download readiness.",
        };
    }
}
