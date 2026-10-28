using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Knx.App.Workbench;

/// <summary>Applies the platform contrast preference to the whole client area, including future floating windows.</summary>
internal sealed class WorkbenchHighContrast : IDisposable
{
    private static WorkbenchHighContrast? _current;
    private static bool _windowHooked;
    private readonly Application _application;
    private readonly IPlatformSettings? _platform;
    private readonly Dictionary<string, (bool Present, object? Value)> _previous = [];
    private readonly List<WeakReference<Window>> _windows = [];
    private bool _enabled;
    private bool _disposed;

    private WorkbenchHighContrast(Application application)
    {
        _application = application;
        _platform = application.PlatformSettings;
        if (_platform is not null) _platform.ColorValuesChanged += OnColorsChanged;
        application.ActualThemeVariantChanged += OnThemeChanged;
        Refresh();
    }

    internal static void Attach(Application application)
    {
        _current?.Dispose();
        _current = new(application);
        if (_windowHooked) return;
        _windowHooked = true;
        Window.IsVisibleProperty.Changed.AddClassHandler<Window>((window, args) =>
        {
            if (args.NewValue is not true || _current is not { } current) return;
            if (!current._windows.Any(known => known.TryGetTarget(out var existing) && ReferenceEquals(existing, window)))
                current._windows.Add(new(window));
            window.Classes.Set("wb-high-contrast", current._enabled);
        });
    }

    private void OnColorsChanged(object? sender, PlatformColorValues values) => Dispatcher.UIThread.Post(Refresh);
    private void OnThemeChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        if (_disposed) return;
        var enabled = _platform?.GetColorValues().ContrastPreference == ColorContrastPreference.High;
        Apply(enabled, ReadSystemColor(5, Colors.Black), ReadSystemColor(8, Colors.White),
            ReadSystemColor(13, Colors.White), ReadSystemColor(14, Colors.Black));
    }

    // Explicit colors also make the reversible palette transition testable without changing OS preferences.
    internal void Apply(bool enabled, Color background, Color foreground, Color selected, Color selectedText)
    {
        _enabled = enabled;
        if (enabled)
        {
            var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
            foreach (var key in new[] { "WbBackground", "WbPaper", "WbPanel", "WbRail", "WbTabbar", "WbStatusbar", "WbTableHeader", "WbDisabled", "WbHover",
                "EtsChrome", "EtsPanel", "EtsButtonBackground", "EtsButtonHover", "EtsStatusBackground", "EtsDisabledBackground",
                "TextControlBackgroundDisabled", "ComboBoxBackgroundDisabled", "ToolTipBackground" }) colors[key] = background;
            foreach (var key in new[] { "WbText", "WbMuted", "WbSubtle", "WbLine", "WbAccent", "WbAccentHover", "WbDisabledText", "WbError", "WbWarning", "WbSuccess", "WbInfo", "WbStatusProjectText",
                "EtsText", "EtsMuted", "EtsBorder", "EtsAccent", "EtsAccentHover", "EtsError", "EtsWarning", "EtsOk", "EtsDisabledText",
                "TextControlForegroundDisabled", "TextControlBorderBrushDisabled", "ComboBoxForegroundDisabled", "ComboBoxBorderBrushDisabled", "ToolTipForeground", "ToolTipBorderBrush" }) colors[key] = foreground;
            foreach (var key in new[] { "WbTint", "WbSelected", "EtsAccentSoft" }) colors[key] = selected;
            colors["WbSelectedText"] = selectedText;
            colors["WbOnAccent"] = colors["EtsOnAccent"] = background;
            foreach (var (key, color) in colors)
            {
                if (!_previous.ContainsKey(key))
                    // TryGetValue captures direct entries only; resolved theme colors must never become permanent overrides.
                    _previous[key] = _application.Resources.TryGetValue(key, out var value) ? (true, value) : (false, null);
                _application.Resources[key] = new SolidColorBrush(color);
            }
        }
        else
        {
            foreach (var (key, previous) in _previous)
                if (previous.Present) _application.Resources[key] = previous.Value!;
                else _application.Resources.Remove(key);
            _previous.Clear();
        }
        for (var index = _windows.Count - 1; index >= 0; index--)
            if (_windows[index].TryGetTarget(out var window)) window.Classes.Set("wb-high-contrast", enabled);
            else _windows.RemoveAt(index);
    }

    // Zero is a valid RGB value (black), not an indication that GetSysColor failed.
    private static Color ReadSystemColor(int index, Color fallback)
    {
        if (!OperatingSystem.IsWindows()) return fallback;
        var value = GetSysColor(index);
        return Color.FromRgb((byte)value, (byte)(value >> 8), (byte)(value >> 16));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_platform is not null) _platform.ColorValuesChanged -= OnColorsChanged;
        _application.ActualThemeVariantChanged -= OnThemeChanged;
        Apply(false, default, default, default, default);
    }

    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
}
