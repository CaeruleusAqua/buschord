using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;

internal static class PropertyPatchChecks
{
    private static readonly XNamespace Ns = PropertySourceChecks.Ns;
    private static XElement Stat(XDocument doc) => doc.Descendants(Ns + "ApplicationProgram").Single().Element(Ns + "Static")!;
    private static PropertyPatchPlan Preview(ArchivedImageSourceChecks.Fixture f, string raw = "5", ParameterPatchByteOrder order = ParameterPatchByteOrder.BigEndian)
        => OfflinePropertyPatch.Preview(f.Project, f.Device, [new("R", raw)], order);
    private static void Place(XDocument doc, uint offset, byte bit)
    { var p = doc.Descendants(Ns + "Property").Single(); p.SetAttributeValue("Offset", offset); p.SetAttributeValue("BitOffset", bit); }
    private static void Add(XDocument doc, string id, string parameter, uint offset, byte bit, bool withReference = true, string? type = "T", string property = "ObjectIndex='3' PropertyId='10'")
    {
        Stat(doc).Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", parameter),
            type is null ? null : new XAttribute("ParameterType", type), new XAttribute("Value", "0"), XElement.Parse($"<Property xmlns='{Ns}' {property} Offset='{offset}' BitOffset='{bit}'/>")));
        if (withReference) Stat(doc).Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", id), new XAttribute("RefId", parameter)));
    }
    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "PropertyPatch " + name); if (!ok) failures++; }
        void Refused(string name, ArchivedImageSourceChecks.Fixture f, PropertyPatchIssueCode? code = null, string raw = "5", ParameterPatchByteOrder order = ParameterPatchByteOrder.BigEndian)
        {
            var result = Preview(f, raw, order);
            Check(name, !result.IsAvailable && result.Binding is null && result.Fields.Count == 0 && result.Ranges.Count == 0 && result.Images.Count == 0
                && result.Issues.Count == 1 && (code is null || result.Issues[0].Code == code));
        }
        var f = PropertySourceChecks.Load(directory); var original = Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting);
        var archive = SHA256.HashData(File.ReadAllBytes(f.Path)); var revision = f.Project.ApplicationDataRevision; var undo = new UndoStack(); undo.Reset(f.Project);
        var plan = Preview(f); var image = plan.Images.Single(); var range = plan.Ranges.Single(); var binding = plan.Binding;
        Check("literal fixed field produces exact bytes and keeps unrelated bits", plan.IsAvailable && image.BeforeData.ToHexString() == "AABBE3CC"
            && image.AfterData.ToHexString() == "AABBF7CC" && range.ByteOffsetInPropertyData == 2 && range.BeforeData.ToHexString() == "E3"
            && range.AfterData.ToHexString() == "F7" && range.FieldMask.ToHexString() == "1C");
        Check("count bytes and validity stay separate and unchanged", image.BeforeCountData.ToHexString() == "0002" && image.AfterCountData.ToHexString() == "0002"
            && image.CountMask.ToHexString() == "FFFF" && image.CountUnchanged && image.EncodedElementCount == 2 && range.BeforeMask.ToHexString() == "FF");
        Check("selector and property key agree across every artifact", plan.Fields.Single().PropertyKey == image.PropertyKey && range.PropertyKey == image.PropertyKey
            && plan.SourceIndex.Entries.Single().PropertyKey == image.PropertyKey && image.Selector == range.Selector && plan.Fields.Single().Selector == image.Selector);
        Check("capacity stays unknown on an available preview", plan.SourceIndex.Entries.Single().CapacityElements is null
            && plan.UnresolvedRequirements.Any(r => r.Code == PropertyPatchRequirementCode.PropertyCapacityUnknown));
        Check("all authority flags stay false", !plan.CompleteApplicationImage && !plan.DownloadEligible && !plan.ExecutionEligible
            && plan.UnresolvedRequirements.Any(r => r.Code == PropertyPatchRequirementCode.DeviceStateNotVerified));
        Check("strict new binding replays the same property profile", binding is { ProfileVersion: 1, NumericParseProfile: "ExactInteger", Fingerprint.Length: 64,
            FieldsHash.Length: 64, PropertyImagesHash.Length: 64 } && binding.IsCurrent(f.Project, f.Device));
        var dataCopy = image.AfterData.ToArray(); dataCopy[0] = 0; var countCopy = image.AfterCountData.ToArray(); countCopy[0] = 0;
        Check("copy-out buffers cannot mutate plan or count", image.AfterData.ToHexString() == "AABBF7CC" && image.AfterCountData.ToHexString() == "0002");
        Check("preview leaves archive model revision and undo unchanged", archive.SequenceEqual(SHA256.HashData(File.ReadAllBytes(f.Path)))
            && original == Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting) && revision == f.Project.ApplicationDataRevision && undo.Steps == 0 && !undo.IsDirty);
        f.Device.ParameterValues["unrelated"] = "1";
        Check("patch binding stales on saved input while source binding remains current", binding?.IsCurrent(f.Project, f.Device) == false
            && plan.SourceIndex.Binding?.IsCurrent(f.Project, f.Device) == true);
        f.Device.ParameterValues.Remove("unrelated");
        Check("exact original saved facts restore patch currency on the same identity", binding?.IsCurrent(f.Project, f.Device) == true);
        var budget = PropertySourceChecks.Load(directory); var budgetBinding = Preview(budget).Binding!;
        budget.Device.ParameterValues["oversized"] = new string('x', 16 * 1024 * 1024 + 1);
        Check("binding rejects newly oversized values before capture/repreview", !budgetBinding.IsCurrent(budget.Project, budget.Device));
        var budgetPreview = Preview(budget);
        Check("oversized saved values fail before source inspection", !budgetPreview.IsAvailable && budgetPreview.Issues.Single().Code == PropertyPatchIssueCode.InvalidRequest
            && budgetPreview.SourceIndex.Binding is null && budgetPreview.SourceIndex.FactoryBlobHash is null);
        Refused("sub-byte little endian is outside the layout", f, PropertyPatchIssueCode.UnsupportedLayout, order: ParameterPatchByteOrder.LittleEndian);
        foreach (var (first, native, tag, selector) in new (byte, ushort, uint, ushort)[] { (5, 4, 4, 4), (5, 5, 5, 0), (5, 6, 5, 1), (0, 0, 5, 0), (255, 255, 5, 0) })
        {
            var fixture = PropertySourceChecks.Load(directory, PropertySourceChecks.App($"ObjectIndex='{native}' PropertyId='10'"),
                PropertySourceChecks.Master(first: first, index: native), PropertySourceChecks.Frame(tag: tag, selector: selector));
            var result = Preview(fixture);
            Check("explicit FirstApp boundary preserves selector " + first + "/" + native, result.IsAvailable && result.Images.Single().Selector.Kind == (tag == 4 ? PropertySourceKind.SystemProperty : PropertySourceKind.AppProperty)
                && result.Images.Single().AfterData.ToHexString() == "AABBF7CC");
        }
        var byType = PropertySourceChecks.Load(directory, PropertySourceChecks.App("ObjectType='123' Occurrence='7' PropertyId='10'"), PropertySourceChecks.Master(type: 123), PropertySourceChecks.Frame(tag: 11, selector: 123, occurrence: 7));
        Check("exact by-type occurrence patches without index guessing", Preview(byType).IsAvailable && Preview(byType).Images.Single().Selector is
            { Kind: PropertySourceKind.ByType, ObjectType: 123, Occurrence: 7, NativeObjectIndex: null, ApplicationObjectIndex: null });
        Refused("source XML occurrence cannot wrap to a serialized byte", PropertySourceChecks.Load(directory, PropertySourceChecks.App("ObjectType='123' Occurrence='263' PropertyId='10'"),
            PropertySourceChecks.Master(type: 123), PropertySourceChecks.Frame(tag: 11, selector: 123, occurrence: 7)), PropertyPatchIssueCode.UnsupportedParameter);
        var word = PropertySourceChecks.App(bits: 16); Place(word, 0, 0);
        foreach (var (order, expected) in new[] { (ParameterPatchByteOrder.BigEndian, "1234E3CC"), (ParameterPatchByteOrder.LittleEndian, "3412E3CC") })
        {
            var p = Preview(PropertySourceChecks.Load(directory, word), "4660", order);
            Check("same integer codec and unchanged BE count under " + order, p.IsAvailable && p.Images.Single().AfterData.ToHexString() == expected && p.Images.Single().AfterCountData.ToHexString() == "0002");
        }
        var declared = new XDocument(word); Stat(declared).Add(new XElement(Ns + "Options", new XAttribute("ParameterByteOrder", "BigEndian")));
        Check("native Static Options admits matching declared byte order", Preview(PropertySourceChecks.Load(directory, declared), "4660").IsAvailable);
        Refused("native Static Options rejects conflicting little endian", PropertySourceChecks.Load(directory, declared), PropertyPatchIssueCode.UnsupportedLayout, "4660", ParameterPatchByteOrder.LittleEndian);
        var littleDeclared = new XDocument(word); Stat(littleDeclared).Add(new XElement(Ns + "Options", new XAttribute("ParameterByteOrder", "LittleEndian")));
        Check("native Static Options admits explicitly declared little endian", Preview(PropertySourceChecks.Load(directory, littleDeclared), "4660", ParameterPatchByteOrder.LittleEndian).IsAvailable);
        Refused("native Static Options rejects conflicting big endian", PropertySourceChecks.Load(directory, littleDeclared), PropertyPatchIssueCode.UnsupportedLayout, "4660");
        foreach (var mode in new[] { "duplicate", "misplaced", "malformed" })
        {
            var invalidOptions = new XDocument(declared); var option = Stat(invalidOptions).Element(Ns + "Options")!;
            if (mode == "duplicate") Stat(invalidOptions).Add(new XElement(option));
            else if (mode == "misplaced") { option.Remove(); invalidOptions.Descendants(Ns + "ApplicationProgram").Single().Add(option); }
            else option.SetAttributeValue("ParameterByteOrder", "unknown");
            Refused("invalid native Options container/value " + mode, PropertySourceChecks.Load(directory, invalidOptions), PropertyPatchIssueCode.InvalidDeclaration);
        }
        var signed = PropertySourceChecks.App(bits: 8, signed: true); Place(signed, 0, 0);
        Check("signed fixed word preserves two's complement", Preview(PropertySourceChecks.Load(directory, signed), "-1").Images.Single().AfterData.ToHexString() == "FFBBE3CC");
        var enumeration = PropertySourceChecks.App(); enumeration.Descendants(Ns + "ParameterType").Single().ReplaceNodes(new XElement(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", "3"),
            new[] { 0, 5, 7 }.Select(v => new XElement(Ns + "Enumeration", new XAttribute("Id", "E" + v), new XAttribute("Value", v), new XAttribute("Text", "Choice " + v)))));
        Check("declared sparse enum reuses exact membership and packed word", Preview(PropertySourceChecks.Load(directory, enumeration)).Images.Single().AfterData.ToHexString() == "AABBF7CC");
        foreach (var raw in new[] { "1", "05", "Choice 5", "E5" }) Refused("enum rejects undeclared/lexical/display alternative " + raw, PropertySourceChecks.Load(directory, enumeration), PropertyPatchIssueCode.InvalidValue, raw);
        var excessive = new XDocument(enumeration); excessive.Descendants(Ns + "TypeRestriction").Single().SetAttributeValue("SizeInBit", "16");
        excessive.Descendants(Ns + "TypeRestriction").Single().ReplaceNodes(Enumerable.Range(0, 4097).Select(v => new XElement(Ns + "Enumeration", new XAttribute("Id", "E" + v), new XAttribute("Value", v))));
        Refused("enum choice bound precedes typed parse", PropertySourceChecks.Load(directory, excessive), PropertyPatchIssueCode.ResourceLimitExceeded);
        foreach (var (raw, expected) in new[]
        {
            (PropertySourceChecks.Frame(omitCount:true), PropertyPatchIssueCode.UnknownElementCount),
            (PropertySourceChecks.Frame(omitDataMask:true), PropertyPatchIssueCode.InvalidInitialMask),
            (PropertySourceChecks.Frame(omitCountMask:true), PropertyPatchIssueCode.InvalidInitialMask),
            (PropertySourceChecks.Frame(dataMask:[255,255,0,255]), PropertyPatchIssueCode.InvalidInitialMask),
            (PropertySourceChecks.Frame(countMask:[0,0]), PropertyPatchIssueCode.InvalidInitialMask),
            (PropertySourceChecks.Frame(count:[1,0]), PropertyPatchIssueCode.IncompleteInitialData),
            (PropertySourceChecks.Frame(count:[0,0]), PropertyPatchIssueCode.IncompleteInitialData),
            (PropertySourceChecks.Frame(data:[1,2]), PropertyPatchIssueCode.IncompleteInitialData),
        }) Refused("before completeness/validity failure " + expected, PropertySourceChecks.Load(directory, raw: raw), expected);
        foreach (var pdt in new string?[] { null, "PDT_UTF8", "PDT_CONTROL" })
            Refused("missing/unknown/asymmetric PDT " + pdt, PropertySourceChecks.Load(directory, master: PropertySourceChecks.Master(pdt)), pdt == "PDT_CONTROL" ? PropertyPatchIssueCode.AsymmetricPdt : PropertyPatchIssueCode.UnknownPdt);
        Check("new symmetric role mapping does not require old UI width support", Preview(PropertySourceChecks.Load(directory, master: PropertySourceChecks.Master("PDT_GENERIC_02"))).IsAvailable);
        var pdtA = PropertySourceChecks.Load(directory, master: PropertySourceChecks.Master("PDT_UNSIGNED_INT"));
        var pdtB = PropertySourceChecks.Load(directory, master: PropertySourceChecks.Master("PDT_GENERIC_02"));
        var sameWidthA = Preview(pdtA); var sameWidthB = Preview(pdtB);
        Check("same-width PDT changes isolate the new binding from unchanged old sources", sameWidthA.IsAvailable && sameWidthB.IsAvailable
            && pdtA.Project.Id == pdtB.Project.Id && pdtA.Project.Guid == pdtB.Project.Guid && pdtA.Project.ApplicationDataRevision == pdtB.Project.ApplicationDataRevision
            && sameWidthA.Binding?.SourceFingerprint == sameWidthB.Binding?.SourceFingerprint
            && sameWidthA.Binding?.DefinitionHash == sameWidthB.Binding?.DefinitionHash
            && sameWidthA.SourceIndex.FactoryBlobHash == sameWidthB.SourceIndex.FactoryBlobHash
            && sameWidthA.SourceIndex.PropertyProfileSelection.Fingerprint != sameWidthB.SourceIndex.PropertyProfileSelection.Fingerprint
            && sameWidthA.SourceIndex.Fingerprint != sameWidthB.SourceIndex.Fingerprint && sameWidthA.Binding?.Fingerprint != sameWidthB.Binding?.Fingerprint);
        Check("PDT semantic rebind preserves every supplied and patched byte", sameWidthA.Images.Single().BeforeData.Sha256 == sameWidthB.Images.Single().BeforeData.Sha256
            && sameWidthA.Images.Single().AfterData.Sha256 == sameWidthB.Images.Single().AfterData.Sha256
            && sameWidthA.Images.Single().BeforeCountData.Sha256 == sameWidthB.Images.Single().BeforeCountData.Sha256
            && sameWidthA.Images.Single().AfterCountData.Sha256 == sameWidthB.Images.Single().AfterCountData.Sha256);
        var alias = PropertySourceChecks.App(); Stat(alias).Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "Alias"), new XAttribute("RefId", "P"), new XAttribute("Access", "None")));
        Refused("inactive/read-only alias is not write authority", PropertySourceChecks.Load(directory, alias), PropertyPatchIssueCode.AmbiguousOwnership);
        var misplacedRef = PropertySourceChecks.App(); misplacedRef.Descendants(Ns + "Dynamic").Single().Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "HiddenAlias"), new XAttribute("RefId", "P")));
        Refused("misplaced alias cannot disappear outside the native root ref container", PropertySourceChecks.Load(directory, misplacedRef), PropertyPatchIssueCode.InvalidDeclaration);
        var misplacedType = PropertySourceChecks.App(); misplacedType.Descendants(Ns + "Dynamic").Single().Add(new XElement(misplacedType.Descendants(Ns + "ParameterType").Single()));
        Refused("misplaced duplicate type cannot shadow the native root type", PropertySourceChecks.Load(directory, misplacedType), PropertyPatchIssueCode.InvalidDeclaration);
        var textProperty = PropertySourceChecks.App(); textProperty.Descendants(Ns + "Property").Single().Value = "not a leaf";
        Refused("selected property storage must be a native empty leaf", PropertySourceChecks.Load(directory, textProperty), PropertyPatchIssueCode.InvalidDeclaration);
        var textRef = PropertySourceChecks.App(); textRef.Descendants(Ns + "ParameterRef").Single().Value = "not a leaf";
        Refused("native reference text is not silently discarded", PropertySourceChecks.Load(directory, textRef), PropertyPatchIssueCode.InvalidDeclaration);
        foreach (var withType in new[] { true, false })
        {
            var overlap = PropertySourceChecks.App(); Add(overlap, "OtherR", "OtherP", 2, 3, false, withType ? "T" : null);
            Refused("unreferenced overlapping owner cannot disappear " + withType, PropertySourceChecks.Load(directory, overlap), PropertyPatchIssueCode.AmbiguousOwnership);
        }
        var after = PropertySourceChecks.App(); Add(after, "UnknownR", "UnknownP", 3, 0, false, null);
        Check("known later start proves an unknown positive span cannot overlap backwards", Preview(PropertySourceChecks.Load(directory, after)).IsAvailable);
        foreach (var union in new[] { false, true })
        {
            var memory = PropertySourceChecks.App();
            Stat(memory).Add(new XElement(Ns + "Code", new XElement(Ns + "RelativeSegment", new XAttribute("Id", "RS"), new XAttribute("Offset", "0"), new XAttribute("Size", "4"), new XAttribute("LoadStateMachine", "4"))));
            var decl = new XElement(Ns + "Parameter", new XAttribute("Id", "MemoryP"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "0"), new XAttribute("Access", "None"));
            var storage = new XElement(Ns + "Memory", new XAttribute("CodeSegment", "RS"), new XAttribute("Offset", "0"), new XAttribute("BitOffset", "0"));
            if (union) Stat(memory).Element(Ns + "Parameters")!.Add(new XElement(Ns + "Union", storage, decl)); else { decl.Add(storage); Stat(memory).Element(Ns + "Parameters")!.Add(decl); }
            var master = PropertySourceChecks.Master(); master.Descendants(Ns + "Features").Single().Add(new XElement(Ns + "Feature", new XAttribute("Name", "PropertyMappedLsms"), new XAttribute("Value", "1")));
            master.Descendants(Ns + "HawkConfigurationData").Single().Add(XElement.Parse($$"""
                <Resources xmlns="{{Ns}}"><Resource Name="Mapped" Access="remote"><Location AddressSpace="RelativeMemory" InterfaceObjectRef="4" StartAddress="0"/>
                <ImgLocation AddressSpace="SystemProperty" InterfaceObjectRef="3" PropertyID="10" StartAddress="0"/><ResourceType Length="4"/></Resource>
                <Resource Name="Pointer" Access="remote"><Location AddressSpace="Pointer" PtrResource="Mapped"/><ResourceType Length="4"/></Resource></Resources>
                """));
            Refused("unselected memory owner cannot be assumed disjoint " + union, PropertySourceChecks.Load(directory, memory, master), PropertyPatchIssueCode.UnsupportedPhysicalAlias);
        }
        foreach (var kind in new[] { "Module", "Repeat", "ParameterCalculation" })
        {
            var doc = PropertySourceChecks.App(); if (kind == "ParameterCalculation") Stat(doc).Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + kind)));
            else doc.Descendants(Ns + "Dynamic").Single().Add(new XElement(Ns + kind));
            Refused("unsupported structural/evaluation source " + kind, PropertySourceChecks.Load(directory, doc), kind == "ParameterCalculation" ? PropertyPatchIssueCode.UnsupportedDependency : PropertyPatchIssueCode.UnsupportedParameter);
        }
        var assign = PropertySourceChecks.App(); assign.Descendants(Ns + "Dynamic").Single().Add(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "R"), new XAttribute("Value", "1")));
        Refused("selected assignment dependency remains unsupported", PropertySourceChecks.Load(directory, assign), PropertyPatchIssueCode.UnsupportedDependency);
        foreach (var flag in new[] { "RequiresExternalSoftware", "Broken", "DownloadInfoIncomplete" })
        { var doc = PropertySourceChecks.App(); doc.Descendants(Ns + "ApplicationProgram").Single().SetAttributeValue(flag, "true"); Refused("external/incomplete state " + flag, PropertySourceChecks.Load(directory, doc), PropertyPatchIssueCode.UnsupportedApplication); }
        var plugin = PropertySourceChecks.App(); plugin.Descendants(Ns + "ParameterType").Single().SetAttributeValue("Plugin", "adapter");
        Refused("type plugin state remains unaccounted", PropertySourceChecks.Load(directory, plugin), PropertyPatchIssueCode.UnsupportedApplication);
        var binary = PropertySourceChecks.Load(directory); binary.Device.BinaryData.Add("state", [1]); Refused("device binary state is not a property before-source", binary, PropertyPatchIssueCode.UnsupportedApplication);
        var mixed = PropertySourceChecks.App(); Add(mixed, "TypedR", "TypedP", 3, 0, property: "ObjectType='123' Occurrence='0' PropertyId='10'");
        Refused("unknown index/type alias mapping cannot authorize ownership", PropertySourceChecks.Load(directory, mixed), PropertyPatchIssueCode.AmbiguousOwnership);
        var packed = PropertySourceChecks.App(bits: 2); Place(packed, 2, 0); Stat(packed).Element(Ns + "ParameterTypes")!.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "E"),
            new XElement(Ns + "TypeRestriction", new XAttribute("Base", "Value"), new XAttribute("SizeInBit", "3"), new XElement(Ns + "Enumeration", new XAttribute("Id", "E5"), new XAttribute("Value", "5")))));
        Add(packed, "R2", "P2", 2, 2, type: "E"); var packFixture = PropertySourceChecks.Load(directory, packed);
        var pairs = new[] { new ParameterPatchValue("R", "1"), new ParameterPatchValue("R2", "5") };
        var forward = OfflinePropertyPatch.Preview(packFixture.Project, packFixture.Device, pairs, ParameterPatchByteOrder.BigEndian);
        var reverse = OfflinePropertyPatch.Preview(packFixture.Project, packFixture.Device, pairs.Reverse().ToArray(), ParameterPatchByteOrder.BigEndian);
        Check("disjoint packed Number/Enum is request-order independent", forward.IsAvailable && reverse.IsAvailable && forward.Images.Single().AfterData.ToHexString() == "AABB6BCC"
            && forward.Images.Single().AfterData.Sha256 == reverse.Images.Single().AfterData.Sha256 && forward.Binding?.Fingerprint == reverse.Binding?.Fingerprint);
        var invalid = OfflinePropertyPatch.Preview(f.Project, f.Device, [new("R", "5"), new("missing", "1")], ParameterPatchByteOrder.BigEndian);
        Check("one invalid selection discards every tentative property range", !invalid.IsAvailable && invalid.Ranges.Count == 0 && invalid.Images.Count == 0 && invalid.Binding is null);
        foreach (var subset in new IReadOnlyList<ParameterPatchValue>[] { [], [new("R", "1"), new("R", "2")], Enumerable.Range(0, 65).Select(i => new ParameterPatchValue("R" + i, "1")).ToArray() })
        {
            var invalidRequest = OfflinePropertyPatch.Preview(f.Project, f.Device, subset, ParameterPatchByteOrder.BigEndian);
            Check("request bounds and duplicates fail atomically", !invalidRequest.IsAvailable && invalidRequest.Issues.Single().Code == PropertyPatchIssueCode.InvalidRequest && invalidRequest.Ranges.Count == 0);
        }
        var manyApp = PropertySourceChecks.App(); var manyMaster = PropertySourceChecks.Master();
        var records = new List<byte[]>(); var manyValues = new List<ParameterPatchValue> { new("R", "5") };
        for (var i = 0; i < 17; i++)
        {
            var pid = (ushort)(10 + i);
            records.Add(PropertySourceChecks.Record(4, 0, 3, pid, Convert.FromHexString("AABBE3CC"), [255, 255, 255, 255]));
            records.Add(PropertySourceChecks.Record(4, 0xFFFFFFFE, 3, pid, [0, 2], [255, 255]));
            if (i == 0) continue;
            Add(manyApp, "R" + i, "P" + i, 2, 3, property: $"ObjectIndex='3' PropertyId='{pid}'"); manyValues.Add(new("R" + i, "5"));
            manyMaster.Descendants(Ns + "InterfaceObject").Single().Add(new XElement(Ns + "Property", new XAttribute("PropertyID", pid), new XAttribute("PropertyDataType", "PDT_UNSIGNED_INT")));
        }
        var many = PropertySourceChecks.Load(directory, manyApp, manyMaster, PropertySourceChecks.Container(records.ToArray()));
        var limited = OfflinePropertyPatch.Preview(many.Project, many.Device, manyValues, ParameterPatchByteOrder.BigEndian);
        Check("seventeen complete properties hit the exact atomic property budget", !limited.IsAvailable && limited.SourceIndex.Entries.Count == 17
            && limited.Issues.Single().Code == PropertyPatchIssueCode.ResourceLimitExceeded && limited.Images.Count == 0 && limited.Ranges.Count == 0);
        var reload = Knxproj.Load(f.Path);
        Check("reload identity cannot reuse a previous strict patch binding", binding?.IsCurrent(reload, ProjectQuery.DeviceList(reload).Single()) == false);
        foreach (var guid in new[] { new string('x', 257), "bad\nidentity", "   " })
        {
            var invalidGuid = PropertySourceChecks.Load(directory); invalidGuid.Project.Guid = guid;
            Refused("metadata identity is bounded before source/capture hashes", invalidGuid, PropertyPatchIssueCode.InvalidRequest);
        }
        Console.WriteLine($"PropertyPatch: {checks} checks, {failures} failures"); return failures;
    }
}
