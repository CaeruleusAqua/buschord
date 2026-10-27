using System.IO.Compression;
using System.Xml.Linq;
using Knx;

internal static class FloatParameterPatchChecks
{
    private const string AppId = "M-0001_A-0100-10-0000";
    private static readonly XNamespace Ns = "http://knx.org/xml/project/23";
    private static XDocument App(string minimum = "-3.4028234663852886e38", string maximum = "3.4028234663852886e38",
        int bit = 0, byte[]? data = null, byte[]? mask = null) => XDocument.Parse($$"""
        <KNX xmlns="{{Ns}}"><ApplicationProgram Id="{{AppId}}"><Static><Code><AbsoluteSegment Id="AS" Address="4096" Size="{{data?.Length ?? 8}}">
        <Data>{{Convert.ToBase64String(data ?? new byte[8])}}</Data>{{(mask is null ? "" : "<Mask>" + Convert.ToBase64String(mask) + "</Mask>")}}
        </AbsoluteSegment></Code><ParameterTypes><ParameterType Id="T"><TypeFloat Encoding="IEEE-754 Single" minInclusive="{{minimum}}" maxInclusive="{{maximum}}"/></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P" ParameterType="T" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="{{bit}}"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="R" RefId="P"/></ParameterRefs></Static><Dynamic><ParameterRefRef RefId="R"/></Dynamic></ApplicationProgram></KNX>
        """);
    private static (Project Project, DeviceInstance Device) Fixture(string directory, XDocument app)
    {
        var path = Path.Combine(directory, "float-patch-" + Guid.NewGuid().ToString("N") + ".knxproj");
        var project = ProjectWizard.Create(new() { Name = "Explicit IEEE Single subset", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", ProductRefId = AppId, Address = new(1, 1, 1) });
        Knxproj.Save(project, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) using (var stream = zip.CreateEntry("M-0001/" + AppId + ".xml").Open()) app.Save(stream);
        var loaded = Knxproj.Load(path); return (loaded, ProjectQuery.DeviceList(loaded).Single());
    }
    private static void AddIntegerFields(XDocument app)
    {
        var stat = app.Descendants(Ns + "Static").Single();
        stat.Element(Ns + "ParameterTypes")!.Add(
            new XElement(Ns + "ParameterType", new XAttribute("Id", "TN"), new XElement(Ns + "TypeNumber", new XAttribute("Type", "signedInt"), new XAttribute("SizeInBit", "8"), new XAttribute("minInclusive", "-128"), new XAttribute("maxInclusive", "127"))),
            new XElement(Ns + "ParameterType", new XAttribute("Id", "TE"), new XElement(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", "16"),
                new XElement(Ns + "Enumeration", new XAttribute("Id", "E0"), new XAttribute("Value", "0")), new XElement(Ns + "Enumeration", new XAttribute("Id", "E1"), new XAttribute("Value", "300")))));
        foreach (var (id, type, offset) in new[] { ("N", "TN", 4), ("E", "TE", 5) })
        {
            stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", id), new XAttribute("ParameterType", type), new XAttribute("Value", "0"),
                new XElement(Ns + "Memory", new XAttribute("CodeSegment", "AS"), new XAttribute("Offset", offset), new XAttribute("BitOffset", "0"))));
            stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "R" + id), new XAttribute("RefId", id)));
        }
    }
    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "FloatPatch " + name); if (!ok) failures++; }
        ParameterPatchPreview Preview(XDocument app, string raw = "0.25", ParameterPatchByteOrder order = ParameterPatchByteOrder.BigEndian)
        { var f = Fixture(directory, app); return OfflineParameterPatch.Preview(f.Project, f.Device, [new("R", raw)], order); }
        static bool Atomic(ParameterPatchPreview result, ParameterPatchIssueCode code) => !result.IsAvailable && result.Binding is null
            && result.Issues.Single().Code == code && result.Ranges.Count == 0 && result.Resources.Count == 0 && result.Fields.Count == 0;

        // Literal IEEE32 payloads, independently established before this implementation.
        // The double-rounding vector distinguishes XML Double -> Single from Single.Parse.
        var vectors = new (string Raw, string Big, string Little)[]
        {
            ("0", "00000000", "00000000"), ("-0", "80000000", "00000080"), ("-0.0", "80000000", "00000080"),
            ("0.25", "3E800000", "0000803E"), ("-2.5", "C0200000", "000020C0"), ("0.1", "3DCCCCCD", "CDCCCC3D"),
            ("1.0000000596046448", "3F800000", "0000803F"), ("1.000000178813934326171875", "3F800002", "0200803F"),
            ("16777217", "4B800000", "0000804B"), ("16777219", "4B800002", "0200804B"),
            ("1.401298464324817e-45", "00000001", "01000000"), ("1.1754943508222875e-38", "00800000", "00008000"),
            ("3.4028234663852886e38", "7F7FFFFF", "FFFF7F7F"), ("-3.4028234663852886e38", "FF7FFFFF", "FFFF7FFF"),
        };
        foreach (var vector in vectors)
        {
            var be = Preview(App(), vector.Raw); var le = Preview(App(), vector.Raw, ParameterPatchByteOrder.LittleEndian);
            Check("literal " + vector.Raw, be.IsAvailable && le.IsAvailable && be.Ranges.Single().AfterData.ToHexString() == vector.Big
                && le.Ranges.Single().AfterData.ToHexString() == vector.Little && be.Ranges.Single().FieldMask.ToHexString() == "FFFFFFFF"
                && be.Ranges.Single().AfterMask is null && be.Fields.Single().RawValue == vector.Raw);
        }
        var display = App("0", "10"); display.Descendants(Ns + "TypeFloat").Single().SetAttributeValue("DisplayFormat", "0.000");
        var result = Preview(display);
        Check("bounded DisplayFormat remains inert for public tariff shape", result.IsAvailable && result.Ranges.Single().AfterData.ToHexString() == "3E800000"
            && result.Binding is { CodecProfileVersion: 3, NumericParseProfile: "ExactIntegerAndDotNet10XmlDoubleToSingle", PackedTimeEncodingProfile: null });
        Check("inclusive finite limits", Preview(App("0", "10"), "0").IsAvailable && Preview(App("0", "10"), "10").IsAvailable
            && Atomic(Preview(App("0", "10"), "10.0001"), ParameterPatchIssueCode.InvalidValue));
        var exactBound = App("0.1", "0.1"); exactBound.Descendants(Ns + "Parameter").Single().SetAttributeValue("Value", "0.1");
        var exactResult = Preview(exactBound, "0.1");
        Check("Double inclusive bounds are checked before Single narrowing", exactResult.IsAvailable
            && exactResult.Ranges.Single().AfterData.ToHexString() == "3DCCCCCD");
        foreach (var raw in new[] { "NaN", "INF", "-INF", "1e400", "-1e400", "3.4028236e38", "1,25", "not a number" })
            Check("nonfinite overflow or malformed value " + raw, Atomic(Preview(App("-1e300", "1e300"), raw), ParameterPatchIssueCode.InvalidValue));
        foreach (var bounds in new (string? Min, string? Max)[] { (null, "10"), ("0", null), ("NaN", "10"), ("0", "INF"), ("2", "1"), ("x", "10"), ("0", new string('1', 65)) })
        {
            var doc = App(); var shape = doc.Descendants(Ns + "TypeFloat").Single(); shape.SetAttributeValue("minInclusive", bounds.Min); shape.SetAttributeValue("maxInclusive", bounds.Max);
            Check("finite required ordered bounds " + bounds.Min + "/" + bounds.Max, Atomic(Preview(doc), ParameterPatchIssueCode.InvalidDeclaration));
        }
        foreach (var attr in new[] { "Increment", "DisplayOffset", "DisplayFactor" })
        foreach (var value in new[] { "", "0", "1" })
        {
            var doc = App(); doc.Descendants(Ns + "TypeFloat").Single().SetAttributeValue(attr, value);
            Check("no implicit step or transform " + attr + "=" + value, Atomic(Preview(doc), ParameterPatchIssueCode.UnsupportedParameter));
        }
        foreach (var encoding in new[] { "IEEE-754 Double", "DPT 9", "single" })
        {
            var doc = App(); doc.Descendants(Ns + "TypeFloat").Single().SetAttributeValue("Encoding", encoding);
            Check("other encoding remains unsupported " + encoding, Atomic(Preview(doc), ParameterPatchIssueCode.UnsupportedParameter));
        }
        foreach (var order in Enum.GetValues<ParameterPatchByteOrder>())
            Check("unaligned Single rejected " + order, Atomic(Preview(App(bit: 1), order: order), ParameterPatchIssueCode.UnsupportedLayout));
        var mutations = new (string Name, Action<XDocument> Mutate, ParameterPatchIssueCode Code)[]
        {
            ("foreign Float", d => d.Descendants(Ns + "TypeFloat").Single().Name = XNamespace.Get("urn:foreign") + "TypeFloat", ParameterPatchIssueCode.InvalidDeclaration),
            ("native width override", d => d.Descendants(Ns + "TypeFloat").Single().SetAttributeValue("SizeInBit", "32"), ParameterPatchIssueCode.InvalidDeclaration),
            ("unknown metadata", d => d.Descendants(Ns + "TypeFloat").Single().SetAttributeValue("Precision", "2"), ParameterPatchIssueCode.InvalidDeclaration),
            ("child payload", d => d.Descendants(Ns + "TypeFloat").Single().Add(new XElement(Ns + "Vendor")), ParameterPatchIssueCode.InvalidDeclaration),
            ("shape text", d => d.Descendants(Ns + "TypeFloat").Single().Add("not metadata"), ParameterPatchIssueCode.InvalidDeclaration),
            ("oversized display format", d => d.Descendants(Ns + "TypeFloat").Single().SetAttributeValue("DisplayFormat", new string('0', 129)), ParameterPatchIssueCode.InvalidDeclaration),
            ("wrong native UIHint", d => d.Descendants(Ns + "TypeFloat").Single().SetAttributeValue("UIHint", "DropDown"), ParameterPatchIssueCode.InvalidDeclaration),
            ("alias", d => d.Descendants(Ns + "ParameterRefs").Single().Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "Alias"), new XAttribute("RefId", "P"))), ParameterPatchIssueCode.AmbiguousOverlap),
            ("distinct unreferenced owner", d => d.Descendants(Ns + "Parameters").Single().Add(new XElement(Ns + "Parameter", new XAttribute("Id", "Other"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "0"),
                new XElement(Ns + "Memory", new XAttribute("CodeSegment", "AS"), new XAttribute("Offset", "2"), new XAttribute("BitOffset", "0")))), ParameterPatchIssueCode.AmbiguousOverlap),
            ("assigned selected value", d => d.Descendants(Ns + "Dynamic").Single().Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "R"), new XAttribute("Value", "0.5"))), ParameterPatchIssueCode.UnsupportedDependency),
            ("short mask", d => d.Descendants(Ns + "AbsoluteSegment").Single().Add(new XElement(Ns + "Mask", "AAAAAA==")), ParameterPatchIssueCode.InvalidInitialMask),
            ("malformed mask", d => d.Descendants(Ns + "AbsoluteSegment").Single().Add(new XElement(Ns + "Mask", "invalid??")), ParameterPatchIssueCode.InvalidInitialMask),
            ("missing initial data", d => d.Descendants(Ns + "Data").Single().Remove(), ParameterPatchIssueCode.UnknownInitialData),
        };
        foreach (var mutation in mutations)
        { var doc = App(); mutation.Mutate(doc); Check("atomic " + mutation.Name, Atomic(Preview(doc), mutation.Code)); }
        var calculation = App(); calculation.Descendants(Ns + "Parameters").Single().Add(new XElement(Ns + "Parameter", new XAttribute("Id", "Other"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "0")));
        calculation.Descendants(Ns + "ParameterRefs").Single().Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "OtherRef"), new XAttribute("RefId", "Other")));
        calculation.Descendants(Ns + "Static").Single().Add(new XElement(Ns + "ParameterCalculations", XElement.Parse("""
        <ParameterCalculation xmlns="http://knx.org/xml/project/23" Language="JavaScript"><LRTransformation>y=x;</LRTransformation>
        <LParameters><ParameterRefRef RefId="R" AliasName="x"/></LParameters><RParameters><ParameterRefRef RefId="OtherRef" AliasName="y"/></RParameters></ParameterCalculation>
        """)));
        Check("selected static calculation rejected without script execution", Atomic(Preview(calculation), ParameterPatchIssueCode.UnsupportedDependency));
        var mixed = App(data: [0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x5A, 0xA5, 0x7B], mask: [0x80, 0, 0, 0, 0, 0, 0, 0x08]); AddIntegerFields(mixed);
        var fixture = Fixture(directory, mixed); var originalXml = Knxproj.ToXml(fixture.Project).ToString();
        var forward = OfflineParameterPatch.Preview(fixture.Project, fixture.Device, [new("R", "0.25"), new("RN", "-2"), new("RE", "300")], ParameterPatchByteOrder.BigEndian);
        var reverse = OfflineParameterPatch.Preview(fixture.Project, fixture.Device, [new("RE", "300"), new("RN", "-2"), new("R", "0.25")], ParameterPatchByteOrder.BigEndian);
        Check("Single Number and sparse Enum commute with shared Data and Mask", forward.IsAvailable && reverse.IsAvailable
            && forward.Ranges.Single().AfterData.ToHexString() == "3E800000FE012C" && reverse.Ranges.Single().AfterData.ToHexString() == "3E800000FE012C"
            && forward.Ranges.Single().AfterMask?.ToHexString() == "FFFFFFFFFFFFFF" && forward.Binding!.Fingerprint == reverse.Binding!.Fingerprint);
        Check("preview and source tails are immutable and never download ready", forward.IsAvailable && Knxproj.ToXml(fixture.Project).ToString() == originalXml
            && fixture.Device.ParameterValues.Count == 0 && forward.Resources.Single().Data.ToHexString() == "A55AA55AA55AA57B"
            && forward.Resources.Single().Mask!.ToHexString() == "8000000000000008" && !forward.CompleteApplicationImage && !forward.DownloadEligible);
        var integerOnly = OfflineParameterPatch.Preview(fixture.Project, fixture.Device, [new("RN", "-2"), new("RE", "300")], ParameterPatchByteOrder.BigEndian);
        Check("Number Enum only use exact integer profile version3", integerOnly.IsAvailable && integerOnly.Binding is { CodecProfileVersion: 3, NumericParseProfile: "ExactInteger", PackedTimeEncodingProfile: null });
        var mixedBad = OfflineParameterPatch.Preview(fixture.Project, fixture.Device, [new("RN", "-2"), new("R", "NaN")], ParameterPatchByteOrder.BigEndian);
        Check("invalid Single never publishes earlier valid integer bytes", Atomic(mixedBad, ParameterPatchIssueCode.InvalidValue));
        mixed.Descendants(Ns + "TypeFloat").Single().SetAttributeValue("Increment", "0.01");
        var unrelated = Fixture(directory, mixed);
        Check("unrelated editing Float step does not gate exact integer request", OfflineParameterPatch.Preview(unrelated.Project, unrelated.Device, [new("RN", "-2")], ParameterPatchByteOrder.BigEndian).IsAvailable);
        fixture.Device.ParameterValues["R"] = "0.5";
        Check("source changes stale the Single binding without applying preview", forward.Binding is not null && !forward.Binding.IsCurrent(fixture.Project, fixture.Device));
        var normal = Ksxml.Parse(App()).Instances.Single();
        Check("normal Float editing and32-bit footprint remain unchanged", ParameterValidation.TryValidate(normal, "0.1", out _)
            && normal.Storage.Footprint is { Status: ParameterFootprintStatus.Known, BitLength: 32 });
        return failures;
    }
}
