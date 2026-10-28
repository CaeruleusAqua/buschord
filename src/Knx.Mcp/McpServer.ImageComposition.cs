using System.Text.Json;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private static ImageCompositionRequest? ReadImageCompositionOptions(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        var count = args.EnumerateObject().Count(property => property.NameEquals("image_composition_preview"));
        if (count == 0) return null;
        if (count != 1) throw new ToolException("image_composition_preview must occur exactly once.");
        var options = args.GetProperty("image_composition_preview");
        if (options.ValueKind != JsonValueKind.Object)
            throw new ToolException("image_composition_preview must be an object with one explicit mask_policy.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in options.EnumerateObject())
            if (!names.Add(property.Name) || property.Name != "mask_policy")
                throw new ToolException("image_composition_preview allows exactly one mask_policy, without other fields.");
        if (names.Count != 1 || options.GetProperty("mask_policy").ValueKind != JsonValueKind.String)
            throw new ToolException("image_composition_preview requires an explicit mask_policy string.");
        var policy = options.GetProperty("mask_policy").GetString() switch
        {
            "source_defined_bits" => StaticCodeMaskPolicy.SourceDefinedBits,
            "no_static_source_bits" => StaticCodeMaskPolicy.NoStaticSourceBits,
            _ => throw new ToolException("mask_policy must be source_defined_bits or no_static_source_bits."),
        };
        return new(policy);
    }

    private static object ImageCompositionDiagnostic(Project project, DeviceInstance device, ImageCompositionRequest request)
    {
        const int sourceLimit = 128, chunkLimit = 128, spanLimit = 256, requirementLimit = 256;
        var plan = OfflineImageComposition.Preview(project, device, request);
        static string Code(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
        static object Arena(ResourcePatchArena arena) => new { kind = Code(arena.Kind), loadStateMachine = arena.LoadStateMachine };
        var sources = plan.SourceSegments.Take(sourceLimit).Select(source => new
        {
            sourceId = source.SourceId, arena = Arena(source.Arena), byteOffsetInArena = source.ByteOffsetInArena,
            declaredSize = source.DeclaredSize, allocationLength = source.AllocationLength,
            rawDataLength = source.RawData?.Length, rawDataHash = source.RawData?.Sha256,
            rawMaskLength = source.RawMask?.Length, rawMaskHash = source.RawMask?.Sha256,
        }).ToArray();
        var chunks = plan.Chunks.Take(chunkLimit).Select(chunk => new
        {
            key = chunk.Key, arena = Arena(chunk.Arena), byteOffsetInArena = chunk.ByteOffsetInArena,
            byteLength = chunk.Length, desiredDataHash = chunk.DesiredData.Sha256, desiredMaskHash = chunk.DesiredMask.Sha256,
            sourceIdsTotal = chunk.SourceIds.Count, sourceIdsShown = Math.Min(chunk.SourceIds.Count, sourceLimit),
            sourceIdsTruncated = chunk.SourceIds.Count > sourceLimit, sourceIds = chunk.SourceIds.Take(sourceLimit).ToArray(),
        }).ToArray();
        var spans = plan.UnspecifiedSpans.Take(spanLimit).Select(span => new
        {
            chunkKey = span.ChunkKey, arena = Arena(span.Arena), byteOffsetInArena = span.ByteOffsetInArena,
            byteLength = span.Length, unspecifiedBitCount = span.UnspecifiedBitCount,
        }).ToArray();
        var requirements = plan.UnresolvedRequirements.Take(requirementLimit).Select(requirement => new
        { code = Code(requirement.Code), sourceId = requirement.SourceId, chunkKey = requirement.ChunkKey }).ToArray();
        return new
        {
            available = plan.IsAvailable, scope = plan.Scope, profileVersion = plan.ProfileVersion,
            maskPolicy = Code(request.MaskPolicy), readOnly = true,
            completeApplicationImage = plan.CompleteApplicationImage, downloadEligible = plan.DownloadEligible,
            executionEligible = plan.ExecutionEligible,
            binding = plan.Binding is { } binding ? new
            {
                profileVersion = binding.ProfileVersion, projectId = binding.ProjectId, projectGuid = binding.ProjectGuid,
                deviceId = binding.DeviceId, applicationDataRevision = binding.ApplicationDataRevision,
                applicationId = binding.ApplicationId, definitionHash = binding.DefinitionHash,
                sourceFingerprint = binding.SourceFingerprint, maskPolicy = Code(binding.MaskPolicy),
                sourceSegmentsHash = binding.SourceSegmentsHash, chunksHash = binding.ChunksHash,
                unspecifiedSpansHash = binding.UnspecifiedSpansHash, requirementsHash = binding.RequirementsHash,
                fingerprint = binding.Fingerprint,
            } : null,
            imageSources = ArchivedImageSourceDiagnostic(plan.Sources),
            desiredBitCount = plan.DesiredBitCount, unspecifiedBitCount = plan.UnspecifiedBitCount,
            issues = plan.Issues.Select(issue => new { code = Code(issue.Code), sourceId = issue.SourceId }).ToArray(),
            sourceSegmentsTotal = plan.SourceSegments.Count, sourceSegmentsShown = sources.Length,
            sourceSegmentsTruncated = plan.SourceSegments.Count > sources.Length, sourceSegments = sources,
            chunksTotal = plan.Chunks.Count, chunksShown = chunks.Length,
            chunksTruncated = plan.Chunks.Count > chunks.Length, chunks,
            unspecifiedSpansTotal = plan.UnspecifiedSpans.Count, unspecifiedSpansShown = spans.Length,
            unspecifiedSpansTruncated = plan.UnspecifiedSpans.Count > spans.Length, unspecifiedSpans = spans,
            requirementsTotal = plan.UnresolvedRequirements.Count, requirementsShown = requirements.Length,
            requirementsTruncated = plan.UnresolvedRequirements.Count > requirements.Length, unresolvedRequirements = requirements,
            interpretation = "Root static-code contributions under the explicit mask policy. Raw source bytes remain separate from unspecified working bits. No parameter/table writers, current device state, concrete read actions or finalized image are implied.",
        };
    }
}
