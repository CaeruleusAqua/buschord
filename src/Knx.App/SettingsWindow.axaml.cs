using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Knx;
using System.Text.Json;

namespace Knx.App;

/// <summary>Extras → Optionen: settings, theme and language (applied immediately).</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly double _minimumWidth;
    private readonly double _minimumHeight;
    private readonly bool _archiveProtectionLoaded;
    private readonly string? _originalArchiveSalt;
    private bool _opened;
    private bool _viewportPending;

    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        WorkbenchDialogChrome.Apply(this);
        _minimumWidth = MinWidth;
        _minimumHeight = MinHeight;
        ThemeBox.SelectedIndex = settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        LanguageBox.ItemsSource = Strings.Options;
        LanguageBox.SelectedItem = Strings.Options.FirstOrDefault(option =>
            option.Code == Strings.Normalize(settings.Language)) ?? Strings.Options[0];
        ConfirmDeleteBox.IsChecked = settings.ConfirmDelete;
        BusInterfaceBox.Text = settings.BusInterface;
        TunnelAddressBox.Text = settings.TunnelAddress;
        RecentList.ItemsSource = settings.RecentProjects.ToArray();
        RecentList.IsVisible = settings.RecentProjects.Count > 0;
        RecentEmpty.IsVisible = settings.RecentProjects.Count == 0;
        try
        {
            _originalArchiveSalt = ArchiveProtectionSettings.Load().Salt;
            ArchiveSaltBox.Text = _originalArchiveSalt;
            _archiveProtectionLoaded = true;
        }
        catch (Exception error) when (IsSettingsError(error))
        {
            ArchiveSaltBox.IsEnabled = false;
            ShowSettingsError("OptionsArchiveProtectionLoadError");
        }
        Opened += (_, _) =>
        {
            _opened = true;
            if (Screens is { } screens) screens.Changed += OnScreensChanged;
            FitWorkingArea();
            ScheduleViewportFit();
            ThemeBox.Focus();
        };
        Resized += (_, _) => ScheduleViewportFit();
        ScalingChanged += (_, _) => ScheduleViewportFit();
        Closed += (_, _) =>
        {
            _opened = false;
            if (Screens is { } screens) screens.Changed -= OnScreensChanged;
        };
    }

    private void OnScreensChanged(object? sender, EventArgs e) => ScheduleViewportFit();

    private void ScheduleViewportFit()
    {
        if (!_opened || _viewportPending) return;
        _viewportPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _viewportPending = false;
            if (_opened) FitWorkingArea();
        }, DispatcherPriority.Loaded);
    }

    private void FitWorkingArea()
    {
        if (!IsVisible || WindowState != WindowState.Normal || FrameSize is not { } frame
            || Screens?.ScreenFromWindow(this) is not { } screen) return;
        var scale = RenderScaling;
        var area = screen.WorkingArea;
        if (!double.IsFinite(scale) || scale <= 0 || area.Width <= 0 || area.Height <= 0
            || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        // The native frame supplies the actual decorations in the same units as ClientSize.
        var borderWidth = Math.Max(0, frame.Width - ClientSize.Width);
        var borderHeight = Math.Max(0, frame.Height - ClientSize.Height);
        var maximumWidth = Math.Max(1, Math.Floor(area.Width / scale - borderWidth));
        var maximumHeight = Math.Max(1, Math.Floor(area.Height / scale - borderHeight));
        MinWidth = Math.Min(_minimumWidth, maximumWidth);
        MinHeight = Math.Min(_minimumHeight, maximumHeight);
        MaxWidth = maximumWidth;
        MaxHeight = maximumHeight;
        var width = Math.Clamp(double.IsFinite(Width) ? Width : ClientSize.Width, MinWidth, maximumWidth);
        var height = Math.Clamp(double.IsFinite(Height) ? Height : ClientSize.Height, MinHeight, maximumHeight);
        Width = width;
        Height = height;

        var frameWidth = (int)Math.Ceiling((width + borderWidth) * scale);
        var frameHeight = (int)Math.Ceiling((height + borderHeight) * scale);
        var position = new PixelPoint(
            Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - frameWidth)),
            Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - frameHeight)));
        if (Position != position) Position = position;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (!_archiveProtectionLoaded)
        {
            ShowSettingsError("OptionsArchiveProtectionLoadError");
            return;
        }
        AppSettings candidate;
        try
        {
            // Reload the independent profile so only the edited salt is replaced.
            var profilePath = ArchiveProtectionSettings.FilePath;
            var preferencesPath = Path.GetFullPath(AppSettings.Path);
            if (string.Equals(profilePath, preferencesPath, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("The settings files must have distinct locations.");
            var profile = ArchiveProtectionSettings.Load(profilePath);
            var salt = ArchiveSaltBox.Text;
            profile.Salt = _originalArchiveSalt is null && string.IsNullOrEmpty(salt) ? null : salt;

            candidate = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;
            candidate.Theme = ThemeBox.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
            candidate.Language = (LanguageBox.SelectedItem as Strings.LanguageOption)?.Code ?? "de";
            candidate.ConfirmDelete = ConfirmDeleteBox.IsChecked == true;
            candidate.BusInterface = string.IsNullOrWhiteSpace(BusInterfaceBox.Text) ? null : BusInterfaceBox.Text;
            if (!string.IsNullOrWhiteSpace(TunnelAddressBox.Text))
                candidate.TunnelAddress = TunnelAddressBox.Text!;

            SaveCandidates(preferencesPath, candidate, profilePath, profile);
        }
        catch (SettingsRecoveryException)
        {
            ShowSettingsError("OptionsSaveRecoveryError");
            return;
        }
        catch (Exception error) when (IsSettingsError(error))
        {
            ShowSettingsError("OptionsSaveError");
            return;
        }

        _settings.Theme = candidate.Theme;
        _settings.Language = candidate.Language;
        _settings.ConfirmDelete = candidate.ConfirmDelete;
        _settings.BusInterface = candidate.BusInterface;
        _settings.TunnelAddress = candidate.TunnelAddress;
        ApplyTheme(_settings.Theme);
        Strings.Language = _settings.Language;
        Close(true);
    }

    private static void SaveCandidates(string preferencesPath, AppSettings preferences,
        string profilePath, ArchiveProtectionSettings profile)
    {
        using var preferencesWrite = SettingsFileWrite.Prepare(preferencesPath, preferences.Save);
        using var profileWrite = SettingsFileWrite.Prepare(profilePath, profile.Save);
        try
        {
            preferencesWrite.Commit();
            profileWrite.Commit();
        }
        catch (Exception error) when (IsSettingsError(error))
        {
            var profileRestored = profileWrite.Rollback();
            var preferencesRestored = preferencesWrite.Rollback();
            if (!profileRestored || !preferencesRestored) throw new SettingsRecoveryException();
            throw;
        }
    }

    private sealed class SettingsRecoveryException : IOException { }

    // Each rename is local to its target directory. This is recovery for reported
    // I/O failures, not a filesystem transaction across process or power loss.
    private sealed class SettingsFileWrite : IDisposable
    {
        private readonly string _target;
        private readonly string _staged;
        private readonly string _backup;
        private readonly bool _existed;
        private bool _committed;
        private bool _retainBackup;

        private SettingsFileWrite(string target, string directory, bool existed)
        {
            _target = target;
            var token = ".buschord-settings-" + Guid.NewGuid().ToString("N");
            _staged = Path.Combine(directory, token + ".tmp");
            _backup = Path.Combine(directory, token + ".backup");
            _existed = existed;
        }

        internal static SettingsFileWrite Prepare(string target, Action<string> write)
        {
            var directory = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(directory);
            var existed = false;
            try
            {
                var attributes = File.GetAttributes(target);
                if ((attributes & FileAttributes.Directory) != 0) throw new IOException("The settings target is not a file.");
                existed = true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var result = new SettingsFileWrite(target, directory, existed);
            try
            {
                if (existed) File.Copy(target, result._backup, overwrite: false);
                write(result._staged);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        internal void Commit()
        {
            File.Move(_staged, _target, overwrite: true);
            _committed = true;
        }

        internal bool Rollback()
        {
            if (!_committed) return true;
            try
            {
                if (_existed) File.Move(_backup, _target, overwrite: true);
                else File.Delete(_target);
                _committed = false;
                return true;
            }
            catch (Exception error) when (IsSettingsError(error))
            {
                _retainBackup = _existed;
                return false;
            }
        }

        public void Dispose()
        {
            RemoveTemporary(_staged);
            if (!_retainBackup) RemoveTemporary(_backup);
        }

        private static void RemoveTemporary(string path)
        {
            try { File.Delete(path); }
            catch (Exception error) when (IsSettingsError(error)) { }
        }
    }

    private static bool IsSettingsError(Exception error) => error is InvalidDataException or IOException
        or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private void ShowSettingsError(string key)
    {
        SettingsErrorText.Text = Strings.Get(key);
        SettingsErrorText.IsVisible = true;
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

/// <summary>A masked editor whose accessibility peer does not publish the stored value.</summary>
public sealed class ArchiveSaltTextBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);
    protected override AutomationPeer OnCreateAutomationPeer() => new SaltAutomationPeer(this);

    private sealed class SaltAutomationPeer(ArchiveSaltTextBox owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
        protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() => Array.Empty<AutomationPeer>();
    }
}
