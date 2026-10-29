using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knx.Recovery;

internal static class RecoveryWire
{
    internal const int ManifestLimit = 8 * 1024 * 1024;
    internal const long UniqueBlobLimit = 2L * 1024 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        MaxDepth = 16, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static T Decode<T>(byte[] data, int maximum)
    {
        if (data.Length > maximum) throw Reject(RecoveryFailure.LimitExceeded, "Recovery.MetadataTooLarge");
        try
        {
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 16 });
            var nodes = 0;
            void Visit(JsonElement node)
            {
                if (++nodes > 1000000) throw Reject(RecoveryFailure.LimitExceeded, "Recovery.MetadataTooComplex");
                if (node.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in node.EnumerateObject())
                    {
                        if (!names.Add(property.Name)) throw Reject(RecoveryFailure.Corrupt, "Recovery.DuplicateProperty");
                        Visit(property.Value);
                    }
                }
                else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) Visit(child);
            }
            Visit(document.RootElement);
            return JsonSerializer.Deserialize<T>(data, Json) ?? throw Reject(RecoveryFailure.Corrupt, "Recovery.NullMetadata");
        }
        catch (JsonException error) { throw Reject(RecoveryFailure.Corrupt, "Recovery.InvalidJson", error); }
    }

    internal static byte[] Encode<T>(T value, int maximum)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > maximum) throw Reject(RecoveryFailure.LimitExceeded, "Recovery.MetadataTooLarge");
        return bytes;
    }
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit) && value == value.ToUpperInvariant();
    internal static bool IsId(string? value) => value is { Length: 32 } && Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;
    internal static void Id(string? value) { if (!IsId(value)) throw Reject(RecoveryFailure.Corrupt, "Recovery.InvalidId"); }
    internal static void PlainText(string? value, int maximum, bool nullable = false)
    {
        if (value is null ? !nullable : value.Length > maximum || value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw Reject(RecoveryFailure.Corrupt, "Recovery.InvalidText");
    }
    internal static void NoLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Reject(RecoveryFailure.Corrupt, "Recovery.StorageLink");
    }
    internal static async Task<byte[]> ReadAsync(string path, int maximum, CancellationToken token)
    {
        NoLink(path);
        await using var input = RecoveryStoragePermissions.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum) throw Reject(RecoveryFailure.LimitExceeded, "Recovery.MetadataTooLarge");
        var data = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(data, token).ConfigureAwait(false);
        if (await input.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw Reject(RecoveryFailure.Corrupt, "Recovery.MetadataChanged");
        return data;
    }
    internal static async Task AtomicAsync(string path, byte[] bytes, bool replace, CancellationToken token)
    {
        var parent = Path.GetDirectoryName(path)!; RecoveryStoragePermissions.Directory(parent); NoLink(parent); NoLink(path);
        var temp = Path.Combine(parent, ".recovery-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = RecoveryStoragePermissions.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(temp, path, replace);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    internal static RecoveryRejectedException Reject(RecoveryFailure failure, string code, Exception? cause = null)
        => new(failure, cause is null ? code : code + " (" + cause.GetType().Name + ")");
}
