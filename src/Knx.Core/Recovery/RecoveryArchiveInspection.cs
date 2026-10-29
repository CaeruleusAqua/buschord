using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Knx.Recovery;

internal sealed record RecoveryArchiveInspection(string? NestedEntry, ProjectEntryLayout? Layout,
    string Format, IReadOnlySet<string>? NativeIds, string? ProjectId, string? ProjectGuid);

internal static class RecoveryArchives
{
    internal static RecoveryArchiveInspection Inspect(byte[] bytes, string? expectedNested, bool inferNested = false)
    {
        try { return InspectCore(bytes, expectedNested, inferNested); }
        catch (Exception error) when (error is InvalidDataException or XmlException or OverflowException or ArgumentException)
        { throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.InvalidArchive", error); }
    }
    private static RecoveryArchiveInspection InspectCore(byte[] bytes, string? expectedNested, bool inferNested)
    {
        PlainFlags(bytes);
        using var source = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(source, ZipArchiveMode.Read);
        var names = Names(zip);
        var layout = ProjectEntryLayout.Find(names);
        var nested = names.Where(name => name.StartsWith("P-", StringComparison.Ordinal) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !name.Contains('/')).ToArray();
        if (names.Count == 0)
        {
            if (expectedNested is not null) throw Bad("Recovery.EmptyArchiveNestedMismatch");
            return new(null, null, "empty", null, null, null); // Source created by WithApplications for a new editor project.
        }
        if (layout is not null)
        {
            if (nested.Length != 0 || expectedNested is not null) throw Bad("Recovery.AmbiguousArchiveLayout");
            return Identified(zip, layout, null);
        }
        if (nested.Length != 1 || !inferNested && nested[0] != expectedNested) throw Bad("Recovery.NestedArchiveMismatch");
        var innerBytes = ReadEntry(zip.GetEntry(nested[0])!, 64 * 1024 * 1024);
        PlainFlags(innerBytes);
        using var innerSource = new MemoryStream(innerBytes, false);
        using var inner = new ZipArchive(innerSource, ZipArchiveMode.Read);
        var innerNames = Names(inner);
        if (innerNames.Any(name => name.StartsWith("P-", StringComparison.Ordinal) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !name.Contains('/')))
            throw Bad("Recovery.NestedArchiveTooDeep");
        var innerLayout = ProjectEntryLayout.Find(innerNames) ?? throw Bad("Recovery.MissingProjectLayout");
        var result = Identified(inner, innerLayout, nested[0]);
        var outerInfo = zip.GetEntry("project.xml");
        if (outerInfo is not null)
        {
            var information = ReadXml(ReadEntry(outerInfo, 16 * 1024 * 1024));
            innerLayout.Validate(ReadXml(ReadEntry(inner.GetEntry(innerLayout.DataEntry)!, 16 * 1024 * 1024)), information);
            if (inner.GetEntry(innerLayout.InformationEntry) is null && ProjectFormats.IsNative(information.Root?.Name.Namespace))
            {
                var outerGuid = InformationGuid(information);
                if (result.ProjectGuid is null) result = result with { ProjectGuid = outerGuid };
            }
        }
        return result;
    }

    private static RecoveryArchiveInspection Identified(ZipArchive zip, ProjectEntryLayout layout, string? nested)
    {
        var data = ReadXml(ReadEntry(zip.GetEntry(layout.DataEntry) ?? throw Bad("Recovery.MissingProjectXml"), 16 * 1024 * 1024));
        var information = zip.GetEntry(layout.InformationEntry);
        var informationXml = information is null ? null : ReadXml(ReadEntry(information, 16 * 1024 * 1024));
        layout.Validate(data, informationXml);
        var format = ProjectFormats.Of(data.Root!);
        var project = data.Root!.Element(data.Root.Name.Namespace + "Project")!;
        var guid = (string?)project.Attribute("Guid");
        if (format.IsNative())
        {
            var inlineGuid = InformationGuid(data);
            var separateGuid = informationXml is null ? null : InformationGuid(informationXml);
            guid ??= inlineGuid;
            guid ??= separateGuid;
        }
        return new(nested, layout, format.Namespace().NamespaceName,
            format.IsNative() ? data.Descendants().Attributes("Id").Select(attribute => attribute.Value).ToHashSet(StringComparer.Ordinal) : null,
            (string?)project.Attribute("Id"), guid);
    }
    private static string? InformationGuid(XDocument document)
    {
        var root = document.Root!;
        var infos = root.Element(root.Name.Namespace + "Project")?.Elements(root.Name.Namespace + "ProjectInformation").Take(2).ToArray() ?? [];
        if (infos.Length > 1) throw Bad("Recovery.AmbiguousProjectInformation");
        return infos.Length == 0 ? null : (string?)infos[0].Attribute("Guid");
    }
    private static List<string> Names(ZipArchive zip)
    {
        if (zip.Entries.Count > 4096) throw Limit("Recovery.ArchiveTooManyEntries");
        var names = new HashSet<string>(StringComparer.Ordinal); long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            if (!names.Add(entry.FullName) || entry.Length > ArchiveEntryLimits.For(entry.FullName)) throw Bad("Recovery.InvalidArchiveEntry");
            expanded = checked(expanded + entry.Length);
            if (expanded > 256L * 1024 * 1024) throw Limit("Recovery.ArchiveExpansionLimit");
        }
        return names.ToList();
    }

    // Inspect BOTH local and central flags before any source/snapshot content is persisted.
    // No payload decryption, passwords, vendor code, or filename-based encryption exceptions.
    private static void PlainFlags(byte[] data)
    {
        if (data.Length > RecoveryBlobStore.MaximumBlobBytes) throw Limit("Recovery.ArchiveTooLarge");
        int end = -1;
        for (var offset = data.Length - 22; offset >= Math.Max(0, data.Length - 65557); offset--)
            if (U32(offset) == 0x06054b50 && offset + 22 + U16(offset + 20) == data.Length) { end = offset; break; }
        if (end < 0) throw Bad("Recovery.InvalidZip");
        var count = U16(end + 10); var central = U32(end + 16); var size = U32(end + 12);
        if (count > 4096 || U16(end + 4) != 0 || U16(end + 6) != 0 || U16(end + 8) != count || (long)central + size != end)
            throw Bad("Recovery.UnsupportedZipLayout");
        var cursor = checked((int)central);
        for (var index = 0; index < count; index++)
        {
            if ((long)cursor + 46 > end || U32(cursor) != 0x02014b50) throw Bad("Recovery.InvalidZipDirectory");
            var local = U32(cursor + 42);
            if ((long)local + 30 > central || U32(checked((int)local)) != 0x04034b50) throw Bad("Recovery.InvalidZipLocalHeader");
            var localOffset = checked((int)local);
            if ((U16(cursor + 8) & 0x2041) != 0 || (U16(localOffset + 6) & 0x2041) != 0
                || U16(cursor + 10) == 99 || U16(localOffset + 8) == 99)
                throw RecoveryWire.Reject(RecoveryFailure.ProtectedSource, "Recovery.ProtectedSource");
            var next = (long)cursor + 46 + U16(cursor + 28) + U16(cursor + 30) + U16(cursor + 32);
            if (next > end) throw Bad("Recovery.InvalidZipDirectory");
            cursor = (int)next;
        }
        if (cursor != end) throw Bad("Recovery.InvalidZipDirectory");
        ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at, 2));
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4));
    }
    internal static XDocument ReadXml(byte[] bytes)
    {
        if (bytes.Length > 16 * 1024 * 1024) throw Limit("Recovery.XmlTooLarge");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 };
        using var input = new MemoryStream(bytes, false);
        using (var check = XmlReader.Create(input, settings))
        {
            var nodes = 0;
            while (check.Read())
            { nodes = checked(nodes + 1 + check.AttributeCount); if (check.Depth > 128 || nodes > 1000000) throw Limit("Recovery.XmlComplexityLimit"); }
        }
        input.Position = 0;
        using var reader = XmlReader.Create(input, settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
    private static byte[] ReadEntry(ZipArchiveEntry entry, int maximum)
    {
        if (entry.Length < 0 || entry.Length > maximum) throw Limit("Recovery.ArchiveEntryTooLarge");
        using var input = entry.Open(); var bytes = new byte[checked((int)entry.Length)]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw Bad("Recovery.ArchiveEntryLengthChanged");
        return bytes;
    }
    private static RecoveryRejectedException Bad(string code) => RecoveryWire.Reject(RecoveryFailure.Corrupt, code);
    private static RecoveryRejectedException Limit(string code) => RecoveryWire.Reject(RecoveryFailure.LimitExceeded, code);
}
