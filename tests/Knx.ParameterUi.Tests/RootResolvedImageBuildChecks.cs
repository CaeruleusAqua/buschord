using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class RootResolvedImageBuildChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private static readonly LoadProcedureRequest Full = new("all", LoadProcedureAccess.Remote, LoadParts.Full);
    private static XElement Program(XDocument document) => document.Descendants(Ns + "ApplicationProgram").Single();
    private static XElement Static(XDocument document) => Program(document).Element(Ns + "Static")!;
    private static XElement Dynamic(XDocument document) => Program(document).Element(Ns + "Dynamic")!;
    private static XElement Placement(string id) => new(Ns + "ParameterRefRef", new XAttribute("RefId", id));
    private static XElement Write(string hex) => new(Ns + "LdCtrlWriteProp", new XAttribute("ObjIdx", 4), new XAttribute("PropId", 27),
        new XAttribute("Verify", false), new XAttribute("InlineData", hex));
    private static XElement Choose(string id, string test, params XElement[] body)
        => new(Ns + "choose", new XAttribute("ParamRefId", id), new XElement(Ns + "when", new XAttribute("test", test), body));
    private static XElement Container(XDocument document, string name)
    {
        var element = Static(document).Element(Ns + name);
        if (element is not null) return element;
        element = new XElement(Ns + name); Static(document).Add(element); return element;
    }
    private static XDocument Hardware() => XDocument.Parse("""
        <KNX xmlns="http://knx.org/xml/project/20"><ManufacturerData><Manufacturer RefId="M-0001"><Hardware><Hardware Id="M-0001_H-image"><Hardware2Programs>
        <Hardware2Program Id="M-0001_H-image_A-0100-10-0000"><ApplicationProgramRef RefId="M-0001_A-0100-10-0000"/></Hardware2Program>
        </Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
        """);
    private static XDocument App(int fields = 1)
    {
        var document = ArchivedImageSourceChecks.App(); var app = Program(document); app.SetAttributeValue("MinEtsVersion", "6.0");
        app.SetAttributeValue("LoadProcedureStyle", "ProductProcedure"); Static(document).AddFirst(new XElement(Ns + "Options"));
        AddFields(document, fields, false);
        Static(document).Add(new XElement(Ns + "LoadProcedures", new XElement(Ns + "LoadProcedure", new XElement(Ns + "LdCtrlConnect"),
            new XElement(Ns + "choose", new XAttribute("ParamRefId", "R"), new XElement(Ns + "when", new XAttribute("test", 1), Write("11")),
                new XElement(Ns + "when", new XAttribute("default", true), Write("22"))), new XElement(Ns + "LdCtrlDisconnect"))));
        return document;
    }
    private static void AddFields(XDocument document, int count, bool relative)
    {
        Container(document, "ParameterTypes").Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "TF"), new XElement(Ns + "TypeNumber",
            new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", 32), new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", uint.MaxValue))));
        Container(document, "Code").Add(new XElement(Ns + (relative ? "RelativeSegment" : "AbsoluteSegment"), new XAttribute("Id", "Parameters"),
            new XAttribute(relative ? "Offset" : "Address", relative ? 4 : 0x1000), relative ? new XAttribute("LoadStateMachine", 4) : null, new XAttribute("Size", count * 4)));
        for (var i = 0; i < count; i++)
        {
            Container(document, "Parameters").Add(new XElement(Ns + "Parameter", new XAttribute("Id", "P" + i), new XAttribute("ParameterType", "TF"),
                new XAttribute("Value", i + 1), i % 4 == 1 ? new XAttribute("LegacyPatchAlways", true) : null,
                new XElement(Ns + "Memory", new XAttribute("CodeSegment", "Parameters"), new XAttribute("Offset", i * 4), new XAttribute("BitOffset", 0))));
            Container(document, "ParameterRefs").Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "F" + i), new XAttribute("RefId", "P" + i), new XAttribute("Value", 2)));
            if (i % 4 == 0) Dynamic(document).Add(Placement("F" + i));
        }
    }
    private static ArchivedImageSourceChecks.Fixture Load(string directory, XDocument? app = null, GeneratedTableFormat? tables = null)
        => ArchivedImageSourceChecks.Load(directory, app: app ?? App(), hardware: Hardware(), master: GeneratedTableSerializerChecks.Master(tables ?? GeneratedTableFormat.M112));
    private static RootResolvedImageBuildRequest Request(StaticCodeMaskPolicy policy = StaticCodeMaskPolicy.SourceDefinedBits, GeneratedTableFormat? tables = null)
        => new(policy, Full, tables is { } format ? new(format) : null);
    private static RootResolvedImageBuildPlan Preview(ArchivedImageSourceChecks.Fixture fixture, RootResolvedImageBuildRequest? request = null)
        => OfflineRootImageBuild.PreviewResolved(fixture.Project, fixture.Device, request ?? Request());
    private static bool Empty(RootResolvedImageBuildPlan plan, RootImageBuildIssueCode? code = null)
        => !plan.IsAvailable && plan.Binding is null && plan.Policy is null && plan.Population is null && plan.Procedure is null && plan.ProcedureProof is null && plan.Tables is null
            && !plan.CompleteFixedRootPopulation && !plan.CompleteApplicationImage && !plan.ExecutionEligible && !plan.DownloadEligible
            && plan.Claims.Count == 0 && plan.Chunks.Count == 0 && plan.SourceSegments.Count == 0 && plan.UnspecifiedSpans.Count == 0 && plan.UnresolvedRequirements.Count == 0
            && plan.DesiredBitCount == 0 && plan.UnspecifiedBitCount == 0 && plan.Issues.Count == 1 && (code is null || plan.Issues[0].Code == code);
    private static byte[] Data(RootResolvedImageBuildPlan plan, ResourcePatchArena arena, uint offset, int length, bool validity = false)
    {
        var chunk = plan.Chunks.Single(c => c.Arena == arena && c.ByteOffsetInArena <= offset && (ulong)offset + (uint)length <= (ulong)c.ByteOffsetInArena + (uint)c.Length);
        return (validity ? chunk.DesiredValidity : chunk.WorkingData).ToArray().AsSpan(checked((int)(offset - chunk.ByteOffsetInArena)), length).ToArray();
    }
    private static string Hash(object value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static ArchivedImageSourceChecks.Fixture TableFixture(string directory, GeneratedTableFormat format, bool compatible = true, Action<XDocument>? edit = null)
    {
        var document = GeneratedTableSerializerChecks.Application(format); Program(document).SetAttributeValue("MinEtsVersion", "6.0");
        Static(document).AddFirst(new XElement(Ns + "Options"));
        if (compatible) Static(document).Descendants(Ns + "Mask").Remove();
        var baseApp = ArchivedImageSourceChecks.App();
        Static(document).Add(new XElement(Static(baseApp).Element(Ns + "ParameterTypes")!), new XElement(Static(baseApp).Element(Ns + "Parameters")!),
            new XElement(Static(baseApp).Element(Ns + "ParameterRefs")!));
        Dynamic(document).Add(Placement("R")); AddFields(document, 1, format != GeneratedTableFormat.M112);
        var controls = Static(document).Element(Ns + "LoadProcedures")!.Elements().Single(); var members = controls.Elements().ToArray();
        controls.ReplaceNodes(Choose("R", "1", members));
        edit?.Invoke(document);
        var fixture = Load(directory, document, format); var range = new GroupRange { Id = "GR-root-resolved", RangeStart = 0, RangeEnd = 65535 };
        fixture.Project.Installations[0].MainRanges.Add(range);
        foreach (var (id, address) in new[] { ("GA1", 0x0101), ("GA2", 0x0A03), ("GA3", 0x0B01) })
        { range.Addresses.Add(new() { Id = id, Name = id, Address = new((ushort)address) }); fixture.Project.Workbench.Groups[id] = new() { Secure = false }; }
        var numbers = format == GeneratedTableFormat.M112 ? new[] { 0, 1, 3 } : new[] { 1, 2, 4 };
        for (var i = 0; i < numbers.Length; i++)
        {
            var row = new ComObjectInstanceRef { Id = "O-" + numbers[i] + "_R-0", ComObjectId = "O-" + numbers[i], Number = numbers[i] };
            row.Links.AddRange(i == 0 ? ["GA1"] : i == 1 ? ["GA2", "GA1"] : ["GA3", "GA2"]); fixture.Device.ComObjectRefs.Add(row);
            fixture.Project.Workbench.Objects[new ObjectKey(fixture.Device.Id, row.Id).ToString()] = new() { SendingGroupId = row.Links[0] };
        }
        return fixture;
    }

    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok, RootResolvedImageBuildPlan? plan = null)
        { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "RootResolvedImageBuild " + name + (!ok && plan is not null ? " [" + string.Join(",", plan.Issues.Select(i => i.Code)) + "]" : "")); if (!ok) failures++; }
        void Refused(string name, Action<XDocument> edit, RootImageBuildIssueCode? expected = null)
        { var document = App(); edit(document); var plan = Preview(Load(directory, document)); Check(name, Empty(plan, expected), plan); }
        var fixture = Load(directory, App(130)); for (var i = 0; i < 130; i++) fixture.Device.ParameterValues["F" + i] = "3";
        var bytes = File.ReadAllBytes(fixture.Path); var xml = Knxproj.ToXml(fixture.Project).ToString(SaveOptions.DisableFormatting);
        var revision = fixture.Project.ApplicationDataRevision; var history = new UndoStack(); history.Reset(fixture.Project); var historyIndex = history.Index;
        var result = Preview(fixture); var expected = new byte[520];
        for (var i = 0; i < 130; i++) BinaryPrimitives.WriteUInt32BigEndian(expected.AsSpan(i * 4, 4), (uint)(i % 4 is 0 or 1 ? 3 : i + 1));
        Check("complete mixed population exceeds subset field and byte limits", result.IsAvailable && result.Population is { ReferenceCount: 131, PhysicalRecordCount: 130, NoStorageCount: 1 }
            && result.Claims.Count == 130 && Data(result, new(ResourcePatchArenaKind.AbsoluteMemory), 0x1000, 520).SequenceEqual(expected), result);
        if (!result.IsAvailable) { Console.WriteLine("RootResolved fixture unavailable"); return failures; }
        Check("active always and underlying-default records stay distinct", result.Population!.ActiveCount == 33 && result.Population.AlwaysCount == 33
            && result.Population.InactiveDefaultCount == 64 && result.Population.Records.Single(r => r.RefId == "F2") is { EncodedWord: 3, CurrentValueOrigin: RootParameterValueOrigin.Saved }
            && result.Population.Records.Single(r => r.RefId == "F3") is { EncodedWord: 4 });
        Check("new profile has false image and execution authority", result.Scope == "RootFixedMemoryResolvedWorkingSet" && result.ProfileVersion == 2
            && result.Binding?.ProfileVersion == 2 && result.CompleteFixedRootPopulation && !result.CompleteApplicationImage && !result.ExecutionEligible && !result.DownloadEligible);
        Check("shared proof binds the exact application controller and source placements", result.ProcedureProof is { Controllers.Count: 1 } proof
            && proof.ApplicationContextFingerprint == result.Binding!.ContextFingerprint && proof.Fingerprint == result.Binding.SharedProcedureProofHash
            && proof.ControllerSetHash == result.Binding.ControllerSetHash && proof.PlacementInventoryHash == result.Binding.SharedPlacementInventoryHash
            && proof.Controllers.Single() is { RefId: "R", Active: true, EverDeclaredOwnerCount: 1, ActiveNormalOwnerCount: 1, ActiveValueHash: not null }
            && proof.Controllers.Single().ActiveValueHash == Hash(1L));
        Check("resolved procedure emits original selected branch bytes", result.Procedure is { IsAvailable: true, ProfileVersion: 2 }
            && result.Procedure.Controls.Single(c => c.InlineData is not null).InlineData!.ToHexString() == "11");
        Check("all admitted physical writer bytes have their own desired validity", Data(result, new(ResourcePatchArenaKind.AbsoluteMemory), 0x1000, 520, true).All(b => b == 255)
            && result.DesiredBitCount == 4160 && result.UnspecifiedBitCount == 0);
        Check("source model archive and history remain unchanged", bytes.SequenceEqual(File.ReadAllBytes(fixture.Path)) && xml == Knxproj.ToXml(fixture.Project).ToString(SaveOptions.DisableFormatting)
            && revision == fixture.Project.ApplicationDataRevision && historyIndex == history.Index && !history.IsDirty);
        var copy = result.Chunks[0].WorkingData.ToArray(); copy[0] ^= 255;
        Check("owned buffers and new summary collections are immutable", !copy.SequenceEqual(result.Chunks[0].WorkingData.ToArray())
            && ((IList<RootImageChunk>)result.Chunks).IsReadOnly && ((IList<RootProcedureControllerSummary>)result.ProcedureProof!.Controllers).IsReadOnly);
        Check("same-route replay is stable", result.Binding!.IsCurrent(fixture.Project, fixture.Device) && Preview(fixture).Binding?.Fingerprint == result.Binding.Fingerprint);
        fixture.Device.ParameterValues["R"] = "2"; var fallback = Preview(fixture);
        Check("current controller value selects default and stales the prior route", !result.Binding.IsCurrent(fixture.Project, fixture.Device) && fallback.IsAvailable
            && fallback.Procedure!.Controls.Single(c => c.InlineData is not null).InlineData!.ToHexString() == "22"
            && fallback.Binding!.ProcedureGraphHash == result.Binding.ProcedureGraphHash && fallback.Binding.ProcedureWitnessHash != result.Binding.ProcedureWitnessHash, fallback);
        fixture.Device.ParameterValues.Remove("R"); Check("restored exact inputs restore prior binding", result.Binding.IsCurrent(fixture.Project, fixture.Device));
        var reload = Knxproj.Load(fixture.Path);
        Check("same ids on another loaded model cannot replay an old binding", !result.Binding.IsCurrent(reload, ProjectQuery.DeviceList(reload).Single()));
        fixture.Project.Name = "Changed"; history.Push(fixture.Project); var undoProject = history.Undo()!;
        Check("undo replacement remains a separate identity", !result.Binding.IsCurrent(undoProject, ProjectQuery.DeviceList(undoProject).Single())
            && OfflineRootImageBuild.PreviewResolved(undoProject, ProjectQuery.DeviceList(undoProject).Single(), Request()).IsAvailable);
        typeof(Project).GetProperty(nameof(Project.ApplicationDataRevision))!.SetValue(fixture.Project, revision + 1);
        Check("application revision invalidates a previously current binding", !result.Binding.IsCurrent(fixture.Project, fixture.Device));
        typeof(Project).GetProperty(nameof(Project.ApplicationDataRevision))!.SetValue(fixture.Project, revision);
        var original = (XDocument)typeof(Project).GetProperty("OriginalXml", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Project)!;
        var nativeDevice = original.Descendants().Single(e => e.Name.LocalName == "DeviceInstance" && (string?)e.Attribute("Id") == fixture.Device.Id);
        var retained = new XElement(nativeDevice.Name.Namespace + "ParameterInstanceRefs", new XElement(nativeDevice.Name.Namespace + "ParameterInstanceRef", new XAttribute("RefId", "R"), new XAttribute("Value", 1)));
        nativeDevice.Add(retained);
        Check("retained native parameter carriers are independently freshness-bound", !result.Binding.IsCurrent(fixture.Project, fixture.Device)); retained.Remove();

        var inactive = App(4); Dynamic(inactive).Add(Choose("R", "0", Placement("F3")));
        Static(inactive).Element(Ns + "LoadProcedures")!.Elements().Single().Add(Choose("F3", "3", Write("33")));
        var inactiveFixture = Load(directory, inactive); inactiveFixture.Device.ParameterValues["F3"] = "3"; var inactiveResult = Preview(inactiveFixture);
        Check("inactive logical current is not confused with the underlying writer default", inactiveResult.IsAvailable
            && inactiveResult.Population!.Records.Single(r => r.RefId == "F3") is { EncodedWord: 4, CurrentValueHash: not null }
            && inactiveResult.ProcedureProof!.Controllers.Single(c => c.RefId == "F3") is { Active: false, ActiveValueHash: null, EverDeclaredOwnerCount: 1 }
            && inactiveResult.Procedure!.Controls.All(c => c.InlineData?.ToHexString() != "33"), inactiveResult);
        foreach (var behavior in new[] { "None", "Background" })
        {
            var document = App(3); Static(document).Element(Ns + "Options")!.SetAttributeValue("DownloadInvisibleParameters", behavior);
            var preview = Preview(Load(directory, document));
            Check("inactive null geometry and mask " + behavior, preview.IsAvailable && preview.Population!.InactiveNullCount == 1
                && Data(preview, new(ResourcePatchArenaKind.AbsoluteMemory), 0x1008, 4).All(b => b == 0)
                && Data(preview, new(ResourcePatchArenaKind.AbsoluteMemory), 0x1008, 4, true).All(b => b == (behavior == "None" ? 0 : 255)), preview);
        }
        var littleDoc = App(); Static(littleDoc).Element(Ns + "Options")!.SetAttributeValue("ParameterByteOrder", "LittleEndian"); var little = Preview(Load(directory, littleDoc));
        Check("native byte order governs the full word pass", little.IsAvailable && little.Policy!.ByteOrder == ParameterPatchByteOrder.LittleEndian
            && Data(little, new(ResourcePatchArenaKind.AbsoluteMemory), 0x1000, 4).SequenceEqual(new byte[] { 2, 0, 0, 0 }), little);
        var oldOptions = App(3); Program(oldOptions).SetAttributeValue("MinEtsVersion", "4.0"); var legacyOptions = Preview(Load(directory, oldOptions));
        Check("older native version keeps background inactive policy", legacyOptions.IsAvailable && legacyOptions.Policy?.DownloadBehavior == RootParameterDownloadBehavior.Background
            && legacyOptions.Population?.InactiveNullCount == 1, legacyOptions);
        var primitiveDoc = App(); Static(primitiveDoc).Descendants(Ns + "choose").Single().ReplaceWith(Write("11")); var primitiveFixture = Load(directory, primitiveDoc);
        var primitive = Preview(primitiveFixture); var old = OfflineRootImageBuild.Preview(primitiveFixture.Project, primitiveFixture.Device, new(StaticCodeMaskPolicy.SourceDefinedBits, Full));
        Check("primitive fast path keeps root profile two and equal old working bytes", primitive.IsAvailable && primitive.ProfileVersion == 2 && primitive.Procedure?.ProfileVersion == 1
            && old.IsAvailable && primitive.Chunks[0].WorkingData.ToHexString() == old.Chunks[0].WorkingData.ToHexString() && primitive.Binding!.Fingerprint != old.Binding!.Fingerprint);
        var unusedDoc = App(); Container(unusedDoc, "Parameters").Add(new XElement(Ns + "Parameter", new XAttribute("Id", "UnusedP"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "03")));
        Container(unusedDoc, "ParameterRefs").Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "UnusedR"), new XAttribute("RefId", "UnusedP")));
        Static(unusedDoc).Element(Ns + "LoadProcedures")!.Add(new XElement(Ns + "LoadProcedure", new XAttribute("MergeId", 7), Choose("UnusedR", "1", Write("44"))));
        var unused = Preview(Load(directory, unusedDoc));
        Check("unused never-placed group stays declaration-only but remains in complete population", unused.IsAvailable
            && unused.ProcedureProof!.Controllers.All(c => c.RefId != "UnusedR") && unused.Population!.Records.Any(r => r.RefId == "UnusedR"), unused);
        var guardDoc = App(); Container(guardDoc, "Parameters").Add(new XElement(Ns + "Parameter", new XAttribute("Id", "CP"), new XAttribute("ParameterType", "T"), new XAttribute("Value", 1)));
        Container(guardDoc, "ParameterRefs").Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "CR"), new XAttribute("RefId", "CP")));
        Dynamic(guardDoc).Add(Choose("R", "1", Placement("CR"))); Static(guardDoc).Element(Ns + "LoadProcedures")!.Elements().Single().Add(Choose("CR", "1", Write("55")));
        Container(guardDoc, "Parameters").Elements().Single(p => (string?)p.Attribute("Id") == "P").SetAttributeValue("Value", "01");
        var badGuard = Preview(Load(directory, guardDoc));
        Check("full guard raw closure refuses numeric-equivalent noncanonical strings", Empty(badGuard, RootImageBuildIssueCode.ProcedureUnavailable)
            && badGuard.Issues[0].ProcedureIssue?.Code == LoadProcedureIssueCode.InvalidControllerValue, badGuard);
        Refused("missing controller placement never falls back to saved state", d => Dynamic(d).Elements(Ns + "ParameterRefRef").First().Remove(), RootImageBuildIssueCode.ProcedureUnavailable);
        Refused("single unsupported late writer discards complete output", d => Container(d, "ParameterTypes").Elements().Last().Elements().Single().SetAttributeValue("Type", "other"));
        Refused("union memory remains outside the shared route", d => Container(d, "Parameters").Add(new XElement(Ns + "Union")), RootImageBuildIssueCode.UnsupportedProfile);
        Refused("assignment state remains outside the shared route", d => Dynamic(d).Add(new XElement(Ns + "Assign")), RootImageBuildIssueCode.UnsupportedProfile);
        Refused("invalid native policy is atomic", d => Static(d).Element(Ns + "Options")!.SetAttributeValue("ParameterByteOrder", "other"), RootImageBuildIssueCode.InvalidPolicy);

        foreach (var format in Enum.GetValues<GeneratedTableFormat>())
        {
            var tableFixture = TableFixture(directory, format); var tablePlan = Preview(tableFixture, Request(tables: format));
            Check(format + " conditional writers and tables share one complete route", tablePlan.IsAvailable && tablePlan.Tables is { Payloads.Count: 3, ProfileVersion: 2 }
                && tablePlan.Procedure is { ProfileVersion: 2 } && tablePlan.Claims.Count == 1, tablePlan);
            if (!tablePlan.IsAvailable) continue;
            Check(format + " actual paired procedure and table emission hashes are bound", tablePlan.Tables!.ActualProcedureWitnessHash == tablePlan.Procedure!.WitnessHash
                && tablePlan.Binding!.TableEmissionHash == tablePlan.Tables.EmissionHash && tablePlan.Binding.TableCompatibilityHash == tablePlan.Tables.CompatibilityHash
                && tablePlan.Binding.IsCurrent(tableFixture.Project, tableFixture.Device));
            var ga = tablePlan.Tables.Payloads.Single(p => p.Role == GeneratedTableRole.GroupAddress);
            Check(format + " full emitted table address bytes match independent literals", Convert.ToHexString(Data(tablePlan, ga.Arena, ga.ByteOffsetInArena, ga.Length))
                == (format == GeneratedTableFormat.M112 ? "04BEEF01010A030B01" : "000301010A030B01"));
            Check(format + " no old table replay binding or false finalization is exposed", typeof(RootResolvedImageTableSummary).GetProperty("Binding") is null
                && tablePlan.UnresolvedRequirements.Any(r => r.Code == RootImageBuildRequirementCode.ProcedureFinalizationUnresolved)
                && (format == GeneratedTableFormat.M112 ? tablePlan.Tables.ProcedureSizeAdjustments.Count == 0 : tablePlan.Tables.ProcedureSizeAdjustments.Count == 3));
            var primitiveTable = TableFixture(directory, format, edit: d =>
            { var choice = Static(d).Element(Ns + "LoadProcedures")!.Descendants(Ns + "choose").Single(); choice.ReplaceWith(choice.Elements().Single().Elements().ToArray()); });
            var primitiveTablePlan = Preview(primitiveTable, Request(tables: format));
            Check(format + " primitive procedure fast path still uses the new root table route", primitiveTablePlan.IsAvailable
                && primitiveTablePlan.ProfileVersion == 2 && primitiveTablePlan.Procedure?.ProfileVersion == 1 && primitiveTablePlan.Tables?.ProfileVersion == 2, primitiveTablePlan);
            var reset = Preview(tableFixture, Request(StaticCodeMaskPolicy.NoStaticSourceBits, format));
            Check(format + " explicit reset changes validity while preserving working input", reset.IsAvailable
                && reset.Tables!.Payloads.Single(p => p.Role == GeneratedTableRole.GroupAddress).DataHash == ga.DataHash
                && (format != GeneratedTableFormat.M112 || Data(reset, ga.Arena, ga.ByteOffsetInArena + 1, 2, true).All(b => b == 0)), reset);
            tableFixture.Device.ComObjectRefs[0].Links.Clear();
            Check(format + " current association edits stale the shared route", !tablePlan.Binding!.IsCurrent(tableFixture.Project, tableFixture.Device));
        }
        var incompatible = Preview(TableFixture(directory, GeneratedTableFormat.M112, false), Request(tables: GeneratedTableFormat.M112));
        Check("cleaned retained raw seed cannot be adopted from old table payload", Empty(incompatible, RootImageBuildIssueCode.TableWorkingInputsMismatch), incompatible);
        ArchivedImageSourceChecks.Fixture HeaderField(uint offset, byte value) => TableFixture(directory, GeneratedTableFormat.M112, edit: d =>
        {
            var type = Container(d, "ParameterTypes").Elements().Single(t => (string?)t.Attribute("Id") == "TF").Elements().Single();
            type.SetAttributeValue("SizeInBit", 8); type.SetAttributeValue("maxInclusive", 255);
            var parameter = Container(d, "Parameters").Elements().Single(p => (string?)p.Attribute("Id") == "P0"); parameter.SetAttributeValue("Value", value);
            parameter.Element(Ns + "Memory")!.SetAttributeValue("CodeSegment", "GA"); parameter.Element(Ns + "Memory")!.SetAttributeValue("Offset", offset);
            Container(d, "ParameterRefs").Elements().Single(r => (string?)r.Attribute("Id") == "F0").SetAttributeValue("Value", value);
        });
        var collision = Preview(HeaderField(0, 0x99), Request(tables: GeneratedTableFormat.M112));
        Check("equal pre-table data does not authorize a parameter-table bit collision", Empty(collision, RootImageBuildIssueCode.ParameterTableOverlap), collision);
        var retainedHeader = Preview(HeaderField(1, 0xBB), Request(tables: GeneratedTableFormat.M112));
        Check("entire retained input includes unwritten table header bytes", Empty(retainedHeader, RootImageBuildIssueCode.TableWorkingInputsMismatch), retainedHeader);
        var invalidRequest = OfflineRootImageBuild.PreviewResolved(fixture.Project, fixture.Device, new(StaticCodeMaskPolicy.Unspecified, Full));
        Check("explicit valid mask policy is required", Empty(invalidRequest, RootImageBuildIssueCode.InvalidRequest));
        Check("group-only request cannot silently skip parameter writers", Empty(Preview(fixture, new(StaticCodeMaskPolicy.SourceDefinedBits, Full with { Parts = LoadParts.Groups })), RootImageBuildIssueCode.InvalidRequest));
        Check("tables require explicit group or full parts", Empty(Preview(fixture, new(StaticCodeMaskPolicy.SourceDefinedBits, Full with { Parts = LoadParts.Parameters }, new(GeneratedTableFormat.M112))), RootImageBuildIssueCode.InvalidRequest));
        Check("unknown requested table format is refused before compilation", Empty(Preview(fixture, new(StaticCodeMaskPolicy.SourceDefinedBits, Full, new((GeneratedTableFormat)999))), RootImageBuildIssueCode.InvalidRequest));
        Check("public resolved root entry accepts neither runtime nor supplied contexts", typeof(OfflineRootImageBuild).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "PreviewResolved").GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(Project), typeof(DeviceInstance), typeof(RootResolvedImageBuildRequest) }));
        Console.WriteLine($"RootResolvedImageBuild: {checks} checks, {failures} failures"); return failures;
    }
}
