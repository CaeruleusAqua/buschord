using Knx.Catalog;
using System.Security.Cryptography;
using System.Text;

namespace Knx.Core.Catalog.Downloads;

/// <summary>User-initiated official-source acquisition with local consent and preserving import.</summary>
public sealed class CatalogDownloadService : IDisposable
{
    private readonly CatalogStore _store;
    private readonly CatalogDownloadOptions _options;
    private readonly CatalogHttpTransport _transport;
    private readonly Dictionary<string, CatalogSourceConsent> _consents;
    private readonly SemaphoreSlim _stateGate = new(1);
    private readonly SemaphoreSlim _importGate = new(1);
    private readonly object _consentLock = new();
    private readonly string _consentPath;

    /// <summary>Optional sources and HTTP handler are controlled integration/test dependencies, not user manifests.</summary>
    public CatalogDownloadService(CatalogStore store, string? dataDirectory = null, HttpMessageHandler? handler = null,
        CatalogDownloadOptions? options = null, IReadOnlyList<CatalogDownloadSource>? sources = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new();
        _transport = new(_options, handler);
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppSettings.AppFolderName, "catalog-downloads"));
        _consentPath = Path.Combine(DataDirectory, "consents.json");
        Sources = Array.AsReadOnly((sources ?? CatalogDownloadSources.All).ToArray());
        if (Sources.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != Sources.Count)
            throw new ArgumentException("Catalogue source identities must be unique.", nameof(sources));
        _consents = (CatalogDownloadPersistence.Read<List<CatalogSourceConsent?>>(_consentPath) ?? []).OfType<CatalogSourceConsent>()
            .Where(ValidConsent)
            .GroupBy(c => c.SourceId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        Registry = new ManufacturerRegistryCache(Path.Combine(DataDirectory, "registry.json"), _transport);
    }

    public string DataDirectory { get; }
    public IReadOnlyList<CatalogDownloadSource> Sources { get; }
    public ManufacturerRegistryCache Registry { get; }

    public CatalogSourceConsent? GetConsent(string sourceId)
    {
        lock (_consentLock) return _consents.GetValueOrDefault(sourceId);
    }

    public CatalogSourceAuthorization GetAuthorization(CatalogDownloadSource source)
    {
        lock (_consentLock) return Authorization(source, _consents.GetValueOrDefault(source.Id));
    }

    private static CatalogSourceAuthorization Authorization(CatalogDownloadSource source, CatalogSourceConsent? consent)
    {
        if (source.Rights == CatalogSourceRights.Restricted) return CatalogSourceAuthorization.Restricted;
        if (!source.CanDownload) return CatalogSourceAuthorization.ManualOnly;
        if (consent is null || consent.TermsRevision != source.TermsRevision || consent.ScopeHash != GetSourceScopeHash(source))
            return CatalogSourceAuthorization.Missing;
        return consent.Kind switch
        {
            CatalogConsentKind.ManufacturerTerms when source.Rights == CatalogSourceRights.ManufacturerGranted => CatalogSourceAuthorization.ManufacturerTerms,
            CatalogConsentKind.UserDeclaredAuthorization when !string.IsNullOrWhiteSpace(consent.EvidenceNote) && consent.EvidenceNote.Trim().Length >= 20 => CatalogSourceAuthorization.UserDeclaredAuthorization,
            _ => CatalogSourceAuthorization.Missing,
        };
    }

    public async Task<CatalogSourceConsent> RecordConsentAsync(string sourceId, CatalogConsentKind kind,
        string evidenceNote, CancellationToken cancellationToken = default)
    {
        var source = Find(sourceId);
        if (source.Rights == CatalogSourceRights.Restricted || !source.CanDownload)
            throw new InvalidOperationException("This source cannot be authorized for automatic acquisition.");
        if (kind == CatalogConsentKind.ManufacturerTerms && source.Rights != CatalogSourceRights.ManufacturerGranted)
            throw new InvalidOperationException("No manufacturer grant has been established for this source.");
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        evidenceNote = (evidenceNote ?? "").Trim();
        if (evidenceNote.Length > 4000 || kind == CatalogConsentKind.UserDeclaredAuthorization && evidenceNote.Length < 20)
            throw new ArgumentException("Describe your existing source-specific permission in 20 to 4000 characters.", nameof(evidenceNote));
        var consent = new CatalogSourceConsent(Guid.NewGuid().ToString("N"), source.Id, source.TermsRevision,
            kind, evidenceNote, DateTimeOffset.UtcNow, GetSourceScopeHash(source));
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<CatalogSourceConsent> records;
            lock (_consentLock) records = _consents.Values.Where(c => c.SourceId != sourceId).Append(consent).ToList();
            await CatalogDownloadPersistence.WriteAsync(_consentPath, records, cancellationToken).ConfigureAwait(false);
            lock (_consentLock) _consents[sourceId] = consent;
            return consent;
        }
        finally { _stateGate.Release(); }
    }

    public async Task RevokeConsentAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<CatalogSourceConsent> records;
            lock (_consentLock) records = _consents.Values.Where(c => c.SourceId != sourceId).ToList();
            await CatalogDownloadPersistence.WriteAsync(_consentPath, records, cancellationToken).ConfigureAwait(false);
            lock (_consentLock) _consents.Remove(sourceId);
        }
        finally { _stateGate.Release(); }
    }

    public IReadOnlyList<CatalogDownloadBatchReport> LoadReports()
    {
        var folder = Path.Combine(DataDirectory, "reports");
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.json").OrderByDescending(Path.GetFileName).Take(100)
            .Select(CatalogDownloadPersistence.Read<CatalogDownloadBatchReport>).OfType<CatalogDownloadBatchReport>()
            .Where(ValidReport).ToArray();
    }

    public async Task<CatalogDownloadBatchReport> DownloadAsync(IEnumerable<string> sourceIds,
        IProgress<CatalogDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        var selected = sourceIds.Distinct(StringComparer.Ordinal).Select(Find).ToArray();
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N");
        var start = DateTimeOffset.UtcNow;
        var jobsRoot = Path.Combine(DataDirectory, "jobs");
        var job = Path.Combine(jobsRoot, id);
        Directory.CreateDirectory(job);
        var budget = new CatalogByteBudget(_options.MaximumBatchBytes);
        var items = new List<CatalogDownloadItemReport>();
        var provenance = new List<CatalogDownloadProvenance>();
        var reportGate = new object();
        var completed = 0;
        void Add(CatalogDownloadItemReport item) { lock (reportGate) items.Add(item); }
        void Report(string source, CatalogDownloadPhase phase, long bytes = 0, long? total = null, string detail = "") =>
            progress?.Report(new(source, phase, bytes, total, detail, Volatile.Read(ref completed), selected.Length));
        async Task Run(CatalogDownloadSource source, int index)
        {
            var folder = Path.Combine(job, index.ToString("D4"));
            Directory.CreateDirectory(folder);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                CatalogSourceConsent? consent;
                CatalogSourceAuthorization authorization;
                lock (_consentLock)
                {
                    consent = _consents.GetValueOrDefault(source.Id);
                    authorization = Authorization(source, consent);
                }
                if (authorization is not (CatalogSourceAuthorization.ManufacturerTerms or CatalogSourceAuthorization.UserDeclaredAuthorization))
                {
                    Add(new(source.Id, source.Name, CatalogDownloadItemStatus.NotAuthorized, authorization.ToString()));
                    return;
                }
                var capturedConsent = consent!;
                void EnsureAuthorized()
                {
                    lock (_consentLock)
                    {
                        var current = _consents.GetValueOrDefault(source.Id);
                        if (current?.Id != capturedConsent.Id || Authorization(source, current) is not
                            (CatalogSourceAuthorization.ManufacturerTerms or CatalogSourceAuthorization.UserDeclaredAuthorization))
                            throw new CatalogSourceAuthorizationException();
                    }
                }
                Report(source.Id, CatalogDownloadPhase.Queued);
                var transfer = await _transport.FetchAsync(source.DownloadUri!, source.AllowedHosts,
                    Path.Combine(folder, "download.knxprod"), budget, _options.MaximumFileBytes, false,
                    (bytes, total) => Report(source.Id, CatalogDownloadPhase.Downloading, bytes, total), cancellationToken, EnsureAuthorized).ConfigureAwait(false);
                var origin = new CatalogDownloadProvenance(source.Id, source.SourcePage.AbsoluteUri, source.DownloadUri!.AbsoluteUri,
                    transfer.FinalUrl.AbsoluteUri, transfer.Sha256, transfer.Bytes, transfer.ETag, transfer.LastModified,
                    source.TermsRevision, capturedConsent.Id, capturedConsent.Kind, DateTimeOffset.UtcNow, source.TermsUri?.AbsoluteUri, source.Notice);
                // Persist origin before any library mutation, so even an interrupted batch has a local trail.
                await CatalogDownloadPersistence.WriteAsync(Path.Combine(DataDirectory, "provenance", id + "-" + index + ".json"),
                    origin, CancellationToken.None).ConfigureAwait(false);
                lock (reportGate) provenance.Add(origin);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureAuthorized();
                Report(source.Id, CatalogDownloadPhase.Validating);
                var staged = await Task.Run(() => CatalogDownloadValidation.Stage(transfer.Path, folder,
                    source.ManufacturerId, _options, cancellationToken), cancellationToken).ConfigureAwait(false);
                foreach (var skipped in staged.Skipped)
                    Add(new(source.Id, skipped, CatalogDownloadItemStatus.Unsupported, "Not a supported .knxprod; no vendor software was executed."));
                foreach (var catalog in staged.Catalogs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Report(source.Id, CatalogDownloadPhase.Importing, detail: catalog.Name);
                    await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        CatalogPreservingImportResult imported;
                        try
                        {
                            // Serialize authorization changes with the final preserving library commit.
                            EnsureAuthorized();
                            imported = _store.ImportPreservingExisting(catalog.Path, cancellationToken, source.ManufacturerId);
                        }
                        finally { _stateGate.Release(); }
                        var status = imported.Disposition switch
                        {
                            CatalogPreservingImportDisposition.Imported => CatalogDownloadItemStatus.Imported,
                            CatalogPreservingImportDisposition.IdenticalSkipped => CatalogDownloadItemStatus.IdenticalSkipped,
                            _ => CatalogDownloadItemStatus.ConflictKeptExisting,
                        };
                        var detail = status == CatalogDownloadItemStatus.ConflictKeptExisting ? "Changed bytes share an installed identity; the installed catalogue was kept." :
                            catalog.SignaturePresent ? "A signature file is present; it has not been cryptographically verified." : "";
                        if (!string.IsNullOrEmpty(imported.CacheWarning)) detail += " " + imported.CacheWarning;
                        Add(new(source.Id, catalog.Name, status, detail.Trim(), catalog.Sha256, imported.LibraryPath));
                    }
                    finally { _importGate.Release(); }
                }
                Report(source.Id, CatalogDownloadPhase.Completed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Add(new(source.Id, source.Name, CatalogDownloadItemStatus.Cancelled, "Acquisition cancelled; completed imports were preserved."));
                Report(source.Id, CatalogDownloadPhase.Cancelled);
            }
            catch (CatalogSourceAuthorizationException)
            {
                Add(new(source.Id, source.Name, CatalogDownloadItemStatus.NotAuthorized, "The source authorization was revoked or changed; no further catalogue was imported."));
                Report(source.Id, CatalogDownloadPhase.Failed, detail: "Source authorization changed.");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or
                System.Xml.XmlException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException or TimeoutException or NotSupportedException or OverflowException)
            {
                Add(new(source.Id, source.Name, CatalogDownloadItemStatus.Failed, error.Message));
                Report(source.Id, CatalogDownloadPhase.Failed, detail: error.Message);
            }
            finally { Interlocked.Increment(ref completed); }
        }
        try { await Task.WhenAll(selected.Select(Run)).ConfigureAwait(false); }
        finally { CatalogDownloadPersistence.CleanJobDirectory(job, jobsRoot); }
        var report = new CatalogDownloadBatchReport(id, start, DateTimeOffset.UtcNow, cancellationToken.IsCancellationRequested,
            budget.Received, items.ToArray(), provenance.ToArray());
        // A cancelled job still has a durable report; no cancellation token can erase its provenance.
        try
        {
            await CatalogDownloadPersistence.WriteAsync(Path.Combine(DataDirectory, "reports", id + ".json"), report, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            report = report with { PersistenceWarning = "The batch report could not be saved. Any completed imports and saved source provenance were preserved." };
        }
        return report;
    }

    private CatalogDownloadSource Find(string id) => Sources.FirstOrDefault(source => source.Id == id) ??
        throw new ArgumentException("Unknown catalogue source.", nameof(id));

    private bool ValidConsent(CatalogSourceConsent consent) =>
        Text(consent.Id, 100) && Text(consent.SourceId, 200) && Sources.Any(source => source.Id == consent.SourceId) &&
        Text(consent.TermsRevision, 512) && Hash(consent.ScopeHash) && Enum.IsDefined(consent.Kind) &&
        consent.EvidenceNote is not null && consent.EvidenceNote.Length <= 4000 &&
        (consent.Kind != CatalogConsentKind.UserDeclaredAuthorization || consent.EvidenceNote.Trim().Length >= 20) &&
        consent.RecordedAt != default;

    private static bool ValidReport(CatalogDownloadBatchReport report) =>
        Text(report.Id, 150) && report.StartedAt != default && report.FinishedAt >= report.StartedAt && report.BytesReceived >= 0 &&
        report.Items is not null && report.Items.Count <= 100000 && report.Items.All(item => item is not null &&
            Text(item.SourceId, 200) && Text(item.File, 1000) && Enum.IsDefined(item.Status) && item.Detail is not null &&
            item.Detail.Length <= 16000 && (item.Sha256 is null || Hash(item.Sha256)) && (item.LibraryPath is null || item.LibraryPath.Length <= 32768)) &&
        (report.PersistenceWarning is null || report.PersistenceWarning.Length <= 16000) &&
        report.Provenance is not null && report.Provenance.Count <= 1000 && report.Provenance.All(origin => origin is not null &&
            Text(origin.SourceId, 200) && WebUrl(origin.SourcePage) && WebUrl(origin.RequestedUrl) && WebUrl(origin.FinalUrl) &&
            Hash(origin.Sha256) && origin.Bytes >= 0 && Text(origin.TermsRevision, 512) && Text(origin.ConsentId, 100) &&
            Enum.IsDefined(origin.ConsentKind) && origin.RetrievedAt != default && (origin.TermsUrl is null || WebUrl(origin.TermsUrl)) &&
            origin.SourceNotice is not null && origin.SourceNotice.Length <= 16000);

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool WebUrl(string? value) => value is { Length: <= 8192 } && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort;

    public static string GetSourceScopeHash(CatalogDownloadSource source)
    {
        var fields = new[] { source.Id, source.ManufacturerId, source.SourcePage.AbsoluteUri, source.DownloadUri?.AbsoluteUri ?? "",
            string.Join(',', source.AllowedHosts.Select(h => h.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            source.Availability.ToString(), source.Rights.ToString(), source.TermsUri?.AbsoluteUri ?? "", source.TermsRevision,
            source.Notice, source.NoticeVersion, source.Formats };
        var canonical = string.Concat(fields.Select(field => field.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + field));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed class CatalogSourceAuthorizationException : InvalidOperationException { }

    public void Dispose() { _transport.Dispose(); _stateGate.Dispose(); _importGate.Dispose(); }
}
