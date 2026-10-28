using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;

namespace Knx.Core.Catalog.Downloads;

internal sealed record StagedDownloadCatalog(string Path, string Name, string Sha256, bool SignaturePresent);
internal sealed record StagedDownloadValidation(IReadOnlyList<StagedDownloadCatalog> Catalogs, IReadOnlyList<string> Skipped);

internal static class CatalogDownloadValidation
{
    private sealed class ExpansionBudget(CatalogDownloadOptions options)
    {
        public long Declared;
        public long Read;
        public int Entries;
        public void CountRead(int count)
        {
            Read = checked(Read + count);
            if (Read > options.MaximumExpandedBytes) throw new InvalidDataException("The archive expansion budget was exceeded.");
        }
    }

    public static StagedDownloadValidation Stage(string path, string directory, string expectedManufacturer,
        CatalogDownloadOptions options, CancellationToken token)
    {
        var budget = new ExpansionBudget(options);
        var catalogs = new List<StagedDownloadCatalog>();
        var skipped = new List<string>();
        using var archive = ZipFile.OpenRead(path);
        CheckEntries(archive, options, budget);
        if (IsCatalogue(archive))
        {
            ValidateXml(archive, expectedManufacturer, options, budget, token);
            catalogs.Add(Describe(path, System.IO.Path.GetFileName(path), archive, token));
        }
        else
        {
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Name.EndsWith(".knxprod", StringComparison.OrdinalIgnoreCase))
                {
                    var target = System.IO.Path.Combine(directory, "catalogue-" + catalogs.Count + ".knxprod");
                    using (var input = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        CopyBounded(input, output, Math.Min(entry.Length, options.MaximumFileBytes), budget, token);
                        if (output.Length != entry.Length) throw new InvalidDataException("The inner catalogue is incomplete.");
                    }
                    using var nested = ZipFile.OpenRead(target);
                    CheckEntries(nested, options, budget);
                    if (!IsCatalogue(nested)) throw new InvalidDataException("An inner .knxprod has no supported catalogue data.");
                    ValidateXml(nested, expectedManufacturer, options, budget, token);
                    catalogs.Add(Describe(target, entry.Name, nested, token));
                }
                else if (entry.Name.Length > 0)
                    skipped.Add(entry.FullName.Length > 300 ? entry.FullName[..300] : entry.FullName);
            }
        }
        if (catalogs.Count == 0)
            throw new InvalidDataException("No supported .knxprod catalogue was found; legacy databases, firmware and programs require manual handling.");
        return new(catalogs.AsReadOnly(), skipped.AsReadOnly());
    }

    private static bool IsCatalogue(ZipArchive archive) => archive.Entries.Any(entry =>
        entry.Name.Equals("knxprod.xml", StringComparison.OrdinalIgnoreCase) ||
        entry.Name.Equals("Catalog.xml", StringComparison.OrdinalIgnoreCase) ||
        entry.Name.Equals("Hardware.xml", StringComparison.OrdinalIgnoreCase));

    private static void CheckEntries(ZipArchive archive, CatalogDownloadOptions options, ExpansionBudget budget)
    {
        budget.Entries = checked(budget.Entries + archive.Entries.Count);
        if (budget.Entries > options.MaximumArchiveEntries) throw new InvalidDataException("The archive contains too many entries.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (path.StartsWith('/') || entry.FullName.Contains('\\') || path.Contains(':') || path.Any(ch => ch < 32) ||
                path.TrimEnd('/').Split('/').Any(part => part.Length == 0 || part is "." or ".." || part != part.TrimEnd(' ', '.')) || !paths.Add(path) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("The archive contains an unsafe or ambiguous path.");
            budget.Declared = checked(budget.Declared + entry.Length);
            if (entry.Length > options.MaximumEntryBytes || budget.Declared > options.MaximumExpandedBytes ||
                entry.Length > Math.Max(1, entry.CompressedLength) * (double)options.MaximumCompressionRatio)
                throw new InvalidDataException("The archive exceeds its expansion or compression limits.");
        }
    }

    private static void ValidateXml(ZipArchive archive, string expected, CatalogDownloadOptions options,
        ExpansionBudget budget, CancellationToken token)
    {
        var manufacturerFound = false;
        foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            using var content = entry.Open();
            using var bounded = new CountedReadStream(content, entry.Length, options.MaximumEntryBytes, budget, token);
            using var xml = XmlReader.Create(bounded, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = options.MaximumEntryBytes, IgnoreComments = true,
            });
            var primary = entry.Name.Equals("knxprod.xml", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Equals("Catalog.xml", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.Equals("Hardware.xml", StringComparison.OrdinalIgnoreCase);
            var applicationPart = System.IO.Path.GetFileNameWithoutExtension(entry.Name).Contains("_A-", StringComparison.OrdinalIgnoreCase);
            var masterFile = entry.Name.Equals("knx_master.xml", StringComparison.OrdinalIgnoreCase);
            string? rootNamespace = null;
            var names = new string?[129];
            var namespaces = new string?[129];
            while (xml.Read())
            {
                token.ThrowIfCancellationRequested();
                if (xml.Depth > 128) throw new InvalidDataException("The downloaded XML nesting exceeds its depth limit.");
                if (xml.NodeType == XmlNodeType.Element && xml.Depth == 0)
                {
                    var candidateNamespace = xml.NamespaceURI;
                    var prefix = candidateNamespace.StartsWith("http://knx.org/xml/project/", StringComparison.Ordinal) ||
                                 candidateNamespace.StartsWith("http://knx.org/xml/prod/", StringComparison.Ordinal);
                    var knxRoot = xml.LocalName == "KNX" && prefix && int.TryParse(candidateNamespace.Split('/')[^1], out var version) && version is >= 11 and <= 23;
                    if ((primary || applicationPart || masterFile) && !knxRoot)
                        throw new InvalidDataException("The catalogue XML namespace is not supported.");
                    rootNamespace = knxRoot ? candidateNamespace : null;
                }
                if (xml.NodeType != XmlNodeType.Element) continue;
                names[xml.Depth] = xml.LocalName;
                namespaces[xml.Depth] = xml.NamespaceURI;
                if (xml.LocalName == "Manufacturer" && (rootNamespace is not null || primary || applicationPart || masterFile))
                {
                    var productManufacturer = rootNamespace is not null && xml.Depth == 2 && names[0] == "KNX" &&
                        names[1] == "ManufacturerData" && namespaces[1] == rootNamespace && xml.NamespaceURI == rootNamespace;
                    if (productManufacturer)
                    {
                        if (xml.GetAttribute("RefId") != expected)
                            throw new InvalidDataException("The catalogue manufacturer does not match the selected source profile.");
                        manufacturerFound = true;
                    }
                    else
                    {
                        // Only the real master-data shape can list other manufacturer codes. A filename alone grants no exception.
                        var masterCode = masterFile && rootNamespace is not null && xml.Depth == 3 && xml.IsEmptyElement &&
                            names[1] == "MasterData" && namespaces[1] == rootNamespace && names[2] == "Manufacturers" &&
                            namespaces[2] == rootNamespace && xml.NamespaceURI == rootNamespace &&
                            int.TryParse(xml.GetAttribute("KnxManufacturerId"), out var code) && code is >= 0 and <= 65535 &&
                            xml.GetAttribute("Id") == "M-" + code.ToString("X4") && xml.GetAttribute("RefId") is null;
                        if (!masterCode) throw new InvalidDataException("A manufacturer declaration has an unexpected position or namespace.");
                    }
                }
            }
            if (bounded.BytesRead != entry.Length) throw new InvalidDataException("An XML archive entry is incomplete.");
        }
        if (!manufacturerFound) throw new InvalidDataException("No matching manufacturer identity was found in the catalogue.");
    }

    private static StagedDownloadCatalog Describe(string path, string name, ZipArchive archive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var input = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        return new(path, name, hash, archive.Entries.Any(e => e.Name.EndsWith(".signature", StringComparison.OrdinalIgnoreCase)));
    }

    private static void CopyBounded(Stream input, Stream output, long limit, ExpansionBudget budget, CancellationToken token)
    {
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            budget.CountRead(read);
            total = checked(total + read);
            if (total > limit) throw new InvalidDataException("An archive entry exceeded its declared size.");
            output.Write(buffer, 0, read);
        }
    }

    private sealed class CountedReadStream(Stream inner, long declared, long maximum, ExpansionBudget budget,
        CancellationToken token) : Stream
    {
        public long BytesRead { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            token.ThrowIfCancellationRequested();
            var read = inner.Read(buffer, offset, count);
            BytesRead = checked(BytesRead + read);
            budget.CountRead(read);
            if (BytesRead > declared || BytesRead > maximum) throw new InvalidDataException("An XML archive entry exceeded its size bound.");
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
