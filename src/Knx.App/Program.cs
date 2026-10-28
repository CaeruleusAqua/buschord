using Avalonia;
using Knx;
using Knx.App.Workbench;
using Knx.App.Services;
using Knx.App.Localization;
using Knx.Updates;
using Velopack;
using System.Runtime.InteropServices;

namespace Knx.App;

internal static class Program
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        if (args.Contains("--workbench-selftest")) WorkbenchCheck.Enabled = true;
        if (WorkbenchCheck.Enabled || args.Any(a => a.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase)))
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "workbench-shell-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(root, "settings.json"));
            WorkbenchCheck.TestDirectory = root;
        }
        if (args is ["--smoke"])
        {
            Smoke.Run();
            return;
        }

        if (args is ["--screenshot-selftest"])
        {
            Environment.Exit(Screenshot.SelfTest());
            return;
        }

        foreach (var argument in args)
        {
            if (argument.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
                App.ForcedLanguage = argument["--lang=".Length..];
            else if (argument.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
                App.ForcedTheme = argument["--theme=".Length..];
            else if (argument.StartsWith("--open=", StringComparison.OrdinalIgnoreCase))
                App.StartupProject = argument["--open=".Length..];
            else if (argument.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase))
                Screenshot.TargetPath = argument["--screenshot=".Length..];
            else if (argument.StartsWith("--screen=", StringComparison.OrdinalIgnoreCase))
                Screenshot.Screen = argument["--screen=".Length..];
            else if (argument.Equals("--catalog", StringComparison.OrdinalIgnoreCase))
                App.ShowCatalogOnStartup = true;
        }

        if (Screenshot.TargetPath is not null && !Screenshot.IsKnownScreen(Screenshot.Screen))
        {
            Console.Error.WriteLine(
                $"Unbekannter Screen '{Screenshot.Screen}'. Erlaubt: {string.Join(", ", Screenshot.Screens)}");
            Environment.Exit(2);
        }

        // --log pipes Avalonia's binding/property diagnostics to stderr (they go to Trace by default).
        if (args.Contains("--log"))
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());

        if (WorkbenchCheck.Enabled || Screenshot.TargetPath is not null)
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return;
        }

        using var updates = new AppUpdateService();
        AppUpdateService.Current = updates;
        try
        {
            // No project or window exists at this boundary. A failed attempt leaves the
            // current version available; the broker's one-shot fallback avoids restart loops.
            if (!args.Contains("--skip-auto-update-once", StringComparer.Ordinal)
                && updates.TryApplyAsync(restart: true, args).GetAwaiter().GetResult()) return;
            IDisposable? lease = null;
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    try
                    {
                        lease = AppUpdateService.PackageManaged ? UpdateProcessLease.EnterPackageManagedApplication()
                            : UpdateProcessLease.EnterApplication(AppUpdateService.StateDirectory);
                        break;
                    }
                    catch (IOException) when (attempt < 39) { Thread.Sleep(250); }
                    catch (IOException) { ShowUpdateBusy(); return; }
                    catch (UnauthorizedAccessException) { ShowUpdateBusy(); return; }
                }
            }
            var exitCode = 1;
            try { exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
            finally
            {
                updates.StopAsync().GetAwaiter().GetResult();
                lease?.Dispose();
            }
            Environment.ExitCode = exitCode;
            // The existing workbench save/discard gate has already completed. Other
            // application instances still hold their own lease and prevent the swap.
            if (exitCode == 0) updates.TryApplyAsync(restart: false, []).GetAwaiter().GetResult();
        }
        finally { AppUpdateService.Current = null; }
    }

    private static void ShowUpdateBusy()
    {
        try { Strings.Language = App.ForcedLanguage ?? AppSettings.Load().Language; } catch { }
        var message = UpdateUiStrings.Get("StartupBlocked");
        if (OperatingSystem.IsWindows()) _ = MessageBoxW(IntPtr.Zero, message, "BusChord", 0x40);
        else Console.Error.WriteLine(message);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
