using System.Reflection;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class TableEmissionMaterializerChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Compiler = typeof(Project).Assembly.GetType("Knx.GeneratedTableCompiler", true)!;
    private static readonly Type Shared = typeof(Project).Assembly.GetType("Knx.SharedGeneratedTableEmission", true)!;
    private static readonly Type Factory = Compiler.GetNestedType("SharedEmissionMaterializer", BindingFlags.NonPublic)!;
    private static readonly object Materializer = Factory.GetField("Instance", Hidden)!.GetValue(null)!;
    private static readonly LoadProcedureRequest Full = new("all", LoadProcedureAccess.Remote, LoadParts.Full);
    private static T Fact<T>(object value, string name) => NativeActivationChecks.Fact<T>(value, name);
    private static IReadOnlyList<GeneratedTablePayload> Payloads(object result) => Fact<IReadOnlyList<GeneratedTablePayload>>(result, "Payloads");
    private static XElement Program(XDocument app) => app.Descendants(Ns + "ApplicationProgram").Single();
    private static XDocument Hardware() => XDocument.Parse("""
        <KNX xmlns="http://knx.org/xml/project/20"><ManufacturerData><Manufacturer RefId="M-0001"><Hardware><Hardware Id="M-0001_H-image"><Hardware2Programs>
        <Hardware2Program Id="M-0001_H-image_A-0100-10-0000"><ApplicationProgramRef RefId="M-0001_A-0100-10-0000"/></Hardware2Program>
        </Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
        """);

    private static ArchivedImageSourceChecks.Fixture Load(string directory, GeneratedTableFormat format,
        XDocument? app = null, XDocument? hardware = null)
    {
        var fixture = ArchivedImageSourceChecks.Load(directory, app: app ?? GeneratedTableSerializerChecks.Application(format),
            hardware: hardware ?? Hardware(), master: GeneratedTableSerializerChecks.Master(format));
        var range = new GroupRange { Id = "GR-emission", Name = "Synthetic groups", RangeStart = 0, RangeEnd = 65535 };
        fixture.Project.Installations[0].MainRanges.Add(range);
        foreach (var (id, address) in new[] { ("GA1", 0x0101), ("GA2", 0x0A03), ("GA3", 0x0B01) })
        {
            range.Addresses.Add(new() { Id = id, Name = id, Address = new((ushort)address) });
            fixture.Project.Workbench.Groups[id] = new() { Secure = false };
        }
        var numbers = format == GeneratedTableFormat.M112 ? new[] { 0, 1, 3 } : new[] { 1, 2, 4 };
        for (var i = 0; i < numbers.Length; i++)
        {
            var row = new ComObjectInstanceRef { Id = "O-" + numbers[i] + "_R-0", ComObjectId = "O-" + numbers[i], Number = numbers[i] };
            row.Links.AddRange(i == 0 ? ["GA1"] : i == 1 ? ["GA2", "GA1"] : ["GA3", "GA2"]);
            fixture.Device.ComObjectRefs.Add(row);
            fixture.Project.Workbench.Objects[new ObjectKey(fixture.Device.Id, row.Id).ToString()] = new() { SendingGroupId = row.Links[0] };
        }
        return fixture;
    }
    private static object Inputs(ArchivedImageSourceChecks.Fixture fixture) => NativeActivationChecks.Inputs(fixture);
    private static LoadProcedurePlan Procedure(object inputs, LoadProcedureRequest? request = null)
        => LoadProcedureCompiler.Compile(Fact<XDocument>(inputs, "Application"), Fact<XDocument?>(inputs, "Master"), request ?? Full);
    private static object Context(ArchivedImageSourceChecks.Fixture fixture, object inputs, IParameterScriptRuntime? scripts = null)
        => typeof(Project).Assembly.GetType("Knx.NativeApplicationEvaluationContext", true)!.GetMethod("Create", Hidden)!
            .Invoke(null, [fixture.Project, fixture.Device, inputs, null, scripts])!;
    private static GeneratedTablePreview Legacy(ArchivedImageSourceChecks.Fixture fixture, object inputs,
        LoadProcedurePlan procedure, GeneratedTableFormat format)
        => (GeneratedTablePreview)Compiler.GetMethod("Compile", Hidden)!
            .Invoke(null, [fixture.Project, fixture.Device, inputs, procedure, new GeneratedTableRequest(format), null])!;
    private static object Unavailable(GeneratedTableIssue issue, GeneratedTableFormat? format)
        => Factory.GetMethod("Unavailable", BindingFlags.Instance | BindingFlags.Public)!.Invoke(Materializer, [issue, format])!;
    private static object Emit(ArchivedImageSourceChecks.Fixture fixture, object inputs, LoadProcedurePlan procedure,
        GeneratedTableFormat format, object? context = null)
    {
        try
        {
            return Compiler.GetMethod("CreateEmission", Hidden)!.MakeGenericMethod(Shared)
                .Invoke(null, [fixture.Project, fixture.Device, inputs, procedure, new GeneratedTableRequest(format), null, context, Materializer])!;
        }
        catch (TargetInvocationException failure) when (failure.InnerException?.GetType().FullName == "Knx.GeneratedTableCompiler+Rejected")
        { return Unavailable(Fact<GeneratedTableIssue>(failure.InnerException!, "Issue"), Enum.IsDefined(format) ? format : null); }
        catch (TargetInvocationException failure) when (failure.InnerException is XmlException or InvalidDataException or FormatException or OverflowException or ArgumentException)
        { return Unavailable(new(GeneratedTableIssueCode.InvalidTableDeclaration), Enum.IsDefined(format) ? format : null); }
    }
    private static string PayloadSnapshot(IEnumerable<GeneratedTablePayload> payloads) => JsonSerializer.Serialize(payloads.Select(p => new
    {
        p.Role, p.Arena, p.ByteOffset, p.Origin, p.ResourceId, p.Length, data = p.Data.ToHexString(), mask = p.WriteMask.ToHexString(),
        before = p.BeforeData?.ToHexString(), beforeMask = p.BeforeMask?.ToHexString(), afterMask = p.AfterMask?.ToHexString(),
    }).ToArray());
    private static bool Empty(object result, GeneratedTableIssueCode code, GeneratedTableFormat? format)
        => !Fact<bool>(result, "IsAvailable") && Fact<GeneratedTableFormat?>(result, "Format") == format
            && Fact<string?>(result, "Fingerprint") is null && Fact<string?>(result, "PayloadHash") is null
            && Fact<string?>(result, "NativeWitnessHash") is null && Fact<string?>(result, "SourceFingerprint") is null
            && Fact<string?>(result, "ApplicationDefinitionHash") is null && Fact<string?>(result, "ProjectInputHash") is null
            && Fact<string?>(result, "ProcedureWitnessHash") is null && !Fact<bool>(result, "RequiresScripts")
            && Fact<LoadProcedurePlan?>(result, "ActualProcedure") is null && Payloads(result).Count == 0
            && Fact<IReadOnlyList<GeneratedTableProcedureAdjustment>>(result, "ProcedureSizeAdjustments").Count == 0
            && Fact<IReadOnlyList<GeneratedTableRequirement>>(result, "Requirements").Count == 0
            && Fact<IReadOnlyList<GeneratedTableIssue>>(result, "Issues") is { Count: 1 } issues && issues[0].Code == code;
    private sealed class Runtime : IParameterScriptRuntime
    {
        internal int Calls;
        public IReadOnlyDictionary<string, object?> Execute(string source, IReadOnlyDictionary<string, object?> inputs, IReadOnlyList<string> outputs)
        { Calls++; throw new InvalidOperationException("No calculation is present in this fixture."); }
    }

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "TableEmission " + name); if (!ok) failures++; }
        void Refused(string name, ArchivedImageSourceChecks.Fixture fixture, GeneratedTableFormat format, GeneratedTableIssueCode code,
            LoadProcedurePlan? procedure = null)
        {
            var inputs = Inputs(fixture); procedure ??= Procedure(inputs);
            var xml = Knxproj.ToXml(fixture.Project).ToString(SaveOptions.DisableFormatting); var bytes = File.ReadAllBytes(fixture.Path);
            var undo = new UndoStack(); undo.Reset(fixture.Project);
            var shared = Emit(fixture, inputs, procedure, format); var legacy = Legacy(fixture, inputs, procedure, format);
            Check(name, Empty(shared, code, Enum.IsDefined(format) ? format : null) && !legacy.IsAvailable
                && legacy.Binding is null && legacy.Payloads.Count == 0 && legacy.Issues is { Count: 1 } && legacy.Issues[0].Code == code
                && legacy.Format == Fact<GeneratedTableFormat?>(shared, "Format") && xml == Knxproj.ToXml(fixture.Project).ToString(SaveOptions.DisableFormatting)
                && bytes.SequenceEqual(File.ReadAllBytes(fixture.Path)) && !undo.CanUndo && !undo.IsDirty);
        }

        foreach (var format in Enum.GetValues<GeneratedTableFormat>())
        {
            var fixture = Load(directory, format); var inputs = Inputs(fixture); var procedure = Procedure(inputs); var context = Context(fixture, inputs);
            var bytes = File.ReadAllBytes(fixture.Path); var xml = Knxproj.ToXml(fixture.Project).ToString(SaveOptions.DisableFormatting);
            var undo = new UndoStack(); undo.Reset(fixture.Project); var revision = fixture.Project.ApplicationDataRevision;
            var shared = Emit(fixture, inputs, procedure, format, context); var legacy = Legacy(fixture, inputs, procedure, format);
            Check(format + " materializers both receive three complete emitted roles", Fact<bool>(shared, "IsAvailable") && legacy.IsAvailable
                && Payloads(shared).Count == 3 && legacy.Payloads.Count == 3);
            if (!Fact<bool>(shared, "IsAvailable") || !legacy.IsAvailable) continue;
            var payloads = Payloads(shared); var ga = payloads.Single(p => p.Role == GeneratedTableRole.GroupAddress);
            var associations = payloads.Single(p => p.Role == GeneratedTableRole.Association); var objects = payloads.Single(p => p.Role == GeneratedTableRole.CommunicationObject);
            var small = format == GeneratedTableFormat.M112;
            Check(format + " full address bytes match fixed independent literals", ga.Data.ToHexString() == (small ? "04BEEF01010A030B01" : "000301010A030B01"));
            Check(format + " full multi-object association bytes match fixed literals", associations.Data.ToHexString() == (format switch
            {
                GeneratedTableFormat.M112 => "0501000201030301010203",
                GeneratedTableFormat.SystemB2ByteAssociations => "000501010202030401020204",
                _ => "00050001000100020002000300040001000200020004",
            }));
            Check(format + " full sparse object bytes match fixed literals", objects.Data.ToHexString() == (small
                ? "05775510116FC012131BC7141500C016177FC8181900CA" : "00054F003B0700005F08000A"));
            Check(format + " exact new-write masks retain source validity separately", small
                ? ga.WriteMask.ToHexString() == "FF0000FFFFFFFFFFFF" && ga.BeforeMask?.ToHexString() == "5A5A5A5A5A5A5A5A5A"
                    && ga.AfterMask?.ToHexString() == "FF5A5AFFFFFFFFFFFF" && objects.WriteMask.ToHexString() == new string('F', 46)
                    && objects.BeforeMask?.ToHexString() == new string('0', 46) && associations.BeforeMask is null
                : payloads.All(p => p.BeforeData is null && p.BeforeMask is null && p.WriteMask.ToArray().All(b => b == 255)
                    && p.AfterMask?.ToHexString() == p.WriteMask.ToHexString()));
            Check(format + " every payload coordinate origin and buffer matches legacy", PayloadSnapshot(payloads) == PayloadSnapshot(legacy.Payloads)
                && Fact<string>(shared, "PayloadHash") == legacy.Binding!.PayloadHash);
            var adjustments = Fact<IReadOnlyList<GeneratedTableProcedureAdjustment>>(shared, "ProcedureSizeAdjustments");
            Check(format + " size adjustments keep exact control and role ordering", JsonSerializer.Serialize(adjustments) == JsonSerializer.Serialize(legacy.ProcedureSizeAdjustments)
                && (small ? adjustments.Count == 0 : adjustments.Count == 3 && adjustments.All(a => a.BeforeValue == 6000)
                    && adjustments.Select(a => a.AfterValue).SequenceEqual(new ulong[] { 8, format == GeneratedTableFormat.SystemB2ByteAssociations ? 12UL : 22UL, 12 })));
            Check(format + " requirements and unused-tail policy are unchanged", JsonSerializer.Serialize(Fact<IReadOnlyList<GeneratedTableRequirement>>(shared, "Requirements")) == JsonSerializer.Serialize(legacy.Requirements)
                && (small ? legacy.Requirements.Count(r => r.Code == GeneratedTableRequirementCode.UnusedTailMaskInvalidation) == 2
                    : legacy.Requirements.Any(r => r.Code == GeneratedTableRequirementCode.PhysicalResourceMapping)));
            Check(format + " new facts bind actual inputs without a legacy replay binding", Fact<int>(shared, "ProfileVersion") == 2
                && ReferenceEquals(Fact<LoadProcedurePlan>(shared, "ActualProcedure"), procedure)
                && Fact<string>(shared, "ProcedureWitnessHash") == procedure.WitnessHash
                && Fact<string>(shared, "NativeWitnessHash") == legacy.NativeWitnessHash
                && Fact<string>(shared, "SourceFingerprint") == legacy.Binding!.SourceFingerprint
                && Fact<string>(shared, "ApplicationDefinitionHash") == Fact<ArchivedImageSourceManifest>(inputs, "Manifest").ApplicationDefinitionHash
                && Fact<string>(shared, "ProjectInputHash") == Fact<string>(context, "ProjectInputHash")
                && Fact<string>(shared, "Fingerprint") is { Length: 64 } hash && hash != legacy.Binding.Fingerprint
                && Shared.GetProperty("Binding", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is null
                && Shared.GetMethod("IsCurrent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is null);
            var repeat = Emit(fixture, inputs, procedure, format, context);
            Check(format + " repeated private materialization is stable and legacy replay stays current", Fact<string>(shared, "Fingerprint") == Fact<string>(repeat, "Fingerprint")
                && legacy.Binding!.IsCurrent(fixture.Project, fixture.Device)
                && legacy.Binding.Fingerprint == Legacy(fixture, inputs, procedure, format).Binding?.Fingerprint);
            var copy = ga.Data.ToArray(); copy[0] ^= 255;
            Check(format + " immutable lists and copy-out keep original bytes", ((IList<GeneratedTablePayload>)payloads).IsReadOnly
                && ((IList<GeneratedTableProcedureAdjustment>)adjustments).IsReadOnly && ga.Data[0] != copy[0]
                && PayloadSnapshot(payloads) == PayloadSnapshot(legacy.Payloads));
            Check(format + " no source model revision or undo mutation", bytes.SequenceEqual(File.ReadAllBytes(fixture.Path))
                && xml == Knxproj.ToXml(fixture.Project).ToString(SaveOptions.DisableFormatting) && revision == fixture.Project.ApplicationDataRevision
                && undo.Steps == 0 && !undo.IsDirty);
        }

        var ordinary = Load(directory, GeneratedTableFormat.M112); var ordinaryInputs = Inputs(ordinary); var ordinaryProcedure = Procedure(ordinaryInputs);
        var invalidOrderApp = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.M112);
        Program(invalidOrderApp).Descendants(Ns + "Data").First().Value = "invalid";
        var invalidOrder = Load(directory, GeneratedTableFormat.M112, invalidOrderApp); invalidOrder.Device.BinaryData["unaccounted"] = [1];
        Refused("invalid format wins before malformed Code and context", invalidOrder, (GeneratedTableFormat)999, GeneratedTableIssueCode.InvalidRequest, ordinaryProcedure);
        Refused("parts gate precedes malformed Code and context", invalidOrder, GeneratedTableFormat.M112, GeneratedTableIssueCode.InvalidRequest,
            Procedure(ordinaryInputs, Full with { Parts = LoadParts.Parameters }));
        var factoryApp = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.M112);
        Program(factoryApp).Descendants(Ns + "Data").First().Value = "invalid";
        var factoryHardware = Hardware(); factoryHardware.Descendants(Ns + "Hardware2Program").Single().SetAttributeValue("LoadedImage", "");
        Refused("factory declaration gate precedes malformed Code decode", Load(directory, GeneratedTableFormat.M112, factoryApp, factoryHardware),
            GeneratedTableFormat.M112, GeneratedTableIssueCode.UnsupportedFactoryImage, ordinaryProcedure);
        Refused("format mismatch precedes malformed Code decode", Load(directory, GeneratedTableFormat.M112, factoryApp),
            GeneratedTableFormat.SystemB2ByteAssociations, GeneratedTableIssueCode.UnsupportedTableFormat, ordinaryProcedure);
        var invalidCode = Load(directory, GeneratedTableFormat.M112, factoryApp); invalidCode.Device.BinaryData["unaccounted"] = [1];
        Refused("Code decode failure precedes context binary rejection", invalidCode, GeneratedTableFormat.M112, GeneratedTableIssueCode.InvalidTableDeclaration, ordinaryProcedure);
        var unknown = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.M112); Program(unknown).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "UnknownState"));
        Refused("unavailable context is materialized without partial bytes", Load(directory, GeneratedTableFormat.M112, unknown),
            GeneratedTableFormat.M112, GeneratedTableIssueCode.UnsupportedStructureActivation);
        var mismatch = Load(directory, GeneratedTableFormat.M112); mismatch.Device.ComObjectRefs[0].Number = 999;
        Refused("unavailable witness is materialized without partial bytes", mismatch, GeneratedTableFormat.M112, GeneratedTableIssueCode.SavedNumberMismatch);
        var capacity = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.M112); Program(capacity).Descendants(Ns + "AddressTable").Single().SetAttributeValue("MaxEntries", 0);
        Refused("capacity failure follows complete native witness", Load(directory, GeneratedTableFormat.M112, capacity), GeneratedTableFormat.M112, GeneratedTableIssueCode.TableCapacityExceeded);
        var duplicate = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.SystemB4ByteAssociations);
        var control = Program(duplicate).Descendants(Ns + "LdCtrlRelSegment").First(); control.AddAfterSelf(new XElement(control));
        Refused("late adjustment ambiguity still discards every emitted payload", Load(directory, GeneratedTableFormat.SystemB4ByteAssociations, duplicate),
            GeneratedTableFormat.SystemB4ByteAssociations, GeneratedTableIssueCode.AmbiguousProcedureControl);
        var runtime = new Runtime(); var capabilityContext = Context(ordinary, ordinaryInputs, runtime);
        var capability = Emit(ordinary, ordinaryInputs, ordinaryProcedure, GeneratedTableFormat.M112, capabilityContext);
        Check("private facts do not erase a zero-call runtime capability", Fact<bool>(capability, "IsAvailable")
            && Fact<bool>(capability, "RequiresScripts") && runtime.Calls == 0
            && Fact<string>(capability, "Fingerprint") != Fact<string>(Emit(ordinary, ordinaryInputs, ordinaryProcedure, GeneratedTableFormat.M112), "Fingerprint"));
        var explicitFailure = Unavailable(new(GeneratedTableIssueCode.InputsChanged), GeneratedTableFormat.SystemB4ByteAssociations);
        Check("failure retains explicit format but no success metadata", Empty(explicitFailure, GeneratedTableIssueCode.InputsChanged, GeneratedTableFormat.SystemB4ByteAssociations));
        Check("the prep exposes no new compiler entry or caller factory", Compiler.GetMethods(BindingFlags.Public | BindingFlags.Static).Length == 0
            && Compiler.GetMethod("CreateEmission", Hidden) is { IsPrivate: true, IsGenericMethodDefinition: true }
            && Factory.IsNestedPrivate && Shared.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 0);
        Console.WriteLine($"Table emission materializer checks: {checks}, failures: {failures}");
        return failures;
    }
}
