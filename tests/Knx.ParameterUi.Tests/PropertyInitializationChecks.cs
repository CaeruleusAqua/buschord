using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;

internal static class PropertyInitializationChecks
{
    private static readonly XNamespace Ns = PropertySourceChecks.Ns;
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private static readonly Type Initializer = typeof(Project).Assembly.GetType("Knx.NativePropertyInitializer", true)!;
    private static readonly Type Sources = typeof(Project).Assembly.GetType("Knx.RootPropertyWriterSources", true)!;
    private static readonly Type Context = typeof(Project).Assembly.GetType("Knx.NativeApplicationEvaluationContext", true)!;
    private static readonly Type View = typeof(Project).Assembly.GetType("Knx.NativeProcedureEvaluationContext", true)!;
    private static readonly LoadProcedureRequest Request = new("all", LoadProcedureAccess.Remote, LoadParts.Full);
    private static T Get<T>(object value, string name) => NativeActivationChecks.Fact<T>(value, name);
    private static object[] Items(object value, string name) => Get<IEnumerable>(value, name).Cast<object>().ToArray();
    private static XElement Program(XDocument app) => app.Descendants(Ns + "ApplicationProgram").Single();
    private static XElement Static(XDocument app) => Program(app).Element(Ns + "Static")!;
    private static XElement Write(ushort index = 3, ushort pid = 10, ushort start = 1, ushort count = 2)
        => new(Ns + "LdCtrlWriteProp", new XAttribute("ObjIdx", index), new XAttribute("PropId", pid),
            new XAttribute("StartElement", start), new XAttribute("Count", count), new XAttribute("Verify", false));
    internal static XDocument Application(bool conditional = false)
    {
        var app = PropertySourceChecks.App(); Program(app).SetAttributeValue("LoadProcedureStyle", "ProductProcedure");
        Program(app).SetAttributeValue("MinEtsVersion", "6.0");
        var storage = app.Descendants(Ns + "Property").Single(); storage.SetAttributeValue("Offset", 0); storage.SetAttributeValue("BitOffset", 0);
        Static(app).Add(new XElement(Ns + "Options", new XAttribute("ParameterByteOrder", "BigEndian"), new XAttribute("DownloadInvisibleParameters", "DefaultValue")));
        var controls = new XElement(Ns + "LoadProcedure", new XElement(Ns + "LdCtrlConnect"), Write(), new XElement(Ns + "LdCtrlDisconnect"));
        if (conditional)
        {
            controls.Element(Ns + "LdCtrlWriteProp")!.ReplaceWith(new XElement(Ns + "choose", new XAttribute("ParamRefId", "R"),
                new XElement(Ns + "when", new XAttribute("test", "0"), Write(count: 2)),
                new XElement(Ns + "when", new XAttribute("default", true), Write(count: 3))));
        }
        Static(app).Add(new XElement(Ns + "LoadProcedures", controls)); return app;
    }
    internal static XDocument Master(string? pdt = "PDT_UNSIGNED_INT", byte first = 5, ushort index = 3)
        => PropertySourceChecks.Master(pdt, first, index);
    internal static XDocument Hardware(byte[]? raw = null)
    {
        if (raw is not null) return PropertySourceChecks.Hardware(raw);
        return XDocument.Parse($$"""
            <KNX xmlns="{{Ns}}"><ManufacturerData><Manufacturer RefId="M-0001"><Hardware><Hardware Id="M-0001_H-image"><Hardware2Programs>
            <Hardware2Program Id="M-0001_H-image_A-0100-10-0000"><ApplicationProgramRef RefId="{{PropertySourceChecks.AppId}}"/></Hardware2Program>
            </Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
            """);
    }
    private sealed record Fixture(ArchivedImageSourceChecks.Fixture Source, object Inputs, object Prepared, object SourcePlan, object Application, object View);
    private static Fixture Load(string directory, XDocument? app = null, XDocument? master = null, byte[]? raw = null,
        string? saved = null, string[]? extraSeeds = null, XDocument? hardware = null)
    {
        var f = ArchivedImageSourceChecks.Load(directory, app: app ?? Application(), master: master ?? Master(), hardware: hardware ?? Hardware(raw));
        if (saved is not null) f.Device.ParameterValues["R"] = saved;
        var inputs = typeof(ArchivedImageSources).GetMethod("ReadInputs", Hidden)!.Invoke(null, [f.Project, f.Device])!;
        var sources = Sources.GetMethod("Read", Hidden)!.Invoke(null, [f.Project, f.Device, inputs])!;
        var prepared = typeof(LoadProcedureCompiler).GetMethod("PrepareResolved", Hidden)!.Invoke(null, [inputs, Request])!;
        var seeds = extraSeeds ?? Get<IReadOnlyList<string>>(prepared, "ControllerReferences").ToArray();
        var context = Context.GetMethod("Create", Hidden)!.Invoke(null, [f.Project, f.Device, inputs, seeds, null])!;
        var view = View.GetMethod("FromApplicationContext", Hidden)!.Invoke(null, [f.Project, f.Device, inputs, prepared, context])!;
        return new(f, inputs, prepared, sources, context, view);
    }
    private static object Compile(Fixture f, object? inputs = null, object? prepared = null, object? view = null, object? context = null, object? sources = null)
        => Initializer.GetMethod("Compile", Hidden)!.Invoke(null, [f.Source.Project, f.Source.Device, inputs ?? f.Inputs,
            prepared ?? f.Prepared, view ?? f.View, context ?? f.Application, sources ?? f.SourcePlan])!;
    private static bool Empty(object plan, PropertyInitializationIssueCode? code = null)
        => !Get<bool>(plan, "IsAvailable") && Items(plan, "Allocations").Length == 0 && Items(plan, "Counts").Length == 0
            && Get<LoadProcedurePlan?>(plan, "ActualProcedure") is null && Get<string?>(plan, "Fingerprint") is null
            && Get<string?>(plan, "AllocationHash") is null && Get<string?>(plan, "SourceClosureHash") is null
            && Get<string?>(plan, "CoverageHash") is null && Get<IReadOnlyList<PropertyInitializationIssue>>(plan, "Issues") is { Count: 1 } issues
            && (code is null || issues[0].Code == code);
    private static object Allocation(object plan) => Items(plan, "Allocations").Single();
    private static string Data(object plan) => Get<ImmutablePatchBytes>(Allocation(plan), "InitialData").ToHexString();
    private static string Validity(object plan) => Get<ImmutablePatchBytes>(Allocation(plan), "InitialValidity").ToHexString();
    private static void Set(object value, string name, object? replacement) => value.GetType().GetField("<" + name + ">k__BackingField", Any)!.SetValue(value, replacement);

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "PropertyInitialization " + name); if (!ok) failures++; }
        void Refused(string name, XDocument app, XDocument? master = null, byte[]? raw = null, PropertyInitializationIssueCode? code = null)
        { Check(name, Empty(Compile(Load(directory, app, master, raw)), code)); }
        var f = Load(directory); var xml = Knxproj.ToXml(f.Source.Project).ToString(SaveOptions.DisableFormatting);
        var archive = SHA256.HashData(File.ReadAllBytes(f.Source.Path)); var revision = f.Source.Project.ApplicationDataRevision;
        var undo = new UndoStack(); undo.Reset(f.Source.Project);
        var initial = Compile(f);
        Check("primitive procedure allocates four literal zero bytes and zero validity", Get<bool>(initial, "IsAvailable") && Data(initial) == "00000000" && Validity(initial) == "00000000"
            && Get<LoadProcedurePlan>(initial, "ActualProcedure").ProfileVersion == 1 && Get<int>(Allocation(initial), "ControlBytes") == 4);
        Check("PDT width and control count have distinct roles", Get<PropertyPdtLayout>(Allocation(initial), "PdtLayout") is
            { Status: PropertyPdtStatus.KnownSymmetric, ReadElementBytes: 2, WriteElementBytes: 2 }
            && Get<uint>(Allocation(initial), "ElementExtent") == 2 && Get<int>(Allocation(initial), "Length") == 4);
        Check("full common payload reserve includes all downstream copies", Get<long>(initial, "ReservedPayloadBytes") == 44);
        Check("source closure allocation coverage and aggregate fingerprints are complete", Get<string>(initial, "SourceClosureHash").Length == 64
            && Get<string>(initial, "AllocationHash").Length == 64 && Get<string>(initial, "CoverageHash").Length == 64 && Get<string>(initial, "Fingerprint").Length == 64);
        Check("preview preserves complete source model revision and undo", xml == Knxproj.ToXml(f.Source.Project).ToString(SaveOptions.DisableFormatting)
            && archive.SequenceEqual(SHA256.HashData(File.ReadAllBytes(f.Source.Path))) && revision == f.Source.Project.ApplicationDataRevision && undo.Steps == 0 && !undo.IsDirty);
        foreach (var mode in new[] { "ff", "null", "partial", "zero" })
        {
            var raw = PropertySourceChecks.Frame(data: [0x7F, 0x55], dataMask: mode == "ff" ? [255, 255] : mode == "partial" ? [255, 0] : [0, 0], omitDataMask: mode == "null");
            var plan = Compile(Load(directory, raw: raw));
            Check("raw source data grows with explicit zero validity independently of source mask " + mode,
                Get<bool>(plan, "IsAvailable") && Data(plan) == "7F550000" && Validity(plan) == "00000000"
                && Get<int?>(Allocation(plan), "FactoryDataLength") == 2);
            var count = Items(plan, "Counts").Single();
            Check("count is separate observed BE data and never adopted " + mode, Get<ImmutablePatchBytes>(count, "Data").ToHexString() == "0002"
                && Get<ushort?>(count, "EncodedCount") == 2 && Get<bool>(count, "NotAdopted") && Get<PropertyCountStatus>(count, "Status") == PropertyCountStatus.FullyValid);
        }
        var grow = Compile(Load(directory, raw: PropertySourceChecks.Frame(data: [1, 2, 3, 4, 5, 6])));
        Check("longer exact factory source grows allocation without count-derived sizing", Data(grow) == "010203040506" && Validity(grow) == "000000000000"
            && Get<int>(Allocation(grow), "ControlBytes") == 4 && Get<int>(Allocation(grow), "Length") == 6);
        var copy = Get<ImmutablePatchBytes>(Allocation(grow), "InitialData").ToArray(); copy[0] = 255;
        Check("copy-out data cannot mutate privately owned allocation", Data(grow) == "010203040506");
        var zero = Compile(Load(directory, raw: PropertySourceChecks.Frame(data: [], dataMask: [], omitCount: true)));
        Check("zero-length exact source remains distinguishable from absent factory", Get<bool>(zero, "IsAvailable") && Data(zero) == "00000000"
            && Get<int?>(Allocation(zero), "FactoryDataLength") == 0 && Get<string>(zero, "Fingerprint") != Get<string>(initial, "Fingerprint"));
        foreach (var (countData, mask, missingMask, expectedStatus) in new (byte[], byte[], bool, PropertyCountStatus)[]
        {
            ([0, 0], [255, 255], false, PropertyCountStatus.FullyValid),
            ([255, 255], [255, 0], false, PropertyCountStatus.MaskPartial),
            ([0, 7], [0, 0], true, PropertyCountStatus.MaskMissing),
        })
        {
            var plan = Compile(Load(directory, raw: PropertySourceChecks.Frame(data: [0x7F, 0x55], count: countData, countMask: mask, omitCountMask: missingMask)));
            Check("observed count and partial/absent validity never size working Data " + expectedStatus,
                Get<bool>(plan, "IsAvailable") && Data(plan) == "7F550000" && Validity(plan) == "00000000"
                && Get<PropertyCountStatus>(Items(plan, "Counts").Single(), "Status") == expectedStatus);
        }
        var emptyHardware = Hardware(); emptyHardware.Descendants(Ns + "Hardware2Program").Single().SetAttributeValue("LoadedImage", "");
        Check("declared empty factory is not silently an absent source", Empty(Compile(Load(directory, hardware: emptyHardware)), PropertyInitializationIssueCode.FactoryUnavailable));
        Check("malformed declared factory fails without partial buffers", Empty(Compile(Load(directory, raw: [1, 2, 3])), PropertyInitializationIssueCode.FactoryUnavailable));
        var decodedEmpty = Compile(Load(directory, raw: PropertySourceChecks.Container()));
        Check("decoded empty declared factory remains a distinct unsupported source", Empty(decodedEmpty, PropertyInitializationIssueCode.FactoryUnavailable)
            && Get<IReadOnlyList<PropertyInitializationIssue>>(decodedEmpty, "Issues").Single() is { FactoryStatus: ArchivedFactoryImageStatus.Decoded, FactoryBlobHash.Length: 64 });
        var appIndex = Application(); appIndex.Descendants(Ns + "Property").Single().SetAttributeValue("ObjectIndex", 6);
        appIndex.Descendants(Ns + "LdCtrlWriteProp").Single().SetAttributeValue("ObjIdx", 6);
        var appNative = Compile(Load(directory, appIndex, Master(index: 6), PropertySourceChecks.Frame(tag: 5, selector: 1, data: [0x7F, 0x55])));
        Check("FirstApp conversion chooses exact native application arena", Get<bool>(appNative, "IsAvailable") && Data(appNative) == "7F550000"
            && Get<PropertySourceSelector>(Allocation(appNative), "NativeSelector") is { Kind: PropertySourceKind.AppProperty, ApplicationObjectIndex: 1, NativeObjectIndex: null });
        Refused("canonical-only system alias is not an exact native factory seed", appIndex, Master(index: 6),
            PropertySourceChecks.Frame(tag: 4, selector: 6), PropertyInitializationIssueCode.AmbiguousFactorySource);
        var typed = Application(); var typedProperty = typed.Descendants(Ns + "Property").Single(); typedProperty.Attribute("ObjectIndex")!.Remove();
        typedProperty.SetAttributeValue("ObjectType", 123); typedProperty.SetAttributeValue("Occurrence", 7);
        var typedControl = typed.Descendants(Ns + "LdCtrlWriteProp").Single(); typedControl.Attribute("ObjIdx")!.Remove();
        typedControl.SetAttributeValue("ObjType", 123); typedControl.SetAttributeValue("Occurrence", 7);
        var typedPlan = Compile(Load(directory, typed, PropertySourceChecks.Master(type: 123), PropertySourceChecks.Frame(tag: 11, selector: 123, occurrence: 7, data: [0x12, 0x34])));
        Check("type occurrence stays separate and exact without an index guess", Get<bool>(typedPlan, "IsAvailable") && Data(typedPlan) == "12340000"
            && Get<PropertySourceSelector>(Allocation(typedPlan), "NativeSelector") is { Kind: PropertySourceKind.ByType, ObjectType: 123, Occurrence: 7 });
        var wideControl = Application(); wideControl.Descendants(Ns + "LdCtrlWriteProp").Single().Attribute("ObjIdx")!.Remove();
        wideControl.Descendants(Ns + "LdCtrlWriteProp").Single().SetAttributeValue("ObjType", 123); wideControl.Descendants(Ns + "LdCtrlWriteProp").Single().SetAttributeValue("Occurrence", 263);
        Refused("wide control occurrence never wraps into a raw byte", wideControl, code: PropertyInitializationIssueCode.UnsupportedSelector);
        Refused("offset factory fragments cannot manufacture a base-zero seed", Application(), raw: PropertySourceChecks.Frame(dataOffset: 2), code: PropertyInitializationIssueCode.AmbiguousFactorySource);
        Refused("disjoint same-target fragments still require a closed exact allocation source", Application(),
            raw: PropertySourceChecks.Container(PropertySourceChecks.Record(4, 0, 3, 10, [1], [255]), PropertySourceChecks.Record(4, 3, 3, 10, [2], [255])),
            code: PropertyInitializationIssueCode.AmbiguousFactorySource);
        var multiple = Application(); var procedure = multiple.Descendants(Ns + "LoadProcedure").Single(); procedure.Add(Write(start: 3, count: 2));
        var coalesced = Compile(Load(directory, multiple));
        Check("two distinct ranges use one native group maximum extent", Get<bool>(coalesced, "IsAvailable") && Data(coalesced) == "0000000000000000"
            && Items(Allocation(coalesced), "Controls").Length == 2 && Get<uint>(Allocation(coalesced), "ElementExtent") == 4);
        var duplicate = Application(); duplicate.Descendants(Ns + "LoadProcedure").Single().Add(Write());
        Refused("duplicate native grouping key fails atomically", duplicate, code: PropertyInitializationIssueCode.DuplicateControlGroup);
        var unrelated = Application(); unrelated.Descendants(Ns + "LoadProcedure").Single().Add(Write(pid: 11, count: 1));
        var otherMaster = Master(); otherMaster.Descendants(Ns + "InterfaceObject").Single().Add(new XElement(Ns + "Property", new XAttribute("PropertyID", 11), new XAttribute("PropertyDataType", "PDT_UNSIGNED_CHAR")));
        var other = Compile(Load(directory, unrelated, otherMaster));
        Check("unrelated actual WriteProp allocation cannot disappear", Get<bool>(other, "IsAvailable") && Items(other, "Allocations").Length == 2
            && Items(other, "Allocations").Select(a => Get<int>(a, "Length")).SequenceEqual(new[] { 4, 1 }));
        Refused("unknown PDT on unrelated control is not a one-byte fallback", unrelated, code: PropertyInitializationIssueCode.UnsupportedPdt);
        var asymmetricMaster = new XDocument(otherMaster); asymmetricMaster.Descendants(Ns + "Property").Last().SetAttributeValue("PropertyDataType", "PDT_CONTROL");
        Refused("read/write width asymmetry refuses unrelated allocation", unrelated, asymmetricMaster, code: PropertyInitializationIssueCode.UnsupportedPdt);
        foreach (var maximum in new[] { 128, 129 })
        {
            var app = Application(); var controls = app.Descendants(Ns + "LoadProcedure").Single(); controls.ReplaceNodes();
            var master = Master("PDT_UNSIGNED_CHAR"); var obj = master.Descendants(Ns + "InterfaceObject").Single(); obj.ReplaceNodes();
            foreach (var pid in Enumerable.Range(10, maximum + 1).Where(pid => pid != 27).Take(maximum).Select(pid => (ushort)pid))
            { controls.Add(Write(pid: pid, count: 1)); obj.Add(new XElement(Ns + "Property", new XAttribute("PropertyID", pid), new XAttribute("PropertyDataType", "PDT_UNSIGNED_CHAR"))); }
            var result = Compile(Load(directory, app, master));
            Check("exact allocation count boundary " + maximum, maximum == 128 ? Get<bool>(result, "IsAvailable") && Items(result, "Allocations").Length == 128
                : Empty(result, PropertyInitializationIssueCode.LimitExceeded));
        }
        var totalApp = Application(); var totalControls = totalApp.Descendants(Ns + "LoadProcedure").Single(); totalControls.ReplaceNodes();
        var totalMaster = Master("PDT_GENERIC_20"); var totalObject = totalMaster.Descendants(Ns + "InterfaceObject").Single(); totalObject.ReplaceNodes();
        for (ushort pid = 10; pid < 14; pid++)
        { totalControls.Add(Write(pid: pid, count: 20000)); totalObject.Add(new XElement(Ns + "Property", new XAttribute("PropertyID", pid), new XAttribute("PropertyDataType", "PDT_GENERIC_20"))); }
        Refused("joint downstream copy reserve is checked before buffer allocation", totalApp, totalMaster, code: PropertyInitializationIssueCode.LimitExceeded);
        foreach (var (start, count) in new (ushort, ushort)[] { (0, 2), (1, 0), (65535, 65535) })
        {
            var invalid = Application(); invalid.Descendants(Ns + "LdCtrlWriteProp").Single().ReplaceWith(Write(start: start, count: count));
            Refused("invalid or oversized control extent " + start + "/" + count, invalid, start == 65535 ? Master("PDT_GENERIC_20") : null,
                code: start == 0 || count == 0 ? PropertyInitializationIssueCode.UnsupportedControl : null);
        }
        var pid27 = Application(); pid27.Descendants(Ns + "LdCtrlWriteProp").Single().SetAttributeValue("PropId", 27);
        Refused("PID27 special allocation is outside this profile", pid27, code: PropertyInitializationIssueCode.UnsupportedControl);
        var missing = Application(); missing.Descendants(Ns + "LdCtrlWriteProp").Single().Remove();
        Refused("all physical records need real actual preallocation", missing, code: PropertyInitializationIssueCode.MissingAllocation);
        var latent = Application(); Static(latent).Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "latent"), new XAttribute("ParameterType", "T"), new XAttribute("Value", 0),
            new XElement(Ns + "Property", new XAttribute("ObjectIndex", 3), new XAttribute("PropertyId", 10), new XAttribute("Offset", 4), new XAttribute("BitOffset", 0))));
        Refused("latent unreferenced physical owner needs complete allocation coverage", latent, code: PropertyInitializationIssueCode.UncoveredOwner);
        var invisible = Application(); invisible.Descendants(Ns + "ParameterRefRef").Single().Remove(); invisible.Descendants(Ns + "LdCtrlWriteProp").Single().Remove();
        Refused("inactive null record cannot hide missing allocation", invisible, code: PropertyInitializationIssueCode.MissingAllocation);
        var noStorage = Application(); noStorage.Descendants(Ns + "Property").Single().Remove(); noStorage.Descendants(Ns + "LdCtrlWriteProp").Single().Remove();
        var noStoragePlan = Compile(Load(directory, noStorage));
        Check("established NoStorage needs no manufactured allocation", Get<bool>(noStoragePlan, "IsAvailable") && Items(noStoragePlan, "Allocations").Length == 0
            && Get<long>(noStoragePlan, "ReservedPayloadBytes") == 0);
        var memoryControl = Application(); memoryControl.Descendants(Ns + "LoadProcedure").Single().Add(new XElement(Ns + "LdCtrlRelSegment",
            new XAttribute("LsmIdx", 1), new XAttribute("Size", 4), new XAttribute("Mode", 0), new XAttribute("Fill", 0)));
        Refused("selected relative memory allocation has no silent property-only interpretation", memoryControl, code: PropertyInitializationIssueCode.UnsupportedControl);
        var loadImage = Application(); loadImage.Descendants(Ns + "LoadProcedure").Single().Add(new XElement(Ns + "LdCtrlLoadImageProp",
            new XAttribute("ObjIdx", 3), new XAttribute("PropId", 27)));
        Refused("special LoadImageProperty allocation cannot escape source closure", loadImage, code: PropertyInitializationIssueCode.UnsupportedControl);
        foreach (var name in new[] { "GroupAddressTablePtr", "GroupAssociationTablePtr", "GroupObjectTablePtr", "ApplicationDataPtr", "PeiprogDataPtr" })
        {
            var master = Master(); var set = master.Descendants(Ns + "HawkConfigurationData").Single();
            XElement Candidate(string style, int length) => new(Ns + "Resource", new XAttribute("Name", name), new XAttribute("Access", "remote"), new XAttribute("MgmtStyle", style),
                new XElement(Ns + "Location", new XAttribute("AddressSpace", "SystemProperty"), new XAttribute("StartAddress", 0), new XAttribute("InterfaceObjectRef", 3), new XAttribute("PropertyID", 10)),
                new XElement(Ns + "ResourceType", new XAttribute("Length", length)));
            set.Add(new XElement(Ns + "Resources", Candidate("simple", 2), Candidate("lsm", 4)));
            Refused("all five possible pointer contributors must be closed " + name, Application(), master, code: PropertyInitializationIssueCode.UnsupportedInitialSource);
        }
        var conditional = Load(directory, Application(true)); var selected = Compile(conditional);
        Check("fresh conditional procedure selects the real allocation size", Get<bool>(selected, "IsAvailable") && Data(selected) == "00000000"
            && Get<LoadProcedurePlan>(selected, "ActualProcedure").ProfileVersion == 2 && Get<LoadProcedurePlan>(selected, "ActualProcedure").ConditionalSelection!.Decisions.Single().SelectedWhenOrdinals.Count == 1);
        var fallback = Compile(Load(directory, Application(true), saved: "1"));
        Check("sole default selects different actual allocation and fingerprint", Get<bool>(fallback, "IsAvailable") && Data(fallback) == "000000000000"
            && Get<LoadProcedurePlan>(fallback, "ActualProcedure").ConditionalSelection!.Decisions.Single().UsedDefault
            && Get<string>(selected, "Fingerprint") != Get<string>(fallback, "Fingerprint"));
        conditional.Source.Device.ParameterValues["R"] = "1";
        Check("saved changes stale the sealed context before allocation", Empty(Compile(conditional), PropertyInitializationIssueCode.InputsChanged));
        var foreign = Load(directory);
        Check("foreign Inputs cannot pair by matching IDs and source hashes", Empty(Compile(f, inputs: foreign.Inputs), PropertyInitializationIssueCode.ContextMismatch));
        Check("foreign source capability cannot pair by matching source bytes", Empty(Compile(f, sources: foreign.SourcePlan), PropertyInitializationIssueCode.ContextMismatch));
        Check("foreign context or shared view cannot become authority", Empty(Compile(f, view: foreign.View)) && Empty(Compile(f, context: foreign.Application)));
        var extra = Load(directory, extraSeeds: ["R"]);
        Check("undeclared additional evaluation seeds are refused", Empty(Compile(extra)));
        var carrier = Load(directory); var originalCarrier = Get<XDocument>(carrier.Source.Project, "OriginalXml");
        var nativeDevice = originalCarrier.Descendants().Single(e => e.Name.LocalName == "DeviceInstance" && (string?)e.Attribute("Id") == carrier.Source.Device.Id);
        nativeDevice.Add(new XElement(nativeDevice.Name.Namespace + "ParameterInstances"));
        Check("post-capture native carrier refusal is controlled and atomic", Empty(Compile(carrier), PropertyInitializationIssueCode.InputsChanged));
        var sourceDocument = Get<XDocument>(f.Inputs, "Application"); sourceDocument.Descendants(Ns + "Parameter").Single().SetAttributeValue("Value", 1);
        Check("in-memory source mutation invalidates the paired plan", Empty(Compile(f), PropertyInitializationIssueCode.InputsChanged));
        var forged = Load(directory); Set(forged.Application, "ScriptCapabilityUsed", true);
        Check("a runtime capability cannot enter the source-only initializer", Empty(Compile(forged), PropertyInitializationIssueCode.ContextMismatch));
        Console.WriteLine($"PropertyInitialization: {checks} checks, {failures} failures"); return failures;
    }
}
