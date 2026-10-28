using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;

internal static class NativeGeneralPopulationChecks
{
    private const BindingFlags StaticHidden = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly XNamespace Ns = PropertySourceChecks.Ns;
    private static readonly Assembly Core = typeof(Project).Assembly;
    private static Type Type(string name) => Core.GetType("Knx." + name, true)!;
    private static object Invoke(string type, string method, params object?[] args)
        => Type(type).GetMethod(method, StaticHidden)!.Invoke(null, args)!;
    private static T Get<T>(object value, string name) => (T)value.GetType().GetProperty(name, Any)!.GetValue(value)!;
    private static object[] Items(object value, string name) => ((IEnumerable)Get<object>(value, name)).Cast<object>().ToArray();
    private static bool Available(object value) => Get<bool>(value, "IsAvailable");
    private static bool Issue(object value, string code) => !Available(value) && Items(value, "Issues").Any(i => Get<object>(i, "Code").ToString() == code);
    internal static XElement Program(XDocument app) => app.Descendants(Ns + "ApplicationProgram").Single();
    internal static XElement Stat(XDocument app) => Program(app).Element(Ns + "Static")!;
    internal static XDocument App(int memory = 128, int bits = 32, string behavior = "DefaultValue")
    {
        var app = ArchivedImageSourceChecks.App(); var stat = Stat(app); var program = Program(app);
        program.SetAttributeValue("MinEtsVersion", "6.0"); program.SetAttributeValue("LoadProcedureStyle", "ProductProcedure");
        stat.AddFirst(new XElement(Ns + "Options", new XAttribute("ParameterByteOrder", "BigEndian"), new XAttribute("DownloadInvisibleParameters", behavior)));
        var number = stat.Descendants(Ns + "TypeNumber").Single(); number.SetAttributeValue("SizeInBit", bits); number.SetAttributeValue("maxInclusive", (1L << bits) - 1);
        var width = (bits + 7) / 8;
        stat.Add(new XElement(Ns + "Code", new XElement(Ns + "AbsoluteSegment", new XAttribute("Id", "S"), new XAttribute("Address", 100), new XAttribute("Size", memory * width),
            new XElement(Ns + "Data", "pQ=="), new XElement(Ns + "Mask", "8A=="))));
        var parameters = stat.Element(Ns + "Parameters")!; var refs = stat.Element(Ns + "ParameterRefs")!; parameters.RemoveNodes(); refs.RemoveNodes();
        var dynamic = program.Element(Ns + "Dynamic")!; dynamic.RemoveNodes();
        for (var i = 0; i < memory + 2; i++)
        {
            var property = i >= memory;
            parameters.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "P" + i), new XAttribute("ParameterType", "T"),
                new XAttribute("Value", bits == 32 ? i + 1 : i % (1 << bits)), property
                    ? new XElement(Ns + "Property", new XAttribute("ObjectIndex", 3), new XAttribute("PropertyId", 10), new XAttribute("Offset", (i - memory) * width), new XAttribute("BitOffset", 0))
                    : new XElement(Ns + "Memory", new XAttribute("CodeSegment", "S"), new XAttribute("Offset", i * width), new XAttribute("BitOffset", 0))));
            refs.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "R" + i), new XAttribute("RefId", "P" + i)));
            dynamic.Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R" + i)));
        }
        parameters.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "NS"), new XAttribute("ParameterType", "T"), new XAttribute("Value", 1)));
        refs.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "RNS"), new XAttribute("RefId", "NS")));
        stat.Add(new XElement(Ns + "LoadProcedures", new XElement(Ns + "LoadProcedure", new XElement(Ns + "LdCtrlConnect"),
            new XElement(Ns + "LdCtrlWriteProp", new XAttribute("ObjIdx", 3), new XAttribute("PropId", 10), new XAttribute("StartElement", 1), new XAttribute("Count", 2), new XAttribute("Verify", false)),
            new XElement(Ns + "LdCtrlDisconnect"))));
        return app;
    }
    private sealed record Capture(ArchivedImageSourceChecks.Fixture Fixture, object Inputs, object Inventory, object Preparation, object Context, object Population,
        string ModelBefore, byte[] ArchiveBefore, long RevisionBefore);
    private static object Inputs(ArchivedImageSourceChecks.Fixture fixture) => Invoke("ArchivedImageSources", "ReadInputs", fixture.Project, fixture.Device);
    private static object Inventory(ArchivedImageSourceChecks.Fixture fixture, object inputs) => Invoke("NativeRootImageInventory", "Read", fixture.Project, fixture.Device, inputs);
    private static object Preparation(ArchivedImageSourceChecks.Fixture fixture, object inputs, object inventory, uint[] words)
        => Invoke("NativeImagePreparation", "Prepare", fixture.Project, fixture.Device, inputs, inventory, words);
    private static Capture Load(string directory, XDocument? app = null, uint[]? words = null, Action<ArchivedImageSourceChecks.Fixture>? edit = null, XDocument? master = null)
    {
        var fixture = ArchivedImageSourceChecks.Load(directory, hardware: PropertyInitializationChecks.Hardware(), app: app ?? App(), master: master ?? PropertySourceChecks.Master("PDT_GENERIC_04"));
        edit?.Invoke(fixture); var modelBefore = Knxproj.ToXml(fixture.Project).ToString(); var archiveBefore = SHA256.HashData(File.ReadAllBytes(fixture.Path));
        var revisionBefore = fixture.Project.ApplicationDataRevision;
        var inputs = Inputs(fixture); var inventory = Inventory(fixture, inputs);
        var preparation = Preparation(fixture, inputs, inventory, words ?? [1, 1025, 128, 1152]);
        var refs = Items(preparation, "ControllerReferences").Cast<string>().ToArray();
        var context = Invoke("NativeApplicationEvaluationContext", "Create", fixture.Project, fixture.Device, inputs, refs, null);
        var population = Invoke("NativeRootWriterPopulation", "Create", inventory, preparation, context);
        return new(fixture, inputs, inventory, preparation, context, population, modelBefore, archiveBefore, revisionBefore);
    }
    private static object Variant(Capture capture, uint word)
    {
        var variant = Items(capture.Preparation, "Variants").Single(v => Get<uint>(v, "RawOptions") == word);
        return capture.Population.GetType().GetMethod("ForVariant", Any)!.Invoke(capture.Population, [variant])!;
    }
    private static object Record(object population, string id) => Items(population, "Records").Single(r => Get<string>(r, "RefId") == id);
    private static bool Current(object inventory) => (bool)inventory.GetType().GetMethods(Any).Single(m => m.Name == "MatchesCurrent" && m.GetParameters().Length == 0).Invoke(inventory, null)!;
    private static bool Empty(object inventory, string code) => Issue(inventory, code) && Items(inventory, "References").Length == 0
        && Items(inventory, "Owners").Length == 0 && Items(inventory, "CodeSources").Length == 0 && Get<string?>(inventory, "SourceHash") is null;
    private static object ReadChanged(string directory, Action<XDocument> change)
    {
        var app = App(2); change(app);
        var fixture = ArchivedImageSourceChecks.Load(directory, hardware: PropertyInitializationChecks.Hardware(), app: app, master: PropertySourceChecks.Master("PDT_GENERIC_04"));
        return Inventory(fixture, Inputs(fixture));
    }

    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "NativeGeneralPopulation " + name); if (!ok) failures++; }
        var full = Load(directory); var before = full.ModelBefore; var bytes = full.ArchiveBefore;
        Check("one mixed inventory covers 131 refs and 520 physical bytes without a subset", Available(full.Inventory) && Items(full.Inventory, "References").Length == 131
            && Items(full.Inventory, "Owners").Length == 130 && Items(full.Inventory, "Owners").Sum(o => (long)Get<uint>(o, "BitLength")) == 4160);
        Check("source code retains raw A5 and F0 rather than canonical A0", Available(full.Inventory) && ((ImageCompositionSourceSegment)Items(full.Inventory, "CodeSources").Single()).RawData!.ToHexString() == "A5"
            && Get<long>(full.Inventory, "CodeSourcePayloadBytes") == 2 && Get<long>(full.Inventory, "PeakPayloadBytes") == 4);
        Check("hardware primary proof and no-factory status remain source facts", Available(full.Inventory) && Get<bool>(full.Inventory, "PrimaryProgramOnly")
            && !Get<bool>(full.Inventory, "FactoryDeclared") && Get<ArchivedFactoryImageSource>(full.Inventory, "FactorySources").Status == ArchivedFactoryImageStatus.Missing);
        Check("neutral source policy exposes no fixed IncludeAll option getter", Available(full.Inventory)
            && Get<object>(full.Inventory, "Policy").GetType().GetProperty("CustomerPolicy", Any) is null);
        Check("one ordinary application context supports the complete mixed population", Available(full.Context) && Available(full.Population));
        if (Available(full.Population))
        {
            var included = Variant(full, 1); var skipped = Variant(full, 128);
            Check("scheduled population retains NoStorage separately from physical writers", Available(included) && Items(included, "Records").Length == 131
                && Items(included, "Claims").Length == 130 && Get<RootParameterDisposition>(Record(included, "RNS"), "Disposition") == RootParameterDisposition.NoStorage);
            Check("NoStorage has no native emitted record or allocation", !Get<bool>(Record(included, "RNS"), "NativeRecordEmitted") && !Get<bool>(Record(included, "RNS"), "Allocates"));
            Check("task-skipped is empty while inventory remains complete", Available(skipped) && Items(skipped, "Records").Length == 0 && Items(skipped, "Claims").Length == 0
                && Items(skipped, "AllocationRequirements").Length == 0 && Get<object>(skipped, "ParameterTaskDisposition").ToString() == "NotScheduled");
            Check("claims retain the source word without copying any backing buffers", Get<uint?>(Get<object>(Record(included, "R127"), "Claim"), "EncodedWord") == 128);
            var excludedSkip = Variant(full, 1152);
            Check("skipped task preserves customer assumption and full-word identity", Available(excludedSkip) && Items(excludedSkip, "Records").Length == 0
                && Get<object>(excludedSkip, "CustomerPolicy").ToString() == "ExcludeAdjustable" && Get<string>(excludedSkip, "Fingerprint") != Get<string>(skipped, "Fingerprint"));
        }
        var archiveAfter = SHA256.HashData(File.ReadAllBytes(full.Fixture.Path));
        Check("source and population creation preserve exact project and archive", before == Knxproj.ToXml(full.Fixture.Project).ToString()
            && bytes.SequenceEqual(archiveAfter) && full.RevisionBefore == full.Fixture.Project.ApplicationDataRevision);
        var policyApp = App(2); Stat(policyApp).Element(Ns + "Parameters")!.Elements().First().SetAttributeValue("CustomerAdjustable", true);
        var customer = Load(directory, policyApp);
        if (Available(customer.Population))
        {
            var excluded = Variant(customer, 1025); var record = Record(excluded, "R0"); var claim = Get<object>(record, "Claim");
            Check("inherited adjustable nonnull record clears validity without writing Data", Available(excluded) && Get<bool>(record, "CustomerExcluded")
                && !Get<bool>(record, "WritesData") && Get<object>(claim, "ValidityClear").ToString() == "WholeBytes" && Get<object>(claim, "ValiditySet").ToString() == "None");
        }
        else Check("customer fixture available", false);
        policyApp = App(2, behavior: "Background"); var p = Stat(policyApp).Element(Ns + "Parameters")!.Elements().First(); p.SetAttributeValue("CustomerAdjustable", true);
        Program(policyApp).Element(Ns + "Dynamic")!.Elements().First().Remove(); var background = Load(directory, policyApp);
        if (Available(background.Population))
        {
            var excluded = Record(Variant(background, 1025), "R0"); var included = Record(Variant(background, 1), "R0");
            Check("excluded null background allocates but changes neither Data nor validity", Get<bool>(excluded, "Allocates") && !Get<bool>(excluded, "WritesData")
                && Get<object>(Get<object>(excluded, "Claim"), "ValiditySet").ToString() == "None" && Get<object>(Get<object>(excluded, "Claim"), "ValidityClear").ToString() == "None");
            Check("included null background validates full touched bytes", !Get<bool>(included, "WritesData") && Get<object>(Get<object>(included, "Claim"), "ValiditySet").ToString() == "WholeBytes");
        }
        else Check("background fixture available", false);
        var sameByte = App(2, 2); var memory = Stat(sameByte).Descendants(Ns + "Memory").ToArray(); memory[0].SetAttributeValue("BitOffset", 6); memory[1].SetAttributeValue("Offset", 0); memory[1].SetAttributeValue("BitOffset", 2);
        memory[0].Parent!.SetAttributeValue("CustomerAdjustable", true); var collisions = Load(directory, sameByte);
        Check("Data-disjoint fields are admitted before mask policy is chosen", Available(collisions.Inventory) && Available(collisions.Population));
        if (Available(collisions.Population))
        {
            Check("same-byte Set/Set remains commutative", Available(Variant(collisions, 1)));
            var rejected = Variant(collisions, 1025);
            Check("same-byte Clear/Set refuses atomically despite disjoint Data bits", Issue(rejected, "NoncommutativeValidity") && Items(rejected, "Records").Length == 0 && Items(rejected, "Claims").Length == 0);
        }
        var defaults = App(2); Program(defaults).Element(Ns + "Dynamic")!.Elements().First().Remove(); var inactive = Load(directory, defaults, edit: f => f.Device.ParameterValues["R0"] = "7");
        if (Available(inactive.Population)) Check("inactive default selects Parameter.Value instead of Saved current", Get<uint?>(Get<object>(Record(Variant(inactive, 1), "R0"), "Claim"), "EncodedWord") == 1);
        else Check("inactive default fixture available", false);
        full.Fixture.Device.ParameterValues["R0"] = "03";
        Check("new saved presence stales source capture even when encodable", !Current(full.Inventory)); full.Fixture.Device.ParameterValues.Clear();
        Check("restoring exact saved map restores freshness", Current(full.Inventory));
        var raw = Load(directory, App(2), edit: f => f.Device.ParameterValues["R0"] = "03"); raw.Fixture.Device.ParameterValues["R0"] = "3";
        Check("raw 03 to 3 cannot reuse source population capture", !Current(raw.Inventory));
        Check("unknown null NoStorage type fails before context", Empty(ReadChanged(directory, a =>
        {
            Stat(a).Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "U"), new XElement(Ns + "TypeUnknown")));
            var noStorage = Stat(a).Element(Ns + "Parameters")!.Elements().Single(e => (string?)e.Attribute("Id") == "NS");
            noStorage.SetAttributeValue("ParameterType", "U"); noStorage.Attribute("Value")!.Remove();
        }), "UnsupportedType"));
        Check("misplaced aliases cannot evade the original ref container", Empty(ReadChanged(directory, a => Program(a).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "alien"), new XAttribute("RefId", "P0")))), "InvalidDeclaration"));
        Check("unselected duplicate alias refuses the full profile", Empty(ReadChanged(directory, a => Stat(a).Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "alias"), new XAttribute("RefId", "P0")))), "AmbiguousOwnership"));
        Check("unknown MemoryType never becomes an accepted mapped source", Empty(ReadChanged(directory, a => Stat(a).Descendants(Ns + "AbsoluteSegment").Single().SetAttributeValue("MemoryType", "other")), "UnsupportedMapping"));
        Check("foreign source capsule cannot reuse another inventory", !(bool)full.Inventory.GetType().GetMethod("BelongsTo", Any)!.Invoke(full.Inventory, [raw.Fixture.Project, raw.Fixture.Device, raw.Inputs])!);
        var savedBefore = full.Fixture.Device.ParameterValues.ToArray(); full.Fixture.Device.ParameterValues["R0"] = new string('1', 65);
        Check("oversized raw values fail before source/context allocation", Empty(Inventory(full.Fixture, Inputs(full.Fixture)), "InvalidValue"));
        full.Fixture.Device.ParameterValues.Clear(); foreach (var pair in savedBefore) full.Fixture.Device.ParameterValues.Add(pair.Key, pair.Value);
        var alwaysApp = App(2); Stat(alwaysApp).Element(Ns + "Parameters")!.Elements().First().SetAttributeValue("LegacyPatchAlways", true);
        Program(alwaysApp).Element(Ns + "Dynamic")!.Elements().First().Remove(); var always = Load(directory, alwaysApp, edit: f => f.Device.ParameterValues["R0"] = "7");
        Check("Always uses hydrated current even when its normal counter is zero", Available(always.Population)
            && Get<RootParameterDisposition>(Record(Variant(always, 1), "R0"), "Disposition") == RootParameterDisposition.AlwaysValue
            && Get<uint?>(Get<object>(Record(Variant(always, 1), "R0"), "Claim"), "EncodedWord") == 7);
        var noneApp = App(2, behavior: "None"); Stat(noneApp).Element(Ns + "Parameters")!.Elements().First().SetAttributeValue("CustomerAdjustable", true);
        Program(noneApp).Element(Ns + "Dynamic")!.Elements().First().Remove(); var none = Load(directory, noneApp);
        Check("excluded null None preserves allocation without any validity effect", Available(none.Population)
            && Get<bool>(Record(Variant(none, 1025), "R0"), "Allocates") && !Get<bool>(Record(Variant(none, 1025), "R0"), "WritesData")
            && Get<object>(Get<object>(Record(Variant(none, 1025), "R0"), "Claim"), "ValidityClear").ToString() == "None");
        var overrideApp = App(2); Stat(overrideApp).Element(Ns + "Parameters")!.Elements().First().SetAttributeValue("CustomerAdjustable", true);
        Stat(overrideApp).Element(Ns + "ParameterRefs")!.Elements().First().SetAttributeValue("CustomerAdjustable", false); var overridden = Load(directory, overrideApp);
        Check("explicit reference Customer=false overrides parameter inheritance", Available(overridden.Population)
            && !Get<bool>(Record(Variant(overridden, 1025), "R0"), "CustomerExcluded") && Get<bool>(Record(Variant(overridden, 1025), "R0"), "WritesData"));
        var canonical = Load(directory, App(2), master: PropertySourceChecks.Master("PDT_GENERIC_04", first: 2));
        Check("native AppProperty normalization retains its separate raw selector", Available(canonical.Inventory)
            && Items(canonical.Inventory, "Owners").Where(o => Get<object>(Get<object>(o, "Target"), "Space").ToString() == "PropertyData")
                .All(o => Get<PropertySourceSelector>(o, "RawPropertySelector").NativeObjectIndex == 3
                    && Get<PropertySourceSelector>(Get<object>(o, "Target"), "Selector") is { Kind: PropertySourceKind.AppProperty, ApplicationObjectIndex: 1 }));
        var badHardware = PropertyInitializationChecks.Hardware(); badHardware.Descendants(Ns + "ApplicationProgramRef").Single().SetAttributeValue("Role", "Primary");
        var bad = ArchivedImageSourceChecks.Load(directory, hardware: badHardware, app: App(2), master: PropertySourceChecks.Master("PDT_GENERIC_04"));
        Check("no-AP2 capability requires sole RefId-only native child shape", Empty(Inventory(bad, Inputs(bad)), "UnsupportedSource"));
        Check("wrongly nested enum payload is refused before generic type parsing", Empty(ReadChanged(directory, a =>
            Stat(a).Descendants(Ns + "TypeNumber").Single().Add(new XElement(Ns + "Wrapper", Enumerable.Range(0, 4097).Select(i =>
                new XElement(Ns + "Enumeration", new XAttribute("Id", "E" + i), new XAttribute("Value", i)))))), "InvalidDeclaration"));
        var archiveField = typeof(Project).GetField("_archiveState", Any)!;
        var capturedArchive = archiveField.GetValue(full.Fixture.Project);
        try
        {
            archiveField.SetValue(full.Fixture.Project, null);
            Check("repeated freshness checks use the captured H2P without archive recapture", Current(full.Inventory) && Current(full.Inventory));
        }
        finally { archiveField.SetValue(full.Fixture.Project, capturedArchive); }
        var retained = (XDocument)full.Inventory.GetType().GetField("_hardwareDocument", Any)!.GetValue(full.Inventory)!;
        var retainedReference = retained.Descendants(Ns + "ApplicationProgramRef").Single(); var oldReference = (string?)retainedReference.Attribute("RefId");
        retainedReference.SetAttributeValue("RefId", "M-0001_A-other");
        Check("retained private H2P taint is rejected without a replacement capture", !Current(full.Inventory));
        retainedReference.SetAttributeValue("RefId", oldReference);
        Check("restoring the exact retained H2P restores freshness", Current(full.Inventory));
        Console.WriteLine($"NativeGeneralPopulation {checks} checks, {failures} failures"); return failures;
    }
}
