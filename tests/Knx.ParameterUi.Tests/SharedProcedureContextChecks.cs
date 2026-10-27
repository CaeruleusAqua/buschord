using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Knx;

internal static class SharedProcedureContextChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type ProcedureContext = typeof(Project).Assembly.GetType("Knx.NativeProcedureEvaluationContext", true)!;
    private static readonly Type ApplicationContext = typeof(Project).Assembly.GetType("Knx.NativeApplicationEvaluationContext", true)!;
    private static readonly LoadProcedureRequest Request = new("all", LoadProcedureAccess.Remote, LoadParts.Full);
    private static T Get<T>(object value, string name) => (T)value.GetType().GetProperty(name, Any)!.GetValue(value)!;
    private static XElement Program(XDocument doc) => doc.Descendants(Ns + "ApplicationProgram").Single();
    private static XElement Static(XDocument doc) => Program(doc).Element(Ns + "Static")!;
    private static XElement Dynamic(XDocument doc) => Program(doc).Element(Ns + "Dynamic")!;
    private static XElement Placement(string reference) => new(Ns + "ParameterRefRef", new XAttribute("RefId", reference));
    private static XElement Choose(string reference, string test, params XElement[] body)
        => new(Ns + "choose", new XAttribute("ParamRefId", reference), new XElement(Ns + "when", new XAttribute("test", test), body));
    private static XElement Write(string value) => new(Ns + "LdCtrlWriteProp", new XAttribute("ObjIdx", 4),
        new XAttribute("PropId", 27), new XAttribute("Verify", false), new XAttribute("InlineData", value));
    private static XDocument App(string? controller = "R")
    {
        var document = ArchivedImageSourceChecks.App(); Program(document).SetAttributeValue("LoadProcedureStyle", "ProductProcedure");
        Static(document).Add(new XElement(Ns + "LoadProcedures", new XElement(Ns + "LoadProcedure",
            new XElement(Ns + "LdCtrlConnect"), controller is null ? Write("11") : Choose(controller, "1", Write("11")), new XElement(Ns + "LdCtrlDisconnect"))));
        return document;
    }
    private static void AddReference(XDocument document, string id, string parameter, string value = "1")
    {
        Static(document).Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", parameter), new XAttribute("ParameterType", "T"), new XAttribute("Value", value)));
        Static(document).Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", id), new XAttribute("RefId", parameter)));
    }
    private static XDocument AliasApp()
    {
        var document = App("R2"); AddReference(document, "R2", "P2");
        Static(document).Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "R3"), new XAttribute("RefId", "P2"), new XAttribute("Access", "None")));
        Dynamic(document).Add(Choose("R", "0", Placement("R2")), Choose("R", "1", Placement("R3")));
        return document;
    }
    private sealed record Capture(ArchivedImageSourceChecks.Fixture Fixture, object Inputs, object Prepared, object Application, object Shared);
    private static object Inputs(ArchivedImageSourceChecks.Fixture fixture)
        => typeof(ArchivedImageSources).GetMethod("ReadInputs", Hidden)!.Invoke(null, [fixture.Project, fixture.Device])!;
    private static object Prepare(object inputs) => typeof(LoadProcedureCompiler).GetMethod("PrepareResolved", Hidden)!.Invoke(null, [inputs, Request])!;
    private static object CreateApplication(ArchivedImageSourceChecks.Fixture fixture, object inputs, string[] refs, IParameterScriptRuntime? runtime = null)
        => ApplicationContext.GetMethod("Create", Hidden)!.Invoke(null, [fixture.Project, fixture.Device, inputs, refs, runtime])!;
    private static object Shared(ArchivedImageSourceChecks.Fixture fixture, object inputs, object prepared, object application)
        => ProcedureContext.GetMethod("FromApplicationContext", Hidden)!.Invoke(null, [fixture.Project, fixture.Device, inputs, prepared, application])!;
    private static object Original(Capture capture)
        => ProcedureContext.GetMethod("Create", Hidden)!.Invoke(null, [capture.Fixture.Project, capture.Fixture.Device, capture.Inputs, capture.Prepared])!;
    private static Capture Load(string directory, XDocument? document = null, Action<ArchivedImageSourceChecks.Fixture>? edit = null,
        string[]? refs = null, IParameterScriptRuntime? runtime = null)
    {
        var fixture = ArchivedImageSourceChecks.Load(directory, app: document ?? App()); edit?.Invoke(fixture);
        var inputs = Inputs(fixture); var prepared = Prepare(inputs);
        var references = refs ?? Get<IReadOnlyList<string>>(prepared, "ControllerReferences").ToArray();
        var application = CreateApplication(fixture, inputs, references, runtime);
        return new(fixture, inputs, prepared, application, Shared(fixture, inputs, prepared, application));
    }
    private static LoadProcedurePlan Compile(Capture capture, object? context = null)
        => (LoadProcedurePlan)typeof(LoadProcedureCompiler).GetMethod("CompileResolved", Hidden)!.Invoke(null, [capture.Prepared, context ?? capture.Shared])!;
    private static bool Current(Capture capture, object? prepared = null)
        => (bool)ProcedureContext.GetMethod("MatchesCurrent", Any)!.Invoke(capture.Shared, [prepared ?? capture.Prepared])!;
    private static object Controller(Capture capture, string reference) => ((IEnumerable)Get<object>(capture.Shared, "Controllers")).Cast<object>()
        .Where(p => Get<string>(p, "Key") == reference).Select(p => Get<object>(p, "Value")).Single();
    private static bool Available(object context) => Get<bool>(context, "IsAvailable");
    private static bool Empty(object context, LoadProcedureIssueCode? code = null)
        => !Available(context) && Get<string?>(context, "Fingerprint") is null && !Get<bool>(context, "IsSharedApplicationProof")
            && Get<string?>(context, "SharedApplicationFingerprint") is null && Get<string?>(context, "SharedControllerSetHash") is null
            && Get<string?>(context, "SharedPlacementInventoryHash") is null && !((IEnumerable)Get<object>(context, "Controllers")).Cast<object>().Any()
            && Get<IReadOnlyList<LoadProcedureIssue>>(context, "Issues") is { Count: 1 } issues && (code is null || issues[0].Code == code);
    private static string Payload(LoadProcedurePlan plan) => JsonSerializer.Serialize(new
    { plan.ProfileVersion, plan.Controls, plan.MergeDecisions, plan.ResourceReferences, plan.TableRequirements, plan.Requirements });
    private sealed class Runtime : IParameterScriptRuntime
    {
        internal int Calls;
        public IReadOnlyDictionary<string, object?> Execute(string source, IReadOnlyDictionary<string, object?> inputs, IReadOnlyList<string> outputs)
        { Calls++; return outputs.ToDictionary(id => id, _ => (object?)1d, StringComparer.Ordinal); }
    }

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "SharedProcedureContext " + name); if (!ok) failures++; }
        void Refused(string name, XDocument document, LoadProcedureIssueCode expected, Action<ArchivedImageSourceChecks.Fixture>? edit = null)
        {
            var capture = Load(directory, document, edit);
            Check(name, Available(capture.Application) && Empty(capture.Shared, expected) && !Compile(capture).IsAvailable && Compile(capture).Controls.Count == 0);
        }
        var basic = Load(directory); var snapshot = Get<object>(basic.Application, "Snapshot"); var original = Original(basic);
        Check("provided fresh application proof mints an active root procedure view", Available(basic.Application) && Available(basic.Shared)
            && Get<int>(Controller(basic, "R"), "EverDeclaredOwnerCount") == 1 && Get<int>(Controller(basic, "R"), "ActiveNormalOwnerCount") == 1
            && Get<long?>(Controller(basic, "R"), "ActiveValue") == 1);
        Check("new proof kind and hashes are separate from the original source-only context", Get<bool>(basic.Shared, "IsSharedApplicationProof")
            && Get<string>(basic.Shared, "SharedApplicationFingerprint") == Get<string>(basic.Application, "Fingerprint")
            && Get<string>(basic.Shared, "SharedControllerSetHash").Length == 64 && Get<string>(basic.Shared, "SharedPlacementInventoryHash").Length == 64
            && !Get<bool>(original, "IsSharedApplicationProof") && Get<string?>(original, "SharedApplicationFingerprint") is null
            && Get<string>(original, "Fingerprint") != Get<string>(basic.Shared, "Fingerprint"));
        var oldPlan = Compile(basic, original); var sharedPlan = Compile(basic);
        Check("shared and source-only resolution emit identical controls and requirements", sharedPlan.IsAvailable && oldPlan.IsAvailable
            && Payload(sharedPlan) == Payload(oldPlan) && sharedPlan.WitnessHash != oldPlan.WitnessHash);
        Check("mint and repeated resolution reuse the same snapshot", ReferenceEquals(snapshot, Get<object>(basic.Application, "Snapshot"))
            && Compile(basic).WitnessHash == sharedPlan.WitnessHash && Current(basic));
        Check("fresh source-only replay retains its original complete fingerprint", Get<string>(Original(basic), "Fingerprint") == Get<string>(original, "Fingerprint")
            && Compile(basic, Original(basic)).WitnessHash == oldPlan.WitnessHash);
        Check("controller dictionary cannot be edited through a mutable collection", Get<object>(basic.Shared, "Controllers") is not IDictionary dictionary || dictionary.IsReadOnly);
        var bytes = File.ReadAllBytes(basic.Fixture.Path); var xml = Knxproj.ToXml(basic.Fixture.Project).ToString(SaveOptions.DisableFormatting);
        var revision = basic.Fixture.Project.ApplicationDataRevision; var undo = new UndoStack(); undo.Reset(basic.Fixture.Project); var index = undo.Index;
        _ = Shared(basic.Fixture, basic.Inputs, basic.Prepared, basic.Application); _ = Compile(basic);
        Check("shared inspection preserves archive model revision and undo", bytes.SequenceEqual(File.ReadAllBytes(basic.Fixture.Path))
            && xml == Knxproj.ToXml(basic.Fixture.Project).ToString(SaveOptions.DisableFormatting) && revision == basic.Fixture.Project.ApplicationDataRevision
            && index == undo.Index && !undo.IsDirty);
        Check("same sources with a separately prepared graph are not existing context authority", !Current(basic, Prepare(basic.Inputs)));

        var inactive = Load(directory, AliasApp()); var row = Controller(inactive, "R2");
        Check("active alias never activates the requested inactive ref", Available(inactive.Shared) && Get<int>(row, "EverDeclaredOwnerCount") == 1
            && Get<int>(row, "ActiveNormalOwnerCount") == 0 && !Get<bool>(row, "Active") && Get<long?>(row, "ActiveValue") is null
            && Compile(inactive).Controls.Count == 2);
        Check("guard closure contains its selector and excludes value aliases", ((IEnumerable)Get<object>(inactive.Shared, "Controllers")).Cast<object>().Select(p => Get<string>(p, "Key"))
            .Order(StringComparer.Ordinal).SequenceEqual(new[] { "R", "R2" }));
        var active = Load(directory, AliasApp(), f => f.Device.ParameterValues["R"] = "0");
        Check("own conditional placement activates the requested ref", Available(active.Shared) && Get<int>(Controller(active, "R2"), "ActiveNormalOwnerCount") == 1
            && Compile(active).Controls.Count == 3);
        var never = App(); Dynamic(never).RemoveNodes();
        Refused("never-placed controller is not an inactive saved controller", never, LoadProcedureIssueCode.NeverPlacedController, f => f.Device.ParameterValues["R"] = "1");
        var guard = App("R2"); AddReference(guard, "R2", "P2"); Dynamic(guard).Add(Choose("R", "1", Placement("R2")));
        Static(guard).Element(Ns + "Parameters")!.Elements().First().SetAttributeValue("Value", "01");
        Refused("noncanonical guard raw value is checked through recursive closure", guard, LoadProcedureIssueCode.InvalidControllerValue);
        var recursive = App("R3"); AddReference(recursive, "R2", "P2"); AddReference(recursive, "R3", "P3");
        Dynamic(recursive).Add(Choose("R", "1", Placement("R2")), Choose("R2", "1", Placement("R3")));
        Static(recursive).Element(Ns + "Parameters")!.Elements().First().SetAttributeValue("Value", "01");
        Refused("second-level owner guard retains the original canonical admission", recursive, LoadProcedureIssueCode.InvalidControllerValue);
        Refused("noncanonical requested saved value is not repaired by numeric word equality", App(), LoadProcedureIssueCode.InvalidControllerValue,
            f => f.Device.ParameterValues["R"] = "03");
        var dual = App(); AddReference(dual, "Other", "OtherP"); AddReference(dual, "Source", "SourceP");
        Dynamic(dual).Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "Other"), new XAttribute("SourceParamRefRef", "Source"), new XAttribute("Value", "1")));
        Refused("whole original grammar rejects an unrelated dual-form assignment", dual, LoadProcedureIssueCode.UnsupportedActivationDependency);
        var noSeedsDual = new XDocument(dual); Static(noSeedsDual).Descendants(Ns + "choose").Single().ReplaceWith(Write("11"));
        var noSeeds = Load(directory, noSeedsDual);
        Check("zero reachable seeds adds no old activity grammar gate", Available(noSeeds.Application) && Available(noSeeds.Shared) && Compile(noSeeds).ProfileVersion == 1);
        var sibling = App("R2"); AddReference(sibling, "R2", "P2"); Dynamic(sibling).Add(new XElement(Ns + "choose", new XAttribute("ParamRefId", "R"),
            new XElement(Ns + "when", new XAttribute("test", "1"), new XElement(Ns + "ParameterSeparator")),
            new XElement(Ns + "when", new XAttribute("default", true), Placement("R2"))));
        var suppressed = Load(directory, sibling); var fallback = Load(directory, sibling, f => f.Device.ParameterValues["R"] = "2");
        Check("all sibling tests suppress an otherwise relevant default owner", Available(suppressed.Shared) && !Get<bool>(Controller(suppressed, "R2"), "Active"));
        Check("default owner contributes only after every sibling misses", Available(fallback.Shared) && Get<bool>(Controller(fallback, "R2"), "Active"));
        var overlap = App(); var procedureChoice = Static(overlap).Descendants(Ns + "choose").Single(); procedureChoice.ReplaceNodes(
            new XElement(Ns + "when", new XAttribute("test", "0 1"), Write("11")), new XElement(Ns + "when", new XAttribute("test", "1 2"), Write("22")));
        var multi = Load(directory, overlap); var multiPlan = Compile(multi);
        Check("all matching procedure branches emit in original order", Available(multi.Shared) && multiPlan.IsAvailable
            && multiPlan.Controls.Where(c => c.InlineData is not null).Select(c => c.InlineData!.ToHexString()).SequenceEqual(new[] { "11", "22" }));

        var unused = App(null); AddReference(unused, "Unused", "UnusedP", "03");
        Static(unused).Element(Ns + "LoadProcedures")!.Add(new XElement(Ns + "LoadProcedure", new XAttribute("MergeId", 7), Choose("Unused", "1", Write("22"))));
        var unusedResult = Load(directory, unused);
        Check("unused group never-placed raw value is type-only rather than an activity seed", Available(unusedResult.Shared)
            && Get<IReadOnlyList<string>>(unusedResult.Prepared, "ControllerReferences").Count == 0
            && !((IEnumerable)Get<object>(unusedResult.Shared, "Controllers")).Cast<object>().Any() && Compile(unusedResult).IsAvailable);
        var invalidUnused = new XDocument(unused); var otherType = new XElement(Static(invalidUnused).Element(Ns + "ParameterTypes")!.Elements().Single());
        otherType.SetAttributeValue("Id", "UnusedType"); otherType.Elements().Single().Attribute("maxInclusive")!.Remove();
        Static(invalidUnused).Element(Ns + "ParameterTypes")!.Add(otherType);
        Static(invalidUnused).Element(Ns + "Parameters")!.Elements().Last().SetAttributeValue("ParameterType", "UnusedType");
        var invalidUnusedResult = Load(directory, invalidUnused);
        Check("unused group invalid controller declaration refuses before shared resolution", !Get<bool>(invalidUnusedResult.Prepared, "IsAvailable") && Empty(invalidUnusedResult.Shared));
        var primitive = Load(directory, App(null)); var primitivePlan = Compile(primitive);
        Check("zero reachable controllers preserves primitive legacy plan", Available(primitive.Shared) && primitivePlan.IsAvailable && primitivePlan.ProfileVersion == 1
            && Payload(primitivePlan) == Payload(Compile(primitive, Original(primitive))));

        var extra = App(); AddReference(extra, "Extra", "ExtraP"); Dynamic(extra).Add(Placement("Extra"));
        Check("extra supplied controller seeds are not equivalent authority", Empty(Load(directory, extra, refs: ["R", "Extra"]).Shared, LoadProcedureIssueCode.ContextMismatch));
        Check("missing supplied controller seed is refused even when values exist", Empty(Load(directory, refs: []).Shared, LoadProcedureIssueCode.ContextMismatch));
        var cycle = App(); Dynamic(cycle).ReplaceNodes(Choose("R", "1", Placement("R"))); var cyclic = Load(directory, cycle);
        Check("cyclic base proof cannot be replaced with another activity evaluation", !Available(cyclic.Application)
            && Get<IReadOnlyList<GeneratedTableIssue>>(cyclic.Application, "Issues").Any(i => i.Code == GeneratedTableIssueCode.ActivationCycle) && Empty(cyclic.Shared));
        var runtime = new Runtime(); var capable = Load(directory, runtime: runtime);
        Check("unused runtime capability is refused without executing it again", Available(capable.Application) && runtime.Calls == 0
            && Empty(capable.Shared, LoadProcedureIssueCode.ContextMismatch));
        Check("new API has no runtime or snapshot argument or retained runtime field", ProcedureContext.GetMethod("FromApplicationContext", Hidden)!.GetParameters().Length == 5
            && ProcedureContext.GetFields(Any).All(f => !typeof(IParameterScriptRuntime).IsAssignableFrom(f.FieldType)));
        var foreign = Load(directory);
        Check("foreign context with identical source ids cannot authorize another owner", Empty(Shared(basic.Fixture, basic.Inputs, basic.Prepared, foreign.Application)));
        var replaced = Load(directory); var oldDevice = replaced.Fixture.Device; var line = replaced.Fixture.Project.Installations[0].Areas[0].Lines[0];
        line.Devices.Clear(); line.Devices.Add(new DeviceInstance { Id = oldDevice.Id, ProductRefId = oldDevice.ProductRefId, Hardware2ProgramRefId = oldDevice.Hardware2ProgramRefId, Address = oldDevice.Address });
        Check("same-id replacement device invalidates current shared proof", !Current(replaced) && Empty(Shared(replaced.Fixture, replaced.Inputs, replaced.Prepared, replaced.Application)));
        var reloaded = Knxproj.Load(basic.Fixture.Path); var reloadedFixture = basic.Fixture with { Project = reloaded, Device = ProjectQuery.DeviceList(reloaded).Single() };
        Check("same-id replacement project cannot reuse a shared proof", Empty(Shared(reloadedFixture, basic.Inputs, basic.Prepared, basic.Application)));

        void Taint(string name, Action<Capture> change)
        {
            var capture = Load(directory); change(capture);
            Check(name, !Current(capture) && Empty(Shared(capture.Fixture, capture.Inputs, capture.Prepared, capture.Application))
                && !Compile(capture).IsAvailable && Compile(capture).Controls.Count == 0);
        }
        Taint("application source mutation invalidates both mint and compiler", c => Get<XDocument>(c.Inputs, "Application").Descendants(Ns + "Parameter").Single().SetAttributeValue("Value", 2));
        Taint("archived master mutation invalidates shared pairing", c => Get<XDocument>(c.Inputs, "Master").Descendants(Ns + "Feature").First().SetAttributeValue("Value", 6));
        Taint("saved model mutation is checked without an external freshness assertion", c => c.Fixture.Device.ParameterValues["R"] = "2");
        Taint("snapshot mutation cannot be reused as a supplied proof", c => Get<XElement>(Get<ApplicationSnapshot>(c.Application, "Snapshot").Evaluation, "ExpandedRoot").SetAttributeValue("Changed", "1"));
        Taint("communication-object model mutation invalidates the shared application view", c => c.Fixture.Device.ComObjectRefs.Add(new() { Id = "unknown", ComObjectId = "unknown", Number = 1 }));
        var provenance = Load(directory); var catalog = Get<object>(provenance.Application, "Catalog");
        var sourceNodes = Get<IReadOnlyList<XElement>>(catalog, "Nodes"); var node = sourceNodes.Single(e => e.Name == Ns + "ParameterRefRef");
        node.RemoveAnnotations(typeof(Project).Assembly.GetType("Knx.NativeCommunicationElementOrigin", true)!);
        Check("missing typed original placement provenance cannot mint a shared view", Empty(Shared(provenance.Fixture, provenance.Inputs, provenance.Prepared, provenance.Application), LoadProcedureIssueCode.ContextMismatch));
        var badCount = Load(directory); var activation = Get<object>(badCount.Application, "Activation");
        var counter = ((IEnumerable)Get<object>(activation, "Controllers")).Cast<object>().Select(p => Get<object>(p, "Value")).Single();
        counter.GetType().GetProperty("NormalReferenceCount", Any)!.SetValue(counter, 2);
        Check("native counter must equal independently paired active placement identities", Empty(Shared(badCount.Fixture, badCount.Inputs, badCount.Prepared, badCount.Application), LoadProcedureIssueCode.ContextMismatch));
        var rawKey = Load(directory, edit: f => f.Device.ParameterValues["M-0001_A-0100-10-0000_R"] = "2");
        Check("original short ref does not acquire a reverse-prefix saved fallback", Available(rawKey.Application) && Available(rawKey.Shared)
            && Get<long?>(Controller(rawKey, "R"), "ActiveValue") == 1
            && Get<string>(Original(rawKey), "SavedValuesHash") == Get<string>(rawKey.Shared, "SavedValuesHash"));
        var bound = Load(directory, App(null)); var captured = Get<object>(bound.Application, "CapturedInputs");
        var oversized = Enumerable.Range(0, 64).ToDictionary(i => "K" + i, _ => (string?)new string('1', 65536), StringComparer.Ordinal);
        captured.GetType().GetField("<Values>k__BackingField", Any)!.SetValue(captured, new ReadOnlyDictionary<string, string?>(oversized));
        Check("whole captured saved-map four-MiB bound also applies to zero controllers", Empty(Shared(bound.Fixture, bound.Inputs, bound.Prepared, bound.Application), LoadProcedureIssueCode.LimitExceeded));

        Console.WriteLine($"SharedProcedureContext: {checks} checks, {failures} failures"); return failures;
    }
}
