using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Knx;
using Knx.App.Services;
using Knx.Catalog;

internal static class ParameterFootprintChecks
{
    private static XDocument Document(string type = "<TypeNumber Type='unsignedInt' SizeInBit='8'/>", string location = "<Memory CodeSegment='AS' Offset='0' BitOffset='0'/>", string? size = "16")
    {
        var doc = XDocument.Parse($$"""
            <KNX><ApplicationProgram Id="{{ParameterStorageChecks.ProgramId}}"><Static><Code>
            <AbsoluteSegment Id="AS" Address="4096" Size="16"/><RelativeSegment Id="RS" LoadStateMachine="4" Offset="32" Size="16"/>
            </Code><ParameterTypes><ParameterType Id="T">{{type}}</ParameterType></ParameterTypes><Parameters>
            <Parameter Id="P" ParameterType="T" Value="1">{{location}}</Parameter></Parameters><ParameterRefs><ParameterRef Id="R" RefId="P"/></ParameterRefs>
            </Static><Dynamic><ParameterRefRef RefId="R"/></Dynamic></ApplicationProgram></KNX>
            """);
        doc.Descendants("AbsoluteSegment").Single().SetAttributeValue("Size", size);
        return doc;
    }
    private static ParameterInstance One(string type = "<TypeNumber Type='unsignedInt' SizeInBit='8'/>", string location = "<Memory CodeSegment='AS' Offset='0' BitOffset='0'/>", string? size = "16") =>
        Ksxml.Parse(Document(type, location, size)).Instances.Single();

    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "Footprint " + name); if (!ok) failures++; }
        var row = One(); var storage = row.Storage;
        Check("resource descriptor retains declared geometry rather than a write range", storage.Resource is
            { Id: "AS", Kind: ParameterStorageKind.AbsoluteMemory, StartAddress: 4096, DeclaredSize: 16, LoadStateMachine: null }
            && storage.Footprint is { Status: ParameterFootprintStatus.Known, BitLength: 8, ByteLength: 1, Coverage: ParameterResourceCoverage.Covered });
        Check("an exact byte boundary is covered", One(location: "<Memory CodeSegment='AS' Offset='15' BitOffset='0'/>").Storage.Footprint is
            { ByteLength: 1, Coverage: ParameterResourceCoverage.Covered });
        var uncovered = One(location: "<Memory CodeSegment='AS' Offset='15' BitOffset='1'/>");
        Check("bit displacement includes the second spanned byte", uncovered.Storage.Footprint is
            { BitLength: 8, ByteLength: 2, Coverage: ParameterResourceCoverage.Uncovered });
        Check("uncovered geometry neither disables values nor invalidates the application", uncovered.IsEnabled && uncovered.UnsupportedReason is null
            && ParameterValidation.TryValidate(uncovered, "2", out _) && ApplicationSnapshot.Evaluate(Document(location: "<Memory CodeSegment='AS' Offset='15' BitOffset='1'/>"), new Dictionary<string, string?>()).IsValid);
        Check("a packed twelve-bit type fits exactly across two bytes", One("<TypeNumber Type='unsignedInt' SizeInBit='12'/>", "<Memory CodeSegment='AS' Offset='0' BitOffset='4'/>", "2").Storage.Footprint is
            { BitLength: 12, ByteLength: 2, Coverage: ParameterResourceCoverage.Covered });
        Check("one additional bit can make a third byte uncovered", One("<TypeNumber Type='unsignedInt' SizeInBit='12'/>", "<Memory CodeSegment='AS' Offset='0' BitOffset='5'/>", "2").Storage.Footprint is
            { ByteLength: 3, Coverage: ParameterResourceCoverage.Uncovered });
        Check("explicit zero resource size is not an unknown size", One(size: "0").Storage is
            { Resource.DeclaredSize: 0, Footprint.Coverage: ParameterResourceCoverage.Uncovered });
        Check("missing and malformed sizes remain unknown without altering coordinates", new string?[] { null, "bad", "-1", "1.5", "1048576", "4294967296" }
            .Select(s => One(size: s)).All(i => i.Storage is { Status: ParameterStorageStatus.Supported, StartAddress: 4096, Resource.DeclaredSize: null,
                Footprint.Status: ParameterFootprintStatus.Known, Footprint.Coverage: ParameterResourceCoverage.Unknown }));
        Check("relative resources retain their own base and load-state-machine arena", One(location: "<Memory CodeSegment='RS' Offset='15' BitOffset='1'/>").Storage is
            { Resource: { Id: "RS", Kind: ParameterStorageKind.RelativeMemory, StartAddress: 32, DeclaredSize: 16, LoadStateMachine: 4 },
                Footprint.Coverage: ParameterResourceCoverage.Uncovered });
        Check("property type width does not invent a resource capacity", One(location: "<Property ObjectIndex='3' PropertyId='10' Offset='7' BitOffset='1'/>").Storage is
            { Resource: null, Footprint: { Status: ParameterFootprintStatus.Known, BitLength: 8, ByteLength: 2, Coverage: ParameterResourceCoverage.Unknown } });
        Check("type and occurrence properties also keep coverage unknown", One(location: "<Property ObjectType='123' Occurrence='2' PropertyId='10' Offset='7' BitOffset='1'/>").Storage is
            { Resource: null, Footprint.Coverage: ParameterResourceCoverage.Unknown });
        Check("enumeration bit width is independent of the selected label", One("<TypeRestriction SizeInBit='3'><Enumeration Value='1' Text='One'/></TypeRestriction>", "<Memory CodeSegment='AS' Offset='0' BitOffset='6'/>").Storage.Footprint is
            { Status: ParameterFootprintStatus.Known, BitLength: 3, ByteLength: 2 });
        var boolean = Document(); var booleanType = boolean.Descendants("ParameterType").Single(); booleanType.RemoveNodes();
        booleanType.SetAttributeValue("DataType", "Boolean"); booleanType.SetAttributeValue("SizeInBit", "1");
        Check("an explicitly one-bit Boolean has a fixed type width", Ksxml.Parse(boolean).Instances.Single().Storage.Footprint is { Status: ParameterFootprintStatus.Known, BitLength: 1, ByteLength: 1 });
        booleanType.SetAttributeValue("SizeInBit", "2");
        Check("a Boolean display kind is not evidence for another bit width", Ksxml.Parse(boolean).Instances.Single().Storage.Footprint?.Status == ParameterFootprintStatus.Unknown);
        Check("Number33 and Number64 are outside the native width profile", new[] { 33, 64 }.All(w =>
            One($"<TypeNumber Type='unsignedInt' SizeInBit='{w}'/>").Storage.Footprint is { Status: ParameterFootprintStatus.OutOfRange, BitLength: null, ByteLength: null }));
        Check("Enum Time and Float retain separate 64-bit diagnostic profiles", One("<TypeRestriction SizeInBit='64'><Enumeration Value='1' Text='One'/></TypeRestriction>").Storage.Footprint is { Status: ParameterFootprintStatus.Known, BitLength: 64 }
            && One("<TypeTime Unit='Seconds' SizeInBit='64' minInclusive='0' maxInclusive='1000'/>").Storage.Footprint is { Status: ParameterFootprintStatus.Known, BitLength: 64 }
            && One("<TypeFloat Encoding='IEEE-754 Double'/>").Storage.Footprint is { Status: ParameterFootprintStatus.Known, BitLength: 64 });
        Check("unsupported integer diagnostic widths are distinct from absent widths", One("<TypeNumber Type='unsignedInt' SizeInBit='65'/>").Storage.Footprint is
            { Status: ParameterFootprintStatus.OutOfRange, BitLength: null, ByteLength: null, Coverage: ParameterResourceCoverage.Unknown }
            && One("<TypeNumber Type='unsignedInt'/>").Storage.Footprint?.Status == ParameterFootprintStatus.Unknown);
        Check("an unknown number encoding cannot establish a formatted width", One("<TypeNumber Type='vendor' SizeInBit='8'/>").Storage.Footprint?.Status == ParameterFootprintStatus.Unknown);
        // Fixed type widths are distinct from capacities and display units.
        Check("known float encodings establish only their exact fixed type widths", new[] { ("DPT 9", 16u, 3u), ("IEEE-754 Single", 32u, 5u), ("IEEE-754 Double", 64u, 9u) }
            .All(x => One($"<TypeFloat Encoding='{x.Item1}'/>", "<Memory CodeSegment='AS' Offset='0' BitOffset='1'/>").Storage.Footprint is { Status: ParameterFootprintStatus.Known } f
                && f.BitLength == x.Item2 && f.ByteLength == x.Item3));
        Check("an unverified float encoding stays unknown even with a numeric capacity", One("<TypeFloat Encoding='vendor' SizeInBit='32'/>").Storage.Footprint?.Status == ParameterFootprintStatus.Unknown);
        Check("valid Time uses its declared fixed bits, not the duration UI unit", One("<TypeTime Unit='Seconds' SizeInBit='16' minInclusive='0' maxInclusive='1000' UIHint='Duration_hhmmss'/>").Storage.Footprint is
            { Status: ParameterFootprintStatus.Known, BitLength: 16, ByteLength: 2 }
            && One("<TypeTime Unit='unknown' SizeInBit='16' minInclusive='0' maxInclusive='1000'/>").Storage.Footprint?.Status == ParameterFootprintStatus.Unknown);
        Check("the explicit IP family determines fixed bits without guessing another family", One("<TypeIPAddress AddressType='HostAddress' Version='IPv4'/>").Storage.Footprint is { BitLength: 32, ByteLength: 4 }
            && One("<TypeIPAddress AddressType='HostAddress' Version='IPv6'/>").Storage.Footprint is { BitLength: 128, ByteLength: 16 }
            && One("<TypeIPAddress AddressType='HostAddress' Version='unknown'/>").Storage.Footprint?.Status == ParameterFootprintStatus.Unknown);
        Check("raw capacity, text encoding, color and unknown forms remain unknown", new[] { "<TypeRawData MaxSize='128'/>", "<TypeText SizeInBit='128'/>",
            "<TypeColor Space='RGB'/>", "<TypeNone/>" }.All(t => One(t).Storage.Footprint is { Status: ParameterFootprintStatus.Unknown, BitLength: null, ByteLength: null, Coverage: ParameterResourceCoverage.Unknown }));
        Check("unknown locations and undeclared storage do not gain a footprint from the type", One(location: "<Memory CodeSegment='missing' Offset='0' BitOffset='0'/>").Storage.Footprint?.Status == ParameterFootprintStatus.Unknown
            && One(location: "").Storage is { Status: ParameterStorageStatus.NotDeclared, Resource: null, Footprint: null });
        var copy = new ParameterInstance(row);
        Check("copies retain nested immutable resource and footprint facts", ReferenceEquals(copy.Storage.Resource, storage.Resource)
            && ReferenceEquals(copy.Storage.Footprint, storage.Footprint) && storage with { Resource = storage.Resource! with { DeclaredSize = 17 } } != storage);
        var equal = typeof(ParameterEditingSession).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == "SameDeclaration" && m.GetParameters()[0].ParameterType == typeof(ParameterInstance));
        Check("a size-only resource change participates in declaration equality", !(bool)equal.Invoke(null, [row, One(size: "17"), false])!
            && (bool)equal.Invoke(null, [row, new ParameterInstance(row), false])!);
        var modules = ParameterStorageChecks.Modules(); var original = modules.ToString(SaveOptions.DisableFormatting);
        var snapshot = ApplicationSnapshot.Evaluate(modules, new Dictionary<string, string?>());
        Check("repeated and nested scopes retain resource identity and their own geometry", snapshot.IsValid && snapshot.Instances.Where(i => i.Id != "R-count")
            .All(i => i.Storage.Resource is not null && i.Storage.Footprint is { Status: ParameterFootprintStatus.Known, Coverage: ParameterResourceCoverage.Covered })
            && modules.ToString(SaveOptions.DisableFormatting) == original);
        var limit = new XDocument(modules); var argument = limit.Descendants("NumericArg").Last(); argument.Attribute("AllocatorRefId")!.Remove();
        argument.SetAttributeValue("Value", (uint.MaxValue - 4096 - 3).ToString());
        var far = Ksxml.Parse(limit).Instances.First(i => i.Id.EndsWith("_R-1") && !i.Id.Contains("_INNER"));
        Check("one-past-end calculations cannot wrap back into the resource", far.Storage is { StartAddress: uint.MaxValue,
            Footprint: { Status: ParameterFootprintStatus.Known, ByteLength: 2, Coverage: ParameterResourceCoverage.Uncovered } });
        DraftSizeCheck(directory, Check);
        return failures;
    }

    private static void DraftSizeCheck(string directory, Action<string, bool> check)
    {
        var path = Path.Combine(directory, "footprint-size-draft.knxproj");
        var project = ProjectWizard.Create(new() { Name = "Size-only declaration draft", Areas = 1, LinesPerArea = 1 });
        for (var n = 1; n <= 2; n++) project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "FD" + n, ProductRefId = ParameterStorageChecks.ProgramId, Address = new(1, 1, (byte)n) });
        Knxproj.Save(project, path);
        void WriteApplication(string size)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update); var name = "M-0001/" + ParameterStorageChecks.ProgramId + ".xml";
            zip.GetEntry(name)?.Delete(); using var stream = zip.CreateEntry(name).Open(); Document(size: size).Save(stream);
        }
        WriteApplication("16");
        var vm = new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(directory, "footprint-catalog")))); vm.CreateProject(Knxproj.Load(path), path);
        var devices = ProjectQuery.DeviceList(vm.Project!).ToArray(); var session = new ParameterEditingSession(vm); session.Load(new(devices[0]));
        session.Rows.Single().Value = "9"; session.Load(new(devices[1])); session.Load(new(devices[0]));
        check("unchanged resource geometry preserves an ordinary unsaved draft", session.Rows.Single().Value == "9" && session.Rows.Single().Instance.Storage.Resource?.DeclaredSize == 16);
        session.Load(new(devices[1])); WriteApplication("17"); var fresh = Knxproj.Load(path);
        var state = typeof(Project).GetProperty("ArchiveState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        state.SetValue(vm.Project, state.GetValue(fresh)); session.Load(new(devices[0]));
        check("a changed size reloads resource facts and retires the stale value draft", session.Rows.Single().Value == "1"
            && session.Rows.Single().Instance.Storage.Resource?.DeclaredSize == 17 && session.Rows.Single().Instance.Storage.StartAddress == 4096);
    }
}
