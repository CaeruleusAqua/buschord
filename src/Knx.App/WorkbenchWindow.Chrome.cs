using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WindowStateValue = Avalonia.Controls.WindowState;

namespace Knx.App;

/// <summary>
/// Variant-A window chrome: the native caption is not drawn, the 49 px top strip is the caption
/// hit region and the three 46 x 49 blocks keep HTMINBUTTON/HTMAXBUTTON/HTCLOSE semantics.
/// The native frame (resize, snap, DWM) stays intact. App-owned modal windows use
/// their independent, shared WorkbenchDialogChrome caption.
/// </summary>
public partial class WorkbenchWindow
{
    /// <summary>One source for the XAML row height and ExtendClientAreaTitleBarHeightHint.</summary>
    internal const double ChromeHeight = 49;
    private const uint WmSysCommand = 0x0112;
    // Low bit 2 marks a menu/keyboard invocation; DefWindowProc masks it off but uses it to
    // enter the native move/size loop instead of ignoring the command.
    private const uint ScSize = 0xF002;
    private const uint ScMove = 0xF012;
    private static readonly Geometry MaximizeGlyph = Geometry.Parse("M0 0 H10 V10 H0 Z");
    private static readonly Geometry RestoreGlyph = Geometry.Parse("M0 2 H8 V10 H0 Z M2 0 H10 V8");
    private ContextMenu? _windowMenu;
    private MenuItem? _windowRestoreItem;
    private MenuItem? _windowMoveItem;
    private MenuItem? _windowSizeItem;
    private MenuItem? _windowMinimizeItem;
    private MenuItem? _windowMaximizeItem;
    private bool _chromeReady;
    private bool _compactSearch;
    private DispatcherTimer? _captionHoverTimer;
    private Button? _hoveredCaption;
    private DateTime _captionHoverSince;
    private bool _captionTipOpen;

    /// <summary>Rollback state selected with --native-titlebar or BUSCHORD_NATIVE_TITLEBAR=1.</summary>
    internal bool NativeTitleBar { get; private set; }

    internal static string MaximizeGlyphData => MaximizeGlyph.ToString() ?? "";

    private void InitializeWindowChrome()
    {
        // Single-flag rollback to the documented variant E: keep the native caption above the
        // existing strip and hide the custom blocks. No other code path changes.
        NativeTitleBar = WorkbenchDialogChrome.NativeTitleBarRequested;
        if (NativeTitleBar)
        {
            WindowDecorations = Avalonia.Controls.WindowDecorations.Full;
            ExtendClientAreaToDecorationsHint = false;
            ExtendClientAreaTitleBarHeightHint = -1;
            WindowDecorationsTheme = null;
            WindowButtons.IsVisible = false;
            WindowDecorationProperties.SetElementRole(Topbar, WindowDecorationsElementRole.None);
        }
        else if (WindowDecorationsTheme is null && this.TryFindResource("WbWindowDecorationsTheme", out var found)
                 && found is Avalonia.Styling.ControlTheme theme)
        {
            // DynamicResource normally resolves; this covers a late resource lookup without
            // silently falling back to Fluent's drawn caption (S0 finding F1).
            WindowDecorationsTheme = theme;
        }

        BuildWindowMenu();
        _chromeReady = true;
        UpdateWindowChrome();
        Opened += (_, _) =>
        {
            HookChromeContrast();
            StartCaptionHoverTracking();
            UpdateWindowChrome();
        };
        Closed += (_, _) => _captionHoverTimer?.Stop();
    }

    /// <summary>
    /// Caption blocks answer WM_NCHITTEST with HTMINBUTTON/HTMAXBUTTON/HTCLOSE, so Windows sends
    /// no client pointer-move events and Avalonia never sets :pointerover. A light poll supplies
    /// the same hover, pressed and tooltip feedback without giving up the native roles.
    /// </summary>
    private void StartCaptionHoverTracking()
    {
        if (NativeTitleBar || _captionHoverTimer is not null) return;
        _captionHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _captionHoverTimer.Tick += (_, _) => UpdateCaptionHover();
        _captionHoverTimer.Start();
    }

    private void UpdateCaptionHover()
    {
        Button? hovered = null;
        if (IsActive && GetCursorPos(out var cursor))
        {
            var point = Avalonia.VisualExtensions.PointToClient(this, new PixelPoint(cursor.X, cursor.Y));
            foreach (var button in new[] { ChromeMinimize, ChromeMaximize, ChromeClose })
            {
                if (button.TranslatePoint(new Point(0, 0), this) is not { } origin) continue;
                if (!new Rect(origin, button.Bounds.Size).Contains(point)) continue;
                hovered = button;
                break;
            }
        }

        if (!ReferenceEquals(hovered, _hoveredCaption))
        {
            if (_hoveredCaption is { } previous)
            {
                previous.Classes.Set("wb-caption-hover", false);
                previous.Classes.Set("wb-caption-press", false);
                if (_captionTipOpen) ToolTip.SetIsOpen(previous, false);
            }
            _hoveredCaption = hovered;
            _captionHoverSince = DateTime.UtcNow;
            _captionTipOpen = false;
        }
        if (hovered is null) return;

        var pressed = GetAsyncKeyState(0x01) < 0;
        hovered.Classes.Set("wb-caption-hover", true);
        hovered.Classes.Set("wb-caption-press", pressed);
        if (pressed || _captionTipOpen || (DateTime.UtcNow - _captionHoverSince).TotalMilliseconds < 550) return;
        ToolTip.SetIsOpen(hovered, true);
        _captionTipOpen = true;
    }

    /// <summary>Rebuilt on a live language change; the menu keeps single-item enable rules.</summary>
    private void BuildWindowMenu()
    {
        var menu = new ContextMenu();
        MenuItem Add(string key, Action action)
        {
            var item = new MenuItem { Header = W(key) };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        _windowRestoreItem = Add("WindowRestore", () => WindowState = WindowStateValue.Normal);
        _windowMoveItem = Add("WindowMove", () => RunSystemCommand(ScMove));
        _windowSizeItem = Add("WindowSize", () => RunSystemCommand(ScSize));
        menu.Items.Add(new Separator());
        _windowMinimizeItem = Add("WindowMinimize", () => WindowState = WindowStateValue.Minimized);
        _windowMaximizeItem = Add("WindowMaximize", () => WindowState = WindowStateValue.Maximized);
        menu.Items.Add(new Separator());
        Add("WindowClose", Close);

        _windowMenu = menu;
        Topbar.ContextMenu = menu;
        WindowMenuButton.ContextMenu = menu;
        UpdateWindowMenuState();
    }

    private void OnWindowMenu(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowWindowMenu();

    private void ShowWindowMenu()
    {
        if (_windowMenu is null) return;
        UpdateWindowMenuState();
        if (_windowMenu.IsOpen) _windowMenu.Close();
        else _windowMenu.Open(WindowMenuButton);
    }

    private void OnChromeMinimize(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => WindowState = WindowStateValue.Minimized;

    private void OnChromeMaximize(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState == WindowStateValue.Maximized ? WindowStateValue.Normal : WindowStateValue.Maximized;

    private void OnChromeClose(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && _chromeReady) UpdateWindowChrome();
    }

    private void UpdateWindowChrome()
    {
        var maximized = WindowState == WindowStateValue.Maximized;
        if (ChromeMaximizeGlyph is not null) ChromeMaximizeGlyph.Data = maximized ? RestoreGlyph : MaximizeGlyph;
        var name = W(maximized ? "WindowRestore" : "WindowMaximize");
        if (ChromeMaximize is not null)
        {
            AutomationProperties.SetName(ChromeMaximize, name);
            ToolTip.SetTip(ChromeMaximize, name);
        }
        UpdateWindowMenuState();
    }

    private void UpdateWindowMenuState()
    {
        var state = WindowState;
        if (_windowRestoreItem is not null) _windowRestoreItem.IsEnabled = state == WindowStateValue.Maximized;
        if (_windowMoveItem is not null) _windowMoveItem.IsEnabled = OperatingSystem.IsWindows() && state != WindowStateValue.Minimized;
        if (_windowSizeItem is not null) _windowSizeItem.IsEnabled = OperatingSystem.IsWindows() && state == WindowStateValue.Normal;
        if (_windowMinimizeItem is not null) _windowMinimizeItem.IsEnabled = state != WindowStateValue.Minimized;
        if (_windowMaximizeItem is not null) _windowMaximizeItem.IsEnabled = state != WindowStateValue.Maximized;
    }

    /// <summary>
    /// Starts the native move/size loop instead of reimplementing it. The system menu itself
    /// cannot open on an extended caption strip (S0 finding F5), but its commands still work.
    /// </summary>
    private void RunSystemCommand(uint command)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero) return;
        ReleaseCapture();
        _ = SendMessage(handle.Handle, WmSysCommand, new IntPtr((int)command), IntPtr.Zero);
    }

    /// <summary>Compact search below 1040 DIP so the 138 px caption cluster never squeezes it out.</summary>
    private void ApplyTopbarLayout(double width)
    {
        var compact = width is > 0 and < 1040;
        if (_compactSearch == compact) return;
        _compactSearch = compact;
        TopbarSearchFull.IsVisible = !compact;
        TopbarSearchCompact.IsVisible = compact;
        TopbarSearch.MinWidth = compact ? 36 : 220;
        if (compact)
        {
            TopbarSearch.Width = 36;
            TopbarSearch.Height = 32;
            TopbarSearch.Padding = new Thickness(0);
        }
        else
        {
            TopbarSearch.ClearValue(Layoutable.WidthProperty);
            TopbarSearch.ClearValue(Layoutable.HeightProperty);
            TopbarSearch.ClearValue(TemplatedControl.PaddingProperty);
        }
    }

    /// <summary>Without this the custom strip would ignore a Windows high-contrast theme (F6/S4).</summary>
    private void HookChromeContrast()
    {
        if (this.GetPlatformSettings() is not { } settings) return;
        settings.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(ApplyChromeContrast);
        ApplyChromeContrast();
    }

    private void ApplyChromeContrast()
    {
        var high = this.GetPlatformSettings()?.GetColorValues().ContrastPreference == ColorContrastPreference.High;
        if (high)
        {
            var window = SystemColor(5, Colors.Black);
            var text = SystemColor(8, Colors.White);
            var highlight = SystemColor(13, Colors.White);
            var highlightText = SystemColor(14, Colors.Black);
            Resources["HcChromeWindow"] = new SolidColorBrush(window);
            Resources["HcChromeText"] = new SolidColorBrush(text);
            Resources["HcChromeHighlight"] = new SolidColorBrush(highlight);
            Resources["HcChromeHighlightText"] = new SolidColorBrush(highlightText);
        }
        Classes.Set("high-contrast", high);
    }

    private static Color SystemColor(int index, Color fallback)
    {
        if (!OperatingSystem.IsWindows()) return fallback;
        var value = GetSysColor(index);
        return Color.FromRgb((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
