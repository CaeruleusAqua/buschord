using System.IO.Compression;
using System.Xml.Linq;
using Knx;

internal static class PackedTimeParameterPatchChecks
{
    private const string AppId = "M-0001_A-0100-10-0000";
    private static readonly XNamespace Ns = "http://knx.org/xml/project/23";
    private static XDocument App(string minimum = "0", string maximum = "691199", byte[]? data = null, byte[]? mask = null) => XDocument.Parse($$"""
        <KNX xmlns="{{Ns}}"><ApplicationProgram Id="{{AppId}}"><Static><Code><AbsoluteSegment Id="AS" Address="4096" Size="{{data?.Length ?? 8}}">
        <Data>{{Convert.ToBase64String(data ?? new byte[8])}}</Data>{{(mask is null ? "" : "<Mask>" + Convert.ToBase64String(mask) + "</Mask>")}}
        </AbsoluteSegment></Code><ParameterTypes><ParameterType Id="T"><TypeTime SizeInBit="24" Unit="PackedDaysHoursMinutesAndSeconds" minInclusive="{{minimum}}" maxInclusive="{{maximum}}" UIHint="Time_hhmmss"/></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P" ParameterType="T" Value="{{minimum}}"><Memory CodeSegment="AS" Offset="0" BitOffset="0"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="R" RefId="P"/></ParameterRefs></Static><Dynamic><ParameterRefRef RefId="R"/></Dynamic></ApplicationProgram></KNX>
        """);
    private static (Project Project, DeviceInstance Device) Fixture(string directory, XDocument app)
    {
        var path = Path.Combine(directory, "packed-time-patch-" + Guid.NewGuid().ToString("N") + ".knxproj");
        var project = ProjectWizard.Create(new() { Name = "Explicit packed Time seconds subset", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", ProductRefId = AppId, Address = new(1, 1, 1) });
        Knxproj.Save(project, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) using (var stream = zip.CreateEntry("M-0001/" + AppId + ".xml").Open()) app.Save(stream);
        var loaded = Knxproj.Load(path); return (loaded, ProjectQuery.DeviceList(loaded).Single());
    }
    private static void AddMixedFields(XDocument app)
    {
        var stat = app.Descendants(Ns + "Static").Single();
        stat.Element(Ns + "ParameterTypes")!.Add(
            new XElement(Ns + "ParameterType", new XAttribute("Id", "TN"), new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", "3"), new XAttribute("minInclusive", "0"), new XAttribute("maxInclusive", "7"))),
            new XElement(Ns + "ParameterType", new XAttribute("Id", "TE"), new XElement(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", "3"),
                new XElement(Ns + "Enumeration", new XAttribute("Id", "E0"), new XAttribute("Value", "0")), new XElement(Ns + "Enumeration", new XAttribute("Id", "E6"), new XAttribute("Value", "6")))),
            new XElement(Ns + "ParameterType", new XAttribute("Id", "TF"), new XElement(Ns + "TypeFloat", new XAttribute("Encoding", "IEEE-754 Single"), new XAttribute("minInclusive", "0"), new XAttribute("maxInclusive", "10"))));
        foreach (var (id, type, offset, bit) in new[] { ("N", "TN", 3, 0), ("E", "TE", 3, 4), ("F", "TF", 4, 0) })
        {
            stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", id), new XAttribute("ParameterType", type), new XAttribute("Value", "0"),
                new XElement(Ns + "Memory", new XAttribute("CodeSegment", "AS"), new XAttribute("Offset", offset), new XAttribute("BitOffset", bit))));
            stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "R" + id), new XAttribute("RefId", id)));
        }
    }
    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "PackedTimePatch " + name); if (!ok) failures++; }
        ParameterPatchPreview Preview(XDocument app, string raw = "90061", ParameterPatchByteOrder order = ParameterPatchByteOrder.BigEndian)
        { var f = Fixture(directory, app); return OfflineParameterPatch.Preview(f.Project, f.Device, [new("R", raw)], order); }
        static bool Atomic(ParameterPatchPreview p, ParameterPatchIssueCode code) => !p.IsAvailable && p.Binding is null && p.Issues.Single().Code == code
            && p.Ranges.Count == 0 && p.Resources.Count == 0 && p.Fields.Count == 0;
        // Literal vectors for the fixed packed byte layout.
        foreach (var (raw, bytes) in new[] { ("0", "000000"), ("1", "010000"), ("59", "3B0000"), ("60", "000100"), ("3599", "3B3B00"),
            ("3600", "000001"), ("86399", "3B3B17"), ("86400", "000020"), ("90061", "010121"), ("604800", "0000E0"), ("691199", "3B3BF7") })
        {
            var p = Preview(App(), raw);
            Check("raw seconds " + raw + " -> " + bytes, p.IsAvailable && p.Ranges.Single().AfterData.ToHexString() == bytes
                && p.Ranges.Single().FieldMask.ToHexString() == "FFFFFF" && p.Ranges.Single().AfterMask is null && p.Fields.Single().RawValue == raw);
        }
        var supported = Preview(App());
        Check("packed bytes differ from numeric raw seconds and bind exact encoding", supported.IsAvailable && supported.Ranges.Single().AfterData.ToHexString() != "015FCD"
            && supported.Binding is { CodecProfileVersion: 3, NumericParseProfile: "ExactInteger", PackedTimeEncodingProfile: "PackedDaysHoursMinutesAndSeconds24BigEndianOnly" });
        var narrowed = Preview(App("90061", "90061"));
        Check("required raw-second limits are inclusive before packing", narrowed.IsAvailable && narrowed.Ranges.Single().AfterData.ToHexString() == "010121"
            && Atomic(Preview(App("90060", "90061"), "90059"), ParameterPatchIssueCode.InvalidValue));
        foreach (var raw in new[] { "-1", "691200", "9223372036854775807", "9223372036854775808", "-9223372036854775808", "1.5", "1e3", "PT1H", "01:01:01", "90061s", "NaN" })
            Check("no scale wrap or formatted-duration parse " + raw, Atomic(Preview(App(), raw), ParameterPatchIssueCode.InvalidValue));
        foreach (var unit in new[] { "Hours", "Minutes", "Seconds", "HundredMilliseconds", "TenMilliseconds", "Milliseconds", "PackedSecondsAndMilliseconds", "PackedMinutesSecondsAndMilliseconds", "Unknown" })
        {
            var d = App(); d.Descendants(Ns + "TypeTime").Single().SetAttributeValue("Unit", unit);
            if (unit is "Hours" or "Minutes" or "Seconds" or "HundredMilliseconds" or "TenMilliseconds" or "Milliseconds")
            {
                var dense = Preview(d);
                Check("ordinary Time retains raw counts instead of packed bytes " + unit, dense.IsAvailable && dense.Ranges.Single().AfterData.ToHexString() == "015FCD"
                    && dense.Ranges.Single().FieldMask.ToHexString() == "FFFFFF" && dense.Binding is { CodecProfileVersion: 4,
                        DenseTimeEncodingProfile: "SignedRawOrdinaryTime8To32", PackedTimeEncodingProfile: null });
            }
            else Check("other Time unit is not a numeric shortcut " + unit, Atomic(Preview(d), ParameterPatchIssueCode.UnsupportedParameter));
        }
        foreach (var width in new[] { "8", "16", "23", "25", "32", "64", "not-width" })
        {
            var d = App(); d.Descendants(Ns + "TypeTime").Single().SetAttributeValue("SizeInBit", width);
            Check("exact native packed width " + width, Atomic(Preview(d), ParameterPatchIssueCode.UnsupportedParameter));
        }
        foreach (var (min, max) in new (string? Min, string? Max)[] { (null, "691199"), ("0", null), ("-1", "691199"), ("0", "691200"), ("2", "1"),
            ("0.0", "691199"), ("0", "9223372036854775808"), ("0", "INF") })
        {
            var d = App(); var t = d.Descendants(Ns + "TypeTime").Single(); t.SetAttributeValue("minInclusive", min); t.SetAttributeValue("maxInclusive", max);
            Check("bounded ordered Int64 declaration " + min + "/" + max, Atomic(Preview(d), ParameterPatchIssueCode.InvalidDeclaration));
        }
        Check("LittleEndian cannot reverse native fixed bytes", Atomic(Preview(App(), order: ParameterPatchByteOrder.LittleEndian), ParameterPatchIssueCode.UnsupportedLayout));
        var shifted = App(); shifted.Descendants(Ns + "Memory").Single().SetAttributeValue("BitOffset", "1");
        Check("packed byte profile is aligned only", Atomic(Preview(shifted), ParameterPatchIssueCode.UnsupportedLayout));
        var mutations = new (string Name, Action<XDocument> Mutate, ParameterPatchIssueCode Code)[]
        {
            ("foreign Time", d => d.Descendants(Ns + "TypeTime").Single().Name = XNamespace.Get("urn:foreign") + "TypeTime", ParameterPatchIssueCode.InvalidDeclaration),
            ("unknown unit conversion", d => d.Descendants(Ns + "TypeTime").Single().SetAttributeValue("DisplayFactor", "1"), ParameterPatchIssueCode.InvalidDeclaration),
            ("unknown increment", d => d.Descendants(Ns + "TypeTime").Single().SetAttributeValue("Increment", "1"), ParameterPatchIssueCode.InvalidDeclaration),
            ("unknown UI hint", d => d.Descendants(Ns + "TypeTime").Single().SetAttributeValue("UIHint", "DateTime"), ParameterPatchIssueCode.InvalidDeclaration),
            ("child payload", d => d.Descendants(Ns + "TypeTime").Single().Add(new XElement(Ns + "Vendor")), ParameterPatchIssueCode.InvalidDeclaration),
            ("shape text", d => d.Descendants(Ns + "TypeTime").Single().Add("time"), ParameterPatchIssueCode.InvalidDeclaration),
            ("alias", d => d.Descendants(Ns + "ParameterRefs").Single().Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "Alias"), new XAttribute("RefId", "P"))), ParameterPatchIssueCode.AmbiguousOverlap),
            ("distinct unreferenced owner", d => d.Descendants(Ns + "Parameters").Single().Add(new XElement(Ns + "Parameter", new XAttribute("Id", "Other"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "0"),
                new XElement(Ns + "Memory", new XAttribute("CodeSegment", "AS"), new XAttribute("Offset", "2"), new XAttribute("BitOffset", "0")))), ParameterPatchIssueCode.AmbiguousOverlap),
            ("assigned selected raw time", d => d.Descendants(Ns + "Dynamic").Single().Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "R"), new XAttribute("Value", "60"))), ParameterPatchIssueCode.UnsupportedDependency),
            ("short mask", d => d.Descendants(Ns + "AbsoluteSegment").Single().Add(new XElement(Ns + "Mask", "AAAA")), ParameterPatchIssueCode.InvalidInitialMask),
            ("malformed mask", d => d.Descendants(Ns + "AbsoluteSegment").Single().Add(new XElement(Ns + "Mask", "invalid??")), ParameterPatchIssueCode.InvalidInitialMask),
            ("missing data", d => d.Descendants(Ns + "Data").Single().Remove(), ParameterPatchIssueCode.UnknownInitialData),
        };
        foreach (var mutation in mutations) { var d = App(); mutation.Mutate(d); Check("atomic " + mutation.Name, Atomic(Preview(d), mutation.Code)); }
        var calculation = App(); calculation.Descendants(Ns + "Parameters").Single().Add(new XElement(Ns + "Parameter", new XAttribute("Id", "Other"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "0")));
        calculation.Descendants(Ns + "ParameterRefs").Single().Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "OtherRef"), new XAttribute("RefId", "Other")));
        calculation.Descendants(Ns + "Static").Single().Add(new XElement(Ns + "ParameterCalculations", XElement.Parse("""
        <ParameterCalculation xmlns="http://knx.org/xml/project/23" Language="JavaScript"><LRTransformation>y=x;</LRTransformation>
        <LParameters><ParameterRefRef RefId="R" AliasName="x"/></LParameters><RParameters><ParameterRefRef RefId="OtherRef" AliasName="y"/></RParameters></ParameterCalculation>
        """)));
        Check("selected static calculation stays unavailable", Atomic(Preview(calculation), ParameterPatchIssueCode.UnsupportedDependency));
        var noHint = App(); noHint.Descendants(Ns + "TypeTime").Single().Attribute("UIHint")!.Remove();
        Check("UIHint is optional inert display metadata", Preview(noHint).IsAvailable);
        var mixed = App(data: [0xA5, 0x5A, 0x7B, 0xA5, 0xA5, 0x5A, 0xA5, 0x5A, 0x7B], mask: [0x80, 1, 0, 0x81, 0, 0, 0, 0, 8]); AddMixedFields(mixed);
        var fixture = Fixture(directory, mixed); var original = Knxproj.ToXml(fixture.Project).ToString();
        var forward = OfflineParameterPatch.Preview(fixture.Project, fixture.Device, [new("R", "90061"), new("RN", "5"), new("RE", "6"), new("RF", "0.25")], ParameterPatchByteOrder.BigEndian);
        var reverse = OfflineParameterPatch.Preview(fixture.Project, fixture.Device, [new("RF", "0.25"), new("RE", "6"), new("RN", "5"), new("R", "90061")], ParameterPatchByteOrder.BigEndian);
        Check("all four codecs commute in one resource with disjoint packed integer bits", forward.IsAvailable && reverse.IsAvailable
            && forward.Ranges.Single().AfterData.ToHexString() == "010121AD3E800000" && reverse.Ranges.Single().AfterData.ToHexString() == "010121AD3E800000"
            && forward.Ranges.Single().FieldMask.ToHexString() == "FFFFFFEEFFFFFFFF" && forward.Ranges.Single().AfterMask?.ToHexString() == "FFFFFFEFFFFFFFFF"
            && forward.Binding!.Fingerprint == reverse.Binding!.Fingerprint);
        Check("mixed parser and native packed profiles remain separate", forward.Binding is { CodecProfileVersion: 3,
            NumericParseProfile: "ExactIntegerAndDotNet10XmlDoubleToSingle", PackedTimeEncodingProfile: "PackedDaysHoursMinutesAndSeconds24BigEndianOnly" });
        Check("original source data mask project and tail remain immutable", forward.IsAvailable && forward.Resources.Single().Data.ToHexString() == "A55A7BA5A55AA55A7B"
            && forward.Resources.Single().Mask!.ToHexString() == "800100810000000008" && Knxproj.ToXml(fixture.Project).ToString() == original
            && fixture.Device.ParameterValues.Count == 0 && !forward.DownloadEligible && !forward.CompleteApplicationImage);
        var integers = OfflineParameterPatch.Preview(fixture.Project, fixture.Device, [new("RN", "5"), new("RE", "6")], ParameterPatchByteOrder.BigEndian);
        Check("unselected packed declarations do not claim encoding capability", integers.IsAvailable
            && integers.Binding is { CodecProfileVersion: 3, NumericParseProfile: "ExactInteger", PackedTimeEncodingProfile: null });
        Check("mixed invalid raw time cannot publish earlier numeric bytes", Atomic(OfflineParameterPatch.Preview(fixture.Project, fixture.Device,
            [new("RN", "5"), new("R", "691200")], ParameterPatchByteOrder.BigEndian), ParameterPatchIssueCode.InvalidValue));
        mixed.Descendants(Ns + "TypeTime").Single().SetAttributeValue("Unit", "Seconds");
        var unrelated = Fixture(directory, mixed);
        Check("ordinary unselected Time does not gate Number-only preview", OfflineParameterPatch.Preview(unrelated.Project, unrelated.Device, [new("RN", "5")], ParameterPatchByteOrder.BigEndian).IsAvailable);
        fixture.Device.ParameterValues["R"] = "60";
        Check("changed saved raw seconds stale the packed source binding", forward.Binding is not null && !forward.Binding.IsCurrent(fixture.Project, fixture.Device));
        var ordinary = App(); ordinary.Descendants(Ns + "TypeTime").Single().SetAttributeValue("Unit", "Seconds"); ordinary.Descendants(Ns + "TypeTime").Single().SetAttributeValue("SizeInBit", "64");
        var row = Ksxml.Parse(ordinary).Instances.Single();
        Check("normal Time editing and64-bit diagnostics stay unchanged", ParameterValidation.TryValidate(row, "90061", out _)
            && row.Storage.Footprint is { Status: ParameterFootprintStatus.Known, BitLength: 64 });
        return failures;
    }
}
