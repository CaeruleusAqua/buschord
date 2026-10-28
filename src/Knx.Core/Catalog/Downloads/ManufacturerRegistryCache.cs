using System.Net;
using System.Text.RegularExpressions;
using System.Xml;

namespace Knx.Core.Catalog.Downloads;

/// <summary>Explicit client-side refresh of public identity metadata; never a bundled copied registry.</summary>
public sealed class ManufacturerRegistryCache
{
    public const string DirectoryUrl = "https://www.knx.org/community/manufacturers";
    public const string MasterUrl = "https://update.knx.org/data/XML/project-23/knx_master.xml";
    public const string RightsNotice = "The directory and manufacturer codes are different identity lists, not a count of unique active manufacturers. Names and websites are published directory metadata, not independent legal-entity verification. Fetching to your local cache does not grant redistribution rights. KNX master-data documentation describes scoped permissions for Manufacturer Tool owners; review the official conditions before reuse.";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private readonly string _path;
    private readonly CatalogHttpTransport _transport;
    private readonly SemaphoreSlim _refresh = new(1);

    internal ManufacturerRegistryCache(string path, CatalogHttpTransport transport) { _path = path; _transport = transport; }

    public ManufacturerRegistrySnapshot? Load()
    {
        var snapshot = CatalogDownloadPersistence.Read<ManufacturerRegistrySnapshot>(_path);
        if (snapshot is null || snapshot.RetrievedAt == default || snapshot.Entries is null || snapshot.Entries.Count > 40000 ||
            snapshot.DirectoryCount < 0 || snapshot.CodeCount < 0 || (long)snapshot.DirectoryCount + snapshot.CodeCount != snapshot.Entries.Count ||
            snapshot.RightsNotice is null || snapshot.RightsNotice.Length > 16000 || snapshot.SourceUrls is null ||
            snapshot.SourceUrls.Count > 10 || snapshot.SourceUrls.Any(url => !ValidUrl(url, false)) ||
            snapshot.Entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 1024 ||
                !ValidUrl(entry.SourceUrl, false) || entry.IdentityKind is not ("DirectoryEntry" or "ManufacturerCode") ||
                entry.Country?.Length > 256 || entry.MemberStatus?.Length > 256 || entry.Website is not null && !ValidUrl(entry.Website, true) ||
                entry.IdentityKind == "ManufacturerCode" && (entry.Code is null or < 0 or > 65535 || entry.ManufacturerId != "M-" + entry.Code.Value.ToString("X4")) ||
                entry.IdentityKind == "DirectoryEntry" && (entry.ManufacturerId is not null || entry.Code is not null)))
            return null;
        if (snapshot.Entries.Count(entry => entry.IdentityKind == "ManufacturerCode") != snapshot.CodeCount) return null;
        return snapshot;
    }

    private static bool ValidUrl(string? value, bool allowHttp) => value is { Length: <= 8192 } &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || allowHttp && uri.Scheme == "http") &&
        uri.UserInfo.Length == 0 && uri.IsDefaultPort;

    public async Task<ManufacturerRegistrySnapshot> RefreshAsync(bool explicitUserRequest,
        IProgress<ManufacturerRegistryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!explicitUserRequest) throw new InvalidOperationException("Manufacturer metadata is fetched only after an explicit user refresh.");
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        var root = Path.Combine(Path.GetDirectoryName(_path)!, "registry-jobs");
        var job = Path.Combine(root, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(job);
            var budget = new CatalogByteBudget(64L * 1024 * 1024);
            async Task<string> Fetch(string url, int index)
            {
                var uri = new Uri(url);
                var transfer = await _transport.FetchAsync(uri, [uri.IdnHost], Path.Combine(job, index + ".metadata"),
                    budget, 8L * 1024 * 1024, true, null, cancellationToken).ConfigureAwait(false);
                return transfer.Path;
            }
            var masterPath = await Fetch(MasterUrl, -1).ConfigureAwait(false);
            var entries = ParseMaster(masterPath);
            var codeCount = entries.Count;
            var firstPath = await Fetch(DirectoryUrl, 0).ConfigureAwait(false);
            var first = await File.ReadAllTextAsync(firstPath, cancellationToken).ConfigureAwait(false);
            var countMatch = Regex.Match(first, @"([\d,]+) Results", RegexOptions.CultureInvariant, RegexTimeout);
            if (!countMatch.Success || !int.TryParse(countMatch.Groups[1].Value.Replace(",", ""), out var total) || total is < 1 or > 20000)
                throw new InvalidDataException("The manufacturer directory count could not be verified.");
            var last = Regex.Matches(first, @"\?page=(\d+)", RegexOptions.CultureInvariant, RegexTimeout)
                .Select(match => int.Parse(match.Groups[1].Value)).DefaultIfEmpty(0).Max();
            if (last > 300) throw new InvalidDataException("The manufacturer directory exceeds its page limit.");
            var directory = ParseDirectory(first, DirectoryUrl);
            if (directory.Count == 0) throw new InvalidDataException("The manufacturer directory structure was not recognized.");
            progress?.Report(new(1, last + 1, directory.Count));
            for (var page = 1; page <= last; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = DirectoryUrl + "?page=" + page;
                var path = await Fetch(url, page).ConfigureAwait(false);
                var rows = ParseDirectory(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), url);
                if (rows.Count == 0) throw new InvalidDataException("A manufacturer directory page was empty or changed format.");
                directory.AddRange(rows);
                if (directory.Count > total) throw new InvalidDataException("The manufacturer directory changed during refresh; retry later.");
                progress?.Report(new(page + 1, last + 1, directory.Count));
            }
            if (directory.Count != total) throw new InvalidDataException("The manufacturer directory changed during refresh; the previous cache was kept.");
            entries.AddRange(directory);
            var snapshot = new ManufacturerRegistrySnapshot(DateTimeOffset.UtcNow, entries.AsReadOnly(),
                directory.Count, codeCount, RightsNotice, Array.AsReadOnly(new[] { DirectoryUrl, MasterUrl,
                    "https://support.knx.org/hc/en-us/article_attachments/17389755651474" }));
            await CatalogDownloadPersistence.WriteAsync(_path, snapshot, cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        finally
        {
            try { CatalogDownloadPersistence.CleanJobDirectory(job, root); }
            finally { _refresh.Release(); }
        }
    }

    private static List<ManufacturerRegistryEntry> ParseMaster(string path)
    {
        using var input = File.OpenRead(path);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8L * 1024 * 1024,
        });
        var result = new List<ManufacturerRegistryEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.Depth > 128) throw new InvalidDataException("The manufacturer master-data XML exceeds its depth limit.");
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0 &&
                (reader.LocalName != "KNX" || reader.NamespaceURI != "http://knx.org/xml/project/23"))
                throw new InvalidDataException("The manufacturer master-data root is not recognized.");
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Manufacturer" ||
                reader.NamespaceURI != "http://knx.org/xml/project/23" || reader.GetAttribute("KnxManufacturerId") is null) continue;
            var id = reader.GetAttribute("Id");
            var name = reader.GetAttribute("Name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !ids.Add(id) ||
                !int.TryParse(reader.GetAttribute("KnxManufacturerId"), out var code) || code is < 0 or > 65535 ||
                id != "M-" + code.ToString("X4"))
                throw new InvalidDataException("The master data contains an invalid or duplicate manufacturer identity.");
            result.Add(new(name, id, code, reader.GetAttribute("MemberStatus"), null, null, MasterUrl, "ManufacturerCode"));
            if (result.Count > 20000) throw new InvalidDataException("The manufacturer-code list exceeds its count limit.");
        }
        if (result.Count is < 1 or > 20000) throw new InvalidDataException("No bounded manufacturer-code list was found.");
        return result;
    }

    private static List<ManufacturerRegistryEntry> ParseDirectory(string html, string source)
    {
        var result = new List<ManufacturerRegistryEntry>();
        foreach (Match item in Regex.Matches(html, @"<details\b[^>]*>(.*?)</details>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout))
        {
            var name = Regex.Match(item.Groups[1].Value, @"<h4>(.*?)</h4>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
            if (!name.Success) continue;
            var country = Regex.Match(item.Groups[1].Value, "<p class=\"sub-text\">(.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
            var website = Regex.Match(item.Groups[1].Value, "Website:\\s*<a\\b[^>]*href=\"([^\"]+)\"", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
            string? url = null;
            if (website.Success && Uri.TryCreate(WebUtility.HtmlDecode(website.Groups[1].Value), UriKind.Absolute, out var uri) &&
                uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 && uri.IsDefaultPort)
                url = uri.AbsoluteUri;
            result.Add(new(Plain(name.Groups[1].Value), null, null, null,
                country.Success ? Plain(country.Groups[1].Value) : null, url, source, "DirectoryEntry"));
        }
        return result;
    }

    private static string Plain(string html) => Regex.Replace(WebUtility.HtmlDecode(
        Regex.Replace(html, "<[^>]+>", " ", RegexOptions.CultureInvariant, RegexTimeout)), @"\s+", " ",
        RegexOptions.CultureInvariant, RegexTimeout).Trim();
}
