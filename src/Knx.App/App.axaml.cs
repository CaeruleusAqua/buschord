using Avalonia;
using Avalonia.Controls;
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
            // The main window owns the process: closing it also ends floated panels,
            // so a single click on X cannot be swallowed by a floating dock window.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var window = new MainWindow();
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => window.RequestShutdown();

            if (Screenshot.TargetPath is { } screenshot)
            {
                // Deliberately no project: loading one writes the real settings file.
                Screenshot.Capture(desktop, window, screenshot, Screenshot.Screen);
            }
            else
            {
                // The splash is only cosmetic: it never blocks start-up and disappears
                // as soon as the main window has rendered.
                var splash = new SplashWindow();
                splash.Show();
                string? loadError = null;
                window.Opened += (_, _) =>
                {
                    splash.Close();
                    if (loadError is { } error)
                        _ = ConfirmDialog.ShowMessageAsync(window, "Projekt nicht ladbar", error);
                };
                // The splash must never outlive the main window, not even on a failed start-up.
                window.Closed += (_, _) => splash.Close();

                if (StartupProject is { } path)
                {
                    if (!File.Exists(path))
                    {
                        loadError = $"Die Datei '{path}' wurde nicht gefunden.";
                        window.ViewModel.Status = $"Projekt nicht ladbar: {loadError}";
                    }
                    else
                    {
                        try
                        {
                            window.ViewModel.LoadProject(path);
                        }
                        catch (Exception ex)
                        {
                            // A corrupt project must not take the process down or leave the
                            // topmost splash behind: the app continues without a project.
                            loadError = ex.Message;
                            window.ViewModel.Status = $"Projekt nicht ladbar: {ex.Message}";
                        }
                    }
                }

                if (ShowCatalogOnStartup)
                    window.ShowCatalog();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
