using Avalonia.Controls;
using Avalonia.Interactivity;
using Knx;

namespace Knx.App;

/// <summary>Wizard for a new project: name, group address style, topology skeleton.</summary>
public partial class NewProjectWindow : Window
{
    public NewProjectWindow()
    {
        InitializeComponent();
    }

    public NewProjectOptions Options { get; private set; } = new();

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        Options = new NewProjectOptions
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Neues Projekt" : NameBox.Text!,
            Comment = string.IsNullOrWhiteSpace(CommentBox.Text) ? null : CommentBox.Text,
            GroupAddressStyle = StyleBox.SelectedIndex switch
            {
                1 => GroupAddressStyle.TwoLevel,
                2 => GroupAddressStyle.Free,
                _ => GroupAddressStyle.ThreeLevel,
            },
            Areas = (int)(AreasBox.Value ?? 1),
            LinesPerArea = (int)(LinesBox.Value ?? 1),
        };
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
