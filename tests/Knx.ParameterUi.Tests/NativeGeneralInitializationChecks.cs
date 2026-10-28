using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;

internal static class NativeGeneralInitializationChecks
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly XNamespace Ns = PropertySourceChecks.Ns;
    private static Type Core(string name) => typeof(Project).Assembly.GetType("Knx." + name, true)!;
    private static object Call(string type, string method, params object?[] args) => Core(type).GetMethod(method, Static)!.Invoke(null, args)!;
    private static T Get<T>(object value, string name) => (T)value.GetType().GetProperty(name, Any)!.GetValue(value)!;
    private static object[] Items(object value, string name) => Get<IEnumerable>(value, name).Cast<object>().ToArray();
    private static bool Available(object value) => Get<bool>(value, "IsAvailable");
    private static bool Issue(object value, string code) => Items(value, "Issues").Any(i => Get<object>(i, "Code").ToString() == code);
    private sealed record Capture(ArchivedImageSourceChecks.Fixture Fixture, object Inputs, object Inventory,
        object Preparation, object? Application, object? Views, object? Population, object? Initialization);
    private static XDocument Master()
    {
        var doc = PropertySourceChecks.Master("PDT_GENERIC_04"); var configuration = doc.Descendants(Ns + "HawkConfigurationData").Single();
        configuration.Add(new XElement(Ns + "MemorySegments", new XElement(Ns + "MemorySegment", new XAttribute("MemoryType", "EEPROM"),
            new XAttribute("Length", 8), new XElement(Ns + "Location", new XAttribute("AddressSpace", "StandardMemory"), new XAttribute("StartAddress", 100)),
            new XElement(Ns + "AccessRights", new XAttribute("Read", "Configuration"), new XAttribute("Write", "Configuration")))));
        var names = new[] { "GroupAddressTablePtr", "GroupAssociationTablePtr", "GroupObjectTablePtr", "ApplicationDataPtr", "PeiprogDataPtr" };
        configuration.Add(new XElement(Ns + "Resources", names.Select((name, i) => new XElement(Ns + "Resource", new XAttribute("Name", name),
            new XAttribute("Access", "remote"), new XElement(Ns + "Location", new XAttribute("AddressSpace", "SystemProperty"),
                new XAttribute("StartAddress", 0), new XAttribute("InterfaceObjectRef", i + 1), new XAttribute("PropertyID", 7)),
            new XElement(Ns + "ResourceType", new XAttribute("Length", 4))))));
        return doc;
    }
    private static Capture Load(string directory, XDocument? app = null, XDocument? master = null, uint[]? options = null, byte[]? factory = null, bool compile = true)
    {
        var fixture = ArchivedImageSourceChecks.Load(directory, app: app ?? NativeGeneralPopulationChecks.App(2), master: master ?? Master(),
            hardware: PropertyInitializationChecks.Hardware(factory));
        var inputs = Call("ArchivedImageSources", "ReadInputs", fixture.Project, fixture.Device);
        var inventory = Call("NativeRootImageInventory", "Read", fixture.Project, fixture.Device, inputs);
        var preparation = Call("NativeImagePreparation", "Prepare", fixture.Project, fixture.Device, inputs, inventory, options ?? [1u]);
        if (!compile || !Available(preparation)) return new(fixture, inputs, inventory, preparation, null, null, null, null);
        var refs = Get<IEnumerable<string>>(preparation, "ControllerReferences").ToArray();
        var application = Call("NativeApplicationEvaluationContext", "Create", fixture.Project, fixture.Device, inputs, refs, null);
        var views = Call("NativeImageVariantViewSet", "Create", fixture.Project, fixture.Device, inputs, inventory, preparation, application);
        var population = Call("NativeRootWriterPopulation", "Create", inventory, preparation, application);
        var initialization = Call("NativeImageInitializer", "CompileAll", fixture.Project, fixture.Device, inputs, inventory, preparation, views, application, population);
        return new(fixture, inputs, inventory, preparation, application, views, population, initialization);
    }
    private static object Variant(Capture capture, uint options, bool? tool = null) => Items(capture.Preparation, "Variants").Single(v => Get<uint>(v, "RawOptions") == options
        && (tool is null || Get<object?>(v, "ToolManagedIpFact") is { } fact && Get<bool>(fact, "Value") == tool));
    private static object Plan(Capture capture, uint options) => Items(capture.Initialization!, "Variants").Single(v => Get<uint>(Get<object>(v, "Variant"), "RawOptions") == options);
    private static bool GlobalEmpty(object value) => Get<bool>(value, "HasGlobalFailure") && Items(value, "Variants").Length == 0 && Get<string?>(value, "Fingerprint") is null;

    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string text, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "NativeGeneralInitialization " + text); if (!ok) failures++; }
        var capture = Load(directory, options: [1, 129]); var before = Knxproj.ToXml(capture.Fixture.Project).ToString(SaveOptions.DisableFormatting);
        var archive = SHA256.HashData(File.ReadAllBytes(capture.Fixture.Path));
        Check("mixed source and one context produce both explicit alternatives", Available(capture.Preparation) && capture.Initialization is not null
            && !Get<bool>(capture.Initialization, "HasGlobalFailure") && Items(capture.Initialization, "Variants").All(Available));
        if (capture.Initialization is not null && !Get<bool>(capture.Initialization, "HasGlobalFailure"))
        {
            var ordinary = Plan(capture, 1); var full = Plan(capture, 129);
            if (Available(ordinary) && Available(full))
            {
                var contributions = Items(ordinary, "Contributions"); var fullContributions = Items(full, "Contributions");
                Check("ordered contributions include zero region then original code then five pointers then property", contributions.Select(c => Get<object>(c, "Kind").ToString())
                    .SequenceEqual(new[] { "ZeroRegion", "NativeCode", "PointerZero", "PointerZero", "PointerZero", "PointerZero", "PointerZero", "FactoryProperty" }));
                var code = contributions[1]; var originalCode = (ImageCompositionSourceSegment)Items(capture.Inventory, "CodeSources").Single();
                Check("recipe shares original A5/F0 operands without canonicalizing or copying", ReferenceEquals(Get<ImmutablePatchBytes?>(code, "RawData"), originalCode.RawData)
                    && Get<ImmutablePatchBytes>(code, "RawData").ToHexString() == "A5" && Get<ImmutablePatchBytes>(code, "RawMask").ToHexString() == "F0");
                Check("zero recipes do not manufacture source byte arrays", contributions.Where(c => Get<object>(c, "Kind").ToString() is "ZeroRegion" or "PointerZero")
                    .All(c => Get<ImmutablePatchBytes?>(c, "RawData") is null && Get<ImmutablePatchBytes?>(c, "RawMask") is null));
                Check("non-full transitions clear masks before materializing null masks", Items(ordinary, "PostSourceTransitions").Select(t => Get<object>(t, "Kind").ToString())
                    .Take(2).SequenceEqual(new[] { "ResetMasksToZero", "MaterializeNullMasks" }));
                Check("QueryFull retains masks and skips every pointer candidate", !fullContributions.Any(c => Get<object>(c, "Kind").ToString() == "PointerZero")
                    && !Items(full, "PostSourceTransitions").Any(t => Get<object>(t, "Kind").ToString() == "ResetMasksToZero"));
                Check("all four physical owners have coverage while NoStorage remains unallocated", Items(ordinary, "CoverageFacts").Length == 4);
                var prepared = capture.Preparation.GetType().GetMethod("GetProcedurePreparation", Any)!.Invoke(capture.Preparation, [Variant(capture, 1)])!;
                var graph = Get<object>(prepared, "Graph"); var declaration = Get<object>(Get<object>(ordinary, "CompiledProcedure"), "DeclarationPlan");
                Check("primitive declaration is exactly Prepare's original legacy object, without another compile", ReferenceEquals(declaration, Get<object>(graph, "Legacy")));
                Check("observed options remain conditional and hold full-word identity", !Get<bool>(capture.Preparation, "HasActualRoute")
                    && Get<string>(ordinary, "Fingerprint") != Get<string>(full, "Fingerprint"));
            }
        }
        Check("duplicate options are rejected before any source capability is required", Issue(Call("NativeImagePreparation", "Prepare", null, null, null, null, new uint[] { 1, 1 }), "InvalidRequest"));
        Check("option count seventeen is rejected before source access", Issue(Call("NativeImagePreparation", "Prepare", null, null, null, null, Enumerable.Range(0, 17).Select(i => (uint)i).ToArray()), "InvalidRequest"));
        var special = Load(directory, options: [0, 8, 136], compile: true);
        Check("unknown consumed tool IP expands only low-eight-equals-eight", Items(special.Preparation, "Variants").Length == 4
            && !Get<bool>(Get<object>(Variant(special, 8, true), "Tasks"), "BasicProcedure")
            && Get<bool>(Get<object>(Variant(special, 8, false), "Tasks"), "AddCode")
            && Get<bool>(Get<object>(Variant(special, 136), "Tasks"), "AddCode"));
        if (special.Initialization is not null && !Get<bool>(special.Initialization, "HasGlobalFailure"))
        {
            var skipped = Items(special.Initialization, "Variants").Single(p => ReferenceEquals(Get<object>(p, "Variant"), Variant(special, 8, true)));
            Check("skipped AddCode means no recipes and no mask transitions", Available(skipped) && Items(skipped, "Contributions").Length == 0 && Items(skipped, "PostSourceTransitions").Length == 0);
        }
        var master = Master(); var config = master.Descendants(Ns + "HawkConfigurationData").Single();
        config.Add(new XElement(Ns + "Procedures", new[] { "ap1", "all", "grp", "par", "par,grp", "cfg" }.Select(s => new XElement(Ns + "Procedure",
            new XAttribute("ProcedureType", "Load"), new XAttribute("ProcedureSubType", s), new XAttribute("Access", "remote"), new XElement(Ns + "LdCtrlRestart")))));
        var defaultApp = NativeGeneralPopulationChecks.App(2); NativeGeneralPopulationChecks.Program(defaultApp).SetAttributeValue("LoadProcedureStyle", "DefaultProcedure");
        var selection = Load(directory, defaultApp, master, [1, 2, 3, 128, 129, 130, 131, 191, 255], compile: false);
        foreach (var (word, subtype) in new (uint, string)[] { (1,"par"),(2,"grp"),(3,"par,grp"),(128,"ap1"),(129,"par"),(130,"grp"),(131,"par,grp"),(191,"ap1"),(255,"ap1") })
            Check("native source template for full word " + word + " is " + subtype, Get<string>(Get<object>(Variant(selection, word), "Selection"), "NativeSubtype") == subtype);
        Check("identical derived preparation pairs are shared across full raw words", ReferenceEquals(
            selection.Preparation.GetType().GetMethod("GetProcedurePreparation", Any)!.Invoke(selection.Preparation, [Variant(selection,191)]),
            selection.Preparation.GetType().GetMethod("GetProcedurePreparation", Any)!.Invoke(selection.Preparation, [Variant(selection,255)])));
        var normalized = Master(); normalized.Descendants(Ns + "HawkConfigurationData").Single().Add(new XElement(config.Element(Ns + "Procedures")!));
        normalized.Descendants(Ns + "Procedure").First(p => (string?)p.Attribute("ProcedureSubType") == "par").Element(Ns + "LdCtrlRestart")!.SetAttributeValue("AppliesTo", "full");
        var refused = Load(directory, defaultApp, normalized, [1], compile: false);
        Check("non-equivalent mask normalization refuses without altered source documents", !Available(Variant(refused,1)) && Issue(Variant(refused,1),"UnsupportedControl"));
        var memoryOnly = new XDocument(defaultApp); var memoryStat = NativeGeneralPopulationChecks.Stat(memoryOnly);
        foreach (var parameter in memoryStat.Element(Ns+"Parameters")!.Elements().Where(e=>e.Element(Ns+"Property") is not null).ToArray())
        {
            var id=(string)parameter.Attribute("Id")!;
            foreach(var reference in memoryStat.Element(Ns+"ParameterRefs")!.Elements().Where(r=>(string?)r.Attribute("RefId")==id).ToArray())
            {
                var rid=(string)reference.Attribute("Id")!;
                memoryOnly.Descendants(Ns+"Dynamic").Descendants(Ns+"ParameterRefRef").Where(r=>(string?)r.Attribute("RefId")==rid).Remove();
                reference.Remove();
            }
            parameter.Remove();
        }
        var maskNormalized=Load(directory,memoryOnly,master,[1]);
        Check("default procedure can compile the equivalent mask scope", maskNormalized.Initialization is not null
            && !Get<bool>(maskNormalized.Initialization,"HasGlobalFailure") && Available(Plan(maskNormalized,1)));
        if(maskNormalized.Initialization is not null && !Get<bool>(maskNormalized.Initialization,"HasGlobalFailure") && Available(Plan(maskNormalized,1)))
        {
            var cp=Get<object>(Plan(maskNormalized,1),"CompiledProcedure");
            Check("new native effective condition is all without mutating declaration auto", Items(cp,"NativeEffectiveControls").All(c=>Get<LoadControlCondition>(c,"EffectiveAppliesTo")==LoadControlCondition.All)
                && ((LoadProcedurePlan)Get<object>(cp,"DeclarationPlan")).Controls.Single().EffectiveAppliesTo==LoadControlCondition.Auto);
        }
        var missing = Master(); missing.Descendants(Ns + "Resource").First().Remove(); var missingPointer = Load(directory, master: missing);
        Check("missing pointer declaration is not fabricated as absent fallback", missingPointer.Initialization is not null && !Get<bool>(missingPointer.Initialization,"HasGlobalFailure")
            && Issue(Plan(missingPointer,1),"UnsupportedMapping") && Items(Plan(missingPointer,1),"Contributions").Length == 0);
        var factory = PropertySourceChecks.Frame(data: Convert.FromHexString("7F55000000000000"), dataMask: Enumerable.Repeat((byte)255,8).ToArray(), count:[0x12,0x34]);
        var factoryCapture = Load(directory, factory:factory);
        Check("factory recipe and independent count source are admitted", factoryCapture.Initialization is not null
            && !Get<bool>(factoryCapture.Initialization,"HasGlobalFailure") && Available(Plan(factoryCapture,1)));
        if (factoryCapture.Initialization is not null && !Get<bool>(factoryCapture.Initialization,"HasGlobalFailure") && Available(Plan(factoryCapture,1)))
        {
            var p = Plan(factoryCapture,1); var property = Items(p,"Contributions").Single(c=>Get<object>(c,"Kind").ToString()=="FactoryProperty");
            Check("factory operands stay raw separate source roles before zero-mask materialization", Get<ImmutablePatchBytes>(property,"RawData").ToHexString()=="7F55000000000000"
                && Get<ImmutablePatchBytes>(property,"RawMask").ToHexString()=="FFFFFFFFFFFFFFFF");
            Check("count stays observed BE and not adopted", Items(p,"ObservedCounts").Length==1
                && Get<ushort>(Items(p,"ObservedCounts")[0],"EncodedCount")==0x1234 && Get<bool>(Items(p,"ObservedCounts")[0],"NotAdopted"));
        }
        if (capture.Application is not null)
        {
            var foreign = Load(directory);
            var invalid = Call("NativeImageInitializer","CompileAll",capture.Fixture.Project,capture.Fixture.Device,capture.Inputs,capture.Inventory,
                capture.Preparation,foreign.Views,capture.Application,capture.Population);
            Check("foreign same-shape view-set fails globally without recipes", GlobalEmpty(invalid));
            var had=capture.Fixture.Device.ParameterValues.TryGetValue("R0",out var saved);
            capture.Fixture.Device.ParameterValues["R0"]="03";
            Check("raw saved taint invalidates preparation without semantic canonicalization", !(bool)capture.Preparation.GetType().GetMethod("MatchesCurrent",Any)!.Invoke(capture.Preparation,null)!);
            var stale = Call("NativeImageInitializer","CompileAll",capture.Fixture.Project,capture.Fixture.Device,capture.Inputs,capture.Inventory,
                capture.Preparation,capture.Views,capture.Application,capture.Population);
            Check("stale input produces an empty global failure", GlobalEmpty(stale));
            if(had)capture.Fixture.Device.ParameterValues["R0"]=saved!;else capture.Fixture.Device.ParameterValues.Remove("R0");
        }
        var budgetApp=NativeGeneralPopulationChecks.App(2);
        XElement Operand(int size)=>new(Ns+"LdCtrlWriteProp",new XAttribute("ObjIdx",3),new XAttribute("PropId",10),
            new XAttribute("StartElement",1),new XAttribute("Count",1),new XAttribute("Verify",false),new XAttribute("InlineData",new string('0',checked(size*2))));
        NativeGeneralPopulationChecks.Stat(budgetApp).Element(Ns+"LoadProcedures")!.Add(new XElement(Ns+"LoadProcedure",new XAttribute("MergeId",7),
            Operand(1024*1024-2)));
        var exactBudget=Load(directory,budgetApp,options:[1,129,3,131],compile:false);
        Check("aggregate counts unused operands plus resource Data and Mask at exact four MiB", Available(exactBudget.Preparation)
            && Get<long>(exactBudget.Preparation,"DecodedOperandBytes")==4L*1024*1024
            && Get<long>(exactBudget.Preparation,"ReservedProcedurePayloadBytes")==16L*1024*1024);
        var tooMuch=Load(directory,budgetApp,options:[1,129,3,131,128],compile:false);
        Check("fifth distinct preparation fails aggregate budget before retained decoder graphs", !Available(tooMuch.Preparation)
            && Issue(tooMuch.Preparation,"LimitExceeded") && Items(tooMuch.Preparation,"Variants").Length==0);
        Check("read-only preparation and compilation preserve project and original archive", before==Knxproj.ToXml(capture.Fixture.Project).ToString(SaveOptions.DisableFormatting)
            && archive.SequenceEqual(SHA256.HashData(File.ReadAllBytes(capture.Fixture.Path))));
        Console.WriteLine($"NativeGeneralInitialization: {checks} checks, {failures} failures"); return failures;
    }
}
