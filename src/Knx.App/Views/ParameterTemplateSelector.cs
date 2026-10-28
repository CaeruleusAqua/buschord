using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx.App.Services;
using Knx.App.Localization;

namespace Knx.App.Views;

/// <summary>Wählt das Editor-Steuerelement anhand des Parameter-Kinds.</summary>
public sealed class ParameterTemplateSelector : IDataTemplate
{
    /// <summary>Instanz für XAML-Referenzen (ContentTemplate).</summary>
    public static ParameterTemplateSelector Instance { get; } = new();

    private static readonly Dictionary<ParameterKind, IDataTemplate> Templates = new()
    {
        [ParameterKind.Boolean] = Func(x => BuildCheckBox((ParameterItemViewModel)x)),
        [ParameterKind.Number] = Func(x => BuildNumeric((ParameterItemViewModel)x, false)),
        [ParameterKind.Float] = Func(x => BuildNumeric((ParameterItemViewModel)x, true)),
        [ParameterKind.Enumeration] = Func(x => BuildCombo((ParameterItemViewModel)x)),
        [ParameterKind.Color] = Func(x => BuildColor((ParameterItemViewModel)x)),
        [ParameterKind.Date] = Func(x => BuildText((ParameterItemViewModel)x, "TT.MM.JJJJ")),
        [ParameterKind.Time] = Func(x => BuildText((ParameterItemViewModel)x, null)),
        [ParameterKind.Duration] = Func(x => BuildNumeric((ParameterItemViewModel)x, false)),
        [ParameterKind.IpAddress] = Func(x => BuildText((ParameterItemViewModel)x,
            ((ParameterItemViewModel)x).Type?.AddressVersion == "IPv6" ? "::" : "0.0.0.0")),
        [ParameterKind.RawData] = Func(x => BuildText((ParameterItemViewModel)x, "Hex")),
        [ParameterKind.Picture] = Func(x => BuildPicture((ParameterItemViewModel)x)),
        [ParameterKind.Text] = Func(x => BuildText((ParameterItemViewModel)x, null)),
        [ParameterKind.ReadOnly] = Func(x => BuildReadOnly((ParameterItemViewModel)x)),
    };

    public bool Match(object? data) => data is ParameterItemViewModel;

    public Control? Build(object? param)
        => param is ParameterItemViewModel vm && Templates.TryGetValue(vm.Kind, out var t) ? t.Build(vm) : null;

    private static IDataTemplate Func(Func<object, Control> factory) => new FuncDataTemplate<object>((o, _) => factory(o!));

    private static Control BuildCheckBox(ParameterItemViewModel vm)
    {
        var box = new CheckBox { IsChecked = vm.IsChecked, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        box.IsCheckedChanged += (_, _) => vm.IsChecked = box.IsChecked == true;
        return box;
    }

    private static Control BuildNumeric(ParameterItemViewModel vm, bool allowDecimal)
    {
        var box = new NumericUpDown
        {
            Value = decimal.TryParse(vm.Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null,
            Increment = allowDecimal ? 0.1m : 1m,
            FormatString = allowDecimal ? "0.###" : "0",
            IsEnabled = vm.IsEnabled,
        };
        box.Classes.Add("wb-field");
        box.ValueChanged += (_, _) => vm.Value = box.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return WrapWithUnit(box, vm.Unit);
    }

    private static Control BuildCombo(ParameterItemViewModel vm)
    {
        var combo = new ComboBox
        {
            ItemsSource = vm.Choices,
            SelectedItem = vm.Choices.FirstOrDefault(c => c.Value == vm.Value),
            IsEnabled = vm.IsEnabled,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        combo.Classes.Add("wb-field");
        combo.SelectionChanged += (_, _) => vm.Value = (combo.SelectedItem as ParameterChoice)?.Value;
        return combo;
    }

    private static Control BuildText(ParameterItemViewModel vm, string? watermark)
    {
        var box = new TextBox
        {
            Text = vm.Value,
            Watermark = watermark,
            IsEnabled = vm.IsEnabled,
        };
        box.Classes.Add("wb-field");
        box.TextChanged += (_, _) => vm.Value = box.Text;
        return WrapWithUnit(box, vm.Unit);
    }

    private static Control BuildColor(ParameterItemViewModel vm)
    {
        // Color values are edited as text.
        var box = new TextBox { Text = vm.Value, Watermark = "#RRGGBB", IsEnabled = vm.IsEnabled };
        box.Classes.Add("wb-field");
        box.TextChanged += (_, _) => vm.Value = box.Text;
        return box;
    }

    private static Control BuildPicture(ParameterItemViewModel vm)
    {
        var panel = new StackPanel { Spacing = 4 };
        var image = new Image { MaxHeight = 260, MaxWidth = 540, Stretch = Avalonia.Media.Stretch.Uniform,
            HorizontalAlignment = vm.Type?.PictureAlignment switch
            { "Right" => Avalonia.Layout.HorizontalAlignment.Right, "Center" => Avalonia.Layout.HorizontalAlignment.Center, _ => Avalonia.Layout.HorizontalAlignment.Left } };
        Avalonia.Automation.AutomationProperties.SetName(image, vm.Name);
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "wb-muted" } };
        panel.Children.Add(image); panel.Children.Add(status);
        ParameterImageCache.ImageLease? lease = null;
        ParameterResourceReader? leasedReader = null;
        string? leasedResourceId = null;
        var attached = false;
        var refreshQueued = false;
        var reloadRequested = false;
        long attachmentGeneration = 0;
        Visual[] visibilityOwners = [];
        void RefreshImage(bool reload = false)
        {
            if (reload && (!ReferenceEquals(leasedReader, vm.Resources) || leasedResourceId != vm.Type?.PictureResourceId))
            { image.Source = null; lease?.Dispose(); lease = null; }
            image.HorizontalAlignment = vm.Type?.PictureAlignment switch
            { "Right" => Avalonia.Layout.HorizontalAlignment.Right, "Center" => Avalonia.Layout.HorizontalAlignment.Center, _ => Avalonia.Layout.HorizontalAlignment.Left };
            Avalonia.Automation.AutomationProperties.SetName(image, vm.Name);
            if (!attached || !panel.IsEffectivelyVisible)
            { image.Source = null; lease?.Dispose(); lease = null; return; }
            if (lease is null)
            {
                lease = ParameterImageCache.Acquire(vm);
                leasedReader = vm.Resources; leasedResourceId = vm.Type?.PictureResourceId;
            }
            if (lease.Bitmap is { } bitmap)
            {
                image.Source = bitmap; status.Text = ""; return;
            }
            status.Text = lease.BudgetExceeded ? WorkbenchStrings.Get("ParameterImageBudget") : WorkbenchStrings.Get(lease.Status switch
            {
                ParameterResourceStatus.TooLarge => "ParameterResourceTooLarge",
                ParameterResourceStatus.Unsupported => "ParameterResourceUnsupported",
                ParameterResourceStatus.Invalid => "ParameterResourceInvalid",
                _ => "ParameterResourceMissing",
            });
        }
        void QueueRefresh(bool reload)
        {
            if (!attached) return;
            reloadRequested |= reload;
            if (refreshQueued) return;
            refreshQueued = true;
            var generation = attachmentGeneration;
            Dispatcher.UIThread.Post(() =>
            {
                if (!attached || generation != attachmentGeneration) return;
                refreshQueued = false;
                var force = reloadRequested; reloadRequested = false;
                RefreshImage(force);
            });
        }
        void RowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ParameterItemViewModel.Type) or nameof(ParameterItemViewModel.Resources) or nameof(ParameterItemViewModel.Name))
                QueueRefresh(true);
        }
        void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        { if (e.PropertyName == "Item[]") QueueRefresh(false); }
        void VisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.IsVisibleProperty)
                QueueRefresh(false);
        }
        panel.AttachedToVisualTree += (_, _) =>
        {
            attached = true; attachmentGeneration++;
            visibilityOwners = panel.GetVisualAncestors().Prepend(panel).ToArray();
            foreach (var owner in visibilityOwners) owner.PropertyChanged += VisibilityChanged;
            vm.PropertyChanged += RowChanged;
            Strings.Instance.PropertyChanged += LanguageChanged;
            // Earlier ancestor-visibility notifications release their leases before this view acquires one.
            QueueRefresh(true);
        };
        panel.DetachedFromVisualTree += (_, _) =>
        {
            attached = false; attachmentGeneration++; refreshQueued = reloadRequested = false;
            foreach (var owner in visibilityOwners) owner.PropertyChanged -= VisibilityChanged;
            vm.PropertyChanged -= RowChanged;
            Strings.Instance.PropertyChanged -= LanguageChanged;
            visibilityOwners = [];
            RefreshImage();
        };
        return panel;
    }

    private static Control BuildReadOnly(ParameterItemViewModel vm)
        => new TextBlock { Text = vm.Value ?? "", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

    private static Control WrapWithUnit(Control control, string? unit)
    {
        if (string.IsNullOrEmpty(unit)) return control;
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(control);
        var label = new TextBlock { Text = unit, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        label.Classes.Add("wb-muted");
        panel.Children.Add(label);
        return panel;
    }
}
