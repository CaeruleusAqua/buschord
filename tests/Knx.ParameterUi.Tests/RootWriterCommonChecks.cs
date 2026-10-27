using System.Collections;
using System.Reflection;
using System.Xml.Linq;
using Knx;

internal static class RootWriterCommonChecks
{
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Common = typeof(Project).Assembly.GetType("Knx.RootParameterWriterCommon", true)!;
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private static object? Call(string method, params object?[] arguments) => Common.GetMethod(method, Hidden)!.Invoke(null, arguments);
    private static RootImageBuildIssue? Failure(Action action)
    {
        try { action(); return null; }
        catch (TargetInvocationException failure) when (failure.InnerException?.GetType().FullName == "Knx.RootBuildRejected")
        { return (RootImageBuildIssue)failure.InnerException.GetType().GetProperty("Issue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(failure.InnerException)!; }
    }
    private static RootImageBuildPolicy Policy(string? version, XElement? options, string id = "M-0001_A-0001-01-0000")
        => (RootImageBuildPolicy)Call("ReadPolicy", new XElement(Ns + "ApplicationProgram", version is null ? null : new XAttribute("MinEtsVersion", version)), options, id)!;
    private static object Descriptor(ParameterStorageLocation? location)
    {
        var declaration = new XElement(Ns + "ParameterType", new XAttribute("Id", "T"), new XElement(Ns + "TypeNumber",
            new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", 8), new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 255)));
        var type = typeof(Project).Assembly.GetType("Knx.RootParameterSource", true)!;
        return type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single(c => c.GetParameters().Length == 9).Invoke(
            [new XElement(Ns + "Parameter", new XAttribute("Id", "P")), new XElement(Ns + "ParameterRef", new XAttribute("Id", "R")),
                declaration, Ksxml.ParseParameterType(declaration)!, location, "1", "2", false, false]);
    }
    private static object SourceMap(string reference, object source)
    {
        var map = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), source.GetType()))!;
        map.Add(reference, source); return map;
    }

    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "RootWriterCommon " + name); if (!ok) failures++; }
        var options = new XElement(Ns + "Options");
        var modern = Policy("6.0", options);

        var ids = new Dictionary<string, string>(StringComparer.Ordinal) { ["A_R"] = "A_R" };
        Check("short key resolves to an existing full reference", (string)Call("Normalize", ids, "R", "A")! == "A_R");
        Check("full key resolves to an existing short reference", (string)Call("Normalize", new Dictionary<string, string> { ["R"] = "R" }, "A_R", "A")! == "R");
        Check("an exact key wins over prefix normalization", (string)Call("Normalize", new Dictionary<string, string> { ["R"] = "R", ["A_R"] = "A_R" }, "R", "A")! == "R");
        Check("unknown reference retains native-carrier refusal", Failure(() => Call("Normalize", ids, "missing", "A"))?.Code == RootImageBuildIssueCode.UnsupportedNativeCarrier);

        var property = new ParameterStorageLocation(ParameterStorageStatus.Supported, ParameterStorageKind.PropertyByIndex,
            StartAddress: 2, BitOffset: 0, ObjectIndex: 5, PropertyId: 10,
            Footprint: new(ParameterFootprintStatus.Known, 8, 1));
        var sources = SourceMap("A_R", Descriptor(property));
        var device = new DeviceInstance { Id = "D", ProductRefId = "A" };
        device.ParameterValues["R"] = "03";
        var saved = (IReadOnlyDictionary<string, string>)Call("ReadSaved", ids, sources, device, "A", modern)!;
        Check("validated property descriptors reuse saved-value validation without a memory arena", saved.Count == 1 && saved["A_R"] == "03"
            && property.CodeSegmentId is null && property.Resource is null);
        device.ParameterValues["R"] = "4";
        Check("normalized saved result owns an independent immutable snapshot", saved["A_R"] == "03"
            && ((IDictionary<string, string>)saved).IsReadOnly);
        device.ParameterValues["A_R"] = "4";
        Check("equal normalized duplicates remain accepted", ((IReadOnlyDictionary<string, string>)Call("ReadSaved", ids, sources, device, "A", modern)!).Count == 1);
        device.ParameterValues["R"] = "03"; device.ParameterValues["A_R"] = "3";
        var duplicate = Failure(() => Call("ReadSaved", ids, sources, device, "A", modern));
        Check("lexically different duplicates remain invalid despite equal encoded words", duplicate?.Code == RootImageBuildIssueCode.InvalidValue && duplicate.RefId == "A_R");
        device.ParameterValues.Clear(); device.ParameterValues["R"] = "256";
        var invalid = Failure(() => Call("ReadSaved", ids, sources, device, "A", modern));
        Check("saved values retain range issue and reference attribution", invalid?.Code == RootImageBuildIssueCode.InvalidValue && invalid.RefId == "A_R"
            && invalid.ParameterIssue is not null);
        device.ParameterValues.Clear(); device.ParameterValues["unknown"] = "256";
        Check("reference normalization still precedes numeric validation", Failure(() => Call("ReadSaved", ids, sources, device, "A", modern))?.Code == RootImageBuildIssueCode.UnsupportedNativeCarrier);
        device.ParameterValues.Clear(); device.ParameterValues["R"] = "long nonphysical value";
        Check("NoStorage saved values are not run through the physical word codec", ((IReadOnlyDictionary<string, string>)Call("ReadSaved", ids,
            SourceMap("A_R", Descriptor(null)), device, "A", modern)!)["A_R"] == "long nonphysical value");

        bool Matches(Dictionary<string, string?> captured) => (bool)Call("MatchesSaved", ids, saved, captured, "A")!;
        Check("captured short keys pair with the exact raw source value", Matches(new() { ["R"] = "03" }));
        Check("word-equivalent raw changes do not pair", !Matches(new() { ["R"] = "3" }));
        Check("missing and null captured entries do not pair", !Matches(new()) && !Matches(new() { ["R"] = null }));
        Check("equal captured aliases pair but conflicting aliases do not", Matches(new() { ["R"] = "03", ["A_R"] = "03" })
            && !Matches(new() { ["R"] = "03", ["A_R"] = "3" }));
        Check("unknown and oversized captured inputs fail closed", !Matches(new() { ["missing"] = "03" })
            && !Matches(new() { ["R"] = new string('1', 65537) }));

        var fixture = ArchivedImageSourceChecks.Load(directory);
        var original = (XDocument)typeof(Project).GetProperty("OriginalXml", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Project)!;
        var native = original.Descendants().Single(e => e.Name.LocalName == "DeviceInstance"); var ns = native.Name.Namespace;
        native.Add(new XElement(ns + "ParameterInstanceRefs", new XElement(ns + "ParameterInstanceRef", new XAttribute("RefId", "R"), new XAttribute("Value", "03"))));
        var carrierIds = new Dictionary<string, string>(StringComparer.Ordinal) { ["R"] = "R" };
        string Capture() => (string)Call("CaptureCarriers", carrierIds, fixture.Project, fixture.Device, "A")!;
        var modelBefore = Knxproj.ToXml(fixture.Project).ToString(); var xmlBefore = original.ToString();
        var carrierHash = Capture();
        Check("carrier capture is stable and does not mutate either model or source", Capture() == carrierHash
            && modelBefore == Knxproj.ToXml(fixture.Project).ToString() && xmlBefore == original.ToString());
        fixture.Device.ParameterValues["R"] = "3";
        Check("saved values remain a separate binding fact from native carriers", Capture() == carrierHash);
        native.Element(ns + "ParameterInstanceRefs")!.Element(ns + "ParameterInstanceRef")!.SetAttributeValue("Value", "3");
        Check("carrier hash retains the exact raw native value", Capture() != carrierHash);
        native.Add(new XElement(ns + "ParameterValues"));
        Check("unknown retained cache carriers remain refused", Failure(() => Capture())?.Code == RootImageBuildIssueCode.UnsupportedNativeCarrier);
        native.Element(ns + "ParameterValues")!.Remove();
        native.Name = XName.Get("DeviceInstance", "urn:foreign");
        Check("a self-namespaced device cannot authorize native carriers", Failure(() => Capture())?.Code == RootImageBuildIssueCode.UnsupportedNativeCarrier);
        Console.WriteLine($"RootWriterCommon checks: {checks}, failures: {failures}");
        return failures;
    }
}
