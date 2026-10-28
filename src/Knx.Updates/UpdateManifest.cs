using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Velopack;

namespace Knx.Updates;

public enum UpdatePolicy { Disabled, CheckOnly, Automatic }
public sealed record UpdatePreferences(UpdatePolicy Policy = UpdatePolicy.Automatic, string Channel = "stable");
public sealed record UpdateTrust(Uri? Feed, IReadOnlyDictionary<string, string> PublicKeys)
{
    // Release builds replace this with reviewed public trust anchors. Never learn a key from a feed.
    public static UpdateTrust Unconfigured { get; } = new(null, new Dictionary<string, string>());
    public bool Configured => Feed is not null && PublicKeys.Count > 0;
}
public sealed record UpdateAsset(string FileName, string Version, string Url, string Sha256, long Size);
public sealed record UpdateManifest(int Schema, string AppId, string Rid, string Channel, long Sequence,
    DateTimeOffset PublishedAt, DateTimeOffset ExpiresAt, UpdateAsset Asset);
public sealed record SignedUpdateEnvelope(string KeyId, string Payload, string Signature);
public sealed record VerifiedUpdate(UpdateManifest Manifest, string Envelope, string PayloadHash);
public sealed record RollbackState(long Sequence, string Version, string PayloadHash);

public static class UpdateManifests
{
    public const int MaximumManifestBytes = 128 * 1024;
    public const long MaximumPackageBytes = 1024L * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 12,
    };

    public static string Sign(UpdateManifest manifest, string keyId, ECDsa key)
    {
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new CryptographicException("A P-256 signing key is required.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        return JsonSerializer.Serialize(new SignedUpdateEnvelope(keyId, Convert.ToBase64String(payload),
            Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))), Json);
    }

    public static VerifiedUpdate Verify(string envelope, UpdateTrust trust, string rid, string channel,
        string installedVersion, DateTimeOffset now, RollbackState? previous = null)
    {
        if (envelope.Length > MaximumManifestBytes) throw new InvalidDataException("The update manifest exceeds its size limit.");
        var signed = JsonSerializer.Deserialize<SignedUpdateEnvelope>(envelope, Json) ?? throw new InvalidDataException("Missing signed update manifest.");
        if (signed.KeyId is null || !trust.PublicKeys.TryGetValue(signed.KeyId, out var pem)) throw new CryptographicException("Unknown update signing key.");
        var payload = Convert.FromBase64String(signed.Payload);
        var signature = Convert.FromBase64String(signed.Signature);
        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" || signature.Length != 64 || !key.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new CryptographicException("The update signature is invalid.");
        var m = JsonSerializer.Deserialize<UpdateManifest>(payload, Json) ?? throw new InvalidDataException("Missing update payload.");
        if (m.Schema != 1 || m.AppId != "BusChord" || m.Rid != rid || m.Channel != channel ||
            channel is not ("stable" or "preview") || rid is not ("win-x64" or "linux-x64" or "win-arm64" or "linux-arm64") ||
            m.Sequence < 1 || m.Asset is null || m.PublishedAt > now.AddMinutes(5) || m.ExpiresAt <= now ||
            m.ExpiresAt <= m.PublishedAt || m.ExpiresAt - m.PublishedAt > TimeSpan.FromDays(14))
            throw new InvalidDataException("The update manifest has the wrong scope or is expired.");
        var a = m.Asset;
        if (string.IsNullOrWhiteSpace(a.FileName) || a.FileName.Length > 200 || a.FileName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) ||
            !a.FileName.EndsWith("-full.nupkg", StringComparison.Ordinal) || a.Size is <= 0 or > MaximumPackageBytes ||
            a.Sha256 is not { Length: 64 } || !a.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The update package metadata is invalid.");
        ValidateHttps(new Uri(a.Url, UriKind.Absolute));
        var version = SemanticVersion.Parse(a.Version);
        if (version < SemanticVersion.Parse(installedVersion) || channel == "stable" && version.IsPrerelease)
            throw new InvalidDataException("An update must move forward in the selected channel.");
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        if (previous is not null && (m.Sequence < previous.Sequence || version < SemanticVersion.Parse(previous.Version) ||
            m.Sequence == previous.Sequence && hash != previous.PayloadHash))
            throw new InvalidDataException("The update manifest violates the rollback guard.");
        return new(m, envelope, hash);
    }

    public static void ValidateHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 || uri.IsLoopback || System.Net.IPAddress.TryParse(uri.IdnHost, out _))
            throw new InvalidDataException("Updates require a public HTTPS address without credentials.");
    }
}
