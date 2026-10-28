using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Knx.TestFixtures;

internal static class Native22Fixture
{
    public const string Namespace = "http://knx.org/xml/project/22";
    public const string ProjectId = "P-0022";
    public const string DeviceId = "D-22";
    public const string GroupId = "P-0022_GA-1";
    public const string SecondGroupId = "P-0022_GA-2";
    public const string ApplicationId = "M-FFE0_A-22";
    public const string MemberParameterId = ApplicationId + "_MD-1_M-1_MI-2_P-1_R-1";
    public const string MemberObjectId = "MD-1_M-1_MI-2_O-1_R-1";

    public const string DataXml = """
        <KNX xmlns="http://knx.org/xml/project/22">
          <Project Id="P-0022"><Installations>
            <Installation Name="Synthetic installation 22" InstallationId="0" Ipv6InstallationId="18446744073709551615">
              <Topology><Area Id="A-22" Name="Area 22" Address="1" Puid="1">
                <Line Id="L-22" Name="Line 22" Address="1" Puid="2">
                  <Segment Id="S-22" Name="Segment 22" Number="0" MediumTypeRefId="MT-0" DomainAddress="42" Puid="3">
                    <DeviceInstance Id="D-22" Name="Synthetic device 22" ProductRefId="M-FFE0_P-22" Hardware2ProgramRefId="M-FFE0_H-22_HP-22" Address="7" IsSlowResender="true" Puid="4">
                      <ParameterInstanceRefs><ParameterInstanceRef RefId="R-22" Value="17" /></ParameterInstanceRefs>
                      <ComObjectInstanceRefs><ComObjectInstanceRef RefId="O-22" Text="Object 22" ChannelId="C-22" Links="GA-1" /></ComObjectInstanceRefs>
                      <ChannelInstances><ChannelInstance Id="C-22" RefId="CH-22" Name="Channel 22" IsActive="true" /></ChannelInstances>
                      <AdditionalAddresses><Address Name="Unassigned secondary" /></AdditionalAddresses>
                    </DeviceInstance>
                  </Segment>
                </Line>
              </Area></Topology>
              <Locations />
              <GroupAddresses><GroupRanges><GroupRange Id="GR-22" Name="Range 22" RangeStart="0" RangeEnd="65535" Puid="5">
                <GroupAddress Id="P-0022_GA-1" Name="Switch 22" Address="2305" Puid="6" />
                <GroupAddress Id="P-0022_GA-2" Name="Status 22" Address="2306" Puid="7" />
              </GroupRange></GroupRanges></GroupAddresses>
            </Installation>
          </Installations></Project>
        </KNX>
        """;

    public const string InformationXml = """
        <KNX xmlns="http://knx.org/xml/project/22"><Project Id="P-0022">
          <ProjectInformation Name="Synthetic native 22" GroupAddressStyle="ThreeLevel"
            Guid="00000022-1111-2222-3333-444444444444" LastUsedPuid="7" Comment="Synthetic metadata 22" />
        </Project></KNX>
        """;

    public const string ApplicationXml = """
        <KNX xmlns="http://knx.org/xml/project/22"><ManufacturerData><Manufacturer RefId="M-FFE0"><ApplicationPrograms><ApplicationProgram Id="M-FFE0_A-22" ApplicationNumber="22" ApplicationVersion="1" ProgramType="ApplicationProgram" MaskVersion="MV-0701" Name="Synthetic application 22" LoadProcedureStyle="DefaultProcedure" PeiType="0" DefaultLanguage="en-US" DynamicTableManagement="false" Linkable="false">
          <Static><ParameterTypes><ParameterType Id="T-count" Name="Count type"><TypeNumber SizeInBit="8" Type="unsignedInt" minInclusive="0" maxInclusive="3" /></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P-count" Name="Count" Text="Count" ParameterType="T-count" Value="2" /></Parameters>
            <ParameterRefs><ParameterRef Id="R-count" RefId="P-count" /></ParameterRefs></Static>
          <ModuleDefs><ModuleDef Id="M-FFE0_A-22_MD-1" Name="Synthetic module"><Arguments><Argument Id="M-FFE0_A-22_MD-1_A-1" Name="Literal" Type="Numeric" /></Arguments><Static>
            <Parameters><Parameter Id="M-FFE0_A-22_MD-1_P-1" Name="Member" Text="Member" ParameterType="T-count" Value="0" /></Parameters>
            <ParameterRefs><ParameterRef Id="M-FFE0_A-22_MD-1_P-1_R-1" RefId="M-FFE0_A-22_MD-1_P-1" /></ParameterRefs>
            <ComObjects><ComObject Id="M-FFE0_A-22_MD-1_O-1" Name="Object" Number="0" Text="Module object" FunctionText="Switch" ObjectSize="1 Bit" ReadFlag="Enabled" WriteFlag="Enabled" CommunicationFlag="Enabled" TransmitFlag="Enabled" UpdateFlag="Disabled" ReadOnInitFlag="Disabled" /></ComObjects>
            <ComObjectRefs><ComObjectRef Id="M-FFE0_A-22_MD-1_O-1_R-1" RefId="M-FFE0_A-22_MD-1_O-1" /></ComObjectRefs>
          </Static><Dynamic><ParameterBlock Id="M-FFE0_A-22_MD-1_PB-1"><ParameterRefRef RefId="M-FFE0_A-22_MD-1_P-1_R-1" /><ComObjectRefRef RefId="M-FFE0_A-22_MD-1_O-1_R-1" /></ParameterBlock></Dynamic></ModuleDef></ModuleDefs>
          <Dynamic><ChannelIndependentBlock><ParameterBlock Id="PB-root"><ParameterRefRef RefId="R-count" /></ParameterBlock><Repeat Id="REP" Name="Repeated modules" Count="0" ParameterRefId="R-count"><Module Id="M-FFE0_A-22_MD-1_M-1" RefId="M-FFE0_A-22_MD-1"><NumericArg RefId="M-FFE0_A-22_MD-1_A-1" Value="5" /></Module></Repeat></ChannelIndependentBlock></Dynamic>
        </ApplicationProgram></ApplicationPrograms></Manufacturer></ManufacturerData></KNX>
        """;

    public static Dictionary<string, byte[]> Parts(string prefix = "") => new(StringComparer.Ordinal)
    {
        [prefix + "0.xml"] = Encoding.UTF8.GetBytes(DataXml),
        [prefix + "project.xml"] = Encoding.UTF8.GetBytes(InformationXml),
        [prefix + "opaque.bin"] = [0, 255, 22, 128],
    };

    public static byte[] Archive(string layout) => Wrap(Parts(Prefix(layout)), layout);

    public static byte[] Wrap(Dictionary<string, byte[]> parts, string layout)
    {
        if (layout != "nested") return Zip(parts);
        var inner = parts.Where(p => !p.Key.StartsWith("M-", StringComparison.Ordinal) && p.Key != "knx_master.xml").ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var outer = parts.Where(p => !inner.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        outer.Add(ProjectId + ".zip", Zip(inner)); outer.Add("outer.bin", [255, 22, 0]);
        return Zip(outer);
    }

    public static Dictionary<string, byte[]> ModuleParts(string prefix = "")
    {
        XNamespace ns = Namespace;
        var data = XDocument.Parse(DataXml); var device = data.Descendants(ns + "DeviceInstance").Single();
        device.SetAttributeValue("ProductRefId", ApplicationId); device.Attribute("Hardware2ProgramRefId")!.Remove();
        device.Element(ns + "ParameterInstanceRefs")!.ReplaceWith(new XElement(ns + "ParameterInstanceRefs",
            new XElement(ns + "ParameterInstanceRef", new XAttribute("RefId", "R-count"), new XAttribute("Value", "2")),
            new XElement(ns + "ParameterInstanceRef", new XAttribute("RefId", MemberParameterId), new XAttribute("Value", "2"))));
        device.Element(ns + "ComObjectInstanceRefs")!.ReplaceWith(new XElement(ns + "ComObjectInstanceRefs", new XComment("module object comment"),
            new XElement(ns + "ComObjectInstanceRef", new XAttribute("RefId", MemberObjectId), new XAttribute("Text", "Explicit member 22"), new XAttribute("ChannelId", "C-22"), new XAttribute("Links", "GA-1"))));
        device.Element(ns + "ChannelInstances")!.AddAfterSelf(new XElement(ns + "ModuleInstances", new XComment("module container comment"),
            Enumerable.Range(1, 2).Select(i => new XElement(ns + "ModuleInstance", new XAttribute("Id", "MD-1_M-1_MI-" + i), new XAttribute("RefId", "MD-1_M-1"),
                new XElement(ns + "Arguments", new XElement(ns + "Argument", new XAttribute("RefId", "MD-1_A-1"), new XAttribute("Value", "5")))))));
        var parts = Parts(prefix); parts[prefix + "0.xml"] = Bytes(data); parts["M-FFE0/" + ApplicationId + ".xml"] = Encoding.UTF8.GetBytes(ApplicationXml);
        return parts;
    }

    public static byte[] Zip(IReadOnlyDictionary<string, byte[]> parts)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var (name, bytes) in parts) { using var output = archive.CreateEntry(name).Open(); output.Write(bytes); }
        return buffer.ToArray();
    }

    public static Dictionary<string, byte[]> ReadZip(byte[] bytes)
    {
        using var buffer = new MemoryStream(bytes); using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        return archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToDictionary(e => e.FullName, e =>
        {
            using var input = e.Open(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
        }, StringComparer.Ordinal);
    }

    public static Dictionary<string, byte[]> ProjectParts(string path, string layout)
    {
        var outer = ReadZip(File.ReadAllBytes(path)); return layout == "nested" ? ReadZip(outer[ProjectId + ".zip"]) : outer;
    }

    public static string Prefix(string layout) => layout switch { "prefixed" => ProjectId + "/", "root" or "nested" => "", _ => throw new ArgumentOutOfRangeException(nameof(layout)) };
    public static XDocument Xml(byte[] bytes) { using var input = new MemoryStream(bytes, false); return XDocument.Load(input, LoadOptions.PreserveWhitespace); }
    public static byte[] Bytes(XDocument document) => Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
}
