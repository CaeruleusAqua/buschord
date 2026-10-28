using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Knx.App.Localization;
using Path = Avalonia.Controls.Shapes.Path;

namespace Knx.App;

/// <summary>
/// The common caption for app-owned modal windows. Keeps the native frame, owner and
/// Close pipeline; the existing content, default buttons and Escape handling stay intact.
/// </summary>
public static class WorkbenchDialogChrome
{
    public const double CaptionHeight = 40;
    private static readonly ConditionalWeakTable<Window, Caption> Captions = new();

    internal static bool NativeTitleBarRequested => Environment.GetCommandLineArgs()
            .Any(argument => argument.Equals("--native-titlebar", StringComparison.OrdinalIgnoreCase))
        || Environment.GetEnvironmentVariable("BUSCHORD_NATIVE_TITLEBAR") is "1" or "true";

    /// <summary>
    /// Call after InitializeComponent, before ShowDialog. Idempotent, including when the
    /// caller subsequently replaces Content or Title. Other platforms and the titlebar
    /// rollback flag keep their system decorations until their native chrome is accepted.
    /// </summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.Classes.Contains("wb-dialog")) window.Classes.Add("wb-dialog");
        if (!OperatingSystem.IsWindows() || NativeTitleBarRequested) return;
        _ = Captions.GetValue(window, static owner => new Caption(owner));
    }

    private sealed class Caption
    {
        private readonly Window _window;
        private readonly Grid _root;
        private readonly Border _header;
        private readonly ContentPresenter _body;
        private bool _wrapping;

        internal Caption(Window window)
        {
            _window = window;
            window.Styles.Add(new StyleInclude(new Uri("avares://BusChord/"))
            {
                Source = new Uri("avares://BusChord/Workbench/WorkbenchDialogChrome.axaml"),
            });

            var title = new TextBlock
            {
                Name = "DialogChromeTitle",
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(16, 0, 12, 0),
                IsHitTestVisible = false,
                Classes = { "wb-dialog-caption-title" },
            };
            title.Bind(TextBlock.TextProperty, window.GetObservable(Window.TitleProperty));

            var close = new Button
            {
                Name = "DialogChromeClose",
                Width = 46,
                Height = CaptionHeight,
                Classes = { "wb-dialog-caption-close" },
            };
            AutomationProperties.SetAutomationId(close, "DialogChromeClose");
            close.Bind(AutomationProperties.NameProperty,
                new Binding("[WindowClose]") { Source = WorkbenchStrings.Instance });
            close.Bind(ToolTip.TipProperty,
                new Binding("[WindowClose]") { Source = WorkbenchStrings.Instance });
            var glyph = new Path
            {
                Width = 10,
                Height = 10,
                Data = Geometry.Parse("M0 0 L10 10 M10 0 L0 10"),
                StrokeThickness = 1.2,
                Stretch = Stretch.None,
                IsHitTestVisible = false,
            };
            glyph.Bind(Shape.StrokeProperty, close.GetObservable(TemplatedControl.ForegroundProperty));
            close.Content = glyph;
            // A client button keeps normal hover, keyboard focus and its UIA Invoke pattern.
            // Calling Close is also what the native caption does: cancellation/Closing hooks
            // and typed dialog results continue to belong to the existing window.
            close.Click += (_, _) => window.Close();
            WindowDecorationProperties.SetElementRole(close, WindowDecorationsElementRole.User);

            var strip = new Grid { ColumnDefinitions = new("*,Auto") };
            strip.Children.Add(title);
            Grid.SetColumn(close, 1);
            strip.Children.Add(close);
            _header = new Border
            {
                Name = "DialogChromeHeader",
                Height = CaptionHeight,
                Classes = { "wb-dialog-caption" },
                Child = strip,
            };
            WindowDecorationProperties.SetElementRole(_header, WindowDecorationsElementRole.TitleBar);
            _body = new ContentPresenter
            {
                Name = "DialogChromeContent",
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            WindowDecorationProperties.SetElementRole(_body, WindowDecorationsElementRole.User);
            _root = new Grid { Name = "DialogChromeRoot", RowDefinitions = new("Auto,*") };
            _root.Children.Add(_header);
            Grid.SetRow(_body, 1);
            _root.Children.Add(_body);

            window.WindowDecorations = WindowDecorations.Full;
            window.ExtendClientAreaToDecorationsHint = true;
            window.ExtendClientAreaTitleBarHeightHint = CaptionHeight;
            window.Bind(Window.WindowDecorationsThemeProperty,
                window.GetResourceObservable("WbDialogWindowDecorationsTheme"));
            window.Classes.Add("wb-dialog-chrome");
            Wrap(window.Content);
            window.PropertyChanged += OnPropertyChanged;
            window.Opened += OnOpened;
            window.Closed += OnClosed;
        }

        private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
        {
            if (change.Property == ContentControl.ContentProperty && !_wrapping
                && !ReferenceEquals(_window.Content, _root)) Wrap(_window.Content);
        }

        private void Wrap(object? content)
        {
            _wrapping = true;
            try
            {
                // Detach before reparenting the existing content. Typed XAML field references,
                // bindings, focus handlers and controls themselves are preserved verbatim.
                _window.Content = null;
                _body.Content = content;
                _window.Content = _root;
            }
            finally { _wrapping = false; }
        }

        private void OnOpened(object? sender, EventArgs args)
        {
            if (_window.IsExtendedIntoWindowDecorations) return;
            // A backend that does not support extending the client area keeps its real
            // caption. Do not display a second app caption or suppress its system controls.
            _header.IsVisible = false;
            _window.ClearValue(Window.WindowDecorationsThemeProperty);
            _window.ExtendClientAreaToDecorationsHint = false;
            _window.ExtendClientAreaTitleBarHeightHint = -1;
            _window.Classes.Remove("wb-dialog-chrome");
        }

        private void OnClosed(object? sender, EventArgs args)
        {
            _window.PropertyChanged -= OnPropertyChanged;
            _window.Opened -= OnOpened;
            _window.Closed -= OnClosed;
        }
    }
}
