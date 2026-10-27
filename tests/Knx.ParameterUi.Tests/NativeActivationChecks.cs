using System.Collections;
using System.Reflection;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class NativeActivationChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    internal static T Fact<T>(object value, string name) => (T)value.GetType().GetProperty(name, Any)!.GetValue(value)!;
    internal static object Inputs(ArchivedImageSourceChecks.Fixture fixture) => typeof(ArchivedImageSources).GetMethod("ReadInputs", Hidden)!.Invoke(null, [fixture.Project, fixture.Device])!;
    internal static object Context(ArchivedImageSourceChecks.Fixture fixture, IParameterScriptRuntime? scripts = null, string[]? additional = null)
        => ContextWith(fixture, Inputs(fixture), scripts, additional);
    private static object ContextWith(ArchivedImageSourceChecks.Fixture fixture, object inputs, IParameterScriptRuntime? scripts = null, string[]? additional = null)
        => typeof(Project).Assembly.GetType("Knx.NativeApplicationEvaluationContext", true)!.GetMethod("Create", Hidden)!.Invoke(null, [fixture.Project, fixture.Device, inputs, additional, scripts])!;
    private static object Witness(ArchivedImageSourceChecks.Fixture fixture, object context, object? inputs = null)
        => typeof(Project).Assembly.GetType("Knx.NativeCommunicationWitness", true)!.GetMethod("CreateFromContext", Hidden)!
            .Invoke(null, [fixture.Project, fixture.Device, inputs ?? Inputs(fixture), context])!;
    private static bool Matches(ArchivedImageSourceChecks.Fixture fixture, object context, object? inputs = null)
        => (bool)context.GetType().GetMethod("MatchesInputs", Any)!.Invoke(context, [fixture.Project, fixture.Device, inputs ?? Inputs(fixture)])!;
    private static object Controller(object context, string id) => ((IEnumerable)Fact<object>(Fact<object>(context, "Activation"), "Controllers")).Cast<object>()
        .Where(pair => Fact<string>(pair, "Key") == id).Select(pair => Fact<object>(pair, "Value")).Single();
    private static XElement Program(XDocument doc) => doc.Descendants(Ns + "ApplicationProgram").Single();
    private static XElement Dynamic(XDocument doc) => Program(doc).Element(Ns + "Dynamic")!;
    private static ArchivedImageSourceChecks.Fixture Load(string directory, XDocument? doc = null) => ArchivedImageSourceChecks.Load(directory, app: doc ?? NativeCommunicationChecks.App());
    private static void AddReference(XDocument doc, string id, string parameter, string value = "1")
    {
        var stat = Program(doc).Element(Ns + "Static")!;
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", parameter), new XAttribute("ParameterType", "T"), new XAttribute("Value", value)));
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", id), new XAttribute("RefId", parameter)));
    }
    private static XElement Placement(string id) => new(Ns + "ParameterRefRef", new XAttribute("RefId", id));
    private static XElement ObjectPlacement(string id = "C3") => new(Ns + "ComObjectRefRef", new XAttribute("RefId", id));
    private static XElement Choose(string id, string test, params XElement[] children) => new(Ns + "choose", new XAttribute("ParamRefId", id), new XElement(Ns + "when", new XAttribute("test", test), children));
    private static void UnrelatedCalculation(XDocument doc)
    {
        AddReference(doc, "L", "P-L"); AddReference(doc, "Out", "P-Out");
        Program(doc).Element(Ns + "Static")!.Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + "ParameterCalculation", new XAttribute("Language", "JavaScript"),
            new XElement(Ns + "LRTransformation", "output=input;"),
            new XElement(Ns + "LParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "L"), new XAttribute("AliasName", "input"))),
            new XElement(Ns + "RParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "Out"), new XAttribute("AliasName", "output"))))));
    }
    private sealed class Runtime(Action? callback = null) : IParameterScriptRuntime
    {
        internal int Calls;
        public IReadOnlyDictionary<string, object?> Execute(string source, IReadOnlyDictionary<string, object?> inputs, IReadOnlyList<string> outputs)
        { Calls++; callback?.Invoke(); return outputs.ToDictionary(id => id, _ => (object?)1d, StringComparer.Ordinal); }
    }
    private static XDocument Enumeration(int bits = 8, int count = 2)
    {
        var doc = NativeCommunicationChecks.App(); var type = doc.Descendants(Ns + "ParameterType").Single();
        type.RemoveNodes(); type.Add(new XElement(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", bits),
            Enumerable.Range(0, count).Select(i => new XElement(Ns + "Enumeration", new XAttribute("Id", "E-" + i), new XAttribute("Value", i), new XAttribute("Text", "Choice " + i)))));
        return doc;
    }
    private static XDocument ConditionalModules()
    {
        var doc = NativeCommunicationChecks.Modules(); var type = doc.Descendants(Ns + "ParameterType").Single();
        type.RemoveNodes(); type.Add(new XElement(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", "8"),
            Enumerable.Range(0, 3).Select(i => new XElement(Ns + "Enumeration", new XAttribute("Id", "E-" + i), new XAttribute("Value", i)))));
        var calls = Dynamic(doc).Elements(Ns + "Module").ToArray(); foreach (var call in calls) call.Remove();
        Dynamic(doc).Add(new XElement(Ns + "choose", new XAttribute("ParamRefId", "R"),
            new XElement(Ns + "when", new XAttribute("test", "1"), calls[0]), new XElement(Ns + "when", new XAttribute("test", "0"), calls[1])));
        return doc;
    }

    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "NativeActivation " + name); if (!ok) failures++; }
        void Refused(string name, XDocument doc, GeneratedTableIssueCode code, Action<ArchivedImageSourceChecks.Fixture>? edit = null, IParameterScriptRuntime? scripts = null)
        {
            var fixture = Load(directory, doc); edit?.Invoke(fixture); var context = Context(fixture, scripts); var witness = Witness(fixture, context);
            var issues = Fact<IReadOnlyList<GeneratedTableIssue>>(witness, "Issues");
            Check(name, !Fact<bool>(context, "IsAvailable") && Fact<string?>(context, "Fingerprint") is null
                && !Fact<bool>(witness, "IsAvailable") && Fact<string?>(witness, "Fingerprint") is null && issues.Count == 1 && issues[0].Code == code
                && ((IEnumerable)Fact<object>(witness, "Slots")).Cast<object>().Count() == 0 && ((IEnumerable)Fact<object>(witness, "Groups")).Cast<object>().Count() == 0
                && ((IEnumerable)Fact<object>(witness, "Associations")).Cast<object>().Count() == 0);
        }
        var f = Load(directory); var inputs = Inputs(f); var context = ContextWith(f, inputs); var source = ArchivedImageSources.Read(f.Project, f.Device);
        var model = Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting); var bytes = File.ReadAllBytes(f.Path); var revision = f.Project.ApplicationDataRevision;
        var undo = new UndoStack(); undo.Reset(f.Project); var undoIndex = undo.Index;
        Check("fresh ordinary native root controllers are available", Fact<bool>(context, "IsAvailable") && Fact<bool>(Controller(context, "R"), "Active")
            && Fact<int>(Controller(context, "R"), "NormalReferenceCount") == 1 && Fact<int>(Controller(context, "R"), "ValueAssignmentReferenceCount") == 0);
        Check("context retains exact source and selected mask profile hashes", Fact<string>(context, "ApplicationDefinitionHash") == source.ApplicationDefinitionHash
            && Fact<string>(context, "SourceFingerprint") == source.Binding?.Fingerprint && Fact<string>(context, "ResourceContextFingerprint") == source.ResourceContext?.Fingerprint);
        var witness = Witness(f, context, inputs);
        Check("checked shared context supplies complete communication facts", Fact<bool>(witness, "IsAvailable") && Matches(f, context, inputs));
        var frozen = (IDictionary<string, string?>)Fact<IReadOnlyDictionary<string, string?>>(context, "EffectiveValues"); var immutable = false;
        try { frozen["R"] = "0"; } catch (NotSupportedException) { immutable = true; }
        Check("owned effective values cannot be changed by a consumer", immutable && Fact<IReadOnlyDictionary<string, string?>>(context, "EffectiveValues").GetValueOrDefault("R") == "1");
        Check("fresh proof is stable and has no script replay dependency by default", Fact<string>(context, "Fingerprint") == Fact<string>(Context(f), "Fingerprint") && !Fact<bool>(context, "RequiresScripts"));
        Check("minting and consuming do not mutate model archive revision or undo", model == Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting)
            && bytes.AsSpan().SequenceEqual(File.ReadAllBytes(f.Path)) && revision == f.Project.ApplicationDataRevision && undoIndex == undo.Index);

        var twoOwners = NativeCommunicationChecks.App(); Dynamic(twoOwners).Add(Placement("R")); var two = Context(Load(directory, twoOwners));
        Check("two distinct placement owners share one active reference facade", Fact<bool>(two, "IsAvailable") && Fact<int>(Controller(two, "R"), "NormalReferenceCount") == 2);
        var hidden = NativeCommunicationChecks.App(); hidden.Descendants(Ns + "Parameter").Single().SetAttributeValue("Access", "None");
        var hiddenContext = Context(Load(directory, hidden));
        Check("native placement remains active when the UI editor hides its row", Fact<bool>(hiddenContext, "IsAvailable") && Fact<bool>(Controller(hiddenContext, "R"), "Active"));
        var none = NativeCommunicationChecks.App(); Dynamic(none).Elements(Ns + "choose").Remove(); Dynamic(none).Elements(Ns + "ParameterRefRef").Remove();
        var inactive = Context(Load(directory, none), additional: ["R"]);
        Check("known unplaced additional controller has no invented activity", Fact<bool>(inactive, "IsAvailable") && !Fact<bool>(Controller(inactive, "R"), "Active")
            && Fact<int>(Controller(inactive, "R"), "NormalReferenceCount") == 0);
        var noPlacement = NativeCommunicationChecks.App(); Dynamic(noPlacement).Elements(Ns + "ParameterRefRef").Remove();
        Refused("stored value cannot activate an unplaced controller", noPlacement, GeneratedTableIssueCode.ActivationMismatch, fixture => fixture.Device.ParameterValues["R"] = "1");
        var fallback = new XDocument(noPlacement); var branch = fallback.Descendants(Ns + "when").Single(); branch.Attribute("test")!.Remove(); branch.SetAttributeValue("default", "true");
        Refused("inactive controller skips default as well as tested branches", fallback, GeneratedTableIssueCode.ActivationMismatch);
        var overlap = NativeCommunicationChecks.App(); overlap.Descendants(Ns + "choose").Single().Add(new XElement(Ns + "when", new XAttribute("test", ">=1"), ObjectPlacement()));
        Refused("all matching native branches cannot masquerade as first match", overlap, GeneratedTableIssueCode.ActivationMismatch);
        var emptyOverlap = NativeCommunicationChecks.App(); emptyOverlap.Descendants(Ns + "choose").Single().Add(new XElement(Ns + "when", new XAttribute("test", ">=1")));
        Refused("selected source branch mismatch refuses even identical object payloads", emptyOverlap, GeneratedTableIssueCode.ActivationMismatch);
        var alias = NativeCommunicationChecks.App(); Program(alias).Element(Ns + "Static")!.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "R-alias"), new XAttribute("RefId", "P")));
        alias.Descendants(Ns + "choose").Single().SetAttributeValue("ParamRefId", "R-alias");
        Refused("definition aliases do not share native reference activity", alias, GeneratedTableIssueCode.ActivationMismatch);
        Dynamic(alias).Add(Placement("R-alias")); var placedAlias = Context(Load(directory, alias));
        Check("an independently placed alias has its own normal owner", Fact<bool>(placedAlias, "IsAvailable") && Fact<int>(Controller(placedAlias, "R-alias"), "NormalReferenceCount") == 1);
        var self = NativeCommunicationChecks.App(); var placement = Dynamic(self).Elements(Ns + "ParameterRefRef").Single(); placement.Remove(); self.Descendants(Ns + "when").Single().Add(placement);
        Refused("self activation cycles are not resolved by saved values", self, GeneratedTableIssueCode.ActivationCycle);
        var mutual = NativeCommunicationChecks.App(); AddReference(mutual, "S", "P-S"); Dynamic(mutual).RemoveNodes();
        Dynamic(mutual).Add(Choose("R", "1", Placement("S"), ObjectPlacement()), Choose("S", "1", Placement("R")));
        Refused("mutual native activity cycles refuse atomically", mutual, GeneratedTableIssueCode.ActivationCycle);
        var parent = NativeCommunicationChecks.App(); AddReference(parent, "S", "P-S"); var current = parent.Descendants(Ns + "choose").Single(); current.Remove();
        Dynamic(parent).Add(Choose("S", "1", current));
        Refused("active child controller cannot bypass an inactive parent", parent, GeneratedTableIssueCode.ActivationMismatch);

        foreach (var (name, test) in new[] { ("leading whitespace", " 1"), ("trailing whitespace", "1 "), ("double whitespace", "0  1"), ("comparison whitespace", ">= 1"),
            ("tab whitespace", "0\t1"), ("decimal comparison", ">=0.5"), ("mixed comparisons", ">0 <2"), ("label text", "Enabled"), ("plus condition", "+1"), ("double equals", "==1"), ("overflow condition", "9223372036854775808") })
        { var doc = NativeCommunicationChecks.App(); doc.Descendants(Ns + "when").Single().SetAttributeValue("test", test); Refused(name + " is outside the strict condition profile", doc, GeneratedTableIssueCode.UnsupportedActivationCondition); }
        var signedSet = NativeCommunicationChecks.App(); signedSet.Descendants(Ns + "when").Single().SetAttributeValue("test", "-1 1");
        Check("signed integer alternatives use exact numeric membership", Fact<bool>(Context(Load(directory, signedSet)), "IsAvailable"));
        var badDefault = NativeCommunicationChecks.App(); badDefault.Descendants(Ns + "choose").Single().Add(new XElement(Ns + "when", new XAttribute("default", "true")), new XElement(Ns + "when", new XAttribute("default", "true")));
        Refused("two default branches have no implicit tie break", badDefault, GeneratedTableIssueCode.UnsupportedActivationCondition);
        foreach (var mutation in new (string Name, Action<XDocument> Edit)[]
        {
            ("number missing minimum", d => d.Descendants(Ns + "TypeNumber").Single().Attribute("minInclusive")!.Remove()),
            ("number missing maximum", d => d.Descendants(Ns + "TypeNumber").Single().Attribute("maxInclusive")!.Remove()),
            ("number unordered bounds", d => d.Descendants(Ns + "TypeNumber").Single().SetAttributeValue("minInclusive", "2")),
            ("number unknown width", d => d.Descendants(Ns + "TypeNumber").Single().Attribute("SizeInBit")!.Remove()),
            ("number invalid width", d => d.Descendants(Ns + "TypeNumber").Single().SetAttributeValue("SizeInBit", "33")),
            ("number foreign semantic attribute", d => d.Descendants(Ns + "TypeNumber").Single().SetAttributeValue(XName.Get("Type", "urn:foreign"), "unsignedInt")),
        })
        { var doc = NativeCommunicationChecks.App(); mutation.Edit(doc); Refused(mutation.Name, doc, GeneratedTableIssueCode.UnsupportedActivationCondition); }
        foreach (var mutation in new (string Name, Action<XDocument> Edit)[]
        {
            ("enum missing width", d => d.Descendants(Ns + "TypeRestriction").Single().Attribute("SizeInBit")!.Remove()),
            ("enum negative member", d => d.Descendants(Ns + "Enumeration").First().SetAttributeValue("Value", "-1")),
            ("enum overflow member", d => d.Descendants(Ns + "Enumeration").First().SetAttributeValue("Value", "4294967296")),
            ("enum missing member identity", d => d.Descendants(Ns + "Enumeration").First().Attribute("Id")!.Remove()),
            ("enum duplicate value", d => d.Descendants(Ns + "Enumeration").Last().SetAttributeValue("Value", "0")),
            ("enum duplicate identity", d => d.Descendants(Ns + "Enumeration").Last().SetAttributeValue("Id", "E-0")),
            ("enum binary value", d => d.Descendants(Ns + "Enumeration").First().SetAttributeValue("BinaryValue", "")),
            ("enum non-member child", d => d.Descendants(Ns + "TypeRestriction").Single().Add(new XElement(Ns + "Unknown"))),
        })
        { var doc = Enumeration(); mutation.Edit(doc); Refused(mutation.Name, doc, GeneratedTableIssueCode.UnsupportedActivationCondition); }
        var wide = Enumeration(64); var wideContext = Context(Load(directory, wide));
        Check("logical enumeration width is separate from a byte-codec limit", Fact<bool>(wideContext, "IsAvailable"));
        Check("4096 native choices are bounded and usable", Fact<bool>(Context(Load(directory, Enumeration(16, 4096))), "IsAvailable"));
        var overChoices = Enumeration(16, 4097); var neverCalled = new Runtime();
        Refused("selected choice limit precedes optional evaluation", overChoices, GeneratedTableIssueCode.LimitExceeded, scripts: neverCalled);
        Check("choice preflight never invokes the supplied runtime", neverCalled.Calls == 0);

        foreach (var sourceSide in new[] { false, true })
        {
            var doc = NativeCommunicationChecks.App(); AddReference(doc, "Other", "P-Other");
            Dynamic(doc).Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", sourceSide ? "Other" : "R"),
                new XAttribute("SourceParamRefRef", sourceSide ? "R" : "Other")));
            Refused((sourceSide ? "AssignSourceNormalRef" : "AssignTargetValueReference") + " requires a native dependency profile", doc, GeneratedTableIssueCode.UnsupportedActivationDependency);
        }
        var calc = NativeCommunicationChecks.App(); UnrelatedCalculation(calc); calc.Descendants(Ns + "RParameters").Single().Elements().Single().SetAttributeValue("RefId", "R");
        var disabledRuntime = new Runtime(); Refused("derived controller cannot use old GUI calculation state", calc, GeneratedTableIssueCode.UnsupportedActivationDependency, scripts: disabledRuntime);
        Check("derived-state refusal precedes supplied script execution", disabledRuntime.Calls == 0);
        var unrelated = NativeCommunicationChecks.App(); UnrelatedCalculation(unrelated); var unrelatedFixture = Load(directory, unrelated);
        Check("bounded unrelated calculations do not require implicit execution", Fact<bool>(Context(unrelatedFixture), "IsAvailable"));
        var runtime = new Runtime(); var shared = Context(unrelatedFixture, runtime); var calls = runtime.Calls;
        var first = Witness(unrelatedFixture, shared); var second = Witness(unrelatedFixture, shared);
        Check("one fresh context is reused without a second script evaluation", Fact<bool>(shared, "IsAvailable") && Fact<bool>(first, "IsAvailable")
            && Fact<bool>(second, "IsAvailable") && calls > 0 && runtime.Calls == calls && Fact<string>(first, "Fingerprint") == Fact<string>(second, "Fingerprint"));
        Check("explicit capability participation is a replay requirement", Fact<bool>(shared, "RequiresScripts") && Fact<bool>(first, "RequiresScripts"));
        var malformed = new XDocument(unrelated); malformed.Descendants(Ns + "when").Single().SetAttributeValue("test", "0  2"); var noCallback = new Runtime();
        Refused("malformed unselected source condition is checked before runtime", malformed, GeneratedTableIssueCode.UnsupportedActivationCondition, scripts: noCallback);
        Check("condition preflight never invokes the optional callback", noCallback.Calls == 0);
        var mutationFixture = Load(directory, unrelated); var mutate = new Runtime(() => mutationFixture.Device.ParameterValues["R"] = "0");
        var mutatingContext = Context(mutationFixture, mutate);
        Check("host mutation during explicit runtime cannot mint a context", !Fact<bool>(mutatingContext, "IsAvailable")
            && Fact<IReadOnlyList<GeneratedTableIssue>>(mutatingContext, "Issues")[0].Code == GeneratedTableIssueCode.InputsChanged);

        f = Load(directory); inputs = Inputs(f); context = ContextWith(f, inputs);
        var other = Load(directory); Check("a foreign project and device cannot consume the context", !Matches(other, context));
        f.Device.ParameterValues["R"] = "0";
        Check("current parameter edits stale the context and witness atomically", !Matches(f, context, inputs) && !Fact<bool>(Witness(f, context, inputs), "IsAvailable"));
        f = Load(directory); inputs = Inputs(f); context = ContextWith(f, inputs);
        Fact<XDocument>(inputs, "Application").Descendants(Ns + "when").Single().SetAttributeValue("test", "0");
        Check("wrong current application cannot reuse a matching manifest object", !Matches(f, context, inputs) && !Fact<bool>(Witness(f, context, inputs), "IsAvailable"));
        f = Load(directory); inputs = Inputs(f); context = ContextWith(f, inputs);
        Fact<XDocument>(inputs, "Master").Descendants().First(e => e.Name.LocalName == "Resource").SetAttributeValue("Name", "changed");
        Check("wrong archived master cannot reuse the captured profile", !Matches(f, context, inputs));
        f = Load(directory); inputs = Inputs(f); context = ContextWith(f, inputs);
        var owned = Fact<XElement>(context, "OriginalApplication"); owned.Element(Ns + "Dynamic")!.Elements(Ns + "choose").Single().SetAttributeValue("ParamRefId", "unknown");
        Check("the original owned graph is rechecked between consumers", !Matches(f, context, inputs));
        f = Load(directory); inputs = Inputs(f); context = ContextWith(f, inputs);
        var expanded = typeof(DynamicEvaluation).GetProperty("ExpandedRoot", Any)!.GetValue(Fact<ApplicationSnapshot>(context, "Snapshot").Evaluation) as XElement;
        expanded!.Descendants(Ns + "ComObject").First().SetAttributeValue("ObjectSize", "2 Bytes");
        Check("expanded snapshot geometry cannot change after minting", !Matches(f, context, inputs));
        f = Load(directory); context = Context(f); var oldInputs = Inputs(f); var archive = typeof(Project).GetProperty("ArchiveState", Any)!;
        archive.SetValue(f.Project, archive.GetValue(Knxproj.Load(f.Path)));
        Check("an archive revision change rejects an otherwise identical context", !Matches(f, context, oldInputs));

        var module = NativeCommunicationChecks.Modules(); var definition = module.Descendants(Ns + "ModuleDef").Single();
        definition.Element(Ns + "Static")!.AddFirst(new XElement(Ns + "Parameters", new XElement(Ns + "Parameter", new XAttribute("Id", "MD_P"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "1"))),
            new XElement(Ns + "ParameterRefs", new XElement(Ns + "ParameterRef", new XAttribute("Id", "MD_R"), new XAttribute("RefId", "MD_P"))));
        var objectPlacement = definition.Element(Ns + "Dynamic")!.Elements().Single(); objectPlacement.Remove();
        definition.Element(Ns + "Dynamic")!.Add(Placement("MD_R"), Choose("MD_R", "1", objectPlacement));
        var modules = Context(Load(directory, module));
        var activeControllers = Fact<bool>(modules, "IsAvailable")
            ? ((IEnumerable)Fact<object>(Fact<object>(modules, "Activation"), "Controllers")).Cast<object>().Select(p => Fact<object>(p, "Value")).ToArray() : [];
        Check("literal module instances retain separate parameter-reference owners", Fact<bool>(modules, "IsAvailable") && activeControllers.Length == 2
            && activeControllers.All(c => Fact<int>(c, "NormalReferenceCount") == 1 && Fact<bool>(c, "Active"))
            && activeControllers.Select(c => Fact<string>(c, "RefId")).Distinct(StringComparer.Ordinal).Count() == 2);

        foreach (var defaultValue in new[] { "true", "1" })
        {
            var combination = new XDocument(unrelated); var when = combination.Descendants(Ns + "when").Single();
            when.SetAttributeValue("default", defaultValue); when.SetAttributeValue("test", defaultValue == "true" ? "1" : "0");
            var preflightRuntime = new Runtime();
            Refused("default " + defaultValue + " with test is outside the selection profile", combination, GeneratedTableIssueCode.UnsupportedActivationCondition, scripts: preflightRuntime);
            Check("combined default and test refuses before callback " + defaultValue, preflightRuntime.Calls == 0);
        }
        var expandedCombination = NativeCommunicationChecks.App(); expandedCombination.Descendants(Ns + "when").Single().SetAttributeValue("default", "true");
        var catalogType = typeof(Project).Assembly.GetType("Knx.NativeCommunicationOriginCatalog", true)!;
        var rawCatalog = catalogType.GetConstructor(Any, null, [typeof(XElement)], null)!.Invoke([expandedCombination.Root!]);
        catalogType.GetMethod("Stamp", Any)!.Invoke(rawCatalog, [expandedCombination.Root!]);
        var rawSnapshot = ApplicationSnapshot.Evaluate(expandedCombination, new Dictionary<string, string?>());
        var rawValues = rawSnapshot.Evaluation.Parameters.ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.Ordinal);
        var postProof = typeof(Project).Assembly.GetType("Knx.NativeActivationProof", true)!.GetMethod("Create", Hidden)!
            .Invoke(null, [rawCatalog, Program(expandedCombination), rawSnapshot, rawValues, Array.Empty<string>()])!;
        Check("expanded selection independently refuses combined default and test", !Fact<bool>(postProof, "IsAvailable")
            && Fact<IReadOnlyList<GeneratedTableIssue>>(postProof, "Issues")[0].Code == GeneratedTableIssueCode.UnsupportedActivationCondition
            && Fact<string?>(postProof, "Fingerprint") is null);
        var conditional = Load(directory, ConditionalModules()); var on = Context(conditional);
        Check("root native active branch selects only its original module call", Fact<bool>(on, "IsAvailable")
            && Fact<ApplicationSnapshot>(on, "Snapshot").ModuleInstances.Count == 1 && Fact<ApplicationSnapshot>(on, "Snapshot").ModuleInstances[0].RefId == "M1");
        Check("all structural selectors participate in the shared context", Fact<bool>(on, "IsAvailable")
            && Fact<IReadOnlySet<string>>(on, "AdditionalControllerReferences").SetEquals(["R"])
            && Fact<bool>(Controller(on, "R"), "Active") && Fact<int>(Controller(on, "R"), "NormalReferenceCount") == 1);
        var sourceBeforeModeEdit = ArchivedImageSources.Read(conditional.Project, conditional.Device).Binding!.Fingerprint;
        conditional.Device.ParameterValues["R"] = "0"; var off = Context(conditional);
        Check("fresh current selector changes the complete expected module set", Fact<bool>(off, "IsAvailable")
            && Fact<ApplicationSnapshot>(off, "Snapshot").ModuleInstances.Count == 1 && Fact<ApplicationSnapshot>(off, "Snapshot").ModuleInstances[0].RefId == "M2"
            && Fact<string>(off, "Fingerprint") != Fact<string>(on, "Fingerprint") && !Matches(conditional, on));
        Check("mode edits retain exact archived source provenance", ArchivedImageSources.Read(conditional.Project, conditional.Device).Binding!.Fingerprint == sourceBeforeModeEdit);
        conditional.Device.ParameterValues["R"] = "2"; var allInactive = Context(conditional);
        Check("all inactive calls still require the original root selector proof", Fact<bool>(allInactive, "IsAvailable")
            && Fact<ApplicationSnapshot>(allInactive, "Snapshot").ModuleInstances.Count == 0
            && Fact<IReadOnlySet<string>>(allInactive, "AdditionalControllerReferences").SetEquals(["R"]) && Fact<long>(Controller(allInactive, "R"), "Value") == 2);
        var firstProof = Fact<object>(on, "Activation");
        var proofBelongs = firstProof.GetType().GetMethod("BelongsTo", Any)!;
        Check("activation facts are sealed to their own catalog and snapshot", (bool)proofBelongs.Invoke(firstProof, [Fact<object>(on, "Catalog"), Fact<ApplicationSnapshot>(on, "Snapshot")])!
            && !(bool)proofBelongs.Invoke(firstProof, [Fact<object>(allInactive, "Catalog"), Fact<ApplicationSnapshot>(allInactive, "Snapshot")])!);
        var explicitSelector = Context(conditional, additional: ["R"]);
        Check("caller and structural selector sets combine without duplicate authority", Fact<bool>(explicitSelector, "IsAvailable")
            && Fact<IReadOnlySet<string>>(explicitSelector, "AdditionalControllerReferences").Count == 1);
        Console.WriteLine($"NativeActivation checks={checks} failures={failures}");
        return failures;
    }
}
