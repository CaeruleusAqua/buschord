using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Knx.Catalog;

namespace Knx;

internal sealed record ArchivePartIdentity(string EntryPath);

// Immutable archive bytes are shared by undo snapshots, not copied per edit.
internal sealed record ArchiveState(byte[] Bytes, string? NestedEntry, bool Encrypted = false,
    IReadOnlyDictionary<string, byte[]>? ApplicationParts = null, IReadOnlySet<string>? NativeObjectIds = null,
    IReadOnlyDictionary<string, byte[]>? GeneratedParts = null, ProjectEntryLayout? Layout = null)
{
    private const int MaxArchiveBytes = 128 * 1024 * 1024;
    private const int MaxEntryBytes = 64 * 1024 * 1024;
    private const int MaxXmlBytes = 16 * 1024 * 1024;
    private const int MaxApplicationXmlBytes = ArchiveEntryLimits.ApplicationXml;
    private const string Supported = "Supported format: one project at root 0.xml, in one P-*.zip, or in one native /20 or /22 P-*/ directory; ZIP64 and multi-volume archives are not supported.";

    /// <summary>Immutable application overlay prepared before committing a product migration.</summary>
    internal static ArchiveState WithApplications(ArchiveState? state, params XDocument[] applications)
    {
        if (state is null)
        {
            using var empty = new MemoryStream();
            using (new ZipArchive(empty, ZipArchiveMode.Create, true)) { }
            state = new ArchiveState(empty.ToArray(), null);
        }
        var parts = state.GeneratedParts is null ? new Dictionary<string, byte[]>(StringComparer.Ordinal)
            : new Dictionary<string, byte[]>(state.GeneratedParts, StringComparer.Ordinal);
        foreach (var document in applications)
        {
            var programs = document.Descendants().Where(e => e.Name.LocalName == "ApplicationProgram").ToArray();
            if (programs.Length != 1) throw new InvalidDataException("Exactly one application program is required for migration.");
            var id = (string?)programs[0].Attribute("Id") ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^M-[0-9a-fA-F]{4}_A-[A-Za-z0-9_.-]+$"))
                throw new InvalidDataException("Invalid application archive identity.");
            var entry = Knxproj.ApplicationProgramEntryPath(id)!;
            var bytes = XmlBytes(document, MaxApplicationXmlBytes);
            if (bytes.Length > MaxApplicationXmlBytes) throw new InvalidDataException("Application XML exceeds the archive limit.");
            var existing = parts.TryGetValue(entry, out var added) ? ReadXml(added, MaxApplicationXmlBytes)
                : LoadPart(state, entry, true, null);
            if (existing is not null)
            {
                var declared = existing.Descendants().Where(e => e.Name.LocalName == "ApplicationProgram").ToArray();
                if (declared.Length != 1 || !SameXml(declared[0], programs[0]))
                    throw new InvalidDataException("An application with this identity already has different declarations.");
                continue;
            }
            parts.Add(entry, bytes);
        }
        if (parts.Count > 4096 || parts.Values.Sum(p => (long)p.Length) > MaxArchiveBytes)
            throw new InvalidDataException("Application overlay exceeds the archive limit.");
        return state with { GeneratedParts = new System.Collections.ObjectModel.ReadOnlyDictionary<string, byte[]>(parts) };
    }

    /// <summary>Preserves original manufacturer hardware and opaque resources supplied by a catalogue.</summary>
    internal static ArchiveState WithManufacturerParts(ArchiveState? state, IReadOnlyDictionary<string, byte[]>? incoming)
    {
        state ??= WithApplications(null);
        if (incoming is null || incoming.Count == 0) return state;
        if (incoming.Count > 4096) throw new InvalidDataException("Too many manufacturer archive parts.");
        var parts = state.GeneratedParts is null ? new Dictionary<string, byte[]>(StringComparer.Ordinal)
            : new Dictionary<string, byte[]>(state.GeneratedParts, StringComparer.Ordinal);
        var originals = state.ApplicationParts ?? EncryptedArchive.ReadAll(state.Bytes, null);
        foreach (var (entry, data) in incoming)
        {
            var segments = entry.Split('/');
            if (segments.Length < 2 || !System.Text.RegularExpressions.Regex.IsMatch(segments[0], @"^M-[0-9a-fA-F]{4}$")
                || segments.Any(s => s is "" or "." or ".." || s.Any(c => char.IsControl(c) || c is '\\' or ':')))
                throw new InvalidDataException("Invalid manufacturer archive path.");
            if (data is null || data.Length > ArchiveEntryLimits.For(entry)) throw new InvalidDataException("Manufacturer part exceeds the archive limit.");
            var previous = parts.GetValueOrDefault(entry) ?? originals.GetValueOrDefault(entry);
            if (previous is not null && !previous.AsSpan().SequenceEqual(data))
            {
                if (!SameApplicationPart(entry, previous, data))
                    throw new InvalidDataException("Conflicting manufacturer archive part: " + entry);
            }
            if (previous is not null) continue; // The immutable source already retains these declarations/resources.
            parts[entry] = (byte[])data.Clone();
        }
        if (parts.Count > 4096 || parts.Values.Sum(p => (long)p.Length) > MaxArchiveBytes)
            throw new InvalidDataException("Manufacturer overlay exceeds the archive limit.");
        return state with { GeneratedParts = new System.Collections.ObjectModel.ReadOnlyDictionary<string, byte[]>(parts) };
    }

    private static bool SameXml(XElement first, XElement second)
    {
        XElement Comparable(XElement element)
        {
            var copy = new XElement(element);
            foreach (var text in copy.DescendantNodes().OfType<XText>().Where(t => t is not XCData
                && string.IsNullOrWhiteSpace(t.Value) && t.Parent?.HasElements == true
                && !t.Parent.AncestorsAndSelf().Any(a => (string?)a.Attribute(XNamespace.Xml + "space") == "preserve")).ToArray())
                text.Remove();
            return copy;
        }
        return XNode.DeepEquals(Comparable(first), Comparable(second));
    }

    private static bool SameApplicationPart(string entry, byte[] first, byte[] second)
    {
        // A vendor resource may contain "_A-" in its filename. Only a canonical,
        // identified application document has proven insignificant format spacing.
        if (!System.Text.RegularExpressions.Regex.IsMatch(entry, @"^M-[0-9a-fA-F]{4}/M-[0-9a-fA-F]{4}_A-[A-Za-z0-9_.-]+\.xml$")) return false;
        var a = ReadXml(first, MaxApplicationXmlBytes); var b = ReadXml(second, MaxApplicationXmlBytes);
        var programsA = a.Descendants().Where(e => e.Name.LocalName == "ApplicationProgram").ToArray();
        var programsB = b.Descendants().Where(e => e.Name.LocalName == "ApplicationProgram").ToArray();
        return a.Root?.Name.LocalName == "KNX" && b.Root?.Name.LocalName == "KNX"
            && programsA.Length == 1 && programsB.Length == 1
            && (string?)programsA[0].Attribute("Id") == (string?)programsB[0].Attribute("Id")
            && Knxproj.ApplicationProgramEntryPath((string?)programsA[0].Attribute("Id") ?? "") == entry
            && SameXml(a.Root, b.Root);
    }

    internal static Project Load(string path, string? password = null, IParameterScriptRuntime? scripts = null)
    {
        var bytes = ReadFile(path);
        var encrypted = ValidateZip(bytes);
        var (xml, nested, projectEncrypted, information, hardware, sidecar, layout) = ReadProject(bytes, password, encrypted, 0);
        var project = Knxproj.FromXml(xml);
        if (information is not null) Knxproj.ApplyProjectInformation(project, information);
        // Keep authenticated manufacturer parts and bounded optional root master metadata available
        // without retaining the project password. Do not parse optional master XML during Open.
        var applications = encrypted ? EncryptedArchive.ReadAll(bytes, password)
            .Where(p => p.Key.StartsWith("M-", StringComparison.Ordinal) || p.Key == "knx_master.xml" && p.Value.Length <= MaxXmlBytes)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) : null;
        project.ArchiveState = new ArchiveState(bytes, nested, projectEncrypted, applications,
            ProjectFormats.IsNative(xml.Root?.Name.Namespace) ? xml.Descendants().Attributes("Id").Select(a => a.Value).ToHashSet(StringComparer.Ordinal) : null,
            Layout: layout);
        foreach (var part in hardware) CatalogHardwareFacts.Read(part, project.Hardware);
        foreach (var group in ProjectQuery.DeviceList(project).GroupBy(d => d.Hardware2ProgramRefId ?? d.ProductRefId))
        {
            try
            {
                if (Knxproj.LoadApplicationProgram(project, group.Key, password) is { } application)
                    ApplicationObjects.Read(project, group, application, scripts);
            }
            // An unsupported definition must not prevent inspection of the rest of the project.
            catch (Exception ex) when (ex is InvalidDataException or XmlException) { }
        }
        if (sidecar is not null) Knxproj.ApplyArchiveSidecar(project, sidecar);
        return project;
    }

    private static (XDocument Xml, string? Nested, bool Encrypted, XDocument? Information, List<XDocument> Hardware, XDocument? Sidecar, ProjectEntryLayout Layout) ReadProject(
        byte[] zipBytes, string? password, bool encrypted, int depth)
    {
        if (depth > 1) throw new InvalidDataException("Nested project archives deeper than one level are not supported. " + Supported);
        IReadOnlyDictionary<string, byte[]>? decrypted = encrypted ? EncryptedArchive.ReadAll(zipBytes, password) : null;
        var names = decrypted?.Keys.ToList() ?? ZipNames(zipBytes);
        var hardware = ReadHardwareParts(decrypted, zipBytes, names);
        var candidates = names.Where(n => n.StartsWith("P-", StringComparison.Ordinal) &&
            n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !n.Contains('/')).ToList();
        var layout = ProjectEntryLayout.Find(names);
        var informationEntry = layout?.InformationEntry ?? "project.xml";
        var sidecarEntry = layout?.SidecarEntry ?? NativeProject.SidecarEntry;
        var information = names.Contains(informationEntry)
            ? ReadXml(ReadProjectEntry(decrypted, zipBytes, informationEntry, MaxXmlBytes)) : null;
        var sidecar = names.Contains(sidecarEntry)
            ? ReadXml(ReadProjectEntry(decrypted, zipBytes, sidecarEntry, MaxXmlBytes)) : null;

        if (layout is not null)
        {
            if (candidates.Count > 0) throw new InvalidDataException("Ambiguous root and nested project archives. " + Supported);
            var data = ReadXml(ReadProjectEntry(decrypted, zipBytes, layout.DataEntry, MaxXmlBytes));
            layout.Validate(data, information);
            return (data, null, encrypted, information, hardware, sidecar, layout);
        }
        if (candidates.Count != 1) throw new InvalidDataException(Supported);
        var nested = candidates[0];
        var inner = ReadProjectEntry(decrypted, zipBytes, nested, MaxEntryBytes);
        var innerEncrypted = ValidateZip(inner);
        var (xml, deeper, _, innerInformation, innerHardware, innerSidecar, innerLayout) = ReadProject(inner, password, innerEncrypted, depth + 1);
        if (deeper is not null) throw new InvalidDataException("Nested project archives deeper than one level are not supported. " + Supported);
        innerLayout.Validate(xml, information);
        hardware.AddRange(innerHardware);
        return (xml, nested, encrypted || innerEncrypted, innerInformation ?? information, hardware, innerSidecar ?? sidecar, innerLayout);
    }

    /// <summary>
    /// Manufacturer hardware data is optional planning input (BusCurrent, IsPowerSupply, IsCoupler).
    /// An unreadable part leaves the facts empty, so the check stays silent instead of failing the project.
    /// </summary>
    private static List<XDocument> ReadHardwareParts(IReadOnlyDictionary<string, byte[]>? decrypted,
        byte[] zipBytes, List<string> names)
    {
        var parts = new List<XDocument>();
        foreach (var name in names)
        {
            if (!name.EndsWith("/Hardware.xml", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                parts.Add(ReadXml(ReadProjectEntry(decrypted, zipBytes, name, MaxXmlBytes)));
            }
            catch (Exception e) when (e is InvalidDataException or XmlException) { }
        }
        return parts;
    }

    private static List<string> ZipNames(byte[] bytes)
    {
        using var source = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(source, ZipArchiveMode.Read);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    private static byte[] ReadProjectEntry(IReadOnlyDictionary<string, byte[]>? decrypted, byte[] zipBytes, string name, int limit)
    {
        byte[] bytes;
        if (decrypted is not null)
        {
            if (!decrypted.TryGetValue(name, out var data)) throw new InvalidDataException(Supported);
            if (data.Length > limit) throw new InvalidDataException("Archive entry exceeds the safety limit.");
            bytes = data;
        }
        else
        {
            using var source = new MemoryStream(zipBytes, false);
            using var zip = new ZipArchive(source, ZipArchiveMode.Read);
            bytes = ReadEntry(zip.GetEntry(name) ?? throw new InvalidDataException(Supported), limit);
        }
        return bytes;
    }

    internal static XDocument? LoadPart(string path, string entryPath, bool application = false, string? password = null)
        => LoadPart(ReadFile(path), entryPath, application, password);

    internal static XDocument? LoadPart(ArchiveState state, string entryPath, bool application = false, string? password = null)
    {
        var limit = application ? MaxApplicationXmlBytes : MaxXmlBytes;
        if (state.GeneratedParts?.TryGetValue(entryPath, out var generated) == true) return ReadPartXml(generated, limit, entryPath);
        // The authenticated cache is complete for outer manufacturer parts and available bounded
        // root master metadata. A missing/oversized optional master needs no password retry.
        if (state.ApplicationParts is not null && (entryPath.StartsWith("M-", StringComparison.Ordinal) || entryPath == "knx_master.xml"))
            return state.ApplicationParts.TryGetValue(entryPath, out var cached) ? ReadPartXml(cached, limit, entryPath) : null;
        return LoadPart(state.Bytes, entryPath, application, password);
    }

    internal static XDocument? LoadPart(byte[] bytes, string entryPath, bool application = false, string? password = null)
    {
        var limit = application ? MaxApplicationXmlBytes : MaxXmlBytes;
        if (ValidateZip(bytes))
        {
            var entries = EncryptedArchive.ReadAll(bytes, password);
            return entries.TryGetValue(entryPath, out var data) ? ReadPartXml(data, limit, entryPath) : null;
        }
        using var source = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(source, ZipArchiveMode.Read);
        return zip.GetEntry(entryPath) is { } entry ? ReadPartXml(ReadEntry(entry, limit), limit, entryPath) : null;
    }

    private static XDocument ReadPartXml(byte[] bytes, int limit, string entryPath)
    {
        var document = ReadXml(bytes, limit);
        document.AddAnnotation(new ArchivePartIdentity(entryPath));
        // Hash only this small metadata part, never all large application XMLs. The annotation
        // identifies source bytes; it does not participate in the selected profile fingerprint.
        if (entryPath == "knx_master.xml") document.AddAnnotation(new MasterDataProvenance(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))));
        return document;
    }

    internal static void Save(Project project, XDocument xml, string path, string? password = null, IParameterScriptRuntime? scripts = null)
    {
        var state = project.ArchiveState;
        if (state?.Encrypted == true && password is null) throw new InvalidDataException(EncryptedArchive.PasswordRequiredMessage);
        byte[] candidate;
        if (state is null)
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                if (!ProjectFormats.IsNative(xml.Root!.Name.Namespace)) Replace(archive, "0.xml", XmlBytes(xml));
            candidate = ProjectFormats.IsNative(xml.Root!.Name.Namespace)
                ? RewriteProject(project, xml, new ArchiveState(buffer.ToArray(), null), password, scripts) : buffer.ToArray();
        }
        else candidate = RewriteProject(project, xml, state, password, scripts);
        var fullPath = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                output.Write(candidate);
                output.Flush(true);
            }
            // Validate the finished candidate before the only operation touching the destination.
            _ = Load(temporary, password, scripts);
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] RewriteProject(Project project, XDocument xml, ArchiveState state, string? password, IParameterScriptRuntime? scripts)
    {
        var outer = EncryptedArchive.ReadAll(state.Bytes, password);
        var projectBytes = state.NestedEntry is { } nested ? outer.GetValueOrDefault(nested) ?? throw new InvalidDataException(Supported) : state.Bytes;
        var inner = state.NestedEntry is null ? outer : EncryptedArchive.ReadAll(projectBytes, password);
        var layout = state.Layout ?? ProjectEntryLayout.Root;
        var replacements = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var outerReplacements = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (entry, data) in state.GeneratedParts ?? new Dictionary<string, byte[]>())
            (state.NestedEntry is null ? replacements : outerReplacements).Add(entry, data);
        var format = ProjectFormats.Of(xml.Root!);
        if (format.IsNative())
        {
            var projection = new NativeProject(format);
            XDocument? Information(Dictionary<string, byte[]> entries, string name) => entries.TryGetValue(name, out var data) ? ReadXml(data) : null;
            var innerInformation = Information(inner, layout.InformationEntry);
            var outerInformation = state.NestedEntry is not null ? Information(outer, "project.xml") : null;
            var originalProjectXml = inner.TryGetValue(layout.DataEntry, out var initialXml) ? ReadXml(initialXml) : project.OriginalXml;
            var inlineInformation = innerInformation is null && outerInformation is null
                && originalProjectXml?.Descendants(format.Namespace() + "ProjectInformation").Any() == true;
            var previous = new[] { innerInformation, outerInformation, originalProjectXml }.Where(d => d is not null).SelectMany(d => d!.Descendants())
                .Attributes("LastUsedPuid").Select(a => int.TryParse(a.Value, out var n) ? n : 0).DefaultIfEmpty().Max();
            var native = projection.PrepareArchive(project, xml, previous, inlineInformation);
            // An existing reserved entry must be ours before it can be updated.
            if (inner.TryGetValue(layout.SidecarEntry, out var sidecar))
            {
                var existing = ReadXml(sidecar);
                if (existing.Root?.Name != Knxproj.ExtensionNamespace + "Project") throw new InvalidDataException("BusChord sidecar entry is occupied by unrelated data.");
                projection.RetainState(project, native.Sidecar, existing, originalProjectXml, password);
            }
            projection.SynchronizeModules(project, native.Data, native.Sidecar,
                originalProjectXml, password, scripts);
            replacements[layout.DataEntry] = XmlBytes(native.Data);
            replacements[layout.SidecarEntry] = XmlBytes(native.Sidecar);
            if (!inlineInformation && (innerInformation is not null || outerInformation is null))
                replacements[layout.InformationEntry] = XmlBytes(projection.InformationDocument(project, innerInformation, native.LastPuid));
            if (outerInformation is not null)
                outerReplacements["project.xml"] = XmlBytes(projection.InformationDocument(project, outerInformation, native.LastPuid));
        }
        else replacements[layout.DataEntry] = XmlBytes(xml);
        var updated = EncryptedArchive.Rewrite(projectBytes, replacements, password);
        if (state.NestedEntry is null) return updated;
        outerReplacements[state.NestedEntry] = updated;
        return EncryptedArchive.Rewrite(state.Bytes, outerReplacements, password);
    }

    private static byte[] XmlBytes(XDocument document, int limit = MaxXmlBytes)
    {
        using var output = new MemoryStream(); document.Save(output);
        if (output.Length > limit) throw new InvalidDataException("XML exceeds its archive safety limit.");
        return output.ToArray();
    }

    private static void Replace(ZipArchive zip, string name, byte[] bytes)
    {
        if (zip.Mode == ZipArchiveMode.Update) zip.GetEntry(name)?.Delete();
        using var output = zip.CreateEntry(name).Open();
        output.Write(bytes);
    }

    private static byte[] ReadFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxArchiveBytes) throw new InvalidDataException("Archive exceeds the 128 MiB safety limit.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
    {
        var length = entry.Length;
        if (length > limit) throw new InvalidDataException("Archive entry exceeds the safety limit.");
        using var input = entry.Open();
        var bytes = new byte[checked((int)length)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("Archive entry length mismatch.");
        return bytes;
    }

    private static XDocument ReadXml(byte[] bytes, int limit = MaxXmlBytes)
    {
        if (bytes.Length > limit) throw new InvalidDataException("XML exceeds the safety limit.");
        using var input = new MemoryStream(bytes, false);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = limit };
        // Limit depth before LINQ-to-XML/model recursion can consume the stack.
        using (var reader = XmlReader.Create(input, settings))
            while (reader.Read())
                if (reader.Depth > 128) throw new InvalidDataException("Project XML exceeds the depth safety limit.");
        input.Position = 0;
        using var bounded = XmlReader.Create(input, settings);
        return XDocument.Load(bounded, LoadOptions.PreserveWhitespace);
    }

    /// <summary>Validates the central directory and returns whether any entry is encrypted.</summary>
    private static bool ValidateZip(byte[] bytes)
    {
        // Check central-directory flags before opening encrypted/AES entries. No passwords or extraction.
        int end = -1;
        for (int i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 65557); i--)
            if (U32(i) == 0x06054b50 && i + 22 + U16(i + 20) == bytes.Length) { end = i; break; }
        if (end < 0) throw new InvalidDataException(Supported);
        var count = U16(end + 10);
        var offset = U32(end + 16);
        var size = U32(end + 12);
        if (U16(end + 4) != 0 || U16(end + 6) != 0 || U16(end + 8) != count || count > 4096 ||
            offset == uint.MaxValue || size == uint.MaxValue || (long)offset + size != end)
            throw new InvalidDataException(Supported + " Limit: 4096 entries.");
        var cursor = checked((int)offset);
        long total = 0;
        var encrypted = false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            if ((long)cursor + 46 > end || U32(cursor) != 0x02014b50) throw new InvalidDataException(Supported);
            var local = U32(cursor + 42);
            if ((long)local + 30 > offset || U32((int)local) != 0x04034b50) throw new InvalidDataException(Supported);
            var flags = U16(cursor + 8);
            var method = U16(cursor + 10);
            // Strong encryption (bit 6) and masked headers (bit 13) are not supported.
            if ((flags & 0x2040) != 0 || (U16((int)local + 6) & 0x2040) != 0)
                throw new InvalidDataException("Unsupported ZIP encryption. " + Supported);
            encrypted |= (flags & 0x1) != 0 || method == 99 || (U16((int)local + 6) & 0x1) != 0 || U16((int)local + 8) == 99;
            var length = U32(cursor + 24);
            total += length;
            var nameLength = U16(cursor + 28);
            var next = (long)cursor + 46 + nameLength + U16(cursor + 30) + U16(cursor + 32);
            if (next > end) throw new InvalidDataException(Supported);
            var entryName = System.Text.Encoding.UTF8.GetString(bytes, cursor + 46, nameLength);
            if (length > ArchiveEntryLimits.For(entryName) || total > 256L * 1024 * 1024)
                throw new InvalidDataException("Archive expanded size exceeds safety limits (128 MiB/application, 64 MiB/other entry, 256 MiB total).");
            // Compare raw name bytes as well as ZipArchive's decoded entry names below.
            if (!names.Add(Convert.ToHexString(bytes.AsSpan(cursor + 46, nameLength)))) throw new InvalidDataException("Duplicate archive entry names are not supported.");
            cursor = (int)next;
        }
        if (cursor != end) throw new InvalidDataException(Supported);
        using var stream = new MemoryStream(bytes, false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != count) throw new InvalidDataException("Ambiguous archive entry names.");

        ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at, 2));
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
        return encrypted;
    }
}
