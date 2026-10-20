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
         "demo", "demoparams", "demolinks", "demomonitor", "demoprogramming", "demoinspector", "democatalog",
         "demo-small", "demoparams-small", "demomonitor-small"];

    /// <summary>PNG path from --screenshot=&lt;path&gt;; null keeps the normal start.</summary>
    public static string? TargetPath { get; set; }

    /// <summary>Window from --screen=&lt;name&gt;, default main.</summary>
    public static string Screen { get; set; } = "main";

    public static bool IsKnownScreen(string screen) =>
        Screens.Contains(screen, StringComparer.OrdinalIgnoreCase);

    /// <summary>Shows the requested view at the fixed size, writes the PNG and exits.</summary>
    public static void Capture(IClassicDesktopStyleApplicationLifetime desktop, MainWindow main,
        string path, string screen)
    {
        Window window = main;
        Task? catalogueReady = null;
        Action? afterShown = null;
        switch (screen.ToLowerInvariant())
        {
            case "catalog":
                main.ShowCatalog();
                // Capture the real catalogue, not the loading frame: the pane reports when its data is bound.
                catalogueReady = main.CatalogReady;
                break;
            case "parameters":
                // Demo project and parameter set live in memory only: no settings or file write.
                // Activate after the dock has been shown so the document is realised.
                afterShown = () =>
                {
                    DemoProject(main.ViewModel);
                    var (instances, types) = DemoParameters();
                    main.ShowDemoParameters(instances, types);
                };
                break;
            case "links":
                afterShown = () =>
                {
                    DemoProject(main.ViewModel);
                    main.ShowLinks();
                };
                break;
            case "settings":
                window = new SettingsWindow(main.ViewModel.Settings);
                break;
            case "new-project":
                window = new NewProjectWindow();
                break;
            case "splash":
                window = new SplashWindow();
                break;
        }

        var captureSize = screen.Equals("splash", StringComparison.OrdinalIgnoreCase)
            ? new PixelSize(380, 420)
            : new PixelSize(Width, Height);
        window.Width = captureSize.Width;
        window.Height = captureSize.Height;

        // The frame is only complete after opening and one layout/render cycle; the short
        // delay also lets the initial theme transitions settle.
        window.Opened += (_, _) =>
        {
            afterShown?.Invoke();
            var started = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, _) =>
            {
                if (started.Elapsed < TimeSpan.FromMilliseconds(
                        screen is "parameters" or "links" ? 1500 : 300)) return;
                // The catalogue screen waits for the library service (up to 90 s) instead of
                // capturing an empty frame; other screens only need the render cycle.
                if (catalogueReady is { IsCompleted: false } && started.Elapsed < TimeSpan.FromSeconds(90)) return;
                timer.Stop();
                try
                {
                    Write(window, path, captureSize);
                    desktop.Shutdown(0);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Screenshot fehlgeschlagen: {ex.Message}");
                    desktop.Shutdown(1);
                }
            };
            timer.Start();
        };

        // No owner: the main window is never shown when another screen is captured.
        window.Show();
    }

    internal static void Write(Window window, string path, PixelSize size)
    {
        var full = System.IO.Path.GetFullPath(path);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);

        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(full);
        Console.WriteLine($"Screenshot: {full} ({size.Width}x{size.Height})");
    }

    /// <summary>Demoware: a synthetic parameter set for the workspace screenshot.</summary>
    private static (List<ParameterInstance> Instances, List<ParameterType> Types) DemoParameters()
    {
        var types = new List<ParameterType>
        {
            new() { Id = "PT-1", Name = "Helligkeit", DataType = ParameterDataType.Number, Unit = "%" },
            new() { Id = "PT-2", Name = "Modus", DataType = ParameterDataType.Enumeration },
            new() { Id = "PT-3", Name = "Aktiv", DataType = ParameterDataType.Boolean },
        };
        types[1].Restrictions.Add(new ParameterRestriction { EnumerationValue = "Automatik" });
        types[1].Restrictions.Add(new ParameterRestriction { EnumerationValue = "Hand" });
        var instances = new List<ParameterInstance>
        {
            new() { Id = "PI-1", Name = "Helligkeit", ParameterTypeId = "PT-1", Value = "128" },
            new() { Id = "PI-2", Name = "Modus", ParameterTypeId = "PT-2", Value = "Hand" },
            new() { Id = "PI-3", Name = "Aktiv", ParameterTypeId = "PT-3", Value = "1" },
        };
        return (instances, types);
    }

    /// <summary>In-memory demo project so workspace screens show real rows; never writes settings or files.</summary>
    private static void DemoProject(MainWindowViewModel vm)
    {
        var project = new Project { Id = "P-DEMO", Name = "Demo" };
        var installation = new Installation { Id = "I-1", Name = "Haus" };
        var area = new Area { Id = "A-1", Address = 1, Name = "Erdgeschoss" };
        var line = new Line { Id = "L-1", Address = 1, Name = "Beleuchtung" };
        var device = new DeviceInstance
        {
            Id = "1.1.1", Address = new IndividualAddress(1, 1, 1), Name = "Dimmaktor Wohnzimmer",
            ProductRefId = "M-00FA_A-0001-01-0000",
        };
        device.ComObjectRefs.Add(new ComObjectInstanceRef { Id = "CO-1", ComObjectId = "CO-1", Number = 1, Text = "Schalten" });
        device.ComObjectRefs.Add(new ComObjectInstanceRef { Id = "CO-2", ComObjectId = "CO-2", Number = 2, Text = "Dimmen" });
        device.ComObjectRefs[0].Links.Add("GA-1");
        line.Devices.Add(device);
        area.Lines.Add(line);
        installation.Areas.Add(area);
        var range = new GroupRange { Id = "GR-1", Name = "Licht", RangeStart = 1, RangeEnd = 1 };
        range.Addresses.Add(new GroupAddressEntry
        {
            Id = "GA-1", Name = "Wohnzimmer Schalten", Address = GroupAddress.ThreeLevel(1, 1, 1), DatapointType = "DPST-1-1",
        });
        range.Addresses.Add(new GroupAddressEntry
        {
            Id = "GA-2", Name = "Wohnzimmer Dimmen", Address = GroupAddress.ThreeLevel(1, 1, 2), DatapointType = "DPST-5-1",
        });
        installation.MainRanges.Add(range);
        project.Installations.Add(installation);
        vm.CreateProject(project, null);
        vm.SelectedDevice = vm.Devices.FirstOrDefault();
    }

    /// <summary>
    /// --screenshot-selftest: runs the app twice in screenshot mode and checks that a
    /// non-trivial PNG is produced and that an unknown --screen fails.
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
            Check("PNG ist 1440x900", width == Width && height == Height, $"{width}x{height}");
        }

        var bogusCode = Run(exe, $"--screenshot={bogus}", "--screen=bogus");
        Check("Unbekannter Screen schlägt fehl", bogusCode != 0, bogusCode.ToString());
        Check("Unbekannter Screen schreibt kein PNG", !File.Exists(bogus));

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
