using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;
using Knx.App.Services;
using Knx.Catalog;

internal static class ParameterMaskProfileChecks
{
    private const string Mask = "MV-07B0";
    private const string Namespace = "http://knx.org/xml/project/20";
    internal static XDocument Master(string first = "6") => XDocument.Parse($$"""
        <KNX xmlns="{{Namespace}}"><MasterData Version="1"><MaskVersions>
        <MaskVersion Id="{{Mask}}" MaskVersion="1968"><HawkConfigurationData><Features>
        <Feature Name="FirstAppObjectIdx" Value="{{first}}"/></Features><InterfaceObjects>
        <InterfaceObject Index="0" ObjectType="0"><Property PropertyID="25" PropertyDataType="PDT_VERSION"/></InterfaceObject>
        <InterfaceObject Index="5" ObjectType="4"><Property PropertyID="7" PropertyDataType="PDT_UNSIGNED_LONG"/></InterfaceObject>
        <InterfaceObject Index="6" ObjectType="200"><Property PropertyID="7" PropertyDataType="PDT_UNSIGNED_INT"/>
        <Property PropertyID="27" PropertyDataType="PDT_GENERIC_08"/><Property PropertyID="9" PropertyDataType="PDT_VARIABLE_LENGTH"/></InterfaceObject>
        </InterfaceObjects></HawkConfigurationData><HawkConfigurationData LegacyVersion="1"><Features>
        <Feature Name="FirstAppObjectIdx" Value="99"/></Features></HawkConfigurationData></MaskVersion>
        <MaskVersion Id="MV-0020" MaskVersion="32"><HawkConfigurationData><Features><Feature Name="FirstAppObjectIdx" Value="4"/></Features></HawkConfigurationData></MaskVersion>
        </MaskVersions></MasterData></KNX>
        """);
    private static XDocument Application(string property = "ObjectIndex='6' PropertyId='7'", string? mask = Mask) => XDocument.Parse($$"""
        <KNX xmlns="{{Namespace}}"><ApplicationProgram Id="{{ParameterStorageChecks.ProgramId}}" {{(mask is null ? "" : "MaskVersion='" + mask + "'")}}><Static>
        <ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8"/></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P" ParameterType="T" Value="1"><Property {{property}} Offset="0" BitOffset="0"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="R" RefId="P"/></ParameterRefs></Static><Dynamic><ParameterRefRef RefId="R"/></Dynamic></ApplicationProgram></KNX>
        """);
    private static ParameterInstance One(ParameterProfileContext? profile, string property = "ObjectIndex='6' PropertyId='7'", string? mask = Mask) =>
        Ksxml.Parse(Application(property, mask), out _, profile: profile).Instances.Single();
    private static XElement Current(XDocument master) => master.Descendants().Single(e => e.Name.LocalName == "MaskVersion" && (string?)e.Attribute("Id") == Mask)
        .Elements().Single(e => e.Name.LocalName == "HawkConfigurationData" && e.Attribute("LegacyVersion") is null);
    private static XDocument Change(Action<XDocument> change) { var master = Master(); change(master); return master; }

    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "MaskProfile " + name); if (!ok) failures++; }
        var master = Master(); var original = master.ToString(SaveOptions.DisableFormatting);
        var master21 = Master();
        master21.Root!.Attribute("xmlns")?.Remove();
        foreach (var node in master21.Descendants()) node.Name = XNamespace.Get("http://knx.org/xml/project/21") + node.Name.LocalName;
        var admitted = ParameterProfileContext.Read(master21, Mask);
        foreach (var ns in new[] { "http://knx.org/xml/project/22", "urn:unknown:application", "" })
        {
            var app22 = Application();
            app22.Root!.Attribute("xmlns")?.Remove();
            foreach (var node in app22.Descendants()) node.Name = XNamespace.Get(ns) + node.Name.LocalName;
            var row = Ksxml.Parse(app22, out _, profile: admitted).Instances.Single();
            Check("unapproved app namespace cannot borrow matching master21: " + ns, row.IsEnabled && row.UnsupportedReason is null
                && row.Storage is { Status: ParameterStorageStatus.Supported, ObjectIndex: 6, PropertyId: 7,
                    PropertyProfile: { Selection.Status: MaskProfileStatus.UnsupportedApplicationNamespace, Selection.FirstAppObjectIndex: null,
                        Domain: null, ApplicationObjectIndex: null, DeclaredPdt: null, ElementBytes: null, TypeStatus: ParameterPropertyTypeStatus.NotDeclared } }
                && ParameterValidation.TryValidate(row, "2", out _));
        }
        foreach (var ns in new[] { "http://knx.org/xml/project/20", "http://knx.org/xml/project/21", "http://knx.org/xml/project/23" })
        {
            var nativeApp = Application(); nativeApp.Root!.Attribute("xmlns")?.Remove();
            foreach (var node in nativeApp.Descendants()) node.Name = XNamespace.Get(ns) + node.Name.LocalName;
            var row = Ksxml.Parse(nativeApp, out _, profile: admitted).Instances.Single();
            Check("admitted app namespace preserves existing facts and fingerprint: " + ns, ReferenceEquals(admitted.ForApplication(Mask, ns), admitted)
                && row.Storage.PropertyProfile is { Domain: ParameterPropertyDomain.Application, ApplicationObjectIndex: 0, ElementBytes: 2 }
                && row.Storage.PropertyProfile.Selection.Fingerprint == admitted.Selection.Fingerprint);
        }
        var context = ParameterProfileContext.Read(master, Mask);
        Check("exact current-zero profile retains explicit first index and declared counts", context.Selection is
            { Status: MaskProfileStatus.Supported, MaskVersionId: Mask, NumericMask: 1968, LegacyVersion: 0, FirstAppObjectIndex: 6 }
            && context.InterfaceObjectCount == 3 && context.DeclaredPropertyCount == 5 && context.MasterDataHash is null);
        var boundary = new[] { One(context, "ObjectIndex='5' PropertyId='7'"), One(context), One(context, "ObjectIndex='7' PropertyId='7'") };
        Check("F-1 F and F+1 preserve native indices and separate system from application zero", boundary.Select(i => i.Storage.ObjectIndex).SequenceEqual(new byte?[] { 5, 6, 7 })
            && boundary[0].Storage.PropertyProfile is { Domain: ParameterPropertyDomain.System, ApplicationObjectIndex: null }
            && boundary[1].Storage.PropertyProfile is { Domain: ParameterPropertyDomain.Application, ApplicationObjectIndex: 0 }
            && boundary[2].Storage.PropertyProfile is { Domain: ParameterPropertyDomain.Application, ApplicationObjectIndex: 1 });
        Check("an explicit first index zero remains meaningful", One(ParameterProfileContext.Read(Master("0"), Mask), "ObjectIndex='0' PropertyId='25'").Storage.PropertyProfile is
            { Domain: ParameterPropertyDomain.Application, ApplicationObjectIndex: 0, Selection.FirstAppObjectIndex: 0 });
        Check("maximum byte index does not overflow at the application boundary", One(ParameterProfileContext.Read(Master("255"), Mask), "ObjectIndex='255' PropertyId='7'").Storage.PropertyProfile is
            { Domain: ParameterPropertyDomain.Application, ApplicationObjectIndex: 0 });
        Check("declared PDT element width does not become property capacity", boundary[1].Storage is
            { PropertyProfile: { TypeStatus: ParameterPropertyTypeStatus.Known, DeclaredPdt: "PDT_UNSIGNED_INT", ElementBytes: 2 },
                Footprint.Coverage: ParameterResourceCoverage.Unknown, Resource: null });
        Check("the four observed PDT mappings retain their exact element widths", One(context, "ObjectIndex='0' PropertyId='25'").Storage.PropertyProfile?.ElementBytes == 2
            && boundary[0].Storage.PropertyProfile?.ElementBytes == 4 && One(context, "ObjectIndex='6' PropertyId='27'").Storage.PropertyProfile?.ElementBytes == 8);
        Check("unimplemented variable PDT stays a named unknown width", One(context, "ObjectIndex='6' PropertyId='9'").Storage.PropertyProfile is
            { DeclaredPdt: "PDT_VARIABLE_LENGTH", ElementBytes: null, TypeStatus: ParameterPropertyTypeStatus.Unsupported });
        Check("a missing PDT declaration never supplies a one-byte default", One(context, "ObjectIndex='6' PropertyId='78'").Storage.PropertyProfile is
            { TypeStatus: ParameterPropertyTypeStatus.NotDeclared, ElementBytes: null, Domain: ParameterPropertyDomain.Application });
        var typed = One(context, "ObjectType='200' Occurrence='2' PropertyId='7'");
        Check("by-type metadata retains occurrence without inventing an index", typed.Storage is { ObjectType: 200, Occurrence: 2, ObjectIndex: null,
            PropertyProfile: { TypeStatus: ParameterPropertyTypeStatus.Known, ElementBytes: 2, Domain: null, ApplicationObjectIndex: null } });
        var duplicateType = Change(d => Current(d).Descendants().First(e => e.Name.LocalName == "InterfaceObject").SetAttributeValue("ObjectType", "200"));
        Check("multiple objects of a type do not guess a PDT occurrence", One(ParameterProfileContext.Read(duplicateType, Mask), "ObjectType='200' PropertyId='7'").Storage.PropertyProfile is
            { TypeStatus: ParameterPropertyTypeStatus.Ambiguous, ElementBytes: null, ApplicationObjectIndex: null });
        var foreign = ParameterProfileContext.Read(master, "MV-0020");
        Check("foreign profile context cannot derive an application index or PDT", One(foreign).Storage.PropertyProfile is
            { Selection.Status: MaskProfileStatus.MaskMismatch, ApplicationMaskVersionId: Mask, Domain: null, ApplicationObjectIndex: null, ElementBytes: null });
        Check("missing app mask cannot borrow the supplied context", One(context, mask: null).Storage.PropertyProfile is
            { Selection.Status: MaskProfileStatus.MissingMaskId, Domain: null, ElementBytes: null });
        Check("mask ids are exact including complete profile suffixes", ParameterProfileContext.Read(master, Mask + "-extra").Selection.Status == MaskProfileStatus.MaskNotFound
            && ParameterProfileContext.Read(master, "mv-07b0").Selection.Status == MaskProfileStatus.MaskNotFound);
        Check("no context or unavailable metadata is additive to ordinary editing", One(null) is { IsEnabled: true, UnsupportedReason: null,
            Storage: { Status: ParameterStorageStatus.Supported, PropertyProfile.Selection.Status: MaskProfileStatus.MissingMasterData } }
            && One(ParameterProfileContext.Unavailable(Mask)) is { IsEnabled: true, Storage.PropertyProfile.Selection.Status: MaskProfileStatus.UnreadableMasterData }
            && ParameterValidation.TryValidate(One(null), "2", out _));
        Check("absent feature never assumes default one", ParameterProfileContext.Read(Change(d => Current(d).Elements().First().Remove()), Mask).Selection.Status == MaskProfileStatus.MissingFirstAppObject);
        Check("malformed negative and oversized first indices are unknown", new[] { "bad", "-1", "256" }.All(v =>
            One(ParameterProfileContext.Read(Master(v), Mask)).Storage.PropertyProfile is { Selection.Status: MaskProfileStatus.InvalidDeclaration, Domain: null }));
        Check("duplicate first feature or current set is ambiguous", ParameterProfileContext.Read(Change(d => Current(d).Elements().First().Add(new XElement(Current(d).Descendants().First(e => e.Name.LocalName == "Feature")))), Mask).Selection.Status == MaskProfileStatus.Ambiguous
            && ParameterProfileContext.Read(Change(d => Current(d).AddAfterSelf(new XElement(Current(d)))), Mask).Selection.Status == MaskProfileStatus.Ambiguous);
        Check("duplicate complete mask identity is ambiguous", ParameterProfileContext.Read(Change(d => Current(d).Parent!.AddAfterSelf(new XElement(Current(d).Parent!))), Mask).Selection.Status == MaskProfileStatus.Ambiguous);
        Check("legacy-only metadata cannot stand in for explicit current zero", ParameterProfileContext.Read(Change(d => Current(d).Remove()), Mask).Selection.Status == MaskProfileStatus.MissingHawkData);
        var currentZero = Change(d => Current(d).SetAttributeValue("LegacyVersion", "0"));
        Check("absent and explicit legacy zero describe identical selected facts", ParameterProfileContext.Read(currentZero, Mask).Selection == context.Selection);
        Check("duplicate index and duplicate property declarations are ambiguous", ParameterProfileContext.Read(Change(d => Current(d).Descendants().First(e => e.Name.LocalName == "InterfaceObject").SetAttributeValue("Index", "6")), Mask).Selection.Status == MaskProfileStatus.Ambiguous
            && ParameterProfileContext.Read(Change(d => { var p = Current(d).Descendants().First(e => e.Name.LocalName == "Property"); p.AddAfterSelf(new XElement(p)); }), Mask).Selection.Status == MaskProfileStatus.Ambiguous);
        Check("malformed selected object and numeric mask ranges reject derivation", ParameterProfileContext.Read(Change(d => Current(d).Descendants().First(e => e.Name.LocalName == "InterfaceObject").SetAttributeValue("Index", "65536")), Mask).Selection.Status == MaskProfileStatus.InvalidDeclaration
            && ParameterProfileContext.Read(Change(d => Current(d).Parent!.SetAttributeValue("MaskVersion", "65536")), Mask).Selection.Status == MaskProfileStatus.InvalidDeclaration);
        var unrelated = Change(d => d.Descendants().Last(e => e.Name.LocalName == "Feature").SetAttributeValue("Value", "9"));
        var legacyOnly = Change(d => Current(d).Parent!.Elements().Last().Descendants().Single(e => e.Name.LocalName == "Feature").SetAttributeValue("Value", "88"));
        Check("unrelated masks legacy facts and master formatting do not change the fingerprint", ParameterProfileContext.Read(unrelated, Mask).Selection == context.Selection
            && ParameterProfileContext.Read(legacyOnly, Mask).Selection == context.Selection
            && ParameterProfileContext.Read(XDocument.Parse(master.ToString()), Mask).Selection == context.Selection);
        Check("selected first-index and PDT changes change semantic equality", ParameterProfileContext.Read(Master("7"), Mask).Selection.Fingerprint != context.Selection.Fingerprint
            && ParameterProfileContext.Read(Change(d => Current(d).Descendants().First(e => e.Name.LocalName == "Property").SetAttributeValue("PropertyDataType", "PDT_UNSIGNED_LONG")), Mask).Selection.Fingerprint != context.Selection.Fingerprint);
        var copied = new ParameterInstance(boundary[1]);
        Check("copies share only immutable value-equal profile facts", ReferenceEquals(copied.Storage.PropertyProfile, boundary[1].Storage.PropertyProfile)
            && copied.Storage.PropertyProfile! with { ApplicationObjectIndex = 99 } != boundary[1].Storage.PropertyProfile);
        master.Descendants().First(e => e.Name.LocalName == "Feature").SetAttributeValue("Value", "8");
        Check("caller XML mutations cannot change an already selected context", context.Selection.FirstAppObjectIndex == 6 && One(context).Storage.PropertyProfile?.ApplicationObjectIndex == 0);
        var invalidNamespace = Master(); invalidNamespace.Root!.Name = XNamespace.Get("urn:foreign") + "KNX";
        Check("foreign namespace DTD excessive depth and oversize metadata cannot establish known facts", ParameterProfileContext.Read(invalidNamespace, Mask).Selection.Status == MaskProfileStatus.InvalidDeclaration
            && ParameterProfileContext.Read(new XDocument(new XDocumentType("KNX", null, null, null), new XElement("KNX")), Mask).Selection.Status == MaskProfileStatus.InvalidDeclaration
            && ParameterProfileContext.Read(Oversized(), Mask).Selection.Status == MaskProfileStatus.InvalidDeclaration
            && ParameterProfileContext.Read(Deep(), Mask).Selection.Status == MaskProfileStatus.InvalidDeclaration);
        var comments = Master(); comments.Root!.Add(new XComment(new string('x', 16 * 1024 * 1024 + 1)));
        Check("direct factory callers cannot bypass metadata budgets with comments", ParameterProfileContext.Read(comments, Mask).Selection.Status == MaskProfileStatus.InvalidDeclaration);
        var app = Application(); var appSource = app.ToString(SaveOptions.DisableFormatting);
        var snapshot = ApplicationSnapshot.Evaluate(app, new Dictionary<string, string?>(), profile: context);
        Check("snapshot and repeated parse propagate the same diagnosis without editing source", snapshot.IsValid
            && snapshot.Instances.Single().Storage == One(context).Storage && app.ToString(SaveOptions.DisableFormatting) == appSource
            && Master().ToString(SaveOptions.DisableFormatting) == original);
        DraftChecks(directory, Check);
        return failures;
    }

    private static XDocument Deep()
    {
        var document = Master(); var node = document.Root!;
        for (var i = 0; i < 129; i++) { var next = new XElement("X"); node.Add(next); node = next; }
        return document;
    }
    private static XDocument Oversized() { var document = Master(); document.Root!.Add(new XElement("Opaque", new string('x', 16 * 1024 * 1024 + 1))); return document; }

    private static void DraftChecks(string directory, Action<string, bool> check)
    {
        var path = Path.Combine(directory, "mask-profile-draft.knxproj");
        var project = ProjectWizard.Create(new() { Name = "Archive mask diagnostics", Areas = 1, LinesPerArea = 1 });
        for (var n = 1; n <= 2; n++) project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "MP" + n, ProductRefId = ParameterStorageChecks.ProgramId, Address = new(1, 1, (byte)n) });
        Knxproj.Save(project, path);
        check("missing archived master never falls back to installed ETS data", Knxproj.LoadMasterData(Knxproj.Load(path)) is null);
        var app = ParameterStorageChecks.Modules(); app.Descendants("ApplicationProgram").Single().SetAttributeValue("MaskVersion", Mask);
        app.Descendants("Memory").First().ReplaceWith(XElement.Parse("<Property ObjectIndex='2' PropertyId='7' Offset='0' BitOffset='0' BaseIndex='APP_MD-1_A-1'/>"));
        foreach (var node in app.Descendants()) node.Name = XNamespace.Get(Namespace) + node.Name.LocalName;
        void Write(XDocument data)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            foreach (var (name, document) in new[] { ("M-0001/" + ParameterStorageChecks.ProgramId + ".xml", app), ("knx_master.xml", data) })
            { zip.GetEntry(name)?.Delete(); using var stream = zip.CreateEntry(name).Open(); document.Save(stream); }
        }
        Write(Master());
        var vm = new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(directory, "mask-profile-catalog")))); vm.CreateProject(Knxproj.Load(path), path);
        var loaded = Knxproj.LoadMasterData(vm.Project!)!;
        var loadedContext = ParameterProfileContext.Read(loaded, Mask);
        string expectedHash;
        using (var zip = ZipFile.OpenRead(path)) using (var stream = zip.GetEntry("knx_master.xml")!.Open()) expectedHash = Convert.ToHexStringLower(SHA256.HashData(stream));
        check("archive loader attaches exact source-byte identity separately", loadedContext.MasterDataHash == expectedHash && loadedContext.Selection.Fingerprint != expectedHash);
        var devices = ProjectQuery.DeviceList(vm.Project!).ToArray(); var session = new ParameterEditingSession(vm); session.Load(new(devices[0]));
        session.Rows.Single(r => r.Instance.Id == "R-count").Value = "3";
        var scalar = session.Rows.First(r => r.Instance.Id.EndsWith("_R-1") && !r.Instance.Id.Contains("_INNER")); var id = scalar.Instance.Id; scalar.Value = "9";
        session.Load(new(devices[1]));
        var unrelated = Master(); unrelated.Descendants().Last(e => e.Name.LocalName == "Feature").SetAttributeValue("Value", "9"); Write(unrelated);
        check("external master file changes do not alter the loaded immutable archive", ParameterProfileContext.Read(Knxproj.LoadMasterData(vm.Project!), Mask).MasterDataHash == expectedHash);
        var state = typeof(Project).GetProperty("ArchiveState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        state.SetValue(vm.Project, state.GetValue(Knxproj.Load(path))); session.Load(new(devices[0]));
        check("unrelated master changes preserve count drafts against saved structure", session.Rows.Single(r => r.Instance.Id == "R-count").Value == "3"
            && session.Rows.Single(r => r.Instance.Id == id).Value == "9" && session.Rows.Where(r => r.Instance.Id.EndsWith("_R-1") && !r.Instance.Id.Contains("_INNER"))
                .Select(r => r.Instance.Storage.PropertyProfile?.ApplicationObjectIndex).SequenceEqual(new byte?[] { 96, 112, 128 }));
        session.Load(new(devices[1])); Write(Master("7")); state.SetValue(vm.Project, state.GetValue(Knxproj.Load(path))); session.Load(new(devices[0]));
        check("changed selected profile retires stale drafts despite unchanged app XML", session.Rows.Single(r => r.Instance.Id == "R-count").Value == "2"
            && session.Rows.Single(r => r.Instance.Id == id).Value == "1" && session.Rows.Single(r => r.Instance.Id == id).Instance.Storage is
                { ObjectIndex: 102, PropertyProfile.ApplicationObjectIndex: 95 });
        var saved = Path.Combine(directory, "mask-profile-saveas.knxproj"); Knxproj.Save(vm.Project!, saved);
        check("Save As retains archive master bytes and native indices", ParameterProfileContext.Read(Knxproj.LoadMasterData(Knxproj.Load(saved)), Mask).Selection.FirstAppObjectIndex == 7
            && Knxproj.LoadApplicationProgram(Knxproj.Load(saved), ParameterStorageChecks.ProgramId)!.Descendants(XNamespace.Get(Namespace) + "Property").Single().Attribute("ObjectIndex")!.Value == "2");
    }
}
