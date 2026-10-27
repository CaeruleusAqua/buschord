using System.Text.Json;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private static object ArchivedImageSourceDiagnostic(Project project, DeviceInstance device)
        => ArchivedImageSourceDiagnostic(ArchivedImageSources.Read(project, device));

    private static object ArchivedImageSourceDiagnostic(ArchivedImageSourceManifest source)
    {
        const int resourceLimit = 64, regionLimit = 32, factoryLimit = 32;
        static string Code<T>(T value) where T : Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
        static object? Location(ArchivedResourceLocation? location) => location is null ? null : new
        {
            addressSpace = Code(location.AddressSpace), byteOffset = location.Offset,
            nativeObjectIndex = location.NativeObjectIndex, applicationObjectIndex = location.ApplicationObjectIndex,
            objectType = location.ObjectType, occurrence = location.Occurrence, propertyId = location.PropertyId,
            pointerResource = location.PointerResource, interfaceObjectRef = location.InterfaceObjectRef,
        };
        var context = source.ResourceContext;
        var hardware = source.Hardware;
        var factory = hardware?.FactoryImage;
        var requirements = new List<string>
        {
            "complete_parameter_activation", "generated_tables", "load_procedure", "physical_resource_mapping",
            "property_initialization_and_count_policy", "secure_configuration",
        };
        if (source.Status != ArchivedImageSourceStatus.Inspected) requirements.Add("application_sources_unavailable");
        if (context?.Status != ArchivedResourceContextStatus.Supported) requirements.Add("resource_context_unavailable");
        if (hardware?.Status != ArchivedHardwareSourceStatus.Inspected) requirements.Add("linked_hardware_source_unavailable");
        if (factory?.Status == ArchivedFactoryImageStatus.Missing) requirements.Add("factory_image_missing");
        else if (factory?.Status != ArchivedFactoryImageStatus.Decoded) requirements.Add("factory_image_unavailable");
        requirements.Add("factory_stamp_unverified");
        return new
        {
            scope = source.Scope, profileVersion = source.ProfileVersion, status = Code(source.Status), readOnly = true,
            applicationId = source.ApplicationId, applicationNamespace = source.ApplicationNamespace,
            applicationDefinitionHash = source.ApplicationDefinitionHash,
            binding = source.Binding is { } binding ? new
            { profileVersion = binding.ProfileVersion, fingerprint = binding.Fingerprint, resourceContextFingerprint = binding.ResourceContextFingerprint } : null,
            resourceContext = context is null ? null : new
            {
                status = Code(context.Status), maskVersionId = context.MaskVersionId, numericMask = context.NumericMask,
                legacyVersion = context.LegacyVersion, firstAppObjectIndex = context.FirstAppObjectIndex,
                propertyMappedLsms = context.PropertyMappedLsms, archiveMasterHash = context.MasterDataHash,
                documentHash = context.DocumentHash, fingerprint = context.Fingerprint,
                resourceCount = context.Resources.Count, resourceLimit, resourcesTruncated = context.Resources.Count > resourceLimit,
                resources = context.Resources.Take(resourceLimit).Select(resource => new
                {
                    name = resource.Name, access = resource.Access, managementStyles = resource.ManagementStyles,
                    location = Location(resource.Location), imageLocation = Location(resource.ImageLocation),
                    declaredLength = resource.DeclaredLength, flavour = resource.Flavour, optional = resource.Optional,
                    readAccess = resource.ReadAccess, writeAccess = resource.WriteAccess,
                }).ToArray(),
                memoryRegionCount = context.MemoryRegions.Count, regionLimit, regionsTruncated = context.MemoryRegions.Count > regionLimit,
                memoryRegions = context.MemoryRegions.Take(regionLimit).Select(region => new
                {
                    memoryType = region.MemoryType, location = Location(region.Location), declaredLength = region.Length,
                    optional = region.Optional, readAccess = region.ReadAccess, writeAccess = region.WriteAccess,
                }).ToArray(),
            },
            hardware = hardware is null ? null : new
            {
                status = Code(hardware.Status), hardware2ProgramId = hardware.Hardware2ProgramId,
                definitionHash = hardware.DefinitionHash, loadedImageDeclared = hardware.LoadedImageDeclared,
                checksumStatus = Code(hardware.ChecksumStatus), checksumLength = hardware.ChecksumData?.Length,
                checksumHash = hardware.ChecksumData?.Sha256,
                factoryImage = factory is null ? null : new
                {
                    profileVersion = factory.ProfileVersion, status = Code(factory.Status), blobHash = factory.BlobHash,
                    decodedHash = factory.DecodedHash, stampVerified = factory.StampVerified,
                    segmentCount = factory.Segments.Count, factoryLimit, segmentsTruncated = factory.Segments.Count > factoryLimit,
                    segments = factory.Segments.Take(factoryLimit).Select(segment => new
                    {
                        location = new
                        {
                            addressSpace = Code(segment.Location.AddressSpace), component = Code(segment.Location.Component),
                            byteOffset = segment.Location.ByteOffset, nativeObjectIndex = segment.Location.NativeObjectIndex,
                            applicationObjectIndex = segment.Location.ApplicationObjectIndex, objectType = segment.Location.ObjectType,
                            occurrence = segment.Location.Occurrence, propertyId = segment.Location.PropertyId,
                        },
                        byteLength = segment.Data.Length, dataHash = segment.Data.Sha256, maskHash = segment.Mask?.Sha256,
                    }).ToArray(),
                },
            },
            unresolvedRequirements = requirements.ToArray(),
            completeApplicationImage = source.CompleteApplicationImage, downloadEligible = source.DownloadEligible,
            interpretation = "Archived source declarations and optional factory buffers only. No pointer resolution, property capacity/current count, checksum verification, active patch set or device-state claim.",
        };
    }
}
