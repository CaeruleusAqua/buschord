using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Knx;
using Knx.App.Services;

namespace Knx.App;

/// <summary>
/// --screenshot=&lt;path&gt; renders one window to a PNG at a fixed size and exits, so the
/// UI can be inspected without taking screenshots by hand. --screen=&lt;name&gt; selects the
/// view (main, catalog, parameters, links, settings, new-project); the workspace views use the
/// main window with the matching tab selected.
/// </summary>
internal static class Screenshot
{
    public const int Width = 1440;
    public const int Height = 900;

    internal static readonly string[] Screens =
        ["main", "catalog", "parameters", "links", "settings", "new-project", "splash", "monitor", "inspector",
         "portfolio", "portfolio-small", "portfolio-tiny",
         "demo", "demoparams", "demolinks", "demomonitor", "demoprogramming", "demoinspector", "democatalog",
         "demo-small", "demoparams-small", "demomonitor-small", "demoinspector-small", "democatalog-small",
         "demo-tiny", "demomonitor-tiny", "demoinspector-tiny"];

    /// <summary>PNG path from --screenshot=&lt;path&gt;; null keeps the normal start.</summary>
    public static string? TargetPath { get; set; }

    /// <summary>Window from --screen=&lt;name&gt;, default main.</summary>
    public static string Screen { get; set; } = "main";

    public static bool IsKnownScreen(string screen) =>
        Screens.Contains(screen, StringComparer.OrdinalIgnoreCase);

    internal static void Write(Window window, string path, PixelSize size)
    {
        var full = System.IO.Path.GetFullPath(path);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);

        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(full);
        Console.WriteLine($"Screenshot: {full} ({size.Width}x{size.Height})");
    }

    /// <summary>
    /// --screenshot-selftest: checks main/programming captures and rejects an unknown screen.
    /// </summary>
    public static int SelfTest()
    {
        var failures = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
            if (!ok) failures++;
        }

        var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "opencode", "buschord-screenshot-" + Guid.NewGuid().ToString("N"));
        var good = System.IO.Path.Combine(directory, "main.png");
        var bogus = System.IO.Path.Combine(directory, "bogus.png");
        var programming = System.IO.Path.Combine(directory, "programming.png");
        foreach (var file in new[] { good, bogus })
            if (File.Exists(file)) File.Delete(file);

        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Eigener Prozesspfad fehlt");

        var goodCode = Run(exe, $"--screenshot={good}");
        Check("Screenshot beendet mit 0", goodCode == 0, goodCode.ToString());
        Check("Screenshot-Datei existiert", File.Exists(good));
        if (File.Exists(good))
        {
            var bytes = File.ReadAllBytes(good);
            var (width, height) = PngSize(bytes);
            Check("PNG größer als 10 KB", bytes.Length > 10_240, bytes.Length.ToString());
            Check("PNG hat eine plausible Erfassungsgröße", width >= 900 && height >= 600 && width <= Width + 1 && height <= Height + 1,
                $"{width}x{height}");
        }

        var bogusCode = Run(exe, $"--screenshot={bogus}", "--screen=bogus");
        Check("Unbekannter Screen schlägt fehl", bogusCode != 0, bogusCode.ToString());
        Check("Unbekannter Screen schreibt kein PNG", !File.Exists(bogus));

        var programmingCode = Run(exe, $"--screenshot={programming}", "--screen=demoprogramming");
        Check("Programming screenshot exits successfully", programmingCode == 0, programmingCode.ToString());
        Check("Programming screenshot exists", File.Exists(programming));

        Console.WriteLine(failures == 0 ? "SCREENSHOT OK" : $"SCREENSHOT {failures} FEHLER");
        return failures == 0 ? 0 : 1;
    }

    private static int Run(string exe, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (System.IO.Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(typeof(Screenshot).Assembly.Location);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>Reads width/height from the PNG IHDR chunk (no image library needed).</summary>
    private static (int Width, int Height) PngSize(byte[] bytes)
    {
        static int Int32(byte[] buffer, int offset) =>
            (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];

        return bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N'
            ? (Int32(bytes, 16), Int32(bytes, 20))
            : (0, 0);
    }
}
