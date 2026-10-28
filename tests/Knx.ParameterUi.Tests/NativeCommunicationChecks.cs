using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class NativeCommunicationChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
    private sealed record Slot(int Number, bool Placeholder, bool Active, uint Bits, bool C, bool R, bool W, bool T, bool U, bool I, string Priority);
    private sealed record Group(ushort Address, bool Acknowledge);
    private sealed record Association(ushort Number, int GroupIndex, bool Sending);
    private sealed record View(object Raw, bool Available, string? Fingerprint, string? Inputs, string? Source, string? Application,
        Slot[] Slots, Group[] Groups, Association[] Associations, IReadOnlyList<GeneratedTableIssue> Issues);
    private static T Get<T>(object value, string property) => (T)value.GetType().GetProperty(property, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
    private static T Public<T>(object value, string property) => (T)value.GetType().GetProperty(property)!.GetValue(value)!;
    private static object[] Rows(object value, string property) => ((IEnumerable)Get<object>(value, property)).Cast<object>().ToArray();
    private static View Read(ArchivedImageSourceChecks.Fixture fixture, IParameterScriptRuntime? scripts = null)
    {
        var inputs = typeof(ArchivedImageSources).GetMethod("ReadInputs", Hidden)!.Invoke(null, [fixture.Project, fixture.Device]);
        var type = typeof(Project).Assembly.GetType("Knx.NativeCommunicationWitness", true)!;
        var result = type.GetMethod("Create", Hidden)!.Invoke(null, [fixture.Project, fixture.Device, inputs, scripts])!;
        return new(result, Get<bool>(result, "IsAvailable"), Get<string?>(result, "Fingerprint"), Get<string?>(result, "ProjectInputHash"),
            Get<string?>(result, "SourceFingerprint"), Get<string?>(result, "ApplicationDefinitionHash"),
            Rows(result, "Slots").Select(s => new Slot(Public<ushort>(s, "Number"), Public<bool>(s, "IsPlaceholder"), Public<bool>(s, "Active"),
                Public<uint>(s, "SizeInBit"), Public<bool>(s, "CommunicationEnabled"), Public<bool>(s, "ReadEnabled"), Public<bool>(s, "WriteEnabled"),
                Public<bool>(s, "TransmitEnabled"), Public<bool>(s, "UpdateEnabled"), Public<bool>(s, "ReadOnInitEnabled"), Public<object>(s, "Priority").ToString()!)).ToArray(),
            Rows(result, "Groups").Select(g => new Group(Public<ushort>(g, "Address"), Public<bool>(g, "Acknowledge"))).ToArray(),
            Rows(result, "Associations").Select(a => new Association(Public<ushort>(a, "ObjectNumber"), Public<int>(a, "GroupAddressIndex"), Public<bool>(a, "Sending"))).ToArray(),
            Get<IReadOnlyList<GeneratedTableIssue>>(result, "Issues"));
    }
    private static XElement Definition(string id, int number, string size = "1 Bit") => new(Ns + "ComObject",
        new XAttribute("Id", id), new XAttribute("Name", id), new XAttribute("Text", id), new XAttribute("Number", number), new XAttribute("ObjectSize", size),
        new XAttribute("CommunicationFlag", "Enabled"), new XAttribute("ReadFlag", "Enabled"), new XAttribute("WriteFlag", "Disabled"),
        new XAttribute("TransmitFlag", "Enabled"), new XAttribute("UpdateFlag", "Disabled"), new XAttribute("ReadOnInitFlag", "Disabled"));
    internal static XDocument App()
    {
        var doc = ArchivedImageSourceChecks.App(); var app = Program(doc); var stat = app.Element(Ns + "Static")!;
        stat.Element(Ns + "ParameterTypes")!.Elements().Single().Elements().Single().SetAttributeValue("maxInclusive", "1");
        stat.Add(new XElement(Ns + "ComObjectTable", Definition("O1", 1), Definition("O3", 3, "2 Bytes"), Definition("O5", 5, "1 Byte")),
            new XElement(Ns + "ComObjectRefs", new XElement(Ns + "ComObjectRef", new XAttribute("Id", "C1"), new XAttribute("RefId", "O1")),
                new XElement(Ns + "ComObjectRef", new XAttribute("Id", "C1-other"), new XAttribute("RefId", "O1")),
                new XElement(Ns + "ComObjectRef", new XAttribute("Id", "C3"), new XAttribute("RefId", "O3"), new XAttribute("Priority", "Alert"),
                    new XAttribute("CommunicationFlag", "Disabled"), new XAttribute("WriteFlag", "Enabled"))));
        app.Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ComObjectRefRef", new XAttribute("RefId", "C1")),
            new XElement(Ns + "choose", new XAttribute("ParamRefId", "R"), new XElement(Ns + "when", new XAttribute("test", "1"),
                new XElement(Ns + "ComObjectRefRef", new XAttribute("RefId", "C3")))));
        return doc;
    }
    private static XElement Program(XDocument doc) => doc.Descendants(Ns + "ApplicationProgram").Single();
    private static ArchivedImageSourceChecks.Fixture Load(string directory, XDocument? app = null) => ArchivedImageSourceChecks.Load(directory, app: app ?? App());
    private static ComObjectInstanceRef Saved(ArchivedImageSourceChecks.Fixture fixture, string id = "C1", string definition = "O1", int number = 1, params string[] links)
    {
        var obj = new ComObjectInstanceRef { Id = id, ComObjectId = definition, Number = number };
        obj.Links.AddRange(links); fixture.Device.ComObjectRefs.Add(obj); return obj;
    }
    private static GroupRange Groups(ArchivedImageSourceChecks.Fixture fixture)
    {
        var range = new GroupRange { Id = "GR", Name = "Groups", RangeStart = 1, RangeEnd = 65535 };
        range.Addresses.Add(new() { Id = "G9", Name = "Nine", Address = new(9), Puid = 1 });
        range.Addresses.Add(new() { Id = "G2", Name = "Two", Address = new(2), Puid = 2 });
        fixture.Project.Installations[0].MainRanges.Add(range); return range;
    }
    private static ObjectMetadata Metadata(ArchivedImageSourceChecks.Fixture fixture, string id = "C1")
    {
        var metadata = new ObjectMetadata(); fixture.Project.Workbench.Objects[new ObjectKey(fixture.Device.Id, id).ToString()] = metadata; return metadata;
    }
    private static XElement Native(ArchivedImageSourceChecks.Fixture fixture, string id = "C1")
    {
        var original = (XDocument)typeof(Project).GetProperty("OriginalXml", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fixture.Project)!;
        var device = original.Descendants().Single(e => e.Name.LocalName == "DeviceInstance"); var ns = device.Name.Namespace;
        var container = device.Element(ns + "ComObjectInstanceRefs");
        if (container is null) { container = new(ns + "ComObjectInstanceRefs"); device.Add(container); }
        var row = new XElement(ns + "ComObjectInstanceRef", new XAttribute("RefId", id)); container.Add(row); return row;
    }
    internal static XDocument Modules(bool repeat = false, bool nested = false)
    {
        var doc = ArchivedImageSourceChecks.App(); var app = Program(doc); var md = new XElement(Ns + "ModuleDef", new XAttribute("Id", "MD"),
            new XElement(Ns + "Arguments", new XElement(Ns + "Argument", new XAttribute("Id", "MD_A"), new XAttribute("Name", "ObjectBase"), new XAttribute("Type", "Numeric"))),
            new XElement(Ns + "Static", new XElement(Ns + "ComObjects", Definition("MD_O", 0)),
                new XElement(Ns + "ComObjectRefs", new XElement(Ns + "ComObjectRef", new XAttribute("Id", "MD_C"), new XAttribute("RefId", "MD_O")))),
            new XElement(Ns + "Dynamic", new XElement(Ns + "ComObjectRefRef", new XAttribute("RefId", "MD_C"))));
        md.Descendants(Ns + "ComObject").Single().SetAttributeValue("BaseNumber", "MD_A");
        app.Add(new XElement(Ns + "ModuleDefs", md)); var dynamic = app.Element(Ns + "Dynamic")!;
        XElement Call(string id, int value) => new(Ns + "Module", new XAttribute("Id", id), new XAttribute("RefId", "MD"),
            new XElement(Ns + "NumericArg", new XAttribute("RefId", "MD_A"), new XAttribute("Value", value)));
        if (nested)
        {
            var child = new XElement(md);
            foreach (var attr in child.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "Id" or "RefId" or "BaseNumber"))
                if (attr.Value.StartsWith("MD_", StringComparison.Ordinal)) attr.Value = "MD_SM_" + attr.Value[3..];
            child.SetAttributeValue("Id", "MD_SM");
            md.Element(Ns + "Static")!.RemoveNodes(); md.Element(Ns + "Dynamic")!.RemoveNodes();
            md.Add(new XElement(Ns + "SubModuleDefs", child));
            md.Element(Ns + "Dynamic")!.Add(new XElement(Ns + "Module", new XAttribute("Id", "MD_Child"), new XAttribute("RefId", "MD_SM"),
                new XElement(Ns + "NumericArg", new XAttribute("RefId", "MD_SM_A"), new XAttribute("Value", "1"), new XAttribute("BaseValue", "MD_A"))));
            dynamic.Add(Call("M", 3));
        }
        else if (repeat) dynamic.Add(new XElement(Ns + "Repeat", new XAttribute("Id", "Repeat"), new XAttribute("Count", "2"), Call("M", 3)));
        else { dynamic.Add(Call("M1", 2), Call("M2", 4)); }
        return doc;
    }
    private sealed class Runtime(Action? callback = null) : IParameterScriptRuntime
    {
        internal int Calls;
        public IReadOnlyDictionary<string, object?> Execute(string source, IReadOnlyDictionary<string, object?> inputs, IReadOnlyList<string> outputs)
        { Calls++; callback?.Invoke(); return outputs.ToDictionary(id => id, _ => (object?)1d, StringComparer.Ordinal); }
    }
    private static void Calculation(XDocument doc, string target = "R")
    {
        var stat = Program(doc).Element(Ns + "Static")!;
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "P-source"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "1")));
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "R-source"), new XAttribute("RefId", "P-source")));
        stat.Add(new XElement(Ns + "ParameterCalculations", new XElement(Ns + "ParameterCalculation", new XAttribute("Language", "JavaScript"),
            new XElement(Ns + "LRTransformation", "right=left;"),
            new XElement(Ns + "LParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R-source"), new XAttribute("AliasName", "left"))),
            new XElement(Ns + "RParameters", new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", target), new XAttribute("AliasName", "right"))))));
    }

    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "NativeCommunication " + name); if (!ok) failures++; }
        void Refused(string name, ArchivedImageSourceChecks.Fixture fixture, GeneratedTableIssueCode? expected = null, IParameterScriptRuntime? scripts = null)
        {
            var result = Read(fixture, scripts);
            Check(name, !result.Available && result.Fingerprint is null && result.Slots.Length == 0 && result.Groups.Length == 0 && result.Associations.Length == 0
                && result.Issues.Count == 1 && (expected is null || result.Issues[0].Code == expected));
        }
        var f = Load(directory); var inputBytes = File.ReadAllBytes(f.Path); var before = Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting);
        var revision = f.Project.ApplicationDataRevision; var undo = new UndoStack(); undo.Reset(f.Project); var undoIndex = undo.Index;
        var result = Read(f); var manifest = ArchivedImageSources.Read(f.Project, f.Device);
        Check("complete native inventory contains bare declarations and explicit number holes", result.Available && result.Slots.Length == 6
            && result.Slots[0] is { Placeholder: true, Active: false, Bits: 0 } && result.Slots[2].Placeholder && result.Slots[4].Placeholder
            && result.Slots[5] is { Placeholder: false, Active: false, Bits: 8 });
        Check("multiple active references resolve independent overrides and C is not activity", result.Available && result.Slots[1] is { Active: true, Bits: 1, C: true, R: true, T: true, Priority: "Low" }
            && result.Slots[3] is { Active: true, Bits: 16, C: false, W: true, Priority: "Alert" });
        Check("inactive aliases do not create duplicate slot owners", result.Available && result.Slots.Count(s => !s.Placeholder) == 3);
        Check("inactive native C remains a raw fact separate from activity", result.Available && result.Slots[5] is { C: true, Active: false });
        Check("witness source hashes reuse the exact source manifest", result.Available && result.Source == manifest.Binding?.Fingerprint && result.Application == manifest.ApplicationDefinitionHash);
        Check("identical fresh captures are stable", result.Available && Read(f).Fingerprint == result.Fingerprint && Read(f).Inputs == result.Inputs);
        Check("inspection leaves model revision archive and undo unchanged", before == Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting)
            && revision == f.Project.ApplicationDataRevision && undo.Index == undoIndex && File.ReadAllBytes(f.Path).AsSpan().SequenceEqual(inputBytes));
        var immutable = (IList)Get<object>(result.Raw, "Slots"); var refusedMutation = false;
        try { immutable.Clear(); } catch (NotSupportedException) { refusedMutation = true; }
        Check("published slot inventory is immutable", result.Available && refusedMutation && Read(f).Fingerprint == result.Fingerprint);
        var empty = Read(Load(directory, ArchivedImageSourceChecks.App()));
        Check("empty declared inventory is distinct from a failed inventory", empty.Available && empty.Slots.Length == 0 && empty.Issues.Count == 0);
        Metadata(f).Active = false;
        Check("host display activity does not deactivate a natively placed slot", Read(f) is { Available: true } display && display.Slots[1].Active);

        f = Load(directory); Groups(f); Saved(f, links: ["G9", "G2"]); Saved(f, "C3", "O3", 3, "G2");
        result = Read(f);
        Check("multiple object associations have a unique sorted group table", result.Available && result.Groups.SequenceEqual([new(2, false), new(9, false)])
            && result.Associations.Length == 3 && result.Associations.Count(a => a.Sending) == 2);
        Check("legacy first current link is sending without moving model links", result.Available && result.Associations.Any(a => a.Number == 1 && a.GroupIndex == 1 && a.Sending)
            && f.Device.ComObjectRefs[0].Links.SequenceEqual(["G9", "G2"]));
        var metadata = Metadata(f); metadata.SendingGroupId = "G2"; metadata.Connectors["G2"] = new() { Send = true, Acknowledge = true };
        var changed = Read(f);
        Check("explicit current sender supersedes retained first-link convention", changed.Available && changed.Associations.Any(a => a.Number == 1 && a.GroupIndex == 0 && a.Sending)
            && !changed.Associations.Any(a => a.Number == 1 && a.GroupIndex == 1 && a.Sending));
        Check("acknowledgement merges per group while preserving link order", changed.Available && changed.Groups[0].Acknowledge && !changed.Groups[1].Acknowledge
            && f.Device.ComObjectRefs[0].Links.SequenceEqual(["G9", "G2"]) && changed.Fingerprint != result.Fingerprint);
        metadata.Connectors["G9"] = new() { Send = true };
        Refused("two explicit senders have no partial inventory", f, GeneratedTableIssueCode.MultipleSendingAssociations);

        foreach (var mutation in new (string Name, Action<XDocument> Edit, GeneratedTableIssueCode? Code)[]
        {
            ("missing native size", d => d.Descendants(Ns + "ComObject").First().Attribute("ObjectSize")!.Remove(), GeneratedTableIssueCode.UnsupportedObjectSize),
            ("non-native size spelling", d => d.Descendants(Ns + "ComObject").First().SetAttributeValue("ObjectSize", "8 Bit"), GeneratedTableIssueCode.UnsupportedObjectSize),
            ("variable object size", d => d.Descendants(Ns + "ComObject").First().SetAttributeValue("ObjectSize", "LegacyVarData"), GeneratedTableIssueCode.UnsupportedObjectSize),
            ("unknown native priority", d => d.Descendants(Ns + "ComObject").First().SetAttributeValue("Priority", "System"), GeneratedTableIssueCode.UnsupportedObjectPriority),
            ("missing native flag", d => d.Descendants(Ns + "ComObject").First().Attribute("ReadFlag")!.Remove(), GeneratedTableIssueCode.InvalidObjectDeclaration),
            ("raw reference number is not a slot override", d => d.Descendants(Ns + "ComObjectRef").First().SetAttributeValue("Number", "7"), GeneratedTableIssueCode.InvalidObjectDeclaration),
            ("duplicate definition numbers", d => d.Descendants(Ns + "ComObject").Last().SetAttributeValue("Number", "1"), GeneratedTableIssueCode.AmbiguousObjectNumber),
            ("duplicate active aliases", d => Program(d).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ComObjectRefRef", new XAttribute("RefId", "C1-other"))), GeneratedTableIssueCode.AmbiguousObjectNumber),
            ("unknown placement target", d => Program(d).Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ComObjectRefRef", new XAttribute("RefId", "missing"))), null),
            ("object declaration under an unknown container", d => { var table = d.Descendants(Ns + "ComObjectTable").Single(); table.ReplaceWith(new XElement(Ns + "Wrapper", new XElement(table))); }, GeneratedTableIssueCode.InvalidObjectDeclaration),
            ("root object cannot use a module container", d => d.Descendants(Ns + "ComObjectTable").Single().Name = Ns + "ComObjects", GeneratedTableIssueCode.InvalidObjectDeclaration),
            ("foreign object namespace", d => d.Descendants(Ns + "ComObject").First().Name = XName.Get("ComObject", "urn:other"), null),
            ("unknown dynamic wrapper", d => { var node = d.Descendants(Ns + "ComObjectRefRef").First(); node.ReplaceWith(new XElement(Ns + "Wrapper", new XElement(node))); }, null),
            ("foreign controller type shortcut", d => { var p = d.Descendants(Ns + "Parameter").Single(); p.Attribute("ParameterType")!.Remove(); p.SetAttributeValue("ParameterTypeId", "T"); }, GeneratedTableIssueCode.UnknownParameterOrigin),
            ("invalid controller value", d => d.Descendants(Ns + "Parameter").Single().SetAttributeValue("Value", "2"), GeneratedTableIssueCode.UnsupportedActivationCondition),
            ("explicit external configuration", d => Program(d).SetAttributeValue("RequiresExternalSoftware", "true"), GeneratedTableIssueCode.ExternalConfigurationUnaccounted),
            ("plugin child is unaccounted", d => Program(d).Element(Ns + "Static")!.Add(new XElement(Ns + "Extension", new XElement(Ns + "EtsUiPlugin"))), GeneratedTableIssueCode.ExternalConfigurationUnaccounted),
        })
        { var doc = App(); mutation.Edit(doc); Refused(mutation.Name, Load(directory, doc), mutation.Code); }

        f = Load(directory); Groups(f); Saved(f, number: 2); Refused("saved number cannot move a native object", f, GeneratedTableIssueCode.SavedNumberMismatch);
        f = Load(directory); Groups(f); Saved(f, id: "unknown"); Refused("unknown saved reference is refused", f, GeneratedTableIssueCode.UnknownSavedObject);
        f = Load(directory); var range = Groups(f); Saved(f, links: ["G9"]); range.Addresses.Add(new() { Id = "G-other", Name = "Other", Address = new(9) });
        Saved(f, "C3", "O3", 3, "G-other"); Refused("distinct group ids with the same linked raw address", f, GeneratedTableIssueCode.AmbiguousGroupAddress);
        f = Load(directory); Groups(f); Saved(f, links: ["G9", "G9"]); Refused("duplicate current association", f, GeneratedTableIssueCode.DuplicateAssociation);
        f = Load(directory); Saved(f, links: ["missing"]); Refused("missing current group address", f, GeneratedTableIssueCode.UnknownGroupAddress);
        f = Load(directory); Groups(f); Saved(f, "C1-other", "O1", 1, "G9"); Refused("inactive alias with links", f, GeneratedTableIssueCode.InactiveSavedObjectHasLinks);
        f = Load(directory); Groups(f); Saved(f, links: ["G9"]); Metadata(f).Connectors["G9"] = new() { Priority = 0 };
        Refused("connector priority zero still requires an unsupported interpretation", f, GeneratedTableIssueCode.UnsupportedConnectorMetadata);
        f = Load(directory); Groups(f); Saved(f, links: ["G9"]); Metadata(f).Connectors["G9"] = new() { Slot = 0 };
        Refused("connector slot zero still requires an unsupported interpretation", f, GeneratedTableIssueCode.UnsupportedConnectorMetadata);
        f = Load(directory); Groups(f); Saved(f); Metadata(f).Flags = "C"; Refused("unproven host flags cannot replace native flags", f, GeneratedTableIssueCode.InvalidNativeOverride);
        f = Load(directory); Saved(f); Metadata(f).Bits = 16; Refused("unproven host size cannot replace native size", f, GeneratedTableIssueCode.InvalidNativeOverride);
        f = Load(directory); f.Device.BinaryData.Add("unaccounted", [1]); Refused("binary configuration requires a state adapter", f, GeneratedTableIssueCode.UnaccountedBinaryData);
        f = Load(directory); range = Groups(f); range.Ranges.Add(range); Refused("cyclic group ranges are bounded", f, GeneratedTableIssueCode.LimitExceeded);
        f = Load(directory); Groups(f); Saved(f, links: ["G9"]); f.Project.Workbench.Groups["G9"] = new() { Secure = true };
        Refused("actual secure group semantics are refused", f, GeneratedTableIssueCode.SecureAssociationUnsupported);
        f = Load(directory); Groups(f); Saved(f, links: ["G9"]); f.Project.Installations[0].Areas[0].Lines[0].MediumTypeRefId = "MT-1";
        Refused("unsupported linked medium requires its own semantics", f, GeneratedTableIssueCode.UnsupportedMedium);
        var secureObject = App(); secureObject.Descendants(Ns + "ComObject").First().SetAttributeValue("SecurityRequired", "Auth");
        f = Load(directory, secureObject); Groups(f); Saved(f, links: ["G9"]);
        Refused("actual object authentication requirements are refused", f, GeneratedTableIssueCode.SecureAssociationUnsupported);

        f = Load(directory); Groups(f); Saved(f, links: ["G9", "G2"]); var raw = Native(f);
        raw.SetAttributeValue("Priority", "High"); raw.SetAttributeValue("ReadFlag", "Disabled"); raw.SetAttributeValue("Acknowledges", "G2");
        result = Read(f);
        Check("retained native priority flags and acknowledgements are honored", result.Available && result.Slots[1] is { Priority: "High", R: false }
            && result.Groups[0].Acknowledge && !result.Groups[1].Acknowledge);
        var inputHash = result.Inputs; f.Device.ComObjectRefs[0].Links.Reverse(); changed = Read(f);
        Check("current link edits supersede retained link order and stale input hashes", changed.Available && changed.Inputs != inputHash
            && changed.Associations.Any(a => a.GroupIndex == 0 && a.Sending));
        f.Device.ComObjectRefs.Clear(); changed = Read(f);
        Check("removing a saved row cannot resurrect its old override", changed.Available && changed.Slots[1] is { Priority: "Low", R: true });
        f = Load(directory); Saved(f); raw = Native(f); raw.SetAttributeValue("Opaque", new string('x', 4 * 1024 * 1024 + 1));
        Refused("retained unknown attribute is bounded before hashing", f, GeneratedTableIssueCode.LimitExceeded);
        f = Load(directory); Saved(f); raw = Native(f); raw.SetAttributeValue("Acknowledges", new string(' ', 4 * 1024 * 1024 + 1));
        Refused("raw acknowledge text is bounded before splitting", f, GeneratedTableIssueCode.LimitExceeded);

        f = Load(directory, Modules()); result = Read(f);
        Check("independent module occurrences have original number-base witnesses", result.Available && result.Slots.Length == 5
            && result.Slots[2] is { Active: true, Bits: 1 } && result.Slots[4] is { Active: true, Bits: 1 });
        Check("fresh module scope proofs are stable", result.Available && Read(f).Fingerprint == result.Fingerprint);
        f = Load(directory, Modules(nested: true));
        Refused("nested module activation remains outside the initial proof", f, GeneratedTableIssueCode.UnsupportedStructureActivation);
        var localControl = Modules(); var localModule = localControl.Descendants(Ns + "ModuleDef").Single();
        localModule.Element(Ns + "Static")!.AddFirst(
            new XElement(Ns + "Parameters", new XElement(Ns + "Parameter", new XAttribute("Id", "MD_P"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "1"))),
            new XElement(Ns + "ParameterRefs", new XElement(Ns + "ParameterRef", new XAttribute("Id", "MD_R"), new XAttribute("RefId", "MD_P"))));
        var placement = localModule.Element(Ns + "Dynamic")!.Elements().Single(); placement.Remove();
        localModule.Element(Ns + "Dynamic")!.Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "MD_R")),
            new XElement(Ns + "choose", new XAttribute("ParamRefId", "MD_R"), new XElement(Ns + "when", new XAttribute("test", "1"), placement)));
        result = Read(Load(directory, localControl));
        Check("module controller parameters retain scoped identity and their original root type", result.Available && result.Slots[2].Active && result.Slots[4].Active);
        var localType = new XDocument(localControl); var localTypeStatic = localType.Descendants(Ns + "ModuleDef").Single().Element(Ns + "Static")!;
        localTypeStatic.AddFirst(new XElement(Ns + "ParameterTypes", new XElement(Ns + "ParameterType", new XAttribute("Id", "MD_T"),
            new XElement(Ns + "TypeNumber", new XAttribute("Type", "unsignedInt"), new XAttribute("SizeInBit", "8"), new XAttribute("minInclusive", "0"), new XAttribute("maxInclusive", "1")))));
        localTypeStatic.Element(Ns + "Parameters")!.Elements().Single().SetAttributeValue("ParameterType", "MD_T");
        Refused("module controller cannot introduce a non-native local type container", Load(directory, localType), GeneratedTableIssueCode.UnknownParameterOrigin);
        var repeated = Modules(repeat: true); var module = repeated.Descendants(Ns + "ModuleDef").Single();
        var arg = module.Element(Ns + "Arguments")!.Elements().Single(); arg.SetAttributeValue("Allocates", "1");
        Program(repeated).Element(Ns + "Static")!.Add(new XElement(Ns + "Allocators", new XElement(Ns + "Allocator", new XAttribute("Id", "AL"), new XAttribute("Start", "2"), new XAttribute("maxInclusive", "8"))));
        var numeric = Program(repeated).Element(Ns + "Dynamic")!.Descendants(Ns + "NumericArg").Single(); numeric.SetAttributeValue("Value", "0"); numeric.SetAttributeValue("AllocatorRefId", "AL");
        f = Load(directory, repeated);
        Refused("repeat allocator activation remains outside the initial proof", f, GeneratedTableIssueCode.UnsupportedStructureActivation);
        foreach (var mutation in new (string Name, Action<XDocument> Edit)[]
        {
            ("module object cannot use a root table container", d => d.Descendants(Ns + "ComObjects").Single().Name = Ns + "ComObjectTable"),
            ("hidden module definition wrapper", d => { var defs = Program(d).Element(Ns + "ModuleDefs")!; defs.ReplaceWith(new XElement(Ns + "Wrapper", new XElement(defs))); }),
            ("module definition under static", d => { var defs = Program(d).Element(Ns + "ModuleDefs")!; defs.Remove(); Program(d).Element(Ns + "Static")!.Add(defs); }),
            ("module call under unknown wrapper", d => { var call = Program(d).Element(Ns + "Dynamic")!.Elements(Ns + "Module").First(); call.ReplaceWith(new XElement(Ns + "Wrapper", new XElement(call))); }),
        })
        { var doc = Modules(); mutation.Edit(doc); Refused(mutation.Name, Load(directory, doc)); }
        var collision = Modules(); Program(collision).Element(Ns + "Dynamic")!.Elements(Ns + "Module").Last().Elements().Single().SetAttributeValue("Value", "2");
        Refused("different module origins cannot share a native slot", Load(directory, collision), GeneratedTableIssueCode.AmbiguousObjectNumber);

        var calc = App(); Calculation(calc); f = Load(directory, calc);
        Refused("calculation-derived activity requires its own native value proof", f, GeneratedTableIssueCode.UnsupportedActivationDependency);
        var runtime = new Runtime(); result = Read(f, runtime);
        Check("explicit runtime does not bypass the derived-activity boundary", !result.Available && result.Issues[0].Code == GeneratedTableIssueCode.UnsupportedActivationDependency && runtime.Calls == 0);
        var unrelated = App(); Calculation(unrelated, "R-source");
        // A distinct output leaves the object controller outside the calculation graph.
        var stat = Program(unrelated).Element(Ns + "Static")!;
        stat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "P-other"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "1")));
        stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "R-other"), new XAttribute("RefId", "P-other")));
        unrelated.Descendants(Ns + "RParameters").Single().Elements().Single().SetAttributeValue("RefId", "R-other");
        result = Read(Load(directory, unrelated));
        Check("unrelated unrun calculation does not blanket block native tables", result.Available && !Get<bool>(result.Raw, "RequiresScripts"));
        f = Load(directory, unrelated); var changedDuringScript = false;
        var mutating = new Runtime(() => { if (!changedDuringScript) { changedDuringScript = true; f.Device.ParameterValues["R"] = "0"; } });
        Refused("script callback mutation cannot publish mixed inputs", f, GeneratedTableIssueCode.InputsChanged, mutating);
        var transitive = App(); Calculation(transitive, "R-result"); var trstat = Program(transitive).Element(Ns + "Static")!;
        trstat.Element(Ns + "Parameters")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "P-result"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "1")));
        trstat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "R-result"), new XAttribute("RefId", "P-result")));
        Program(transitive).Element(Ns + "Dynamic")!.AddFirst(new XElement(Ns + "Assign", new XAttribute("TargetParamRefRef", "R"), new XAttribute("SourceParamRefRef", "R-result")));
        Refused("derived activity dependencies follow assignment aliases", Load(directory, transitive), GeneratedTableIssueCode.UnsupportedActivationDependency);

        Console.WriteLine($"NativeCommunication checks={checks} failures={failures}");
        return failures;
    }
}
