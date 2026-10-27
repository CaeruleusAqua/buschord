using Avalonia;
using Knx;

namespace Knx.App;

internal static class Program
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    [STAThread]
    public static void Main(string[] args)
    {
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
