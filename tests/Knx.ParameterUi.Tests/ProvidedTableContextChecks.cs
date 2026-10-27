using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class ProvidedTableContextChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly LoadProcedureRequest Full = new("all", LoadProcedureAccess.Remote, LoadParts.Full);
    private static readonly Type Compiler = typeof(Project).Assembly.GetType("Knx.GeneratedTableCompiler", true)!;
    private static T Fact<T>(object value, string name) => NativeActivationChecks.Fact<T>(value, name);
    private static XDocument Application(object inputs) => Fact<XDocument>(inputs, "Application");
    private static XDocument? Master(object inputs) => Fact<XDocument?>(inputs, "Master");
    private static object Inputs(ArchivedImageSourceChecks.Fixture fixture) => NativeActivationChecks.Inputs(fixture);
    private static object Context(ArchivedImageSourceChecks.Fixture fixture, object inputs,
        IParameterScriptRuntime? scripts = null, string[]? additional = null)
        => typeof(Project).Assembly.GetType("Knx.NativeApplicationEvaluationContext", true)!.GetMethod("Create", Hidden)!
            .Invoke(null, [fixture.Project, fixture.Device, inputs, additional, scripts])!;
    private static GeneratedTablePreview Compile(ArchivedImageSourceChecks.Fixture fixture, object inputs, LoadProcedurePlan procedure,
        GeneratedTableFormat format, object? context)
        => (GeneratedTablePreview)Compiler.GetMethod("CompileFromContext", Hidden)!
            .Invoke(null, [fixture.Project, fixture.Device, inputs, procedure, new GeneratedTableRequest(format), context])!;
    private static GeneratedTablePreview Legacy(ArchivedImageSourceChecks.Fixture fixture, object inputs, LoadProcedurePlan procedure,
        GeneratedTableFormat format, IParameterScriptRuntime? scripts = null)
        => (GeneratedTablePreview)Compiler.GetMethod("Compile", Hidden)!
            .Invoke(null, [fixture.Project, fixture.Device, inputs, procedure, new GeneratedTableRequest(format), scripts])!;
    private static LoadProcedurePlan Procedure(object inputs, LoadProcedureRequest? request = null)
        => LoadProcedureCompiler.Compile(Application(inputs), Master(inputs), request ?? Full);
    private static XElement Program(XDocument doc) => doc.Descendants(Ns + "ApplicationProgram").Single();

    private static ArchivedImageSourceChecks.Fixture Load(string directory, GeneratedTableFormat format = GeneratedTableFormat.M112,
        XDocument? app = null, XDocument? master = null, XDocument? hardware = null)
    {
        hardware ??= XDocument.Parse("<KNX xmlns='http://knx.org/xml/project/20'><ManufacturerData><Manufacturer RefId='M-0001'><Hardware><Hardware Id='M-0001_H-image'><Hardware2Programs><Hardware2Program Id='M-0001_H-image_A-0100-10-0000'><ApplicationProgramRef RefId='M-0001_A-0100-10-0000'/></Hardware2Program></Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>");
        var fixture = ArchivedImageSourceChecks.Load(directory, hardware: hardware,
            app: app ?? GeneratedTableSerializerChecks.Application(format, 257, 300), master: master ?? GeneratedTableSerializerChecks.Master(format));
        var range = new GroupRange { Id = "GR-context", RangeStart = 0, RangeEnd = 65535 };
        fixture.Project.Installations[0].MainRanges.Add(range);
        foreach (var (id, address) in new[] { ("GA1", 0x0101), ("GA2", 0x0A03), ("GA3", 0x0B01) })
        {
            range.Addresses.Add(new() { Id = id, Name = id, Address = new((ushort)address) });
            fixture.Project.Workbench.Groups[id] = new() { Secure = false };
        }
        var numbers = format == GeneratedTableFormat.M112 ? new[] { 0, 1, 3 } : new[] { 1, 2, 257 };
        for (var i = 0; i < numbers.Length; i++)
        {
            var obj = new ComObjectInstanceRef { Id = "O-" + numbers[i] + "_R-0", ComObjectId = "O-" + numbers[i], Number = numbers[i] };
            // The two-byte profile retains the high active slot without associating it.
            var groups = i == 0 ? new[] { "GA1" } : i == 1 ? new[] { "GA2", "GA1" }
                : format == GeneratedTableFormat.SystemB2ByteAssociations ? [] : new[] { "GA3", "GA2" };
            obj.Links.AddRange(groups); fixture.Device.ComObjectRefs.Add(obj);
            if (groups.Length > 0) fixture.Project.Workbench.Objects[new ObjectKey(fixture.Device.Id, obj.Id).ToString()] = new() { SendingGroupId = groups[0] };
        }
        return fixture;
    }

    private static void AddParameters(XDocument app, bool enumeration = false)
    {
        var stat = Program(app).Element(Ns + "Static")!;
        var shape = enumeration
            ? new XElement(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", 8),
                new XElement(Ns + "Enumeration", new XAttribute("Id", "E0"), new XAttribute("Value", 0)),
                new XElement(Ns + "Enumeration", new XAttribute("Id", "E1"), new XAttribute("Value", 1)))
            : new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", 8),
                new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 255));
        stat.Add(new XElement(Ns + "ParameterTypes", new XElement(Ns + "ParameterType", new XAttribute("Id", "T-extra"), shape)),
            new XElement(Ns + "Parameters", new XElement(Ns + "Parameter", new XAttribute("Id", "P-extra"),
                new XAttribute("ParameterType", "T-extra"), new XAttribute("Value", 1))),
            new XElement(Ns + "ParameterRefs", new XElement(Ns + "ParameterRef", new XAttribute("Id", "R-extra"), new XAttribute("RefId", "P-extra"))));
        Program(app).Element(Ns + "Dynamic")!.AddFirst(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R-extra")));
    }

    private static XDocument ScriptApplication()
    {
        var app = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.M112); AddParameters(app);
        var stat = Program(app).Element(Ns + "Static")!;
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "P-output"),
            new XAttribute("ParameterType", "T-extra"), new XAttribute("Value", 1)));
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "R-output"), new XAttribute("RefId", "P-output")));
        stat.Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + "ParameterCalculation", new XAttribute("Language", "JavaScript"),
            new XElement(Ns + "LRTransformation", "output=input;"),
            new XElement(Ns + "LParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R-extra"), new XAttribute("AliasName", "input"))),
            new XElement(Ns + "RParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R-output"), new XAttribute("AliasName", "output"))))));
        return app;
    }

    private sealed class Runtime : IParameterScriptRuntime
    {
        internal int Calls;
        public IReadOnlyDictionary<string, object?> Execute(string source, IReadOnlyDictionary<string, object?> inputs, IReadOnlyList<string> outputs)
        { Calls++; return outputs.ToDictionary(id => id, _ => (object?)1d, StringComparer.Ordinal); }
    }

    private static string Snapshot(GeneratedTablePreview result) => JsonSerializer.Serialize(new
    {
        result.Scope, result.ProfileVersion, result.IsAvailable, result.Format, result.NativeWitnessHash,
        result.ExecutionEligible, result.CompleteApplicationImage, result.DownloadEligible, result.Binding, result.Issues,
        result.Requirements, result.ProcedureSizeAdjustments,
        payloads = result.Payloads.Select(p => new
        {
            p.Role, p.Arena, p.ByteOffset, p.Origin, p.ResourceId, p.Length,
            data = p.Data.ToHexString(), mask = p.WriteMask.ToHexString(), before = p.BeforeData?.ToHexString(),
            beforeMask = p.BeforeMask?.ToHexString(), afterMask = p.AfterMask?.ToHexString(),
        }).ToArray(),
    });
    private static bool Empty(GeneratedTablePreview result, GeneratedTableIssueCode code) => !result.IsAvailable
        && result.Binding is null && result.NativeWitnessHash is null && result.Payloads.Count == 0
        && result.ProcedureSizeAdjustments.Count == 0 && result.Requirements.Count == 0
        && result.Issues.Count == 1 && result.Issues[0].Code == code;

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool value) { checks++; Console.WriteLine((value ? "PASS " : "FAIL ") + "ProvidedTableContext " + name); if (!value) failures++; }
        foreach (var format in Enum.GetValues<GeneratedTableFormat>())
        {
            var f = Load(directory, format); var inputs = Inputs(f); var context = Context(f, inputs); var procedure = Procedure(inputs);
            var bytes = File.ReadAllBytes(f.Path); var model = Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting);
            var provided = Compile(f, inputs, procedure, format, context); var legacy = Legacy(f, inputs, procedure, format);
            Check(format + " complete payload and binding equal the ordinary compiler", provided.IsAvailable && legacy.IsAvailable
                && Snapshot(provided) == Snapshot(legacy) && provided.Payloads.Count == 3);
            Check(format + " existing binding replay remains current", provided.Binding?.IsCurrent(f.Project, f.Device) == true
                && legacy.Binding?.IsCurrent(f.Project, f.Device) == true && provided.Binding?.RequiresScripts == false);
            Check(format + " source and current model are never mutated", bytes.SequenceEqual(File.ReadAllBytes(f.Path))
                && model == Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting));
        }

        var scripted = Load(directory, app: ScriptApplication()); var scriptInputs = Inputs(scripted); var runtime = new Runtime();
        var scriptContext = Context(scripted, scriptInputs, runtime); var calls = runtime.Calls; var scriptProcedure = Procedure(scriptInputs);
        Check("explicit runtime creates one usable context", Fact<bool>(scriptContext, "IsAvailable") && calls > 0);
        var shared = Compile(scripted, scriptInputs, scriptProcedure, GeneratedTableFormat.M112, scriptContext);
        var sharedAgain = Compile(scripted, scriptInputs, scriptProcedure, GeneratedTableFormat.M112, scriptContext);
        Check("repeated supplied-context compilation never executes the runtime", shared.IsAvailable && sharedAgain.IsAvailable
            && runtime.Calls == calls && Snapshot(shared) == Snapshot(sharedAgain));
        var oldRuntime = new Runtime(); var oldScripted = Legacy(scripted, scriptInputs, scriptProcedure, GeneratedTableFormat.M112, oldRuntime);
        Check("runtime-capability payload and binding exactly match the legacy path", oldRuntime.Calls > 0 && Snapshot(shared) == Snapshot(oldScripted));
        Check("script replay capability is preserved", shared.Binding?.RequiresScripts == true
            && !shared.Binding.IsCurrent(scripted.Project, scripted.Device)
            && shared.Binding.IsCurrent(scripted.Project, scripted.Device, new Runtime()));
        var zero = Load(directory); var zeroInputs = Inputs(zero); var zeroRuntime = new Runtime();
        var zeroContext = Context(zero, zeroInputs, zeroRuntime);
        var zeroResult = Compile(zero, zeroInputs, Procedure(zeroInputs), GeneratedTableFormat.M112, zeroContext);
        Check("zero executions do not erase an explicitly provided runtime capability", zeroResult.IsAvailable && zeroRuntime.Calls == 0
            && zeroResult.Binding?.RequiresScripts == true && !zeroResult.Binding.IsCurrent(zero.Project, zero.Device)
            && zeroResult.Binding.IsCurrent(zero.Project, zero.Device, new Runtime()));

        var extraApp = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.M112); AddParameters(extraApp);
        var extra = Load(directory, app: extraApp); var extraInputs = Inputs(extra); var extraContext = Context(extra, extraInputs, additional: ["R-extra"]);
        Check("the additional-controller refusal fixture has a valid context", Fact<bool>(extraContext, "IsAvailable"));
        Check("non-replayable additional caller controllers are refused", Empty(Compile(extra, extraInputs, Procedure(extraInputs), GeneratedTableFormat.M112, extraContext), GeneratedTableIssueCode.InvalidRequest));

        var conditionalApp = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.M112); AddParameters(conditionalApp, enumeration: true);
        Program(conditionalApp).Add(new XElement(Ns + "ModuleDefs", new XElement(Ns + "ModuleDef", new XAttribute("Id", "MD"),
            new XElement(Ns + "Arguments", new XElement(Ns + "Argument", new XAttribute("Id", "MD_A"), new XAttribute("Type", "Numeric"), new XAttribute("Name", "Base"))),
            new XElement(Ns + "Static"), new XElement(Ns + "Dynamic"))));
        Program(conditionalApp).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "choose", new XAttribute("ParamRefId", "R-extra"),
            new XElement(Ns + "when", new XAttribute("test", "1"), new XElement(Ns + "Module", new XAttribute("Id", "CALL"), new XAttribute("RefId", "MD"),
                new XElement(Ns + "NumericArg", new XAttribute("RefId", "MD_A"), new XAttribute("Value", 0))))));
        var conditional = Load(directory, app: conditionalApp); var conditionalInputs = Inputs(conditional); var conditionalContext = Context(conditional, conditionalInputs);
        Check("ordinary structural controllers remain an allowed nonempty set", Fact<bool>(conditionalContext, "IsAvailable")
            && Fact<IReadOnlySet<string>>(conditionalContext, "AdditionalControllerReferences").SetEquals(["R-extra"]));
        var conditionalProcedure = Procedure(conditionalInputs); var conditionalResult = Compile(conditional, conditionalInputs, conditionalProcedure, GeneratedTableFormat.M112, conditionalContext);
        Check("structural-controller compilation stays fully legacy-replayable", conditionalResult.IsAvailable
            && Snapshot(conditionalResult) == Snapshot(Legacy(conditional, conditionalInputs, conditionalProcedure, GeneratedTableFormat.M112))
            && conditionalResult.Binding?.IsCurrent(conditional.Project, conditional.Device) == true);

        void Taint(string name, Action<ArchivedImageSourceChecks.Fixture, object, object> mutate)
        {
            var f = Load(directory); var inputs = Inputs(f); var context = Context(f, inputs); var procedure = Procedure(inputs);
            mutate(f, inputs, context);
            var bytes = File.ReadAllBytes(f.Path); var model = Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting);
            Check(name, Empty(Compile(f, inputs, procedure, GeneratedTableFormat.M112, context), GeneratedTableIssueCode.InputsChanged)
                && bytes.SequenceEqual(File.ReadAllBytes(f.Path)) && model == Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting));
        }
        Taint("changed application document refuses atomically", (_, inputs, _) => Program(Application(inputs)).SetAttributeValue("Changed", "1"));
        Taint("changed master document refuses atomically", (_, inputs, _) => Master(inputs)!.Root!.SetAttributeValue("Changed", "1"));
        Taint("changed owned catalogue refuses atomically", (_, _, context) => Fact<XElement>(context, "OriginalApplication").SetAttributeValue("Changed", "1"));
        Taint("changed expanded snapshot refuses atomically", (_, _, context) => Fact<XElement>(Fact<ApplicationSnapshot>(context, "Snapshot").Evaluation, "ExpandedRoot").SetAttributeValue("Changed", "1"));
        Taint("changed saved values refuse atomically", (f, _, _) => f.Device.ParameterValues["R-extra"] = "2");
        Taint("changed links refuse atomically", (f, _, _) => f.Device.ComObjectRefs[0].Links.Clear());
        Taint("changed native metadata refuses atomically", (f, _, _) => f.Project.Workbench.Objects[new ObjectKey(f.Device.Id, f.Device.ComObjectRefs[0].Id).ToString()].Flags = "C");
        Taint("changed revision refuses atomically", (f, _, _) => typeof(Project).GetProperty(nameof(Project.ApplicationDataRevision))!.SetValue(f.Project, f.Project.ApplicationDataRevision + 1));

        var own = Load(directory); var ownInputs = Inputs(own); var ownContext = Context(own, ownInputs); var ownProcedure = Procedure(ownInputs);
        var foreign = Load(directory); var foreignInputs = Inputs(foreign); var foreignContext = Context(foreign, foreignInputs);
        Check("same ids do not authorize a foreign context", own.Project.Id == foreign.Project.Id && own.Device.Id == foreign.Device.Id
            && Empty(Compile(own, ownInputs, ownProcedure, GeneratedTableFormat.M112, foreignContext), GeneratedTableIssueCode.InputsChanged));
        Check("foreign source bindings cannot be substituted", Empty(Compile(own, foreignInputs, ownProcedure, GeneratedTableFormat.M112, ownContext), GeneratedTableIssueCode.InputsChanged));
        var reloaded = Knxproj.Load(own.Path); var replacement = own with { Project = reloaded, Device = ProjectQuery.DeviceList(reloaded).Single() };
        Check("reloading the same project id invalidates an old context", replacement.Project.Id == own.Project.Id && replacement.Device.Id == own.Device.Id
            && Empty(Compile(replacement, Inputs(replacement), ownProcedure, GeneratedTableFormat.M112, ownContext), GeneratedTableIssueCode.InputsChanged));
        Check("null context never falls back to evaluation", Empty(Compile(own, ownInputs, ownProcedure, GeneratedTableFormat.M112, null), GeneratedTableIssueCode.InvalidRequest));
        Check("invalid requested format produces no artifacts", Empty(Compile(own, ownInputs, ownProcedure, (GeneratedTableFormat)99, ownContext), GeneratedTableIssueCode.InvalidRequest));

        var otherApp = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.M112);
        Program(otherApp).Descendants(Ns + "LoadProcedure").Single().AddFirst(new XElement(Ns + "LdCtrlRestart"));
        var otherProcedure = LoadProcedureCompiler.Compile(otherApp, Master(ownInputs), Full);
        Check("an available procedure from another source cannot be paired", otherProcedure.IsAvailable
            && Empty(Compile(own, ownInputs, otherProcedure, GeneratedTableFormat.M112, ownContext), GeneratedTableIssueCode.InputsChanged));
        var otherMaster = GeneratedTableSerializerChecks.Master(GeneratedTableFormat.M112);
        otherMaster.Descendants(Ns + "ResourceType").First().SetAttributeValue("Length", 2);
        var masterProcedure = LoadProcedureCompiler.Compile(Application(ownInputs), otherMaster, Full);
        Check("another selected master profile cannot be paired", masterProcedure.IsAvailable
            && Empty(Compile(own, ownInputs, masterProcedure, GeneratedTableFormat.M112, ownContext), GeneratedTableIssueCode.InputsChanged));
        var unavailable = LoadProcedureCompiler.Compile(new XDocument(new XElement("invalid")), null, Full);
        Check("unavailable procedure is refused without emission", Empty(Compile(own, ownInputs, unavailable, GeneratedTableFormat.M112, ownContext), GeneratedTableIssueCode.InvalidRequest));
        var noParts = Procedure(ownInputs, Full with { Parts = LoadParts.None });
        Check("ordinary parts gate still applies", Empty(Compile(own, ownInputs, noParts, GeneratedTableFormat.M112, ownContext), GeneratedTableIssueCode.InvalidRequest));

        var copied = Load(directory, GeneratedTableFormat.SystemB4ByteAssociations); var copiedInputs = Inputs(copied);
        var copiedContext = Context(copied, copiedInputs); var copiedProcedure = Procedure(copiedInputs);
        var alteredApp = new XDocument(Application(copiedInputs));
        alteredApp.Descendants(Ns + "LdCtrlRelSegment").First().SetAttributeValue("Size", 1234);
        var alteredProcedure = LoadProcedureCompiler.Compile(alteredApp, Master(copiedInputs), Full);
        var constructor = typeof(LoadProcedurePlan).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 12);
        var forged = (LoadProcedurePlan)constructor.Invoke([copiedProcedure.ApplicationId, copiedProcedure.MaskVersionId,
            copiedProcedure.Style!.Value, copiedProcedure.Request, copiedProcedure.ApplicationDefinitionHash,
            copiedProcedure.SelectedProfileHash, copiedProcedure.WitnessHash, alteredProcedure.Controls.ToArray(),
            copiedProcedure.MergeDecisions.ToArray(), copiedProcedure.ResourceReferences.ToArray(),
            copiedProcedure.TableRequirements.ToArray(), copiedProcedure.Requirements.ToArray()]);
        var forgedLegacy = Legacy(copied, copiedInputs, forged, GeneratedTableFormat.SystemB4ByteAssociations);
        var forgedResult = Compile(copied, copiedInputs, forged, GeneratedTableFormat.SystemB4ByteAssociations, copiedContext);
        Check("a copied witness cannot substitute foreign controls or adjustments", forgedLegacy.IsAvailable && forgedResult.IsAvailable
            && forgedLegacy.ProcedureSizeAdjustments.Any(a => a.BeforeValue == 1234)
            && !forgedResult.ProcedureSizeAdjustments.Any(a => a.BeforeValue == 1234)
            && Snapshot(forgedResult) == Snapshot(Compile(copied, copiedInputs, copiedProcedure, GeneratedTableFormat.SystemB4ByteAssociations, copiedContext)));

        var extendedApp = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.SystemB4ByteAssociations, 257, 300);
        Program(extendedApp).Descendants(Ns + "LoadProcedure").Single().AddFirst(new XElement(Ns + "LdCtrlCompareRelMem", new XAttribute("ObjIdx", 4),
            new XAttribute("Offset", 0), new XAttribute("Size", 1), new XAttribute("InlineData", "00"), new XAttribute("Mask", "FF"), new XAttribute("Invert", "false")));
        var extended = Load(directory, GeneratedTableFormat.SystemB4ByteAssociations, extendedApp); var extendedInputs = Inputs(extended);
        var resolved = OfflineApplicationBuildPlan.PreviewResolved(extended.Project, extended.Device, Full).Procedure;
        Check("resolved profile two is explicitly outside the supplied table path", resolved is { IsAvailable: true, ProfileVersion: 2 }
            && Empty(Compile(extended, extendedInputs, resolved, GeneratedTableFormat.SystemB4ByteAssociations, Context(extended, extendedInputs)), GeneratedTableIssueCode.InvalidRequest));

        var badApp = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.M112); Program(badApp).SetAttributeValue("RequiresExternalSoftware", "true");
        var bad = Load(directory, app: badApp); var badInputs = Inputs(bad); var badContext = Context(bad, badInputs);
        Check("unavailable context keeps its precise refusal without fallback", Empty(Compile(bad, badInputs, Procedure(badInputs), GeneratedTableFormat.M112, badContext), GeneratedTableIssueCode.ExternalConfigurationUnaccounted));
        var capacityApp = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.M112); capacityApp.Descendants(Ns + "AddressTable").Single().SetAttributeValue("MaxEntries", 0);
        var capacity = Load(directory, app: capacityApp); var capacityInputs = Inputs(capacity); var capacityProcedure = Procedure(capacityInputs);
        var capacityResult = Compile(capacity, capacityInputs, capacityProcedure, GeneratedTableFormat.M112, Context(capacity, capacityInputs));
        Check("legacy capacity failure and all empty output fields stay exact", Empty(capacityResult, GeneratedTableIssueCode.TableCapacityExceeded)
            && Snapshot(capacityResult) == Snapshot(Legacy(capacity, capacityInputs, capacityProcedure, GeneratedTableFormat.M112)));
        Console.WriteLine($"Provided table context checks: {checks}, failures: {failures}");
        return failures;
    }
}
