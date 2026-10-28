namespace Knx.Core.Catalog.Downloads;

public enum CatalogSourceRights { Unknown, ManufacturerGranted, Restricted }
public enum CatalogSourceAvailability { Direct, Bundle, Manual, LoginRequired, Unsupported }
public enum CatalogConsentKind { ManufacturerTerms, UserDeclaredAuthorization }
public enum CatalogSourceAuthorization { Missing, ManufacturerTerms, UserDeclaredAuthorization, Restricted, ManualOnly }
public enum CatalogDownloadPhase { Queued, Downloading, Validating, Importing, Completed, Failed, Cancelled }
public enum CatalogDownloadItemStatus { Imported, IdenticalSkipped, ConflictKeptExisting, Unsupported, NotAuthorized, Failed, Cancelled }

/// <summary>Small reviewed discovery profile, independent of the full manufacturer registry.</summary>
public sealed record CatalogDownloadSource(string Id, string Name, string ManufacturerId,
    Uri SourcePage, Uri? DownloadUri, IReadOnlyList<string> AllowedHosts,
    CatalogSourceAvailability Availability, CatalogSourceRights Rights, Uri? TermsUri,
    string TermsRevision, string Notice, string Formats, DateTimeOffset VerifiedAt, string ManufacturerName = "", string NoticeVersion = "1")
{
    public bool CanDownload => DownloadUri is not null && (Availability is CatalogSourceAvailability.Direct or CatalogSourceAvailability.Bundle);
}

/// <summary>User declaration records personal evidence; it never changes the manufacturer's rights state.</summary>
public sealed record CatalogSourceConsent(string Id, string SourceId, string TermsRevision,
    CatalogConsentKind Kind, string EvidenceNote, DateTimeOffset RecordedAt, string ScopeHash = "");
public sealed record CatalogDownloadProgress(string SourceId, CatalogDownloadPhase Phase,
    long BytesReceived = 0, long? TotalBytes = null, string Detail = "", int CompletedSources = 0, int TotalSources = 0);
public sealed record CatalogDownloadItemReport(string SourceId, string File, CatalogDownloadItemStatus Status,
    string Detail, string? Sha256 = null, string? LibraryPath = null);
public sealed record CatalogDownloadProvenance(string SourceId, string SourcePage, string RequestedUrl,
    string FinalUrl, string Sha256, long Bytes, string? ETag, string? LastModified,
    string TermsRevision, string ConsentId, CatalogConsentKind ConsentKind, DateTimeOffset RetrievedAt,
    string? TermsUrl = null, string SourceNotice = "");
public sealed record CatalogDownloadBatchReport(string Id, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    bool Cancelled, long BytesReceived, IReadOnlyList<CatalogDownloadItemReport> Items,
    IReadOnlyList<CatalogDownloadProvenance> Provenance)
{
    public int CatalogsAdded => Items.Count(item => item.Status == CatalogDownloadItemStatus.Imported);
    public string? PersistenceWarning { get; init; }
}

public sealed record CatalogDownloadOptions
{
    public long MaximumFileBytes { get; init; } = 512L * 1024 * 1024;
    public long MaximumBatchBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaximumExpandedBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaximumEntryBytes { get; init; } = 256L * 1024 * 1024;
    public int MaximumArchiveEntries { get; init; } = 20000;
    public int MaximumCompressionRatio { get; init; } = 500;
    public int MaximumRedirects { get; init; } = 4;
    public int MaximumRetries { get; init; } = 2;
    public int ParallelDownloads { get; init; } = 3;
    public int MinimumHostIntervalMilliseconds { get; init; } = 1000;
    public int MaximumRetryDelayMilliseconds { get; init; } = 30000;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed record ManufacturerRegistryEntry(string Name, string? ManufacturerId, int? Code,
    string? MemberStatus, string? Country, string? Website, string SourceUrl,
    string IdentityKind);
public sealed record ManufacturerRegistrySnapshot(DateTimeOffset RetrievedAt,
    IReadOnlyList<ManufacturerRegistryEntry> Entries, int DirectoryCount, int CodeCount,
    string RightsNotice, IReadOnlyList<string> SourceUrls);
public sealed record ManufacturerRegistryProgress(int CompletedPages, int TotalPages, int Entries);
