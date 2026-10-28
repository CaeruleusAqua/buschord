using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Knx;

namespace Knx.Core.Tests;

internal static class ParameterPresentationChecks
{
    internal static void Run(Action<string, bool, string?> check)
    {
        void Check(string name, bool ok) => check("parameter presentation: " + name, ok, null);
        var document = XDocument.Parse("""
            <KNX><ApplicationProgram Id="M-0001_A-presentation" ContextHelpFile="M-0001_BG-help" DefaultLanguage="en-US"><Static>
            <ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8"/></ParameterType><ParameterType Id="I"><TypePicture RefId="M-0001_BG-image" HorizontalAlignment="Center"/></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P1" Text="Switch" ParameterType="T" Value="0"/><Parameter Id="P2" Text="Option" ParameterType="T" Value="1"/><Parameter Id="P3" Text="Diagram" ParameterType="I"/></Parameters>
            <ParameterRefs><ParameterRef Id="R1" RefId="P1"/><ParameterRef Id="R2" RefId="P2"/><ParameterRef Id="R3" RefId="P3"/></ParameterRefs></Static>
            <Dynamic><ParameterBlock Id="B" Text="Page" Layout="Table" HelpContext="General"><Rows><Row Id="Row1"/><Row Id="Row2"/></Rows><Columns><Column Id="C1" Width="40%"/><Column Id="C2" Width="60%"/></Columns>
            <ParameterRefRef RefId="R1" Cell="1,1"/><choose ParamRefId="R1"><when test="0"><ParameterRefRef RefId="R2" Cell="1,2" HelpContext="Option"/></when><when default="true"><ParameterRefRef RefId="R3" Cell="2,1"/></when></choose></ParameterBlock></Dynamic></ApplicationProgram></KNX>
            """);
        var parsed = Ksxml.Parse(document, out var evaluation);
        Check("only the selected dynamic branch supplies placements", evaluation.Presentation.Placements.ContainsKey("R2") && !evaluation.Presentation.Placements.ContainsKey("R3"));
        Check("declared table cells, weights and inherited help are retained", evaluation.Presentation.Pages["B"] is { IsTable: true, Rows: 2 }
            && evaluation.Presentation.Pages["B"].ColumnWeights.SequenceEqual([40d,60d])
            && parsed.Instances.Single(i => i.Id == "R1").Placement is { Row: 0, Column: 0, HelpContext: "General" });
        var changed = DynamicParameters.Evaluate(document.Root!, new Dictionary<string,string?> { ["R1"] = "1" });
        Check("a draft changes the same-pass layout and picture placement", changed.Presentation.Placements.ContainsKey("R3") && !changed.Presentation.Placements.ContainsKey("R2"));
        var copy = new ParameterType(parsed.Types.Single(t => t.Id == "I"));
        Check("picture declaration survives detached editor copies", copy.PictureResourceId == "M-0001_BG-image" && copy.PictureAlignment == "Center");
        var invalid = new XDocument(document); invalid.Descendants("ParameterRefRef").First(e => (string?)e.Attribute("RefId") == "R2").SetAttributeValue("Cell", "1,1");
        Check("overlapping active cells reject table layout without hiding parameters", !DynamicParameters.Evaluate(invalid.Root!).Presentation.Pages["B"].IsTable && Ksxml.Parse(invalid).Instances.Count == 3);
        invalid.Descendants("ParameterRefRef").First(e => (string?)e.Attribute("RefId") == "R2").SetAttributeValue("Cell", "9999999999,1");
        Check("out-of-range cells use field-list fallback", !DynamicParameters.Evaluate(invalid.Root!).Presentation.Pages["B"].IsTable);

        var directory = Path.Combine(Path.GetTempPath(), "buschord-presentation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory,"resources.knxproj"); Knxproj.Save(ProjectWizard.Create(new()),path);
            using (var zip = ZipFile.Open(path,ZipArchiveMode.Update))
            {
                void Write(string name,byte[] data) { using var output=zip.CreateEntry(name).Open(); output.Write(data); }
                Write("M-0001/Baggages.xml",Encoding.UTF8.GetBytes("""
                    <KNX><Baggages><Baggage Id="M-0001_BG-help" Name="help.zip" TargetPath="docs"/><Baggage Id="M-0001_BG-image" Name="image.png" TargetPath="images"/><Baggage Id="M-0001_BG-huge" Name="huge.png"/><Baggage Id="M-0001_BG-escape" Name="outside.txt" TargetPath="../.."/><Baggage Id="M-0001_BG-url" Name="https://invalid.test/image.png"/></Baggages></KNX>
                    """));
                using var helpBuffer = new MemoryStream();
                using(var helpArchive = new ZipArchive(helpBuffer,ZipArchiveMode.Create,true))
                { using var writer=new StreamWriter(helpArchive.CreateEntry("General.txt").Open(),new UTF8Encoding(false)); writer.Write("Synthetic context help. <script>literal text</script>"); }
                Write("M-0001/Baggages/docs/help.zip",helpBuffer.ToArray());
                var header = new byte[24]; new byte[]{137,80,78,71,13,10,26,10}.CopyTo(header,0); "IHDR"u8.CopyTo(header.AsSpan(12));
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16),2); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20),3); Write("M-0001/Baggages/images/image.png",header);
                BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16),100000); Write("M-0001/Baggages/huge.png",header);
            }
            var reader=new ParameterResourceReader(Knxproj.Load(path),document);
            var help=reader.ReadHelp("General");
            Check("declared UTF-8 context help retains language and literal markup", help is {Status:ParameterResourceStatus.Available,Language:"en-US"} && help.Text!.Contains("<script>literal text</script>"));
            Check("unknown contexts and path traversal do not read other entries", reader.ReadHelp("Unknown").Status == ParameterResourceStatus.Missing && reader.ReadHelp("../General").Status == ParameterResourceStatus.Missing);
            var image=reader.ReadImage("M-0001_BG-image");
            Check("image header is bounded before any native decode", image is {Status:ParameterResourceStatus.Available,Width:2,Height:3} && reader.ReadImage("M-0001_BG-huge").Status == ParameterResourceStatus.TooLarge);
            image.Bytes![0]=0;
            Check("resource buffers do not mutate the immutable archive or cache", reader.ReadImage("M-0001_BG-image").Bytes![0] == 137);
            Check("external URIs and escaping declarations cannot be resolved", reader.ReadImage("M-0001_BG-escape").Status == ParameterResourceStatus.Missing && reader.ReadImage("M-0001_BG-url").Status == ParameterResourceStatus.Missing);
        }
        finally { Directory.Delete(directory,true); }
    }
}
