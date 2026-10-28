using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;

internal static class NativeImageBuildPublicChecks
{
    private static readonly XNamespace Ns = PropertySourceChecks.Ns;
    private static NativeImageBuildPlan Preview(ArchivedImageSourceChecks.Fixture fixture, uint[]? options = null,
        GeneratedTableFormat? tables = null)
        => OfflineNativeImageBuild.Preview(fixture.Project, fixture.Device,
            new(options ?? [1u], tables is { } format ? new(format) : null));
    private static string Xml(Project project) => Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Explain(NativeImageBuildPlan plan) => string.Join(",", plan.Issues.Select(i => i.Code))
        + string.Join(";", plan.Variants.Select(v => string.Join(",", v.Issues.Select(i => i.Code))));
    private static NativeImageBuildChunk Memory(NativeImageBuildVariant variant, uint offset = 100)
        => variant.Chunks.Single(c => c.Target.Space == NativeImageBuildSpace.StandardMemory && c.ByteOffset == offset);
    private static NativeImageBuildChunk Property(NativeImageBuildVariant variant, ushort pid)
        => variant.Chunks.Single(c => c.Target.Space == NativeImageBuildSpace.PropertyData && c.Target.Property?.PropertyId == pid);
    private static bool Bytes(ImmutablePatchBytes? actual, byte[] expected) => actual is not null && actual.ToArray().SequenceEqual(expected);
    private static bool NoAuthority(NativeImageBuildPlan plan) => !plan.ActualOptionsKnown && !plan.CompleteApplicationImage
        && !plan.ExecutionEligible && !plan.DownloadEligible;
    private static bool Empty(NativeImageBuildPlan plan, NativeImageBuildIssueCode? code = null)
        => plan.HasGlobalFailure && !plan.IsAvailable && !plan.AllRequestedAssumptionsAvailable && plan.Binding is null
            && plan.Variants.Count == 0 && plan.Issues.Count == 1 && (code is null || plan.Issues[0].Code == code)
            && plan.SourceHash is null && plan.SharedContextHash is null && plan.PreparationHash is null
            && plan.AllocatedImagePayloadBytes == 0 && NoAuthority(plan);
    private static bool Empty(NativeImageBuildVariant variant) => !variant.IsAvailable && variant.Issues.Count != 0
        && variant.Fingerprint is null && variant.InitializationHash is null && variant.CoverageHash is null
        && variant.Population is null && variant.Procedure is null && variant.Tables is null
        && variant.Sources.Count == 0 && variant.Chunks.Count == 0 && variant.ObservedCounts.Count == 0 && variant.Requirements.Count == 0;
    private static bool ReadOnly<T>(IReadOnlyList<T> values)
    {
        if (values is not IList<T> list || !list.IsReadOnly) return false;
        try { list.Clear(); return false; } catch (NotSupportedException) { return true; }
    }
    private static bool CopyProtected(ImmutablePatchBytes? bytes)
    {
        if (bytes is null) return true;
        var original = bytes.ToArray(); var hash = bytes.Sha256; var copy = bytes.ToArray();
        if (copy.Length != 0) copy[0] ^= 255;
        return bytes.ToArray().SequenceEqual(original) && bytes.Sha256 == hash;
    }

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool ok)
        { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "NativeImageBuildPublic " + name); if (!ok) failures++; }

        foreach (var (name, request) in new (string, NativeImageBuildRequest?)[]
        {
            ("null request", null), ("null option list", new(null!)), ("empty option list", new(Array.Empty<uint>())),
            ("duplicate full words", new(new uint[] { 1, 1 })),
            ("seventeen distinct words", new(Enumerable.Range(0, 17).Select(i => (uint)i).ToArray())),
            ("undefined table format", new(new uint[] { 1 }, new((GeneratedTableFormat)(-1)))),
        })
            Check(name + " fails before any project or archive access", Empty(OfflineNativeImageBuild.Preview(null!, null!, request!), NativeImageBuildIssueCode.InvalidRequest));
        Check("valid options with missing project are a controlled request failure",
            Empty(OfflineNativeImageBuild.Preview(null!, null!, new(new uint[] { 1 })), NativeImageBuildIssueCode.InvalidRequest));

        var fixture = NativeGeneralImageBuildChecks.Mixed(directory);
        var xml = Xml(fixture.Project); var archiveHash = Hash(File.ReadAllBytes(fixture.Path)); var revision = fixture.Project.ApplicationDataRevision;
        var undo = new UndoStack(); undo.Reset(fixture.Project);
        var options = new List<uint> { 1 }; var requestCopy = new NativeImageBuildRequest(options);
        var plan = OfflineNativeImageBuild.Preview(fixture.Project, fixture.Device, requestCopy);
        Check("public mixed path accepts all131 original references: " + Explain(plan), plan.IsAvailable && !plan.HasGlobalFailure
            && plan.AllRequestedAssumptionsAvailable && plan.Variants.Count == 1 && plan.DeclaredReferenceCount == 131 && plan.DeclaredOwnerCount == 130);
        Check("public profile is explicitly logical and conditional", plan.ProfileVersion == 1 && plan.Scope == "NativeLogicalWorkingSet" && NoAuthority(plan));
        Check("source and one shared evaluation have distinct published hashes", plan.IsAvailable && plan.SourceFingerprint is { Length: 64 }
            && plan.SourceHash is { Length: 64 } && plan.ApplicationDefinitionHash is { Length: 64 } && plan.HardwareDefinitionHash is { Length: 64 }
            && plan.PreparationHash is { Length: 64 } && plan.SharedContextHash is { Length: 64 } && plan.Binding?.Fingerprint.Length == 64);
        options[0] = 129; options.Clear();
        Check("input-list mutation cannot replace the owned assumptions", plan.AssumedOptions.SequenceEqual(new uint[] { 1 })
            && plan.Variants.SingleOrDefault()?.AssumedOptions == 1 && plan.Binding?.IsCurrent(fixture.Project, fixture.Device) == true);
        if (plan.IsAvailable)
        {
            var variant = plan.Variants.Single(); var memory = Memory(variant); var property = Property(variant, 10);
            var initial = new byte[512]; initial[0] = 0xA0;
            var working = new byte[512]; working[0] = 0xA2;
            for (var i = 1; i < 128; i++) BinaryPrimitives.WriteUInt32BigEndian(working.AsSpan(i * 4, 4), (uint)i + 1);
            var validity = Enumerable.Repeat((byte)255, 512).ToArray(); validity[1] = validity[2] = validity[3] = 0;
            var writes = Enumerable.Repeat((byte)255, 512).ToArray(); writes[0] = 3; writes[1] = writes[2] = writes[3] = 0;
            Check("whole512 initial bytes preserve the region/code merge independently of the writer", Bytes(memory.InitialData, initial)
                && Bytes(memory.InitialValidity, new byte[512]));
            Check("whole512 working bytes match an independent BCL word serializer", Bytes(memory.WorkingData, working));
            Check("whole512 desired validity distinguishes the three unwritten bytes", Bytes(memory.DesiredValidity, validity));
            Check("whole512 canonical desired bytes follow the independently expected validity", Bytes(memory.CanonicalDesired,
                working.Zip(validity, (data, mask) => (byte)(data & mask)).ToArray()));
            Check("whole512 parameter write-bit map distinguishes a field from whole-byte validity", Bytes(memory.ParameterWriteBits, writes));
            var code = variant.Sources.Single(s => s.Kind == NativeImageBuildSourceKind.NativeCode);
            Check("source raw A5/F0 remains separate from initial A0 and working A2", code.RawData?.ToHexString() == "A5"
                && code.RawMask?.ToHexString() == "F0" && code.ByteOffset == 100 && code.Length == 512
                && code.DeclaredLength == 512 && memory.InitialData[0] == 0xA0 && memory.WorkingData[0] == 0xA2);
            Check("source roles retain the native contribution order", variant.Sources.Select(s => s.Order).SequenceEqual(Enumerable.Range(0, variant.Sources.Count))
                && variant.Sources[0].Kind == NativeImageBuildSourceKind.ZeroRegion && variant.Sources[0].RawData is null
                && variant.Sources[0].RawMask is null && variant.Sources.Count(s => s.Kind == NativeImageBuildSourceKind.PointerZero) == 5);
            Check("whole8 property initial bytes are raw factory data with fresh zero validity", Bytes(property.InitialData, Convert.FromHexString("DEADBEEF10203040"))
                && Bytes(property.InitialValidity, new byte[8]));
            Check("whole8 property working desired and write bits cover words129and130", Bytes(property.WorkingData, Convert.FromHexString("0000008100000082"))
                && Bytes(property.CanonicalDesired, Convert.FromHexString("0000008100000082"))
                && Bytes(property.DesiredValidity, Enumerable.Repeat((byte)255, 8).ToArray())
                && Bytes(property.ParameterWriteBits, Enumerable.Repeat((byte)255, 8).ToArray()));
            var unrelated = Property(variant, 11);
            Check("unrelated selected allocation remains present with unknown zero bytes", Bytes(unrelated.InitialData, new byte[8])
                && Bytes(unrelated.WorkingData, new byte[8]) && Bytes(unrelated.DesiredValidity, new byte[8])
                && Bytes(unrelated.CanonicalDesired, new byte[8]) && Bytes(unrelated.ParameterWriteBits, new byte[8]));
            var pointers = variant.Chunks.Where(c => c.Target.Property?.PropertyId == 7).ToArray();
            Check("five logical pointer chunks remain zero and unknown", pointers.Length == 5 && pointers.All(c => c.Length == 4
                && Bytes(c.InitialData, new byte[4]) && Bytes(c.InitialValidity, new byte[4])
                && Bytes(c.WorkingData, new byte[4]) && Bytes(c.DesiredValidity, new byte[4])));
            Check("count1234 remains a separate unadopted two-byte observation", variant.ObservedCounts is [{ EncodedCount: 0x1234, NotAdopted: true } count]
                && count.Data.ToHexString() == "1234" && !variant.Chunks.Any(c => c.Target.Space == NativeImageBuildSpace.PropertyCount));
            Check("all131 records include130 native physical writers and one distinct NoStorage", variant.Population is
                { ReferenceCount: 131, PhysicalRecordCount: 130, NoStorageCount: 1, ActiveCount: 130, OwnerCount: 130,
                    ParameterTaskScheduled: true, CompleteAdmittedRootPopulation: true } population
                && population.Records.Single(r => r.RefId == "RNS") is { Disposition: RootParameterDisposition.NoStorage,
                    NativeRecordEmitted: false, Allocates: false, WritesData: false, Field: null }
                && population.Records.Where(r => r.NativeRecordEmitted).All(r => r.NormalReferenceCount == 1 && r.Active));
            Check("population exposes hashes and geometry instead of raw or encoded values", typeof(NativeImageBuildRecord).GetProperties().All(p =>
                p.Name is not ("EncodedWord" or "Word" or "RawValue" or "CurrentValue" or "RecordValue"))
                && variant.Population!.Records.All(r => r.CurrentValueHash.Length == 64
                    && (r.RecordValueHash is null || r.RecordValueHash.Length == 64)));
            Check("primitive procedure controls are detached immutable metadata", variant.Procedure is { State: NativeImageBuildProcedureState.Compiled } procedure
                && procedure.EffectiveControls.Select(c => c.Kind).SequenceEqual(new[] { LoadControlKind.Connect,
                    LoadControlKind.WriteProperty, LoadControlKind.WriteProperty, LoadControlKind.Disconnect })
                && procedure.DeclarationWitnessHash is { Length: 64 } && procedure.Fingerprint.Length == 64);
            Check("public aggregate ledger includes owned output without overstating the procedure budget", plan.AllocatedImagePayloadBytes > 0
                && plan.AllocatedImagePayloadBytes <= plan.ReservedWorstCaseImagePayloadBytes
                && plan.ReservedWorstCaseImagePayloadBytes <= 16L * 1024 * 1024 && plan.ReservedProcedurePayloadBytes <= 16L * 1024 * 1024);
            Check("remaining route mapping and finalization requirements stay visible", variant.Requirements.Contains("ActualOptionsUnverified")
                && variant.Requirements.Contains("PhysicalMappingUnverified") && variant.Requirements.Contains("FinalizationNotApplied"));
            Check("all exposed aggregate collections reject mutation", ReadOnly(plan.AssumedOptions) && ReadOnly(plan.Variants) && ReadOnly(plan.Issues)
                && ReadOnly(variant.Sources) && ReadOnly(variant.Chunks) && ReadOnly(variant.ObservedCounts) && ReadOnly(variant.Requirements)
                && ReadOnly(memory.SourceIds) && ReadOnly(variant.Population!.Records) && ReadOnly(variant.Procedure!.EffectiveControls)
                && variant.Procedure.EffectiveControls.All(c => ReadOnly(c.Arguments) && ReadOnly(c.OnError)));
            Check("all source initial working canonical mask and count copy-outs protect the stored artifacts", variant.Sources.All(s => CopyProtected(s.RawData) && CopyProtected(s.RawMask))
                && variant.Chunks.All(c => CopyProtected(c.InitialData) && CopyProtected(c.InitialValidity) && CopyProtected(c.WorkingData)
                    && CopyProtected(c.DesiredValidity) && CopyProtected(c.CanonicalDesired) && CopyProtected(c.ParameterWriteBits))
                && variant.ObservedCounts.All(c => CopyProtected(c.Data) && CopyProtected(c.Mask)));
            Check("getter and copy-out use preserves the binding and recorded ledger", plan.Binding!.IsCurrent(fixture.Project, fixture.Device)
                && memory.WorkingData[0] == 0xA2 && code.RawData![0] == 0xA5);
            fixture.Device.ParameterValues["R127"] = "513";
            Check("a late physical reference change stales the public binding", !plan.Binding.IsCurrent(fixture.Project, fixture.Device));
            var changed = Preview(fixture);
            Check("a fresh public plan reflects that last word and owns a new binding", changed.IsAvailable && changed.Binding?.Fingerprint != plan.Binding.Fingerprint
                && Memory(changed.Variants.Single()).WorkingData.ToArray().AsSpan(508, 4).SequenceEqual(new byte[] { 0, 0, 2, 1 }));
            fixture.Device.ParameterValues.Clear();
            Check("exact raw restoration makes the original binding current", plan.Binding.IsCurrent(fixture.Project, fixture.Device));
            var sameIds = Knxproj.Load(fixture.Path); var sameDevice = ProjectQuery.DeviceList(sameIds).Single();
            Check("matching ids in a freshly loaded model cannot reuse the original binding", !plan.Binding.IsCurrent(sameIds, sameDevice)
                && Preview(new(sameIds, sameDevice, fixture.Path, fixture.MasterBytes)).IsAvailable);
            fixture.Device.ParameterValues["R127"] = "0128";
            Check("equal encoded values with different raw presence still stale the binding", !plan.Binding.IsCurrent(fixture.Project, fixture.Device));
            fixture.Device.ParameterValues.Clear();
            var full = Preview(fixture, [129]);
            Check("QueryFull is a separate assumption that retains F0 and skips pointer seeds", full.IsAvailable && NoAuthority(full)
                && Memory(full.Variants.Single()).InitialValidity?[0] == 0xF0
                && full.Variants.Single().Sources.All(s => s.Kind != NativeImageBuildSourceKind.PointerZero)
                && full.Binding?.Fingerprint != plan.Binding.Fingerprint);
        }
        Check("public builds and freshness replay preserve XML archive revision and undo", Xml(fixture.Project) == xml
            && Hash(File.ReadAllBytes(fixture.Path)) == archiveHash && fixture.Project.ApplicationDataRevision == revision
            && undo.Steps == 0 && !undo.CanUndo && !undo.CanRedo && !undo.IsDirty);
        foreach (var missingMask in new[] { false, true })
        {
            var raw = PropertySourceChecks.Frame(data: Convert.FromHexString("7F11223344556677"),
                dataMask: Enumerable.Repeat((byte)255, 8).ToArray(), count: [0x12, 0x34], omitDataMask: missingMask);
            var observed = Preview(NativeGeneralImageBuildChecks.Mixed(directory, raw: raw));
            Check((missingMask ? "absent" : "FF") + " factory validity is provenance rather than initial writer validity", observed.IsAvailable
                && observed.Variants.Single().Sources.Single(s => s.Kind == NativeImageBuildSourceKind.FactoryProperty && s.Target.Property?.PropertyId == 10) is
                    { RawData: { } rawData } source && rawData.ToHexString() == "7F11223344556677"
                && (missingMask ? source.RawMask is null : source.RawMask?.ToHexString() == "FFFFFFFFFFFFFFFF")
                && Bytes(Property(observed.Variants.Single(), 10).InitialData, Convert.FromHexString("7F11223344556677"))
                && Bytes(Property(observed.Variants.Single(), 10).InitialValidity, new byte[8])
                && observed.Variants.Single().ObservedCounts.Single().EncodedCount == 0x1234);
        }

        var skip = Preview(fixture, [128]);
        Check("a skipped parameter task is not a collection of NoStorage records", skip.IsAvailable && skip.Variants.Single() is
            { Tasks.Parameters: false, Population: { ParameterTaskScheduled: false, CompleteAdmittedRootPopulation: false,
                ReferenceCount: 0, PhysicalRecordCount: 0, NoStorageCount: 0, OwnerCount: 0 } } && skip.DeclaredReferenceCount == 131);
        var words = Enumerable.Range(0, 16).Select(i => 8u + 256u * (uint)i).ToArray();
        var conditional = Preview(fixture, words);
        Check("sixteen distinct full words yield thirty-two explicit unknown-IP alternatives: " + Explain(conditional), conditional.IsAvailable
            && conditional.AllRequestedAssumptionsAvailable && conditional.AssumedOptions.SequenceEqual(words) && conditional.Variants.Count == 32 && NoAuthority(conditional));
        Check("each full word retains both unproven tool-IP facts with exact task predicates", conditional.Variants.Count == 32
            && words.All(word => conditional.Variants.Where(v => v.AssumedOptions == word).OrderBy(v => v.ToolManagedIp?.Value).ToArray() is
                [{ ToolManagedIp: { Value: false, SourceProven: false }, Tasks: { BasicProcedure: true, AddCode: true, Parameters: false, Tables: false } },
                 { ToolManagedIp: { Value: true, SourceProven: false }, Tasks: { BasicProcedure: false, AddCode: false, Parameters: false, Tables: false } }]));
        Check("all skipped alternatives carry no initialization or writer payloads", conditional.Variants.Where(v => v.ToolManagedIp?.Value == true).Count() == 16
            && conditional.Variants.Where(v => v.ToolManagedIp?.Value == true).All(v => v.IsAvailable && v.Sources.Count == 0 && v.Chunks.Count == 0
                && v.Population is { ReferenceCount: 0, ParameterTaskScheduled: false } && v.Procedure?.State == NativeImageBuildProcedureState.BasicSkipped));
        Check("option alternatives retain stable unique ordinals and never claim source-proven routes", conditional.Variants.Select(v => v.Ordinal).SequenceEqual(Enumerable.Range(0, 32))
            && conditional.Variants.Select(v => v.Fingerprint).Distinct().Count() == 32
            && conditional.Variants.All(v => v.ToolManagedIp?.SourceProven == false && v.Requirements.Contains("ActualOptionsUnverified")));

        var inactiveApp = NativeGeneralImageBuildChecks.MixedApplication();
        NativeGeneralPopulationChecks.Program(inactiveApp).Element(Ns + "Dynamic")!.Elements(Ns + "ParameterRefRef")
            .Single(e => (string?)e.Attribute("RefId") == "R0").Remove();
        var inactiveFixture = NativeGeneralImageBuildChecks.Mixed(directory, inactiveApp); inactiveFixture.Device.ParameterValues["R0"] = "3";
        var inactive = Preview(inactiveFixture);
        Check("inactive policy retains hydrated current separately from the chosen parameter default", inactive.IsAvailable
            && inactive.Variants.Single().Population!.Records.Single(r => r.RefId == "R0") is
                { Active: false, NormalReferenceCount: 0, Disposition: RootParameterDisposition.InactiveDefault, WritesData: true } inactiveRecord
            && inactiveRecord.CurrentValueHash != inactiveRecord.RecordValueHash && Memory(inactive.Variants.Single()).WorkingData[0] == 0xA2);
        var alwaysApp = new XDocument(inactiveApp);
        NativeGeneralPopulationChecks.Stat(alwaysApp).Element(Ns + "Parameters")!.Elements().First().SetAttributeValue("LegacyPatchAlways", true);
        var alwaysFixture = NativeGeneralImageBuildChecks.Mixed(directory, alwaysApp); alwaysFixture.Device.ParameterValues["R0"] = "3";
        var always = Preview(alwaysFixture);
        Check("an existing Always reference uses hydrated current with no active normal owner", always.IsAvailable
            && always.Variants.Single().Population!.Records.Single(r => r.RefId == "R0") is
                { Active: false, NormalReferenceCount: 0, LegacyPatchAlways: true, Disposition: RootParameterDisposition.AlwaysValue } alwaysRecord
            && alwaysRecord.CurrentValueHash == alwaysRecord.RecordValueHash && Memory(always.Variants.Single()).WorkingData[0] == 0xA3);
        var customerApp = NativeGeneralImageBuildChecks.MixedApplication();
        NativeGeneralPopulationChecks.Stat(customerApp).Element(Ns + "Parameters")!.Elements().First().SetAttributeValue("CustomerAdjustable", true);
        var customer = Preview(NativeGeneralImageBuildChecks.Mixed(directory, customerApp), [1, 1025]);
        Check("customer exclusion is an option assumption with no data write and a whole-byte validity clear", customer.IsAvailable
            && customer.AllRequestedAssumptionsAvailable
            && customer.Variants.Single(v => v.AssumedOptions == 1).Population!.CustomerPolicy == "IncludeAll"
            && customer.Variants.Single(v => v.AssumedOptions == 1025).Population is { CustomerPolicy: "ExcludeAdjustable", CustomerExcludedCount: 1 } excluded
            && excluded.Records.Single(r => r.RefId == "R0") is { NativeRecordEmitted: true, Allocates: true, WritesData: false, CustomerExcluded: true }
            && Memory(customer.Variants.Single(v => v.AssumedOptions == 1025)).WorkingData[0] == 0xA0
            && Memory(customer.Variants.Single(v => v.AssumedOptions == 1025)).DesiredValidity?[0] == 0);

        var littleApp = NativeGeneralPopulationChecks.App();
        NativeGeneralPopulationChecks.Stat(littleApp).Element(Ns + "Options")!.SetAttributeValue("ParameterByteOrder", "LittleEndian");
        var littleFixture = NativeGeneralImageBuildChecks.Mixed(directory, littleApp); littleFixture.Device.ParameterValues["R0"] = "16909060";
        var little = Preview(littleFixture); var expectedLittle = new byte[512];
        for (var i = 0; i < 128; i++) BinaryPrimitives.WriteUInt32LittleEndian(expectedLittle.AsSpan(i * 4, 4), i == 0 ? 0x01020304u : (uint)i + 1);
        Check("whole native32-bit LittleEndian memory uses source policy without the unrelated two-bit field: " + Explain(little), little.IsAvailable
            && Bytes(Memory(little.Variants.Single()).WorkingData, expectedLittle));
        Check("whole LittleEndian property words share the same source byte-order policy", little.IsAvailable
            && Bytes(Property(little.Variants.Single(), 10).WorkingData, Convert.FromHexString("8100000082000000")));
        var littleSource = Preview(littleFixture, [129]);
        Check("endian choice changes writer bytes without rewriting raw code", littleSource.IsAvailable
            && littleSource.Variants.Single().Sources.Single(s => s.Kind == NativeImageBuildSourceKind.NativeCode).RawData?.ToHexString() == "A5");

        var missingPointer = NativeGeneralImageBuildChecks.MixedMaster();
        missingPointer.Descendants(Ns + "Resource").Single(r => (string?)r.Attribute("Name") == "ApplicationDataPtr").Remove();
        var partialFixture = NativeGeneralImageBuildChecks.Mixed(directory, master: missingPointer);
        var partialXml = Xml(partialFixture.Project); var partialBytes = Hash(File.ReadAllBytes(partialFixture.Path));
        var partial = Preview(partialFixture, [1, 129]);
        Check("unsupported assumptions remain isolated explicit alternatives rather than disappearing", partial.IsAvailable && !partial.HasGlobalFailure
            && !partial.AllRequestedAssumptionsAvailable && partial.Binding is not null && partial.Variants.Count == 2
            && Empty(partial.Variants.Single(v => v.AssumedOptions == 1)) && partial.Variants.Single(v => v.AssumedOptions == 129).IsAvailable);
        Check("a local refusal exposes a typed issue but no population controls sources counts or chunks", partial.Variants.Count == 2
            && partial.Variants.Single(v => v.AssumedOptions == 1).Issues.Any(i => i.Code == NativeImageBuildIssueCode.UnsupportedMapping)
            && NoAuthority(partial) && Xml(partialFixture.Project) == partialXml && Hash(File.ReadAllBytes(partialFixture.Path)) == partialBytes);
        var broken = NativeGeneralImageBuildChecks.MixedApplication();
        NativeGeneralPopulationChecks.Stat(broken).Element(Ns + "Parameters")!.Elements().Last().Add(new XElement(Ns + "Memory",
            new XAttribute("CodeSegment", "missing"), new XAttribute("Offset", 0), new XAttribute("BitOffset", 0)));
        Check("a malformed global owner yields no alternative payload or binding", Empty(Preview(NativeGeneralImageBuildChecks.Mixed(directory, broken), [1, 129])));

        var tableFixture = NativeGeneralImageBuildChecks.TableFixture(directory);
        var tableXml = Xml(tableFixture.Project); var tableBytes = Hash(File.ReadAllBytes(tableFixture.Path));
        var tables = Preview(tableFixture, [131], GeneratedTableFormat.M112);
        Check("public tables consume current parameter-written bytes: " + Explain(tables), tables.IsAvailable && tables.Variants.Single().Tables is { Payloads.Count: 3 });
        if (tables.IsAvailable && tables.Variants.Single().Tables is { } emission)
        {
            var variant = tables.Variants.Single(); var ga = emission.Payloads.Single(p => p.Role == GeneratedTableRole.GroupAddress);
            var assoc = emission.Payloads.Single(p => p.Role == GeneratedTableRole.Association);
            var ko = emission.Payloads.Single(p => p.Role == GeneratedTableRole.CommunicationObject);
            Check("GA whole payload retains current CAFE rather than source BEEF", ga.Data.ToHexString() == "04CAFE01010A030B01"
                && ga.BeforeWorkingData.ToHexString() == "99CAFEAAAAAAAAAAAA" && ga.WriteMask.ToHexString() == "FF0000FFFFFFFFFFFF");
            Check("GA before and after validity represent current writer state", ga.BeforeValidity.ToHexString() == "5AFFFF5A5A5A5A5A5A"
                && ga.AfterValidity.ToHexString() == "FFFFFFFFFFFFFFFFFF");
            Check("full GA backing retains the untouched tail bytes and validity", Memory(variant, 0x4000).WorkingData.ToHexString() == "04CAFE01010A030B01AAAAAAAAAAAAAA"
                && Memory(variant, 0x4000).DesiredValidity?.ToHexString() == "FFFFFFFFFFFFFFFFFF5A5A5A5A5A5A5A");
            Check("association whole payload is independently sending-first", assoc.Data.ToHexString() == "0501000201030301010203"
                && assoc.WriteMask.ToHexString() == new string('F', 22)
                && assoc.BeforeWorkingData.ToHexString() == new string('A', 22) && assoc.BeforeValidity.ToHexString() == new string('F', 22));
            var expectedObject = new byte[32]; expectedObject[0] = 5; expectedObject[5] = 0x4F; expectedObject[9] = 0x1B; expectedObject[10] = 7;
            expectedObject[17] = 0x5F; expectedObject[18] = 8; expectedObject[22] = 10;
            var objectMask = new byte[32]; Array.Fill(objectMask, (byte)255, 0, 23);
            Check("full object backing follows current zero-mask seed including every hole and tail", Bytes(Memory(variant, 0x4400).WorkingData, expectedObject)
                && Bytes(Memory(variant, 0x4400).DesiredValidity, objectMask)
                && ko.Data.ToArray().SequenceEqual(expectedObject.Take(23)) && ko.BeforeWorkingData.ToArray().All(b => b == 0));
            Check("public table metadata has its own seed and witness hashes without an old replay binding", emission.Fingerprint.Length == 64
                && emission.CurrentSeedHash.Length == 64 && emission.NativeWitnessHash.Length == 64
                && typeof(NativeImageBuildTables).GetProperty("Binding") is null && emission.Format == GeneratedTableFormat.M112
                && emission.Requirements.Contains("TableUnusedTailMaskUnresolved"));
            Check("all public table collections and complete byte buffers are immutable", ReadOnly(emission.Payloads) && ReadOnly(emission.ProcedureAdjustments)
                && ReadOnly(emission.Requirements) && emission.Payloads.All(p => CopyProtected(p.Data) && CopyProtected(p.WriteMask)
                    && CopyProtected(p.BeforeWorkingData) && CopyProtected(p.BeforeValidity) && CopyProtected(p.AfterValidity)));
            var binding = tables.Binding!; var obj = tableFixture.Device.ComObjectRefs[0]; var oldLinks = obj.Links.ToArray();
            obj.Links.Clear();
            Check("table-link changes stale the combined public binding", !binding.IsCurrent(tableFixture.Project, tableFixture.Device));
            obj.Links.AddRange(oldLinks);
            Check("restoring exact table links restores the combined public binding", binding.IsCurrent(tableFixture.Project, tableFixture.Device));
        }
        Check("tables preserve source model and archive", Xml(tableFixture.Project) == tableXml && Hash(File.ReadAllBytes(tableFixture.Path)) == tableBytes && NoAuthority(tables));
        var collision = Preview(NativeGeneralImageBuildChecks.TableFixture(directory, overlaps: true), [131], GeneratedTableFormat.M112);
        Check("a table-write collision yields no partial public payload or binding", !collision.IsAvailable && collision.Binding is null
            && collision.Variants.Count == 1 && Empty(collision.Variants[0]) && NoAuthority(collision));

        var identity = NativeGeneralImageBuildChecks.Mixed(directory); var identityPlan = Preview(identity);
        var identityUndo = new UndoStack(); identityUndo.Reset(identity.Project); identity.Project.Name = "New identity snapshot"; identityUndo.Push(identity.Project);
        var restored = identityUndo.Undo()!; var restoredDevice = ProjectQuery.DeviceList(restored).Single();
        Check("undo replacement with identical ids requires its own new public binding", identityPlan.Binding is not null
            && !identityPlan.Binding.IsCurrent(restored, restoredDevice)
            && Preview(new(restored, restoredDevice, identity.Path, identity.MasterBytes)).IsAvailable && identityUndo.CanRedo);
        var originalGuid = identity.Project.Guid;
        identity.Project.Guid = new string('x', 8 * 1024 * 1024);
        try
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var rejectedIdentity = Preview(identity);
            var identityAllocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Check("oversized identity is refused before source capture or hashing", Empty(rejectedIdentity, NativeImageBuildIssueCode.InvalidRequest));
            Check("oversized identity does not allocate a serialized eight-MiB hash operand", identityAllocation < 1024 * 1024);
        }
        finally { identity.Project.Guid = originalGuid; }
        Console.WriteLine($"NativeImageBuildPublic: {checks} checks, {failures} failures");
        return failures;
    }
}
