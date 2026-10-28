using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private static object ArchiveMaskProfileDiagnostic(Project project, DeviceInstance device)
    {
        XDocument? application = null;
        var applicationUnreadable = false;
        try { application = Knxproj.LoadApplicationProgram(project, device.Hardware2ProgramRefId ?? device.ProductRefId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException or FormatException or NotSupportedException)
        { applicationUnreadable = true; }
        var programs = application?.Descendants().Where(e => e.Name.LocalName == "ApplicationProgram").Take(2).ToArray();
        var maskId = programs?.Length == 1 ? (string?)programs[0].Attribute("MaskVersion") : null;
        ParameterProfileContext context;
        try { context = ParameterProfileContext.Read(Knxproj.LoadMasterData(project), maskId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException or FormatException or NotSupportedException)
        { context = ParameterProfileContext.Unavailable(maskId); }
        if (programs is { Length: > 0 })
            context = context.ForApplication(maskId, programs.Length == 1 ? programs[0].Name.NamespaceName : null, programs.Length > 1);
        var selection = context.Selection;
        return new
        {
            source = "archive:knx_master.xml",
            readOnly = true,
            applicationDefinitionAvailable = application is not null,
            applicationDefinitionUnreadable = applicationUnreadable,
            applicationMaskVersionId = selection.MaskVersionId,
            status = JsonNamingPolicy.SnakeCaseLower.ConvertName(selection.Status.ToString()),
            selectedMaskVersionId = selection.Status == MaskProfileStatus.Supported ? selection.MaskVersionId : null,
            numericMask = selection.NumericMask,
            requestedLegacyVersion = 0u,
            selectedLegacyVersion = selection.Status == MaskProfileStatus.Supported ? (uint?)selection.LegacyVersion : null,
            firstAppObjectIndex = selection.FirstAppObjectIndex,
            selectedProfileFingerprint = selection.Fingerprint,
            archiveMasterSha256 = context.MasterDataHash,
            declaredInterfaceObjectCount = context.InterfaceObjectCount,
            declaredPropertyCount = context.DeclaredPropertyCount,
            interpretation = "Archive-declared current profile only; no device detection, property capacity or download authorization.",
        };
    }
}
