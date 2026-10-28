using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Knx;
using Knx.App.Services;
using Knx.Catalog;

internal static class ParameterStorageChecks
{
    internal const string ProgramId = "M-0001_A-0100-10-0000";
    private static XDocument Document(string declarations) => XDocument.Parse($$"""
        <KNX><ApplicationProgram Id="{{ProgramId}}"><Static><Code>
        <AbsoluteSegment Id="AS" Address="4096" Size="512" MemoryType="EEPROM"/>
        <RelativeSegment Id="RS" LoadStateMachine="4" Offset="16" Size="512"/>
        </Code><ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8"/></ParameterType></ParameterTypes>
        <Parameters>{{declarations}}</Parameters></Static><Dynamic/></ApplicationProgram></KNX>
        """);
    private static ParameterInstance One(string location) => Ksxml.Parse(Document($"<Parameter Id='P' ParameterType='T' Value='1'>{location}</Parameter>")).Instances.Single();
    internal static XDocument Modules() => XDocument.Parse($$"""
        <KNX><ApplicationProgram Id="{{ProgramId}}"><Static><Code>
        <AbsoluteSegment Id="AS" Address="4096" Size="512"/><RelativeSegment Id="RS" LoadStateMachine="4" Offset="16" Size="512"/>
        </Code><ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8"/></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P-count" ParameterType="T" Value="2"/></Parameters><ParameterRefs><ParameterRef Id="R-count" RefId="P-count"/></ParameterRefs>
        <Allocators><Allocator Id="G" Start="100" maxInclusive="300"/></Allocators><ModuleDefs>
        <ModuleDef Id="APP_MD-1"><Arguments><Argument Id="APP_MD-1_A-1" Name="Base" Allocates="16"/></Arguments><Static>
        <Parameters><Parameter Id="APP_MD-1_P-1" ParameterType="T" Value="1"><Memory CodeSegment="AS" Offset="3" BitOffset="2" BaseOffset="APP_MD-1_A-1"/></Parameter>
        <Union SizeInBit="16"><Memory CodeSegment="RS" Offset="4" BitOffset="6" BaseOffset="APP_MD-1_A-1"/>
        <Parameter Id="APP_MD-1_P-2" ParameterType="T" Value="2" Offset="2" BitOffset="5"/></Union></Parameters>
        <ParameterRefs><ParameterRef Id="APP_MD-1_R-1" RefId="APP_MD-1_P-1"/><ParameterRef Id="APP_MD-1_R-2" RefId="APP_MD-1_P-2"/></ParameterRefs></Static>
        <SubModuleDefs><ModuleDef Id="APP_MD-1_SM-1"><Arguments><Argument Id="APP_MD-1_SM-1_A-1" Name="Inner"/></Arguments><Static>
        <Parameters><Parameter Id="APP_MD-1_SM-1_P-1" ParameterType="T" Value="3"><Memory CodeSegment="AS" Offset="1" BitOffset="0" BaseOffset="APP_MD-1_SM-1_A-1"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="APP_MD-1_SM-1_R-1" RefId="APP_MD-1_SM-1_P-1"/></ParameterRefs></Static>
        <Dynamic><ParameterRefRef RefId="APP_MD-1_SM-1_R-1"/></Dynamic></ModuleDef></SubModuleDefs>
        <Dynamic><ParameterRefRef RefId="APP_MD-1_R-1"/><ParameterRefRef RefId="APP_MD-1_R-2"/>
        <Module Id="APP_MD-1_INNER" RefId="APP_MD-1_SM-1"><NumericArg RefId="APP_MD-1_SM-1_A-1" Value="5" BaseValue="APP_MD-1_A-1"/></Module></Dynamic>
        </ModuleDef></ModuleDefs></Static><Dynamic><ParameterRefRef RefId="R-count"/>
        <Repeat Id="Repeat" Count="0" ParameterRefId="R-count"><Module Id="APP_M-1" RefId="APP_MD-1"><NumericArg RefId="APP_MD-1_A-1" AllocatorRefId="G"/></Module></Repeat>
        </Dynamic></ApplicationProgram></KNX>
        """);

    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "Storage " + name); if (!ok) failures++; }
        var absolute = One("<Memory CodeSegment='AS' Offset='3' BitOffset='2'/>");
        Check("absolute and relative arenas retain distinct effective coordinates", absolute.Storage is { Status: ParameterStorageStatus.Supported,
            Kind: ParameterStorageKind.AbsoluteMemory, StartAddress: 4099, BitOffset: 2, CodeSegmentId: "AS", LoadStateMachine: null }
            && One("<Memory CodeSegment='RS' Offset='3' BitOffset='2'/>").Storage is { Kind: ParameterStorageKind.RelativeMemory, StartAddress: 19, LoadStateMachine: 4 });
        Check("no declaration is distinct from a valid zero property offset", One("").Storage.Status == ParameterStorageStatus.NotDeclared
            && One("<Property ObjectIndex='0' PropertyId='0' Offset='0' BitOffset='0'/>").Storage is
                { Status: ParameterStorageStatus.Supported, StartAddress: 0, ObjectIndex: 0, PropertyId: 0, BitOffset: 0 });
        Check("native property index and type/occurrence remain separate", One("<Property ObjectIndex='3' PropertyId='10' Offset='7' BitOffset='1'/>").Storage is
            { Kind: ParameterStorageKind.PropertyByIndex, ObjectIndex: 3, StartAddress: 7, ObjectType: null }
            && One("<Property ObjectType='123' Occurrence='2' PropertyId='10' Offset='7' BitOffset='1'/>").Storage is
                { Kind: ParameterStorageKind.PropertyByType, ObjectType: 123, Occurrence: 2, ObjectIndex: null });
        Check("schema-default occurrence zero is explicit in a supported type location", One("<Property ObjectType='123' PropertyId='10' Offset='7' BitOffset='1'/>").Storage.Occurrence == 0);
        var union = Ksxml.Parse(Document("<Union SizeInBit='16'><Memory CodeSegment='AS' Offset='10' BitOffset='6'/><Parameter Id='U' ParameterType='T' Value='0' Offset='1' BitOffset='5'/></Union>")).Instances.Single();
        Check("union and member offsets include bit carry exactly once", union.Storage is { StartAddress: 4108, BitOffset: 3 });
        var propertyUnion = Ksxml.Parse(Document("<Union SizeInBit='16'><Property ObjectType='123' PropertyId='10' Offset='10' BitOffset='6'/><Parameter Id='U' ParameterType='T' Value='0' Offset='1' BitOffset='5'/></Union>")).Instances.Single();
        Check("property unions use byte offsets with the same bit carry", propertyUnion.Storage is { Kind: ParameterStorageKind.PropertyByType, StartAddress: 12, BitOffset: 3 });
        var detached = new ParameterInstance(absolute);
        Check("detached parameter copies share only an immutable storage value", ReferenceEquals(detached.Storage, absolute.Storage)
            && detached.Storage with { StartAddress = 99 } != absolute.Storage && absolute.Storage.StartAddress == 4099);
        var aliases = Document("<Parameter Id='P' ParameterType='T' Value='1'><Memory CodeSegment='AS' Offset='3' BitOffset='2'/></Parameter>");
        aliases.Descendants("Static").Single().Add(XElement.Parse("<ParameterRefs><ParameterRef Id='R1' RefId='P' Value='2' Text='First'/><ParameterRef Id='R2' RefId='P' Value='3' Text='Second'/></ParameterRefs>"));
        Check("parameter-ref value and text overrides cannot change the parameter's location", Ksxml.Parse(aliases).Instances.All(i => i.Storage == absolute.Storage)
            && Ksxml.Parse(aliases).Instances.Select(i => i.Value).SequenceEqual(["2", "3"]));
        Check("missing module bases identify the exact unresolved argument", One("<Memory CodeSegment='AS' Offset='3' BitOffset='2' BaseOffset='missing'/>").Storage is
            { Status: ParameterStorageStatus.MissingArgument, ArgumentId: "missing", StartAddress: null });
        Check("malformed and absent required offsets never become zero", new[] { "", " Offset='bad'", " Offset='1.5'" }
            .All(a => One("<Memory CodeSegment='AS' BitOffset='0'" + a + "/>").Storage.Status == ParameterStorageStatus.InvalidDeclaration));
        Check("negative values, invalid bits and unsupported byte property ranges do not wrap", new[] {
            "<Memory CodeSegment='AS' Offset='-1' BitOffset='0'/>", "<Memory CodeSegment='AS' Offset='1' BitOffset='8'/>",
            "<Property ObjectIndex='256' PropertyId='10' Offset='0' BitOffset='0'/>", "<Property ObjectType='1' Occurrence='256' PropertyId='10' Offset='0' BitOffset='0'/>",
            "<Property ObjectIndex='1' PropertyId='256' Offset='0' BitOffset='0'/>" }.All(s => One(s).Storage.Status == ParameterStorageStatus.OutOfRange));
        Check("ambiguous property selector and multiple storage locations are invalid", One("<Property ObjectIndex='1' ObjectType='2' PropertyId='10' Offset='0' BitOffset='0'/>").Storage.Status == ParameterStorageStatus.InvalidDeclaration
            && One("<Memory CodeSegment='AS' Offset='0' BitOffset='0'/><Memory CodeSegment='RS' Offset='0' BitOffset='0'/>").Storage.Status == ParameterStorageStatus.InvalidDeclaration);
        Check("unknown storage payload remains visibly unsupported", One("<VendorMemory/>").Storage.Status == ParameterStorageStatus.Unsupported
            && One("<Memory CodeSegment='AS' Offset='0' BitOffset='0' Vendor='1'/>").Storage.Status == ParameterStorageStatus.Unsupported);
        var missingSegment = One("<Memory CodeSegment='missing-segment' Offset='0' BitOffset='0'/>");
        Check("missing code segments retain their identity without an address guess", missingSegment.Storage is { Status: ParameterStorageStatus.UnknownSegment, CodeSegmentId: "missing-segment", StartAddress: null });
        var ambiguous = Document("<Parameter Id='P' ParameterType='T' Value='1'><Memory CodeSegment='AS' Offset='0' BitOffset='0'/></Parameter>");
        ambiguous.Descendants("Code").Single().Add(new XElement(ambiguous.Descendants("AbsoluteSegment").Single()));
        Check("duplicate segment identities stay unknown", Ksxml.Parse(ambiguous).Instances.Single().Storage.Status == ParameterStorageStatus.UnknownSegment);
        var userMemory = new XDocument(ambiguous); userMemory.Descendants("AbsoluteSegment").Last().Remove(); userMemory.Descendants("AbsoluteSegment").Single().SetAttributeValue("UserMemory", "true");
        Check("an unrepresented user-memory arena stays unsupported", Ksxml.Parse(userMemory).Instances.Single().Storage.Status == ParameterStorageStatus.Unsupported);
        Check("storage errors leave ordinary value editing enabled", missingSegment.IsEnabled && missingSegment.UnsupportedReason is null && ParameterValidation.TryValidate(missingSegment, "2", out _));

        var modules = Modules(); var source = modules.ToString(SaveOptions.DisableFormatting);
        var snapshot = ApplicationSnapshot.Evaluate(modules, new Dictionary<string, string?>());
        var outer = snapshot.Instances.Where(i => i.Id.EndsWith("_R-1") && !i.Id.Contains("_INNER")).ToArray();
        var inner = snapshot.Instances.Where(i => i.Id.Contains("_INNER")).ToArray();
        Check("allocator bases are effective per repeated module instance", outer.Select(i => i.Storage.StartAddress).SequenceEqual(new uint?[] { 4199, 4215 }));
        Check("nested numeric bases use the immediate child scope", inner.Select(i => i.Storage.StartAddress).SequenceEqual(new uint?[] { 4202, 4218 }));
        Check("union base and member offset are distinct in repeated instances", snapshot.Instances.Where(i => i.Id.EndsWith("_R-2"))
            .Select(i => (i.Storage.StartAddress, i.Storage.BitOffset)).SequenceEqual(new (uint?, byte?)[] { (123, 3), (139, 3) }));
        var indexed = new XDocument(modules);
        indexed.Descendants("Memory").First().ReplaceWith(XElement.Parse("<Property ObjectIndex='2' PropertyId='10' Offset='5' BitOffset='1' BaseOffset='APP_MD-1_A-1' BaseIndex='APP_MD-1_A-1'/>"));
        Check("property offsets and native object-index bases are independently added", Ksxml.Parse(indexed).Instances.Where(i => i.Id.EndsWith("_R-1") && !i.Id.Contains("_INNER"))
            .Select(i => (i.Storage.StartAddress, i.Storage.ObjectIndex)).SequenceEqual(new (uint?, byte?)[] { (105, 102), (121, 118) }));
        var typed = new XDocument(modules);
        typed.Descendants("Memory").First().ReplaceWith(XElement.Parse("<Property ObjectType='123' Occurrence='2' PropertyId='10' Offset='5' BitOffset='1' BaseOffset='APP_MD-1_A-1' BaseOccurrence='APP_MD-1_A-1'/>"));
        Check("property occurrence bases do not become object indices", Ksxml.Parse(typed).Instances.Where(i => i.Id.EndsWith("_R-1") && !i.Id.Contains("_INNER"))
            .Select(i => (i.Storage.StartAddress, i.Storage.Occurrence, i.Storage.ObjectIndex)).SequenceEqual(new (uint?, byte?, byte?)[] { (105, 102, null), (121, 118, null) }));
        var unionBases = new XDocument(modules);
        unionBases.Descendants("Union").Single().Element("Memory")!.ReplaceWith(XElement.Parse("<Property ObjectType='123' Occurrence='4' PropertyId='10' Offset='4' BitOffset='6' BaseOffset='APP_MD-1_A-1' BaseOccurrence='APP_MD-1_A-1'/>"));
        Check("union property bases belong to the union storage, not the member", Ksxml.Parse(unionBases).Instances.Where(i => i.Id.EndsWith("_R-2"))
            .Select(i => (i.Storage.StartAddress, i.Storage.BitOffset, i.Storage.Occurrence)).SequenceEqual(new (uint?, byte?, byte?)[] { (107, 3, 104), (123, 3, 120) }));
        unionBases.Descendants("Union").Single().Element("Parameter")!.SetAttributeValue("BaseOffset", "APP_MD-1_A-1");
        Check("unproven separate member-storage bases are unsupported", Ksxml.Parse(unionBases).Instances.Where(i => i.Id.EndsWith("_R-2"))
            .All(i => i.Storage.Status == ParameterStorageStatus.Unsupported));
        indexed.Descendants("Property").Single().SetAttributeValue("ObjectIndex", "200");
        Check("effective byte-index overflow is reported instead of wrapping", Ksxml.Parse(indexed).Instances.Where(i => i.Id.EndsWith("_R-1") && !i.Id.Contains("_INNER"))
            .All(i => i.Storage.Status == ParameterStorageStatus.OutOfRange));
        Check("joint fixpoint and repeated evaluations retain typed storage scopes", snapshot.IsValid && Enumerable.Range(0, 3).All(_ => ApplicationSnapshot.Evaluate(modules,
            new Dictionary<string, string?>()).Instances.Select(i => i.Storage).SequenceEqual(snapshot.Instances.Select(i => i.Storage))) && modules.ToString(SaveOptions.DisableFormatting) == source);
        Check("native module arguments retain allocator identities", snapshot.ModuleInstances.Where(i => i.Arguments.Any(a => a.Value == "G")).Count() == 2);
        var crossed = new XDocument(modules); crossed.Descendants("Memory").Last().SetAttributeValue("BaseOffset", "APP_MD-1_A-1");
        Check("inner storage cannot accidentally read an outer argument by name", ApplicationSnapshot.Evaluate(crossed, new Dictionary<string, string?>()).Instances.Where(i => i.Id.Contains("_INNER"))
            .All(i => i.Storage is { Status: ParameterStorageStatus.MissingArgument, ModuleInstanceId: not null }));
        var overflow = new XDocument(modules); overflow.Descendants("NumericArg").Last().Attribute("AllocatorRefId")!.Remove();
        overflow.Descendants("NumericArg").Last().SetAttributeValue("Value", long.MaxValue.ToString());
        // Child BaseValue would overflow the pre-existing module argument evaluator; isolate the storage sum instead.
        overflow.Descendants("SubModuleDefs").Remove(); overflow.Descendants("Module").First().Remove();
        var overflows = Ksxml.Parse(overflow, out var overflowState).Instances.Where(i => i.Id != "R-count").ToArray();
        Check("storage arithmetic overflow is a diagnosis rather than a guessed modulo address", overflows.Length == 4 && !overflowState.StructureFailed
            && overflows.All(i => i.Storage.Status == ParameterStorageStatus.OutOfRange));
        var same = typeof(ParameterEditingSession).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == "SameDeclaration" && m.GetParameters().First().ParameterType == typeof(ParameterInstance));
        Check("declaration matching includes immutable storage facts", (bool)same.Invoke(null, [absolute, new ParameterInstance(absolute), false])!
            && !(bool)same.Invoke(null, [absolute, One("<Memory CodeSegment='AS' Offset='4' BitOffset='2'/>"), false])!);
        failures += DraftChecks(directory, Check);
        return failures;
    }

    private static int DraftChecks(string directory, Action<string, bool> check)
    {
        var path = Path.Combine(directory, "storage-drafts.knxproj");
        var project = ProjectWizard.Create(new() { Name = "Storage draft checks", Areas = 1, LinesPerArea = 1 });
        for (var n = 1; n <= 2; n++) project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D" + n, ProductRefId = ProgramId, Address = new(1, 1, (byte)n) });
        Knxproj.Save(project, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) using (var stream = zip.CreateEntry("M-0001/" + ProgramId + ".xml").Open()) Modules().Save(stream);
        var vm = new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(directory, "storage-catalog")))); vm.CreateProject(Knxproj.Load(path), path);
        var devices = ProjectQuery.DeviceList(vm.Project!).ToArray(); var session = new ParameterEditingSession(vm); session.Load(new(devices[0]));
        var original = Knxproj.ToXml(vm.Project!).ToString();
        session.Rows.Single(r => r.Instance.Id == "R-count").Value = "3";
        var scalar = session.Rows.First(r => r.Instance.Id.EndsWith("_R-1") && !r.Instance.Id.Contains("_INNER")); var id = scalar.Instance.Id; scalar.Value = "9";
        session.Load(new(devices[1])); session.Load(new(devices[0]));
        check("a count draft restores against saved baseline storage, retaining values and new addresses", session.Rows.Single(r => r.Instance.Id == "R-count").Value == "3"
            && session.Rows.Single(r => r.Instance.Id == id).Value == "9" && session.Rows.Where(r => r.Instance.Id.Contains("_INNER"))
                .Select(r => r.Instance.Storage.StartAddress).SequenceEqual(new uint?[] { 4202, 4218, 4234 }) && Knxproj.ToXml(vm.Project!).ToString() == original);
        session.Load(new(devices[1]));
        var changed = Modules(); changed.Descendants("AbsoluteSegment").Single().SetAttributeValue("Address", "8192");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) { zip.GetEntry("M-0001/" + ProgramId + ".xml")!.Delete(); using var stream = zip.CreateEntry("M-0001/" + ProgramId + ".xml").Open(); changed.Save(stream); }
        var fresh = Knxproj.Load(path); var archive = typeof(Project).GetProperty("ArchiveState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        archive.SetValue(vm.Project, archive.GetValue(fresh));
        session.Load(new(devices[0]));
        check("changed application locations retire a stale draft and reload the fresh diagnosis", session.Rows.Single(r => r.Instance.Id == "R-count").Value == "2"
            && session.Rows.Single(r => r.Instance.Id == id).Value == "1" && session.Rows.Single(r => r.Instance.Id == id).Instance.Storage.StartAddress == 8295);
        return 0;
    }
}
