using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Knx;

/// <summary>Global settings for password-protected project archives, independent of workbench preferences.</summary>
public sealed class ArchiveProtectionSettings
{
    // Default salt value from XKNX/xknxproject, _generate_ets6_zip_password:
    // https://github.com/XKNX/xknxproject/blob/7013d4e5eb92f971c1ac6eecfe6de72464b96498/xknxproject/zip/extractor.py#L207-L218
    private const string DefaultSalt = "21.project.ets.knx.org";
    private const int MaximumFileBytes = 16 * 1024;
    private const int MaximumSaltCharacters = 4096;
    private const int MaximumPasswordCharacters = 65536;
    private const string InvalidProfile = "Archive protection settings are invalid or exceed their safety limits.";
    internal const string MissingSaltMessage = "Archive protection is not configured. Set the global archive protection salt before opening a protected project.";
    public string? Salt { get; set; }
    public int Iterations { get; set; } = 100000;
    public string HashAlgorithm { get; set; } = "SHA256";
    public int KeyLength { get; set; } = 32;
    public string PasswordEncoding { get; set; } = "utf-8";
    public static string FilePath => ResolvePath(null);

    public static ArchiveProtectionSettings Load(string? path = null)
    {
        var resolved = ResolvePath(path);
        try
        {
            using var input = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[MaximumFileBytes + 1]; var length = 0;
            while (length < bytes.Length)
            {
                var read = input.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length > MaximumFileBytes) throw new InvalidDataException(InvalidProfile);
            using var json = JsonDocument.Parse(bytes.AsMemory(0, length), new() { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException(InvalidProfile);
            var result = new ArchiveProtectionSettings(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException(InvalidProfile);
                switch (property.Name.ToLowerInvariant())
                {
                    case "salt":
                        if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new InvalidDataException(InvalidProfile);
                        result.Salt = property.Value.GetString(); break;
                    case "iterations": result.Iterations = Integer(property.Value); break;
                    case "hashalgorithm": result.HashAlgorithm = Text(property.Value); break;
                    case "keylength": result.KeyLength = Integer(property.Value); break;
                    case "passwordencoding": result.PasswordEncoding = Text(property.Value); break;
                    default: throw new InvalidDataException(InvalidProfile);
                }
            }
            _ = result.Validate(requireSalt: false);
            return result;
        }
        catch (FileNotFoundException) { return DefaultProfile(); }
        catch (DirectoryNotFoundException) { return DefaultProfile(); }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("Archive protection settings could not be read or are invalid."); }
    }

    private static ArchiveProtectionSettings DefaultProfile() => new()
    {
        Salt = DefaultSalt, Iterations = 65536, HashAlgorithm = "SHA256",
        KeyLength = 32, PasswordEncoding = "utf-16le",
    };

    public void Save(string? path = null)
    {
        var profile = Validate(requireSalt: false); var resolved = ResolvePath(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            salt = profile.Salt, iterations = profile.Iterations, hashAlgorithm = profile.Hash.Name,
            keyLength = profile.KeyLength, passwordEncoding = profile.EncodingName,
        }, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException(InvalidProfile);
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(resolved)!; Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".archive-protection-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            File.Move(temporary, resolved, overwrite: true); temporary = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new InvalidDataException("Archive protection settings could not be saved."); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal string DeriveZipPassword(string password)
    {
        var profile = Validate(requireSalt: true);
        if (password is null || password.Length > MaximumPasswordCharacters) throw new InvalidDataException(InvalidProfile);
        byte[]? passwordBytes = null, saltBytes = null, derived = null;
        try
        {
            passwordBytes = profile.Encoding.GetBytes(password);
            saltBytes = new UTF8Encoding(false, true).GetBytes(profile.Salt!);
            derived = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, saltBytes, profile.Iterations, profile.Hash, profile.KeyLength);
            return Convert.ToBase64String(derived);
        }
        catch (EncoderFallbackException) { throw new InvalidDataException(InvalidProfile); }
        finally
        {
            if (passwordBytes is not null) CryptographicOperations.ZeroMemory(passwordBytes);
            if (saltBytes is not null) CryptographicOperations.ZeroMemory(saltBytes);
            if (derived is not null) CryptographicOperations.ZeroMemory(derived);
        }
    }

    private sealed record Profile(string? Salt, int Iterations, HashAlgorithmName Hash, int KeyLength, string EncodingName, Encoding Encoding);
    private Profile Validate(bool requireSalt)
    {
        var salt = Salt; var iterations = Iterations; var hash = HashAlgorithm; var keyLength = KeyLength; var encoding = PasswordEncoding;
        if (salt is { Length: > MaximumSaltCharacters } || salt?.Any(char.IsControl) == true
            || iterations is < 1 or > 1000000 || keyLength is < 1 or > 128)
            throw new InvalidDataException(InvalidProfile);
        var algorithm = hash switch
        {
            "SHA256" => HashAlgorithmName.SHA256, "SHA384" => HashAlgorithmName.SHA384, "SHA512" => HashAlgorithmName.SHA512,
            _ => throw new InvalidDataException(InvalidProfile),
        };
        var passwordEncoding = encoding switch
        {
            "utf-8" => (Encoding)new UTF8Encoding(false, true),
            "utf-16le" => new UnicodeEncoding(false, false, true),
            _ => throw new InvalidDataException(InvalidProfile),
        };
        try { if (salt is not null) _ = new UTF8Encoding(false, true).GetByteCount(salt); }
        catch (EncoderFallbackException) { throw new InvalidDataException(InvalidProfile); }
        if (requireSalt && string.IsNullOrEmpty(salt)) throw new InvalidDataException(MissingSaltMessage);
        return new(salt, iterations, algorithm, keyLength, encoding, passwordEncoding);
    }

    private static int Integer(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw new InvalidDataException(InvalidProfile);
    private static string Text(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException(InvalidProfile);
    private static string ResolvePath(string? path)
    {
        var configured = path ?? Environment.GetEnvironmentVariable("BUSCHORD_ARCHIVE_PROTECTION");
        if (configured is null)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) throw new InvalidDataException("The global archive protection settings location is unavailable.");
            configured = Path.Combine(appData, "BusChord", "archive-protection.json");
        }
        if (configured.Length is 0 or > 4096 || configured.IndexOf('\0') >= 0)
            throw new InvalidDataException("The archive protection settings path is invalid.");
        try { return Path.GetFullPath(configured); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("The archive protection settings path is invalid."); }
    }
}
