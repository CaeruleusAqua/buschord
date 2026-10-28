using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Knx.TestFixtures;

internal static class Native20Fixture
{
    public const string Namespace = "http://knx.org/xml/project/20";
    public const string ProjectId = "P-0020";
    public const string DeviceId = "D-1";
    public const string GroupId = "P-0020_GA-1";
    public const string SecondGroupId = "P-0020_GA-2";

    public const string DataXml = """
        <KNX xmlns="http://knx.org/xml/project/20">
          <Project Id="P-0020">
            <Installations>
              <Installation Name="Synthetic installation" InstallationId="0">
                <Topology>
                  <Area Id="A-1" Name="Synthetic area" Address="1" Puid="1">
                    <Line Id="L-1" Name="Synthetic line" Address="1" MediumTypeRefId="MT-0" DomainAddress="42" Puid="2">
                      <DeviceInstance Id="D-1" Name="Synthetic device" ProductRefId="M-FFE0_P-1" Hardware2ProgramRefId="M-FFE0_H-1_HP-1" Address="7" Puid="3">
                        <ParameterInstanceRefs><ParameterInstanceRef RefId="R-1" Value="17" /></ParameterInstanceRefs>
                        <ComObjectInstanceRefs><ComObjectInstanceRef RefId="O-1" Text="Synthetic object" ChannelId="C-1" Links="GA-1" /></ComObjectInstanceRefs>
                        <ChannelInstances><ChannelInstance Id="C-1" RefId="CH-1" Name="Synthetic channel" IsActive="true" /></ChannelInstances>
                      </DeviceInstance>
                    </Line>
                  </Area>
                </Topology>
                <Locations />
                <GroupAddresses><GroupRanges>
                  <GroupRange Id="GR-1" Name="Synthetic range" RangeStart="0" RangeEnd="65535" Puid="4">
                    <GroupAddress Id="P-0020_GA-1" Name="Switch" Address="2305" Puid="5" />
                    <GroupAddress Id="P-0020_GA-2" Name="Status" Address="2306" Puid="6" />
                  </GroupRange>
                </GroupRanges></GroupAddresses>
              </Installation>
            </Installations>
          </Project>
        </KNX>
        """;

    public const string InformationXml = """
        <KNX xmlns="http://knx.org/xml/project/20">
          <Project Id="P-0020">
            <ProjectInformation Name="Synthetic native 20" GroupAddressStyle="ThreeLevel"
              Guid="00000020-1111-2222-3333-444444444444" LastUsedPuid="6" Comment="Synthetic metadata" />
          </Project>
        </KNX>
        """;

    public static Dictionary<string, byte[]> Parts(string prefix = "") => new(StringComparer.Ordinal)
    {
        [prefix + "0.xml"] = Encoding.UTF8.GetBytes(DataXml),
        [prefix + "project.xml"] = Encoding.UTF8.GetBytes(InformationXml),
        [prefix + "opaque.bin"] = [0, 255, 17, 128],
    };

    public static byte[] Archive(string layout) => layout switch
    {
        "root" => Zip(Parts()),
        "prefixed" => Zip(Parts(ProjectId + "/")),
        "nested" => Zip(new Dictionary<string, byte[]>
        {
            [ProjectId + ".zip"] = Zip(Parts()),
            ["outer.bin"] = [255, 7, 0],
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    public static byte[] Zip(IReadOnlyDictionary<string, byte[]> parts)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var (name, bytes) in parts)
            {
                using var output = archive.CreateEntry(name).Open();
                output.Write(bytes);
            }
        return buffer.ToArray();
    }

    public static Dictionary<string, byte[]> ReadZip(byte[] bytes)
    {
        using var buffer = new MemoryStream(bytes);
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        return archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToDictionary(entry => entry.FullName, entry =>
        {
            using var input = entry.Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }, StringComparer.Ordinal);
    }

    public static Dictionary<string, byte[]> ProjectParts(string path, string layout)
    {
        var outer = ReadZip(File.ReadAllBytes(path));
        return layout == "nested" ? ReadZip(outer[ProjectId + ".zip"]) : outer;
    }

    public static string Prefix(string layout) => layout == "prefixed" ? ProjectId + "/" : "";

    public static XDocument Xml(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        return XDocument.Load(input);
    }
}
