using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Bus;
using Knx.Mcp;

internal static class ManagementChangeCliChecks
{
    internal static async Task RunAsync(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-management-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var baseline = Fixture(directory);
        var target = Path.Combine(directory, "desired.knxproj");
        var project = Knxproj.Load(baseline); ProjectQuery.DeviceList(project).Single().ParameterValues["R"] = "2"; Knxproj.Save(project, target);
        var identity = Path.Combine(directory, "identity.json");
        await File.WriteAllTextAsync(identity, JsonSerializer.Serialize(new ManagementDeviceIdentity("1.1.1", 0x0701, "0001", "000100000001", "0001010010")));
        var journal = Path.Combine(directory, "journals");
        var common = new[] { "--management-probe=192.0.2.1", "--target=1.1.1", "--m112-change-confirm=1.1.1",
            "--baseline-project=" + baseline, "--project=" + target, "--expected-identity=" + identity, "--journal-directory=" + journal };
        var method = typeof(McpServer).Assembly.GetType("Knx.Mcp.ManagementProbe")!.GetMethod("RunWithConnectionsAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var opens = 0;
        Func<IPEndPoint, IndividualAddress, IndividualAddress, CancellationToken, Task<IManagementConnection>> noNetwork = (_, _, _, _) =>
        { opens++; return Task.FromException<IManagementConnection>(new IOException("Isolated test opener; no network was opened.")); };
        async Task<(int Code, string Output)> Run(string[] arguments)
        {
            var originalOut = Console.Out; var originalError = Console.Error;
            using var output = new StringWriter();
            try
            {
                Console.SetOut(output); Console.SetError(output);
                var code = await (Task<int>)method.Invoke(null, ["192.0.2.1", arguments, noNetwork])!;
                return (code, output.ToString());
            }
            finally { Console.SetOut(originalOut); Console.SetError(originalError); }
        }
        var preview = await Run([.. common, "--m112-preview"]);
        check("M112 CLI preview compiles baseline and changed source without opening the bus", preview.Code == 0 && opens == 0, preview.Output);
        if (preview.Code != 0) return;
        var data = JsonNode.Parse(preview.Output)!;
        var fingerprint = data["Fingerprint"]!.GetValue<string>();
        check("M112 CLI exposes a bound review and actual parameter range", fingerprint.Length == 64
            && data["changes"]!.AsArray().Any(r => r!["Address"]!.GetValue<int>() == 0x4420), null);
        var denied = await Run([.. common, "--review-fingerprint=" + new string('0', 64)]);
        check("M112 CLI stale review cannot reach the connection factory", denied.Code != 0 && opens == 0, denied.Output);
        var missing = await Run(common);
        check("M112 CLI missing review never falls back to reload or read", missing.Code == 2 && opens == 0, missing.Output);
        var mixed = await Run([.. common, "--m112-full-confirm=1.1.1", "--m112-preview"]);
        check("M112 CLI change/reload modes cannot be combined", mixed.Code == 2 && opens == 0, mixed.Output);
        var wrongTarget = await Run([.. common.Where(a => !a.StartsWith("--m112-change-confirm=")), "--m112-change-confirm=1.1.2", "--m112-preview"]);
        check("M112 CLI mismatched physical confirmation is refused before open", wrongTarget.Code == 2 && opens == 0, wrongTarget.Output);
        var malformed = await Run([.. common, "--review-fingerprint=malformed"]);
        check("M112 CLI malformed review is refused before project IO or connect", malformed.Code == 2 && opens == 0, malformed.Output);
        var secret = "synthetic-secret-not-for-command-line";
        var unsafePassword = await Run([.. common, "--m112-preview", "--password=" + secret]);
        check("M112 CLI rejects password values without echoing them", unsafePassword.Code == 2 && opens == 0 && !unsafePassword.Output.Contains(secret), null);
        var confirmed = await Run([.. common, "--review-fingerprint=" + fingerprint]);
        check("M112 CLI exact reviewed proposal reaches only the injected opener", confirmed.Code == 1 && opens == 1
            && confirmed.Output.Contains("Isolated test opener"), confirmed.Output);
        check("M112 CLI original baseline archive is unchanged", !ProjectQuery.DeviceList(Knxproj.Load(baseline)).Single().ParameterValues.ContainsKey("R"), null);
    }

    private static string Fixture(string directory)
    {
        const string appId = "M-0001_A-0100-10-0000", hardwareId = "M-0001_H-image_A-0100-10-0000";
        XNamespace ns = "http://knx.org/xml/project/20";
        var code = new XElement(ns + "Code");
        void Segment(string id, int address, byte[] data)
            => code.Add(new XElement(ns + "AbsoluteSegment", new XAttribute("Id", id), new XAttribute("Address", address), new XAttribute("Size", data.Length),
                new XAttribute("MemoryType", "EEPROM"), new XElement(ns + "Data", Convert.ToBase64String(data)),
                new XElement(ns + "Mask", Convert.ToBase64String(Enumerable.Repeat((byte)255, data.Length).ToArray()))));
        Segment("GA", 0x4000, new byte[16]); Segment("ASSOC", 0x4201, new byte[16]);
        var objects = new byte[32]; objects[0] = 1; objects[1] = 7; objects[3] = 7; objects[4] = 0x10;
        Segment("KO", 0x4400, objects); var parameters = new byte[32]; parameters[0] = 1; Segment("PARAM", 0x4420, parameters);
        foreach (var (id, address, size) in new[] { ("RAM",0x700,64), ("STACK",0x740,1) })
            code.Add(new XElement(ns + "AbsoluteSegment", new XAttribute("Id", id), new XAttribute("Address", address), new XAttribute("Size", size), new XAttribute("MemoryType", "RAM")));
        var controls = new XElement(ns + "LoadProcedure", new XElement(ns + "LdCtrlConnect"), new XElement(ns + "LdCtrlCompareProp",
            new XAttribute("ObjIdx",0),new XAttribute("PropId",78),new XAttribute("InlineData","00000000000100000000")));
        foreach(var oi in new[]{1,2,3}) controls.Add(new XElement(ns+"LdCtrlUnload",new XAttribute("LsmIdx",oi)));
        foreach(var (oi,address,size) in new[]{(1,0x4000,16),(2,0x4201,16),(3,0x4400,64)})
        {
            controls.Add(new XElement(ns+"LdCtrlLoad",new XAttribute("LsmIdx",oi)));
            if(oi==3) { AddSegment(0x700,64,0,2); AddSegment(0x740,1,1,2); }
            AddSegment(address,size,0,3);
            controls.Add(new XElement(ns+"LdCtrlTaskSegment",new XAttribute("LsmIdx",oi),new XAttribute("Address",address)),new XElement(ns+"LdCtrlLoadCompleted",new XAttribute("LsmIdx",oi)));
            void AddSegment(int start,int length,int kind,int memory) => controls.Add(new XElement(ns+"LdCtrlAbsSegment",new XAttribute("LsmIdx",oi),
                new XAttribute("SegType",kind),new XAttribute("Address",start),new XAttribute("Size",length),new XAttribute("Access",memory==3?255:0),
                new XAttribute("MemType",memory),new XAttribute("SegFlags",memory==3?128:0)));
        }
        controls.Add(new XElement(ns+"LdCtrlRestart"),new XElement(ns+"LdCtrlDisconnect"));
        var app = XDocument.Parse($$"""
            <KNX xmlns="{{ns}}"><ManufacturerData><Manufacturer RefId="M-0001"><ApplicationPrograms>
            <ApplicationProgram Id="{{appId}}" MaskVersion="MV-0701" ApplicationNumber="256" ApplicationVersion="16" PeiType="1" MinEtsVersion="3.0d" LoadProcedureStyle="ProductProcedure" DynamicTableManagement="false"><Static>
            <ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="255"/></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P" ParameterType="T" Value="1"><Memory CodeSegment="PARAM" Offset="0" BitOffset="0"/></Parameter></Parameters>
            <ParameterRefs><ParameterRef Id="R" RefId="P"/></ParameterRefs>
            <AddressTable CodeSegment="GA" Offset="0" MaxEntries="7"/><AssociationTable CodeSegment="ASSOC" Offset="0" MaxEntries="7"/>
            <ComObjectTable CodeSegment="KO" Offset="0"><ComObject Id="O" Number="0" Name="Synthetic" ObjectSize="1 Bit" ReadFlag="Enabled" WriteFlag="Disabled" CommunicationFlag="Enabled" TransmitFlag="Enabled" UpdateFlag="Disabled" ReadOnInitFlag="Disabled"/></ComObjectTable>
            <ComObjectRefs><ComObjectRef Id="OR" RefId="O"/></ComObjectRefs><Options DownloadInvisibleParameters="Background"/>
            </Static><Dynamic><ChannelIndependentBlock><ParameterRefRef RefId="R"/><ComObjectRefRef RefId="OR"/></ChannelIndependentBlock></Dynamic></ApplicationProgram>
            </ApplicationPrograms></Manufacturer></ManufacturerData></KNX>
            """);
        var stat = app.Descendants(ns+"Static").Single(); stat.AddFirst(code); stat.Add(new XElement(ns+"LoadProcedures",controls));
        var resources = new XElement(ns+"Resources");
        foreach(var (name,flavour) in new[]{("GroupAddressTable","AddressTable_Bcu1"),("GroupAssociationTable","AssociationTable_M112"),("GroupObjectTable","GroupObjectTable_M112")})
            resources.Add(new XElement(ns+"Resource",new XAttribute("Name",name),new XAttribute("Access","remote"),new XElement(ns+"Location",new XAttribute("AddressSpace","None")),new XElement(ns+"ResourceType",new XAttribute("Length",1),new XAttribute("Flavour",flavour))));
        foreach(var (name,oi) in new[]{("GroupAddressTable",1),("GroupAssociationTable",2),("Application",3)})
            foreach(var suffix in new[]{"LoadControl","LoadStatus"}) resources.Add(new XElement(ns+"Resource",new XAttribute("Name",name+suffix),new XAttribute("Access","remote"),
                new XElement(ns+"Location",new XAttribute("AddressSpace","StandardMemory"),new XAttribute("StartAddress",suffix=="LoadControl"?0x104:0xB6E9+oi)),
                new XElement(ns+"ResourceType",new XAttribute("Length",suffix=="LoadControl"?12:1),new XAttribute("Flavour","LoadControl_M112"))));
        var master = new XDocument(new XElement(ns+"KNX",new XElement(ns+"MasterData",new XElement(ns+"MaskVersions",new XElement(ns+"MaskVersion",
            new XAttribute("Id","MV-0701"),new XAttribute("MaskVersion",1793),new XElement(ns+"HawkConfigurationData",new XElement(ns+"Features",new XElement(ns+"Feature",new XAttribute("Name","PropertyMappedLsms"),new XAttribute("Value",0))),resources))))));
        var hardware=XDocument.Parse($$"""<KNX xmlns="{{ns}}"><ManufacturerData><Manufacturer RefId="M-0001"><Hardware><Hardware Id="M-0001_H-image"><Hardware2Programs><Hardware2Program Id="{{hardwareId}}"><ApplicationProgramRef RefId="{{appId}}"/></Hardware2Program></Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>""");
        var project=new Project{Id="P-cli",Name="Synthetic CLI"};var installation=new Installation{Id="I"};var area=new Area{Id="A",Address=1};var line=new Line{Id="L",Address=1,MediumTypeRefId="MT-0"};
        line.Devices.Add(new DeviceInstance{Id="D",ProductRefId=appId,Hardware2ProgramRefId=hardwareId,Address=new(1,1,1)});area.Lines.Add(line);installation.Areas.Add(area);project.Installations.Add(installation);
        var path=Path.Combine(directory,"baseline.knxproj");
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Create))
        {
            void Put(string entry,XDocument document){using var stream=zip.CreateEntry(entry).Open();stream.Write(Encoding.UTF8.GetBytes(document.ToString()));}
            Put("0.xml",Knxproj.ToXml(project));Put("knx_master.xml",master);Put("M-0001/"+appId+".xml",app);Put("M-0001/Hardware.xml",hardware);
        }
        return path;
    }
}
