using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Knx;

internal static class ConditionalLoadProcedureChecks
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public;
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private static readonly LoadProcedureRequest Request = new("all", LoadProcedureAccess.Remote, LoadParts.Full);
    private sealed record Capture(ArchivedImageSourceChecks.Fixture Fixture, object Inputs, object Prepared, object Context);
    private static T Fact<T>(object value, string name) => NativeActivationChecks.Fact<T>(value, name);
    private static XElement Program(XDocument doc) => doc.Descendants(Ns + "ApplicationProgram").Single();
    private static XElement Static(XDocument doc) => Program(doc).Element(Ns + "Static")!;
    private static XElement Dynamic(XDocument doc) => Program(doc).Element(Ns + "Dynamic")!;
    private static XElement Control(string kind, params object[] content) => new(Ns + "LdCtrl" + kind, content);
    private static XElement Write(string data, string? condition = null) => Control("WriteProp", new XAttribute("ObjIdx", "4"),
        new XAttribute("PropId", "27"), new XAttribute("Verify", "false"), new XAttribute("InlineData", data),
        condition is null ? null! : new XAttribute("AppliesTo", condition));
    private static XElement Compare() => Control("CompareRelMem", new XAttribute("ObjIdx", "4"), new XAttribute("Offset", "932"),
        new XAttribute("Size", "1"), new XAttribute("InlineData", "FF"), new XAttribute("Mask", "FF"), new XAttribute("Invert", "true"),
        new XElement(Ns + "OnError", new XAttribute("Cause", "CompareMismatch"), new XAttribute("MessageRef", "Compare-message")));
    private static XElement Choose(string reference, params XElement[] branches) => new(Ns + "choose", new XAttribute("ParamRefId", reference), branches);
    private static XElement When(string test, params XElement[] controls) => new(Ns + "when", new XAttribute("test", test), controls);
    private static XElement Default(params XElement[] controls) => new(Ns + "when", new XAttribute("default", "true"), controls);
    private static XDocument App(params XElement[] controls)
    {
        var doc = ArchivedImageSourceChecks.App(); Program(doc).SetAttributeValue("LoadProcedureStyle", "ProductProcedure");
        Static(doc).Add(new XElement(Ns + "LoadProcedures", new XElement(Ns + "LoadProcedure", controls.Length == 0
            ? new[] { Control("Connect"), Choose("R", When("1", Write("11"))), Control("Disconnect") } : controls)));
        return doc;
    }
    private static XDocument Master(params XElement[] controls) => XDocument.Parse("<KNX xmlns='http://knx.org/xml/project/20'><MasterData><MaskVersions><MaskVersion Id='MV-0705' MaskVersion='1797'><HawkConfigurationData LegacyVersion='0'><Features><Feature Name='FirstAppObjectIdx' Value='5'/></Features><Procedures><Procedure ProcedureType='Load' ProcedureSubType='all' Access='remote'/></Procedures></HawkConfigurationData></MaskVersion></MaskVersions></MasterData></KNX>")
        .WithControls(controls);
    private static XDocument WithControls(this XDocument document, XElement[] controls)
    { document.Descendants(Ns + "Procedure").Single().Add(controls); return document; }
    private static void AddReference(XDocument doc, string reference, string parameter, string value = "1")
    {
        Static(doc).Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", parameter), new XAttribute("ParameterType", "T"), new XAttribute("Value", value)));
        Static(doc).Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", reference), new XAttribute("RefId", parameter)));
    }
    private static object Inputs(ArchivedImageSourceChecks.Fixture fixture)
        => typeof(ArchivedImageSources).GetMethod("ReadInputs", Hidden)!.Invoke(null, [fixture.Project, fixture.Device])!;
    private static object Prepare(object inputs, LoadProcedureRequest request)
        => typeof(LoadProcedureCompiler).GetMethod("PrepareResolved", Hidden)!.Invoke(null, [inputs, request])!;
    private static object Context(ArchivedImageSourceChecks.Fixture fixture, object inputs, object prepared)
        => typeof(Project).Assembly.GetType("Knx.NativeProcedureEvaluationContext", true)!.GetMethod("Create", Hidden)!
            .Invoke(null, [fixture.Project, fixture.Device, inputs, prepared])!;
    private static Capture Load(string directory, XDocument? doc = null, XDocument? master = null,
        Action<ArchivedImageSourceChecks.Fixture>? edit = null, LoadProcedureRequest? request = null)
    {
        var f = ArchivedImageSourceChecks.Load(directory, app: doc ?? App(), master: master); edit?.Invoke(f);
        var inputs = Inputs(f); var prepared = Prepare(inputs, request ?? Request);
        return new(f, inputs, prepared, Context(f, inputs, prepared));
    }
    private static LoadProcedurePlan Compile(Capture c, object? context = null)
        => (LoadProcedurePlan)typeof(LoadProcedureCompiler).GetMethod("CompileResolved", Hidden)!.Invoke(null, [c.Prepared, context ?? c.Context])!;
    private static bool Failed(LoadProcedurePlan plan, LoadProcedureIssueCode? code = null)
        => !plan.IsAvailable && plan.WitnessHash is null && plan.Controls.Count == 0 && plan.MergeDecisions.Count == 0
            && plan.ResourceReferences.Count == 0 && plan.TableRequirements.Count == 0 && plan.ConditionalSelection is null
            && plan.Issues.Count == 1 && (code is null || plan.Issues[0].Code == code)
            && !plan.ExecutionEligible && !plan.CompleteApplicationImage && !plan.DownloadEligible;
    private static bool Unchanged(Capture c, Func<bool> action)
    {
        var f = c.Fixture; var xml = Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting);
        var hash = SHA256.HashData(File.ReadAllBytes(f.Path)); var revision = f.Project.ApplicationDataRevision;
        var undo = new UndoStack(); undo.Reset(f.Project); var index = undo.Index; var dirty = undo.IsDirty;
        return action() && xml == Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting) && revision == f.Project.ApplicationDataRevision
            && hash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(f.Path)))
            && undo.Index == index && undo.IsDirty == dirty && !undo.CanUndo && !undo.CanRedo;
    }

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, Func<bool> action)
        {
            checks++;
            try { var ok = action(); Console.WriteLine((ok ? "PASS " : "FAIL ") + "ConditionalLoadProcedure " + name); if (!ok) failures++; }
            catch (Exception error) { failures++; Console.WriteLine("FAIL ConditionalLoadProcedure " + name + ": " + error.GetBaseException().GetType().Name + " " + error.GetBaseException().Message); }
        }
        void Refused(string name, XDocument document, LoadProcedureIssueCode? code = null, XDocument? master = null)
            => Check(name, () => { var c = Load(directory, document, master); return Unchanged(c, () => Failed(Compile(c), code)); });

        Check("primitive source uses the exact legacy plan and omits additive null fields", () =>
        {
            var c = Load(directory, App(Control("Connect"), Write("11"), Control("Disconnect")));
            return Unchanged(c, () =>
            {
                var inputsApp = Fact<XDocument>(c.Inputs, "Application"); var inputsMaster = Fact<XDocument?>(c.Inputs, "Master");
                var old = LoadProcedureCompiler.Compile(inputsApp, inputsMaster, Request); var plan = Compile(c);
                return old.IsAvailable && plan.IsAvailable && plan.ProfileVersion == 1 && !Fact<bool>(c.Prepared, "RequiresExtendedProfile")
                    && plan.WitnessHash == old.WitnessHash && JsonSerializer.Serialize(plan) == JsonSerializer.Serialize(old)
                    && !JsonSerializer.Serialize(plan).Contains("Provenance", StringComparison.Ordinal)
                    && !JsonSerializer.Serialize(plan).Contains("ConditionalSelection", StringComparison.Ordinal);
            });
        });
        Check("all matching load branches concatenate in original order", () =>
        {
            var c = Load(directory, App(Choose("R", When("1", Write("11")), When("1 2", Write("22")), Default(Write("FF")))));
            return Unchanged(c, () =>
            {
                var plan = Compile(c); var decision = plan.ConditionalSelection?.Decisions.Single();
                return plan.IsAvailable && plan.ProfileVersion == 2 && plan.Controls.Select(p => p.InlineData!.ToHexString()).SequenceEqual(new[] { "11", "22" })
                    && decision is { ControllerActive: true, ControllerValue: 1, UsedDefault: false }
                    && decision.SelectedWhenOrdinals.SequenceEqual(new[] { 0, 1 });
            });
        });
        Check("sole default is selected only after every tested sibling misses", () =>
        {
            var c = Load(directory, App(Choose("R", When("0", Write("11")), When("2", Write("22")), Default(Write("FF")))));
            return Unchanged(c, () => Compile(c) is { IsAvailable: true, Controls.Count: 1, ConditionalSelection.Decisions.Count: 1 } plan
                && plan.Controls[0].InlineData!.ToHexString() == "FF" && plan.ConditionalSelection!.Decisions[0].UsedDefault);
        });
        Check("a matching sibling suppresses default even when its parts filter emits nothing", () =>
        {
            var c = Load(directory, App(Choose("R", When("1", Write("11", "grp")), Default(Write("FF")))));
            return Unchanged(c, () => Compile(c) is { IsAvailable: true, Controls.Count: 0, ConditionalSelection.Decisions.Count: 1 } plan
                && !plan.ConditionalSelection!.Decisions[0].UsedDefault && plan.ConditionalSelection.Decisions[0].SelectedWhenOrdinals.SequenceEqual(new[] { 0 }));
        });
        Check("inactive controller skips its entire choose including default", () =>
        {
            var doc = App(Choose("Second", When("2", Compare()), Default(Write("FF")))); AddReference(doc, "Second", "P2", "2");
            Dynamic(doc).Add(Choose("R", When("0", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "Second")))));
            var c = Load(directory, doc);
            return Unchanged(c, () => Compile(c) is { IsAvailable: true, Controls.Count: 0, ConditionalSelection.Decisions.Count: 1 } plan
                && plan.ConditionalSelection!.Decisions[0] is { ControllerActive: false, ControllerValue: null, UsedDefault: false, SelectedWhenOrdinals.Count: 0 });
        });
        Check("guarded alias mode matrix preserves disjoint writes and inactive comparison skips", () =>
        {
            foreach (var mode in new[] { "0", "1" })
                foreach (var value in new[] { "0", "1", "2" })
                {
                    XElement Rel(uint offset, uint size) => Control("WriteRelMem", new XAttribute("ObjIdx", "4"), new XAttribute("Offset", offset), new XAttribute("Size", size), new XAttribute("Verify", "false"));
                    var doc = App(Choose("Second", When("2", Compare())), Choose("R", When("0", Rel(0, 1154)), When("1", Rel(0, 835), Rel(904, 250))));
                    var type = Static(doc).Element(Ns + "ParameterTypes")!.Elements().Single(); type.RemoveNodes();
                    type.Add(new XElement(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", "8"),
                        Enumerable.Range(0, 3).Select(i => new XElement(Ns + "Enumeration", new XAttribute("Id", "Mode-" + i), new XAttribute("Value", i)))));
                    AddReference(doc, "Second", "P2"); Static(doc).Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "Alias"), new XAttribute("RefId", "P2"), new XAttribute("Access", "None")));
                    Dynamic(doc).Add(Choose("R", When("0", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "Second"))),
                        When("1", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "Alias")))));
                    var c = Load(directory, doc, edit: f => { f.Device.ParameterValues["R"] = mode; f.Device.ParameterValues["Second"] = value; });
                    if (!Unchanged(c, () =>
                    {
                        var plan = Compile(c); if (!plan.IsAvailable) return false;
                        var compares = plan.Controls.Where(p => p.Kind == LoadControlKind.CompareRelativeMemory).ToArray();
                        var writes = plan.Controls.Where(p => p.Kind == LoadControlKind.WriteRelativeMemory).ToArray();
                        var geometry = writes.Select(p => (p.Arguments.Single(a => a.Name == "Offset").UnsignedValue!.Value,
                            p.Arguments.Single(a => a.Name == "Size").UnsignedValue!.Value)).ToArray();
                        var expected = mode == "0" ? new[] { (0UL, 1154UL) } : new[] { (0UL, 835UL), (904UL, 250UL) };
                        var second = plan.ConditionalSelection!.Decisions.Single(d => d.ControllerRefId == "Second");
                        return geometry.SequenceEqual(expected) && compares.Length == (mode == "0" && value == "2" ? 1 : 0)
                            && second.ControllerActive == (mode == "0") && (mode == "0" || second.ControllerValue is null);
                    })) return false;
                }
            return true;
        });
        Check("nested decisions and emitted controls retain original source ancestry", () =>
        {
            var c = Load(directory, App(Choose("R", When("1", Choose("R", When("1", Write("11")))))));
            return Unchanged(c, () =>
            {
                var plan = Compile(c); var decisions = plan.ConditionalSelection?.Decisions;
                return plan.IsAvailable && decisions?.Count == 2 && decisions[0].Source.SourcePath.Count < decisions[1].Source.SourcePath.Count
                    && decisions[1].Source.AncestorDecisionOrdinals.SequenceEqual(new[] { 0 })
                    && plan.Controls.Single().Provenance!.AncestorDecisionOrdinals.SequenceEqual(new[] { 0, 1 })
                    && decisions.All(d => d.Source.SourceOrigin == LoadProcedureSourceOrigin.Application);
            });
        });
        Check("deep admitted branches do not inherit the JSON serializer nesting limit", () =>
        {
            XElement body = Write("11"); for (var depth = 0; depth < 20; depth++) body = Choose("R", When("1", body));
            var c = Load(directory, App(body));
            return Unchanged(c, () => Fact<bool>(c.Prepared, "IsAvailable") && Compile(c) is { IsAvailable: true, ProfileVersion: 2, Controls.Count: 1, ConditionalSelection.Decisions.Count: 20 } plan
                && plan.Controls[0].InlineData!.ToHexString() == "11" && plan.Controls[0].Provenance!.AncestorDecisionOrdinals.SequenceEqual(Enumerable.Range(0, 20)));
        });
        Check("repeated merge groups have distinct original template paths and occurrences", () =>
        {
            var doc = App(); Program(doc).SetAttributeValue("LoadProcedureStyle", "MergedProcedure");
            Static(doc).Element(Ns + "LoadProcedures")!.Elements().Single().SetAttributeValue("MergeId", "2");
            var master = Master(Control("Connect"), Control("Merge", new XAttribute("MergeId", "2")), Control("Merge", new XAttribute("MergeId", "2")), Control("Disconnect"));
            var c = Load(directory, doc, master);
            return Unchanged(c, () =>
            {
                var plan = Compile(c); var inserted = plan.Controls.Where(p => p.Origin == LoadControlOrigin.ProductMerge && p.Kind == LoadControlKind.WriteProperty).ToArray();
                var decisions = plan.ConditionalSelection?.Decisions;
                return plan.IsAvailable && inserted.Length == 2 && plan.MergeDecisions.Count == 2 && decisions?.Count == 2
                    && inserted[0].Provenance!.SourcePath.SequenceEqual(inserted[1].Provenance!.SourcePath)
                    && !inserted[0].Provenance!.TemplatePath!.SequenceEqual(inserted[1].Provenance!.TemplatePath!)
                    && inserted[0].Provenance!.ExpansionOrdinal != inserted[1].Provenance!.ExpansionOrdinal
                    && inserted.All(p => p.Provenance!.TemplateOrigin == LoadProcedureSourceOrigin.ArchivedMaster)
                    && plan.MergeDecisions.All(d => d.Provenance!.SourceOrigin == LoadProcedureSourceOrigin.ArchivedMaster)
                    && !decisions[0].Source.TemplatePath!.SequenceEqual(decisions[1].Source.TemplatePath!);
            });
        });
        Check("missing merge groups skip before their own applies-to filter", () =>
        {
            var doc = App(); Program(doc).SetAttributeValue("LoadProcedureStyle", "MergedProcedure");
            var master = Master(Choose("R", When("1", Control("Merge", new XAttribute("MergeId", "9"), new XAttribute("AppliesTo", "grp")))));
            var c = Load(directory, doc, master);
            return Unchanged(c, () => Compile(c) is { IsAvailable: true, Controls.Count: 0, MergeDecisions.Count: 1 } plan
                && plan.MergeDecisions[0].Decision == LoadMergeDecisionKind.MissingGroupSkipped);
        });
        Check("inserted auto becomes all and every FPG combination is applied once", () =>
        {
            string[][] expected = [["11", "44"], ["11", "44"], ["11", "22", "44"], ["11", "44"], ["11", "33", "44"], ["11", "44"], ["11", "22", "33", "44"], ["11", "44"]];
            for (var parts = 0; parts < 8; parts++)
            {
                var doc = App(Choose("R", When("1", Write("11"), Write("22", "par"), Write("33", "grp"), Write("44", "all"))));
                Program(doc).SetAttributeValue("LoadProcedureStyle", "MergedProcedure"); Static(doc).Element(Ns + "LoadProcedures")!.Elements().Single().SetAttributeValue("MergeId", "2");
                var c = Load(directory, doc, Master(Control("Merge", new XAttribute("MergeId", "2"), new XAttribute("AppliesTo", "grp"))), request: Request with { Parts = (LoadParts)parts });
                if (!Unchanged(c, () => Compile(c) is { IsAvailable: true } plan && plan.Controls.Select(p => p.InlineData!.ToHexString()).SequenceEqual(expected[parts])
                    && plan.Controls[0].DeclaredAppliesTo == LoadControlCondition.Auto && plan.Controls[0].EffectiveAppliesTo == LoadControlCondition.All)) return false;
            }
            return true;
        });
        Check("unused groups validate references and types without requiring an activity owner", () =>
        {
            var doc = App(Write("11")); AddReference(doc, "Unused", "P-unused");
            Static(doc).Element(Ns + "LoadProcedures")!.Add(new XElement(Ns + "LoadProcedure", new XAttribute("MergeId", "7"), Choose("Unused", When("1", Write("77")))));
            var c = Load(directory, doc);
            return Unchanged(c, () => Fact<bool>(c.Prepared, "IsAvailable") && Fact<bool>(c.Context, "IsAvailable")
                && Fact<IReadOnlyList<string>>(c.Prepared, "ControllerReferences").Count == 0
                && Fact<IReadOnlyList<string>>(c.Prepared, "DeclaredControllerReferences").SequenceEqual(new[] { "Unused" })
                && Compile(c) is { IsAvailable: true, ProfileVersion: 2, Controls.Count: 1 } plan && plan.Controls[0].InlineData!.ToHexString() == "11");
        });
        var unusedBad = App(Write("11")); Static(unusedBad).Element(Ns + "LoadProcedures")!.Add(new XElement(Ns + "LoadProcedure", new XAttribute("MergeId", "7"), Choose("Missing", When("1", Write("77")))));
        Refused("unused group references are still validated", unusedBad, LoadProcedureIssueCode.UnknownController);
        var hiddenBad = App(Choose("R", When("0", Control("Unknown")), When("1", Write("11"))));
        Refused("unknown controls in unselected branches fail atomically", hiddenBad, LoadProcedureIssueCode.UnsupportedControl);
        var hiddenHex = App(Choose("R", When("0", Write("0 1")), When("1", Write("11"))));
        Refused("malformed inline operands in unselected branches fail before selection", hiddenHex, LoadProcedureIssueCode.InvalidControl);
        Refused("two defaults are not silently ordered", App(Choose("R", Default(Write("11")), Default(Write("22")))), LoadProcedureIssueCode.UnsupportedActivationCondition);
        var mixedDefault = Default(Write("11")); mixedDefault.SetAttributeValue("test", "1");
        Refused("truthy default combined with test remains outside the profile", App(Choose("R", mixedDefault)), LoadProcedureIssueCode.UnsupportedActivationCondition);
        foreach (var test in new[] { "==1", "1  2", " 1", "1 ", "1.0", ">=1 2", "\t1", "+1" })
            Refused("strict integer condition rejects " + JsonSerializer.Serialize(test), App(Choose("R", When(test, Write("11")))), LoadProcedureIssueCode.UnsupportedActivationCondition);
        Refused("nested product merges are refused even in an unused branch", App(Choose("R", When("0", Control("Merge", new XAttribute("MergeId", "2"))))), LoadProcedureIssueCode.UnsupportedControl);

        Check("relative comparison without choose still requires profile two", () =>
        {
            var doc = App(Compare()); var c = Load(directory, doc);
            return Unchanged(c, () =>
            {
                var plan = Compile(c); var control = plan.Controls.SingleOrDefault();
                return Fact<bool>(c.Prepared, "RequiresExtendedProfile") && !Fact<bool>(c.Prepared, "HasConditions") && plan.IsAvailable && plan.ProfileVersion == 2
                    && plan.ConditionalSelection is null && control is { Kind: LoadControlKind.CompareRelativeMemory, InlineData.Length: 1, InlineMask.Length: 1 }
                    && control.InlineData!.ToHexString() == "FF" && control.OnError.Single() is { Cause: LoadControlErrorCause.CompareMismatch, MessageRef: "Compare-message" }
                    && !LoadProcedureCompiler.Compile(doc, Fact<XDocument?>(c.Inputs, "Master"), Request).IsAvailable;
            });
        });
        Check("relative comparison retains exact selectors operands and default arguments", () =>
        {
            var c = Load(directory, App(Compare()));
            return Unchanged(c, () =>
            {
                var plan = Compile(c); if (!plan.IsAvailable) return false; var args = plan.Controls.Single().Arguments.ToDictionary(a => a.Name);
                return args["ObjIdx"].UnsignedValue == 4 && args["Offset"].UnsignedValue == 932 && args["Size"].UnsignedValue == 1
                    && args["Occurrence"].UnsignedValue == 0 && args["AllowCachedValue"].BooleanValue == false && args["Invert"].BooleanValue == true
                    && args["RetryInterval"].UnsignedValue == 0 && args["TimeOut"].UnsignedValue == 0;
            });
        });
        Check("relative comparison size is retained without generating or padding source bytes", () =>
        {
            var comparison = Compare(); comparison.SetAttributeValue("Offset", uint.MaxValue); comparison.SetAttributeValue("Size", uint.MaxValue);
            comparison.Attribute("ObjIdx")!.Remove(); comparison.SetAttributeValue("ObjType", "65535"); comparison.SetAttributeValue("Occurrence", "65535");
            comparison.SetAttributeValue("Range", "[-5,255)u"); var c = Load(directory, App(comparison));
            return Unchanged(c, () => Compile(c) is { IsAvailable: true } plan && plan.Controls[0].InlineData!.Length == 1
                && plan.Controls[0].Arguments.Single(a => a.Name == "Size").UnsignedValue == uint.MaxValue
                && plan.Controls[0].Arguments.Single(a => a.Name == "Occurrence").UnsignedValue == 65535
                && plan.ResourceReferences.Count == 0 && !plan.ExecutionEligible && !plan.CompleteApplicationImage && !plan.DownloadEligible);
        });
        foreach (var kind in new[] { "missing-selector", "both-selectors", "index-occurrence", "missing-offset", "missing-size", "selector-overflow", "offset-overflow", "range", "hex", "unknown-attribute", "error-cause" })
        {
            var comparison = Compare();
            switch (kind)
            {
                case "missing-selector": comparison.Attribute("ObjIdx")!.Remove(); break;
                case "both-selectors": comparison.SetAttributeValue("ObjType", "4"); break;
                case "index-occurrence": comparison.SetAttributeValue("Occurrence", "0"); break;
                case "missing-offset": comparison.Attribute("Offset")!.Remove(); break;
                case "missing-size": comparison.Attribute("Size")!.Remove(); break;
                case "selector-overflow": comparison.SetAttributeValue("ObjIdx", "256"); break;
                case "offset-overflow": comparison.SetAttributeValue("Offset", "4294967296"); break;
                case "range": comparison.SetAttributeValue("Range", "[0 10]"); break;
                case "hex": comparison.SetAttributeValue("InlineData", "AA BB"); break;
                case "unknown-attribute": comparison.SetAttributeValue("Unknown", "1"); break;
                case "error-cause": comparison.Elements().Single().SetAttributeValue("Cause", "Unknown"); break;
            }
            Refused("relative comparison rejects " + kind, App(comparison), LoadProcedureIssueCode.InvalidControl);
        }

        Check("a context from an equivalent foreign preparation cannot select this graph", () =>
        {
            var left = Load(directory); var right = Load(directory);
            return Unchanged(left, () => Failed(Compile(left, right.Context), LoadProcedureIssueCode.ContextMismatch));
        });
        Check("compiler checks current saved values before producing any controls", () =>
        {
            var c = Load(directory);
            return Unchanged(c, () =>
            {
                try { c.Fixture.Device.ParameterValues["R"] = "2"; return Failed(Compile(c), LoadProcedureIssueCode.InputsChanged); }
                finally { c.Fixture.Device.ParameterValues.Remove("R"); }
            });
        });
        Check("prepared source mutation refuses rather than reusing a detached AST", () =>
        {
            var c = Load(directory);
            return Unchanged(c, () =>
            {
                var source = Fact<XDocument>(c.Inputs, "Application"); var test = source.Descendants(Ns + "when").Single().Attribute("test")!; var value = test.Value;
                try { test.Value = "0"; return Failed(Compile(c)); }
                finally { test.Value = value; }
            });
        });
        Check("output source paths decisions controls and inline bytes are immutable", () =>
        {
            var c = Load(directory);
            return Unchanged(c, () =>
            {
                var plan = Compile(c); if (!plan.IsAvailable) return false; var copy = plan.Controls[1].InlineData!.ToArray(); copy[0] = 255;
                var rejected = 0;
                try { ((IList<LoadProcedureControl>)plan.Controls).Clear(); } catch (NotSupportedException) { rejected++; }
                try { ((IList<int>)plan.Controls[1].Provenance!.SourcePath).Clear(); } catch (NotSupportedException) { rejected++; }
                try { ((IList<int>)plan.ConditionalSelection!.Decisions[0].SelectedWhenOrdinals).Clear(); } catch (NotSupportedException) { rejected++; }
                return rejected == 3 && plan.Controls[1].InlineData!.ToHexString() == "11" && !plan.ExecutionEligible && !plan.CompleteApplicationImage && !plan.DownloadEligible
                    && plan.Requirements.Any(r => r.Code == LoadProcedureRequirementCode.GeneratedTablesNotBuilt)
                    && plan.Requirements.Any(r => r.Code == LoadProcedureRequirementCode.ParameterActivationUnresolved);
            });
        });
        Check("many matching empty branches cannot bypass the visited graph budget", () =>
        {
            var c = Load(directory, App(Choose("R", Enumerable.Range(0, 8193).Select(_ => When("1")).ToArray())));
            return Unchanged(c, () => Fact<bool>(c.Prepared, "IsAvailable") && Failed(Compile(c), LoadProcedureIssueCode.LimitExceeded));
        });
        Check("repeated selected merges refuse the whole output when expansion exceeds its budget", () =>
        {
            var doc = App(Choose("R", When("1", Enumerable.Range(0, 1024).Select(_ => Write("11")).ToArray())));
            Program(doc).SetAttributeValue("LoadProcedureStyle", "MergedProcedure"); Static(doc).Element(Ns + "LoadProcedures")!.Elements().Single().SetAttributeValue("MergeId", "2");
            var c = Load(directory, doc, Master(Enumerable.Range(0, 16).Select(_ => Control("Merge", new XAttribute("MergeId", "2"))).ToArray()));
            return Unchanged(c, () => Fact<bool>(c.Prepared, "IsAvailable") && Failed(Compile(c), LoadProcedureIssueCode.LimitExceeded));
        });
        Console.WriteLine($"ConditionalLoadProcedure: {checks} checks, {failures} failures"); return failures;
    }
}
