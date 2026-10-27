using Avalonia;
using Knx;
using Knx.App.Workbench;

namespace Knx.App;

internal static class Program
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    [STAThread]
    public static void Main(string[] args)
    {
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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
}
