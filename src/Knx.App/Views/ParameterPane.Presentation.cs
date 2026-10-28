using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Knx.App.Localization;
using Knx.App.Services;

namespace Knx.App.Views;

public sealed partial class ParameterPane
{
    private string? _layoutIdentity;
    private ParameterItemViewModel? _helpRow;
    private void InitializePresentation()
    {
        AddHandler(KeyDownEvent, OnPresentationKey, RoutingStrategies.Tunnel);
        SizeChanged += (_, _) => RenderPresentation();
    }
    private void OnTableLayoutChanged(object? sender, RoutedEventArgs e)
    { if (TablePages is not null) RenderPresentation(); }

    private void RenderPresentation()
    {
        var rows = Session.Rows.Where(r => r.IsVisible).ToArray();
        var pages = Session.Presentation.Pages;
        var supported = pages.Values.Any(p => p.IsTable && rows.Any(r => r.Instance.Placement?.BlockId == p.Id));
        var width = Bounds.Width;
        var largestColumns = pages.Values.Where(p => p.IsTable).Select(p => p.ColumnWeights.Count).DefaultIfEmpty(1).Max();
        var fits = rows.Length <= 128 && width >= Math.Max(400, largestColumns * 220);
        TableLayoutToggle.IsVisible = supported;
        var show = supported && fits && TableLayoutToggle.IsChecked == true;
        TableScroll.IsVisible = show; ParameterScroll.IsVisible = !show;
        TablePages.IsEnabled = !Session.IsReadOnly;
        TableLayoutHint.IsVisible = supported && (!fits || pages.Values.Any(p => !p.IsTable));
        if (!show)
        {
            // Remove inactive table controls so detached pictures release their native image leases.
            if (TablePages.Children.Count > 0) { TablePages.Children.Clear(); _layoutIdentity = null; }
            return;
        }
        var identity = string.Join('|', rows.Select(r => $"{RuntimeHelpers.GetHashCode(r)}:{r.Instance.Placement}:{r.Kind}"))
            + string.Join('|', pages.Values.Select(p => $"{p.Id}:{p.Rows}:{string.Join(',', p.ColumnWeights)}:{p.IsTable}:{p.Title}"));
        if (_layoutIdentity == identity) return;
        _layoutIdentity = identity; TablePages.Children.Clear();
        var remaining = rows.ToHashSet();
        foreach (var page in pages.Values)
        {
            var fields = rows.Where(r => r.Instance.Placement?.BlockId == page.Id).ToArray(); if (fields.Length == 0) continue;
            var container = new StackPanel { Spacing = 8, Tag = page.Id };
            container.Children.Add(new TextBlock { Text = page.Title, TextWrapping = TextWrapping.Wrap, Classes = { "wb-heading" } });
            if (page.IsTable)
            {
                var table = new Grid { ColumnSpacing = 14, RowSpacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var weight in page.ColumnWeights) table.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(weight, GridUnitType.Star)));
                for (var row = 0; row < page.Rows; row++) table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                foreach (var field in fields)
                {
                    var control = Field(field); Grid.SetRow(control, field.Instance.Placement!.Row!.Value); Grid.SetColumn(control, field.Instance.Placement!.Column!.Value);
                    table.Children.Add(control); remaining.Remove(field);
                }
                container.Children.Add(table);
            }
            else foreach (var field in fields) { container.Children.Add(Field(field)); remaining.Remove(field); }
            TablePages.Children.Add(container);
        }
        foreach (var field in rows.Where(remaining.Contains)) TablePages.Children.Add(Field(field));
    }

    private Control Field(ParameterItemViewModel row)
    {
        var control = ParameterList.ItemTemplate!.Build(row)!;
        control.DataContext = row; control.Width = double.NaN; control.MaxWidth = 540; control.HorizontalAlignment = HorizontalAlignment.Stretch;
        return control;
    }

    private void OnHelp(object? sender, RoutedEventArgs e)
    { if (sender is Button { Tag: ParameterItemViewModel row }) ShowHelp(row); }
    private void OnCloseHelp(object? sender, RoutedEventArgs e) { HelpPanel.IsVisible = false; _helpRow = null; }
    private void ShowHelp(ParameterItemViewModel row) { _helpRow = row; RefreshHelp(); HelpPanel.IsVisible = true; }
    private void RefreshHelp()
    {
        if (_helpRow is not { } row) return;
        if (!Session.Rows.Contains(row)) { HelpPanel.IsVisible = false; _helpRow = null; return; }
        var help = row.ReadHelp(); HelpHeading.Text = WorkbenchStrings.Get("ParameterHelpFor", row.Name);
        HelpLanguage.Text = WorkbenchStrings.Get("ParameterHelpLanguage", help.Language ?? WorkbenchStrings.Get("Unknown"));
        var detail = help.Status == ParameterResourceStatus.Available ? help.Text : WorkbenchStrings.Get(help.Status switch
        {
            ParameterResourceStatus.TooLarge => "ParameterResourceTooLarge",
            ParameterResourceStatus.Unsupported => "ParameterResourceUnsupported",
            ParameterResourceStatus.Invalid => "ParameterResourceInvalid",
            _ => "ParameterResourceMissing",
        });
        HelpText.Text = string.IsNullOrWhiteSpace(row.Description) ? detail : row.Description + Environment.NewLine + Environment.NewLine + detail;
    }
    private void OnPresentationKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && HelpPanel.IsVisible) { HelpPanel.IsVisible = false; _helpRow = null; e.Handled = true; return; }
        if (e.Key != Key.F1 || e.Source is not Control source) return;
        var row = source.DataContext as ParameterItemViewModel ?? source.GetVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<ParameterItemViewModel>().FirstOrDefault();
        if (row is null) return; ShowHelp(row); e.Handled = true;
    }
}
