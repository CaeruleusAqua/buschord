using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App.Localization;
using Knx.App.Workbench;

namespace Knx.App;

public partial class App : Application
{
    /// <summary>Compatibility harness injection; never selected by product settings or command-line options.</summary>
    public static Func<Window>? TestWindowFactory { get; set; }
    /// <summary>Language code passed on the command line (--lang=en), overriding the settings.</summary>
    public static string? ForcedLanguage { get; set; }

    /// <summary>Theme passed on the command line (--theme=light|dark|system).</summary>
    public static string? ForcedTheme { get; set; }

    /// <summary>Project loaded at startup (--open=&lt;knxproj&gt;), mainly for demos and screenshots.</summary>
    public static string? StartupProject { get; set; }

    /// <summary>Opens the catalogue browser at startup (--catalog).</summary>
    public static bool ShowCatalogOnStartup { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        EnsureToolTipContrast();
    }

    private static bool _toolTipContrastHooked;
    private static void EnsureToolTipContrast()
    {
        if (_toolTipContrastHooked) return;
        _toolTipContrastHooked = true;
        // ToolTip content is created in a PopupRoot outside the window's style scope; the
        // generated TextBlock otherwise keeps Fluent's black foreground in dark mode.
        ToolTip.IsOpenProperty.Changed.AddClassHandler<ToolTip>((tip, args) =>
        {
            if (args.NewValue is not true) return;
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var text in tip.GetVisualDescendants().OfType<TextBlock>())
                    text.Foreground = tip.Foreground;
            }, DispatcherPriority.Background);
        });
        ToolTip.TipProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            if (args.NewValue is not string text) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (control.GetValue(ToolTip.TipProperty) is not string current || current != text) return;
                var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
                block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable("ToolTipForeground"));
                control.SetCurrentValue(ToolTip.TipProperty, block);
            }, DispatcherPriority.Background);
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = AppSettings.Load();
        SettingsWindow.ApplyTheme(ForcedTheme ?? settings.Theme);
        Strings.Language = ForcedLanguage ?? settings.Language;
        WorkbenchHighContrast.Attach(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (TestWindowFactory is { } factory)
            {
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = factory();
                if (desktop.MainWindow is WorkbenchWindow testWorkbench)
                    desktop.ShutdownRequested += (_, _) => testWorkbench.RequestShutdown();
                base.OnFrameworkInitializationCompleted();
                return;
            }
            // The main window owns the process: closing it also ends floated panels,
            // so a single click on X cannot be swallowed by a floating dock window.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var testing = WorkbenchCheck.Enabled || Screenshot.TargetPath is not null;
            var testCatalog = testing ? new Services.CatalogLibrary(new Knx.Catalog.CatalogStore(Path.Combine(WorkbenchCheck.TestDirectory!, "catalog"))) : null;
            var window = testing
                ? new WorkbenchWindow(new Services.MainWindowViewModel(testCatalog!), new Workbench.WorkbenchWorkspace(), catalogLibrary: testCatalog)
                : new WorkbenchWindow();
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => window.RequestShutdown();

            if (WorkbenchCheck.Enabled)
            {
                WorkbenchCheck.Run(desktop, window);
            }
            else if (Screenshot.TargetPath is { } screenshot)
            {
                // Synthetic archive and isolated settings/catalogue only; never load the user's startup project.
                WorkbenchCheck.Capture(desktop, window, screenshot, Screenshot.Screen);
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
                        _ = ConfirmDialog.ShowMessageAsync(window, WorkbenchStrings.Get("ProjectLoadFailed"), error);
                };
                // The splash must never outlive the main window, not even on a failed start-up.
                window.Closed += (_, _) => splash.Close();

                if (StartupProject is { } path)
                {
                    if (!File.Exists(path))
                    {
                        loadError = Strings.Get("MsgFileNotFound", path);
                        window.ViewModel.Status = WorkbenchStrings.Get("ProjectLoadFailedStatus", loadError);
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
                            window.ViewModel.Status = WorkbenchStrings.Get("ProjectLoadFailedStatus", ex.Message);
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
