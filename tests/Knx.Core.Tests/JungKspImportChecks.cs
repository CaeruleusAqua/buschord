using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Manufacturer;
using Knx.Workbench;

internal static class JungKspImportChecks
{
    private const string App = JungKspImport.ApplicationProgramId;
    private sealed record Fixture(Project Project, DeviceInstance Device, XDocument Definition, IReadOnlyList<DatapointType> Master);
    internal static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + "Jung KSP import: " + name); if (!ok) failures++; }
        void Reject(string name, Fixture f, Action action)
        {
            var baseline = State(f); var rejected = false;
            try { action(); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { rejected = true; }
            Check(name, rejected && State(f) == baseline);
        }
        JungKspImportPlan Preview(Fixture f, JungKspDocument? input = null) => JungKspImport.Preview(f.Project, f.Device, f.Definition, input ?? Payload(), f.Master, "de");
        void Apply(Fixture f, JungKspImportPlan p, IReadOnlyDictionary<int, bool>? choices = null) => JungKspImport.Apply(f.Project, p, f.Definition, choices);

        var f = Create(); var baseline = State(f); var input = Payload(); var plan = Preview(f, input);
        Check("preview leaves project and source definition unchanged", State(f) == baseline && !plan.IsApplied
            && plan.ApplicationProgramId == App && plan.DeviceId == f.Device.Id && plan.DefinitionSha256.Length == 64 && plan.ProjectSha256.Length == 64);
        Check("preview lists exact full refs, original frame hashes and binary-data change", plan.Parameters.Any(p => p.ReferenceId == P(2) && p.RawByteLength == input.Blocks[1].Length)
            && plan.Parameters.Any(p => p.ReferenceId == P(16) && p.NewValue == "1") && plan.Parameters.Any(p => p.ReferenceId == P(17) && p.NewValue == "1")
            && plan.FrameSha256.SequenceEqual(input.Blocks.Select(b => b.Sha256)) && plan.FunctionTexts.Name == "FunctionTexts"
            && plan.FunctionTexts.NewLength == input.Blocks[4].Length);
        var row = plan.Objects.Single();
        Check("compatible name change is optional unlink with full connector review", row is { Number: 1, WasActive: true, WillBeActive: true,
            SourceReferenceId: "O-1_R-11", TargetReferenceId: "O-1_R-11", OldDpt: "1.001", NewDpt: "1.001", OldBits: 1, NewBits: 1,
            OldText: "Old label", NewText: "New label", OldFlags: "CRWI", NewFunctionText: "Schalten", LinkAction: JungKspLinkAction.OptionalUnlink }
            && row.ExistingLinks.Single() is { ReferenceId: "O-1_R-11", GroupAddressId: "G1", Sending: true, Acknowledge: true, Priority: 2, Slot: 3 });
        Apply(f, plan);
        var obj = f.Device.ComObjectRefs.Single(); var metadata = f.Project.Workbench.Objects[new ObjectKey(f.Device.Id, obj.Id).ToString()];
        Check("apply retains compatible links, connector facts and hidden I state", obj.Links.SequenceEqual(["G1"])
            && metadata.Connector("G1") is { Send: true, Acknowledge: true, Priority: 2, Slot: 3 }
            && metadata is { Active: true, Dpt: "1.001", Bits: 1, Flags: "CRWTUI", Role: "switch", ChannelId: "CH1" });
        Check("full raw block and function-text bytes are persisted without recompression", Convert.FromBase64String(f.Device.ParameterValues[P(2)]).SequenceEqual(input.Blocks[1].GetBytes())
            && f.Device.BinaryData["FunctionTexts"].SequenceEqual(input.Blocks[4].GetBytes())
            && Convert.FromBase64String(f.Device.ParameterValues[P(3)]).SequenceEqual(new byte[] { 7, 8 })
            && f.Device.BinaryData["Other"].SequenceEqual(new byte[] { 9, 10 }));
        Check("apply keeps product/hardware identity and increments one device revision", plan.IsApplied && f.Device.ProductRefId == App
            && f.Device.Hardware2ProgramRefId == App && f.Project.Workbench.Devices[f.Device.Id] is { Revision: 4, DownloadedRevision: 2, SecureCapable: false });
        Reject("plans cannot be replayed", f, () => Apply(f, plan));

        var path = Path.Combine(Path.GetTempPath(), "buschord-jung-ksp-" + Guid.NewGuid().ToString("N") + ".knxproj");
        try
        {
            Knxproj.Save(f.Project, path); var reloaded = Knxproj.Load(path); var device = ProjectQuery.DeviceList(reloaded).Single();
            Check("synthetic save/reload retains raw blobs, binary data and reviewed KO facts", device.ParameterValues[P(2)] == f.Device.ParameterValues[P(2)]
                && device.BinaryData["FunctionTexts"].SequenceEqual(input.Blocks[4].GetBytes()) && device.ComObjectRefs.Single().Links.SequenceEqual(["G1"])
                && device.ComObjectRefs.Single().FunctionText == "Schalten" && Knxproj.LoadApplicationProgram(reloaded, App) is not null);
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        f = Create(); plan = Preview(f); Apply(f, plan, new Dictionary<int, bool> { [1] = true });
        Check("optional explicit unlink removes connector and sending facts together", f.Device.ComObjectRefs.Single().Links.Count == 0
            && f.Project.Workbench.Objects[new ObjectKey(f.Device.Id, "O-1_R-11").ToString()] is { SendingGroupId: null, Connectors.Count: 0 });

        f = Create(); var changed = Payload(main: 5, subtype: 1); plan = Preview(f, changed);
        Check("bit-size change resolves the actually activated reference rather than guessing an id", plan.Objects.Single() is
            { SourceReferenceId: "O-1_R-11", TargetReferenceId: "O-1_R-13", OldBits: 1, NewBits: 8, NewDpt: "5.001", LinkAction: JungKspLinkAction.RequiredUnlink });
        Reject("missing required unlink decision cannot partially apply", f, () => Apply(f, plan));
        Reject("negative required unlink decision cannot partially apply", f, () => Apply(f, plan, new Dictionary<int, bool> { [1] = false }));
        Apply(f, plan, new Dictionary<int, bool> { [1] = true });
        Check("reviewed variant change retires old ref and activates exact new ref", f.Device.ComObjectRefs.Count == 2
            && f.Device.ComObjectRefs.All(o => o.Links.Count == 0) && f.Project.Workbench.Objects[new ObjectKey(f.Device.Id, "O-1_R-11").ToString()].Active == false
            && f.Project.Workbench.Objects[new ObjectKey(f.Device.Id, "O-1_R-13").ToString()] is { Active: true, Dpt: "5.001", Bits: 8 });

        f = Create(); AddDormantByteReference(f); plan = Preview(f, changed);
        Check("inactive target-variant link history is visible in the same required review", plan.Objects.Single().ExistingLinks.Count == 2
            && plan.Objects.Single().ExistingLinks.Any(l => l.ReferenceId == "O-1_R-13" && l.GroupAddressId == "G8"));
        var result = JungKspImport.Apply(f.Project, plan, f.Definition, new Dictionary<int, bool> { [1] = true });
        Check("required unlink clears both old and reactivated reference histories", result.RemovedLinks == 2 && f.Device.ComObjectRefs.All(o => o.Links.Count == 0)
            && f.Project.Workbench.Objects.Where(p => p.Key.Contains("O-1_R-", StringComparison.Ordinal)).All(p => p.Value.Connectors.Count == 0 && p.Value.SendingGroupId is null));

        f = Create(); plan = Preview(f, Payload(empty: true));
        Check("removed object lists an explicit BusChord safety unlink action", plan.Objects.Single() is { Number: 1, WillBeActive: false, TargetReferenceId: null, LinkAction: JungKspLinkAction.RequiredUnlink });
        Reject("object removal also requires explicit unlink approval", f, () => Apply(f, plan));
        Apply(f, plan, new Dictionary<int, bool> { [1] = true });
        Check("reviewed removal zeros both activation partners and preserves inactive object identity", f.Device.ParameterValues[P(16)] == "0" && f.Device.ParameterValues[P(17)] == "0"
            && f.Device.ComObjectRefs.Single().Id == "O-1_R-11" && f.Device.ComObjectRefs.Single().Links.Count == 0
            && !f.Project.Workbench.Objects[new ObjectKey(f.Device.Id, "O-1_R-11").ToString()].Active);

        f = Create(); plan = Preview(f); f.Device.ParameterValues[P(17)] = "2";
        Reject("external parameter edit invalidates the project review stamp", f, () => Apply(f, plan));
        f = Create(); plan = Preview(f); f.Device.BinaryData["Other"][0] = 99;
        Reject("external byte-array edit invalidates the project review stamp", f, () => Apply(f, plan));
        f = Create(); plan = Preview(f); f.Definition.Descendants().Single(e => (string?)e.Attribute("Id") == App + "_O-1_R-11").SetAttributeValue("FunctionText", "changed");
        Reject("definition mutation invalidates the reviewed definition hash", f, () => Apply(f, plan));
        f = Create(); plan = Preview(f); var other = Create();
        Reject("another project with identical ids cannot consume the review", other, () => JungKspImport.Apply(other.Project, plan, other.Definition));
        f = Create(); plan = Preview(f);
        Reject("unreviewed object decisions are rejected", f, () => Apply(f, plan, new Dictionary<int, bool> { [2] = true }));

        f = Create(); var exposed = Payload(); var raw = exposed.Blocks[1].GetBytes(); plan = Preview(f, exposed); Array.Fill(raw, (byte)0);
        Apply(f, plan); Check("mutating returned source-frame bytes cannot modify prepared writes", Convert.FromBase64String(f.Device.ParameterValues[P(2)]).SequenceEqual(exposed.Blocks[1].GetBytes()));
        f = Create(); SetOpaque(f.Device);
        Reject("opaque binary-data metadata blocks preview", f, () => Preview(f));
        f = Create(); plan = Preview(f); SetOpaque(f.Device);
        Reject("opaque metadata appearing after preview blocks Apply", f, () => Apply(f, plan));

        f = Create(); f.Device.ComObjectRefs.Single().Number = 2;
        Reject("saved number overrides cannot substitute for an exact reference", f, () => Preview(f));
        f = Create(); f.Device.ParameterValues["P-unknown_R-unknown"] = "1";
        Reject("unknown saved parameter identities are not guessed", f, () => Preview(f));
        f = Create(); f.Project.Installations[0].MainRanges[0].Addresses.Add(new() { Id = "G1", Name = "Duplicate identity",
            Address = GroupAddress.ThreeLevel(1, 0, 2), DatapointType = "DPST-1-1" });
        Reject("retained links require exactly one matching group-address id", f, () => Preview(f));
        f = Create(); f.Project.Installations[0].MainRanges[0].Addresses.RemoveAll(g => g.Id == "G1");
        Reject("missing group-address identities cannot be retained", f, () => Preview(f));
        f = Create(); f.Definition.Descendants().Single(e => e.Name.LocalName == "ParameterRef" && (string?)e.Attribute("Id") == P(17)).Remove();
        Reject("missing exact activation partner is rejected", f, () => Preview(f));
        f = Create(); f.Definition.Descendants().Single(e => e.Name.LocalName == "TypeRawData" && (string?)e.Attribute("MaxSize") == "30716").SetAttributeValue("MaxSize", 30717);
        Reject("changed raw-size declaration is outside the specific profile", f, () => Preview(f));
        f = Create(); Reject("oversized opaque raw blob cannot bypass its byte limit", f, () => Preview(f, Payload(raw3: new byte[16381])));
        f = Create(); f.Device.BinaryData["NearLimit"] = new byte[8 * 1024 * 1024 - 2];
        Reject("new function texts cannot exceed the native combined eight-MiB binary budget", f, () => Preview(f));
        f = Create(); Reject("oversized KO text cannot reach project or undo serialization", f, () => Preview(f, Payload(name: new string('x', 256))));
        f = Create(); Reject("unknown DPT master data cannot infer a bit width", f, () => Preview(f, Payload(main: 9, subtype: 1)));
        f = Create(); Reject("unknown DPT subtype is not silently replaced by its main type", f, () => Preview(f, Payload(subtype: 99)));
        f = Create(); Reject("unproven database generation is explicitly unsupported", f, () => Preview(f, Payload(database: "4.0.0")));
        f = Create(); Reject("malformed database metadata cannot silently select legacy flags", f, () => Preview(f, Payload(database: "not-a-version")));
        f = Create(); Reject("legacy API zero has no import authorization", f, () => Preview(f, Payload(api: 0)));
        f = Create(); f.Device.Hardware2ProgramRefId = "M-0004_H-unknown";
        Reject("unresolved hardware aliases cannot establish application binding", f, () => Preview(f));
        f = Create(); f.Definition.Descendants().Single(e => e.Name.LocalName == "ApplicationProgram").SetAttributeValue("Id", "M-0004_A-5018-14-17D9");
        Reject("a different full program identity cannot use the adapter", f, () => Preview(f));
        f = Create(); f.Definition.Descendants().Single(e => e.Name.LocalName == "Static").Add(new XElement(f.Definition.Root!.Name.Namespace + "Script", "throw 1"));
        Reject("import never opts into application scripts", f, () => Preview(f));

        f = Create(); Reject("SYSTEM items are refused before publishing partial item results", f, () => Preview(f, Payload(name: "SYSTEM clock")));
        f = Create(); Reject("thermo classes are outside the ordinary psoe profile", f, () => Preview(f, Payload(@class: "psoe_thermo_logic")));
        f = Create(); Reject("unknown classes cannot guess function mappings", f, () => Preview(f, Payload(@class: "vendor_other")));
        f = Create(); Reject("unmapped items are not partially imported", f, () => Preview(f, Payload(change: root => root["elements"]!["objects_list"] = new JsonObject())));
        f = Create(); Reject("one item cannot belong to multiple function maps", f, () => Preview(f, Payload(change: root =>
            root["elements"]!["objects_list"]!["E"]!["function_map"]!["other"] = "1")));
        f = Create(); Reject("numeric item aliases are ambiguous", f, () => Preview(f, Payload(change: root =>
            root["items"]!["objects_list"]!["01"] = root["items"]!["objects_list"]!["1"]!.DeepClone())));
        f = Create(); Reject("later invalid item does not leave an imported first item", f, () => Preview(f, Payload(change: root =>
        { var invalid = root["items"]!["objects_list"]!["1"]!.DeepClone(); invalid["id_driver"] = ""; root["items"]!["objects_list"]!["2"] = invalid; })));
        f = Create(); Reject("command and status cannot target the same KO number", f, () => Preview(f, Payload(status: 1)));
        f = Create(); Reject("unrecognized configuration roots are opaque, not executable", f, () => Preview(f, Payload(change: root => root["system_unknown"] = new JsonObject())));

        f = Create(); plan = Preview(f, Payload(status: 2)); Apply(f, plan);
        Check("status direction activates its exact KO and localized status suffix", f.Device.ComObjectRefs.Single(o => o.Number == 2) is { Id: "O-2_R-21", Text: "New label Zustand", FunctionText: "Statusfunktion" }
            && f.Device.ParameterValues[P(18)] == "1" && f.Device.ParameterValues[P(19)] == "1");
        f = Create(); plan = Preview(f, Payload(function: "toggle_12", functions: "{\"psoe_switch\":{\"toggle_\":{\"command\":{\"en\":\"Fallback\"}}}}")); Apply(f, plan);
        Check("numeric function suffix and missing language use the supported fallbacks", f.Device.ComObjectRefs.Single().FunctionText == "Fallback");
        f = Create(); plan = Preview(f, Payload(functions: "{}")); Apply(f, plan);
        Check("missing function text uses the selected reference default", f.Device.ComObjectRefs.Single().FunctionText == "Default bit");
        f = Create(); f.Project.Workbench.Objects[new ObjectKey(f.Device.Id, "O-1_R-11").ToString()].Flags = "CRW";
        plan = Preview(f, Payload(flags: 4096)); Apply(f, plan);
        Check("transported I never overwrites existing ReadOnInit state", f.Project.Workbench.Objects[new ObjectKey(f.Device.Id, "O-1_R-11").ToString()].Flags == "CWTU");
        f = Create(); plan = Preview(f, Payload(name: "Old label")); Apply(f, plan);
        Check("semantically unchanged objects retain their existing flags", f.Project.Workbench.Objects[new ObjectKey(f.Device.Id, "O-1_R-11").ToString()].Flags == "CRWI");
        return failures;
    }

    private static Fixture Create()
    {
        var ns = (XNamespace)"http://knx.org/xml/project/20";
        var numberType = App + "_PT-Number"; var raw30 = App + "_PT-Raw30"; var raw16 = App + "_PT-Raw16";
        var staticPart = new XElement(ns + "Static", new XElement(ns + "ParameterTypes",
            new XElement(ns + "ParameterType", new XAttribute("Id", numberType), new XElement(ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", 16), new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 65535))),
            new XElement(ns + "ParameterType", new XAttribute("Id", raw30), new XElement(ns + "TypeRawData", new XAttribute("MaxSize", 30716))),
            new XElement(ns + "ParameterType", new XAttribute("Id", raw16), new XElement(ns + "TypeRawData", new XAttribute("MaxSize", 16380)))));
        var parameters = new XElement(ns + "Parameters"); var refs = new XElement(ns + "ParameterRefs");
        foreach (var n in new[] { 1, 2, 3, 4, 16, 17, 18, 19, 20, 21 })
        {
            var raw = n is 2 or 3 or 4; var value = raw ? "" : n == 1 ? "2048" : n is 16 or 17 ? "1" : "0";
            parameters.Add(new XElement(ns + "Parameter", new XAttribute("Id", App + "_P-" + n), new XAttribute("Name", "Synthetic " + n),
                new XAttribute("ParameterType", raw ? n == 2 ? raw30 : raw16 : numberType), new XAttribute("Value", value), new XAttribute("Access", "None")));
            refs.Add(new XElement(ns + "ParameterRef", new XAttribute("Id", P(n)), new XAttribute("RefId", App + "_P-" + n)));
        }
        var definitions = new XElement(ns + "ComObjectTable"); var objectRefs = new XElement(ns + "ComObjectRefs"); var dynamic = new XElement(ns + "Dynamic");
        for (var n = 1; n <= 3; n++)
        {
            var id = App + "_O-" + n;
            definitions.Add(new XElement(ns + "ComObject", new XAttribute("Id", id), new XAttribute("Name", "Synthetic object"), new XAttribute("Number", n),
                new XAttribute("ObjectSize", "14 Bytes"), new XAttribute("Text", "Default object"), new XAttribute("CommunicationFlag", "Enabled"), new XAttribute("ReadFlag", "Enabled")));
            foreach (var (bits, suffix, function, dpt) in new[] { (1, 1, "Default bit", "DPST-1-1"), (8, 3, "Default byte", "DPST-5-1") })
            {
                var reference = id + "_R-" + (n * 10 + suffix);
                objectRefs.Add(new XElement(ns + "ComObjectRef", new XAttribute("Id", reference), new XAttribute("RefId", id), new XAttribute("ObjectSize", bits == 1 ? "1 Bit" : "1 Byte"),
                    new XAttribute("FunctionText", function), new XAttribute("DatapointType", dpt)));
                dynamic.Add(new XElement(ns + "choose", new XAttribute("ParamRefId", P(2 * n + 14)), new XElement(ns + "when", new XAttribute("test", bits), new XElement(ns + "ComObjectRefRef", new XAttribute("RefId", reference)))));
            }
        }
        staticPart.Add(parameters, refs, definitions, objectRefs);
        var definition = new XDocument(new XElement(ns + "KNX", new XElement(ns + "ApplicationProgram", new XAttribute("Id", App), staticPart, dynamic)));
        var project = new Project { Id = "P-KSPTEST", Name = "Synthetic Jung import" };
        var installation = new Installation { Id = "I-1" }; var area = new Area { Id = "A-1", Address = 1 }; var line = new Line { Id = "L-1", Address = 1 };
        var device = new DeviceInstance { Id = "D1", ProductRefId = App, Hardware2ProgramRefId = App, Address = new(1, 1, 1) };
        var obj = new ComObjectInstanceRef { Id = "O-1_R-11", ComObjectId = "O-1", Number = 1, Text = "Old label", FunctionText = "Old function" }; obj.Links.Add("G1"); device.ComObjectRefs.Add(obj);
        device.ParameterValues[P(3)] = Convert.ToBase64String([7, 8]); device.BinaryData["Other"] = [9, 10];
        line.Devices.Add(device); area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
        var groups = new GroupRange { Id = "GR1", Name = "Synthetic", RangeStart = 1, RangeEnd = 65535 };
        groups.Addresses.Add(new() { Id = "G1", Name = "Bit", Address = GroupAddress.ThreeLevel(1, 0, 1), DatapointType = "DPST-1-1" });
        groups.Addresses.Add(new() { Id = "G8", Name = "Byte", Address = GroupAddress.ThreeLevel(1, 0, 8), DatapointType = "DPST-5-1" }); installation.MainRanges.Add(groups);
        project.Workbench.Groups["G1"] = new() { Bits = 1, Secure = false }; project.Workbench.Groups["G8"] = new() { Bits = 8, Secure = false };
        project.Workbench.Devices[device.Id] = new() { Revision = 3, DownloadedRevision = 2, SecureCapable = false };
        project.Workbench.Objects[new ObjectKey(device.Id, obj.Id).ToString()] = new() { Dpt = "1.001", Bits = 1, Active = true, Flags = "CRWI", Role = "switch", ChannelId = "CH1", SendingGroupId = "G1",
            Connectors = new() { ["G1"] = new() { Send = true, Acknowledge = true, Priority = 2, Slot = 3 } } };
        var types = new List<DatapointType>();
        foreach (var (main, bits) in new[] { (1, 1), (5, 8) })
        {
            var type = new DatapointType { Id = "DPT-" + main, Name = "Synthetic", SizeInBit = bits };
            type.Subtypes.Add(new() { Id = $"DPST-{main}-1", Name = "Synthetic", SizeInBit = bits }); types.Add(type);
        }
        return new(project, device, definition, types);
    }
    private static void AddDormantByteReference(Fixture f)
    {
        var obj = new ComObjectInstanceRef { Id = "O-1_R-13", ComObjectId = "O-1", Number = 1, Text = "History" }; obj.Links.Add("G8"); f.Device.ComObjectRefs.Add(obj);
        f.Project.Workbench.Objects[new ObjectKey(f.Device.Id, obj.Id).ToString()] = new() { Dpt = "5.001", Bits = 8, Active = false, Flags = "CRW", SendingGroupId = "G8", Connectors = new() { ["G8"] = new() { Send = true } } };
    }
    private static JungKspDocument Payload(int command = 1, int? status = null, int main = 1, int? subtype = 1,
        string name = "New label", string @class = "psoe_switch", string function = "toggle", uint flags = 5460,
        bool empty = false, int api = 1, string database = "3.0.0", byte[]? raw3 = null, string? functions = null, Action<JsonObject>? change = null)
    {
        var item = new JsonObject { ["name"] = name, ["command"] = command.ToString(), ["status"] = status?.ToString(), ["type"] = "dtp_" + main,
            ["typeformat"] = subtype?.ToString(), ["flags"] = flags, ["id_driver"] = "synthetic" };
        var config = new JsonObject { ["versions"] = new JsonObject { ["dca_api"] = api, ["database"] = database },
            ["items"] = new JsonObject { ["objects_list"] = empty ? new JsonObject() : new JsonObject { ["1"] = item } },
            ["elements"] = new JsonObject { ["objects_list"] = empty ? new JsonObject() : new JsonObject { ["E"] = new JsonObject { ["@class"] = @class, ["function_map"] = new JsonObject { [function] = "1" } } } } };
        change?.Invoke(config);
        functions ??= "{\"dca_generic\":{\"com_status_label\":{\"de\":\"Zustand\"}},\"psoe_switch\":{\"toggle\":{\"command\":{\"en\":\"Switch\",\"de\":\"Schalten\"},\"status\":{\"de\":\"Statusfunktion\"}}}}";
        var parts = new byte[][] { Pack("{\"configurator\":{\"major\":1}}"), Pack(config.ToJsonString()), raw3 ?? [], [], Encoding.ASCII.GetBytes("##01").Concat(Pack(functions)).ToArray(), Encoding.ASCII.GetBytes("##FF0004|ksp\0") };
        using var bytes = new MemoryStream(); var prefix = new byte[4];
        foreach (var part in parts) { BinaryPrimitives.WriteInt32LittleEndian(prefix, part.Length); bytes.Write(prefix); bytes.Write(part); }
        return JungKsp.Read(bytes.ToArray(), "synthetic.ksp");
    }
    private static byte[] Pack(string json)
    {
        var raw = Encoding.UTF8.GetBytes(json); using var output = new MemoryStream(); var prefix = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)raw.Length); output.Write(prefix);
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, true)) zlib.Write(raw); return output.ToArray();
    }
    private static string P(int number) => App + $"_P-{number}_R-{number}";
    // Inject only the opaque-state guard; native XML decoding of that flag has its own archive regressions.
    private static void SetOpaque(DeviceInstance device) => typeof(DeviceInstance).GetProperty(nameof(DeviceInstance.HasOpaqueBinaryData))!.GetSetMethod(true)!.Invoke(device, [true]);
    private static string State(Fixture f) => JsonSerializer.Serialize(f.Project) + f.Definition.ToString(SaveOptions.DisableFormatting)
        + (Knxproj.LoadApplicationProgram(f.Project, App)?.ToString(SaveOptions.DisableFormatting) ?? "<no application overlay>");
}
