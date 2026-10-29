using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx.App.Localization;
using Knx.App.Workbench;

namespace Knx.App.Views;

/// <summary>Native, viewport-limited buttons over a read-only topology drawing.</summary>
public sealed class BusDiagramView : UserControl
{
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly ScrollViewer _scroll = new()
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };
    private readonly TextBlock _selection = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly TextBlock _zoomLabel = new() { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Width = 48 };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Classes = { "wb-muted" }, FontSize = 11 };
    private readonly TextBlock _empty = new()
    {
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 360,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        Classes = { "wb-muted" }, IsHitTestVisible = false
    };
    private readonly Button _open;
    private readonly Action<WorkbenchTarget> _navigate;
    private readonly Dictionary<string, Button> _visible = new(StringComparer.Ordinal);
    private BusDiagramLayout _layout = new([], 500, 200);
    private readonly DiagramLines _lines = new();
    private string? _selectedKey;
    private double _zoom = 1;
    private bool _initialFit = true;
    private Point? _panStart;
    private Vector _panOffset;
    private string? _pendingFocusKey;
    private int _focusVersion;
    private int _viewportVersion;
    public string Language { get; } = Knx.Strings.Language;
    public event EventHandler? SelectionChanged;
    public BusDiagramLayout Layout => _layout;
    public string? SelectedKey => _selectedKey;
    public double Zoom => _zoom;
    public int RealizedNodeCount => _visible.Count;
    public Vector ScrollOffset => _scroll.Offset;
    public Size Viewport => _scroll.Viewport;
    public Size Extent => _scroll.Extent;
    private static string W(string key) => WorkbenchStrings.Get(key);

    public BusDiagramView(Action<WorkbenchTarget> navigate)
    {
        _navigate = navigate;
        Name = "BusDiagram";
        AutomationProperties.SetName(this, W("BusDiagram"));
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 10 };
        var toolbar = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 10, RowSpacing = 6 };
        var zoomControls = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        _open = Button(W("DiagramOpen"), OpenSelected, "diagram.open");
        var controls = new Control[] { Button("−", () => SetZoom(_zoom / 1.25), "diagram.zoomOut", W("DiagramZoomOut")),
            _zoomLabel, Button("+", () => SetZoom(_zoom * 1.25), "diagram.zoomIn", W("DiagramZoomIn")),
            Button(W("DiagramFit"), Fit, "diagram.fit"), Button("100 %", () => SetZoom(1), "diagram.actualSize", W("DiagramActualSize")) };
        foreach (var control in controls) { control.Margin = new(0, 0, 6, 0); zoomControls.Children.Add(control); }
        toolbar.Children.Add(zoomControls); toolbar.Children.Add(_selection); toolbar.Children.Add(_open);
        void ArrangeToolbar()
        {
            var narrow = toolbar.Bounds.Width < 600;
            toolbar.ColumnDefinitions = new(narrow ? "*,Auto" : "Auto,*,Auto");
            toolbar.RowDefinitions = new(narrow ? "Auto,Auto" : "Auto");
            Grid.SetColumnSpan(zoomControls, narrow ? 2 : 1);
            Grid.SetColumn(_selection, narrow ? 0 : 1); Grid.SetRow(_selection, narrow ? 1 : 0);
            Grid.SetColumn(_open, narrow ? 1 : 2); Grid.SetRow(_open, narrow ? 1 : 0);
        }
        toolbar.SizeChanged += (_, _) => ArrangeToolbar(); ArrangeToolbar();
        root.Children.Add(toolbar);
        _scroll.Content = _canvas;
        _scroll.Background = Brushes.Transparent;
        _canvas.Children.Add(_lines);
        Grid.SetRow(_scroll, 1); root.Children.Add(_scroll);
        Grid.SetRow(_empty, 1); root.Children.Add(_empty);
        Grid.SetRow(_hint, 2); root.Children.Add(_hint);
        Content = root;
        _scroll.ScrollChanged += (_, _) => Realize();
        _scroll.SizeChanged += (_, _) =>
        {
            if (_initialFit && _scroll.Viewport.Width > 0)
            { _initialFit = false; Fit(); if (_selectedKey is { } key) FocusNode(key); }
            else Realize();
        };
        _scroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;
            SetZoom(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), e.GetPosition(_scroll)); e.Handled = true;
        }, RoutingStrategies.Tunnel);
        _scroll.AddHandler(PointerPressedEvent, (_, e) =>
        {
            var point = e.GetCurrentPoint(_scroll);
            var interactiveChild = e.Source is Visual visual && (visual is Avalonia.Controls.Button or ScrollBar
                || visual.GetVisualAncestors().Any(a => a is Avalonia.Controls.Button or ScrollBar));
            if (point.Properties.IsMiddleButtonPressed || point.Properties.IsLeftButtonPressed && !interactiveChild)
            { _viewportVersion++; _panStart = e.GetPosition(_scroll); _panOffset = _scroll.Offset; e.Pointer.Capture(_scroll); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        _scroll.PointerMoved += (_, e) => { if (_panStart is { } start) _scroll.Offset = _panOffset - (e.GetPosition(_scroll) - start); };
        _scroll.PointerReleased += (_, e) => { _panStart = null; if (e.Pointer.Captured == _scroll) e.Pointer.Capture(null); };
        _scroll.PointerCaptureLost += (_, _) => _panStart = null;
    }

    public void Refresh(Project project, string? initialKey = null)
    {
        var focused = _visible.FirstOrDefault(p => p.Value.IsKeyboardFocusWithin).Key ?? _pendingFocusKey;
        var focusVersion = ++_focusVersion;
        _viewportVersion++;
        _pendingFocusKey = focused;
        _layout = BusDiagramLayout.Create(project);
        if (_selectedKey is null && initialKey is not null) _selectedKey = initialKey;
        if (!_layout.Nodes.Any(n => n.Key == _selectedKey)) _selectedKey = null;
        _hint.Text = W("DiagramHint");
        _hint.IsVisible = _layout.Nodes.Count > 0;
        _empty.Text = W("DiagramEmpty");
        _empty.IsVisible = _layout.Nodes.Count == 0;
        _scroll.IsVisible = _layout.Nodes.Count > 0;
        AutomationProperties.SetName(this, W("BusDiagram"));
        foreach (var button in _visible.Values) _canvas.Children.Remove(button);
        _visible.Clear();
        UpdateGeometry(); UpdateSelection(); Realize();
        if (focused is not null) Dispatcher.UIThread.Post(() =>
        {
            if (focusVersion != _focusVersion) return;
            _pendingFocusKey = null;
            var top = TopLevel.GetTopLevel(this);
            var current = top?.FocusManager?.GetFocusedElement();
            if (IsEffectivelyVisible && (current is null || ReferenceEquals(current, top)
                || current is Button button && button.Tag as string == focused)) FocusNode(focused);
        }, DispatcherPriority.Loaded);
    }

    public void SelectNode(string key)
    {
        if (!_layout.Nodes.Any(n => n.Key == key)) return;
        var changed = _selectedKey != key;
        _selectedKey = key; UpdateSelection();
        if (changed) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    public void RestoreViewport(BusDiagramView previous)
    {
        _selectedKey = previous.SelectedKey;
        _zoom = previous.Zoom;
        _initialFit = false;
        UpdateGeometry(); UpdateSelection(); Realize();
        var offset = previous.ScrollOffset;
        Dispatcher.UIThread.Post(() => { _scroll.Offset = offset; Realize(); }, DispatcherPriority.Loaded);
    }
    public void OpenSelected()
    {
        if (_layout.Nodes.FirstOrDefault(n => n.Key == _selectedKey)?.Target is { } target) _navigate(target);
    }
    public void Fit()
    {
        if (_scroll.Viewport.Width <= 0 || _scroll.Viewport.Height <= 0) return;
        // Very tall projects stay readable and scrollable instead of shrinking to dots.
        SetZoom(Math.Clamp(Math.Min(_scroll.Viewport.Width / _layout.Width, Math.Max(.85, _scroll.Viewport.Height / _layout.Height)), .35, 1));
        _viewportVersion++;
        _scroll.Offset = default;
    }
    public void SetZoom(double zoom, Point? anchor = null)
    {
        if (!double.IsFinite(zoom)) return;
        zoom = Math.Clamp(zoom, .35, 2);
        var point = anchor ?? new Point(_scroll.Viewport.Width / 2, _scroll.Viewport.Height / 2);
        var offset = (_scroll.Offset + new Vector(point.X, point.Y)) * (zoom / _zoom) - new Vector(point.X, point.Y);
        var version = ++_viewportVersion;
        _zoom = zoom; UpdateGeometry();
        _scroll.Offset = offset; Realize();
        var immediateOffset = _scroll.Offset;
        // The new extent is available after layout; the old extent can temporarily clamp
        // the anchor offset when zooming in near the end of a large topology.
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _viewportVersion || _scroll.Offset != immediateOffset) return;
            _scroll.Offset = offset; Realize();
        }, DispatcherPriority.Loaded);
    }
    private void UpdateGeometry()
    {
        _canvas.Width = _layout.Width * _zoom; _canvas.Height = _layout.Height * _zoom;
        _lines.Width = _canvas.Width; _lines.Height = _canvas.Height;
        _lines.Layout = _layout; _lines.Zoom = _zoom; _lines.InvalidateVisual();
        _zoomLabel.Text = $"{_zoom:P0}";
        foreach (var (key, button) in _visible) Position(button, _layout.Nodes.First(n => n.Key == key));
    }
    private void Position(Button button, BusDiagramNode node)
    {
        button.Width = BusDiagramNode.Width; button.Height = BusDiagramNode.Height;
        button.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        button.RenderTransform = new ScaleTransform(_zoom, _zoom);
        Canvas.SetLeft(button, node.X * _zoom); Canvas.SetTop(button, node.Y * _zoom);
    }
    private void Realize()
    {
        var viewport = new Rect(_scroll.Offset.X - 100, _scroll.Offset.Y - 100, Math.Max(1, _scroll.Viewport.Width) + 200, Math.Max(1, _scroll.Viewport.Height) + 200);
        var nodes = _layout.Nodes.Where(n => viewport.Intersects(new Rect(n.X * _zoom, n.Y * _zoom, BusDiagramNode.Width * _zoom, BusDiagramNode.Height * _zoom))).ToList();
        var keys = nodes.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _visible.Keys.Where(k => !keys.Contains(k) && !_visible[k].IsKeyboardFocusWithin).ToArray())
        { _canvas.Children.Remove(_visible[key]); _visible.Remove(key); }
        foreach (var node in nodes)
        {
            if (_visible.ContainsKey(node.Key)) continue;
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = node.Title, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = node.Detail, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "wb-muted" }, FontSize = 10 });
            var button = Button("", () => SelectNode(node.Key), "diagram.node." + node.Key, node.Title + ", " + node.Detail);
            button.Content = text; button.Tag = node.Key; button.Padding = new(10, 3); button.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            button.Classes.Add("wb-diagram-node");
            button.Classes.Set("active", node.Key == _selectedKey);
            AutomationProperties.SetItemStatus(button, node.Key == _selectedKey ? W("Selected") : "");
            ToolTip.SetTip(button, node.Title + "\n" + node.Detail);
            button.GotFocus += (_, _) => SelectNode(node.Key);
            button.DoubleTapped += (_, e) => { SelectNode(node.Key); OpenSelected(); e.Handled = true; };
            button.AddHandler(KeyDownEvent, (_, e) => NodeKey(node, e), RoutingStrategies.Tunnel);
            Position(button, node); _visible.Add(node.Key, button); _canvas.Children.Add(button);
        }
    }
    private void UpdateSelection()
    {
        var node = _layout.Nodes.FirstOrDefault(n => n.Key == _selectedKey);
        _selection.Text = node is null ? W("DiagramChoose") : node.Title + " · " + node.Detail;
        _open.IsEnabled = node?.Target is not null;
        foreach (var (key, button) in _visible)
        { button.Classes.Set("active", key == _selectedKey); AutomationProperties.SetItemStatus(button, key == _selectedKey ? W("Selected") : ""); }
    }
    private void NodeKey(BusDiagramNode node, KeyEventArgs e)
    {
        BusDiagramNode? next = e.Key switch
        {
            Key.Left => _layout.Nodes.FirstOrDefault(n => n.Key == node.ParentKey),
            Key.Right => _layout.Nodes.FirstOrDefault(n => n.ParentKey == node.Key),
            Key.Up => _layout.Nodes.Where(n => n.X == node.X && n.Y < node.Y).MaxBy(n => n.Y),
            Key.Down => _layout.Nodes.Where(n => n.X == node.X && n.Y > node.Y).MinBy(n => n.Y),
            Key.Home => _layout.Nodes.FirstOrDefault(),
            Key.End => _layout.Nodes.LastOrDefault(),
            _ => null
        };
        if (e.Key == Key.Enter) { SelectNode(node.Key); OpenSelected(); e.Handled = true; }
        else if (next is not null) { FocusNode(next.Key); e.Handled = true; }
    }
    public void FocusNode(string key)
    {
        if (_layout.Nodes.FirstOrDefault(n => n.Key == key) is not { } node) return;
        var version = ++_focusVersion;
        _viewportVersion++;
        var previousFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        _pendingFocusKey = null;
        SelectNode(key);
        var x = node.X * _zoom; var y = node.Y * _zoom;
        _scroll.Offset = new(Math.Clamp(x - 24, 0, Math.Max(0, _canvas.Width - _scroll.Viewport.Width)),
            Math.Clamp(y - _scroll.Viewport.Height / 2, 0, Math.Max(0, _canvas.Height - _scroll.Viewport.Height)));
        Realize();
        // A newly realized button has no arranged bounds yet. Focusing it immediately asks
        // ScrollViewer to bring its temporary (0,0) bounds into view and undoes the jump.
        Dispatcher.UIThread.Post(() =>
        {
            var top = TopLevel.GetTopLevel(this);
            var current = top?.FocusManager?.GetFocusedElement();
            if (version != _focusVersion || !IsEffectivelyVisible || current is not null
                && !ReferenceEquals(current, top) && !ReferenceEquals(current, previousFocus)) return;
            if (_visible.TryGetValue(key, out var button)) button.Focus();
        }, DispatcherPriority.Loaded);
    }
    private static Button Button(string text, Action action, string id, string? name = null)
    {
        var button = new Button { Content = text, Classes = { "wb-button", "wb-quiet" } };
        AutomationProperties.SetAutomationId(button, id); AutomationProperties.SetName(button, name ?? text);
        button.Click += (_, _) => action(); return button;
    }

    private sealed class DiagramLines : Control
    {
        public BusDiagramLayout Layout { get; set; } = new([], 0, 0);
        public double Zoom { get; set; } = 1;
        public DiagramLines() { IsHitTestVisible = false; }
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var pen = new Pen(this.TryFindResource("WbMuted", ActualThemeVariant, out var resource) && resource is IBrush brush ? brush : Brushes.Gray, 1);
            var map = Layout.Nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
            foreach (var node in Layout.Nodes)
            {
                if (node.ParentKey is null || !map.TryGetValue(node.ParentKey, out var parent)) continue;
                var from = new Point((parent.X + BusDiagramNode.Width) * Zoom, (parent.Y + BusDiagramNode.Height / 2) * Zoom);
                var to = new Point(node.X * Zoom, (node.Y + BusDiagramNode.Height / 2) * Zoom);
                var middle = from.X + 20 * Zoom;
                context.DrawLine(pen, from, new(middle, from.Y));
                context.DrawLine(pen, new(middle, from.Y), new(middle, to.Y));
                context.DrawLine(pen, new(middle, to.Y), to);
            }
        }
    }
}
