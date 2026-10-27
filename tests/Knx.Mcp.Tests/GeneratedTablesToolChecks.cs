using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;
using Knx.Workbench;

internal static class GeneratedTablesToolChecks
{
    private const string AppId = "M-0001_A-1";
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private static readonly LoadProcedureRequest Full = new("all", LoadProcedureAccess.Remote, LoadParts.Full);

    internal static string Fixture(string directory)
    {
        var app = XDocument.Parse($$"""
            <KNX xmlns="{{Ns}}"><ManufacturerData><Manufacturer RefId="M-0001"><ApplicationPrograms>
            <ApplicationProgram Id="{{AppId}}" ApplicationNumber="1" ApplicationVersion="1" ProgramType="ApplicationProgram"
                MaskVersion="MV-0705" Name="Synthetic tables" LoadProcedureStyle="ProductProcedure" PeiType="0"
                DefaultLanguage="en-US" DynamicTableManagement="false" Linkable="false">
              <Static><Code>
                <AbsoluteSegment Id="GA" Address="16384" Size="16"><Data>mb7vqqqqqqqqqqqqqqqqqg==</Data><Mask>WlpaWlpaWlpaWlpaWlpaWg==</Mask></AbsoluteSegment>
                <AbsoluteSegment Id="ASSOC" Address="16897" Size="16"><Data>qqqqqqqqqqqqqqqqqqqqqg==</Data></AbsoluteSegment>
                <AbsoluteSegment Id="KO" Address="17408" Size="32"><Data>mXdVEBEgwBITAMAUFQDAFhcgwBgZAMCqqqqqqqqqqqo=</Data><Mask>AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=</Mask></AbsoluteSegment>
              </Code><AddressTable CodeSegment="GA" Offset="0" MaxEntries="5"/>
              <AssociationTable CodeSegment="ASSOC" Offset="0" MaxEntries="5"/>
              <ComObjectTable CodeSegment="KO" Offset="0"/><ComObjectRefs/>
              <LoadProcedures><LoadProcedure><LdCtrlConnect/><LdCtrlUnload LsmIdx="1"/><LdCtrlLoad LsmIdx="1"/>
                <LdCtrlAbsSegment LsmIdx="1" SegType="1" Address="16384" Size="16" Access="0" MemType="0" SegFlags="0"/>
                <LdCtrlAbsSegment LsmIdx="2" SegType="1" Address="16897" Size="16" Access="0" MemType="0" SegFlags="0"/>
                <LdCtrlTaskSegment LsmIdx="2" Address="17408"/><LdCtrlRestart/><LdCtrlDisconnect/>
              </LoadProcedure></LoadProcedures></Static>
              <Dynamic><ChannelIndependentBlock/></Dynamic>
            </ApplicationProgram></ApplicationPrograms></Manufacturer></ManufacturerData></KNX>
            """);
        var table = app.Descendants(Ns + "ComObjectTable").Single();
        foreach (var (number, size, flags) in new[] { (0, "1 Bit", "CRT"), (1, "1 Byte", "RWI"), (3, "2 Bytes", "CRWT"), (4, "4 Bytes", "") })
        {
            var definition = new XElement(Ns + "ComObject", new XAttribute("Id", AppId + "_O-" + number), new XAttribute("Number", number),
                new XAttribute("Name", "Synthetic object " + number), new XAttribute("Text", "Synthetic object " + number), new XAttribute("ObjectSize", size));
            foreach (var (name, flag) in new[] { ("CommunicationFlag", 'C'), ("ReadFlag", 'R'), ("WriteFlag", 'W'), ("TransmitFlag", 'T'), ("UpdateFlag", 'U'), ("ReadOnInitFlag", 'I') })
                definition.Add(new XAttribute(name, flags.Contains(flag) ? "Enabled" : "Disabled"));
            table.Add(definition);
            if (number == 4) continue;
            app.Descendants(Ns + "ComObjectRefs").Single().Add(new XElement(Ns + "ComObjectRef",
                new XAttribute("Id", AppId + "_O-" + number + "_R-" + number), new XAttribute("RefId", AppId + "_O-" + number)));
            app.Descendants(Ns + "ChannelIndependentBlock").Single().Add(new XElement(Ns + "ComObjectRefRef", new XAttribute("RefId", AppId + "_O-" + number + "_R-" + number)));
        }
        var master = $$"""
            <KNX xmlns="{{Ns}}"><MasterData><MaskVersions><MaskVersion Id="MV-0705" MaskVersion="1797"><HawkConfigurationData LegacyVersion="0"><Resources>
              <Resource Name="GroupAddressTable" Access="remote"><Location AddressSpace="StandardMemory" StartAddress="16384"/><ResourceType Length="1" Flavour="AddressTable_Bcu1"/></Resource>
              <Resource Name="GroupAssociationTable" Access="remote"><Location AddressSpace="None"/><ResourceType Length="1" Flavour="AssociationTable_M112"/></Resource>
              <Resource Name="GroupObjectTable" Access="remote"><Location AddressSpace="None"/><ResourceType Length="1" Flavour="GroupObjectTable_M112"/></Resource>
            </Resources></HawkConfigurationData></MaskVersion></MaskVersions></MasterData></KNX>
            """;
        var project = new Project { Id = "P-Tables", Name = "Synthetic communication tables" };
        var installation = new Installation { Id = "0" }; project.Installations.Add(installation);
        var area = new Area { Id = "A-1", Address = 1 }; installation.Areas.Add(area);
        var line = new Line { Id = "L-1", Address = 1, MediumTypeRefId = "MT-0" }; area.Lines.Add(line);
        var device = new DeviceInstance { Id = "D-1", Address = new(1, 1, 1), ProductRefId = AppId }; line.Devices.Add(device);
        var range = new GroupRange { Id = "GR", Name = "Synthetic groups", RangeStart = 0, RangeEnd = 65535 }; installation.MainRanges.Add(range);
        foreach (var (id, raw) in new[] { ("GA1", 257), ("GA2", 2563), ("GA3", 2817) })
            range.Addresses.Add(new() { Id = id, Name = id, Address = new((ushort)raw) });
        foreach (var (number, links) in new[] { (0, new[] { "GA1" }), (1, new[] { "GA2", "GA1" }), (3, new[] { "GA3", "GA2" }) })
        {
            var obj = new ComObjectInstanceRef { Id = "O-" + number + "_R-" + number, ComObjectId = "O-" + number, Number = number };
            obj.Links.AddRange(links); device.ComObjectRefs.Add(obj);
            project.Workbench.Objects.Add(new ObjectKey(device.Id, obj.Id).ToString(), new() { SendingGroupId = links[0] });
        }
        var path = Path.Combine(directory, "generated-tables.knxproj"); Knxproj.Save(project, path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (var (name, text) in new[] { ("M-0001/" + AppId + ".xml", app.ToString()), ("knx_master.xml", master) })
        { archive.GetEntry(name)?.Delete(); using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(text); }
        return path;
    }

    internal static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-table-tools-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var previous = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR"); Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(directory, "catalog"));
        try
        {
            var path = Fixture(directory); var bytes = File.ReadAllBytes(path);
            await using var server = new McpServer(); var sequence = 145000;
            async Task<JsonNode> Tool(string name, object args) => JsonNode.Parse((await server.HandleAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments = args } })))!)!;
            static bool Error(JsonNode r) => r["error"] is not null || r["result"]?["isError"]?.GetValue<bool>() == true;
            static JsonNode Data(JsonNode r) => r["result"]!["structuredContent"]!;
            var opened = await Tool("project_open", new { path }); check("table fixture opens without scripts", !Error(opened), opened.ToJsonString());
            var project = server.Project!; var device = ProjectQuery.DeviceList(project).Single();
            var xml = Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting); var revision = project.ApplicationDataRevision;
            var info = Data(await Tool("project_info", new { })).ToJsonString();
            var ordinary = Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString();
            var symbolicArgs = new { device_address = "1.1.1", load_procedure_preview = new { subtype = "all", access = "remote", full = true, parameters = false, groups = false } };
            var symbolic = Data(await Tool("download_plan", symbolicArgs));
            var tableArgs = new { device_address = "1.1.1", load_procedure_preview = new
                { subtype = "all", access = "remote", full = true, parameters = false, groups = false, generated_tables = new { format = "m112" } } };
            var response = await Tool("download_plan", tableArgs); check("explicit table preview is a controlled tool response", !Error(response), response.ToJsonString());
            if (Error(response)) return;
            var result = Data(response); var build = result["offlineApplicationBuild"]!; var generated = build["generatedTables"]!;
            check("M112 table preview has all three payloads and its own binding", build["available"]?.GetValue<bool>() == true
                && generated["available"]?.GetValue<bool>() == true && (string?)generated["format"] == "m112" && generated["payloadCount"]?.GetValue<int>() == 3
                && generated["payloads"]!.AsArray().Select(p => p!["role"]!.GetValue<string>()).Order(StringComparer.Ordinal)
                    .SequenceEqual(new[] { "association", "communication_object", "group_address" })
                && generated["binding"]?["fingerprint"]?.GetValue<string>() is { Length: 64 }, generated.ToJsonString());
            var core = OfflineApplicationBuildPlan.Preview(project, device, Full, new(GeneratedTableFormat.M112));
            check("MCP keeps the complete Core table binding", core.IsAvailable && core.GeneratedTables?.Binding is { } coreBinding
                && coreBinding.Fingerprint == (string?)generated["binding"]?["fingerprint"] && coreBinding.IsCurrent(project, device), generated.ToJsonString());
            if (core.GeneratedTables is { IsAvailable: true } tables)
            {
                var expected = new Dictionary<string, (string Data, string Mask)>
                {
                    ["group_address"] = ("04BEEF01010A030B01", "FF0000FFFFFFFFFFFF"),
                    ["association"] = ("0501000201030301010203", "FFFFFFFFFFFFFFFFFFFFFF"),
                    ["communication_object"] = ("05775510116FC012131BC7141500C016177FC8181900CA", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"),
                };
                foreach (var payload in generated["payloads"]!.AsArray())
                {
                    var role = payload!["role"]!.GetValue<string>(); var literal = expected[role];
                    static string Hash(string hex) => Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(hex)));
                    check("table payload matches independent literal data and write mask " + role, (string?)payload["dataHash"] == Hash(literal.Data)
                        && (string?)payload["writeMaskHash"] == Hash(literal.Mask) && payload["length"]?.GetValue<int>() == literal.Data.Length / 2
                        && payload["data"] is null && payload["writeMask"] is null, payload.ToJsonString());
                }
                var address = tables.Payloads.Single(p => p.Role == GeneratedTableRole.GroupAddress);
                check("source mask retention is separate from new whole-byte writes", address.AfterMask?.ToHexString() == "FF5A5AFFFFFFFFFFFF"
                    && address.BeforeData?.ToHexString() == "99BEEFAAAAAAAAAAAA" && address.WriteMask.ToHexString() == "FF0000FFFFFFFFFFFF", null);
                var owned = address.Data.ToArray(); owned[0] = 99;
                check("table payload arrays are detached", address.Data.ToHexString() == expected["group_address"].Data, null);
                var obj = device.ComObjectRefs.Single(o => o.Number == 3); obj.Number = 99;
                var rejected = OfflineApplicationBuildPlan.Preview(project, device, Full, new(GeneratedTableFormat.M112));
                check("saved number changes stale tables and refuse the whole requested build", !tables.Binding!.IsCurrent(project, device)
                    && !rejected.IsAvailable && rejected.Binding is null && rejected.GeneratedTables is { Binding: null, Payloads.Count: 0 }, null);
                obj.Number = 3;
                check("restoring inputs restores the same detached table binding", tables.Binding.IsCurrent(project, device)
                    && OfflineApplicationBuildPlan.Preview(project, device, Full, new(GeneratedTableFormat.M112)).GeneratedTables?.Binding?.Fingerprint == tables.Binding.Fingerprint, null);
            }
            check("table diagnostics keep all execution gates closed", result["writesEnabled"]?.GetValue<bool>() == false && result["realDownloadAvailable"]?.GetValue<bool>() == false
                && result["plan"]?["ready"]?.GetValue<bool>() == false && result["plan"]?["program"] is null && result["plan"]?["chunks"]?.AsArray().Count == 0
                && generated["executionEligible"]?.GetValue<bool>() == false && generated["completeApplicationImage"]?.GetValue<bool>() == false
                && generated["downloadEligible"]?.GetValue<bool>() == false, result.ToJsonString());
            check("the primary-stage graph and its original binding remain unchanged", symbolic["offlineApplicationBuild"]?["procedure"]?.ToJsonString() == build["procedure"]?.ToJsonString()
                && symbolic["offlineApplicationBuild"]?["binding"]?.ToJsonString() == build["binding"]?.ToJsonString(), null);
            check("table inspection leaves source model revision undo and absent output unchanged", bytes.SequenceEqual(File.ReadAllBytes(path))
                && xml == Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting) && revision == project.ApplicationDataRevision
                && info == Data(await Tool("project_info", new { })).ToJsonString()
                && ordinary == Data(await Tool("download_plan", new { device_address = "1.1.1" })).ToJsonString()
                && symbolic.ToJsonString() == Data(await Tool("download_plan", symbolicArgs)).ToJsonString(), null);
        }
        finally { Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", previous); Directory.Delete(directory, recursive: true); }
    }
}
