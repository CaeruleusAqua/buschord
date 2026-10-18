using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Knx;

namespace Knx.App;

public partial class App : Application
{
    /// <summary>Language code passed on the command line (--lang=en), overriding the settings.</summary>
    public static string? ForcedLanguage { get; set; }

    /// <summary>Theme passed on the command line (--theme=light|dark|system).</summary>
    public static string? ForcedTheme { get; set; }

    /// <summary>Project loaded at startup (--open=&lt;knxproj&gt;), mainly for demos and screenshots.</summary>
    public static string? StartupProject { get; set; }

    /// <summary>Opens the catalogue browser at startup (--catalog).</summary>
    public static bool ShowCatalogOnStartup { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = AppSettings.Load();
        SettingsWindow.ApplyTheme(ForcedTheme ?? settings.Theme);
        Strings.Language = ForcedLanguage ?? settings.Language;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            if (Screenshot.TargetPath is { } screenshot)
            {
                // Deliberately no project: loading one writes the real settings file.
                Screenshot.Capture(desktop, window, screenshot, Screenshot.Screen);
            }
            else
            {
                if (StartupProject is { } path && File.Exists(path))
                    window.ViewModel.LoadProject(path);

                if (ShowCatalogOnStartup)
                    window.ShowCatalog();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
