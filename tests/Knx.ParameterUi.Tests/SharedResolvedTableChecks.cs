using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class SharedResolvedTableChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Compiler = typeof(Project).Assembly.GetType("Knx.GeneratedTableCompiler", true)!;
    private static readonly Type ContextType = typeof(Project).Assembly.GetType("Knx.NativeApplicationEvaluationContext", true)!;
    private static readonly Type ViewType = typeof(Project).Assembly.GetType("Knx.NativeProcedureEvaluationContext", true)!;
    private static readonly LoadProcedureRequest Full = new("all", LoadProcedureAccess.Remote, LoadParts.Full);
    private static T Get<T>(object value, string name) => NativeActivationChecks.Fact<T>(value, name);
    private static XElement Program(XDocument app) => app.Descendants(Ns + "ApplicationProgram").Single();
    private static XElement Static(XDocument app) => Program(app).Element(Ns + "Static")!;
    private static XElement Write(string data) => new(Ns + "LdCtrlWriteProp", new XAttribute("ObjIdx", 4), new XAttribute("PropId", 27),
        new XAttribute("Verify", false), new XAttribute("InlineData", data));
    private static XDocument App(GeneratedTableFormat format, int lastActive = 4, int maximum = 5, bool conditional = true)
    {
        var app = GeneratedTableSerializerChecks.Application(format, lastActive, maximum); var stat = Static(app);
        stat.Add(new XElement(Ns + "ParameterTypes", new XElement(Ns + "ParameterType", new XAttribute("Id", "T"),
                new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", 8), new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 255)))),
            new XElement(Ns + "Parameters", new XElement(Ns + "Parameter", new XAttribute("Id", "P"), new XAttribute("ParameterType", "T"), new XAttribute("Value", 1))),
            new XElement(Ns + "ParameterRefs", new XElement(Ns + "ParameterRef", new XAttribute("Id", "R"), new XAttribute("RefId", "P"))));
        Program(app).Element(Ns + "Dynamic")!.AddFirst(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R")));
        if (conditional)
        {
            var procedure = stat.Descendants(Ns + "LoadProcedure").Single(); var segments = procedure.Elements(Ns + "LdCtrlRelSegment").ToArray();
            var selected = segments.Select(s => new XElement(s)).ToArray(); var fallback = segments.Select(s => new XElement(s)).ToArray();
            foreach (var segment in fallback) segment.SetAttributeValue("Size", 7000);
            foreach (var segment in segments) segment.Remove();
            procedure.Elements().First().AddAfterSelf(new XElement(Ns + "choose", new XAttribute("ParamRefId", "R"),
                new XElement(Ns + "when", new XAttribute("test", "1"), selected, Write("11")),
                new XElement(Ns + "when", new XAttribute("default", true), fallback, Write("22"))));
        }
        return app;
    }
    private static XDocument Hardware() => XDocument.Parse("""
        <KNX xmlns="http://knx.org/xml/project/20"><ManufacturerData><Manufacturer RefId="M-0001"><Hardware><Hardware Id="M-0001_H-image"><Hardware2Programs>
        <Hardware2Program Id="M-0001_H-image_A-0100-10-0000"><ApplicationProgramRef RefId="M-0001_A-0100-10-0000"/></Hardware2Program>
        </Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
        """);
    private sealed record Fixture(ArchivedImageSourceChecks.Fixture Source, GeneratedTableFormat Format, object Inputs, object Prepared, object Application, object View);
    private static Fixture Load(string directory, GeneratedTableFormat format = GeneratedTableFormat.M112, XDocument? app = null,
        string? value = null, LoadProcedureRequest? request = null, int[]? numbers = null, XDocument? hardware = null)
    {
        var source = ArchivedImageSourceChecks.Load(directory, app: app ?? App(format), hardware: hardware ?? Hardware(), master: GeneratedTableSerializerChecks.Master(format));
        var range = new GroupRange { Id = "GR-shared", Name = "Synthetic groups", RangeStart = 0, RangeEnd = 65535 };
        source.Project.Installations[0].MainRanges.Add(range);
        foreach (var (id, address) in new[] { ("GA1", 0x0101), ("GA2", 0x0A03), ("GA3", 0x0B01) })
        { range.Addresses.Add(new() { Id = id, Name = id, Address = new((ushort)address) }); source.Project.Workbench.Groups[id] = new() { Secure = false }; }
        numbers ??= format == GeneratedTableFormat.M112 ? [0, 1, 3] : [1, 2, 4];
        for (var i = 0; i < numbers.Length; i++)
        {
            var row = new ComObjectInstanceRef { Id = "O-" + numbers[i] + "_R-0", ComObjectId = "O-" + numbers[i], Number = numbers[i] };
            row.Links.AddRange(i == 0 ? ["GA1"] : i == 1 ? ["GA2", "GA1"] : ["GA3", "GA2"]); source.Device.ComObjectRefs.Add(row);
            source.Project.Workbench.Objects[new ObjectKey(source.Device.Id, row.Id).ToString()] = new() { SendingGroupId = row.Links[0] };
        }
        if (value is not null) source.Device.ParameterValues["R"] = value;
        var inputs = typeof(ArchivedImageSources).GetMethod("ReadInputs", Hidden)!.Invoke(null, [source.Project, source.Device])!;
        var prepared = typeof(LoadProcedureCompiler).GetMethod("PrepareResolved", Hidden)!.Invoke(null, [inputs, request ?? Full])!;
        var refs = Get<IReadOnlyList<string>>(prepared, "ControllerReferences");
        var application = ContextType.GetMethod("Create", Hidden)!.Invoke(null, [source.Project, source.Device, inputs, refs, null])!;
        var view = ViewType.GetMethod("FromApplicationContext", Hidden)!.Invoke(null, [source.Project, source.Device, inputs, prepared, application])!;
        return new(source, format, inputs, prepared, application, view);
    }
    private static object Emit(Fixture f, object? inputs = null, object? prepared = null, object? view = null, object? application = null, GeneratedTableFormat? format = null)
        => Compiler.GetMethod("CompileResolvedFromContext", Hidden)!.Invoke(null,
            [f.Source.Project, f.Source.Device, inputs ?? f.Inputs, prepared ?? f.Prepared, view ?? f.View, new GeneratedTableRequest(format ?? f.Format), application ?? f.Application])!;
    private static IReadOnlyList<GeneratedTablePayload> Payloads(object result) => Get<IReadOnlyList<GeneratedTablePayload>>(result, "Payloads");
    private static LoadProcedurePlan Procedure(object result) => Get<LoadProcedurePlan>(result, "ActualProcedure");
    private static bool Empty(object result, GeneratedTableIssueCode? code = null) => !Get<bool>(result, "IsAvailable")
        && Get<string?>(result, "Fingerprint") is null && Get<string?>(result, "PayloadHash") is null && Get<string?>(result, "NativeWitnessHash") is null
        && Get<string?>(result, "SourceFingerprint") is null && Get<string?>(result, "ApplicationDefinitionHash") is null && Get<string?>(result, "ProjectInputHash") is null
        && Get<LoadProcedurePlan?>(result, "ActualProcedure") is null && Payloads(result).Count == 0
        && Get<IReadOnlyList<GeneratedTableProcedureAdjustment>>(result, "ProcedureSizeAdjustments").Count == 0
        && Get<IReadOnlyList<GeneratedTableRequirement>>(result, "Requirements").Count == 0
        && Get<IReadOnlyList<GeneratedTableIssue>>(result, "Issues") is { Count: 1 } issues && (code is null || issues[0].Code == code);
    private static string Bytes(object result) => JsonSerializer.Serialize(Payloads(result).Select(p => new
    { p.Role, p.Arena, p.ByteOffset, p.Origin, p.ResourceId, data = p.Data.ToHexString(), writes = p.WriteMask.ToHexString(), before = p.BeforeData?.ToHexString(), beforeMask = p.BeforeMask?.ToHexString(), afterMask = p.AfterMask?.ToHexString() }));
    private static void Set(object value, string property, object? replacement)
        => value.GetType().GetField("<" + property + ">k__BackingField", Any)!.SetValue(value, replacement);

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "SharedResolvedTables " + name); if (!ok) failures++; }
        foreach (var format in Enum.GetValues<GeneratedTableFormat>())
        {
            var f = Load(directory, format); var snapshot = Get<object>(f.Application, "Snapshot"); var xml = Knxproj.ToXml(f.Source.Project).ToString(SaveOptions.DisableFormatting);
            var archive = File.ReadAllBytes(f.Source.Path); var undo = new UndoStack(); undo.Reset(f.Source.Project); var revision = f.Source.Project.ApplicationDataRevision;
            var result = Emit(f);
            Check(format + " shared source procedure and native tables are available together", Get<bool>(result, "IsAvailable") && Payloads(result).Count == 3);
            if (!Get<bool>(result, "IsAvailable")) continue;
            var procedure = Procedure(result); var payloads = Payloads(result); var ga = payloads.Single(p => p.Role == GeneratedTableRole.GroupAddress);
            var associations = payloads.Single(p => p.Role == GeneratedTableRole.Association); var objects = payloads.Single(p => p.Role == GeneratedTableRole.CommunicationObject);
            var small = format == GeneratedTableFormat.M112;
            Check(format + " freshly selected controls carry source branch and shared proof", procedure.ProfileVersion == 2
                && procedure.Controls.Where(c => c.InlineData is not null).Select(c => c.InlineData!.ToHexString()).SequenceEqual(new[] { "11" })
                && procedure.ConditionalSelection is { Decisions.Count: 1 } selection && selection.ControllerWitnessHash == Get<string>(f.View, "Fingerprint")
                && selection.Decisions[0] is { ControllerActive: true, ControllerValue: 1, UsedDefault: false, SelectedWhenOrdinals.Count: 1 }
                && procedure.Controls.Where(c => c.InlineData is not null).All(c => c.Provenance is { AncestorDecisionOrdinals.Count: 1 }));
            Check(format + " address and association bytes match independent literals", ga.Data.ToHexString() == (small ? "04BEEF01010A030B01" : "000301010A030B01")
                && associations.Data.ToHexString() == (format switch
                {
                    GeneratedTableFormat.M112 => "0501000201030301010203",
                    GeneratedTableFormat.SystemB2ByteAssociations => "000501010202030401020204",
                    _ => "00050001000100020002000300040001000200020004",
                }));
            Check(format + " complete sparse KO bytes and write masks are exact", objects.Data.ToHexString() == (small
                ? "05775510116FC012131BC7141500C016177FC8181900CA" : "00054F003B0700005F08000A")
                && (small ? ga.WriteMask.ToHexString() == "FF0000FFFFFFFFFFFF" && ga.BeforeMask?.ToHexString() == "5A5A5A5A5A5A5A5A5A"
                    && ga.AfterMask?.ToHexString() == "FF5A5AFFFFFFFFFFFF" && objects.WriteMask.ToHexString() == new string('F', 46)
                    : payloads.All(p => p.BeforeData is null && p.BeforeMask is null && p.WriteMask.ToArray().All(b => b == 255) && p.AfterMask?.ToHexString() == p.WriteMask.ToHexString())));
            var adjustments = Get<IReadOnlyList<GeneratedTableProcedureAdjustment>>(result, "ProcedureSizeAdjustments");
            Check(format + " adjustments belong to actually emitted conditional controls", small ? adjustments.Count == 0
                : adjustments.Count == 3 && adjustments.All(a => a.BeforeValue == 6000 && procedure.Controls[a.ControlOrdinal].Kind == LoadControlKind.RelativeSegment)
                    && adjustments.Select(a => a.AfterValue).SequenceEqual(new ulong[] { 8, format == GeneratedTableFormat.SystemB2ByteAssociations ? 12UL : 22UL, 12 }));
            Check(format + " no old replay binding or execution authority is minted", Get<int>(result, "ProfileVersion") == 2
                && Get<string>(result, "ProcedureWitnessHash") == procedure.WitnessHash && Get<string>(result, "ProjectInputHash") == Get<string>(f.Application, "ProjectInputHash")
                && result.GetType().GetProperty("Binding", Any) is null && result.GetType().GetMethod("IsCurrent", Any) is null
                && !procedure.CompleteApplicationImage && !procedure.ExecutionEligible && !procedure.DownloadEligible);
            var repeated = Emit(f);
            Check(format + " re-entry uses the same snapshot and stable whole result", Get<string>(repeated, "Fingerprint") == Get<string>(result, "Fingerprint")
                && Bytes(repeated) == Bytes(result) && ReferenceEquals(snapshot, Get<object>(f.Application, "Snapshot")));
            var first = procedure.Controls.First(c => c.InlineData is not null);
            var forged = new LoadProcedureControl[] { first };
            Set(procedure, "Controls", Array.AsReadOnly(forged));
            var restored = Emit(f);
            Check(format + " a copied old witness with replaced public controls is not reused", Get<bool>(restored, "IsAvailable")
                && Procedure(restored).Controls.Count > 1 && Procedure(restored).WitnessHash == procedure.WitnessHash
                && Get<string>(restored, "Fingerprint") == Get<string>(result, "Fingerprint"));
            Check(format + " source model revision archive and undo stay unchanged", archive.SequenceEqual(File.ReadAllBytes(f.Source.Path))
                && xml == Knxproj.ToXml(f.Source.Project).ToString(SaveOptions.DisableFormatting) && revision == f.Source.Project.ApplicationDataRevision && undo.Steps == 0 && !undo.IsDirty);
        }

        var fallback = Load(directory, GeneratedTableFormat.SystemB4ByteAssociations, value: "0"); var fallbackResult = Emit(fallback);
        Check("default controls and size-before values come from the selected original branch", Get<bool>(fallbackResult, "IsAvailable")
            && Procedure(fallbackResult).Controls.Where(c => c.InlineData is not null).Single().InlineData!.ToHexString() == "22"
            && Procedure(fallbackResult).ConditionalSelection!.Decisions.Single().UsedDefault
            && Get<IReadOnlyList<GeneratedTableProcedureAdjustment>>(fallbackResult, "ProcedureSizeAdjustments").All(a => a.BeforeValue == 7000));
        var all = App(GeneratedTableFormat.M112); var choice = Static(all).Descendants(Ns + "choose").Single();
        choice.ReplaceNodes(new XElement(Ns + "when", new XAttribute("test", "0 1"), Write("11")), new XElement(Ns + "when", new XAttribute("test", "1 2"), Write("22")));
        var allResult = Emit(Load(directory, app: all));
        Check("all matching procedure branches emit in original order", Get<bool>(allResult, "IsAvailable")
            && Procedure(allResult).Controls.Where(c => c.InlineData is not null).Select(c => c.InlineData!.ToHexString()).SequenceEqual(new[] { "11", "22" }));
        var inactive = App(GeneratedTableFormat.SystemB4ByteAssociations); var stat = Static(inactive);
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "PG"), new XAttribute("ParameterType", "T"), new XAttribute("Value", 0)));
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "G"), new XAttribute("RefId", "PG")));
        var dynamic = Program(inactive).Element(Ns + "Dynamic")!; dynamic.Elements(Ns + "ParameterRefRef").Single().ReplaceWith(
            new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "G")), new XElement(Ns + "choose", new XAttribute("ParamRefId", "G"),
                new XElement(Ns + "when", new XAttribute("test", "1"), new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R")))));
        var inactiveResult = Emit(Load(directory, GeneratedTableFormat.SystemB4ByteAssociations, inactive));
        Check("inactive requested ref skips both tested and default procedure bodies", Get<bool>(inactiveResult, "IsAvailable")
            && Procedure(inactiveResult).Controls.All(c => c.InlineData is null && c.Kind != LoadControlKind.RelativeSegment)
            && Procedure(inactiveResult).ConditionalSelection!.Decisions.Single() is { ControllerActive: false, ControllerValue: null, SelectedWhenOrdinals.Count: 0, UsedDefault: false }
            && Get<IReadOnlyList<GeneratedTableProcedureAdjustment>>(inactiveResult, "ProcedureSizeAdjustments").Count == 0);
        var primitive = Load(directory, app: App(GeneratedTableFormat.M112, conditional: false)); var primitiveResult = Emit(primitive);
        Check("new emission stays profile2 while primitive procedure keeps profile1", Get<bool>(primitiveResult, "IsAvailable") && Get<int>(primitiveResult, "ProfileVersion") == 2
            && Procedure(primitiveResult).ProfileVersion == 1 && Procedure(primitiveResult).ConditionalSelection is null);
        Set(Procedure(primitiveResult), "Controls", Array.AsReadOnly(Procedure(primitiveResult).Controls.Take(1).ToArray()));
        Check("primitive controls with a copied witness still require a fresh complete source pairing", Empty(Emit(primitive), GeneratedTableIssueCode.InputsChanged));

        var own = Load(directory); var foreign = Load(directory);
        Check("view from a different but identical base context is refused", Empty(Emit(own, application: foreign.Application), GeneratedTableIssueCode.InvalidRequest));
        Check("foreign view is not a valid pairing for the supplied base", Empty(Emit(own, view: foreign.View), GeneratedTableIssueCode.InvalidRequest));
        var otherPrepared = typeof(LoadProcedureCompiler).GetMethod("PrepareResolved", Hidden)!.Invoke(null, [own.Inputs, Full])!;
        Check("separately prepared identical sources are not the sealed view graph", Empty(Emit(own, prepared: otherPrepared), GeneratedTableIssueCode.InputsChanged));
        var sourceOnly = ViewType.GetMethod("Create", Hidden)!.Invoke(null, [own.Source.Project, own.Source.Device, own.Inputs, own.Prepared])!;
        Check("old independent procedure context cannot authorize the new shared route", Empty(Emit(own, view: sourceOnly), GeneratedTableIssueCode.InvalidRequest));
        var par = Load(directory, request: Full with { Parts = LoadParts.Parameters });
        Check("parameter-only procedure cannot request generated communication tables", Empty(Emit(par), GeneratedTableIssueCode.InvalidRequest));
        Check("unknown explicit table format retains no success data", Empty(Emit(own, format: (GeneratedTableFormat)999), GeneratedTableIssueCode.InvalidRequest));

        void Taint(string name, Action<Fixture> edit)
        {
            var f = Load(directory); edit(f); var bytes = File.ReadAllBytes(f.Source.Path); var xml = Knxproj.ToXml(f.Source.Project).ToString(SaveOptions.DisableFormatting);
            Check(name, Empty(Emit(f), GeneratedTableIssueCode.InputsChanged) && bytes.SequenceEqual(File.ReadAllBytes(f.Source.Path))
                && xml == Knxproj.ToXml(f.Source.Project).ToString(SaveOptions.DisableFormatting));
        }
        Taint("application source taint refuses before payload publication", f => Get<XDocument>(f.Inputs, "Application").Descendants(Ns + "Parameter").Single().SetAttributeValue("Value", 2));
        Taint("master source taint refuses before payload publication", f => Get<XDocument>(f.Inputs, "Master").Descendants(Ns + "ResourceType").First().SetAttributeValue("Flavour", "changed"));
        Taint("saved value changes stale both shared proofs", f => f.Source.Device.ParameterValues["R"] = "2");
        Taint("saved link order changes stale the whole input capture", f => f.Source.Device.ComObjectRefs[1].Links.Reverse());
        Taint("snapshot changes invalidate supplied native activity", f => Get<XElement>(Get<ApplicationSnapshot>(f.Application, "Snapshot").Evaluation, "ExpandedRoot").SetAttributeValue("Changed", 1));
        var replaced = Load(directory); var line = replaced.Source.Project.Installations[0].Areas[0].Lines[0]; var old = replaced.Source.Device;
        line.Devices.Clear(); line.Devices.Add(new DeviceInstance { Id = old.Id, ProductRefId = old.ProductRefId, Hardware2ProgramRefId = old.Hardware2ProgramRefId, Address = old.Address });
        Check("same-id live device replacement is stale", Empty(Emit(replaced), GeneratedTableIssueCode.InputsChanged));
        var extra = Load(directory); Set(extra.Application, "AdditionalControllerReferences", new HashSet<string>(new[] { "R", "Extra" }, StringComparer.Ordinal));
        Check("extra caller seed cannot be hidden behind an old context digest", Empty(Emit(extra), GeneratedTableIssueCode.InvalidRequest));
        var capability = Load(directory); Set(capability.Application, "ScriptCapabilityUsed", true);
        Check("a runtime capability is refused even if no execution is recorded", Empty(Emit(capability), GeneratedTableIssueCode.InvalidRequest));
        var wrongOwner = Load(directory); Set(wrongOwner.Application, "Activation", Get<object>(foreign.Application, "Activation"));
        Check("native activation must belong to the exact catalog and snapshot", Empty(Emit(wrongOwner), GeneratedTableIssueCode.InvalidRequest));

        var wideApp = App(GeneratedTableFormat.SystemB2ByteAssociations, 300, 1000);
        var wide = Load(directory, GeneratedTableFormat.SystemB2ByteAssociations, wideApp, numbers: [1, 2, 300]);
        Check("native associated-number bounds still reject two-byte format atomically", Empty(Emit(wide), GeneratedTableIssueCode.TableCapacityExceeded));
        var wideBig = Load(directory, GeneratedTableFormat.SystemB4ByteAssociations, wideApp, numbers: [1, 2, 300]);
        var wideBigResult = Emit(wideBig);
        Check("explicit four-byte format retains linked object300 and all1000 slots", Get<bool>(wideBigResult, "IsAvailable")
            && Payloads(wideBigResult).Single(p => p.Role == GeneratedTableRole.CommunicationObject).Length == 2002
            && Payloads(wideBigResult).Single(p => p.Role == GeneratedTableRole.Association).Data.ToHexString() == "000500010001000200020003012C000100020002012C");
        var ambiguity = App(GeneratedTableFormat.SystemB4ByteAssociations); var branch = Static(ambiguity).Descendants(Ns + "when").First();
        branch.Add(new XElement(branch.Elements(Ns + "LdCtrlRelSegment").First()));
        Check("fresh conditional adjustment ambiguity returns no partial payload", Empty(Emit(Load(directory, GeneratedTableFormat.SystemB4ByteAssociations, ambiguity)), GeneratedTableIssueCode.AmbiguousProcedureControl));
        Check("new entry accepts no caller procedure controls or runtime argument", Compiler.GetMethod("CompileResolvedFromContext", Hidden)!.GetParameters()
            .All(p => p.ParameterType != typeof(LoadProcedurePlan) && !typeof(IParameterScriptRuntime).IsAssignableFrom(p.ParameterType)));
        Console.WriteLine($"Shared resolved table checks: {checks}, failures: {failures}"); return failures;
    }
}
