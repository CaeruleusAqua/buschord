using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Knx;

internal static class ArchivedImageSourceChecks
{
    private const string AppId = "M-0001_A-0100-10-0000";
    private const string HardwareId = "M-0001_H-image_A-0100-10-0000";
    private const string MaskId = "MV-0705";
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private static readonly byte[] Literal = Convert.FromHexString("010001000100000001000000640000000000000002000000031234FFFF");

    internal static XDocument App() => XDocument.Parse($$"""
        <KNX xmlns="{{Ns}}"><ManufacturerData><Manufacturer RefId="M-0001"><ApplicationPrograms><ApplicationProgram Id="{{AppId}}" MaskVersion="{{MaskId}}"><Static>
        <ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="255"/></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P" ParameterType="T" Value="1"/></Parameters><ParameterRefs><ParameterRef Id="R" RefId="P"/></ParameterRefs>
        </Static><Dynamic><ParameterRefRef RefId="R"/></Dynamic></ApplicationProgram></ApplicationPrograms></Manufacturer></ManufacturerData></KNX>
        """);

    private static XDocument Master() => XDocument.Parse($$"""
        <KNX xmlns="{{Ns}}"><MasterData><MaskVersions><MaskVersion Id="{{MaskId}}" MaskVersion="1797">
        <HawkConfigurationData><Features><Feature Name="FirstAppObjectIdx" Value="5"/></Features><Resources>
        <Resource Name="ApplicationStamp" Access="remote"><Location AddressSpace="StandardMemory" StartAddress="100"/><ResourceType Length="2"/>
        <AccessRights Read="Runtime" Write="Configuration"/></Resource></Resources></HawkConfigurationData>
        </MaskVersion><MaskVersion Id="MV-other" MaskVersion="1"><HawkConfigurationData/></MaskVersion></MaskVersions></MasterData></KNX>
        """);

    private static XDocument Hardware() => XDocument.Parse($$"""
        <KNX xmlns="{{Ns}}"><ManufacturerData><Manufacturer RefId="M-0001"><Hardware><Hardware Id="M-0001_H-image">
        <Hardware2Programs><Hardware2Program Id="{{HardwareId}}" LoadedImage="{{Convert.ToBase64String(Stored(Literal))}}" CheckSums="AQIDBA==">
        <ApplicationProgramRef RefId="{{AppId}}"/></Hardware2Program></Hardware2Programs>
        </Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
        """);

    private static XElement Record(XDocument document) => document.Descendants().Single(e => e.Name.LocalName == "Hardware2Program");
    private static XElement Program(XDocument document) => document.Descendants().Single(e => e.Name.LocalName == "ApplicationProgram");
    private static XDocument ChangedHardware(Action<XElement> edit) { var document = Hardware(); edit(Record(document)); return document; }
    private static byte[] XmlBytes(XDocument document) => Encoding.UTF8.GetBytes("<?xml version='1.0' encoding='utf-8'?>\n" + document.ToString(SaveOptions.DisableFormatting));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal sealed record Fixture(Project Project, DeviceInstance Device, string Path, byte[] MasterBytes);
    internal static Fixture Load(string directory, XDocument? hardware = null, XDocument? app = null, XDocument? master = null,
        bool omitHardware = false, bool omitApp = false, bool omitMaster = false)
    {
        var project = new Project { Id = "P-image-sources", Name = "Synthetic image sources", Guid = "00000001-0000-0000-0000-000000000001" };
        var installation = new Installation { Id = "I-image" }; var area = new Area { Id = "A-image", Address = 1 }; var line = new Line { Id = "L-image", Address = 1 };
        var device = new DeviceInstance { Id = "D-image", ProductRefId = AppId, Hardware2ProgramRefId = HardwareId, Address = new(1, 1, 1) };
        line.Devices.Add(device); area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
        var path = System.IO.Path.Combine(directory, "image-sources-" + Guid.NewGuid().ToString("N") + ".knxproj");
        var masterBytes = XmlBytes(master ?? Master());
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Put(string name, byte[] data) { using var output = zip.CreateEntry(name).Open(); output.Write(data); }
            Put("0.xml", XmlBytes(Knxproj.ToXml(project)));
            if (!omitMaster) Put("knx_master.xml", masterBytes);
            if (!omitHardware) Put("M-0001/Hardware.xml", XmlBytes(hardware ?? Hardware()));
            if (!omitApp) Put("M-0001/" + AppId + ".xml", XmlBytes(app ?? App()));
        }
        var loaded = Knxproj.Load(path);
        return new(loaded, ProjectQuery.DeviceList(loaded).Single(), path, masterBytes);
    }

    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "ArchivedImageSources " + name); if (!ok) failures++; }
        ArchivedImageSourceManifest Inspect(Fixture fixture) => ArchivedImageSources.Read(fixture.Project, fixture.Device);
        ArchivedImageSourceManifest FromHardware(Action<XElement> edit) => Inspect(Load(directory, ChangedHardware(edit)));
        void HardwareRefusal(string name, Action<XElement> edit, ArchivedHardwareSourceStatus expected)
        {
            var result = FromHardware(edit);
            Check(name, result.Status == ArchivedImageSourceStatus.Inspected && result.Hardware?.Status == expected
                && result.Hardware.FactoryImage.Segments.Count == 0 && result.Hardware.ChecksumData is null);
        }

        var fixture = Load(directory); var sourceBytes = File.ReadAllBytes(fixture.Path);
        var modelXml = Knxproj.ToXml(fixture.Project).ToString(SaveOptions.DisableFormatting); var revision = fixture.Project.ApplicationDataRevision;
        var undo = new UndoStack(); undo.Reset(fixture.Project); var beforeIndex = undo.Index;
        var manifest = Inspect(fixture); var hardware = manifest.Hardware; var factory = hardware?.FactoryImage;
        var segment = factory?.Segments.SingleOrDefault(); var binding = manifest.Binding;
        Check("exact linked native hardware and application inspected", manifest.Status == ArchivedImageSourceStatus.Inspected
            && manifest.ApplicationId == AppId && manifest.ApplicationNamespace == Ns.NamespaceName && hardware is { Status: ArchivedHardwareSourceStatus.Inspected, Hardware2ProgramId: HardwareId });
        Check("manifest is a source inventory without download authority", manifest.Scope == "ArchivedImageSources" && manifest.ProfileVersion == 1
            && !manifest.CompleteApplicationImage && !manifest.DownloadEligible && binding?.ProfileVersion == 1);
        Check("literal stored-block image exposes exact root bytes and mask", factory?.Status == ArchivedFactoryImageStatus.Decoded
            && segment?.Location == new FactoryImageLocation(FactoryImageAddressSpace.StandardMemory, FactoryImageComponent.Data, 100)
            && segment.Data.ToHexString() == "1234" && segment.Mask?.ToHexString() == "FFFF");
        Check("checksum bytes are opaque and unverified", hardware?.ChecksumStatus == ArchivedChecksumSourceStatus.Present
            && hardware.ChecksumData?.ToHexString() == "01020304" && factory?.StampVerified == false);
        Check("compressed and decoded source hashes identify different buffers", factory?.BlobHash == Hash(Stored(Literal)) && factory.DecodedHash == Hash(Literal)
            && factory.BlobHash != factory.DecodedHash);
        Check("raw master hash is the original ZIP entry hash", manifest.ResourceContext?.MasterDataHash == Hash(fixture.MasterBytes));
        Check("serialized and selected master fingerprints remain separate", manifest.ResourceContext is { DocumentHash.Length: 64, Fingerprint.Length: 64 } context
            && context.MasterDataHash != context.DocumentHash && context.DocumentHash != context.Fingerprint);
        Check("application and selected hardware definitions have independent hashes", manifest.ApplicationDefinitionHash is { Length: 64 }
            && hardware?.DefinitionHash is { Length: 64 } && manifest.ApplicationDefinitionHash != hardware.DefinitionHash);
        Check("selected resources are independently bound", manifest.ResourceContext is { Status: ArchivedResourceContextStatus.Supported, Resources.Count: 1 }
            && binding?.ResourceContextFingerprint == manifest.ResourceContext.Fingerprint && binding.IsCurrent(fixture.Project, fixture.Device));
        Check("inspection does not mutate archive model revision or undo", sourceBytes.SequenceEqual(File.ReadAllBytes(fixture.Path))
            && Knxproj.ToXml(fixture.Project).ToString(SaveOptions.DisableFormatting) == modelXml && revision == fixture.Project.ApplicationDataRevision
            && undo.Steps == 0 && undo.Index == beforeIndex && !undo.CanUndo && !undo.IsDirty);
        if (segment is not null && hardware?.ChecksumData is { } checksum)
        {
            var data = segment.Data.ToArray(); var mask = segment.Mask!.ToArray(); var stamp = checksum.ToArray(); data[0] = 0; mask[0] = 0; stamp[0] = 0;
            Check("copy-out arrays cannot mutate image mask or checksum", segment.Data.ToHexString() == "1234" && segment.Mask.ToHexString() == "FFFF" && checksum.ToHexString() == "01020304");
            Check("segment collection is immutable", factory!.Segments is not FactoryImageSegment[] && ((IList<FactoryImageSegment>)factory.Segments).IsReadOnly);
        }
        else Check("immutable buffer fixture is available", false);

        var detached = Knxproj.LoadHardwareData(fixture.Project, HardwareId)!; Record(detached).SetAttributeValue("CheckSums", "AA==");
        Check("hardware loader returns detached documents", (string?)Record(Knxproj.LoadHardwareData(fixture.Project, HardwareId)!).Attribute("CheckSums") == "AQIDBA==");
        Check("hardware loader does not cross manufacturer parts", Knxproj.LoadHardwareData(fixture.Project, "M-0002_H-other") is null);
        foreach (var invalid in new[] { "", "M-000G_H-1", "M-0001x", "../M-0001_H-1" })
            Check("hardware loader refuses invalid manufacturer key " + invalid, Knxproj.LoadHardwareData(fixture.Project, invalid) is null);

        var unrelatedMaster = Master(); unrelatedMaster.Descendants(Ns + "MaskVersion").Last().SetAttributeValue("MaskVersion", "2");
        var unrelated = Inspect(Load(directory, master: unrelatedMaster));
        Check("unselected master facts change provenance without selected binding drift", unrelated.ResourceContext?.MasterDataHash != manifest.ResourceContext?.MasterDataHash
            && unrelated.ResourceContext?.DocumentHash != manifest.ResourceContext?.DocumentHash && unrelated.ResourceContext?.Fingerprint == manifest.ResourceContext?.Fingerprint
            && unrelated.Binding?.Fingerprint == binding?.Fingerprint);
        var relevantMaster = Master(); relevantMaster.Descendants(Ns + "Location").Single().SetAttributeValue("StartAddress", "101");
        var relevant = Inspect(Load(directory, master: relevantMaster));
        Check("selected resource geometry changes only the new source binding", relevant.ResourceContext?.Fingerprint != manifest.ResourceContext?.Fingerprint && relevant.Binding?.Fingerprint != binding?.Fingerprint);
        var unselectedHardware = Hardware(); Record(unselectedHardware).Parent!.Add(new XElement(Ns + "Hardware2Program", new XAttribute("Id", "M-0001_H-other"), new XAttribute("CheckSums", "AA==")));
        var otherHardware = Inspect(Load(directory, unselectedHardware));
        Check("unselected hardware records do not replace selected definition", otherHardware.Hardware?.DefinitionHash == hardware?.DefinitionHash && otherHardware.Binding?.Fingerprint == binding?.Fingerprint);
        var changedStamp = FromHardware(r => r.SetAttributeValue("CheckSums", "BQY="));
        Check("changed checksum source changes selected definition and manifest binding", changedStamp.Hardware?.ChecksumData?.ToHexString() == "0506"
            && changedStamp.Hardware.DefinitionHash != hardware?.DefinitionHash && changedStamp.Binding?.Fingerprint != binding?.Fingerprint);
        var appComment = App(); Program(appComment).Add(new XComment("another explicit application definition"));
        Check("application definition participates in source binding", Inspect(Load(directory, app: appComment)).Binding?.Fingerprint != binding?.Fingerprint);

        var absentImage = FromHardware(r => r.Attribute("LoadedImage")!.Remove());
        Check("absent image does not fabricate source bytes", absentImage.Hardware is { LoadedImageDeclared: false, FactoryImage.Status: ArchivedFactoryImageStatus.Missing, FactoryImage.Segments.Count: 0 });
        var emptyImage = FromHardware(r => r.SetAttributeValue("LoadedImage", ""));
        Check("declared empty image remains distinguishable from absence", emptyImage.Hardware is { LoadedImageDeclared: true, FactoryImage.Status: ArchivedFactoryImageStatus.Missing, FactoryImage.Segments.Count: 0 }
            && emptyImage.Binding?.Fingerprint != absentImage.Binding?.Fingerprint);
        var emptyContainer = FromHardware(r => r.SetAttributeValue("LoadedImage", Convert.ToBase64String(Stored(Convert.FromHexString("0100010000000000")))));
        Check("explicit empty serialized image is decoded without inventing segments", emptyContainer.Hardware?.FactoryImage is { Status: ArchivedFactoryImageStatus.Decoded, Segments.Count: 0 });
        foreach (var value in new[] { "!", "A", "AA=A", "AAAA====" })
        {
            var invalid = FromHardware(r => r.SetAttributeValue("LoadedImage", value));
            Check("invalid image base64 is atomic " + value, invalid.Hardware?.FactoryImage is { Status: ArchivedFactoryImageStatus.InvalidData, Segments.Count: 0 });
        }
        var wrongContainer = FromHardware(r => r.SetAttributeValue("LoadedImage", Convert.ToBase64String(Stored(Convert.FromHexString("0200010000000000")))));
        Check("unrecognized decoded format is retained as unavailable", wrongContainer.Hardware?.FactoryImage is { Status: ArchivedFactoryImageStatus.UnsupportedFormat, Segments.Count: 0 });
        var excessiveBlob = Convert.ToBase64String(new byte[1024 * 1024 + 1]);
        var oversized = FromHardware(r => r.SetAttributeValue("LoadedImage", excessiveBlob));
        Check("image byte budget applies before decode publication", oversized.Hardware?.FactoryImage is { Status: ArchivedFactoryImageStatus.LimitExceeded, Segments.Count: 0 });
        var absentStamp = FromHardware(r => r.Attribute("CheckSums")!.Remove());
        Check("absent checksums stay unknown", absentStamp.Hardware is { ChecksumStatus: ArchivedChecksumSourceStatus.Missing, ChecksumData: null });
        var emptyStamp = FromHardware(r => r.SetAttributeValue("CheckSums", ""));
        Check("empty declared checksum is opaque present zero bytes", emptyStamp.Hardware is { ChecksumStatus: ArchivedChecksumSourceStatus.Present, ChecksumData.Length: 0 });
        var invalidStamp = FromHardware(r => r.SetAttributeValue("CheckSums", "!!"));
        Check("invalid checksum has no fabricated bytes or verification", invalidStamp.Hardware is { ChecksumStatus: ArchivedChecksumSourceStatus.InvalidData, ChecksumData: null, FactoryImage.StampVerified: false });
        var oversizedStamp = FromHardware(r => r.SetAttributeValue("CheckSums", excessiveBlob));
        Check("checksum budget has no partial byte result", oversizedStamp.Hardware is { ChecksumStatus: ArchivedChecksumSourceStatus.LimitExceeded, ChecksumData: null });
        var spaced = FromHardware(r => { r.SetAttributeValue("LoadedImage", " \t" + (string?)r.Attribute("LoadedImage") + "\r\n"); r.SetAttributeValue("CheckSums", "AQID\r\nBA=="); });
        Check("XML whitespace in base64 preserves exact payload bytes", spaced.Hardware?.FactoryImage.BlobHash == factory?.BlobHash && spaced.Hardware?.ChecksumData?.ToHexString() == "01020304");

        HardwareRefusal("selected record must have the exact linked id", r => r.SetAttributeValue("Id", "M-0001_H-other"), ArchivedHardwareSourceStatus.ReferenceNotFound);
        HardwareRefusal("duplicate exact linked records are ambiguous", r => r.Parent!.Add(new XElement(r)), ArchivedHardwareSourceStatus.AmbiguousReference);
        HardwareRefusal("foreign selected record namespace is refused", r => r.Name = XNamespace.Get("urn:foreign") + "Hardware2Program", ArchivedHardwareSourceStatus.InvalidDeclaration);
        HardwareRefusal("mixed namespace duplicate cannot bypass ambiguity", r => { var copy = new XElement(r); copy.Name = XNamespace.Get("urn:foreign") + "Hardware2Program"; r.Parent!.Add(copy); }, ArchivedHardwareSourceStatus.AmbiguousReference);
        HardwareRefusal("wrong app reference cannot attach another app image", r => r.Element(Ns + "ApplicationProgramRef")!.SetAttributeValue("RefId", "M-0001_A-other"), ArchivedHardwareSourceStatus.ApplicationMismatch);
        HardwareRefusal("missing app reference stays unavailable", r => r.RemoveNodes(), ArchivedHardwareSourceStatus.ApplicationMismatch);
        HardwareRefusal("duplicate app references close the scope", r => r.Add(new XElement(r.Elements().Single())), ArchivedHardwareSourceStatus.MultipleApplications);
        HardwareRefusal("two distinct app references close the scope", r => r.Add(new XElement(Ns + "ApplicationProgramRef", new XAttribute("RefId", "M-0001_A-other"))), ArchivedHardwareSourceStatus.MultipleApplications);
        HardwareRefusal("foreign app reference is not silently native", r => r.Elements().Single().Name = XNamespace.Get("urn:foreign") + "ApplicationProgramRef", ArchivedHardwareSourceStatus.InvalidDeclaration);
        HardwareRefusal("mixed namespace app references are refused", r => r.Add(new XElement(XNamespace.Get("urn:foreign") + "ApplicationProgramRef", new XAttribute("RefId", AppId))), ArchivedHardwareSourceStatus.InvalidDeclaration);
        HardwareRefusal("hardware manufacturer must match the linked record manufacturer", r => r.Ancestors(Ns + "Manufacturer").Single().SetAttributeValue("RefId", "M-0002"), ArchivedHardwareSourceStatus.InvalidDeclaration);
        HardwareRefusal("selected hardware manufacturer must be unique", r => r.Ancestors(Ns + "Manufacturer").Single().Parent!.Add(new XElement(Ns + "Manufacturer", new XAttribute("RefId", "M-0001"), new XElement(Ns + "Hardware"))), ArchivedHardwareSourceStatus.InvalidDeclaration);
        HardwareRefusal("hardware record container must be native Hardware2Programs", r => r.Parent!.Name = Ns + "Wrapper", ArchivedHardwareSourceStatus.InvalidDeclaration);
        HardwareRefusal("hardware ancestry cannot skip the native hardware container", r => r.Parent!.Parent!.Parent!.Name = Ns + "Wrapper", ArchivedHardwareSourceStatus.InvalidDeclaration);
        HardwareRefusal("an extra wrapper cannot provide hardware record authority", r => r.ReplaceWith(new XElement(Ns + "Wrapper", new XElement(r))), ArchivedHardwareSourceStatus.InvalidDeclaration);
        HardwareRefusal("unknown app-reference child is not ignored", r => r.Add(new XElement(Ns + "Wrapper", new XElement(Ns + "ApplicationProgramRef", new XAttribute("RefId", AppId)))), ArchivedHardwareSourceStatus.InvalidDeclaration);
        var namespaceHardware = Hardware(); RenameNamespace(namespaceHardware, "http://knx.org/xml/project/22");
        Check("unapproved hardware namespace stays unavailable", Inspect(Load(directory, namespaceHardware)).Hardware?.Status == ArchivedHardwareSourceStatus.UnsupportedNamespace);
        var deepHardware = Hardware(); var bottom = deepHardware.Root!;
        for (var depth = 0; depth < 129; depth++) { var wrapper = new XElement(Ns + "Wrapper"); bottom.Add(wrapper); bottom = wrapper; }
        var depthFixture = Load(directory, deepHardware); var depthSource = File.ReadAllBytes(depthFixture.Path); var depthManifest = Inspect(depthFixture);
        Check("depth129 hardware becomes a controlled unreadable source without partial image", depthManifest.Status == ArchivedImageSourceStatus.Inspected
            && depthManifest.Hardware is { Status: ArchivedHardwareSourceStatus.Unreadable, FactoryImage.Segments.Count: 0, ChecksumData: null }
            && depthSource.SequenceEqual(File.ReadAllBytes(depthFixture.Path)));
        var missingPart = Inspect(Load(directory, omitHardware: true));
        Check("missing hardware part does not fall back to external catalog", missingPart.Hardware is { Status: ArchivedHardwareSourceStatus.PartMissing, FactoryImage.Segments.Count: 0 });
        var missingLink = Load(directory); missingLink.Device.Hardware2ProgramRefId = null;
        Check("no hardware link keeps the app inspectable with missing source", Inspect(missingLink).Hardware is { Status: ArchivedHardwareSourceStatus.ReferenceMissing, FactoryImage.Segments.Count: 0 });
        var otherOnly = Hardware(); Record(otherOnly).SetAttributeValue("Id", "M-0001_H-other");
        Check("different H2P with image is never a fallback", Inspect(Load(directory, otherOnly)).Hardware is { Status: ArchivedHardwareSourceStatus.ReferenceNotFound, FactoryImage.Segments.Count: 0 });
        var appMissing = Inspect(Load(directory, omitApp: true));
        Check("missing linked application publishes no manifest binding", appMissing is { Status: ArchivedImageSourceStatus.ApplicationUnavailable, Hardware: null, Binding: null });
        var noMaster = Inspect(Load(directory, omitMaster: true));
        Check("missing master remains a separate unresolved source", noMaster.ResourceContext?.Status == ArchivedResourceContextStatus.MissingMasterData && noMaster.Hardware?.FactoryImage.Status == ArchivedFactoryImageStatus.Decoded);
        foreach (var kind in new[] { "manufacturer", "container", "ancestry", "wrapper", "duplicate manufacturer" })
        {
            var app = App(); var program = Program(app);
            if (kind == "manufacturer") program.Ancestors(Ns + "Manufacturer").Single().SetAttributeValue("RefId", "M-0002");
            else if (kind == "container") program.Parent!.Name = Ns + "Wrapper";
            else if (kind == "ancestry") program.Parent!.Parent!.Parent!.Name = Ns + "Wrapper";
            else if (kind == "wrapper") program.ReplaceWith(new XElement(Ns + "Wrapper", new XElement(program)));
            else program.Ancestors(Ns + "Manufacturer").Single().Parent!.Add(new XElement(Ns + "Manufacturer", new XAttribute("RefId", "M-0001"), new XElement(Ns + "ApplicationPrograms")));
            Check("invalid application " + kind + " cannot publish source authority", Inspect(Load(directory, app: app)) is
                { Status: ArchivedImageSourceStatus.InvalidDeclaration, Hardware: null, Binding: null });
        }
        var renamedApp = App(); Program(renamedApp).SetAttributeValue("Id", "M-0001_A-other");
        var renamedHardware = ChangedHardware(r => r.Element(Ns + "ApplicationProgramRef")!.SetAttributeValue("RefId", "M-0001_A-other"));
        var renamed = Inspect(Load(directory, renamedHardware, renamedApp));
        Check("matching renamed app and H2P ref cannot override the immutable entry identity", renamed is
            { Status: ArchivedImageSourceStatus.InvalidDeclaration, Hardware: null, Binding: null });
        var renamedDirect = Load(directory, app: renamedApp); renamedDirect.Device.Hardware2ProgramRefId = null;
        Check("direct app link also requires the native id to match its archive entry", Inspect(renamedDirect) is
            { Status: ArchivedImageSourceStatus.InvalidDeclaration, Hardware: null, Binding: null });
        foreach (var kind in new[] { "foreign", "duplicate", "mixed" })
        {
            var app = App(); var program = Program(app);
            if (kind == "foreign") program.Name = XNamespace.Get("urn:foreign") + "ApplicationProgram";
            else { var copy = new XElement(program); if (kind == "mixed") copy.Name = XNamespace.Get("urn:foreign") + "ApplicationProgram"; program.Parent!.Add(copy); }
            var bad = Inspect(Load(directory, app: app));
            Check(kind + " application records cannot publish source authority", bad is { Status: ArchivedImageSourceStatus.InvalidDeclaration, Hardware: null, Binding: null });
        }
        var app22 = App(); RenameNamespace(app22, "http://knx.org/xml/project/22");
        Check("project23 does not admit app22 image mapping implicitly", Inspect(Load(directory, app: app22)) is { Status: ArchivedImageSourceStatus.UnsupportedApplicationNamespace, Binding: null });

        if (binding is not null)
        {
            fixture.Device.ParameterValues["R"] = "2"; fixture.Device.ComObjectRefs.Add(new() { Id = "O", ComObjectId = "O" });
            Check("source-only binding does not claim value or link activation", binding.IsCurrent(fixture.Project, fixture.Device));
            fixture.Device.ParameterValues.Clear(); fixture.Device.ComObjectRefs.Clear();
            fixture.Device.Address = new(1, 1, 2); Check("address edit stales source binding", !binding.IsCurrent(fixture.Project, fixture.Device)); fixture.Device.Address = new(1, 1, 1);
            fixture.Device.ProductRefId = "M-0001_A-other"; Check("product edit stales source binding", !binding.IsCurrent(fixture.Project, fixture.Device)); fixture.Device.ProductRefId = AppId;
            fixture.Device.Hardware2ProgramRefId = "M-0001_H-other"; Check("hardware link edit stales source binding", !binding.IsCurrent(fixture.Project, fixture.Device)); fixture.Device.Hardware2ProgramRefId = HardwareId;
            fixture.Project.Guid = "other-guid"; Check("project identity edit stales source binding", !binding.IsCurrent(fixture.Project, fixture.Device)); fixture.Project.Guid = "00000001-0000-0000-0000-000000000001";
            var line = fixture.Project.Installations.Single().Areas.Single().Lines.Single(); line.Devices.Remove(fixture.Device);
            Check("removed device is not a current source", !binding.IsCurrent(fixture.Project, fixture.Device) && Inspect(fixture).Status == ArchivedImageSourceStatus.InvalidRequest); line.Devices.Add(fixture.Device);
            var twin = new DeviceInstance { Id = fixture.Device.Id, ProductRefId = AppId, Hardware2ProgramRefId = HardwareId, Address = fixture.Device.Address };
            Check("matching detached device has no live identity authority", !binding.IsCurrent(fixture.Project, twin) && ArchivedImageSources.Read(fixture.Project, twin).Status == ArchivedImageSourceStatus.InvalidRequest);
            var reloaded = Knxproj.Load(fixture.Path);
            Check("reloaded matching project is a different live source", !binding.IsCurrent(reloaded, ProjectQuery.DeviceList(reloaded).Single()));
            Check("restored unchanged live source revalidates", binding.IsCurrent(fixture.Project, fixture.Device));
            File.WriteAllBytes(fixture.Path, [1, 2, 3]);
            var retained = Inspect(fixture);
            Check("external archive overwrite cannot replace immutable loaded sources", retained.Hardware?.FactoryImage.BlobHash == factory?.BlobHash && retained.Binding?.Fingerprint == binding.Fingerprint
                && binding.IsCurrent(fixture.Project, fixture.Device) && Knxproj.LoadHardwareData(fixture.Project, HardwareId) is not null);
            File.WriteAllBytes(fixture.Path, sourceBytes);
            // A dedicated import replaces this internal immutable snapshot; the test changes no public API.
            var archive = typeof(Project).GetProperty("ArchiveState", BindingFlags.Instance | BindingFlags.NonPublic)!;
            archive.SetValue(fixture.Project, archive.GetValue(Knxproj.Load(fixture.Path)));
            Check("new archive revision stales old binding even for identical bytes", fixture.Project.ApplicationDataRevision > revision && !binding.IsCurrent(fixture.Project, fixture.Device));
        }
        else Check("source binding is available for stale checks", false);
        var undoFixture = Load(directory); var history = new UndoStack(); history.Reset(undoFixture.Project); var oldBinding = Inspect(undoFixture).Binding;
        undoFixture.Device.ParameterValues["R"] = "2"; history.Push(undoFixture.Project); var undone = history.Undo()!;
        var undoneDevice = ProjectQuery.DeviceList(undone).Single(); var undoneManifest = ArchivedImageSources.Read(undone, undoneDevice);
        Check("undo restores immutable archive inputs without reviving old live authority", oldBinding is not null && !oldBinding.IsCurrent(undone, undoneDevice)
            && undoneManifest.Hardware?.FactoryImage.BlobHash == factory?.BlobHash && undoneManifest.Binding?.IsCurrent(undone, undoneDevice) == true);
        var redone = history.Redo()!; var redoneDevice = ProjectQuery.DeviceList(redone).Single();
        Check("redo also needs its own live binding", oldBinding is not null && !oldBinding.IsCurrent(redone, redoneDevice)
            && ArchivedImageSources.Read(redone, redoneDevice).Binding?.IsCurrent(redone, redoneDevice) == true);
        Console.WriteLine($"ArchivedImageSources: {checks - failures}/{checks} passed");
        return failures;
    }

    private static void RenameNamespace(XDocument document, string value)
    {
        document.Root!.Attribute("xmlns")?.Remove();
        foreach (var element in document.Descendants()) element.Name = XNamespace.Get(value) + element.Name.LocalName;
    }
    private static byte[] Stored(byte[] raw)
    {
        var result = new byte[raw.Length + 5]; result[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(1), checked((ushort)raw.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(3), unchecked((ushort)~raw.Length));
        raw.CopyTo(result, 5); return result;
    }
}
