namespace Knx.Xml;

/// <summary>
/// knxproj element names (namespace http://knx.org/xml/project/23).
/// Recovered from the ETS6 XML object model assembly; schema-level fidelity
/// is validated against a real exported project in the acceptance step.
/// </summary>
internal static class XmlNames
{
    public const string Knx = "KNX";
    public const string MasterData = "MasterData";
    public const string Project = "Project";
    public const string Installations = "Installations";
    public const string Installation = "Installation";
    public const string Topology = "Topology";
    /// <summary>Native building tree container, sequence position between Topology and GroupAddresses.</summary>
    public const string Locations = "Locations";
    /// <summary>Native space node (SpaceType_t; also holds DeviceInstanceRef and Function children).</summary>
    public const string Space = "Space";
    /// <summary>Native function/utility group inside a space.</summary>
    public const string Function = "Function";
    /// <summary>Device assignment inside a space (RefId points at a DeviceInstance Id).</summary>
    public const string DeviceInstanceRef = "DeviceInstanceRef";
    public const string Area = "Area";
    public const string Line = "Line";
    public const string Segment = "Segment";
    public const string DeviceInstance = "DeviceInstance";
    public const string ComObjectInstanceRefs = "ComObjectInstanceRefs";
    public const string ComObjectInstanceRef = "ComObjectInstanceRef";
    public const string GroupAddresses = "GroupAddresses";
    public const string GroupRanges = "GroupRanges";
    public const string GroupRange = "GroupRange";
    public const string GroupAddress = "GroupAddress";
}
