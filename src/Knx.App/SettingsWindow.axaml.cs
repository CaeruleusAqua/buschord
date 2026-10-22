using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Knx;

namespace Knx.App;

/// <summary>Extras → Optionen: settings, theme and language (applied immediately).</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        ThemeBox.SelectedIndex = settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        LanguageBox.SelectedIndex = Strings.Normalize(settings.Language) switch
        {
            "en" => 1,
            "fr" => 2,
            _ => 0,
        };
        ConfirmDeleteBox.IsChecked = settings.ConfirmDelete;
        BusInterfaceBox.Text = settings.BusInterface;
        TunnelAddressBox.Text = settings.TunnelAddress;
        RecentList.ItemsSource = settings.RecentProjects.ToArray();
        RecentList.IsVisible = settings.RecentProjects.Count > 0;
        RecentEmpty.IsVisible = settings.RecentProjects.Count == 0;
        Opened += (_, _) => ThemeBox.Focus();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        _settings.Theme = ThemeBox.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
        _settings.Language = LanguageBox.SelectedIndex switch
        {
            1 => "en",
            2 => "fr",
            _ => "de",
        };
        _settings.ConfirmDelete = ConfirmDeleteBox.IsChecked == true;
        _settings.BusInterface = string.IsNullOrWhiteSpace(BusInterfaceBox.Text) ? null : BusInterfaceBox.Text;
        if (!string.IsNullOrWhiteSpace(TunnelAddressBox.Text))
            _settings.TunnelAddress = TunnelAddressBox.Text!;
        _settings.Save();

        ApplyTheme(_settings.Theme);
        Strings.Language = _settings.Language;
        Close(true);
    }

    /// <summary>Applies the theme variants of the options dialog to the running app.</summary>
    public static void ApplyTheme(string theme)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = theme.ToLowerInvariant() switch
        {
            "light" or "hell" => ThemeVariant.Light,
            "dark" or "dunkel" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
