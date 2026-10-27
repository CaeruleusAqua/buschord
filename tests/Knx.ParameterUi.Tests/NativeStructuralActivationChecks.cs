using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;

internal static class NativeStructuralActivationChecks
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private static Type Core(string name) => typeof(Project).Assembly.GetType("Knx." + name, true)!;
    private static T Fact<T>(object value, string name) => NativeActivationChecks.Fact<T>(value, name);
    private static object Context(ArchivedImageSourceChecks.Fixture fixture) => NativeActivationChecks.Context(fixture);
    private static object Structure(object context) => Fact<object>(context, "Structure");
    private static object Proof(object catalog, ApplicationSnapshot snapshot)
        => Core("NativeStructuralActivationProof").GetMethod("Create", Hidden)!.Invoke(null, [catalog, snapshot])!;
    private static object Resolved(object context, object? activation = null)
        => Core("NativeStructuralActivationProof").GetMethod("CreateResolved", Hidden)!.Invoke(null,
            [Fact<object>(context, "Catalog"), Fact<ApplicationSnapshot>(context, "Snapshot"), activation ?? Fact<object>(context, "Activation")])!;
    private static object[] Rows(object value, string name) => ((IEnumerable)Fact<object>(value, name)).Cast<object>().ToArray();
    private static bool Refused(object value, GeneratedTableIssueCode? code = null)
        => !Fact<bool>(value, "IsAvailable") && Fact<string?>(value, "Fingerprint") is null
            && Fact<IReadOnlyList<GeneratedTableIssue>>(value, "Issues") is { Count: 1 } issues
            && (code is null || issues[0].Code == code);
    private static XElement Program(XDocument document) => document.Descendants(Ns + "ApplicationProgram").Single();
    private static ArchivedImageSourceChecks.Fixture Load(string directory, XDocument? app = null)
        => ArchivedImageSourceChecks.Load(directory, app: app ?? NativeCommunicationChecks.Modules());
    private static string Xml(Project project) => Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting);
    private static bool Unchanged(ArchivedImageSourceChecks.Fixture fixture, Func<bool> action)
    {
        var xml = Xml(fixture.Project); var hash = SHA256.HashData(File.ReadAllBytes(fixture.Path));
        var revision = fixture.Project.ApplicationDataRevision; var history = new UndoStack(); history.Reset(fixture.Project);
        var index = history.Index; var dirty = history.IsDirty; var undo = history.CanUndo; var redo = history.CanRedo;
        return action() && xml == Xml(fixture.Project) && revision == fixture.Project.ApplicationDataRevision
            && hash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(fixture.Path)))
            && history.Index == index && history.IsDirty == dirty && history.CanUndo == undo && history.CanRedo == redo;
    }
    private static bool ChangedItems(object context, Func<IReadOnlyList<ApplicationModuleInstance>, IReadOnlyList<ApplicationModuleInstance>> edit, bool resolved = false)
    {
        var snapshot = Fact<ApplicationSnapshot>(context, "Snapshot"); var catalog = Fact<object>(context, "Catalog");
        var field = typeof(ApplicationSnapshot).GetField("<ModuleInstances>k__BackingField", Hidden)!;
        var original = snapshot.ModuleInstances;
        try
        {
            field.SetValue(snapshot, edit(original));
            var proof = resolved ? Resolved(context) : Proof(catalog, snapshot);
            return Refused(proof) && Rows(proof, "Occurrences").Length == 0;
        }
        finally { field.SetValue(snapshot, original); }
    }
    private static bool ChangedOriginKey(object context, int key)
    {
        var catalog = Fact<object>(context, "Catalog"); var snapshot = Fact<ApplicationSnapshot>(context, "Snapshot");
        var root = Fact<XElement>(snapshot.Evaluation, "ExpandedRoot"); var type = Core("NativeCommunicationElementOrigin");
        var element = root.Descendants().First(e => e.Name == Ns + "ComObject" && e.Annotation(type) is { } origin && Rows(origin, "Frames").Length == 1);
        var original = element.Annotation(type)!;
        var constructor = type.GetConstructors(Hidden).Single(c => c.GetParameters().Length == 8);
        var args = constructor.GetParameters().Select(p => p.Name == "Key" ? (object)key : Fact<object?>(original, p.Name!)).ToArray();
        var forged = constructor.Invoke(args);
        try
        {
            element.RemoveAnnotations(type); element.AddAnnotation(forged);
            return Refused(Proof(catalog, snapshot), GeneratedTableIssueCode.ActivationMismatch);
        }
        finally { element.RemoveAnnotations(type); element.AddAnnotation(original); }
    }
    private static XDocument FreeModules()
    {
        var document = NativeCommunicationChecks.Modules();
        var definition = document.Descendants(Ns + "ModuleDef").Single();
        definition.Element(Ns + "Static")!.RemoveNodes(); definition.Element(Ns + "Dynamic")!.RemoveNodes();
        return document;
    }

    private static XDocument ConditionalModules(bool empty = false)
    {
        var document = empty ? FreeModules() : NativeCommunicationChecks.Modules();
        var app = Program(document); var type = app.Element(Ns + "Static")!.Element(Ns + "ParameterTypes")!.Elements().Single();
        type.RemoveNodes(); type.Add(new XElement(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", "8"),
            Enumerable.Range(0, 3).Select(i => new XElement(Ns + "Enumeration", new XAttribute("Id", "E" + i), new XAttribute("Value", i), new XAttribute("Text", "Choice " + i)))));
        var dynamic = app.Element(Ns + "Dynamic")!; var calls = dynamic.Elements(Ns + "Module").ToArray();
        foreach (var call in calls) call.Remove();
        dynamic.Add(new XElement(Ns + "choose", new XAttribute("ParamRefId", "R"),
            calls.Select((call, index) => new XElement(Ns + "when", new XAttribute("test", (index + 1).ToString()), call))));
        return document;
    }
    private static XDocument NestedConditionalModules(bool empty = false)
    {
        var document = ConditionalModules(empty); var app = Program(document); var stat = app.Element(Ns + "Static")!;
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "P-inner"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "1")));
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "Inner"), new XAttribute("RefId", "P-inner")));
        var dynamic = app.Element(Ns + "Dynamic")!; var outer = dynamic.Element(Ns + "choose")!;
        var branches = outer.Elements().ToArray(); foreach (var branch in branches) branch.Remove();
        outer.Add(new XElement(Ns + "when", new XAttribute("test", "1"),
            new XElement(Ns + "choose", new XAttribute("ParamRefId", "Inner"), branches)),
            new XElement(Ns + "when", new XAttribute("test", "2")));
        dynamic.AddFirst(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "Inner")));
        return document;
    }
    private sealed class Runtime : IParameterScriptRuntime
    {
        internal int Calls;
        public IReadOnlyDictionary<string, object?> Execute(string source, IReadOnlyDictionary<string, object?> inputs, IReadOnlyList<string> outputs)
        { Calls++; return outputs.ToDictionary(id => id, _ => (object?)1d, StringComparer.Ordinal); }
    }
    private static void UnrelatedCalculation(XDocument document)
    {
        var stat = Program(document).Element(Ns + "Static")!;
        foreach (var id in new[] { "Input", "Output" })
        {
            stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "P-" + id), new XAttribute("ParameterType", "T"), new XAttribute("Value", "1")));
            stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", id), new XAttribute("RefId", "P-" + id)));
        }
        stat.Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + "ParameterCalculation", new XAttribute("Language", "JavaScript"),
            new XElement(Ns + "LRTransformation", "output=input;"),
            new XElement(Ns + "LParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "Input"), new XAttribute("AliasName", "input"))),
            new XElement(Ns + "RParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "Output"), new XAttribute("AliasName", "output"))))));
    }

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, Func<bool> action)
        {
            checks++;
            try
            {
                var ok = action(); Console.WriteLine((ok ? "PASS " : "FAIL ") + "NativeStructure " + name);
                if (!ok) failures++;
            }
            catch (Exception error)
            {
                failures++; Console.WriteLine("FAIL NativeStructure " + name + ": " + error.GetBaseException().GetType().Name + " " + error.GetBaseException().Message);
            }
        }
        Check("root-only evaluation owns an empty sealed occurrence manifest", () =>
        {
            var f = Load(directory, NativeCommunicationChecks.App());
            return Unchanged(f, () =>
            {
                var context = Context(f); var structure = Structure(context);
                if (!(Fact<bool>(context, "IsAvailable") && Fact<bool>(structure, "IsAvailable")
                    && Fact<string?>(structure, "Fingerprint")?.Length == 64 && Rows(structure, "Occurrences").Length == 0
                    && Fact<ApplicationSnapshot>(context, "Snapshot").ModuleInstances.Count == 0)) return false;
                var snapshot = Fact<ApplicationSnapshot>(context, "Snapshot"); var root = Fact<XElement>(snapshot.Evaluation, "ExpandedRoot");
                var type = Core("NativeCommunicationOccurrenceManifest"); var manifest = root.Annotation(type);
                if (manifest is null) return false;
                try
                {
                    root.RemoveAnnotations(type);
                    return Refused(Proof(Fact<object>(context, "Catalog"), snapshot), GeneratedTableIssueCode.UnsupportedStructureActivation);
                }
                finally { root.AddAnnotation(manifest); }
            });
        });
        Check("two literal calls retain exact identities arguments and native ordering", () =>
        {
            var f = Load(directory);
            return Unchanged(f, () =>
            {
                var context = Context(f); var structure = Structure(context); var snapshot = Fact<ApplicationSnapshot>(context, "Snapshot");
                return Fact<bool>(context, "IsAvailable") && Fact<bool>(structure, "IsAvailable") && Rows(structure, "Occurrences").Length == 2
                    && snapshot.ModuleInstances.Select(i => i.Id).SequenceEqual(new[] { "M1_MI-1", "M2_MI-1" })
                    && snapshot.ModuleInstances.Select(i => i.Arguments.Single().Value).SequenceEqual(new[] { "2", "4" });
            });
        });
        Check("KO-free module instances cannot disappear from the full occurrence inventory", () =>
        {
            var f = Load(directory, FreeModules());
            return Unchanged(f, () =>
            {
                var context = Context(f); var snapshot = Fact<ApplicationSnapshot>(context, "Snapshot");
                return Fact<bool>(context, "IsAvailable") && snapshot.ComObjects.Count == 0 && snapshot.ModuleInstances.Count == 2
                    && Rows(Structure(context), "Occurrences").Length == 2;
            });
        });
        Check("an originally hidden conditional call is refused before flattening", () =>
        {
            var doc = NativeCommunicationChecks.Modules(); var dynamic = Program(doc).Element(Ns + "Dynamic")!;
            var call = dynamic.Elements(Ns + "Module").First(); call.Remove();
            dynamic.Add(new XElement(Ns + "choose", new XAttribute("ParamRefId", "R"),
                new XElement(Ns + "when", new XAttribute("test", "0"), call)));
            var f = Load(directory, doc);
            return Unchanged(f, () => Refused(Context(f), GeneratedTableIssueCode.UnsupportedStructureActivation));
        });
        Check("literal zero repeats and parameter-driven counts remain explicit refusals", () =>
        {
            foreach (var parameterCount in new[] { false, true })
            {
                var doc = NativeCommunicationChecks.Modules(repeat: true); var repeat = doc.Descendants(Ns + "Repeat").Single();
                repeat.SetAttributeValue("Count", "0"); if (parameterCount) repeat.SetAttributeValue("ParameterRefId", "R");
                var f = Load(directory, doc);
                if (!Unchanged(f, () => Refused(Context(f), GeneratedTableIssueCode.UnsupportedStructureActivation))) return false;
            }
            return true;
        });
        Check("nested definitions cannot inherit a flat module admission", () =>
        {
            var f = Load(directory, NativeCommunicationChecks.Modules(nested: true));
            return Unchanged(f, () => Refused(Context(f), GeneratedTableIssueCode.UnsupportedStructureActivation));
        });
        Check("allocator and outer BaseValue arguments are never treated as literals", () =>
        {
            foreach (var attribute in new[] { "AllocatorRefId", "BaseValue" })
            {
                var doc = NativeCommunicationChecks.Modules(); doc.Descendants(Ns + "NumericArg").First().SetAttributeValue(attribute, "MD_A");
                var f = Load(directory, doc);
                if (!Unchanged(f, () => Refused(Context(f), GeneratedTableIssueCode.UnsupportedStructureActivation))) return false;
            }
            return true;
        });
        Check("missing and mismatched typed arguments fail as complete requests", () =>
        {
            foreach (var missing in new[] { false, true })
            {
                var doc = NativeCommunicationChecks.Modules(); var argument = doc.Descendants(Ns + "NumericArg").First();
                if (missing) argument.Remove(); else argument.Name = Ns + "TextArg";
                var f = Load(directory, doc);
                if (!Unchanged(f, () => Refused(Context(f), GeneratedTableIssueCode.UnsupportedStructureActivation))) return false;
            }
            return true;
        });
        Check("foreign namespaces and non-Dynamic call owners cannot mint structure", () =>
        {
            foreach (var foreign in new[] { false, true })
            {
                var doc = NativeCommunicationChecks.Modules(); var call = doc.Descendants(Ns + "Module").First();
                if (foreign) call.Name = XNamespace.Get("urn:foreign") + "Module";
                else { call.Remove(); Program(doc).Element(Ns + "Static")!.Add(call); }
                var f = Load(directory, doc);
                if (!Unchanged(f, () => Refused(Context(f)))) return false;
            }
            return true;
        });
        Check("byte-identical foreign catalogues cannot reuse sealed manifests", () =>
        {
            var f = Load(directory); var other = Load(directory);
            return Unchanged(f, () =>
            {
                var left = Context(f); var right = Context(other);
                return Fact<bool>(left, "IsAvailable") && Fact<bool>(right, "IsAvailable")
                    && Refused(Proof(Fact<object>(right, "Catalog"), Fact<ApplicationSnapshot>(left, "Snapshot")));
            });
        });
        Check("changed saved occurrence arguments or repeat indices cannot retain proof", () =>
        {
            var f = Load(directory);
            return Unchanged(f, () =>
            {
                var context = Context(f);
                return Fact<bool>(context, "IsAvailable") && ChangedItems(context, items => items.Select((item, i) => i == 0
                    ? item with { Arguments = new[] { new ApplicationModuleArgument(item.Arguments[0].RefId, "3") } } : item).ToArray())
                    && ChangedItems(context, items => items.Select((item, i) => i == 0 ? item with { RepeatIndex = "999x1" } : item).ToArray());
            });
        });
        Check("missing and extra KO-free occurrences fail bidirectional comparison", () =>
        {
            var f = Load(directory, FreeModules());
            return Unchanged(f, () =>
            {
                var context = Context(f);
                return Fact<bool>(context, "IsAvailable") && ChangedItems(context, items => items.Take(1).ToArray())
                    && ChangedItems(context, items => items.Append(items[0]).ToArray());
            });
        });
        Check("valid frames cannot attach root or another definition's source keys", () =>
        {
            var doc = NativeCommunicationChecks.Modules(); var definition = doc.Descendants(Ns + "ModuleDef").Single(); var copy = new XElement(definition);
            foreach (var attribute in copy.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "Id" or "RefId" or "BaseNumber"))
                if (attribute.Value == "MD" || attribute.Value.StartsWith("MD_", StringComparison.Ordinal)) attribute.Value = "OTHER" + attribute.Value[2..];
            definition.Parent!.Add(copy); var f = Load(directory, doc);
            return Unchanged(f, () =>
            {
                var context = Context(f); if (!Fact<bool>(context, "IsAvailable")) return false;
                var nodes = Fact<IReadOnlyList<XElement>>(Fact<object>(context, "Catalog"), "Nodes");
                var rootKey = nodes.Select((node, index) => (node, index)).Single(p => p.node.Name == Ns + "Parameter" && (string?)p.node.Attribute("Id") == "P").index;
                var otherKey = nodes.Select((node, index) => (node, index)).Single(p => p.node.Name == Ns + "ComObject" && (string?)p.node.Attribute("Id") == "OTHER_O").index;
                return ChangedOriginKey(context, rootKey) && ChangedOriginKey(context, otherKey);
            });
        });
        Check("source depth and post-preflight source changes invalidate proof", () =>
        {
            var doc = NativeCommunicationChecks.Modules(); var bottom = Program(doc).Element(Ns + "Dynamic")!;
            for (var i = 0; i < 130; i++) { var child = new XElement(Ns + "ParameterBlock", new XAttribute("Id", "PB" + i)); bottom.Add(child); bottom = child; }
            var catalogType = Core("NativeCommunicationOriginCatalog"); var catalog = Activator.CreateInstance(catalogType, Hidden, null, [doc.Root!], null)!;
            var budget = false;
            try { Core("NativeStructuralActivationProof").GetMethod("SourceScopeElements", Hidden)!.Invoke(null, [catalog]); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { budget = Fact<GeneratedTableIssue>(error.InnerException, "Issue").Code == GeneratedTableIssueCode.LimitExceeded; }
            var f = Load(directory);
            return budget && Unchanged(f, () =>
            {
                var context = Context(f); if (!Fact<bool>(context, "IsAvailable")) return false;
                var source = Fact<IReadOnlyList<XElement>>(Fact<object>(context, "Catalog"), "Nodes")[0];
                var parameter = source.Descendants(Ns + "Parameter").Single(); var value = (string?)parameter.Attribute("Value");
                try { parameter.SetAttributeValue("Value", "0"); return Refused(Proof(Fact<object>(context, "Catalog"), Fact<ApplicationSnapshot>(context, "Snapshot")), GeneratedTableIssueCode.ActivationMismatch); }
                finally { parameter.SetAttributeValue("Value", value); }
            });
        });
        Check("single root choice selects original occurrences with literal argument identities", () =>
        {
            foreach (var value in new[] { "1", "2" })
            {
                var f = Load(directory, ConditionalModules()); f.Device.ParameterValues["R"] = value;
                if (!Unchanged(f, () =>
                {
                    var context = Context(f); if (!Fact<bool>(context, "IsAvailable")) return false;
                    var snapshot = Fact<ApplicationSnapshot>(context, "Snapshot"); var structure = Structure(context);
                    return Fact<bool>(structure, "IsAvailable") && Rows(structure, "Occurrences").Length == 1
                        && snapshot.ModuleInstances.Single().Id == "M" + value + "_MI-1"
                        && snapshot.ModuleInstances.Single().Arguments.Single().Value == (value == "1" ? "2" : "4")
                        && Fact<string?>(structure, "Fingerprint")?.Length == 64
                        && Refused(Proof(Fact<object>(context, "Catalog"), snapshot), GeneratedTableIssueCode.UnsupportedStructureActivation);
                })) return false;
            }
            return true;
        });
        Check("no matching branch seals a complete empty occurrence inventory", () =>
        {
            var f = Load(directory, ConditionalModules()); f.Device.ParameterValues["R"] = "0";
            return Unchanged(f, () =>
            {
                var context = Context(f); if (!Fact<bool>(context, "IsAvailable")) return false;
                return Fact<ApplicationSnapshot>(context, "Snapshot").ModuleInstances.Count == 0
                    && Rows(Structure(context), "Occurrences").Length == 0
                    && Fact<IReadOnlySet<string>>(context, "AdditionalControllerReferences").Contains("R")
                    && Fact<bool>(Resolved(context), "IsAvailable");
            });
        });
        Check("pure default selects its original call without manufacturing activation", () =>
        {
            var doc = ConditionalModules(); var branch = doc.Descendants(Ns + "when").Last();
            branch.Attribute("test")!.Remove(); branch.SetAttributeValue("default", "true");
            var f = Load(directory, doc); f.Device.ParameterValues["R"] = "0";
            return Unchanged(f, () =>
            {
                var context = Context(f);
                return Fact<bool>(context, "IsAvailable") && Fact<ApplicationSnapshot>(context, "Snapshot").ModuleInstances.Single().Id == "M2_MI-1";
            });
        });
        Check("overlapping true branches cannot inherit the legacy first branch inventory", () =>
        {
            var doc = ConditionalModules(); doc.Descendants(Ns + "when").Last().SetAttributeValue("test", "1 2");
            var f = Load(directory, doc);
            return Unchanged(f, () => Refused(Context(f), GeneratedTableIssueCode.ActivationMismatch));
        });
        Check("conditional KO-free calls remain selected and missing or extra occurrences fail", () =>
        {
            var f = Load(directory, ConditionalModules(empty: true));
            return Unchanged(f, () =>
            {
                var context = Context(f); if (!Fact<bool>(context, "IsAvailable")) return false;
                return Fact<ApplicationSnapshot>(context, "Snapshot").ComObjects.Count == 0
                    && Rows(Structure(context), "Occurrences").Length == 1
                    && ChangedItems(context, _ => [], resolved: true)
                    && ChangedItems(context, items => items.Concat(items).ToArray(), resolved: true);
            });
        });
        Check("conditional choices cannot consume another catalogue's activation proof", () =>
        {
            var f = Load(directory, ConditionalModules()); var other = Load(directory, ConditionalModules());
            other.Device.ParameterValues["R"] = "2";
            return Unchanged(f, () =>
            {
                var context = Context(f); var foreign = Context(other);
                return Fact<bool>(context, "IsAvailable") && Fact<bool>(foreign, "IsAvailable")
                    && Refused(Resolved(context, Fact<object>(foreign, "Activation")), GeneratedTableIssueCode.UnsupportedStructureActivation);
            });
        });
        Check("saved selector changes mint a different choice and stale the earlier context", () =>
        {
            var f = Load(directory, ConditionalModules());
            return Unchanged(f, () =>
            {
                var before = Context(f); if (!Fact<bool>(before, "IsAvailable")) return false;
                try
                {
                    f.Device.ParameterValues["R"] = "2"; var after = Context(f);
                    return Fact<bool>(after, "IsAvailable") && Fact<ApplicationSnapshot>(after, "Snapshot").ModuleInstances.Single().Id == "M2_MI-1"
                        && Fact<string>(Structure(before), "Fingerprint") != Fact<string>(Structure(after), "Fingerprint")
                        && !(bool)before.GetType().GetMethod("MatchesInputs", Hidden)!.Invoke(before, [f.Project, f.Device, NativeActivationChecks.Inputs(f)])!;
                }
                finally { f.Device.ParameterValues.Remove("R"); }
            });
        });
        Check("a third choose ancestor remains unsupported even on an inactive path", () =>
        {
            var doc = ConditionalModules(); var call = doc.Descendants(Ns + "Module").First(); call.Remove();
            doc.Descendants(Ns + "when").First().Add(new XElement(Ns + "choose", new XAttribute("ParamRefId", "R"),
                new XElement(Ns + "when", new XAttribute("test", "2"),
                    new XElement(Ns + "choose", new XAttribute("ParamRefId", "R"), new XElement(Ns + "when", new XAttribute("test", "2"), call)))));
            var f = Load(directory, doc); f.Device.ParameterValues["R"] = "0";
            return Unchanged(f, () => Refused(Context(f), GeneratedTableIssueCode.UnsupportedStructureActivation));
        });
        Check("selector placement must be exactly one unconditional root owner", () =>
        {
            foreach (var kind in new[] { "missing", "duplicate", "conditional", "module-owner" })
            {
                var doc = ConditionalModules(); var dynamic = Program(doc).Element(Ns + "Dynamic")!; var placement = dynamic.Element(Ns + "ParameterRefRef")!;
                if (kind == "duplicate") dynamic.Add(new XElement(placement));
                else if (kind == "module-owner") doc.Descendants(Ns + "ModuleDef").Single().Element(Ns + "Dynamic")!.Add(new XElement(placement));
                else { placement.Remove(); if (kind == "conditional") dynamic.Element(Ns + "choose")!.Elements().First().Add(placement); }
                var f = Load(directory, doc);
                if (!Unchanged(f, () => Refused(Context(f), GeneratedTableIssueCode.UnsupportedStructureActivation))) return false;
            }
            return true;
        });
        Check("module-local or union declarations cannot supply a root call selector", () =>
        {
            foreach (var kind in new[] { "module", "union" })
            {
                var doc = ConditionalModules(); var stat = Program(doc).Element(Ns + "Static")!; var parameter = stat.Element(Ns + "Parameters")!.Elements().Single();
                parameter.Remove();
                if (kind == "module") doc.Descendants(Ns + "ModuleDef").Single().Element(Ns + "Static")!.Add(new XElement(Ns + "Parameters", parameter));
                else stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Union", new XAttribute("Id", "U"), parameter));
                var f = Load(directory, doc);
                if (!Unchanged(f, () => Refused(Context(f), GeneratedTableIssueCode.UnsupportedStructureActivation))) return false;
            }
            return true;
        });
        Check("nonderived aliases are allowed but an assignment through any alias is refused", () =>
        {
            foreach (var tainted in new[] { false, true })
            {
                var doc = ConditionalModules(); var app = Program(doc);
                app.Element(Ns + "Static")!.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "Alias"), new XAttribute("RefId", "P")));
                if (tainted) app.Element(Ns + "Dynamic")!.Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "Alias"), new XAttribute("Value", "2")));
                var f = Load(directory, doc);
                if (!Unchanged(f, () => tainted ? Refused(Context(f), GeneratedTableIssueCode.UnsupportedActivationDependency) : Fact<bool>(Context(f), "IsAvailable"))) return false;
            }
            return true;
        });
        Check("a calculation binding on an alias taints every candidate call before runtime", () =>
        {
            var doc = ConditionalModules(); var stat = Program(doc).Element(Ns + "Static")!;
            stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "Alias"), new XAttribute("RefId", "P")));
            stat.Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + "ParameterCalculation", new XAttribute("Language", "JavaScript"),
                new XElement(Ns + "LRTransformation", "output=2;"), new XElement(Ns + "RParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "Alias"), new XAttribute("AliasName", "output"))))));
            var f = Load(directory, doc); f.Device.ParameterValues["R"] = "0"; var runtime = new Runtime();
            return Unchanged(f, () => Refused(NativeActivationChecks.Context(f, runtime), GeneratedTableIssueCode.UnsupportedActivationDependency) && runtime.Calls == 0);
        });
        Check("malformed arguments in an unselected branch are never skipped by source validation", () =>
        {
            var doc = ConditionalModules(); doc.Descendants(Ns + "Module").Last().Element(Ns + "NumericArg")!.SetAttributeValue("AllocatorRefId", "HiddenAllocator");
            UnrelatedCalculation(doc); var f = Load(directory, doc); f.Device.ParameterValues["R"] = "0"; var runtime = new Runtime();
            return Unchanged(f, () => Refused(NativeActivationChecks.Context(f, runtime), GeneratedTableIssueCode.UnsupportedStructureActivation) && runtime.Calls == 0);
        });
        Check("unknown inactive definition nodes and invalid parameter or object references fail before runtime", () =>
        {
            foreach (var kind in new[] { "unknown", "object", "parameter", "choose", "assignment", "object-container" })
            {
                var doc = ConditionalModules(); var dynamic = doc.Descendants(Ns + "ModuleDef").Single().Element(Ns + "Dynamic")!;
                if (kind == "object-container") dynamic.Parent!.Element(Ns + "Static")!.Element(Ns + "ComObjects")!.Name = Ns + "ComObjectTable";
                else dynamic.Add(kind switch
                {
                    "object" => new XElement(Ns + "ComObjectRefRef", new XAttribute("RefId", "Missing")),
                    "parameter" => new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "Missing")),
                    "choose" => new XElement(Ns + "choose", new XAttribute("ParamRefId", "Missing"), new XElement(Ns + "when", new XAttribute("test", "1"))),
                    "assignment" => new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "Missing"), new XAttribute("Value", "1")),
                    _ => new XElement(Ns + "UnknownWrapper"),
                });
                UnrelatedCalculation(doc); var f = Load(directory, doc); f.Device.ParameterValues["R"] = "0"; var runtime = new Runtime();
                if (!Unchanged(f, () => Refused(NativeActivationChecks.Context(f, runtime), kind == "object-container"
                    ? GeneratedTableIssueCode.InvalidObjectDeclaration : GeneratedTableIssueCode.UnsupportedStructureActivation) && runtime.Calls == 0)) return false;
            }
            var rootDocument = NativeCommunicationChecks.App(); Program(rootDocument).Element(Ns + "Static")!.Element(Ns + "ComObjectTable")!.Name = Ns + "ComObjects";
            UnrelatedCalculation(rootDocument); var rootFixture = Load(directory, rootDocument); var rootRuntime = new Runtime();
            return Unchanged(rootFixture, () => Refused(NativeActivationChecks.Context(rootFixture, rootRuntime), GeneratedTableIssueCode.InvalidObjectDeclaration) && rootRuntime.Calls == 0);
        });
        Check("conditional source changes invalidate the sealed selection", () =>
        {
            var f = Load(directory, ConditionalModules());
            return Unchanged(f, () =>
            {
                var context = Context(f); if (!Fact<bool>(context, "IsAvailable")) return false;
                var nodes = Fact<IReadOnlyList<XElement>>(Fact<object>(context, "Catalog"), "Nodes"); var branch = nodes.First(n => n.Name == Ns + "when");
                var text = (string?)branch.Attribute("test");
                try { branch.SetAttributeValue("test", "2"); return Refused(Resolved(context), GeneratedTableIssueCode.ActivationMismatch); }
                finally { branch.SetAttributeValue("test", text); }
            });
        });
        Check("two different root controllers require both original branch conditions", () =>
        {
            foreach (var outer in new[] { "0", "1", "2" })
                foreach (var inner in new[] { "0", "1", "2" })
                {
                    var f = Load(directory, NestedConditionalModules()); f.Device.ParameterValues["R"] = outer; f.Device.ParameterValues["Inner"] = inner;
                    if (!Unchanged(f, () =>
                    {
                        var context = Context(f); if (!Fact<bool>(context, "IsAvailable")) return false;
                        var snapshot = Fact<ApplicationSnapshot>(context, "Snapshot"); var expected = outer == "1" && inner != "0" ? "M" + inner + "_MI-1" : null;
                        return snapshot.ModuleInstances.Select(i => i.Id).SequenceEqual(expected is null ? [] : new[] { expected })
                            && Rows(Structure(context), "Occurrences").Length == (expected is null ? 0 : 1)
                            && Fact<IReadOnlySet<string>>(context, "AdditionalControllerReferences").SetEquals(new[] { "Inner", "R" });
                    })) return false;
                }
            return true;
        });
        Check("two-level call chains retain immutable original outer to inner source keys", () =>
        {
            var f = Load(directory, NestedConditionalModules());
            return Unchanged(f, () =>
            {
                var context = Context(f); if (!Fact<bool>(context, "IsAvailable")) return false;
                var catalog = Fact<object>(context, "Catalog"); var nodes = Fact<IReadOnlyList<XElement>>(catalog, "Nodes");
                var keyed = nodes.Select((node, key) => (node, key)).ToDictionary(p => p.node, p => p.key);
                var call = nodes.Single(n => n.Name == Ns + "Module" && (string?)n.Attribute("Id") == "M1");
                var chains = Fact<IReadOnlyDictionary<int, IReadOnlyList<int>>>(Fact<object>(catalog, "StructuralPlan"), "ConditionalWhenKeys");
                var chain = chains[keyed[call]];
                if (chain.Count != 2 || !chain.SequenceEqual(call.Ancestors(Ns + "when").Reverse().Select(n => keyed[n]))) return false;
                try { ((IList<int>)chain)[0] = chain[0]; return false; }
                catch (NotSupportedException) { return true; }
            });
        });
        Check("neither a missing outer nor a missing inner native state can authorize a selected call", () =>
        {
            var f = Load(directory, NestedConditionalModules());
            return Unchanged(f, () =>
            {
                var context = Context(f); if (!Fact<bool>(context, "IsAvailable")) return false;
                var activation = Fact<object>(context, "Activation"); var states = Rows(activation, "ActiveWhenStates");
                var selected = states.Where(s => Fact<string>(s, "ModuleInstanceId") == "").ToArray();
                if (selected.Length != 2) return false;
                var field = activation.GetType().GetField("<ActiveWhenStates>k__BackingField", Hidden)!; var original = field.GetValue(activation);
                try
                {
                    foreach (var omitted in selected)
                    {
                        var kept = states.Where(s => !ReferenceEquals(s, omitted)).ToArray(); var array = Array.CreateInstance(Core("NativeStateIdentity"), kept.Length);
                        for (var i = 0; i < kept.Length; i++) array.SetValue(kept[i], i);
                        field.SetValue(activation, array);
                        if (!Refused(Resolved(context))) return false;
                    }
                    return true;
                }
                finally { field.SetValue(activation, original); }
            });
        });
        Check("pure defaults at both levels select a call only inside the active outer branch", () =>
        {
            var doc = NestedConditionalModules(); var outer = Program(doc).Element(Ns + "Dynamic")!.Element(Ns + "choose")!;
            var outerDefault = outer.Elements().First(); outerDefault.Attribute("test")!.Remove(); outerDefault.SetAttributeValue("default", "true");
            var innerDefault = outerDefault.Element(Ns + "choose")!.Elements().Last(); innerDefault.Attribute("test")!.Remove(); innerDefault.SetAttributeValue("default", "true");
            var f = Load(directory, doc); f.Device.ParameterValues["R"] = "0"; f.Device.ParameterValues["Inner"] = "0";
            return Unchanged(f, () =>
            {
                var context = Context(f);
                return Fact<bool>(context, "IsAvailable") && Fact<ApplicationSnapshot>(context, "Snapshot").ModuleInstances.Single().Id == "M2_MI-1";
            });
        });
        Check("the same independently placed controller may select both levels without duplicate owners", () =>
        {
            var doc = NestedConditionalModules(); doc.Descendants(Ns + "choose").Last().SetAttributeValue("ParamRefId", "R");
            var f = Load(directory, doc);
            return Unchanged(f, () =>
            {
                var context = Context(f);
                return Fact<bool>(context, "IsAvailable") && Fact<ApplicationSnapshot>(context, "Snapshot").ModuleInstances.Single().Id == "M1_MI-1"
                    && Fact<IReadOnlySet<string>>(context, "AdditionalControllerReferences").SetEquals(new[] { "R" });
            });
        });
        Check("two-level KO-free selected and inactive inventories remain complete", () =>
        {
            foreach (var outer in new[] { "0", "1" })
            {
                var f = Load(directory, NestedConditionalModules(empty: true)); f.Device.ParameterValues["R"] = outer;
                if (!Unchanged(f, () =>
                {
                    var context = Context(f); if (!Fact<bool>(context, "IsAvailable")) return false;
                    return Fact<ApplicationSnapshot>(context, "Snapshot").ComObjects.Count == 0
                        && Rows(Structure(context), "Occurrences").Length == (outer == "1" ? 1 : 0)
                        && (outer == "0" || ChangedItems(context, _ => [], resolved: true)
                            && ChangedItems(context, items => items.Concat(items).ToArray(), resolved: true));
                })) return false;
            }
            return true;
        });
        Check("changing either original branch invalidates the paired structural proof", () =>
        {
            foreach (var inner in new[] { false, true })
            {
                var f = Load(directory, NestedConditionalModules());
                if (!Unchanged(f, () =>
                {
                    var context = Context(f); if (!Fact<bool>(context, "IsAvailable")) return false;
                    var nodes = Fact<IReadOnlyList<XElement>>(Fact<object>(context, "Catalog"), "Nodes");
                    var choose = nodes.Where(n => n.Name == Ns + "choose").ElementAt(inner ? 1 : 0); var branch = choose.Elements().First();
                    var value = (string?)branch.Attribute("test");
                    try { branch.SetAttributeValue("test", "0"); return Refused(Resolved(context), GeneratedTableIssueCode.ActivationMismatch); }
                    finally { branch.SetAttributeValue("test", value); }
                })) return false;
            }
            return true;
        });
        Check("saved outer and inner changes each stale the old selection without another retained evaluation", () =>
        {
            foreach (var id in new[] { "R", "Inner" })
            {
                var f = Load(directory, NestedConditionalModules());
                if (!Unchanged(f, () =>
                {
                    var before = Context(f); if (!Fact<bool>(before, "IsAvailable")) return false;
                    try
                    {
                        f.Device.ParameterValues[id] = id == "R" ? "0" : "2"; var after = Context(f);
                        return Fact<bool>(after, "IsAvailable") && Fact<ApplicationSnapshot>(after, "Snapshot").ModuleInstances.Select(m => m.Id)
                                .SequenceEqual(id == "R" ? [] : new[] { "M2_MI-1" })
                            && Fact<string>(Structure(before), "Fingerprint") != Fact<string>(Structure(after), "Fingerprint")
                            && !(bool)before.GetType().GetMethod("MatchesInputs", Hidden)!.Invoke(before, [f.Project, f.Device, NativeActivationChecks.Inputs(f)])!;
                    }
                    finally { f.Device.ParameterValues.Remove(id); }
                })) return false;
            }
            return true;
        });
        Check("overlapping matches at either level remain atomic activation mismatches", () =>
        {
            foreach (var inner in new[] { false, true })
            {
                var doc = NestedConditionalModules(); var choose = doc.Descendants(Ns + "choose").ElementAt(inner ? 1 : 0);
                choose.Elements().Last().SetAttributeValue("test", "1 2"); var f = Load(directory, doc);
                if (!Unchanged(f, () => Refused(Context(f), GeneratedTableIssueCode.ActivationMismatch))) return false;
            }
            return true;
        });
        Check("inner controller declarations retain root enum owner and nonderivation requirements", () =>
        {
            foreach (var kind in new[] { "number", "conditional-owner", "assignment" })
            {
                var doc = NestedConditionalModules(); var app = Program(doc); var stat = app.Element(Ns + "Static")!;
                if (kind == "number")
                {
                    var type = new XElement(Ns + "ParameterType", new XAttribute("Id", "Number"), new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", "8"), new XAttribute("minInclusive", "0"), new XAttribute("maxInclusive", "2")));
                    stat.Element(Ns + "ParameterTypes")!.Add(type); stat.Element(Ns + "Parameters")!.Elements().Last().SetAttributeValue("ParameterType", "Number");
                }
                else if (kind == "conditional-owner")
                {
                    var placement = app.Element(Ns + "Dynamic")!.Elements(Ns + "ParameterRefRef").First(p => (string?)p.Attribute("RefId") == "Inner");
                    placement.Remove(); doc.Descendants(Ns + "when").First().Add(placement);
                }
                else app.Element(Ns + "Dynamic")!.Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "Inner"), new XAttribute("Value", "2")));
                var f = Load(directory, doc); f.Device.ParameterValues["R"] = "0";
                if (!Unchanged(f, () => Refused(Context(f), kind == "assignment" ? GeneratedTableIssueCode.UnsupportedActivationDependency : GeneratedTableIssueCode.UnsupportedStructureActivation))) return false;
            }
            return true;
        });
        Check("a when without its own immediate choose cannot form a nested ancestor pair", () =>
        {
            var doc = NestedConditionalModules(); var inner = doc.Descendants(Ns + "choose").Last(); var branch = inner.Elements().First();
            branch.Remove(); inner.Add(new XElement(Ns + "ParameterBlock", new XAttribute("Id", "Misplaced"), branch));
            var f = Load(directory, doc); f.Device.ParameterValues["R"] = "0";
            return Unchanged(f, () => Refused(Context(f), GeneratedTableIssueCode.UnsupportedStructureActivation));
        });
        Check("malformed inactive nested call arguments are rejected before any optional runtime", () =>
        {
            var doc = NestedConditionalModules(); doc.Descendants(Ns + "Module").Last().Element(Ns + "NumericArg")!.SetAttributeValue("BaseValue", "Unproven");
            UnrelatedCalculation(doc); var f = Load(directory, doc); f.Device.ParameterValues["R"] = "0"; var runtime = new Runtime();
            return Unchanged(f, () => Refused(NativeActivationChecks.Context(f, runtime), GeneratedTableIssueCode.UnsupportedStructureActivation) && runtime.Calls == 0);
        });
        Check("public structural refusal has no payload or binding and preserves legacy expansion", () =>
        {
            var moduleDocument = NativeCommunicationChecks.Modules(); var moduleType = Core("ApplicationModules");
            var expand = moduleType.GetMethod("Expand", Hidden)!; var original = moduleDocument.ToString(SaveOptions.DisableFormatting);
            var expanded = (XElement)expand.Invoke(null, [moduleDocument.Root!, null, null, false, null])!;
            var instances = (IReadOnlyList<ApplicationModuleInstance>)moduleType.GetMethod("ReadInstances", Hidden)!.Invoke(null, [expanded])!;
            if (expand.GetParameters().Length != 5 || instances.Count != 2 || instances[0].Arguments.Single().Value != "2"
                || expanded.Annotation(Core("NativeCommunicationOccurrenceManifest")) is not null || original != moduleDocument.ToString(SaveOptions.DisableFormatting)) return false;
            var app = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.SystemB2ByteAssociations);
            var appProgram = Program(app); appProgram.Add(new XElement(Program(moduleDocument).Element(Ns + "ModuleDefs")!));
            var call = new XElement(Program(moduleDocument).Element(Ns + "Dynamic")!.Elements(Ns + "Module").First());
            appProgram.Element(Ns + "Dynamic")!.Add(new XElement(Ns + "choose", new XAttribute("ParamRefId", "Unknown"), new XElement(Ns + "when", new XAttribute("test", "0"), call)));
            var hardware = XDocument.Parse("<KNX xmlns='http://knx.org/xml/project/20'><ManufacturerData><Manufacturer RefId='M-0001'><Hardware><Hardware Id='M-0001_H-image'><Hardware2Programs><Hardware2Program Id='M-0001_H-image_A-0100-10-0000'><ApplicationProgramRef RefId='M-0001_A-0100-10-0000'/></Hardware2Program></Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>");
            var f = ArchivedImageSourceChecks.Load(directory, hardware: hardware, app: app, master: GeneratedTableSerializerChecks.Master(GeneratedTableFormat.SystemB2ByteAssociations));
            return Unchanged(f, () =>
            {
                var request = new LoadProcedureRequest("all", LoadProcedureAccess.Remote, LoadParts.Full);
                var ordinary = OfflineApplicationBuildPlan.Preview(f.Project, f.Device, request);
                var failed = OfflineApplicationBuildPlan.Preview(f.Project, f.Device, request, new GeneratedTableRequest(GeneratedTableFormat.SystemB2ByteAssociations));
                var again = OfflineApplicationBuildPlan.Preview(f.Project, f.Device, request);
                return ordinary.IsAvailable && again.Binding?.Fingerprint == ordinary.Binding?.Fingerprint
                    && again.Procedure?.WitnessHash == ordinary.Procedure?.WitnessHash && !failed.IsAvailable && failed.Binding is null
                    && failed.GeneratedTables is { IsAvailable: false, Binding: null, Payloads.Count: 0, ProcedureSizeAdjustments.Count: 0 } tables
                    && tables.Issues.Single().Code == GeneratedTableIssueCode.UnsupportedStructureActivation
                    && !failed.ExecutionEligible && !failed.CompleteApplicationImage && !failed.DownloadEligible;
            });
        });
        Console.WriteLine($"NativeStructure: {checks - failures}/{checks} passed");
        return failures;
    }
}
