using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

internal static class ArchiveProtectionTestSettings
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Each check process owns its configuration and never changes user settings.
        var directory = Path.Combine(Path.GetTempPath(), "buschord-check-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "archive-protection.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Salt = "buschord-synthetic-test-salt", Iterations = 4096,
            HashAlgorithm = "SHA256", KeyLength = 32, PasswordEncoding = "utf-8"
        }));
        Environment.SetEnvironmentVariable("BUSCHORD_ARCHIVE_PROTECTION", path);
    }
}
