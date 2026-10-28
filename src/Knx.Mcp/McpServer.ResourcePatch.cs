using System.Text.Json;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private static object OfflineResourcePatchDiagnostic(Project project, DeviceInstance device, ParameterPatchOptions options)
    {
        var plan = OfflineResourcePatch.Preview(project, device, options.Values, options.ByteOrder);
        var resources = plan.Resources.ToDictionary(resource => resource.Id, StringComparer.Ordinal);
        static object Arena(ResourcePatchArena arena) => new
        {
            kind = JsonNamingPolicy.SnakeCaseLower.ConvertName(arena.Kind.ToString()),
            loadStateMachine = arena.LoadStateMachine,
        };
        return new
        {
            available = plan.IsAvailable,
            scope = plan.Scope,
            backendVersion = 1,
            completeApplicationImage = plan.CompleteApplicationImage,
            downloadEligible = plan.DownloadEligible,
            readOnly = true,
            byteOrder = options.ByteOrder == ParameterPatchByteOrder.BigEndian ? "big_endian" : "little_endian",
            binding = plan.Binding is { } binding ? new
            {
                profileVersion = binding.ProfileVersion,
                projectId = binding.ProjectId, projectGuid = binding.ProjectGuid, deviceId = binding.DeviceId,
                applicationDataRevision = binding.ApplicationDataRevision, applicationId = binding.ApplicationId,
                definitionHash = binding.DefinitionHash, savedValuesHash = binding.SavedValuesHash,
                codecProfileVersion = binding.CodecProfileVersion, numericParseProfile = binding.NumericParseProfile,
                packedTimeEncodingProfile = binding.PackedTimeEncodingProfile, denseTimeEncodingProfile = binding.DenseTimeEncodingProfile,
                moduleScopeProfileVersion = binding.ModuleScopeProfileVersion, moduleScopeWitnessHash = binding.ModuleScopeWitnessHash,
                subsetHash = binding.SubsetHash, resourceHash = binding.ResourceHash, fieldsHash = binding.FieldsHash, fingerprint = binding.Fingerprint,
            } : null,
            imageSources = ArchivedImageSourceDiagnostic(project, device),
            issues = plan.Issues.Select(issue => new
            {
                code = JsonNamingPolicy.SnakeCaseLower.ConvertName(issue.Code.ToString()), refId = issue.RefId, resourceId = issue.ResourceId,
            }).ToArray(),
            unresolvedRequirements = plan.UnresolvedRequirements.Select(requirement => new
            {
                code = JsonNamingPolicy.SnakeCaseLower.ConvertName(requirement.Code.ToString()),
                resourceId = requirement.ResourceId, refId = requirement.RefId,
            }).ToArray(),
            fields = plan.Fields.Select(field => new
            {
                refId = field.RefId, parameterId = field.ParameterId, resourceId = field.ResourceId, rawValue = field.RawValue,
                arena = Arena(field.Arena), byteOffsetInArena = field.ByteOffset,
                byteOffsetInResource = checked(field.ByteOffset - resources[field.ResourceId].BaseOffset),
                bitOffset = field.BitOffset, bitLength = field.BitLength,
            }).ToArray(),
            ranges = plan.Ranges.Select(range => new
            {
                resourceId = range.ResourceId, arena = Arena(range.Arena), byteOffsetInArena = range.ByteOffset,
                byteOffsetInResource = checked(range.ByteOffset - resources[range.ResourceId].BaseOffset), byteLength = range.BeforeData.Length,
                beforeData = range.BeforeData.ToHexString(), afterData = range.AfterData.ToHexString(), fieldMask = range.FieldMask.ToHexString(),
                beforeMask = range.BeforeMask?.ToHexString(), afterMask = range.AfterMask?.ToHexString(),
            }).ToArray(),
            resources = plan.Resources.Select(resource => new
            {
                id = resource.Id, arena = Arena(resource.Arena), baseOffsetInArena = resource.BaseOffset,
                declaredSize = resource.DeclaredSize, payloadLength = resource.PayloadLength,
                declaredSizeCoverage = JsonNamingPolicy.SnakeCaseLower.ConvertName(resource.DeclaredSizeCoverage.ToString()),
                beforeDataHash = resource.BeforeData.Sha256, afterDataHash = resource.AfterData.Sha256,
                beforeMaskHash = resource.BeforeMask?.Sha256, afterMaskHash = resource.AfterMask?.Sha256,
            }).ToArray(),
            interpretation = "Explicit offline resource subset: offsets belong to the stated logical arena. No physical LSM mapping, active patch set, complete device image or download permission is implied.",
        };
    }
}
