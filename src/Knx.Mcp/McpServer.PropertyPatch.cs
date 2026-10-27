using System.Text.Json;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private static object OfflinePropertyPatchDiagnostic(Project project, DeviceInstance device, ParameterPatchOptions options)
    {
        var plan = OfflinePropertyPatch.Preview(project, device, options.Values, options.ByteOrder);
        var source = plan.SourceIndex;
        static string Code(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
        static object Selector(PropertySourceSelector selector) => new
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
        static object? Fragment(PropertySourceFragment? fragment) => fragment is null ? null : new
        {
            sourceOrdinal = fragment.SourceOrdinal, rawSelector = Selector(fragment.RawSelector), component = Code(fragment.Component),
            byteOffset = fragment.ByteOffset, byteLength = fragment.Data.Length, dataHash = fragment.Data.Sha256,
            maskHash = fragment.Mask?.Sha256,
        };
        var entries = source.Entries.Take(128).Select(entry => new
        {
            propertyKey = entry.PropertyKey, selector = Selector(entry.Selector), selectorIsCanonical = entry.SelectorIsCanonical,
            fragmentsTotal = entry.DataFragments.Count, fragmentsShown = Math.Min(entry.DataFragments.Count, 32),
            fragmentsTruncated = entry.DataFragments.Count > 32,
            dataFragments = entry.DataFragments.Take(32).Select(fragment => Fragment(fragment)).ToArray(),
            countFragment = Fragment(entry.CountFragment), encodedCount = entry.EncodedCount, countStatus = Code(entry.CountStatus),
            pdtLayout = Pdt(entry.PdtLayout), capacityElements = entry.CapacityElements,
        }).ToArray();
        return new
        {
            available = plan.IsAvailable, scope = plan.Scope, backendVersion = plan.ProfileVersion,
            completeApplicationImage = plan.CompleteApplicationImage, downloadEligible = plan.DownloadEligible,
            executionEligible = plan.ExecutionEligible, readOnly = true,
            byteOrder = options.ByteOrder == ParameterPatchByteOrder.BigEndian ? "big_endian" : "little_endian",
            binding = plan.Binding is { } binding ? new
            {
                profileVersion = binding.ProfileVersion, numericParseProfile = binding.NumericParseProfile,
                projectId = binding.ProjectId, projectGuid = binding.ProjectGuid, deviceId = binding.DeviceId,
                applicationDataRevision = binding.ApplicationDataRevision, applicationId = binding.ApplicationId,
                definitionHash = binding.DefinitionHash, savedValuesHash = binding.SavedValuesHash,
                sourceFingerprint = binding.SourceFingerprint, sourceIndexFingerprint = binding.SourceIndexFingerprint,
                propertyProfileFingerprint = binding.PropertyProfileFingerprint, pdtRoleProfileVersion = binding.PdtRoleProfileVersion,
                subsetHash = binding.SubsetHash, fieldsHash = binding.FieldsHash, propertyImagesHash = binding.PropertyImagesHash,
                fingerprint = binding.Fingerprint,
            } : null,
            propertySources = new
            {
                scope = source.Scope, profileVersion = source.ProfileVersion, status = Code(source.Status),
                factoryStatus = source.FactoryStatus is { } factoryStatus ? Code(factoryStatus) : null,
                factoryBlobHash = source.FactoryBlobHash, sourceFingerprint = source.SourceFingerprint,
                fingerprint = source.Fingerprint, pdtRoleProfileVersion = source.PdtRoleProfileVersion,
                binding = source.Binding is { } sourceBinding ? new
                {
                    profileVersion = sourceBinding.ProfileVersion, sourceFingerprint = sourceBinding.SourceFingerprint,
                    propertyProfileFingerprint = sourceBinding.PropertyProfileFingerprint, fingerprint = sourceBinding.Fingerprint,
                } : null,
                propertyProfileSelection = new
                {
                    status = Code(source.PropertyProfileSelection.Status), maskVersionId = source.PropertyProfileSelection.MaskVersionId,
                    numericMask = source.PropertyProfileSelection.NumericMask, legacyVersion = source.PropertyProfileSelection.LegacyVersion,
                    firstAppObjectIndex = source.PropertyProfileSelection.FirstAppObjectIndex,
                    fingerprint = source.PropertyProfileSelection.Fingerprint,
                },
                entriesTotal = source.Entries.Count, entriesShown = entries.Length,
                entriesTruncated = source.Entries.Count > entries.Length, entries,
            },
            issues = plan.Issues.Select(issue => new { code = Code(issue.Code), refId = issue.RefId, propertyKey = issue.PropertyKey }).ToArray(),
            unresolvedRequirements = plan.UnresolvedRequirements.Select(requirement => new
            { code = Code(requirement.Code), propertyKey = requirement.PropertyKey }).ToArray(),
            fields = plan.Fields.Select(field => new
            {
                refId = field.RefId, parameterId = field.ParameterId, propertyKey = field.PropertyKey, rawValue = field.RawValue,
                selector = Selector(field.Selector), byteOffsetInPropertyData = field.ByteOffsetInPropertyData,
                bitOffset = field.BitOffset, bitLength = field.BitLength,
            }).ToArray(),
            ranges = plan.Ranges.Select(range => new
            {
                propertyKey = range.PropertyKey, selector = Selector(range.Selector), byteOffsetInPropertyData = range.ByteOffsetInPropertyData,
                byteLength = range.Length, beforeData = range.BeforeData.ToHexString(), afterData = range.AfterData.ToHexString(),
                fieldMask = range.FieldMask.ToHexString(), beforeMask = range.BeforeMask.ToHexString(), afterMask = range.AfterMask.ToHexString(),
            }).ToArray(),
            properties = plan.Images.Select(image => new
            {
                propertyKey = image.PropertyKey, selector = Selector(image.Selector), pdtLayout = Pdt(image.PdtLayout),
                payloadLength = image.PayloadLength, beforeDataHash = image.BeforeData.Sha256, afterDataHash = image.AfterData.Sha256,
                beforeMaskHash = image.BeforeMask.Sha256, afterMaskHash = image.AfterMask.Sha256,
                countData = image.BeforeCountData.ToHexString(), countMask = image.CountMask.ToHexString(),
                encodedCount = image.EncodedElementCount, countUnchanged = image.CountUnchanged,
                capacityElements = (uint?)null, capacityStatus = "unknown",
            }).ToArray(),
            interpretation = "Explicit offline property subset using complete supplied data and unchanged count bytes. Source count is not current device state or maximum capacity; physical aliases, activation and complete image construction remain unresolved.",
        };
    }
}
