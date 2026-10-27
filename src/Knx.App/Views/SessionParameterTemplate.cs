using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Knx.App.Services;

namespace Knx.App.Views;

/// <summary>Keep simultaneous fresh views in sync without replacing editors or their local undo stacks.</summary>
public sealed class SessionParameterTemplate : IDataTemplate
{
    public static SessionParameterTemplate Instance { get; } = new();
    public bool Match(object? data) => data is ParameterItemViewModel;

    public Control? Build(object? data)
    {
        var control = ParameterTemplateSelector.Instance.Build(data);
        if (data is not ParameterItemViewModel row || control is null) return control;
        var editor = control is StackPanel panel ? panel.Children[0] : control;
        var binding = new Binding(nameof(row.Value)) { Source = row, Mode = BindingMode.OneWay };
        switch (editor)
        {
            case NumericUpDown number:
                binding.Converter = new FuncValueConverter<string?, object>(value => value is null ? null!
                    : decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed : BindingOperations.DoNothing);
                number.Bind(NumericUpDown.ValueProperty, binding);
                break;
            case TextBox text:
                text.Bind(TextBox.TextProperty, binding);
                break;
            case CheckBox check:
                check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(row.IsChecked)) { Source = row, Mode = BindingMode.OneWay });
                break;
            case ComboBox combo:
                binding.Converter = new FuncValueConverter<string?, object>(value => row.Choices.FirstOrDefault(c => c.Value == value)
                    ?? (object)BindingOperations.DoNothing);
                combo.Bind(ComboBox.SelectedItemProperty, binding);
                break;
            case TextBlock text when row.Kind == ParameterKind.ReadOnly:
                text.Bind(TextBlock.TextProperty, binding);
                break;
        }
        return control;
    }
}
