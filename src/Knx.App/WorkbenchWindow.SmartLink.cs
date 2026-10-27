using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Knx.App.Services;

namespace Knx.App;

public partial class WorkbenchWindow
{
    private sealed class SmartLinkChoice(SmartLinkReviewItem item)
    {
        public SmartLinkReviewItem Item { get; } = item;
        public bool Selected { get; set; } = item.CanApply;
    }

    private Window? ShowSmartLinkDialog()
    {
        if (ViewModel.Project is null) { ViewModel.Status = W("NoProjectLoaded"); return null; }
        var dialog = CreateDialog(W("SmartTitle"), 860, 590); dialog.Name = "SmartLinkReviewDialog";
        var body = new Grid { RowDefinitions = new("Auto,Auto,*"), Margin = new(22, 22, 22, 14), RowSpacing = 10 };
        var summary = Note(""); summary.Name = "SmartLinkSummary";
        var help = Note(W("SmartHint"));
        var list = new ListBox { Name = "SmartLinkResults", MinHeight = 120 };
        Grid.SetRow(help, 1); Grid.SetRow(list, 2); body.Children.Add(summary); body.Children.Add(help); body.Children.Add(list);
        var error = new TextBlock { Name = "SmartLinkError", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "wb-error" } };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        var apply = ActionButton(W("SmartApply"), () => { }, "primary"); apply.Name = "SmartLinkApply"; apply.IsEnabled = false;
        var refresh = ActionButton(W("SmartRefresh"), () => { }, "quiet"); refresh.Name = "SmartLinkRefresh";
        dialog.Content = DialogLayout(body, DialogActions(DialogCancel(dialog), refresh, apply), error);
        SmartLinkReviewPlan? review = null; SmartLinkChoice[] choices = [];
        void UpdateSelection()
        {
            var count = choices.Count(c => c.Selected);
            apply.IsEnabled = review is not null && count > 0;
            summary.Text = W("SmartFound", choices.Length, choices.Count(c => c.Item.CanApply)) + " · " + W("SmartSelected", count);
        }
        list.ItemTemplate = new FuncDataTemplate<SmartLinkChoice>((choice, _) =>
        {
            if (choice is null) return new TextBlock();
            var item = choice.Item;
            var title = $"{item.DeviceAddress} · #{item.ObjectNumber} · {item.ObjectText} → {item.GroupAddress} · {item.GroupName}";
            var check = new CheckBox { IsChecked = choice.Selected, IsEnabled = item.CanApply, VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetName(check, title);
            check.IsCheckedChanged += (_, _) => { choice.Selected = check.IsChecked == true; UpdateSelection(); };
            var text = new StackPanel { Spacing = 3 };
            text.Children.Add(new TextBlock { Text = title, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = W("SmartScore", item.Score, item.DatapointType ?? W("Unknown")), Classes = { "wb-muted" } });
            if (item.Error is { } problem) text.Children.Add(new TextBlock { Text = problem, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "wb-error" } });
            var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8, Margin = new(4, 7) };
            row.Children.Add(check); Grid.SetColumn(text, 1); row.Children.Add(text); return row;
        });
        void Review()
        {
            error.Text = ""; review = null; apply.IsEnabled = false;
            try
            {
                review = ViewModel.PreviewSmartLinks(); choices = review.Items.Select(item => new SmartLinkChoice(item)).ToArray();
                list.ItemsSource = choices; UpdateSelection();
            }
            catch (Exception ex) { choices = []; list.ItemsSource = choices; summary.Text = W("SmartFound", 0, 0); error.Text = ex.Message; }
        }
        refresh.Click += (_, _) => Review();
        apply.Click += (_, _) =>
        {
            if (review is null) return;
            try { ViewModel.ApplySmartLinks(review, choices.Where(c => c.Selected).Select(c => c.Item.Id)); dialog.Close(); RefreshWorkspace(); }
            catch (Exception ex) { error.Text = ex.Message; apply.IsEnabled = false; }
        };
        dialog.Closed += (_, _) => { if (review is not null) ViewModel.CancelSmartLinkReview(review); };
        Review(); dialog.ShowDialog(this); return dialog;
    }

    private Task ExportLabelMergeAsync(bool devices) => ExportAsync(devices ? "geraete-seriendruck.tsv" : "gruppenadressen-seriendruck.tsv",
        project => WorkbenchLabelMerge.Render(project, devices));
}
