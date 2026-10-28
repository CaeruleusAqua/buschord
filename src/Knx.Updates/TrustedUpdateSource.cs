using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Knx.Updates;

/// <summary>Only full packages from a publisher-authenticated, scope-bound feed are exposed to Velopack.</summary>
public sealed class TrustedUpdateSource : IUpdateSource, IDisposable
{
    private readonly UpdateTrust _trust;
    private readonly string _rid, _channel, _installed, _directory;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private VerifiedUpdate? _verified;
    internal CancellationToken OperationCancellation { get; set; }
    public TrustedUpdateSource(UpdateTrust trust, string rid, string channel, string installedVersion,
        string directory, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        _trust = trust; _rid = rid; _channel = channel; _installed = installedVersion;
        _directory = Path.GetFullPath(directory); _time = time ?? TimeProvider.System;
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            UseDefaultCredentials = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BusChord-Updater/1.0");
    }
    public bool Configured => _trust.Configured;
    private string StatePath => Path.Combine(_directory, "rollback-" + _rid + "-" + _channel + ".json");
    private string PendingPath => Path.Combine(_directory, "pending-" + _rid + "-" + _channel + ".json");
    private string FailedPath => Path.Combine(_directory, "failed-" + _rid + "-" + _channel + ".json");
    internal bool CurrentRequiresRetry => _verified is not null && FailedHash(_verified.Manifest.Asset.Sha256);
    private bool FailedHash(string hash)
    {
        if (!File.Exists(FailedPath)) return false;
        if (new FileInfo(FailedPath).Length > 16384) throw new InvalidDataException("Excessive update failure record.");
        var failed = JsonSerializer.Deserialize<FailedUpdate>(File.ReadAllText(FailedPath), UpdateManifests.Json)
            ?? throw new InvalidDataException("Invalid update failure record.");
        return string.Equals(failed.Sha256, hash, StringComparison.OrdinalIgnoreCase);
    }
    private RollbackState? ReadState() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize<RollbackState>(File.ReadAllText(StatePath), UpdateManifests.Json)
            ?? throw new InvalidDataException("Invalid update rollback state.") : null;

    public async Task<VerifiedUpdate?> CheckAsync(CancellationToken token = default)
    {
        if (!Configured) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var feed = await ResolveFeedAsync(deadline.Token).ConfigureAwait(false);
        if (feed is null) return null;
        using var response = await GetAsync(feed, deadline.Token, allowNotFound: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var bytes = await ReadBounded(response.Content, UpdateManifests.MaximumManifestBytes, deadline.Token).ConfigureAwait(false);
        Directory.CreateDirectory(_directory);
        // Cross-process serialization prevents two app instances from lowering the high-water mark.
        using var stateLock = new FileStream(Path.Combine(_directory, "state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var verified = UpdateManifests.Verify(Encoding.UTF8.GetString(bytes), _trust, _rid, _channel, _installed, _time.GetUtcNow(), ReadState());
        await AtomicWrite(StatePath, JsonSerializer.Serialize(new RollbackState(verified.Manifest.Sequence,
            verified.Manifest.Asset.Version, verified.PayloadHash), UpdateManifests.Json), token).ConfigureAwait(false);
        return _verified = verified;
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (appId != "BusChord" || channel != _rid + "-" + _channel) throw new InvalidDataException("Unexpected package identity/channel.");
        var update = await CheckAsync(OperationCancellation).ConfigureAwait(false);
        return new() { Assets = update is null ? [] : [ToAsset(update)] };
    }

    private async Task<Uri?> ResolveFeedAsync(CancellationToken token)
    {
        var name = "updates." + _rid + "." + _channel + ".json";
        var basis = _trust.Feed!;
        if (_channel != "preview" || basis.IdnHost != "github.com") return new Uri(basis, name);
        var path = basis.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length < 2 || path.Take(2).Any(part => part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))))
            throw new InvalidDataException("Invalid public GitHub update repository.");
        var repository = path[0] + "/" + path[1];
        using var response = await GetAsync(new Uri("https://api.github.com/repos/" + repository + "/releases?per_page=20"), token, true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var bytes = await ReadBounded(response.Content, 2 * 1024 * 1024, token).ConfigureAwait(false);
        using var releases = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 20 });
        foreach (var release in releases.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || !release.GetProperty("prerelease").GetBoolean()) continue;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != name) continue;
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!url.StartsWith("https://github.com/" + repository + "/releases/download/", StringComparison.Ordinal))
                    throw new InvalidDataException("The preview feed is outside its public release repository.");
                return new Uri(url);
            }
        }
        return null;
    }

    public static VelopackAsset ToAsset(VerifiedUpdate update) => new()
    {
        PackageId = update.Manifest.AppId, FileName = update.Manifest.Asset.FileName,
        Version = SemanticVersion.Parse(update.Manifest.Asset.Version), Type = VelopackAssetType.Full,
        SHA256 = update.Manifest.Asset.Sha256, Size = update.Manifest.Asset.Size,
    };

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken = default)
    {
        var update = _verified ?? throw new InvalidOperationException("Verify a current feed before downloading.");
        var a = update.Manifest.Asset;
        if (releaseEntry.FileName != a.FileName || releaseEntry.Version != SemanticVersion.Parse(a.Version) ||
            releaseEntry.PackageId != "BusChord" || releaseEntry.SHA256 != a.Sha256 || releaseEntry.Size != a.Size || releaseEntry.Type != VelopackAssetType.Full)
            throw new InvalidDataException("The requested update is outside the authenticated feed.");
        var temp = localFile + "." + Guid.NewGuid().ToString("N") + ".staging";
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancelToken); deadline.CancelAfter(TimeSpan.FromMinutes(10));
            var downloadToken = deadline.Token;
            using var response = await GetAsync(new Uri(a.Url), downloadToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != a.Size)
                throw new InvalidDataException("The package size does not match its signed manifest.");
            await using (var input = await response.Content.ReadAsStreamAsync(downloadToken).ConfigureAwait(false))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536]; long received = 0; int read;
                while ((read = await input.ReadAsync(buffer, downloadToken).ConfigureAwait(false)) > 0)
                {
                    received = checked(received + read);
                    if (received > a.Size) throw new InvalidDataException("The package exceeded its signed size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), downloadToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read); progress((int)(100 * received / a.Size));
                }
                if (received != a.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(a.Sha256)))
                    throw new CryptographicException("The package does not match its authenticated hash.");
                await output.FlushAsync(cancelToken).ConfigureAwait(false); output.Flush(flushToDisk: true);
            }
            cancelToken.ThrowIfCancellationRequested();
            File.Move(temp, localFile, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Call after Velopack's download completes; rechecks even its pre-existing package cache.</summary>
    public async Task MarkReadyAsync(string packageDirectory, CancellationToken token = default)
    {
        var verified = _verified ?? throw new InvalidOperationException("No authenticated update.");
        await VerifyPackageAsync(verified, Path.Combine(packageDirectory, verified.Manifest.Asset.FileName), token).ConfigureAwait(false);
        await AtomicWrite(PendingPath, verified.Envelope, token).ConfigureAwait(false);
    }
    public async Task<VelopackAsset?> PendingAsync(string packageDirectory, CancellationToken token = default)
    {
        if (!Configured || !File.Exists(PendingPath)) return null;
        if (new FileInfo(PendingPath).Length > UpdateManifests.MaximumManifestBytes) throw new InvalidDataException("Invalid pending update.");
        var verified = UpdateManifests.Verify(await File.ReadAllTextAsync(PendingPath, token).ConfigureAwait(false),
            _trust, _rid, _channel, _installed, _time.GetUtcNow(), ReadState());
        await VerifyPackageAsync(verified, Path.Combine(packageDirectory, verified.Manifest.Asset.FileName), token).ConfigureAwait(false);
        if (FailedHash(verified.Manifest.Asset.Sha256)) return null;
        return ToAsset(verified);
    }
    public Task RecordApplyFailureAsync(VelopackAsset asset, int exitCode) => AtomicWrite(FailedPath,
        JsonSerializer.Serialize(new FailedUpdate(asset.Version.ToString(), asset.SHA256, exitCode, _time.GetUtcNow()), UpdateManifests.Json), CancellationToken.None);
    public void ClearApplyFailureForUserRetry() { if (File.Exists(FailedPath)) File.Delete(FailedPath); }
    private sealed record FailedUpdate(string Version, string Sha256, int ExitCode, DateTimeOffset AttemptedAt);
    public static async Task VerifyPackageAsync(VerifiedUpdate update, string path, CancellationToken token)
    {
        await using var input = File.OpenRead(path);
        if (input.Length != update.Manifest.Asset.Size) throw new CryptographicException("The staged update size changed.");
        var hash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(update.Manifest.Asset.Sha256)))
            throw new CryptographicException("The staged update hash changed.");
        UpdatePackageScope.Verify(path, update.Manifest);
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token, bool allowNotFound = false)
    {
        for (var redirects = 0; ; redirects++)
        {
            UpdateManifests.ValidateHttps(uri);
            var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var target = response.Headers.Location; response.Dispose();
                if (redirects >= 4 || target is null) throw new InvalidDataException("Invalid update redirect chain.");
                var next = new Uri(uri, target);
                // GitHub's public release asset redirect is the only cross-host redirect accepted.
                if (!string.Equals(uri.IdnHost, next.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                    !(uri.IdnHost == "github.com" && next.IdnHost == "release-assets.githubusercontent.com"))
                    throw new InvalidDataException("The update redirect changed its trusted delivery host.");
                uri = next; continue;
            }
            try
            {
                if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return response;
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentEncoding.Count > 0) throw new InvalidDataException("Encoded update content is unsupported.");
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }
    private static async Task<byte[]> ReadBounded(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("Update metadata exceeds its limit.");
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (output.Length + read > maximum) throw new InvalidDataException("Update metadata exceeds its limit."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    internal static async Task AtomicWrite(string path, string text, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temp, text, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Dispose() => _http.Dispose();
}
