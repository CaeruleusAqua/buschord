using System.Text.Json;
using System.Text.Json.Serialization;
using Knx.Updates;

namespace Knx.App.Services;

public static class AppUpdatePreferences
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
    public static string FilePath => Path.GetFullPath(Environment.GetEnvironmentVariable("BUSCHORD_UPDATE_PREFERENCES")
        ?? Path.Combine(AppUpdateService.StateDirectory, "preferences.json"));

    public static UpdatePreferences Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[16385]; var count = 0;
            while (count < buffer.Length)
            {
                var read = stream.Read(buffer, count, buffer.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count > 16384) throw new InvalidDataException("Update preferences exceed their size limit.");
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count), new() { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
                    != document.RootElement.EnumerateObject().Count())
                throw new InvalidDataException("Update preferences are ambiguous.");
            var value = JsonSerializer.Deserialize<UpdatePreferences>(buffer.AsSpan(0, count), Json)
                ?? throw new InvalidDataException("Update preferences are missing.");
            Validate(value);
            return value;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (JsonException error) { throw new InvalidDataException("Update preferences are invalid.", error); }
    }

    public static void Save(UpdatePreferences preferences, string? path = null)
    {
        Validate(preferences); path = Path.GetFullPath(path ?? FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(preferences, Json);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(UpdatePreferences value)
    {
        if (!Enum.IsDefined(value.Policy) || value.Channel is not ("stable" or "preview"))
            throw new InvalidDataException("Update preferences have an unsupported policy or channel.");
    }
}
