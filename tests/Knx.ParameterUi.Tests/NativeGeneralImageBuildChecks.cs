using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class NativeGeneralImageBuildChecks
{
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly XNamespace Ns = PropertySourceChecks.Ns;
    private static readonly Type Builder = typeof(Project).Assembly.GetType("Knx.NativeGeneralImageBuild", true)!;
    private static T Get<T>(object value, string name) => (T)value.GetType().GetProperty(name, Any)!.GetValue(value)!;
    private static object[] Items(object value, string name) => Get<IEnumerable>(value, name).Cast<object>().ToArray();
    private static bool Available(object value) => Get<bool>(value, "IsAvailable");
    private static object Build(ArchivedImageSourceChecks.Fixture fixture, uint[]? options = null, GeneratedTableFormat? tables = null)
        => Builder.GetMethod("Build", Hidden)!.Invoke(null, [fixture.Project, fixture.Device, options ?? [1u],
            tables is { } format ? new GeneratedTableRequest(format) : null])!;
    private static string Explain(object value) => string.Join(",", Items(value, "Issues").Select(i => Get<object>(i, "Code").ToString()))
        + string.Join(";", Items(value, "Variants").Select(v => string.Join(",", Items(v, "Issues").Select(i => Get<object>(i, "Code").ToString()))));
    private static object Only(object plan) => Items(plan, "Variants").Single();
    private static object Chunk(object variant, string space, uint offset, ushort? pid = null)
        => Items(variant, "Chunks").Single(c =>
        {
            var range = Get<object>(c, "Range"); var target = Get<object>(range, "Target");
            return Get<object>(target, "Space").ToString() == space && Get<uint>(range, "ByteOffset") == offset
                && (pid is null || Get<PropertySourceSelector?>(target, "Selector")?.PropertyId == pid);
        });
    private static byte[] Bytes(object chunk, string role) => Get<ImmutablePatchBytes>(chunk, role).ToArray();
    internal static XDocument MixedApplication()
    {
        var app = NativeGeneralPopulationChecks.App(); var stat = NativeGeneralPopulationChecks.Stat(app);
        stat.Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "Two"),
            new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", 2),
                new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 3))));
        var first = stat.Element(Ns + "Parameters")!.Elements().First(); first.SetAttributeValue("ParameterType", "Two");
        first.SetAttributeValue("Value", 2); first.Element(Ns + "Memory")!.SetAttributeValue("BitOffset", 6);
        var procedure = stat.Element(Ns + "LoadProcedures")!.Element(Ns + "LoadProcedure")!;
        procedure.Elements().Last().AddBeforeSelf(new XElement(Ns + "LdCtrlWriteProp", new XAttribute("ObjIdx", 3),
            new XAttribute("PropId", 11), new XAttribute("StartElement", 1), new XAttribute("Count", 2), new XAttribute("Verify", false)));
        return app;
    }
    internal static XDocument MixedMaster()
    {
        var master = PropertySourceChecks.Master("PDT_GENERIC_04");
        var configuration = master.Descendants(Ns + "HawkConfigurationData").Single();
        configuration.Element(Ns + "InterfaceObjects")!.Element(Ns + "InterfaceObject")!.Add(new XElement(Ns + "Property",
            new XAttribute("PropertyID", 11), new XAttribute("PropertyDataType", "PDT_GENERIC_04")));
        configuration.Add(new XElement(Ns + "MemorySegments", new XElement(Ns + "MemorySegment", new XAttribute("MemoryType", "EEPROM"),
            new XAttribute("Length", 512), new XAttribute("Optional", false),
            new XElement(Ns + "Location", new XAttribute("AddressSpace", "StandardMemory"), new XAttribute("StartAddress", 100)),
            new XElement(Ns + "AccessRights", new XAttribute("Read", "Runtime"), new XAttribute("Write", "Configuration")))));
        var names = new[] { "GroupAddressTablePtr", "GroupAssociationTablePtr", "GroupObjectTablePtr", "ApplicationDataPtr", "PeiprogDataPtr" };
        configuration.Add(new XElement(Ns + "Resources", names.Select((name, i) => new XElement(Ns + "Resource", new XAttribute("Name", name),
            new XAttribute("Access", "remote"), new XElement(Ns + "Location", new XAttribute("AddressSpace", "SystemProperty"),
                new XAttribute("StartAddress", 0), new XAttribute("InterfaceObjectRef", i + 1), new XAttribute("PropertyID", 7)),
            new XElement(Ns + "ResourceType", new XAttribute("Length", 4))))));
        return master;
    }
    internal static ArchivedImageSourceChecks.Fixture Mixed(string directory, XDocument? app = null, XDocument? master = null, byte[]? raw = null)
    {
        raw ??= PropertySourceChecks.Frame(data: Convert.FromHexString("DEADBEEF10203040"), dataMask: new byte[8], count: [0x12, 0x34]);
        return ArchivedImageSourceChecks.Load(directory, app: app ?? MixedApplication(), master: master ?? MixedMaster(),
            hardware: PropertyInitializationChecks.Hardware(raw));
    }
    private static bool Empty(object plan) => Get<bool>(plan, "HasGlobalFailure") && !Available(plan)
        && Get<object?>(plan, "Binding") is null && Items(plan, "Variants").Length == 0 && Get<string?>(plan, "Fingerprint") is null;

    internal static ArchivedImageSourceChecks.Fixture TableFixture(string directory, bool overlaps = false)
    {
        var app = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.M112);
        var program = app.Descendants(Ns + "ApplicationProgram").Single(); var stat = program.Element(Ns + "Static")!;
        program.SetAttributeValue("MinEtsVersion", "6.0");
        stat.Add(new XElement(Ns + "Options", new XAttribute("ParameterByteOrder", "BigEndian"), new XAttribute("DownloadInvisibleParameters", "DefaultValue")),
            new XElement(Ns + "ParameterTypes", new XElement(Ns + "ParameterType", new XAttribute("Id", "HeaderType"),
                new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", 16),
                    new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 65535)))),
            new XElement(Ns + "Parameters", new XElement(Ns + "Parameter", new XAttribute("Id", "Header"), new XAttribute("ParameterType", "HeaderType"),
                new XAttribute("Value", 0xCAFE), new XElement(Ns + "Memory", new XAttribute("CodeSegment", "GA"),
                    new XAttribute("Offset", overlaps ? 0 : 1), new XAttribute("BitOffset", 0)))),
            new XElement(Ns + "ParameterRefs", new XElement(Ns + "ParameterRef", new XAttribute("Id", "HeaderRef"), new XAttribute("RefId", "Header"))));
        program.Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "HeaderRef")));
        var fixture = ArchivedImageSourceChecks.Load(directory, app: app, master: GeneratedTableSerializerChecks.Master(GeneratedTableFormat.M112),
            hardware: PropertyInitializationChecks.Hardware());
        var installation = fixture.Project.Installations.Single(); installation.Areas.Single().Lines.Single().MediumTypeRefId = "MT-0";
        var range = new GroupRange { Id = "GeneralGroups", Name = "Groups", RangeStart = 0, RangeEnd = 65535 }; installation.MainRanges.Add(range);
        foreach (var (id, address) in new[] { ("GA1", 0x0101), ("GA2", 0x0A03), ("GA3", 0x0B01) })
        {
            range.Addresses.Add(new() { Id = id, Name = id, Address = new((ushort)address) });
            fixture.Project.Workbench.Groups[id] = new() { Secure = false };
        }
        var numbers = new[] { 0, 1, 3 };
        for (var i = 0; i < numbers.Length; i++)
        {
            var obj = new ComObjectInstanceRef { Id = "O-" + numbers[i] + "_R-0", ComObjectId = "O-" + numbers[i], Number = numbers[i] };
            var groups = i == 0 ? new[] { "GA1" } : i == 1 ? new[] { "GA2", "GA1" } : new[] { "GA3", "GA2" };
            obj.Links.AddRange(groups); fixture.Device.ComObjectRefs.Add(obj);
            fixture.Project.Workbench.Objects[new ObjectKey(fixture.Device.Id, obj.Id).ToString()] = new() { SendingGroupId = groups[0] };
        }
        return fixture;
    }

    internal static ArchivedImageSourceChecks.Fixture SystemBTableFixture(string directory, GeneratedTableFormat format)
    {
        var app = GeneratedTableSerializerChecks.Application(format); var stat = app.Descendants(Ns + "Static").Single();
        var code = stat.Element(Ns + "Code")!;
        var sizes = new[] { 8, format == GeneratedTableFormat.SystemB4ByteAssociations ? 22 : 12, 12 };
        var declarations = new[] { "AddressTable", "AssociationTable", "ComObjectTable" };
        for (var i = 0; i < 3; i++)
        {
            var id = "TableBacking" + (i + 1);
            code.Add(new XElement(Ns + "RelativeSegment", new XAttribute("Id", id), new XAttribute("LoadStateMachine", i + 1),
                new XAttribute("Offset", 0), new XAttribute("Size", sizes[i]),
                new XElement(Ns + "Data", Convert.ToBase64String(Enumerable.Repeat((byte)0xA5, sizes[i]).ToArray())),
                new XElement(Ns + "Mask", Convert.ToBase64String(new byte[sizes[i]]))));
            stat.Element(Ns + declarations[i])!.SetAttributeValue("CodeSegment", id);
            stat.Element(Ns + declarations[i])!.SetAttributeValue("Offset", 0);
        }
        var fixture = ArchivedImageSourceChecks.Load(directory, app: app, master: GeneratedTableSerializerChecks.Master(format),
            hardware: PropertyInitializationChecks.Hardware());
        var installation = fixture.Project.Installations.Single(); installation.Areas.Single().Lines.Single().MediumTypeRefId = "MT-0";
        var range = new GroupRange { Id = "SystemBGroups", Name = "Groups", RangeStart = 0, RangeEnd = 65535 }; installation.MainRanges.Add(range);
        foreach (var (id, address) in new[] { ("GA1", 0x0101), ("GA2", 0x0A03), ("GA3", 0x0B01) })
        {
            range.Addresses.Add(new() { Id = id, Name = id, Address = new((ushort)address) });
            fixture.Project.Workbench.Groups[id] = new() { Secure = false };
        }
        var numbers = new[] { 1, 2, 4 };
        for (var i = 0; i < numbers.Length; i++)
        {
            var obj = new ComObjectInstanceRef { Id = "O-" + numbers[i] + "_R-0", ComObjectId = "O-" + numbers[i], Number = numbers[i] };
            var groups = i == 0 ? new[] { "GA1" } : i == 1 ? new[] { "GA2", "GA1" } : new[] { "GA3", "GA2" };
            obj.Links.AddRange(groups); fixture.Device.ComObjectRefs.Add(obj);
            fixture.Project.Workbench.Objects[new ObjectKey(fixture.Device.Id, obj.Id).ToString()] = new() { SendingGroupId = groups[0] };
        }
        return fixture;
    }

    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "NativeGeneralImageBuild " + name); if (!ok) failures++; }
        var fixture = Mixed(directory); var before = Knxproj.ToXml(fixture.Project).ToString(SaveOptions.DisableFormatting);
        var beforeFile = SHA256.HashData(File.ReadAllBytes(fixture.Path)); var plan = Build(fixture);
        Check("one mixed working engine accepts region, code, property, five pointers and 131 references: " + Explain(plan), Available(plan));
        if (Available(plan))
        {
            var variant = Only(plan); var memory = Chunk(variant, "StandardMemory", 100); var property = Chunk(variant, "PropertyData", 0, 10);
            var expectedInitial = new byte[512]; expectedInitial[0] = 0xA0;
            var expectedInitialMask = new byte[512];
            var expectedWorking = new byte[512]; expectedWorking[0] = 0xA2;
            for (var i = 1; i < 128; i++) BinaryPrimitives.WriteUInt32BigEndian(expectedWorking.AsSpan(i * 4, 4), checked((uint)i + 1));
            var expectedMask = Enumerable.Repeat((byte)255, 512).ToArray(); expectedMask[1] = expectedMask[2] = expectedMask[3] = 0;
            Check("full independent initial bytes include native zero-region/code A0 rather than isolated A5", Bytes(memory, "InitialData").SequenceEqual(expectedInitial));
            Check("non-QueryFull clears all initial validity without clearing raw data", Bytes(memory, "InitialValidity").SequenceEqual(expectedInitialMask));
            Check("full 512-byte working literal combines low-bit A2 and all 127 four-byte writers", Bytes(memory, "WorkingData").SequenceEqual(expectedWorking));
            Check("full 512-byte desired mask preserves three unwritten unknown bytes", Bytes(memory, "DesiredValidity").SequenceEqual(expectedMask));
            Check("full property initial data clones the exact raw factory bytes without adopting its mask", Bytes(property, "InitialData").SequenceEqual(Convert.FromHexString("DEADBEEF10203040"))
                && Bytes(property, "InitialValidity").SequenceEqual(new byte[8]));
            Check("full property final bytes and validity independently match values129 and130", Bytes(property, "WorkingData").SequenceEqual(Convert.FromHexString("0000008100000082"))
                && Bytes(property, "DesiredValidity").SequenceEqual(Enumerable.Repeat((byte)255, 8)));
            var unrelated = Chunk(variant, "PropertyData", 0, 11);
            Check("unrelated actual property allocation is retained unknown and untouched", Bytes(unrelated, "InitialData").SequenceEqual(new byte[8])
                && Bytes(unrelated, "WorkingData").SequenceEqual(new byte[8]) && Bytes(unrelated, "DesiredValidity").SequenceEqual(new byte[8]));
            var pointers = Items(variant, "Chunks").Where(c => Get<PropertySourceSelector?>(Get<object>(Get<object>(c, "Range"), "Target"), "Selector")?.PropertyId == 7).ToArray();
            Check("all five actual pointer allocations retain four zero bytes with zero validity", pointers.Length == 5
                && pointers.All(c => Bytes(c, "WorkingData").SequenceEqual(new byte[4]) && Bytes(c, "DesiredValidity").SequenceEqual(new byte[4])));
            var counts = Items(Get<object>(variant, "Initialization"), "ObservedCounts");
            Check("archived count1234 is observed without adoption", counts.Length == 1 && Get<bool>(counts[0], "NotAdopted")
                && Get<ushort>(counts[0], "EncodedCount") == 0x1234 && Get<ImmutablePatchBytes>(counts[0], "Data").ToHexString() == "1234");
            Check("complete mixed population has all131 records and distinct NoStorage", Items(Get<object>(variant, "Population"), "Records").Length == 131
                && Items(Get<object>(variant, "Population"), "Claims").Length == 130);
            Check("all payload copies fit one aggregate ledger and independent procedure budget", Get<long>(plan, "AllocatedImagePayloadBytes") > 0
                && Get<long>(plan, "AllocatedImagePayloadBytes") <= Get<long>(plan, "ReservedWorstCaseImagePayloadBytes")
                && Get<long>(plan, "ReservedWorstCaseImagePayloadBytes") <= 16L * 1024 * 1024
                && Get<long>(plan, "ReservedProcedurePayloadBytes") <= 16L * 1024 * 1024);
            Check("conditional option assumptions grant no actual route, complete image or execution", !Get<bool>(plan, "HasActualRoute")
                && !Get<bool>(plan, "CompleteApplicationImage") && !Get<bool>(plan, "ExecutionEligible") && !Get<bool>(plan, "DownloadEligible"));
            var binding = Get<object>(plan, "Binding");
            bool Current() => (bool)binding.GetType().GetMethod("IsCurrent", Any)!.Invoke(binding, [fixture.Project, fixture.Device])!;
            Check("new combined binding replays its own complete engine", Current());
            fixture.Device.ParameterValues["R127"] = "513";
            Check("an unselected last saved reference invalidates the complete binding", !Current());
            fixture.Device.ParameterValues.Clear();
            Check("exact restoration replays the same combined binding", Current());
            var copied = Get<ImmutablePatchBytes>(memory, "WorkingData").ToArray(); copied[0] = 0;
            Check("copy-out cannot alter working bytes or binding", Bytes(memory, "WorkingData")[0] == 0xA2 && Current());
            var full = Build(fixture, [129]);
            Check("QueryFull separately retains explicit F0 and skips the five pointer seeds", Available(full)
                && Bytes(Chunk(Only(full), "StandardMemory", 100), "InitialValidity")[0] == 0xF0
                && Items(Only(full), "Chunks").Count(c => Get<PropertySourceSelector?>(Get<object>(Get<object>(c, "Range"), "Target"), "Selector")?.PropertyId == 7) == 0);
        }
        Check("combined engine preserves source project and exact archive bytes", before == Knxproj.ToXml(fixture.Project).ToString(SaveOptions.DisableFormatting)
            && beforeFile.SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture.Path))));
        Check("duplicate option assumptions fail atomically", Empty(Build(fixture, [129, 129])));
        Check("more than16 option assumptions fail before source work", Empty(Build(fixture, Enumerable.Range(0, 17).Select(i => (uint)i).ToArray())));
        var padded = MixedApplication(); NativeGeneralPopulationChecks.Stat(padded).Descendants(Ns + "Mask").Single().Remove();
        var noRegion = MixedMaster(); noRegion.Descendants(Ns + "MemorySegments").Single().Remove();
        var paddedPlan = Build(Mixed(directory, padded, noRegion), [129]);
        Check("maskless short code data creates explicit valid prefix and unknown padding: " + Explain(paddedPlan), Available(paddedPlan));
        if (Available(paddedPlan))
        {
            var initial = Bytes(Chunk(Only(paddedPlan), "StandardMemory", 100), "InitialValidity");
            Check("all512 initial mask bytes distinguish one supplied byte from511 padded zeros", initial.Length == 512
                && initial[0] == 255 && initial.Skip(1).All(b => b == 0));
        }
        var absent = new XDocument(padded); NativeGeneralPopulationChecks.Stat(absent).Descendants(Ns + "Data").Single().Remove();
        var absentPlan = Build(Mixed(directory, absent, noRegion), [129]);
        Check("code allocation without supplied data keeps all initial validity zero", Available(absentPlan)
            && Bytes(Chunk(Only(absentPlan), "StandardMemory", 100), "InitialValidity").SequenceEqual(new byte[512]));
        var partial = MixedApplication(); NativeGeneralPopulationChecks.Stat(partial).Element(Ns + "Parameters")!.Elements().Last().Add(new XElement(Ns + "Memory",
            new XAttribute("CodeSegment", "missing"), new XAttribute("Offset", 0), new XAttribute("BitOffset", 0)));
        Check("late unknown writer refuses every alternative with no payload or binding", Empty(Build(Mixed(directory, partial), [129, 128])));
        var tables = Build(TableFixture(directory), [131], GeneratedTableFormat.M112);
        Check("M112 tables consume the current parameter-written working seed: " + Explain(tables), Available(tables));
        if (Available(tables))
        {
            var variant = Only(tables); var emission = Get<object>(variant, "Tables");
            var payloads = Items(emission, "Payloads").Cast<GeneratedTablePayload>().ToArray();
            var ga = payloads.Single(p => p.Role == GeneratedTableRole.GroupAddress);
            Check("retained GA header comes from CAFE parameter bytes rather than archived BEEF", ga.Data.ToHexString() == "04CAFE01010A030B01"
                && ga.BeforeData!.ToHexString().StartsWith("99CAFE", StringComparison.Ordinal)
                && ga.WriteMask.ToHexString() == "FF0000FFFFFFFFFFFF");
            var expectedGa = Convert.FromHexString("04CAFE01010A030B01AAAAAAAAAAAAAA");
            var expectedGaMask = Convert.FromHexString("FFFFFFFFFFFFFFFFFF5A5A5A5A5A5A5A");
            Check("complete GA backing keeps untouched tail bytes and masks", Bytes(Chunk(variant, "StandardMemory", 0x4000), "WorkingData").SequenceEqual(expectedGa)
                && Bytes(Chunk(variant, "StandardMemory", 0x4000), "DesiredValidity").SequenceEqual(expectedGaMask));
            Check("association bytes keep native sending-first order", payloads.Single(p => p.Role == GeneratedTableRole.Association).Data.ToHexString() == "0501000201030301010203");
            var expectedKo = new byte[32]; expectedKo[0] = 5; expectedKo[5] = 0x4F; expectedKo[9] = 0x1B; expectedKo[10] = 7;
            expectedKo[17] = 0x5F; expectedKo[18] = 8; expectedKo[22] = 10;
            Check("complete object backing uses normalized current zeros instead of isolated raw code", Bytes(Chunk(variant, "StandardMemory", 0x4400), "WorkingData").SequenceEqual(expectedKo));
            Check("tables retain explicit procedure, physical and tail requirements", Items(variant, "Requirements").Cast<string>().Contains("TableUnusedTailMaskUnresolved")
                && !Items(variant, "Requirements").Cast<string>().Contains("CommunicationTablesNotApplied"));
            Check("current-seed copies and serializer temporaries stay in aggregate ledger", Get<long>(tables, "AllocatedImagePayloadBytes")
                <= Get<long>(tables, "ReservedWorstCaseImagePayloadBytes"));
        }
        var conflict = Build(TableFixture(directory, overlaps: true), [131], GeneratedTableFormat.M112);
        Check("table data-write collision yields no partial alternative payload or binding", !Available(conflict)
            && Items(conflict, "Variants").All(v => !Available(v) && Items(v, "Chunks").Length == 0 && Get<object?>(v, "Tables") is null));
        foreach (var format in new[] { GeneratedTableFormat.SystemB2ByteAssociations, GeneratedTableFormat.SystemB4ByteAssociations })
        {
            var system = Build(SystemBTableFixture(directory, format), [131], format);
            Check(format + " consumes paired normalized relative working seeds: " + Explain(system), Available(system));
            if (!Available(system)) continue;
            var payloads = Items(Get<object>(Only(system), "Tables"), "Payloads").Cast<GeneratedTablePayload>().ToArray();
            Check(format + " full independent address bytes", payloads.Single(p => p.Role == GeneratedTableRole.GroupAddress).Data.ToHexString() == "000301010A030B01");
            var expected = format == GeneratedTableFormat.SystemB2ByteAssociations ? "000501010202030401020204"
                : "00050001000100020002000300040001000200020004";
            Check(format + " full independent association bytes", payloads.Single(p => p.Role == GeneratedTableRole.Association).Data.ToHexString() == expected);
            Check(format + " full object bytes use normalized zeros instead of rawA5", payloads.Single(p => p.Role == GeneratedTableRole.CommunicationObject).Data.ToHexString() == "00054F003B0700005F08000A"
                && payloads.All(p => p.BeforeData!.ToArray().All(b => b == 0)));
        }
        Console.WriteLine($"NativeGeneralImageBuild: {checks} checks, {failures} failures");
        return failures;
    }
}
