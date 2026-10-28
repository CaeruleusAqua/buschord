using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Knx;

internal static class PropertySourceChecks
{
    internal static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    internal const string AppId = "M-0001_A-0100-10-0000";
    internal const string Literal = "0100010002000000040000000000000003000A000400000003AABBE3CCFFFFFFFF04000000FEFFFFFF03000A0002000000030002FFFF";
    internal static XDocument App(string property = "ObjectIndex='3' PropertyId='10'", int bits = 3, bool signed = false) => XDocument.Parse($$"""
        <KNX xmlns="{{Ns}}"><ManufacturerData><Manufacturer RefId="M-0001"><ApplicationPrograms><ApplicationProgram Id="{{AppId}}" MaskVersion="MV-0705"><Static>
        <ParameterTypes><ParameterType Id="T"><TypeNumber Type="{{(signed ? "signedInt" : "unsignedInt")}}" SizeInBit="{{bits}}" minInclusive="{{(signed ? -(1L << (bits - 1)) : 0)}}" maxInclusive="{{(1L << (bits - (signed ? 1 : 0))) - 1}}"/></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P" ParameterType="T" Value="0"><Property {{property}} Offset="2" BitOffset="3"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="R" RefId="P"/></ParameterRefs></Static><Dynamic><ParameterRefRef RefId="R"/></Dynamic>
        </ApplicationProgram></ApplicationPrograms></Manufacturer></ManufacturerData></KNX>
        """);
    internal static XDocument Master(string? pdt = "PDT_UNSIGNED_INT", byte first = 5, ushort index = 3, ushort type = 100, ushort pid = 10) => XDocument.Parse($$"""
        <KNX xmlns="{{Ns}}"><MasterData><MaskVersions><MaskVersion Id="MV-0705" MaskVersion="1797"><HawkConfigurationData>
        <Features><Feature Name="FirstAppObjectIdx" Value="{{first}}"/></Features><InterfaceObjects>
        <InterfaceObject Index="{{index}}" ObjectType="{{type}}"><Property PropertyID="{{pid}}" {{(pdt is null ? "" : "PropertyDataType='" + pdt + "'")}}/></InterfaceObject>
        </InterfaceObjects></HawkConfigurationData></MaskVersion></MaskVersions></MasterData></KNX>
        """);
    internal static XDocument Hardware(byte[] raw) => XDocument.Parse($$"""
        <KNX xmlns="{{Ns}}"><ManufacturerData><Manufacturer RefId="M-0001"><Hardware><Hardware Id="M-0001_H-image"><Hardware2Programs>
        <Hardware2Program Id="M-0001_H-image_A-0100-10-0000" LoadedImage="{{Convert.ToBase64String(Compress(raw))}}"><ApplicationProgramRef RefId="{{AppId}}"/></Hardware2Program>
        </Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
        """);
    internal static ArchivedImageSourceChecks.Fixture Load(string directory, XDocument? app = null, XDocument? master = null, byte[]? raw = null)
        => ArchivedImageSourceChecks.Load(directory, hardware: Hardware(raw ?? Convert.FromHexString(Literal)), app: app ?? App(), master: master ?? Master());
    internal static byte[] Record(uint tag, uint offset, ushort selector, ushort pid, byte[] data, byte[]? mask)
    {
        using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer);
        writer.Write(tag); writer.Write(offset); writer.Write(selector); writer.Write(pid); writer.Write((uint)data.Length);
        writer.Write((byte)(mask is null ? 1 : 3)); writer.Write(data); if (mask is not null) writer.Write(mask); return buffer.ToArray();
    }
    internal static byte[] Container(params byte[][] records)
    {
        using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer); writer.Write(0x10001u); writer.Write(records.Length);
        foreach (var record in records) writer.Write(record); return buffer.ToArray();
    }
    internal static byte[] Frame(byte[]? data = null, byte[]? dataMask = null, byte[]? count = null, byte[]? countMask = null,
        bool omitDataMask = false, bool omitCountMask = false, bool omitCount = false,
        uint tag = 4, ushort selector = 3, byte occurrence = 0, ushort pid = 10, uint dataOffset = 0)
    {
        data ??= Convert.FromHexString("AABBE3CC"); count ??= [0, 2];
        var offset = tag == 11 ? ((uint)occurrence << 24) | dataOffset : dataOffset;
        var marker = tag == 11 ? ((uint)occurrence << 24) | 0xFFFFFE : 0xFFFFFFFE;
        var first = Record(tag, offset, selector, pid, data, omitDataMask ? null : dataMask ?? Enumerable.Repeat((byte)255, data.Length).ToArray());
        return omitCount ? Container(first) : Container(first, Record(tag, marker, selector, pid, count, omitCountMask ? null : countMask ?? [255, 255]));
    }
    private static byte[] Compress(byte[] raw)
    {
        if (raw.Length <= ushort.MaxValue)
        {
            var stored = new byte[raw.Length + 5]; stored[0] = 1; BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(1), (ushort)raw.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(3), (ushort)~raw.Length); raw.CopyTo(stored, 5); return stored;
        }
        using var output = new MemoryStream(); using (var zip = new DeflateStream(output, CompressionLevel.SmallestSize, true)) zip.Write(raw); return output.ToArray();
    }
    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "PropertySources " + name); if (!ok) failures++; }
        ArchivedPropertySourceIndex Read(ArchivedImageSourceChecks.Fixture f) => ArchivedPropertySources.Read(f.Project, f.Device);
        var f = Load(directory); var source = Read(f); var entry = source.Entries.Single(); var binding = source.Binding;
        Check("independent literal yields exact system selector and separate source components", source.Status == PropertySourceStatus.Inspected
            && entry.Selector == new PropertySourceSelector(PropertySourceKind.SystemProperty, 10, NativeObjectIndex: 3)
            && entry.DataFragments.Single().Data.ToHexString() == "AABBE3CC" && entry.CountFragment?.Data.ToHexString() == "0002");
        Check("count interpretation stays encoded source data without capacity", entry.EncodedCount == 2 && entry.CountStatus == PropertyCountStatus.FullyValid
            && entry.CountFragment?.ByteOffset is null && entry.CapacityElements is null);
        Check("PDT role metadata binds the selected profile separately", entry.PdtLayout is { DeclaredPdt: "PDT_UNSIGNED_INT", ReadElementBytes: 2, WriteElementBytes: 2, Status: PropertyPdtStatus.KnownSymmetric }
            && source.PropertyProfileSelection.Status == MaskProfileStatus.Supported && source.Fingerprint is { Length: 64 }
            && binding?.PropertyProfileFingerprint == source.PropertyProfileSelection.Fingerprint);
        var indexFp = source.Fingerprint; f.Device.ParameterValues["unrelated"] = "17";
        Check("irrelevant saved values are absent from source binding", binding?.IsCurrent(f.Project, f.Device) == true && Read(f).Fingerprint == indexFp);
        f.Device.ComObjectRefs.Add(new() { Id = "unused", ComObjectId = "unused", Number = 3 });
        Check("communication-object model edits are absent from source binding", binding?.IsCurrent(f.Project, f.Device) == true && Read(f).Fingerprint == indexFp);
        var copy = entry.DataFragments[0].Data.ToArray(); copy[2] = 0; var maskCopy = entry.CountFragment!.Mask!.ToArray(); maskCopy[0] = 0;
        Check("public copy-out arrays cannot taint supplied property bytes", entry.DataFragments[0].Data.ToHexString() == "AABBE3CC" && entry.CountFragment.Mask.ToHexString() == "FFFF");
        var noCount = Read(Load(directory, raw: Frame(omitCount: true))).Entries.Single();
        Check("missing count remains unknown rather than data-derived", noCount.CountFragment is null && noCount.EncodedCount is null && noCount.CountStatus == PropertyCountStatus.Missing);
        foreach (var mode in new[] { "null", "zero", "partial" })
        {
            var raw = Frame(omitCountMask: mode == "null", countMask: mode == "zero" ? [0, 0] : [255, 0]); var e = Read(Load(directory, raw: raw)).Entries.Single();
            Check("count mask preserves distinct source state " + mode, e.EncodedCount == 2 && (mode == "null" ? e.CountFragment!.Mask is null && e.CountStatus == PropertyCountStatus.MaskMissing
                : e.CountFragment!.Mask?.ToHexString() == (mode == "zero" ? "0000" : "FF00") && e.CountStatus == PropertyCountStatus.MaskPartial));
        }
        foreach (var size in new[] { 0, 1, 3 })
        {
            var invalid = Read(Load(directory, raw: Frame(count: new byte[size], countMask: new byte[size])));
            Check("malformed count cannot be salvaged from decoder failure " + size, invalid.Status == PropertySourceStatus.FactoryUnavailable
                && invalid.FactoryStatus == ArchivedFactoryImageStatus.InvalidData && invalid.FactoryBlobHash is { Length: 64 } && invalid.Entries.Count == 0);
        }
        var fragmented = Read(Load(directory, raw: Container(Record(4, 0, 3, 10, [1], [255]), Record(4, 3, 3, 10, [2], [255]))));
        Check("source index retains disjoint supplied fragments without filling gaps", fragmented.Status == PropertySourceStatus.Inspected
            && fragmented.Entries.Single().DataFragments.Select(d => d.ByteOffset).SequenceEqual(new uint?[] { 0, 3 }));
        var appSource = Read(Load(directory, app: App("ObjectIndex='6' PropertyId='10'"), master: Master(index: 6), raw: Frame(tag: 5, selector: 1)));
        Check("application index remains distinct from native index", appSource.Entries.Single().Selector == new PropertySourceSelector(PropertySourceKind.AppProperty, 10, ApplicationObjectIndex: 1));
        var typed = Read(Load(directory, app: App("ObjectType='123' Occurrence='7' PropertyId='10'"), master: Master(type: 123), raw: Frame(tag: 11, selector: 123, occurrence: 7)));
        Check("by-type selector retains occurrence without inventing an index", typed.Entries.Single().Selector is
            { Kind: PropertySourceKind.ByType, ObjectType: 123, Occurrence: 7, NativeObjectIndex: null, ApplicationObjectIndex: null });
        var wide = Read(Load(directory, raw: Frame(pid: 300)));
        Check("wide serialized PID is retained without a byte-width lookup", wide.Entries.Single().Selector.PropertyId == 300 && wide.Entries.Single().PdtLayout.Status == PropertyPdtStatus.Unknown);
        var aliases = Read(Load(directory, raw: Container(Record(4, 0, 6, 10, [1], [255]), Record(5, 0, 1, 10, [2], [255]))));
        Check("overlapping normalized system/app owners refuse atomically", aliases.Status == PropertySourceStatus.AmbiguousLayout && aliases.Entries.Count == 0);
        foreach (var pdt in new[] { "PDT_CONTROL", "PDT_GENERIC_02", "PDT_VARIABLE_LENGTH", "PDT_UTF8" })
        {
            var e = Read(Load(directory, master: Master(pdt))).Entries.Single();
            Check("role width adapter separates known and unknown " + pdt, pdt == "PDT_CONTROL"
                ? e.PdtLayout is { ReadElementBytes: 1, WriteElementBytes: 10, Status: PropertyPdtStatus.KnownAsymmetric }
                : pdt == "PDT_GENERIC_02" ? e.PdtLayout is { ReadElementBytes: 2, WriteElementBytes: 2, Status: PropertyPdtStatus.KnownSymmetric }
                : e.PdtLayout is { ReadElementBytes: null, WriteElementBytes: null, Status: PropertyPdtStatus.Unknown });
        }
        var absentPdt = Read(Load(directory, master: Master(null))).Entries.Single();
        Check("missing PDT does not get a one-byte default", absentPdt.PdtLayout is { DeclaredPdt: null, ReadElementBytes: null, WriteElementBytes: null });
        var otherMaster = Master(); otherMaster.Descendants(Ns + "MaskVersions").Single().Add(new XElement(Ns + "MaskVersion", new XAttribute("Id", "MV-other"), new XAttribute("MaskVersion", "1")));
        Check("unrelated mask metadata does not alter selected property semantics", Read(Load(directory, master: otherMaster)).PropertyProfileSelection.Fingerprint == source.PropertyProfileSelection.Fingerprint);
        Check("selected PDT changes the independent property profile fingerprint", Read(Load(directory, master: Master("PDT_UNSIGNED_LONG"))).PropertyProfileSelection.Fingerprint != source.PropertyProfileSelection.Fingerprint);
        var app22 = App(); app22.Root!.Attribute("xmlns")?.Remove(); foreach (var e in app22.Descendants()) e.Name = XNamespace.Get("http://knx.org/xml/project/22") + e.Name.LocalName;
        Check("unsupported application namespace has a precise source status", Read(Load(directory, app: app22)).Status == PropertySourceStatus.UnsupportedApplication);
        var detached = new DeviceInstance { Id = f.Device.Id, ProductRefId = f.Device.ProductRefId };
        Check("invalid request maps separately from missing archive source", ArchivedPropertySources.Read(f.Project, detached).Status == PropertySourceStatus.InvalidDeclaration);
        foreach (var guid in new[] { new string('x', 257), "bad\nidentity", "   " })
        {
            var invalidIdentity = Load(directory); invalidIdentity.Project.Guid = guid;
            var rejected = Read(invalidIdentity);
            Check("invalid metadata is refused before source inspection", rejected.Status == PropertySourceStatus.InvalidDeclaration
                && rejected.Binding is null && rejected.Entries.Count == 0 && rejected.FactoryBlobHash is null);
        }
        var inputType = typeof(ArchivedImageSources).GetNestedType("Inputs", BindingFlags.NonPublic)!;
        var manifestConstructor = typeof(ArchivedImageSourceManifest).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        foreach (var status in new[] { ArchivedImageSourceStatus.LimitExceeded, ArchivedImageSourceStatus.InvalidDeclaration, ArchivedImageSourceStatus.ApplicationUnavailable })
        {
            var args = manifestConstructor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray(); args[0] = status;
            var manifest = manifestConstructor.Invoke(args); var inputs = Activator.CreateInstance(inputType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [manifest, null, null], null)!;
            var mapped = (ArchivedPropertySourceIndex)typeof(ArchivedPropertySources).GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [inputs])!;
            Check("manifest source status remains precise " + status, mapped.Status == (status == ArchivedImageSourceStatus.LimitExceeded ? PropertySourceStatus.LimitExceeded
                : status == ArchivedImageSourceStatus.InvalidDeclaration ? PropertySourceStatus.InvalidDeclaration : PropertySourceStatus.ApplicationUnavailable));
        }
        Console.WriteLine($"PropertySources: {checks} checks, {failures} failures"); return failures;
    }
}
