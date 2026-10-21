using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Knx.App.Services;

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
        [ParameterKind.Time] = Func(x => BuildText((ParameterItemViewModel)x, "HH:MM:SS")),
        [ParameterKind.IpAddress] = Func(x => BuildText((ParameterItemViewModel)x, "0.0.0.0")),
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
        // ponytail: placeholder, replace with Avalonia.ColorPicker when the package is added
        var box = new TextBox { Text = vm.Value, Watermark = "#RRGGBB", IsEnabled = vm.IsEnabled };
        box.Classes.Add("wb-field");
        box.TextChanged += (_, _) => vm.Value = box.Text;
        return box;
    }

    private static Control BuildPicture(ParameterItemViewModel vm)
    {
        var text = new TextBlock { Text = $"[Bild: {vm.Value}]" };
        text.Classes.Add("wb-muted");
        return text;
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
