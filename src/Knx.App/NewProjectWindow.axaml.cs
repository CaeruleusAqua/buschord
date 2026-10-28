using Avalonia.Controls;
using Avalonia.Interactivity;
using Knx;
using Knx.App.Localization;

namespace Knx.App;

/// <summary>Wizard for a new project: name, group address style, topology skeleton.</summary>
public partial class NewProjectWindow : Window
{
    public NewProjectWindow()
    {
        InitializeComponent();
        WorkbenchDialogChrome.Apply(this);
        TemplateBox.SelectionChanged += (_, _) => RefreshTopology();
        AreasBox.ValueChanged += (_, _) => RefreshTopology();
        LinesBox.ValueChanged += (_, _) => RefreshTopology();
        Strings.Instance.PropertyChanged += LanguageChanged;
        Closed += (_, _) => Strings.Instance.PropertyChanged -= LanguageChanged;
        Opened += (_, _) => { Fit(); NameBox.Focus(); };
        ScalingChanged += (_, _) => Fit();
        RefreshTopology();
    }

    public NewProjectOptions Options { get; private set; } = new();

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        Options = new NewProjectOptions
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? Strings.Get("NewProjectTitle") : NameBox.Text!,
            Comment = string.IsNullOrWhiteSpace(CommentBox.Text) ? null : CommentBox.Text,
            GroupAddressStyle = StyleBox.SelectedIndex switch
            {
                1 => GroupAddressStyle.TwoLevel,
                2 => GroupAddressStyle.Free,
                _ => GroupAddressStyle.ThreeLevel,
            },
            Areas = (int)(AreasBox.Value ?? 1),
            LinesPerArea = (int)(LinesBox.Value ?? 1),
            TopologyTemplate = TemplateBox.SelectedIndex == 1 ? NewProjectTopologyTemplate.LegacyZero : NewProjectTopologyTemplate.TpFromOne,
        };
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item[]") RefreshTopology();
    }

    private void RefreshTopology()
    {
        var start = TemplateBox.SelectedIndex == 1 ? 0 : 1;
        AreasBox.Maximum = LinesBox.Maximum = 16 - start;
        TopologyPreview.Text = WorkbenchStrings.Get("NewTopologyPreview", start, start,
            (int)(AreasBox.Value ?? 1), (int)(LinesBox.Value ?? 1), $"{start}.{start}.1");
    }

    private void Fit()
    {
        var screen = Screens.ScreenFromWindow(this); if (screen is null) return;
        var width = Math.Max(1, screen.WorkingArea.Width / RenderScaling - 32);
        var height = Math.Max(1, screen.WorkingArea.Height / RenderScaling - 32);
        MinWidth = Math.Min(MinWidth, width); MinHeight = Math.Min(MinHeight, height);
        Width = Math.Clamp(Width, MinWidth, width); Height = Math.Clamp(Height, MinHeight, height);
    }
}
