using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace Knx.Core.Catalog.Downloads;

internal sealed class CatalogByteBudget(long maximum)
{
    private long _received;
    public long Received => Interlocked.Read(ref _received);
    public void Add(int count)
    {
        if (Interlocked.Add(ref _received, count) > maximum)
            throw new InvalidDataException("The catalogue batch exceeded its byte budget.");
    }
}

internal sealed record CatalogTransfer(string Path, Uri FinalUrl, string Sha256, long Bytes,
    string? ETag, string? LastModified);

internal sealed class CatalogHttpTransport : IDisposable
{
    private readonly HttpClient _http;
    private readonly CatalogDownloadOptions _options;
    private readonly SemaphoreSlim _pool;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.OrdinalIgnoreCase);

    public CatalogHttpTransport(CatalogDownloadOptions options, HttpMessageHandler? handler)
    {
        _options = options;
        if (options.MaximumFileBytes <= 0 || options.MaximumBatchBytes <= 0 || options.MaximumEntryBytes <= 0 ||
            options.MaximumExpandedBytes <= 0 || options.MaximumArchiveEntries <= 0 || options.MaximumCompressionRatio <= 0 ||
            options.ParallelDownloads is < 1 or > 8 || options.MaximumRedirects is < 0 or > 8 ||
            options.MaximumRetries is < 0 or > 5 || options.MinimumHostIntervalMilliseconds < 0 ||
            options.MaximumRetryDelayMilliseconds < 0 || options.RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        _pool = new(options.ParallelDownloads);
        _http = new(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
            AutomaticDecompression = DecompressionMethods.None,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BusChord-Catalogues/1.0");
    }

    public static void ValidateUrl(Uri uri, IReadOnlyList<string> hosts)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.IsLoopback ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || IPAddress.TryParse(uri.IdnHost, out _) ||
            !hosts.Any(host => string.Equals(host, uri.IdnHost, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The catalogue URL is outside the reviewed HTTPS source hosts.");
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = Uri.UnescapeDataString(part.Split('=')[0]);
            if (new[] { "password", "access_token", "token", "session", "sessionid", "sid", "force_sid", "api_key" }
                .Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Account or session URLs are not supported catalogue sources.");
        }
    }

    public async Task<CatalogTransfer> FetchAsync(Uri initial, IReadOnlyList<string> hosts, string target,
        CatalogByteBudget budget, long maximumBytes, bool metadata, Action<long, long?>? progress,
        CancellationToken cancellationToken, Action? ensureAuthorized = null)
    {
        ValidateUrl(initial, hosts);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.RequestTimeout);
        var token = deadline.Token;
        var acquired = false;
        try
        {
            await _pool.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            var uri = initial;
            var redirects = 0;
            var retries = 0;
            while (true)
            {
                ensureAuthorized?.Invoke();
                ValidateUrl(uri, hosts);
                var hostGate = _hosts.GetOrAdd(uri.IdnHost, _ => new(1));
                await hostGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (_last.TryGetValue(uri.IdnHost, out var last))
                    {
                        var wait = TimeSpan.FromMilliseconds(_options.MinimumHostIntervalMilliseconds) - (DateTimeOffset.UtcNow - last);
                        if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
                    }
                    _last[uri.IdnHost] = DateTimeOffset.UtcNow;
                    ensureAuthorized?.Invoke();
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                    {
                        if (++redirects > _options.MaximumRedirects || response.Headers.Location is null)
                            throw new InvalidDataException("The source returned an invalid or excessive redirect chain.");
                        uri = new Uri(uri, response.Headers.Location);
                        ValidateUrl(uri, hosts);
                        continue;
                    }
                    if ((int)response.StatusCode is 429 or 502 or 503 or 504 && retries++ < _options.MaximumRetries)
                    {
                        var wait = response.Headers.RetryAfter?.Delta ??
                            (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(retries);
                        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                        if (wait.TotalMilliseconds > _options.MaximumRetryDelayMilliseconds)
                            throw new HttpRequestException("The source requests a later retry.", null, response.StatusCode);
                        await Task.Delay(wait, token).ConfigureAwait(false);
                        continue;
                    }
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        throw new HttpRequestException("This source requires manual access or login in your browser.", null, response.StatusCode);
                    response.EnsureSuccessStatusCode();
                    var content = response.Content;
                    var length = content.Headers.ContentLength;
                    if (length > maximumBytes) throw new InvalidDataException("The download exceeds its file-size limit.");
                    if (content.Headers.ContentEncoding.Count > 0)
                        throw new InvalidDataException("Encoded download responses are not supported.");
                    var type = content.Headers.ContentType?.MediaType;
                    if (!metadata && type is not (null or "application/zip" or "application/x-zip-compressed" or
                        "application/octet-stream" or "application/knxprod" or "application/download"))
                        throw new InvalidDataException("The source returned a document or login page instead of a catalogue archive.");
                    await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[65536];
                    long received = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        budget.Add(read);
                        received = checked(received + read);
                        if (received > maximumBytes) throw new InvalidDataException("The streaming download exceeded its file-size limit.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        progress?.Invoke(received, length);
                    }
                    if (received == 0 || length is not null && length != received)
                        throw new InvalidDataException("The catalogue download is empty or incomplete.");
                    return new(target, uri, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), received,
                        response.Headers.ETag?.ToString(), content.Headers.LastModified?.ToString("O"));
                }
                finally { hostGate.Release(); }
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(target);
            throw new TimeoutException("The catalogue source did not complete within the download time limit.", error);
        }
        catch { TryDelete(target); throw; }
        finally { if (acquired) _pool.Release(); }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _http.Dispose();
        _pool.Dispose();
        foreach (var host in _hosts.Values) host.Dispose();
    }
}
