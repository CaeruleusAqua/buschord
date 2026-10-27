using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;

internal static class ArchivedResourceChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private const string Mask = "MV-07B0";
    private const string AppNs = "http://knx.org/xml/project/23";
    private static XDocument Master() => XDocument.Parse($$"""
        <KNX xmlns="{{Ns}}"><MasterData><MaskVersions><MaskVersion Id="{{Mask}}" MaskVersion="1968">
        <HawkConfigurationData><Features><Feature Name="FirstAppObjectIdx" Value="6"/><Feature Name="PropertyMappedLsms" Value="1"/></Features><Resources>
        <Resource Name="Absolute" Access="remote"><Location AddressSpace="StandardMemory" StartAddress="100"/><ResourceType Length="4"/><AccessRights Read="Runtime" Write="Configuration"/></Resource>
        <Resource Name="Relative" Access="remote local2"><Location AddressSpace="RelativeMemory" InterfaceObjectRef="4" PropertyID="7" StartAddress="10"/><ResourceType Length="16"/></Resource>
        <Resource Name="System" Access="remote"><Location AddressSpace="SystemProperty" InterfaceObjectRef="0" PropertyID="78" StartAddress="0"/><ImgLocation AddressSpace="StandardMemory" StartAddress="200"/><ResourceType Length="6" Flavour="OpaqueLayout"/></Resource>
        <Resource Name="Application" Access="remote"><Location AddressSpace="AppProperty" InterfaceObjectRef="0" PropertyID="7" StartAddress="0"/><ResourceType Length="4"/></Resource>
        <Resource Name="Typed" Access="remote"><Location AddressSpace="Property" InterfaceObjectRef="200" Occurrence="2" PropertyID="9" StartAddress="3"/><ResourceType Length="2"/></Resource>
        <Resource Name="Pointer" Access="remote"><Location AddressSpace="Pointer" PtrResource="Absolute"/><ResourceType Length="4"/></Resource>
        <Resource Name="Absent" Access="remote"><Location AddressSpace="None"/></Resource>
        <Resource Name="Constant" Access="remote"><Location AddressSpace="Constant" StartAddress="2"/><ResourceType Length="1"/></Resource>
        <Resource Name="Adc" Access="remote"><Location AddressSpace="ADC" StartAddress="0"/><ResourceType Length="2"/></Resource>
        </Resources><MemorySegments><MemorySegment MemoryType="EEPROM" Length="16"><Location AddressSpace="StandardMemory" StartAddress="100"/><AccessRights Read="Configuration" Write="Configuration"/></MemorySegment></MemorySegments></HawkConfigurationData>
        <HawkConfigurationData LegacyVersion="1"><Resources><Resource Name="Unselected" Access="remote"><Location AddressSpace="StandardMemory" StartAddress="999"/></Resource></Resources></HawkConfigurationData>
        </MaskVersion><MaskVersion Id="MV-other" MaskVersion="1"><HawkConfigurationData/></MaskVersion></MaskVersions></MasterData></KNX>
        """);
    private static XElement Current(XDocument doc) => doc.Descendants(Ns + "HawkConfigurationData").First();
    private static XElement Resources(XDocument doc) => Current(doc).Element(Ns + "Resources")!;
    private static XElement Resource(XDocument doc, string name = "Absolute") => Resources(doc).Elements().Single(e => (string?)e.Attribute("Name") == name);
    private static XDocument Change(Action<XDocument> edit) { var doc = Master(); edit(doc); return doc; }
    private static ArchivedResourceContext Read(XDocument? doc) => ArchivedResourceContext.Read(doc, Mask, AppNs);
    private static XElement NewResource(string name, string access = "remote", string? style = null) => new(Ns + "Resource",
        new XAttribute("Name", name), new XAttribute("Access", access), style is null ? null : new XAttribute("MgmtStyle", style),
        new XElement(Ns + "Location", new XAttribute("AddressSpace", "StandardMemory"), new XAttribute("StartAddress", "500")));

    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "ArchivedResource " + name); if (!ok) failures++; }
        var master = Master(); var source = master.ToString(SaveOptions.DisableFormatting); var context = Read(master);
        ArchivedResourceDescriptor Get(string name) => context.ResolveRemote(name).Descriptor!;
        Check("exact current-zero selected manifest", context.Status == ArchivedResourceContextStatus.Supported && context.MaskVersionId == Mask
            && context.NumericMask == 1968 && context.LegacyVersion == 0 && context.FirstAppObjectIndex == 6 && context.PropertyMappedLsms == true
            && context.Resources.Count == 9 && context.MemoryRegions.Count == 1 && context.ResolveRemote("Unselected").Status == ArchivedResourceResolutionStatus.Missing);
        Check("absolute geometry and declared rights", Get("Absolute") is { DeclaredLength: 4, ReadAccess: "Runtime", WriteAccess: "Configuration",
            Location: { AddressSpace: ArchivedResourceAddressSpace.StandardMemory, Offset: 100, NativeObjectIndex: null }, ImageLocation: null });
        Check("relative location stays logical and has no native/application index", Get("Relative").Location is
            { AddressSpace: ArchivedResourceAddressSpace.RelativeMemory, InterfaceObjectRef: 4, PropertyId: 7, Offset: 10, NativeObjectIndex: null, ApplicationObjectIndex: null });
        Check("system0 and separate image location stay distinct", Get("System") is { DeclaredLength: 6, Flavour: "OpaqueLayout",
            Location: { AddressSpace: ArchivedResourceAddressSpace.SystemProperty, NativeObjectIndex: 0, ApplicationObjectIndex: null, ObjectType: null, Offset: 0 },
            ImageLocation: { AddressSpace: ArchivedResourceAddressSpace.StandardMemory, Offset: 200 } });
        Check("application0 never aliases native0", Get("Application").Location is
            { AddressSpace: ArchivedResourceAddressSpace.AppProperty, ApplicationObjectIndex: 0, NativeObjectIndex: null, ObjectType: null });
        Check("type occurrence is not converted into an index", Get("Typed").Location is
            { AddressSpace: ArchivedResourceAddressSpace.PropertyByType, ObjectType: 200, Occurrence: 2, PropertyId: 9, Offset: 3, NativeObjectIndex: null, ApplicationObjectIndex: null });
        Check("pointer remains a declared pointer", Get("Pointer").Location is { AddressSpace: ArchivedResourceAddressSpace.Pointer, PointerResource: "Absolute", Offset: null });
        Check("none constant and ADC preserve their explicit domains", Get("Absent").Location is { AddressSpace: ArchivedResourceAddressSpace.None, Offset: null }
            && Get("Constant").Location is { AddressSpace: ArchivedResourceAddressSpace.Constant, Offset: 2 }
            && Get("Adc").Location is { AddressSpace: ArchivedResourceAddressSpace.Adc, Offset: 0 });
        Check("region length does not become source bytes or resource length", context.MemoryRegions.Single() is
            { MemoryType: "EEPROM", Length: 16, Optional: null, Location.Offset: 100 } && Get("Absolute").DeclaredLength == 4);
        Check("input document remains unchanged", master.ToString(SaveOptions.DisableFormatting) == source);
        master.Descendants(Ns + "Location").First().SetAttributeValue("StartAddress", "101");
        Check("source mutation cannot mutate immutable context", Get("Absolute").Location!.Offset == 100 && Read(master).Fingerprint != context.Fingerprint);
        Check("public collections cannot be changed", context.Resources is not ArchivedResourceDescriptor[] && context.MemoryRegions is not ArchivedMemoryRegion[]
            && ((IList<ArchivedResourceDescriptor>)context.Resources).IsReadOnly && ((IList<string>)Get("Relative").Access).IsReadOnly);
        Check("document hash is distinct from optional original-byte provenance", context.MasterDataHash is null && context.DocumentHash is { Length: 64 } && context.Fingerprint.Length == 64);

        var variants = Change(d => { Resources(d).Add(NewResource("Variant", style: "simple"), NewResource("Variant", style: "lsm")); });
        var resolved = Read(variants);
        Check("management style required before choosing a restricted candidate", resolved.Status == ArchivedResourceContextStatus.Supported
            && resolved.ResolveRemote("Variant").Status == ArchivedResourceResolutionStatus.ManagementStyleRequired);
        Check("explicit exact management style has a unique candidate", resolved.ResolveRemote("Variant", "simple") is
            { Status: ArchivedResourceResolutionStatus.Found, Descriptor.ManagementStyles.Count: 1 }
            && resolved.ResolveRemote("Variant", "lsm").Descriptor!.ManagementStyles.Single() == "lsm");
        var localOnly = Read(Change(d => Resources(d).Add(NewResource("Local", "local1"))));
        Check("remote lookup has no local fallback", localOnly.Status == ArchivedResourceContextStatus.Supported
            && localOnly.ResolveRemote("Local").Status == ArchivedResourceResolutionStatus.Missing);
        var ambiguous = Read(Change(d => Resources(d).Add(NewResource("Absolute", "remote local1"))));
        Check("overlapping access variants are ambiguous during exact lookup", ambiguous.Status == ArchivedResourceContextStatus.Supported
            && ambiguous.ResolveRemote("Absolute").Status == ArchivedResourceResolutionStatus.Ambiguous && ambiguous.ResolveRemote("Absolute").Descriptor is null);
        var broadStyle = Read(Change(d => Resources(d).Add(NewResource("Absolute", style: "simple"))));
        Check("general and style-specific candidates never resolve by source order", broadStyle.ResolveRemote("Absolute", "simple").Status == ArchivedResourceResolutionStatus.Ambiguous);
        Check("request names and styles are exact", context.ResolveRemote("absolute").Status == ArchivedResourceResolutionStatus.Missing
            && context.ResolveRemote(" Absolute").Status == ArchivedResourceResolutionStatus.InvalidRequest
            && context.ResolveRemote("Absolute", "Simple").Status == ArchivedResourceResolutionStatus.InvalidRequest);

        var absent = Read(Change(d => { Resource(d).RemoveNodes(); Resource(d).SetAttributeValue("Optional", "false"); }));
        Check("missing locations and length are null without default0", absent.ResolveRemote("Absolute").Descriptor is
            { Location: null, ImageLocation: null, DeclaredLength: null, Optional: false });
        var zero = Read(Change(d => { Resource(d).Element(Ns + "ResourceType")!.SetAttributeValue("Length", "0"); Resource(d).SetAttributeValue("Optional", "true"); }));
        Check("explicit zero length remains known and optional is metadata", zero.ResolveRemote("Absolute").Descriptor is { DeclaredLength: 0, Optional: true });
        var noFirst = Read(Change(d => Current(d).Element(Ns + "Features")!.Remove()));
        Check("missing first-object and mapping features do not invent defaults", noFirst.Status == ArchivedResourceContextStatus.Supported
            && noFirst.FirstAppObjectIndex is null && noFirst.PropertyMappedLsms is null && noFirst.ResolveRemote("System").Descriptor!.Location!.NativeObjectIndex == 0);
        var noOccurrence = Read(Change(d => Resource(d, "Typed").Element(Ns + "Location")!.Attribute("Occurrence")!.Remove()));
        Check("missing occurrence is retained as unknown", noOccurrence.ResolveRemote("Typed").Descriptor!.Location!.Occurrence is null);
        var noPid = Read(Change(d => Resource(d, "Relative").Element(Ns + "Location")!.Attribute("PropertyID")!.Remove()));
        Check("relative property component is not defaulted to7", noPid.ResolveRemote("Relative").Descriptor!.Location!.PropertyId is null);

        foreach (var ns in new[] { "http://knx.org/xml/project/20", "http://knx.org/xml/project/21", "http://knx.org/xml/project/23" })
        {
            Check("explicit application namespace " + ns, ArchivedResourceContext.Read(Master(), Mask, ns).Fingerprint == context.Fingerprint);
            var native = Master(); native.Root!.Attribute("xmlns")?.Remove();
            foreach (var element in native.Descendants()) element.Name = XNamespace.Get(ns) + element.Name.LocalName;
            Check("explicit master namespace " + ns, Read(native).Fingerprint == context.Fingerprint);
        }
        foreach (var ns in new string?[] { "http://knx.org/xml/project/22", "urn:unknown", "", null })
        {
            var bad = ArchivedResourceContext.Read(Master(), Mask, ns);
            Check("unapproved application namespace refuses derived manifest " + (ns ?? "null"), bad.Status == ArchivedResourceContextStatus.UnsupportedApplicationNamespace
                && bad.Resources.Count == 0 && bad.MemoryRegions.Count == 0 && bad.ResolveRemote("Absolute").Status == ArchivedResourceResolutionStatus.ContextUnavailable);
        }
        foreach (var first in new[] { "0", "255" })
            Check("explicit first-object boundary " + first, Read(Change(d => Current(d).Descendants(Ns + "Feature").First().SetAttributeValue("Value", first))).FirstAppObjectIndex == byte.Parse(first));

        var reorder = Read(Change(d => { var entries = Resources(d).Elements().ToArray(); Resources(d).ReplaceNodes(entries.Reverse()); Resource(d, "Relative").SetAttributeValue("Access", "local2 remote"); }));
        Check("semantic order normalization is independent from document hash", reorder.Fingerprint == context.Fingerprint && reorder.DocumentHash != context.DocumentHash);
        var unrelated = Read(Change(d => { d.Descendants(Ns + "MaskVersion").Last().SetAttributeValue("MaskVersion", "123");
            d.Descendants(Ns + "HawkConfigurationData").ElementAt(1).Element(Ns + "Resources")!.Remove(); Current(d).Add(new XElement(Ns + "Procedures", new XElement(Ns + "Procedure"))); }));
        Check("other masks legacy and unconsumed procedures do not stale semantic binding", unrelated.Fingerprint == context.Fingerprint && unrelated.DocumentHash != context.DocumentHash);
        var mappedChange = Read(Change(d => Current(d).Descendants(Ns + "Feature").Last().SetAttributeValue("Value", "0")));
        Check("consumed mapping feature changes its independent fingerprint", mappedChange.PropertyMappedLsms == false && mappedChange.Fingerprint != context.Fingerprint);
        Check("selected resource geometry changes fingerprint", Read(Change(d => Resource(d).Element(Ns + "Location")!.SetAttributeValue("StartAddress", "101"))).Fingerprint != context.Fingerprint);

        var cases = new (string Name, ArchivedResourceContextStatus Status, Action<XDocument> Edit)[]
        {
            ("duplicate exact mask", ArchivedResourceContextStatus.Ambiguous, d => d.Descendants(Ns + "MaskVersions").Single().Add(new XElement(d.Descendants(Ns + "MaskVersion").First()))),
            ("duplicate current sets", ArchivedResourceContextStatus.Ambiguous, d => Current(d).Parent!.Add(new XElement(Current(d)))),
            ("missing current set", ArchivedResourceContextStatus.MissingConfiguration, d => Current(d).SetAttributeValue("LegacyVersion", "2")),
            ("invalid version", ArchivedResourceContextStatus.InvalidDeclaration, d => Current(d).SetAttributeValue("LegacyVersion", "-1")),
            ("duplicate resources container", ArchivedResourceContextStatus.Ambiguous, d => Current(d).Add(new XElement(Resources(d)))),
            ("duplicate resource key", ArchivedResourceContextStatus.Ambiguous, d => Resources(d).Add(new XElement(Resource(d)))),
            ("duplicate location", ArchivedResourceContextStatus.Ambiguous, d => Resource(d).Add(new XElement(Resource(d).Element(Ns + "Location")!))),
            ("unknown resource semantics", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).SetAttributeValue("Transform", "guess")),
            ("foreign semantic attribute", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).SetAttributeValue(XNamespace.Get("urn:foreign") + "Access", "remote")),
            ("foreign location", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).Element(Ns + "Location")!.Name = XNamespace.Get("urn:foreign") + "Location"),
            ("unsupported location space", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).Element(Ns + "Location")!.SetAttributeValue("AddressSpace", "LcSlaveMemory")),
            ("missing explicit offset", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).Element(Ns + "Location")!.Attribute("StartAddress")!.Remove()),
            ("negative offset", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).Element(Ns + "Location")!.SetAttributeValue("StartAddress", "-1")),
            ("overflowing offset", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).Element(Ns + "Location")!.SetAttributeValue("StartAddress", "4294967296")),
            ("coordinate end overflow", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).Element(Ns + "Location")!.SetAttributeValue("StartAddress", "4294967295")),
            ("missing native selector", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d, "System").Element(Ns + "Location")!.Attribute("InterfaceObjectRef")!.Remove()),
            ("mixed index and type selector", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d, "System").Element(Ns + "Location")!.SetAttributeValue("Occurrence", "1")),
            ("pointer lacks explicit target", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d, "Pointer").Element(Ns + "Location")!.Attribute("PtrResource")!.Remove()),
            ("unknown access token", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).SetAttributeValue("Access", "remote future")),
            ("duplicate access token", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).SetAttributeValue("Access", "remote remote")),
            ("unknown management token", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).SetAttributeValue("MgmtStyle", "future")),
            ("malformed optional", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).SetAttributeValue("Optional", "maybe")),
            ("malformed declared length", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).Element(Ns + "ResourceType")!.SetAttributeValue("Length", "NaN")),
            ("unknown access right", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).Element(Ns + "AccessRights")!.SetAttributeValue("Write", "Always")),
            ("first index outside byte", ArchivedResourceContextStatus.InvalidDeclaration, d => Current(d).Descendants(Ns + "Feature").First().SetAttributeValue("Value", "256")),
            ("mapping feature not explicit0or1", ArchivedResourceContextStatus.InvalidDeclaration, d => Current(d).Descendants(Ns + "Feature").Last().SetAttributeValue("Value", "2")),
            ("duplicate first index", ArchivedResourceContextStatus.Ambiguous, d => Current(d).Element(Ns + "Features")!.Add(new XElement(Current(d).Descendants(Ns + "Feature").First()))),
            ("overlapping regions", ArchivedResourceContextStatus.Ambiguous, d => Current(d).Element(Ns + "MemorySegments")!.Add(new XElement(Current(d).Descendants(Ns + "MemorySegment").Single()))),
            ("resource payload is outside declaration profile", ArchivedResourceContextStatus.InvalidDeclaration, d => Resource(d).Add(new XElement(Ns + "Data", "AA=="))),
        };
        foreach (var item in cases)
        {
            var bad = Read(Change(item.Edit));
            Check(item.Name + " refuses all partial declarations", bad.Status == item.Status && bad.Resources.Count == 0 && bad.MemoryRegions.Count == 0);
        }
        var lastByte = Read(Change(d => { Resource(d).Element(Ns + "Location")!.SetAttributeValue("StartAddress", uint.MaxValue); Resource(d).Element(Ns + "ResourceType")!.SetAttributeValue("Length", "1"); }));
        Check("uintMax last-byte interval has valid ulong end", lastByte.Status == ArchivedResourceContextStatus.Supported && lastByte.ResolveRemote("Absolute").Descriptor!.Location!.Offset == uint.MaxValue);
        var twoLsms = Read(Change(d =>
        {
            var regions = Current(d).Element(Ns + "MemorySegments")!; regions.RemoveNodes();
            foreach (var lsm in new[] { "0", "255" }) regions.Add(new XElement(Ns + "MemorySegment", new XAttribute("MemoryType", "EEPROM"), new XAttribute("Length", "16"),
                new XElement(Ns + "Location", new XAttribute("AddressSpace", "RelativeMemory"), new XAttribute("InterfaceObjectRef", lsm), new XAttribute("StartAddress", "0"))));
        }));
        Check("equal region offsets in distinct LSMs are separate arenas", twoLsms.Status == ArchivedResourceContextStatus.Supported
            && twoLsms.MemoryRegions.Count == 2 && twoLsms.MemoryRegions.Select(r => r.Location.InterfaceObjectRef).SequenceEqual(new ushort?[] { 0, 255 }));
        Check("missing master has controlled empty manifest", Read(null).Status == ArchivedResourceContextStatus.MissingMasterData);
        Check("missing and foreign masks do not suffix-match", ArchivedResourceContext.Read(Master(), null, AppNs).Status == ArchivedResourceContextStatus.MissingMaskId
            && ArchivedResourceContext.Read(Master(), Mask + "-suffix", AppNs).Status == ArchivedResourceContextStatus.MaskNotFound);
        var master22 = Master(); master22.Root!.Attribute("xmlns")?.Remove();
        foreach (var e in master22.Descendants()) e.Name = XNamespace.Get("http://knx.org/xml/project/22") + e.Name.LocalName;
        Check("master22 is not admitted through supported app23", Read(master22).Status == ArchivedResourceContextStatus.UnsupportedMasterNamespace);

        var full = Master(); Resources(full).RemoveNodes();
        Resources(full).Add(Enumerable.Range(0, 4096).Select(i => NewResource("R" + i)));
        Check("4096 declarations within explicit limit", Read(full) is { Status: ArchivedResourceContextStatus.Supported, Resources.Count: 4096 });
        Resources(full).Add(NewResource("R4096"));
        Check("4097 declarations refuse before resource index publication", Read(full) is { Status: ArchivedResourceContextStatus.LimitExceeded, Resources.Count: 0 });
        var deep = Master(); var bottom = Current(deep); for (var i = 0; i < 130; i++) { var next = new XElement(Ns + "Wrapper"); bottom.Add(next); bottom = next; }
        Check("depth budget refuses before hashing", Read(deep).Status == ArchivedResourceContextStatus.LimitExceeded);
        var large = Master(); large.Root!.Add(new XComment(new string('x', 16 * 1024 * 1024)));
        Check("document character budget bounded", Read(large).Status == ArchivedResourceContextStatus.LimitExceeded);
        var dtd = Master(); dtd.AddFirst(new XDocumentType("KNX", null, null, "<!ENTITY x 'value'>"));
        Check("DTD cannot supply resource facts", Read(dtd).Status == ArchivedResourceContextStatus.InvalidDeclaration);

        ArchiveProvenance(directory, Check);
        return failures;
    }

    private static void ArchiveProvenance(string directory, Action<string, bool> check)
    {
        const string appId = "M-0001_A-0100-10-0000";
        var project = new Project { Id = "P-resource-index", Name = "Resource index fixture" };
        var path = Path.Combine(directory, "archived-resource-index-" + Guid.NewGuid().ToString("N") + ".knxproj");
        var master = System.Text.Encoding.UTF8.GetBytes(Master().ToString(SaveOptions.DisableFormatting));
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var output = zip.CreateEntry("0.xml").Open()) Knxproj.ToXml(project).Save(output);
            using (var output = zip.CreateEntry("knx_master.xml").Open()) output.Write(master);
            using var app = zip.CreateEntry("M-0001/" + appId + ".xml").Open();
            XDocument.Parse($"<KNX xmlns='{AppNs}'><ApplicationProgram Id='{appId}' MaskVersion='{Mask}'><Static/></ApplicationProgram></KNX>").Save(app);
        }
        var source = File.ReadAllBytes(path); var loaded = Knxproj.Load(path);
        var context = Read(Knxproj.LoadMasterData(loaded));
        check("immutable archive source supplies original entry byte hash", context.Status == ArchivedResourceContextStatus.Supported
            && context.MasterDataHash == Convert.ToHexStringLower(SHA256.HashData(master)) && context.DocumentHash is { Length: 64 });
        check("manifest inspection leaves archive and project unchanged", source.SequenceEqual(File.ReadAllBytes(path)) && loaded.Id == project.Id);
    }
}
