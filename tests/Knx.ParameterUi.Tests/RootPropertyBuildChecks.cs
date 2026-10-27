using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class RootPropertyBuildChecks
{
    private static readonly XNamespace Ns = PropertySourceChecks.Ns;
    private static readonly RootPropertyBuildRequest Request = new(new("all", LoadProcedureAccess.Remote, LoadParts.Full));
    private sealed record Fixture(Project Project, DeviceInstance Device, string Path);
    private static XElement Program(XDocument document) => document.Descendants(Ns + "ApplicationProgram").Single();
    private static XElement Static(XDocument document) => Program(document).Element(Ns + "Static")!;
    private static XElement Parameter(XDocument document, int index = 0) => Static(document).Element(Ns + "Parameters")!.Elements().ElementAt(index);
    private static XElement Reference(XDocument document, int index = 0) => Static(document).Element(Ns + "ParameterRefs")!.Elements().ElementAt(index);
    private static XElement Write(ushort count = 2, ushort start = 1, ushort pid = 10) => new(Ns + "LdCtrlWriteProp",
        new XAttribute("ObjIdx", 3), new XAttribute("PropId", pid), new XAttribute("StartElement", start), new XAttribute("Count", count), new XAttribute("Verify", false));
    private static XDocument App(int count = 1, int bits = 3, ushort elements = 2)
    {
        var app = PropertySourceChecks.App(bits: bits); var stat = Static(app);
        Program(app).SetAttributeValue("MinEtsVersion", "6.0"); Program(app).SetAttributeValue("LoadProcedureStyle", "ProductProcedure");
        stat.AddFirst(new XElement(Ns + "Options", new XAttribute("ParameterByteOrder", "BigEndian"), new XAttribute("DownloadInvisibleParameters", "DefaultValue")));
        var width = (bits + 7) / 8;
        stat.Element(Ns + "Parameters")!.ReplaceNodes(Enumerable.Range(0, count).Select(i => new XElement(Ns + "Parameter",
            new XAttribute("Id", "P" + i), new XAttribute("ParameterType", "T"), new XAttribute("Value", i + 1),
            new XElement(Ns + "Property", new XAttribute("ObjectIndex", 3), new XAttribute("PropertyId", 10),
                new XAttribute("Offset", i * width), new XAttribute("BitOffset", 0)))));
        stat.Element(Ns + "ParameterRefs")!.ReplaceNodes(Enumerable.Range(0, count).Select(i => new XElement(Ns + "ParameterRef",
            new XAttribute("Id", "R" + i), new XAttribute("RefId", "P" + i))));
        Program(app).Element(Ns + "Dynamic")!.ReplaceNodes(Enumerable.Range(0, count).Select(i => new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R" + i))));
        stat.Add(new XElement(Ns + "LoadProcedures", new XElement(Ns + "LoadProcedure", new XElement(Ns + "LdCtrlConnect"), Write(elements), new XElement(Ns + "LdCtrlDisconnect"))));
        return app;
    }
    private static Fixture Load(string directory, XDocument? app = null, XDocument? master = null, byte[]? factory = null)
    {
        app ??= App(); master ??= PropertySourceChecks.Master();
        var appId = (string)Program(app).Attribute("Id")!; var manufacturer = appId[..6];
        app.Descendants(Ns + "Manufacturer").Single().SetAttributeValue("RefId", manufacturer);
        var hardwareId = manufacturer + "_H-property_A-root";
        var hardware = PropertyInitializationChecks.Hardware(factory);
        hardware.Descendants(Ns + "Manufacturer").Single().SetAttributeValue("RefId", manufacturer);
        hardware.Descendants(Ns + "Hardware").Single(e => e.Attribute("Id") is not null).SetAttributeValue("Id", manufacturer + "_H-property");
        hardware.Descendants(Ns + "Hardware2Program").Single().SetAttributeValue("Id", hardwareId);
        hardware.Descendants(Ns + "ApplicationProgramRef").Single().SetAttributeValue("RefId", appId);
        var project = new Project { Id = "P-root-property", Name = "Synthetic root property", Guid = "00000001-0000-0000-0000-000000000001" };
        var installation = new Installation { Id = "I-property" }; var area = new Area { Id = "A-property", Address = 1 }; var line = new Line { Id = "L-property", Address = 1 };
        var device = new DeviceInstance { Id = "D-property", ProductRefId = appId, Hardware2ProgramRefId = hardwareId, Address = new(1, 1, 1) };
        line.Devices.Add(device); area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
        var path = Path.Combine(directory, "root-property-" + Guid.NewGuid().ToString("N") + ".knxproj");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, xml) in new[] { ("0.xml", Knxproj.ToXml(project)), (manufacturer + "/" + appId + ".xml", app),
                (manufacturer + "/Hardware.xml", hardware), ("knx_master.xml", master) })
            { using var stream = zip.CreateEntry(name).Open(); stream.Write(Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting))); }
        var loaded = Knxproj.Load(path); return new(loaded, ProjectQuery.DeviceList(loaded).Single(), path);
    }
    private static RootPropertyBuildPlan Preview(Fixture fixture, RootPropertyBuildRequest? request = null)
        => OfflineRootPropertyBuild.Preview(fixture.Project, fixture.Device, request ?? Request);
    private static string? Data(RootPropertyBuildPlan plan) => plan.Chunks.SingleOrDefault()?.WorkingData.ToHexString();
    private static string? Mask(RootPropertyBuildPlan plan) => plan.Chunks.SingleOrDefault()?.DesiredValidity.ToHexString();
    private static bool Empty(RootPropertyBuildPlan plan, RootPropertyBuildIssueCode? code = null) => !plan.IsAvailable && plan.Binding is null && plan.Policy is null
        && plan.Population is null && plan.Procedure is null && plan.ProcedureProof is null && plan.Allocations.Count == 0 && plan.Claims.Count == 0
        && plan.Chunks.Count == 0 && plan.ObservedCounts.Count == 0 && plan.UnspecifiedSpans.Count == 0 && plan.UnresolvedRequirements.Count == 0
        && plan.DesiredBitCount == 0 && plan.UnspecifiedBitCount == 0 && !plan.CompleteFixedRootPopulation && !plan.CompleteApplicationImage
        && !plan.ExecutionEligible && !plan.DownloadEligible && plan.Issues.Count == 1 && (code is null || plan.Issues[0].Code == code);
    private static byte[] Raw(byte[] data, byte[]? mask = null, bool omitMask = true, bool omitCount = true)
        => PropertySourceChecks.Frame(data, dataMask: mask, omitDataMask: omitMask, omitCount: omitCount);

    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok, RootPropertyBuildPlan? plan = null)
        {
            checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "RootPropertyBuild " + name
                + (!ok && plan is not null ? " " + string.Join(",", plan.Issues.Select(i => i.Code + ":" + i.InitializationIssue?.Code)) : ""));
            if (!ok) failures++;
        }
        var full = Load(directory, App(130, 32, 130), PropertySourceChecks.Master("PDT_GENERIC_04"));
        var expected = new byte[520]; for (var i = 0; i < 130; i++) BinaryPrimitives.WriteUInt32BigEndian(expected.AsSpan(i * 4), (uint)i + 1);
        var before = Knxproj.ToXml(full.Project).ToString(); var archiveBefore = SHA256.HashData(File.ReadAllBytes(full.Path));
        var revision = full.Project.ApplicationDataRevision; var undo = new UndoStack(); undo.Reset(full.Project);
        var all = Preview(full);
        Check("complete 130-reference population produces independent 520-byte data without a subset", all.IsAvailable && all.Chunks.Count == 1
            && all.Population is { ReferenceCount: 130, PhysicalRecordCount: 130, ActiveCount: 130, OwnerCount: 130 }
            && all.Claims.Count == 130 && all.Chunks[0].WorkingData.ToArray().SequenceEqual(expected), all);
        Check("absent factory creates explicit zero initial data and validity", all.IsAvailable && all.Chunks[0].InitialData.Length == 520
            && all.Chunks[0].InitialData.ToArray().All(b => b == 0) && all.Chunks[0].InitialValidity.ToArray().All(b => b == 0));
        Check("all fixed bytes have desired validity but no full-image authority", all.IsAvailable && all.Chunks[0].DesiredValidity.ToArray().All(b => b == 255)
            && all.DesiredBitCount == 4160 && all.UnspecifiedBitCount == 0 && all.CompleteFixedRootPopulation
            && all.Scope == "RootFixedPropertyWorkingSet" && all.ProfileVersion == 1 && !all.CompleteApplicationImage && !all.ExecutionEligible && !all.DownloadEligible);
        Check("actual primitive procedure retains its own profile and shared proof", all.IsAvailable && all.Procedure!.ProfileVersion == 1
            && all.ProcedureProof!.ApplicationContextFingerprint == all.Binding!.ContextFingerprint && all.Allocations.Single().RequestedLength == 520);
        Check("complete preview and replay preserve model archive revision and undo", all.IsAvailable && all.Binding!.IsCurrent(full.Project, full.Device)
            && before == Knxproj.ToXml(full.Project).ToString() && archiveBefore.SequenceEqual(SHA256.HashData(File.ReadAllBytes(full.Path)))
            && revision == full.Project.ApplicationDataRevision && undo.Steps == 0 && !undo.IsDirty);
        if (all.IsAvailable)
        {
            var copy = all.Chunks[0].InitialData.ToArray(); copy[0] = 255; var working = all.Chunks[0].WorkingData.ToArray(); working[0] = 255;
            Check("copy-out cannot mutate initial or working bytes", all.Chunks[0].InitialData[0] == 0 && all.Chunks[0].WorkingData.ToArray().SequenceEqual(expected));
            full.Device.ParameterValues["R0"] = "1";
            Check("saved-key presence stales a binding even when the encoded value is unchanged", !all.Binding!.IsCurrent(full.Project, full.Device)
                && Preview(full).Binding?.Fingerprint != all.Binding.Fingerprint);
            full.Device.ParameterValues.Clear();
            Check("restoring exact current inputs restores same-route replay", all.Binding!.IsCurrent(full.Project, full.Device));
            var reloaded = Knxproj.Load(full.Path);
            Check("same-id replacement project cannot reuse a binding", !all.Binding.IsCurrent(reloaded, ProjectQuery.DeviceList(reloaded).Single()));
            typeof(Project).GetProperty(nameof(Project.ApplicationDataRevision))!.SetValue(full.Project, revision + 1);
            Check("source revision is bound", !all.Binding.IsCurrent(full.Project, full.Device));
            typeof(Project).GetProperty(nameof(Project.ApplicationDataRevision))!.SetValue(full.Project, revision);
            full.Project.Name = "edited"; undo.Push(full.Project); var restored = undo.Undo()!;
            Check("undo replacement requires its own successful property build", !all.Binding.IsCurrent(restored, ProjectQuery.DeviceList(restored).Single())
                && Preview(new(restored, ProjectQuery.DeviceList(restored).Single(), full.Path)).IsAvailable);
        }
        var little = App(2, 32, 4); Static(little).Element(Ns + "Options")!.SetAttributeValue("ParameterByteOrder", "LittleEndian");
        var littlePlan = Preview(Load(directory, little, PropertySourceChecks.Master("PDT_GENERIC_04")));
        Check("source little endian uses the same fixed codec", littlePlan.IsAvailable && Data(littlePlan) == "01000000020000000000000000000000"
            && Mask(littlePlan) == "FFFFFFFFFFFFFFFF0000000000000000", littlePlan);

        var three = App(); var ps = Static(three).Element(Ns + "Parameters")!; var rs = Static(three).Element(Ns + "ParameterRefs")!;
        Parameter(three).SetAttributeValue("Value", 5);
        var second = new XElement(Parameter(three)); second.SetAttributeValue("Id", "P1"); second.Element(Ns + "Property")!.SetAttributeValue("Offset", 2);
        second.Element(Ns + "Property")!.SetAttributeValue("BitOffset", 3); ps.Add(second);
        var byteType = new XElement(Static(three).Element(Ns + "ParameterTypes")!.Elements().Single()); byteType.SetAttributeValue("Id", "B");
        byteType.Element(Ns + "TypeNumber")!.SetAttributeValue("SizeInBit", 8); byteType.Element(Ns + "TypeNumber")!.SetAttributeValue("maxInclusive", 255);
        Static(three).Element(Ns + "ParameterTypes")!.Add(byteType);
        var last = new XElement(Parameter(three)); last.SetAttributeValue("Id", "P2"); last.SetAttributeValue("ParameterType", "B"); last.SetAttributeValue("Value", 9);
        last.Element(Ns + "Property")!.SetAttributeValue("Offset", 3); ps.Add(last);
        foreach (var i in new[] { 1, 2 }) { rs.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "R" + i), new XAttribute("RefId", "P" + i)));
            Program(three).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R" + i))); }
        var rawFactory = Raw(Convert.FromHexString("7F55E3CC"), [0, 240, 255, 0], false, false);
        var threeFixture = Load(directory, three, factory: rawFactory); var threePlan = Preview(threeFixture);
        Check("three disjoint fields retain raw factory bits despite partial source mask", threePlan.IsAvailable && Data(threePlan) == "BF55F709"
            && Mask(threePlan) == "FF00FFFF" && threePlan.Chunks[0].InitialData.ToHexString() == "7F55E3CC"
            && threePlan.Chunks[0].InitialValidity.ToHexString() == "00000000", threePlan);
        Check("count remains observed and unadopted without capacity or count writes", threePlan.IsAvailable && threePlan.ObservedCounts is { Count: 1 }
            && threePlan.ObservedCounts[0].Data.ToHexString() == "0002" && threePlan.ObservedCounts[0].Mask?.ToHexString() == "FFFF"
            && threePlan.ObservedCounts[0].EncodedCount == 2 && threePlan.ObservedCounts[0].Disposition == "ObservedNotAdopted");
        Check("canonical desired and inverse validity identify the untouched byte", threePlan.IsAvailable
            && threePlan.Chunks[0].CanonicalDesired.ToHexString() == "BF00F709" && threePlan.UnspecifiedBitCount == 8
            && threePlan.UnspecifiedSpans.Single() is { ByteOffset: 1, Length: 1, BitMask: 255 });
        var beforePatch = OfflinePropertyPatch.Preview(threeFixture.Project, threeFixture.Device, [new("R1", "5")], ParameterPatchByteOrder.BigEndian);
        Check("new initializer does not promote the old partial-mask before-data route", !beforePatch.IsAvailable);
        var nullMask = Preview(Load(directory, new XDocument(three), factory: Raw(Convert.FromHexString("7F55E3CC"), omitCount: false)));
        Check("null factory mask and partial factory mask create the same zero-validity working seed", nullMask.IsAvailable && Data(nullMask) == Data(threePlan)
            && Mask(nullMask) == Mask(threePlan) && nullMask.Binding!.Fingerprint != threePlan.Binding!.Fingerprint, nullMask);
        var lowBits = App(bits: 2); Parameter(lowBits).SetAttributeValue("Value", 2); Parameter(lowBits).Element(Ns + "Property")!.SetAttributeValue("BitOffset", 6);
        var lowBitsPlan = Preview(Load(directory, lowBits, factory: Raw([165], [0], false)));
        Check("factory mask00 is not the static-code zero-byte cleanup rule", lowBitsPlan.IsAvailable && Data(lowBitsPlan) == "A6000000"
            && Mask(lowBitsPlan) == "FF000000" && lowBitsPlan.Chunks[0].InitialData.ToHexString() == "A5000000", lowBitsPlan);
        var larger = Preview(Load(directory, factory: Raw(Convert.FromHexString("7F55AABBCCDD"))));
        Check("longer exact factory data grows the genuine allocation and retains unknown tail", larger.IsAvailable && larger.Allocations.Single() is { RequestedLength: 4, Length: 6 }
            && Data(larger) == "3F55AABBCCDD" && Mask(larger) == "FF0000000000", larger);
        var enumApp = App(bits: 16, elements: 1); var enumType = Static(enumApp).Element(Ns + "ParameterTypes")!.Elements().Single();
        enumType.ReplaceNodes(new XElement(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", 16),
            new XElement(Ns + "Enumeration", new XAttribute("Id", "E0"), new XAttribute("Value", 0), new XAttribute("Text", "zero")),
            new XElement(Ns + "Enumeration", new XAttribute("Id", "E60000"), new XAttribute("Value", 60000), new XAttribute("Text", "high"))));
        Parameter(enumApp).SetAttributeValue("Value", 60000);
        var enumFixture = Load(directory, enumApp); var enumPlan = Preview(enumFixture);
        Check("full sparse enumeration population encodes its exact value", enumPlan.IsAvailable && Data(enumPlan) == "EA60" && Mask(enumPlan) == "FFFF", enumPlan);
        enumFixture.Device.ParameterValues["R0"] = "60001";
        Check("an unlisted enumeration value fails atomically", Empty(Preview(enumFixture), RootPropertyBuildIssueCode.InvalidValue));
        var pdtA = Preview(Load(directory, factory: Raw([127])));
        var pdtB = Preview(Load(directory, master: PropertySourceChecks.Master("PDT_GENERIC_02"), factory: Raw([127])));
        Check("same-width PDT semantics have their own new binding", pdtA.IsAvailable && pdtB.IsAvailable && Data(pdtA) == Data(pdtB) && Mask(pdtA) == Mask(pdtB)
            && pdtA.Binding!.SourceFingerprint == pdtB.Binding!.SourceFingerprint && pdtA.Binding.PropertyProfileHash != pdtB.Binding.PropertyProfileHash
            && pdtA.Binding.InitializationHash != pdtB.Binding.InitializationHash && pdtA.Binding.Fingerprint != pdtB.Binding.Fingerprint);

        foreach (var (behavior, data, mask, disposition) in new[]
        {
            ("DefaultValue", "3F000000", "FF000000", RootParameterDisposition.InactiveDefault),
            ("None", "7F000000", "00000000", RootParameterDisposition.InactiveNullNone),
            ("Background", "7F000000", "FF000000", RootParameterDisposition.InactiveNullBackground),
        })
        {
            var app = App(); Program(app).Element(Ns + "Dynamic")!.RemoveNodes(); Reference(app).SetAttributeValue("Value", 2);
            Static(app).Element(Ns + "Options")!.SetAttributeValue("DownloadInvisibleParameters", behavior);
            var f = Load(directory, app, factory: Raw([127])); f.Device.ParameterValues["R0"] = "3"; var plan = Preview(f);
            Check("inactive native policy " + behavior, plan.IsAvailable && Data(plan) == data && Mask(plan) == mask
                && plan.Population!.Records.Single().Disposition == disposition && plan.Claims.Single().ByteOffset == 0, plan);
        }
        var activeApp = App(); Reference(activeApp).SetAttributeValue("Value", 2); Parameter(activeApp).SetAttributeValue("CustomerAdjustable", true);
        var active = Load(directory, activeApp); active.Device.ParameterValues["R0"] = "3"; var activePlan = Preview(active);
        Check("current uses Parameter1 Ref2 Saved3 and inherited customer metadata", activePlan.IsAvailable && Data(activePlan) == "60000000"
            && activePlan.Population!.Records[0] is { CurrentValueOrigin: RootParameterValueOrigin.Saved, CustomerAdjustable: true }, activePlan);
        active.Device.ParameterValues["R0"] = "03";
        Check("raw saved lexicon is bound even for the same word", activePlan.IsAvailable && !activePlan.Binding!.IsCurrent(active.Project, active.Device)
            && Data(Preview(active)) == "60000000");
        var always = App(); Program(always).Element(Ns + "Dynamic")!.RemoveNodes(); Parameter(always).SetAttributeValue("LegacyPatchAlways", true);
        Static(always).Element(Ns + "Options")!.SetAttributeValue("DownloadInvisibleParameters", "None");
        var alwaysFixture = Load(directory, always); alwaysFixture.Device.ParameterValues["R0"] = "3"; var alwaysPlan = Preview(alwaysFixture);
        Check("inactive Always retains its actual current value", alwaysPlan.IsAvailable && Data(alwaysPlan) == "60000000"
            && alwaysPlan.Population!.Records.Single().Disposition == RootParameterDisposition.AlwaysValue, alwaysPlan);
        var old = App(); Program(old).SetAttributeValue("MinEtsVersion", "4.9"); Program(old).Element(Ns + "Dynamic")!.RemoveNodes();
        Static(old).Element(Ns + "Options")!.SetAttributeValue("DownloadInvisibleParameters", "None");
        var oldPlan = Preview(Load(directory, old, factory: Raw([127])));
        Check("native minimum version keeps old inactive background policy", oldPlan.IsAvailable && Data(oldPlan) == "7F000000" && Mask(oldPlan) == "FF000000", oldPlan);
        var noStorage = App(); Static(noStorage).Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "PN"), new XAttribute("ParameterType", "T"), new XAttribute("Value", 4)));
        Static(noStorage).Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "RN"), new XAttribute("RefId", "PN")));
        Program(noStorage).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "RN")));
        var noStoragePlan = Preview(Load(directory, noStorage));
        Check("NoStorage belongs to the full population without a physical claim", noStoragePlan.IsAvailable && noStoragePlan.Population is { ReferenceCount: 2, NoStorageCount: 1 }
            && noStoragePlan.Claims.Count == 1 && noStoragePlan.Population.Records.Single(r => r.RefId == "RN") is { NativeRecordEmitted: false, PropertyKey: null }, noStoragePlan);
        var access = App(); Parameter(access).SetAttributeValue("Access", "None"); Reference(access).SetAttributeValue("Access", "Read");
        var accessPlan = Preview(Load(directory, access)); Check("access and UI visibility do not filter native writers", accessPlan.IsAvailable && Data(accessPlan) == "20000000", accessPlan);
        var prefix = App(); Program(prefix).SetAttributeValue("Id", "M-009E_A-1000-01-0000");
        var prefixPlan = Preview(Load(directory, prefix, factory: Raw([127])));
        Check("the existing special-prefix policy validates only fixed field bits", prefixPlan.IsAvailable && Data(prefixPlan) == "3F000000"
            && Mask(prefixPlan) == "E0000000" && prefixPlan.Policy!.MaskPolicy == RootParameterMaskPolicy.FieldBitsOnly, prefixPlan);

        var conditional = App(); var write = conditional.Descendants(Ns + "LdCtrlWriteProp").Single();
        write.ReplaceWith(new XElement(Ns + "choose", new XAttribute("ParamRefId", "R0"), new XElement(Ns + "when", new XAttribute("test", "1"), Write()),
            new XElement(Ns + "when", new XAttribute("default", true), Write(3))));
        var conditionFixture = Load(directory, conditional); var conditionPlan = Preview(conditionFixture);
        Check("conditional initializer and full writer use the same application proof", conditionPlan.IsAvailable && conditionPlan.Procedure!.ProfileVersion == 2
            && conditionPlan.Allocations.Single().Length == 4 && conditionPlan.ProcedureProof!.Controllers.Single().RefId == "R0"
            && conditionPlan.ProcedureProof.ApplicationContextFingerprint == conditionPlan.Binding!.ContextFingerprint, conditionPlan);
        conditionFixture.Device.ParameterValues["R0"] = "2"; var conditionChanged = Preview(conditionFixture);
        Check("a native default branch changes actual allocation and shared replay", conditionChanged.IsAvailable && conditionChanged.Allocations.Single().Length == 6
            && !conditionPlan.Binding!.IsCurrent(conditionFixture.Project, conditionFixture.Device) && Data(conditionChanged) == "400000000000", conditionChanged);
        var overlap = App(); overlap.Descendants(Ns + "LdCtrlWriteProp").Single().ReplaceWith(new XElement(Ns + "choose", new XAttribute("ParamRefId", "R0"),
            new XElement(Ns + "when", new XAttribute("test", "1"), Write()), new XElement(Ns + "when", new XAttribute("test", ">=1"), Write(2, 2))));
        var overlapPlan = Preview(Load(directory, overlap));
        Check("all matching procedure branches feed the native second-stage extent group", overlapPlan.IsAvailable && overlapPlan.Allocations.Single().Length == 6
            && overlapPlan.Allocations.Single().ControlOrdinals.Count == 2, overlapPlan);

        void Refuse(string name, Action<XDocument> edit, RootPropertyBuildIssueCode? code = null)
        { var app = App(); edit(app); var plan = Preview(Load(directory, app)); Check(name, Empty(plan, code), plan); }
        Refuse("latent unreferenced owner requires real control coverage", app =>
        {
            var hidden = new XElement(Parameter(app)); hidden.SetAttributeValue("Id", "latent"); hidden.SetAttributeValue("Access", "None");
            hidden.Element(Ns + "Property")!.SetAttributeValue("Offset", 100); Static(app).Element(Ns + "Parameters")!.Add(hidden);
        }, RootPropertyBuildIssueCode.InitializationUnavailable);
        Refuse("equal bytes cannot excuse a hidden overlapping owner", app => { var hidden = new XElement(Parameter(app)); hidden.SetAttributeValue("Id", "latent"); Static(app).Element(Ns + "Parameters")!.Add(hidden); }, RootPropertyBuildIssueCode.AmbiguousOwnership);
        Refuse("unselected memory owner remains a hard physical-alias refusal", app =>
            Static(app).Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "latent"), new XAttribute("ParameterType", "T"), new XAttribute("Value", 0), new XElement(Ns + "Memory"))), RootPropertyBuildIssueCode.UnsupportedPhysicalAlias);
        Refuse("aliases are not deduplicated into a fake single writer", app => Static(app).Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "alias"), new XAttribute("RefId", "P0"))), RootPropertyBuildIssueCode.AmbiguousOwnership);
        Refuse("a nonempty source Code cannot be ignored", app => Static(app).Add(new XElement(Ns + "Code", new XElement(Ns + "AbsoluteSegment", new XAttribute("Id", "S"), new XAttribute("Address", 0), new XAttribute("Size", 0)))), RootPropertyBuildIssueCode.UnsupportedProfile);
        Refuse("module definitions remain outside this root source profile", app => Program(app).Add(new XElement(Ns + "ModuleDefs", new XElement(Ns + "ModuleDef", new XAttribute("Id", "MD")))), RootPropertyBuildIssueCode.UnsupportedProfile);
        Refuse("assignments remain outside the writer proof", app => Program(app).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "R0"), new XAttribute("Value", 1))), RootPropertyBuildIssueCode.UnsupportedProfile);
        Refuse("external configuration is not inferred from a saved numeric value", app => Program(app).SetAttributeValue("RequiresExternalSoftware", true), RootPropertyBuildIssueCode.ExternalConfigurationUnaccounted);
        Refuse("property storage with nonwhite text is not a native leaf", app => Parameter(app).Element(Ns + "Property")!.Add("unexpected"), RootPropertyBuildIssueCode.InvalidDeclaration);
        Refuse("missing literal property identity is not object zero inference", app => Parameter(app).Element(Ns + "Property")!.Attribute("PropertyId")!.Remove(), RootPropertyBuildIssueCode.InvalidDeclaration);
        Refuse("misplaced original reference cannot hide from full ownership", app => Program(app).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "hidden"), new XAttribute("RefId", "P0"))), RootPropertyBuildIssueCode.InvalidDeclaration);
        Refuse("unsupported NoStorage Raw remains rejected before evaluation", app => { Parameter(app).RemoveNodes(); Static(app).Element(Ns + "ParameterTypes")!.Elements().Single().ReplaceNodes(new XElement(Ns + "TypeRawData", new XAttribute("MaxSize", 10))); }, RootPropertyBuildIssueCode.UnsupportedType);
        Refuse("unused invalid nested enumeration cannot reach typed parsing", app => Static(app).Element(Ns + "ParameterTypes")!.Elements().Single().Elements().Single()
            .Add(Enumerable.Range(0, 4097).Select(i => new XElement(Ns + "Enumeration", new XAttribute("Id", "E" + i), new XAttribute("Value", i)))), RootPropertyBuildIssueCode.InvalidDeclaration);
        Refuse("selected control removal cannot fabricate an allocation", app => app.Descendants(Ns + "LdCtrlWriteProp").Single().Remove(), RootPropertyBuildIssueCode.InitializationUnavailable);
        Refuse("duplicate native control tuples refuse atomically", app => app.Descendants(Ns + "LdCtrlWriteProp").Single().AddAfterSelf(Write()), RootPropertyBuildIssueCode.InitializationUnavailable);
        var asymmetric = Preview(Load(directory, master: PropertySourceChecks.Master("PDT_CONTROL")));
        Check("asymmetric PDT is not a fixed formatter capacity guess", Empty(asymmetric, RootPropertyBuildIssueCode.UnsupportedProfile), asymmetric);
        var shortCoverage = App(2, 32, 1); var uncovered = Preview(Load(directory, shortCoverage, PropertySourceChecks.Master("PDT_GENERIC_04")));
        Check("later physical records cannot resize the preallocated property", Empty(uncovered, RootPropertyBuildIssueCode.InitializationUnavailable), uncovered);
        var inactive = App(); Program(inactive).Element(Ns + "Dynamic")!.RemoveNodes();
        var invalidSaved = Load(directory, inactive); invalidSaved.Device.ParameterValues["R0"] = "8";
        Check("invalid current remains invalid even before inactive-default selection", Empty(Preview(invalidSaved), RootPropertyBuildIssueCode.InvalidValue));
        var opaque = Load(directory); opaque.Device.BinaryData["opaque"] = [1];
        Check("unaccounted binary payload cannot accompany a property build", Empty(Preview(opaque), RootPropertyBuildIssueCode.UnaccountedBinaryData));
        var longGuid = Load(directory); longGuid.Project.Guid = new string('x', 257);
        Check("identity bounds precede source reading", Empty(Preview(longGuid), RootPropertyBuildIssueCode.InvalidRequest));
        var invalidRequest = Preview(Load(directory), new(new("all", LoadProcedureAccess.Remote, LoadParts.Groups)));
        Check("groups-only request does not silently run parameter writers", Empty(invalidRequest, RootPropertyBuildIssueCode.InvalidRequest));
        var excessive = App(); var excessiveControl = excessive.Descendants(Ns + "LdCtrlWriteProp").Single();
        excessiveControl.SetAttributeValue("StartElement", 65535); excessiveControl.SetAttributeValue("Count", 65535);
        var excessivePlan = Preview(Load(directory, excessive, PropertySourceChecks.Master("PDT_GENERIC_20")));
        Check("actual control and PDT extent is bounded before materialization", Empty(excessivePlan, RootPropertyBuildIssueCode.LimitExceeded), excessivePlan);
        Console.WriteLine($"RootPropertyBuild checks: {checks}, failures: {failures}"); return failures;
    }
}
