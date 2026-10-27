using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class GeneratedTableSerializerChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private const string AppId = "M-0001_A-0100-10-0000";
    private static readonly LoadProcedureRequest Full = new("all", LoadProcedureAccess.Remote, LoadParts.Full);
    private sealed record Fixture(Project Project, DeviceInstance Device, string Path, int[] ActiveNumbers);
    private static XElement Object(int number, string size, string flags) => new(Ns + "ComObject",
        new XAttribute("Id", AppId + "_O-" + number), new XAttribute("Number", number), new XAttribute("Name", "Synthetic object"),
        new XAttribute("Text", "Synthetic object"), new XAttribute("ObjectSize", size),
        new[] { ("CommunicationFlag", 'C'), ("ReadFlag", 'R'), ("WriteFlag", 'W'), ("TransmitFlag", 'T'),
            ("UpdateFlag", 'U'), ("ReadOnInitFlag", 'I') }.Select(f => new XAttribute(f.Item1, flags.Contains(f.Item2) ? "Enabled" : "Disabled")));
    internal static XDocument Application(GeneratedTableFormat format, int lastActive = 4, int maximum = 5)
    {
        var small = format == GeneratedTableFormat.M112; var numbers = small ? new[] { 0, 1, 3, 4 } : new[] { 1, 2, lastActive, maximum };
        XElement Segment(string id, uint address, string data, string? mask = null)
            => new(Ns + "AbsoluteSegment", new XAttribute("Id", id), new XAttribute("Address", address), new XAttribute("Size", data.Length / 2),
                new XAttribute("MemoryType", "EEPROM"), new XElement(Ns + "Data", Convert.ToBase64String(Convert.FromHexString(data))),
                mask is null ? null : new XElement(Ns + "Mask", Convert.ToBase64String(Convert.FromHexString(mask))));
        var code = new XElement(Ns + "Code");
        if (small)
            code.Add(Segment("GA", 0x4000, "99BEEF" + string.Concat(Enumerable.Repeat("AA", 13)), string.Concat(Enumerable.Repeat("5A", 16))),
                Segment("ASSOC", 0x4201, new string('A', 32)),
                Segment("KO", 0x4400, "997755101120C0121300C0141500C0161720C0181900C0" + new string('A', 18), new string('0', 64)));
        else code.Add(new XElement(Ns + "RelativeSegment", new XAttribute("Id", "ApplicationData"), new XAttribute("LoadStateMachine", 4),
            new XAttribute("Offset", 0), new XAttribute("Size", 1), new XElement(Ns + "Data", "AA==")));
        XElement Table(string name, string id, int? max = null) => new(Ns + name,
            small ? new XAttribute("CodeSegment", id) : null, small ? new XAttribute("Offset", 0) : null,
            max is { } limit ? new XAttribute("MaxEntries", limit) : null);
        var objects = Table("ComObjectTable", "KO");
        objects.Add(Object(numbers[0], "1 Bit", "CRT"), Object(numbers[1], "1 Byte", "RWI"),
            Object(numbers[2], "2 Bytes", "CRWT"), Object(numbers[3], "4 Bytes", ""));
        var refs = numbers.Take(3).Select(number => new XElement(Ns + "ComObjectRef", new XAttribute("Id", AppId + "_O-" + number + "_R-0"),
            new XAttribute("RefId", AppId + "_O-" + number))).ToArray();
        var controls = new XElement(Ns + "LoadProcedure", new XElement(Ns + "LdCtrlConnect"));
        if (!small)
            foreach (var index in new[] { 1, 2, 3 })
                controls.Add(new XElement(Ns + "LdCtrlRelSegment", new XAttribute("LsmIdx", index), new XAttribute("Size", 6000),
                    new XAttribute("Mode", 0), new XAttribute("Fill", 0)));
        controls.Add(new XElement(Ns + "LdCtrlRestart"), new XElement(Ns + "LdCtrlDisconnect"));
        var app = new XElement(Ns + "ApplicationProgram", new XAttribute("Id", AppId), new XAttribute("MaskVersion", small ? "MV-0705" : "MV-07B0"),
            new XAttribute("LoadProcedureStyle", "ProductProcedure"), new XAttribute("DynamicTableManagement", small ? "false" : "true"),
            new XElement(Ns + "Static", code, Table("AddressTable", "GA", 1600), Table("AssociationTable", "ASSOC", 1600), objects,
                new XElement(Ns + "ComObjectRefs", refs), new XElement(Ns + "LoadProcedures", controls)),
            new XElement(Ns + "Dynamic", new XElement(Ns + "ChannelIndependentBlock", refs.Select(r => new XElement(Ns + "ComObjectRefRef", new XAttribute("RefId", (string)r.Attribute("Id")!))))));
        return new(new XElement(Ns + "KNX", new XElement(Ns + "ManufacturerData", new XElement(Ns + "Manufacturer", new XAttribute("RefId", "M-0001"),
            new XElement(Ns + "ApplicationPrograms", app)))));
    }
    internal static XDocument Master(GeneratedTableFormat format)
    {
        var small = format == GeneratedTableFormat.M112; var resources = new XElement(Ns + "Resources");
        var names = new[] { "GroupAddressTable", "GroupAssociationTable", "GroupObjectTable" };
        var flavours = small ? new[] { "AddressTable_Bcu1", "AssociationTable_M112", "GroupObjectTable_M112" }
            : new[] { "AddressTable_SystemB", "AssociationTable_SystemB", "GroupObjectTable_SystemB" };
        for (var i = 0; i < names.Length; i++) resources.Add(new XElement(Ns + "Resource", new XAttribute("Name", names[i]), new XAttribute("Access", "remote"),
            small ? new XElement(Ns + "Location", new XAttribute("AddressSpace", "None"))
                : new XElement(Ns + "Location", new XAttribute("AddressSpace", "RelativeMemory"), new XAttribute("InterfaceObjectRef", i + 1), new XAttribute("PropertyID", 7), new XAttribute("StartAddress", 0)),
            new XElement(Ns + "ResourceType", new XAttribute("Length", small ? 1 : 2), new XAttribute("Flavour", flavours[i]))));
        return new(new XElement(Ns + "KNX", new XElement(Ns + "MasterData", new XElement(Ns + "MaskVersions", new XElement(Ns + "MaskVersion",
            new XAttribute("Id", small ? "MV-0705" : "MV-07B0"), new XAttribute("MaskVersion", small ? 1797 : 1968),
            new XElement(Ns + "HawkConfigurationData", new XAttribute("LegacyVersion", 0), resources))))));
    }
    private static Fixture Load(string directory, GeneratedTableFormat format, XDocument? application = null, XDocument? master = null,
        int lastActive = 4, int maximum = 5, bool linkLast = true)
    {
        var small = format == GeneratedTableFormat.M112; var numbers = small ? new[] { 0, 1, 3 } : new[] { 1, 2, lastActive };
        var project = new Project { Id = "P-tables", Name = "Synthetic tables", Guid = "00000001-0000-0000-0000-000000000001" };
        var installation = new Installation { Id = "I-tables" }; var area = new Area { Id = "A-tables", Address = 1 };
        var line = new Line { Id = "L-tables", Address = 1, MediumTypeRefId = "MT-0" };
        var device = new DeviceInstance { Id = "D-tables", ProductRefId = AppId, Address = new(1, 1, 1) };
        line.Devices.Add(device); area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
        var range = new GroupRange { Id = "GR-tables", Name = "Synthetic addresses", RangeStart = 0, RangeEnd = 65535 }; installation.MainRanges.Add(range);
        foreach (var (id, address) in new[] { ("GA1", 0x0101), ("GA2", 0x0A03), ("GA3", 0x0B01) })
        {
            range.Addresses.Add(new() { Id = id, Name = id, Address = new((ushort)address) });
            project.Workbench.Groups[id] = new() { Secure = false };
        }
        for (var i = 0; i < numbers.Length; i++)
        {
            var obj = new ComObjectInstanceRef { Id = "O-" + numbers[i] + "_R-0", ComObjectId = "O-" + numbers[i], Number = numbers[i] };
            var groups = i == 0 ? new[] { "GA1" } : i == 1 ? new[] { "GA2", "GA1" } : linkLast ? ["GA3", "GA2"] : Array.Empty<string>();
            obj.Links.AddRange(groups); device.ComObjectRefs.Add(obj);
            if (groups.Length > 0) project.Workbench.Objects[new ObjectKey(device.Id, obj.Id).ToString()] = new() { SendingGroupId = groups[0] };
        }
        var path = Path.Combine(directory, "generated-tables-" + Guid.NewGuid().ToString("N") + ".knxproj");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Put(string name, XDocument document) { using var output = zip.CreateEntry(name).Open(); output.Write(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting))); }
            Put("0.xml", Knxproj.ToXml(project)); Put("M-0001/" + AppId + ".xml", application ?? Application(format, lastActive, maximum));
            Put("knx_master.xml", master ?? Master(format));
        }
        var loaded = Knxproj.Load(path); return new(loaded, ProjectQuery.DeviceList(loaded).Single(), path, numbers);
    }
    private static OfflineApplicationBuildPreview Preview(Fixture fixture, GeneratedTableFormat format, LoadProcedureRequest? request = null)
        => OfflineApplicationBuildPlan.Preview(fixture.Project, fixture.Device, request ?? Full, new(format));
    private static string Xml(Project project) => Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting);
    private static string Hex(GeneratedTablePreview plan, GeneratedTableRole role) => plan.Payloads.Single(p => p.Role == role).Data.ToHexString();

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool value) { checks++; Console.WriteLine((value ? "PASS " : "FAIL ") + "GeneratedTables " + name); if (!value) failures++; }
        void Refused(string name, Fixture fixture, GeneratedTableFormat format, GeneratedTableIssueCode? expected = null)
        {
            var model = Xml(fixture.Project); var bytes = File.ReadAllBytes(fixture.Path); var history = new UndoStack(); history.Reset(fixture.Project);
            var result = Preview(fixture, format); var tables = result.GeneratedTables;
            Check(name, !result.IsAvailable && result.Binding is null && (tables is null || !tables.IsAvailable && tables.Binding is null
                && tables.Payloads.Count == 0 && tables.ProcedureSizeAdjustments.Count == 0 && tables.NativeWitnessHash is null)
                && (expected is null || tables?.Issues.Single().Code == expected) && !result.ExecutionEligible && !result.DownloadEligible
                && Xml(fixture.Project) == model && File.ReadAllBytes(fixture.Path).SequenceEqual(bytes) && !history.CanUndo && !history.IsDirty);
        }
        var fixture = Load(directory, GeneratedTableFormat.M112); var original = File.ReadAllBytes(fixture.Path); var originalXml = Xml(fixture.Project);
        var history = new UndoStack(); history.Reset(fixture.Project); var revision = fixture.Project.ApplicationDataRevision;
        var ordinary = OfflineApplicationBuildPlan.Preview(fixture.Project, fixture.Device, Full); var result = Preview(fixture, GeneratedTableFormat.M112);
        Check("M112 available with three atomic table roles", result.IsAvailable && result.GeneratedTables is { IsAvailable: true, Payloads.Count: 3 });
        if (result.GeneratedTables is not { IsAvailable: true } plan)
        { Console.WriteLine("Generated table fixture: " + string.Join(",", result.GeneratedTables?.Issues.Select(i => i.Code.ToString()) ?? result.Issues.Select(i => i.Code.ToString()))); return failures; }
        var ga = plan.Payloads.Single(p => p.Role == GeneratedTableRole.GroupAddress); var ko = plan.Payloads.Single(p => p.Role == GeneratedTableRole.CommunicationObject);
        var assoc = plan.Payloads.Single(p => p.Role == GeneratedTableRole.Association);
        Check("GA count and sorted words preserve both source header bytes", ga.Data.ToHexString() == "04BEEF01010A030B01");
        Check("global sending order precedes lower receiving object numbers", assoc.Data.ToHexString() == "0501000201030301010203");
        Check("KO holes inactive definitions pointers and read-init bits are retained", ko.Data.ToHexString() == "05775510116FC012131BC7141500C016177FC8181900CA");
        Check("GA native newly written mask leaves header1 and2 untouched", ga.WriteMask.ToHexString() == "FF0000FFFFFFFFFFFF"
            && ga.BeforeMask?.ToHexString() == "5A5A5A5A5A5A5A5A5A" && ga.AfterMask?.ToHexString() == "FF5A5AFFFFFFFFFFFF");
        Check("every KO header pointer inactive flag and size byte is a full write", ko.WriteMask.ToHexString() == new string('F', 46)
            && ko.BeforeMask?.ToHexString() == new string('0', 46) && ko.AfterMask?.ToHexString() == new string('F', 46));
        Check("association mask is full bytes including count", assoc.WriteMask.ToHexString() == new string('F', 22)
            && assoc.BeforeMask is null && assoc.AfterMask?.ToHexString() == new string('F', 22));
        Check("active C-disabled object remains a valid native table entry", ko.Data[9] == 0x1B && (ko.Data[9] & 4) == 0);
        Check("retained source spans have exact used lengths and arena offsets", ga is { Origin: GeneratedTablePayloadOrigin.RetainedCode, ByteOffset: 0x4000, Length: 9 }
            && assoc.ByteOffset == 0x4201 && ko.ByteOffset == 0x4400 && ko.Length == 23 && ga.BeforeData?.Length == 9);
        Check("omitted tail invalidation stays an explicit image requirement", plan.Requirements.Count(r => r.Code == GeneratedTableRequirementCode.UnusedTailMaskInvalidation) == 2);
        Check("preview authority stays closed", plan.Scope == "GeneratedCommunicationTables" && plan.ProfileVersion == 1
            && !plan.ExecutionEligible && !plan.CompleteApplicationImage && !plan.DownloadEligible && !result.CompleteApplicationImage);
        Check("base procedure binding is exactly unchanged by table opt-in", ordinary.Binding?.Fingerprint == result.Binding?.Fingerprint
            && ordinary.Procedure?.WitnessHash == result.Procedure?.WitnessHash && ordinary.GeneratedTables is null);
        Check("own table binding covers native procedure and payload hashes", plan.Binding is { ProfileVersion: 1, SourceFingerprint.Length: 64,
            ProcedureWitnessHash.Length: 64, NativeWitnessHash.Length: 64, PayloadHash.Length: 64, Fingerprint.Length: 64 }
            && plan.Binding.ProcedureWitnessHash == ordinary.Procedure?.WitnessHash && plan.NativeWitnessHash == plan.Binding.NativeWitnessHash
            && plan.Binding.IsCurrent(fixture.Project, fixture.Device));
        Check("data copyout cannot mutate payload or its binding", CopyProtected(ko.Data) && CopyProtected(ko.WriteMask));
        var collectionProtected = false; try { ((IList<GeneratedTablePayload>)plan.Payloads).Clear(); } catch (NotSupportedException) { collectionProtected = true; }
        Check("published payload collection is detached and read only", collectionProtected && plan.Payloads.Count == 3);
        var first = fixture.Device.ComObjectRefs[0]; var firstKey = new ObjectKey(fixture.Device.Id, first.Id).ToString();
        var firstMeta = fixture.Project.Workbench.Objects[firstKey]; var savedLinks = first.Links.ToArray(); var sending = firstMeta.SendingGroupId;
        first.Links.Clear(); firstMeta.SendingGroupId = null; firstMeta.Connectors.Clear();
        var unlinked = Preview(fixture, GeneratedTableFormat.M112);
        Check("active unlinked object retains R and T with C reset and source I retained", unlinked.GeneratedTables is { IsAvailable: true } unlinkedTables
            && unlinkedTables.Payloads.Single(p => p.Role == GeneratedTableRole.CommunicationObject).Data[5] == 0x6B
            && !plan.Binding!.IsCurrent(fixture.Project, fixture.Device));
        first.Links.AddRange(savedLinks); firstMeta.SendingGroupId = sending;
        Check("restoring exact links revalidates table binding", plan.Binding!.IsCurrent(fixture.Project, fixture.Device));
        firstMeta.Active = false; var ignoredView = Preview(fixture, GeneratedTableFormat.M112);
        Check("Workbench Active is not native activation authority", ignoredView.GeneratedTables is { IsAvailable: true } viewTables
            && Hex(viewTables, GeneratedTableRole.CommunicationObject) == ko.Data.ToHexString()); firstMeta.Active = true;
        var grouped = Preview(fixture, GeneratedTableFormat.M112, Full with { Parts = LoadParts.Groups });
        Check("load parts are covered by the distinct table binding", grouped.GeneratedTables is { IsAvailable: true } groupTables
            && groupTables.Binding?.Fingerprint != plan.Binding.Fingerprint && groupTables.Binding?.ProcedureWitnessHash != plan.Binding.ProcedureWitnessHash);
        Check("inspection never changes source model revision or undo", File.ReadAllBytes(fixture.Path).SequenceEqual(original) && Xml(fixture.Project) == originalXml
            && fixture.Project.ApplicationDataRevision == revision && !history.IsDirty && !history.CanUndo && history.Steps == 0);

        var small = Load(directory, GeneratedTableFormat.SystemB2ByteAssociations); var smallPlan = Preview(small, GeneratedTableFormat.SystemB2ByteAssociations).GeneratedTables;
        Check("SystemB small produces all three fresh logical resources", smallPlan is { IsAvailable: true, Payloads.Count: 3 }
            && smallPlan.Payloads.All(p => p.Origin == GeneratedTablePayloadOrigin.FreshGenerated && p.BeforeData is null && p.BeforeMask is null));
        if (smallPlan is { IsAvailable: true })
        {
            Check("SystemB GA contains no device-address pseudoentry", Hex(smallPlan, GeneratedTableRole.GroupAddress) == "000301010A030B01");
            Check("SystemB small association has uint16 count and byte entries", Hex(smallPlan, GeneratedTableRole.Association) == "000501010202030401020204");
            Check("SystemB reserved0 sparse and bare inactive slots remain exact", Hex(smallPlan, GeneratedTableRole.CommunicationObject) == "00054F003B0700005F08000A");
            Check("fresh generated mask is the complete written byte span", smallPlan.Payloads.All(p => p.WriteMask.ToArray().All(b => b == 255)
                && p.AfterMask?.ToHexString() == p.WriteMask.ToHexString()));
            Check("logical LSMs stay separate and contain only computed bytes", smallPlan.Payloads.Select(p => p.Arena.LoadStateMachine).SequenceEqual(new byte?[] { 1, 2, 3 })
                && smallPlan.Payloads.All(p => p.ByteOffset == 0 && p.Arena.Kind == ResourcePatchArenaKind.RelativeMemory));
            Check("required procedure sizing stays explicit and unapplied", smallPlan.ProcedureSizeAdjustments.Count == 3
                && smallPlan.ProcedureSizeAdjustments.All(a => a.BeforeValue == 6000) && smallPlan.ProcedureSizeAdjustments.Select(a => a.AfterValue).SequenceEqual(new ulong[] { 8, 12, 12 }));
        }
        var big = Preview(small, GeneratedTableFormat.SystemB4ByteAssociations).GeneratedTables;
        Check("explicit SystemB big format preserves full-width entries", big is { IsAvailable: true } && smallPlan is { IsAvailable: true }
            && Hex(big, GeneratedTableRole.Association) == "00050001000100020002000300040001000200020004"
            && Hex(big, GeneratedTableRole.GroupAddress) == Hex(smallPlan!, GeneratedTableRole.GroupAddress)
            && big.Binding?.Fingerprint != smallPlan!.Binding?.Fingerprint);
        var wide = Load(directory, GeneratedTableFormat.SystemB2ByteAssociations, lastActive: 300, maximum: 1000, linkLast: false);
        var widePlan = Preview(wide, GeneratedTableFormat.SystemB2ByteAssociations).GeneratedTables;
        Check("two-byte association profile accepts 1000 slots with low linked object numbers", widePlan is { IsAvailable: true }
            && widePlan.Payloads.Single(p => p.Role == GeneratedTableRole.CommunicationObject).Length == 2002);
        if (widePlan is { IsAvailable: true })
            Check("active unlinked object300 retains RWT while clearing C", widePlan.Payloads.Single(p => p.Role == GeneratedTableRole.CommunicationObject).Data[600] == 0x5B);
        var high = Load(directory, GeneratedTableFormat.SystemB2ByteAssociations, lastActive: 300, maximum: 1000);
        Refused("only an associated high object exceeds the small entry profile", high, GeneratedTableFormat.SystemB2ByteAssociations, GeneratedTableIssueCode.TableCapacityExceeded);
        Check("the same associated high object fits explicit big entries", Preview(high, GeneratedTableFormat.SystemB4ByteAssociations).GeneratedTables?.IsAvailable == true);

        foreach (var (name, change) in new (string, Action<XDocument>)[]
        {
            ("unknown size never becomes a one-bit object", d => d.Descendants(Ns + "ComObject").First().SetAttributeValue("ObjectSize", "unknown")),
            ("short KO source data is not padded to Size", d => d.Descendants(Ns + "AbsoluteSegment").Single(e => (string?)e.Attribute("Id") == "KO").Element(Ns + "Data")!.Value = Convert.ToBase64String(new byte[22])),
            ("missing source bytes do not become zeros", d => d.Descendants(Ns + "AbsoluteSegment").Single(e => (string?)e.Attribute("Id") == "KO").Element(Ns + "Data")!.Remove()),
            ("unselected colliding source owner cannot be ignored", d => d.Descendants(Ns + "Code").Single().Add(new XElement(Ns + "AbsoluteSegment", new XAttribute("Id", "Other"), new XAttribute("Address", 0x4401), new XAttribute("Size", 1), new XElement(Ns + "Data", "AA==")))),
            ("native reference cannot override its slot with an undeclared Number", d => d.Descendants(Ns + "ComObjectRef").First().SetAttributeValue("Number", 8)),
        })
        { var document = Application(GeneratedTableFormat.M112); change(document); Refused(name, Load(directory, GeneratedTableFormat.M112, document), GeneratedTableFormat.M112); }
        var occupied = Application(GeneratedTableFormat.SystemB2ByteAssociations);
        occupied.Descendants(Ns + "Code").Single().Add(new XElement(Ns + "RelativeSegment", new XAttribute("Id", "Other"), new XAttribute("LoadStateMachine", 1),
            new XAttribute("Offset", 0), new XAttribute("Size", 1), new XElement(Ns + "Data", "AA==")));
        Refused("a fresh logical table cannot overwrite an unselected source owner", Load(directory, GeneratedTableFormat.SystemB2ByteAssociations, occupied),
            GeneratedTableFormat.SystemB2ByteAssociations, GeneratedTableIssueCode.AmbiguousResourceOwnership);
        var missingFlavour = Master(GeneratedTableFormat.M112); missingFlavour.Descendants(Ns + "Resource").First().Remove();
        Refused("mask flavour triplet must be complete", Load(directory, GeneratedTableFormat.M112, master: missingFlavour), GeneratedTableFormat.M112, GeneratedTableIssueCode.UnsupportedTableFormat);
        Refused("a known M112 archive cannot be treated as SystemB", fixture, GeneratedTableFormat.SystemB2ByteAssociations, GeneratedTableIssueCode.UnsupportedTableFormat);
        var failure = GeneratedTablePreview.Unavailable(new(GeneratedTableIssueCode.InvalidRequest));
        Check("failure never defaults to M112 or retains partial outputs", failure.Format is null && failure.Binding is null && failure.Payloads.Count == 0 && failure.ProcedureSizeAdjustments.Count == 0);
        var bigFailure = GeneratedTablePreview.Unavailable(new(GeneratedTableIssueCode.InputsChanged), GeneratedTableFormat.SystemB4ByteAssociations);
        Check("explicit failed format remains diagnostic with no authority", bigFailure.Format == GeneratedTableFormat.SystemB4ByteAssociations && !bigFailure.IsAvailable && bigFailure.NativeWitnessHash is null);
        var oldBinding = plan.Binding; var groupAddress = ProjectQuery.GroupAddresses(fixture.Project).Single(g => g.Id == "GA1");
        groupAddress.Address = new(0x0102); history.Push(fixture.Project);
        Check("changed group address stales the original table binding", !oldBinding!.IsCurrent(fixture.Project, fixture.Device));
        var undone = history.Undo()!; var undoneDevice = ProjectQuery.DeviceList(undone).Single();
        var undoPreview = OfflineApplicationBuildPlan.Preview(undone, undoneDevice, Full, new(GeneratedTableFormat.M112));
        Check("undo restores payloads without reviving previous live identity", undoPreview.GeneratedTables is { IsAvailable: true } undoTables
            && Hex(undoTables, GeneratedTableRole.GroupAddress) == ga.Data.ToHexString() && !oldBinding.IsCurrent(undone, undoneDevice)
            && undoTables.Binding!.IsCurrent(undone, undoneDevice));
        Check("all serializer tests retain original source archive bytes", File.ReadAllBytes(fixture.Path).SequenceEqual(original));
        Console.WriteLine($"Generated tables: {checks - failures}/{checks} passed");
        return failures;
    }
    private static bool CopyProtected(ImmutablePatchBytes bytes)
    { var hash = bytes.Sha256; var copy = bytes.ToArray(); if (copy.Length > 0) copy[0] ^= 255; return bytes.Sha256 == hash && !copy.SequenceEqual(bytes.ToArray()); }
}
