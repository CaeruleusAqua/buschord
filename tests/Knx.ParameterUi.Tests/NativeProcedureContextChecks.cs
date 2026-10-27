using System.Collections;
using System.Reflection;
using System.Xml.Linq;
using Knx;

internal static class NativeProcedureContextChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private static readonly LoadProcedureRequest Request = new("all", LoadProcedureAccess.Remote, LoadParts.Full);
    private static T Fact<T>(object value, string name) => (T)value.GetType().GetProperty(name, Any)!.GetValue(value)!;
    private static XElement Program(XDocument doc) => doc.Descendants(Ns + "ApplicationProgram").Single();
    private static XElement Static(XDocument doc) => Program(doc).Element(Ns + "Static")!;
    private static XElement Dynamic(XDocument doc) => Program(doc).Element(Ns + "Dynamic")!;
    private static XElement Placement(string reference) => new(Ns + "ParameterRefRef", new XAttribute("RefId", reference));
    private static XElement Choose(string reference, string test, params XElement[] body)
        => new(Ns + "choose", new XAttribute("ParamRefId", reference), new XElement(Ns + "when", new XAttribute("test", test), body));
    private static XDocument App(string? controller = "R")
    {
        var doc = ArchivedImageSourceChecks.App(); Program(doc).SetAttributeValue("LoadProcedureStyle", "ProductProcedure");
        var control = new XElement(Ns + "LdCtrlWriteProp", new XAttribute("ObjIdx", "4"), new XAttribute("PropId", "27"), new XAttribute("Verify", "false"), new XAttribute("InlineData", "11"));
        Static(doc).Add(new XElement(Ns + "LoadProcedures", new XElement(Ns + "LoadProcedure",
            new XElement(Ns + "LdCtrlConnect"), controller is null ? control : Choose(controller, "1", control), new XElement(Ns + "LdCtrlDisconnect"))));
        return doc;
    }
    private static void AddReference(XDocument doc, string id, string parameter, string value = "1")
    {
        Static(doc).Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", parameter), new XAttribute("ParameterType", "T"), new XAttribute("Value", value)));
        Static(doc).Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", id), new XAttribute("RefId", parameter)));
    }
    private static XDocument AliasApp()
    {
        var doc = App("R2"); AddReference(doc, "R2", "P2", "2");
        Static(doc).Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "R3"), new XAttribute("RefId", "P2"), new XAttribute("Access", "None")));
        Dynamic(doc).Add(Choose("R", "0", Placement("R2")), Choose("R", "1", Placement("R3")));
        return doc;
    }
    private sealed record Capture(ArchivedImageSourceChecks.Fixture Fixture, object Inputs, object Prepared, object Context);
    private static object Inputs(ArchivedImageSourceChecks.Fixture f)
        => typeof(ArchivedImageSources).GetMethod("ReadInputs", Hidden)!.Invoke(null, [f.Project, f.Device])!;
    private static object Prepare(object inputs)
        => typeof(LoadProcedureCompiler).GetMethod("PrepareResolved", Hidden)!.Invoke(null, [inputs, Request])!;
    private static object Context(ArchivedImageSourceChecks.Fixture f, object inputs, object prepared)
        => typeof(Project).Assembly.GetType("Knx.NativeProcedureEvaluationContext", true)!.GetMethod("Create", Hidden)!.Invoke(null, [f.Project, f.Device, inputs, prepared])!;
    private static Capture Load(string directory, XDocument? doc = null, Action<ArchivedImageSourceChecks.Fixture>? edit = null)
    {
        var f = ArchivedImageSourceChecks.Load(directory, app: doc ?? App()); edit?.Invoke(f);
        var inputs = Inputs(f); var prepared = Prepare(inputs); return new(f, inputs, prepared, Context(f, inputs, prepared));
    }
    private static object Controller(Capture c, string id) => ((IEnumerable)Fact<object>(c.Context, "Controllers")).Cast<object>()
        .Where(p => Fact<string>(p, "Key") == id).Select(p => Fact<object>(p, "Value")).Single();
    private static bool Current(Capture c, object? prepared = null)
        => (bool)c.Context.GetType().GetMethod("MatchesCurrent", Any)!.Invoke(c.Context, [prepared ?? c.Prepared])!;
    private static LoadProcedurePlan Compile(Capture c, object? context = null)
        => (LoadProcedurePlan)typeof(LoadProcedureCompiler).GetMethod("CompileResolved", Hidden)!.Invoke(null, [c.Prepared, context ?? c.Context])!;
    private static IReadOnlyList<LoadProcedureIssue> Issues(Capture c)
        => Fact<IReadOnlyList<LoadProcedureIssue>>(Fact<bool>(c.Prepared, "IsAvailable") ? c.Context : c.Prepared, "Issues");

    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "NativeProcedureContext " + name); if (!ok) failures++; }
        void Refused(string name, XDocument doc, LoadProcedureIssueCode code, Action<ArchivedImageSourceChecks.Fixture>? edit = null)
        {
            var c = Load(directory, doc, edit); var issues = Issues(c);
            Check(name, !Fact<bool>(c.Context, "IsAvailable") && Fact<string?>(c.Context, "Fingerprint") is null
                && ((IEnumerable)Fact<object>(c.Context, "Controllers")).Cast<object>().Count() == 0 && issues.Count == 1 && issues[0].Code == code);
        }
        var basic = Load(directory); var row = Controller(basic, "R");
        Check("root source/default and exact normal owner mint an active integer", Fact<bool>(basic.Context, "IsAvailable")
            && Fact<int>(row, "EverDeclaredOwnerCount") == 1 && Fact<int>(row, "ActiveNormalOwnerCount") == 1 && Fact<long?>(row, "ActiveValue") == 1);
        Check("fresh context compiles with its sealed prepared graph", Current(basic) && Compile(basic).IsAvailable);
        var readOnly = App(); readOnly.Descendants(Ns + "ParameterRef").Single().SetAttributeValue("Access", "None");
        Check("editor access does not deactivate an actual normal-reference owner", Fact<bool>(Controller(Load(directory, readOnly), "R"), "Active"));
        Check("context exposes immutable detached controller facts", Fact<object>(basic.Context, "Controllers") is not IDictionary dictionary || dictionary.IsReadOnly);
        Check("no snapshot or runtime parameter exists in the context API", basic.Context.GetType().GetMethods(Hidden).Single(m => m.Name == "Create").GetParameters().Length == 4
            && basic.Context.GetType().GetFields(Any).All(f => f.FieldType != typeof(ApplicationSnapshot) && !typeof(IParameterScriptRuntime).IsAssignableFrom(f.FieldType)));
        var sourceBytes = File.ReadAllBytes(basic.Fixture.Path); var xml = Knxproj.ToXml(basic.Fixture.Project).ToString(SaveOptions.DisableFormatting);
        var undo = new UndoStack(); undo.Reset(basic.Fixture.Project); var undoIndex = undo.Index; var revision = basic.Fixture.Project.ApplicationDataRevision;
        _ = Compile(basic); _ = Current(basic);
        Check("inspection and compilation preserve archive/model/revision/history", File.ReadAllBytes(basic.Fixture.Path).SequenceEqual(sourceBytes)
            && Knxproj.ToXml(basic.Fixture.Project).ToString(SaveOptions.DisableFormatting) == xml && revision == basic.Fixture.Project.ApplicationDataRevision && undo.Index == undoIndex && !undo.IsDirty);
        var anotherPrepared = Prepare(basic.Inputs);
        Check("equivalent but separately sealed prepared graph is not context authority", !Current(basic, anotherPrepared));
        var another = Load(directory);
        Check("foreign live project/context pair is refused by compiler", !Compile(another, basic.Context).IsAvailable);
        basic.Fixture.Device.ParameterValues["R"] = "2";
        Check("saved-value mutation invalidates context without a wrapper check", !Current(basic));
        var stale = Compile(basic);
        Check("compiler itself rejects stale current values atomically", !stale.IsAvailable && stale.Controls.Count == 0 && stale.WitnessHash is null && stale.Issues.Any(i => i.Code == LoadProcedureIssueCode.InputsChanged));

        var inactive = Load(directory, AliasApp(), f => f.Device.ParameterValues["R"] = "1"); var inactiveRow = Controller(inactive, "R2");
        Check("exact inactive alias has ever-owner but no active value", Fact<bool>(inactive.Context, "IsAvailable") && Fact<int>(inactiveRow, "EverDeclaredOwnerCount") == 1
            && Fact<int>(inactiveRow, "ActiveNormalOwnerCount") == 0 && !Fact<bool>(inactiveRow, "Active") && Fact<long?>(inactiveRow, "ActiveValue") is null);
        Check("another active alias does not activate requested ref or enter its controller set", ((IEnumerable)Fact<object>(inactive.Context, "Controllers")).Cast<object>().All(p => Fact<string>(p, "Key") != "R3")
            && Compile(inactive).IsAvailable && Compile(inactive).Controls.Count == 2);
        var active = Load(directory, AliasApp(), f => { f.Device.ParameterValues["R"] = "0"; f.Device.ParameterValues["R2"] = "1"; });
        Check("the requested alias activates only under its own source guard", Fact<int>(Controller(active, "R2"), "ActiveNormalOwnerCount") == 1 && Compile(active).Controls.Count == 3);
        var referenceDefault = App(); referenceDefault.Descendants(Ns + "ParameterRef").Single().SetAttributeValue("Value", "2");
        var referenceResult = Load(directory, referenceDefault);
        Check("explicit native reference override is a value source", Fact<long?>(Controller(referenceResult, "R"), "ActiveValue") == 2);
        var savedOverride = Load(directory, referenceDefault, f => f.Device.ParameterValues["R"] = "1");
        Check("current exact saved value overrides declared reference value", Fact<long?>(Controller(savedOverride, "R"), "ActiveValue") == 1);
        var absentDefault = App(); var defaultParameter = absentDefault.Descendants(Ns + "Parameter").Single(); defaultParameter.Attribute("Value")!.Remove(); defaultParameter.SetAttributeValue("DefaultValue", "1");
        Refused("legacy fallback does not invent a declared raw value", absentDefault, LoadProcedureIssueCode.InvalidControllerValue);
        var qualified = App(); var fullRef = "M-0001_A-0100-10-0000_R";
        foreach (var attr in qualified.Descendants().Attributes().Where(a => a.Value == "R").ToArray()) attr.Value = fullRef;
        Refused("conflicting full and known-local saved keys are ambiguous", qualified, LoadProcedureIssueCode.InvalidControllerValue,
            f => { f.Device.ParameterValues[fullRef] = "1"; f.Device.ParameterValues["R"] = "2"; });
        var never = App(); Dynamic(never).RemoveNodes();
        Refused("saved/static-visible value is not a never-placed owner", never, LoadProcedureIssueCode.NeverPlacedController, f => f.Device.ParameterValues["R"] = "1");
        var defaults = App("R2"); AddReference(defaults, "R2", "P2");
        Dynamic(defaults).Add(new XElement(Ns + "choose", new XAttribute("ParamRefId", "R"),
            new XElement(Ns + "when", new XAttribute("test", "1"), new XElement(Ns + "ParameterSeparator")),
            new XElement(Ns + "when", new XAttribute("default", "true"), Placement("R2"))));
        var suppressed = Load(directory, defaults);
        Check("matching sibling without a needed owner suppresses default", Fact<bool>(suppressed.Context, "IsAvailable") && !Fact<bool>(Controller(suppressed, "R2"), "Active"));
        var fallback = Load(directory, defaults, f => f.Device.ParameterValues["R"] = "2");
        Check("sole default contributes an owner only when every sibling misses", Fact<bool>(Controller(fallback, "R2"), "Active"));
        var multi = App("R2"); AddReference(multi, "R2", "P2");
        Dynamic(multi).Add(new XElement(Ns + "choose", new XAttribute("ParamRefId", "R"),
            new XElement(Ns + "when", new XAttribute("test", "0 1"), Placement("R2")), new XElement(Ns + "when", new XAttribute("test", "1 2"), Placement("R2"))));
        var multiple = Load(directory, multi);
        Check("all matching branches contribute distinct normal owners", Fact<int>(Controller(multiple, "R2"), "EverDeclaredOwnerCount") == 2 && Fact<int>(Controller(multiple, "R2"), "ActiveNormalOwnerCount") == 2);
        var hidden = App("R4"); AddReference(hidden, "R2", "P2"); AddReference(hidden, "R4", "P4");
        Dynamic(hidden).Add(Choose("R", "0", Placement("R2")), new XElement(Ns + "choose", new XAttribute("ParamRefId", "R2"), new XElement(Ns + "when", new XAttribute("default", "true"), Placement("R4"))));
        var hiddenResult = Load(directory, hidden);
        Check("inactive controller skips its default despite a saved raw value", Fact<bool>(hiddenResult.Context, "IsAvailable") && !Fact<bool>(Controller(hiddenResult, "R4"), "Active"));
        var cycle = App(); Dynamic(cycle).RemoveNodes(); Dynamic(cycle).Add(Choose("R", "1", Placement("R")));
        Refused("cyclic owner/controller graph has no partial context", cycle, LoadProcedureIssueCode.ActivationCycle);
        var aliasWriter = AliasApp(); Dynamic(aliasWriter).Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "R3"), new XAttribute("Value", "1")));
        Refused("writer alias taints a relevant value without sharing activity", aliasWriter, LoadProcedureIssueCode.UnsupportedActivationDependency);
        var assignSource = App(); AddReference(assignSource, "Outside", "OutsideP"); Dynamic(assignSource).Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "Outside"), new XAttribute("SourceParamRefRef", "R")));
        Refused("relevant assignment source is a dependency too", assignSource, LoadProcedureIssueCode.UnsupportedActivationDependency);
        var unrelated = App(); AddReference(unrelated, "Other", "OtherP"); Dynamic(unrelated).Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "Other"), new XAttribute("Value", "2")));
        Check("valid unrelated assignment does not contaminate controller cone", Fact<bool>(Load(directory, unrelated).Context, "IsAvailable"));
        var unrelatedEmpty = new XDocument(unrelated); unrelatedEmpty.Descendants(Ns + "Assign").Single().SetAttributeValue("Value", "");
        Check("unrelated empty literal is retained without evaluating its value", Fact<bool>(Load(directory, unrelatedEmpty).Context, "IsAvailable"));
        var dualSource = new XDocument(unrelated); AddReference(dualSource, "Source", "SourceP");
        dualSource.Descendants(Ns + "Assign").Single().SetAttributeValue("SourceParamRefRef", "Source");
        Refused("simultaneous source and literal assignment is outside this profile", dualSource, LoadProcedureIssueCode.UnsupportedActivationDependency);
        var badAssign = App(); Dynamic(badAssign).Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "missing"), new XAttribute("Value", "1")));
        Refused("unrelated assignment references are still globally validated", badAssign, LoadProcedureIssueCode.InvalidControllerDeclaration);
        var badPlacement = App(); Dynamic(badPlacement).Add(Choose("R", "99", Placement("missing")));
        Refused("unknown unselected normal owner is not skipped", badPlacement, LoadProcedureIssueCode.UnknownController);
        var badObject = App(); Dynamic(badObject).Add(Choose("R", "99", new XElement(Ns + "ComObjectRefRef", new XAttribute("RefId", "missing"))));
        Refused("unknown unselected object reference is still validated", badObject, LoadProcedureIssueCode.InvalidControllerDeclaration);
        var unused = App(); AddReference(unused, "Unused", "UnusedP");
        Static(unused).Element(Ns + "LoadProcedures")!.Add(new XElement(Ns + "LoadProcedure", new XAttribute("MergeId", "7"), Choose("Unused", "1", new XElement(Ns + "LdCtrlConnect"))));
        Check("unused group declarations do not require a never-placed activity fact", Fact<bool>(Load(directory, unused).Context, "IsAvailable"));
        var emptyRange = new XDocument(unused);
        Static(emptyRange).Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "EmptyRangeType"),
            new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", "8"), new XAttribute("minInclusive", "300"), new XAttribute("maxInclusive", "400"))));
        emptyRange.Descendants(Ns + "Parameter").Single(p => (string?)p.Attribute("Id") == "UnusedP").SetAttributeValue("ParameterType", "EmptyRangeType");
        var impossible = Load(directory, emptyRange);
        Check("unused controller range with no representable value fails before context mint", !Fact<bool>(impossible.Prepared, "IsAvailable")
            && Issues(impossible).Single().Code == LoadProcedureIssueCode.InvalidControllerDeclaration);
        var calculations = App(); Static(calculations).Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + "ParameterCalculation", new XAttribute("Language", "JavaScript"))));
        Refused("activity mode explicitly refuses calculation declarations", calculations, LoadProcedureIssueCode.UnsupportedActivationDependency);
        foreach (var element in new[] { new XElement(Ns + "Module", new XAttribute("RefId", "MD")), new XElement(Ns + "Repeat", new XAttribute("Count", "0")), new XElement(Ns + "Alien") })
        {
            var doc = App(); Dynamic(doc).Add(Choose("R", "99", element));
            Refused("unselected dynamic contributor remains unsupported " + element.Name.LocalName, doc, LoadProcedureIssueCode.UnsupportedStructureActivation);
        }
        foreach (var text in new[] { " 1", "1 ", "1  2", "\t>=1", ">= 1", "1.0", "==1" })
        {
            var doc = App(); Dynamic(doc).Add(Choose("R", text, new XElement(Ns + "ParameterSeparator")));
            Refused("strict unselected condition grammar " + text.Replace('\t', '_'), doc, LoadProcedureIssueCode.UnsupportedActivationCondition);
        }
        foreach (var marker in new[] { "true", "1" })
        {
            var doc = App(); Dynamic(doc).Add(new XElement(Ns + "choose", new XAttribute("ParamRefId", "R"), new XElement(Ns + "when", new XAttribute("default", marker), new XAttribute("test", "0"))));
            Refused("default/test unsupported combination " + marker, doc, LoadProcedureIssueCode.UnsupportedActivationCondition);
        }
        var falseDefault = App(); AddReference(falseDefault, "Other", "OtherP"); Dynamic(falseDefault).Add(new XElement(Ns + "choose", new XAttribute("ParamRefId", "R"),
            new XElement(Ns + "when", new XAttribute("default", "false"), new XAttribute("test", "1"), Placement("Other"))));
        Check("false-default with an ordinary test stays a tested branch", Fact<bool>(Load(directory, falseDefault).Context, "IsAvailable"));
        foreach (var edit in new Action<XDocument>[]
        {
            d => d.Descendants(Ns + "TypeNumber").Single().Attribute("minInclusive")!.Remove(),
            d => d.Descendants(Ns + "TypeNumber").Single().SetAttributeValue("SizeInBit", "0"),
            d => d.Descendants(Ns + "Parameter").Single().SetAttributeValue("BaseValue", "1"),
            d => d.Descendants(Ns + "ParameterRef").Single().SetAttributeValue("ParameterType", "T"),
            d => { var p = d.Descendants(Ns + "Parameter").Single(); p.SetAttributeValue("ParameterTypeId", "T"); p.Attribute("ParameterType")!.Remove(); },
            d => Static(d).Element(Ns + "ParameterRefs")!.Add(new XElement(d.Descendants(Ns + "ParameterRef").Single())),
        })
        { var doc = App(); edit(doc); Refused("native controller pairing/type requirement", doc, LoadProcedureIssueCode.InvalidControllerDeclaration); }
        foreach (var value in new[] { "01", "+1", "1 ", "-1", "256" })
            Refused("canonical bounded saved value " + value, App(), LoadProcedureIssueCode.InvalidControllerValue, f => f.Device.ParameterValues["R"] = value);
        var enumeration = App(); var type = enumeration.Descendants(Ns + "ParameterType").Single();
        type.RemoveNodes(); type.Add(new XElement(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", "64"),
            new XElement(Ns + "Enumeration", new XAttribute("Id", "E0"), new XAttribute("Value", "0")), new XElement(Ns + "Enumeration", new XAttribute("Id", "E1"), new XAttribute("Value", "1"))));
        Check("logical enum width is not narrowed to scalar byte-codec width", Fact<bool>(Load(directory, enumeration).Context, "IsAvailable"));
        var excess = new XDocument(enumeration); excess.Descendants(Ns + "TypeRestriction").Single().ReplaceNodes(Enumerable.Range(0, 4097).Select(i => new XElement(Ns + "Enumeration", new XAttribute("Id", "E" + i), new XAttribute("Value", i))));
        Refused("choice budget enforced before typed evaluation", excess, LoadProcedureIssueCode.LimitExceeded);
        foreach (var name in new[] { "Broken", "DownloadInfoIncomplete", "RequiresExternalSoftware" })
        { var doc = App(null); Program(doc).SetAttributeValue(name, "true"); Refused("primitive source still respects " + name, doc, LoadProcedureIssueCode.ExternalConfigurationUnaccounted); }
        foreach (var name in new[] { "DCA", "EtsUiPlugin", "EtsDataHandler" })
        { var doc = App(null); Static(doc).Add(new XElement(Ns + "Extension", new XElement(Ns + name))); Refused("primitive external configuration " + name, doc, LoadProcedureIssueCode.ExternalConfigurationUnaccounted); }
        var plugin = App(null); plugin.Descendants(Ns + "ParameterType").Single().SetAttributeValue("Plugin", "adapter");
        Refused("primitive type plugin remains unaccounted", plugin, LoadProcedureIssueCode.ExternalConfigurationUnaccounted);
        Refused("named device binary state is not an ordinary raw controller", App(null), LoadProcedureIssueCode.UnaccountedBinaryData, f => f.Device.BinaryData.Add("config", [1]));
        Refused("opaque device binary state is not ignored", App(null), LoadProcedureIssueCode.UnaccountedBinaryData,
            f => typeof(DeviceInstance).GetProperty("HasOpaqueBinaryData")!.SetValue(f.Device, true));
        var primitive = App(null); Static(primitive).Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + "ParameterCalculation")));
        Dynamic(primitive).Add(new XElement(Ns + "Module", new XAttribute("RefId", "MD")));
        var plain = Load(directory, primitive);
        Check("zero-controller context makes no activity claim or module/calculation restriction", Fact<bool>(plain.Context, "IsAvailable") && ((IEnumerable)Fact<object>(plain.Context, "Controllers")).Cast<object>().Count() == 0 && Compile(plain).IsAvailable);
        var appTaint = Load(directory); Fact<XDocument>(appTaint.Inputs, "Application").Descendants(Ns + "Parameter").Single().SetAttributeValue("Value", "2");
        Check("detached input document mutation invalidates context and compiler", !Current(appTaint) && !Compile(appTaint).IsAvailable);
        var identity = Load(directory); identity.Fixture.Device.Address = new(1, 1, 2);
        Check("live device identity changes invalidate the compiler locally", !Current(identity) && !Compile(identity).IsAvailable);
        var binaryTaint = Load(directory); binaryTaint.Fixture.Device.BinaryData.Add("later", [1]);
        Check("binary state added after capture invalidates the compiler locally", !Current(binaryTaint) && !Compile(binaryTaint).IsAvailable);
        var archiveTaint = Load(directory); var newArchive = Load(directory);
        typeof(Project).GetProperty("ArchiveState", Any)!.SetValue(archiveTaint.Fixture.Project, typeof(Project).GetProperty("ArchiveState", Any)!.GetValue(newArchive.Fixture.Project));
        Check("archive revision replacement invalidates a previously fresh capsule", !Current(archiveTaint) && !Compile(archiveTaint).IsAvailable);
        var reloaded = Knxproj.Load(another.Fixture.Path);
        Check("reload identity cannot reuse a prior context", !(bool)another.Context.GetType().GetMethod("MatchesInputs", Any)!.Invoke(another.Context,
            [reloaded, ProjectQuery.DeviceList(reloaded).Single(), Inputs(another.Fixture), another.Prepared])!);
        var oversaved = Load(directory, App(), f => f.Device.ParameterValues["oversized"] = new string('1', 65537));
        Check("saved value budget returns no partial facts", !Fact<bool>(oversaved.Context, "IsAvailable") && Issues(oversaved).Any(i => i.Code == LoadProcedureIssueCode.LimitExceeded));
        Console.WriteLine($"NativeProcedureContext: {checks} checks, {failures} failures"); return failures;
    }
}
