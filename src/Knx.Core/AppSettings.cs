using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knx;

/// <summary>Application settings (Extras → Optionen). Stored as JSON in %APPDATA%\BusChord.</summary>
public sealed class AppSettings
{
    /// <summary>Folder name of the application inside %APPDATA%.</summary>
    public const string AppFolderName = "BusChord";

    public string Theme { get; set; } = "System";
    public string Language { get; set; } = "de";
    public bool ShowMonitorTimestamps { get; set; } = true;
    public bool ConfirmDelete { get; set; } = true;
    public string? BusInterface { get; set; }
    public string TunnelAddress { get; set; } = "1.1.250";
    public string? LastProject { get; set; }
    public List<string> RecentProjects { get; set; } = [];

    [JsonIgnore]
    public static string Directory
        => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    [JsonIgnore]
    public static string Path => Environment.GetEnvironmentVariable("BUSCHORD_SETTINGS") is { Length: > 0 } custom
        ? custom
        : System.IO.Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load(string? path = null)
    {
        var file = path ?? Path;
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception)
        {
            // A broken/corrupt settings file must not keep the app from starting.
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        var file = path ?? Path;
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, Options));
    }

    /// <summary>Registers a project in the recent list (most recent first, max 10).</summary>
    public void TouchProject(string path)
    {
        LastProject = path;
        RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 10) RecentProjects.RemoveRange(10, RecentProjects.Count - 10);
    }
}
