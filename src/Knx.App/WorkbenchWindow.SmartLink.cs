using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Knx.App.Services;
using Knx.Workbench;

namespace Knx.App;

public partial class WorkbenchWindow
{
    private sealed class SmartLinkChoice(MassLinkReviewRow item)
    {
        public MassLinkReviewRow Item { get; set; } = item;
        public bool Selected { get; set; } = item.CanApply;
        public bool TargetEdited { get; set; }
    }

    private sealed record MassTargetChoice(string? Id, string Text)
    {
        public override string ToString() => Text;
    }
    private sealed record MassScopeChoice(WorkbenchScope Scope, string Text)
    {
        public override string ToString() => Text;
    }

    private Window? ShowSmartLinkDialog()
    {
        if (ViewModel.Project is null) { ViewModel.Status = W("NoProjectLoaded"); return null; }
        var dialog = CreateDialog(W("SmartTitle"), 1080, 650); dialog.Name = "SmartLinkReviewDialog";
        void Fit()
        {
            if (dialog.Screens?.ScreenFromWindow(dialog) is not { } screen || dialog.RenderScaling <= 0) return;
            var maximumWidth = Math.Max(1, screen.WorkingArea.Width / dialog.RenderScaling - 20);
            var maximumHeight = Math.Max(1, screen.WorkingArea.Height / dialog.RenderScaling - 32);
            dialog.MinWidth = Math.Min(dialog.MinWidth, maximumWidth); dialog.MinHeight = Math.Min(dialog.MinHeight, maximumHeight);
            dialog.Width = Math.Clamp(dialog.Width, dialog.MinWidth, maximumWidth);
            dialog.Height = Math.Clamp(dialog.Height, dialog.MinHeight, maximumHeight);
        }
        dialog.Opened += (_, _) => Fit(); dialog.ScalingChanged += (_, _) => Fit();
        var body = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,*"), Margin = new(22, 22, 22, 14), RowSpacing = 10 };
        var summary = Note(""); summary.Name = "SmartLinkSummary";
        var help = Note(W("MassHint"));
        var onlyProblems = new CheckBox { Name = "MassLinkOnlyProblems", Content = W("MassOnlyProblems") };
        AutomationProperties.SetName(onlyProblems, W("MassOnlyProblems"));
        var scopeChoices = new[] { new MassScopeChoice(new(IncludeInactive: true), W("MassAllObjects")) }
            .Concat(ProjectQuery.DeviceList(ViewModel.Project).OrderBy(device => device.Address.Raw)
                .Select(device => new MassScopeChoice(new(WorkbenchScopeKind.Device, device.Id, IncludeInactive: true), $"{device.Address} · {device.Name ?? device.Id}"))).ToArray();
        var scope = new ComboBox { Name = "MassLinkScope", ItemsSource = scopeChoices, SelectedItem = scopeChoices[0], HorizontalAlignment = HorizontalAlignment.Stretch, Classes = { "wb-field" } };
        AutomationProperties.SetName(scope, W("MassScope"));
        var filters = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8 };
        filters.Children.Add(Note(W("MassScope"))); Grid.SetColumn(scope, 1); filters.Children.Add(scope);
        Grid.SetColumn(onlyProblems, 2); filters.Children.Add(onlyProblems);
        var list = new ListBox { Name = "SmartLinkResults", MinHeight = 120 };
        var columns = new Grid { ColumnDefinitions = new("30,2*,*,2*,2*"), ColumnSpacing = 8 };
        foreach (var (column, key) in new[] { (1, "MassObject"), (2, "MassStatus"), (3, "MassTarget"), (4, "MassReason") })
        {
            var label = Note(W(key)); Grid.SetColumn(label, column); columns.Children.Add(label);
        }
        Grid.SetRow(help, 1); Grid.SetRow(filters, 2); Grid.SetRow(columns, 3); Grid.SetRow(list, 4);
        body.Children.Add(summary); body.Children.Add(help); body.Children.Add(filters); body.Children.Add(columns); body.Children.Add(list);
        var error = new TextBlock { Name = "SmartLinkError", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "wb-error" } };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        var apply = ActionButton(W("SmartApply"), () => { }, "primary"); apply.Name = "SmartLinkApply"; apply.IsEnabled = false;
        var refresh = ActionButton(W("SmartRefresh"), () => { }, "quiet"); refresh.Name = "SmartLinkRefresh";
        dialog.Content = DialogLayout(body, DialogActions(DialogCancel(dialog), refresh, apply), error);
        MassLinkReviewPlan? review = null; SmartLinkChoice[] choices = []; MassTargetChoice[] targets = [];
        void UpdateSelection()
        {
            var count = choices.Count(c => c.Selected && c.Item.CanApply);
            apply.IsEnabled = review is not null && count > 0;
            summary.Text = W("MassSummary", choices.Count(choice => choice.Item.State == MassLinkReviewState.Ready),
                choices.Count(choice => choice.Item.State == MassLinkReviewState.Ambiguous),
                choices.Count(choice => choice.Item.State == MassLinkReviewState.Incompatible),
                choices.Count(choice => choice.Item.State == MassLinkReviewState.Unmatched),
                choices.Count(choice => choice.Item.State == MassLinkReviewState.Unverified)) + " · " + W("SmartSelected", count);
        }
        void Filter() => list.ItemsSource = onlyProblems.IsChecked == true
            ? choices.Where(choice => choice.Item.State != MassLinkReviewState.Ready).ToArray() : choices;
        onlyProblems.IsCheckedChanged += (_, _) => Filter();
        list.ItemTemplate = new FuncDataTemplate<SmartLinkChoice>((choice, _) =>
        {
            if (choice is null) return new TextBlock();
            var item = choice.Item;
            var title = $"{item.DeviceAddress} · {item.DeviceName} · #{item.ObjectNumber} · {item.ObjectText}";
            var check = new CheckBox { IsChecked = choice.Selected, IsEnabled = item.CanApply, VerticalAlignment = VerticalAlignment.Top };
            AutomationProperties.SetName(check, title);
            check.IsCheckedChanged += (_, _) => { choice.Selected = check.IsChecked == true; UpdateSelection(); };
            var text = new TextBlock { Text = title, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var state = new TextBlock { Text = MassStateLabel(item.State), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var reason = new TextBlock { Text = MassReason(item), TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "wb-muted" } };
            var target = new ComboBox
            {
                Name = "MassLinkTarget", Tag = item.Id, ItemsSource = targets,
                SelectedItem = targets.FirstOrDefault(value => value.Id == item.SelectedGroupId),
                HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 110, Classes = { "wb-field" },
            };
            AutomationProperties.SetName(target, W("MassTargetFor", title));
            target.SelectionChanged += (_, _) =>
            {
                if (review is null || target.SelectedItem is not MassTargetChoice selected || selected.Id == choice.Item.SelectedGroupId) return;
                try
                {
                    ViewModel.SelectMassLinkTarget(review, choice.Item.Id, selected.Id);
                    choice.Item = review.Rows.Single(value => value.Id == choice.Item.Id);
                    choice.TargetEdited = true;
                    choice.Selected = choice.Item.CanApply;
                    check.IsEnabled = choice.Item.CanApply; check.IsChecked = choice.Selected;
                    state.Text = MassStateLabel(choice.Item.State); reason.Text = MassReason(choice.Item);
                    error.Text = ""; UpdateSelection();
                    if (onlyProblems.IsChecked == true) Filter();
                }
                catch (Exception exception) { error.Text = exception.Message; apply.IsEnabled = false; }
            };
            var row = new Grid { ColumnDefinitions = new("30,2*,*,2*,2*"), ColumnSpacing = 8, Margin = new(4, 7) };
            row.Children.Add(check); Grid.SetColumn(text, 1); row.Children.Add(text);
            Grid.SetColumn(state, 2); row.Children.Add(state); Grid.SetColumn(target, 3); row.Children.Add(target);
            Grid.SetColumn(reason, 4); row.Children.Add(reason); return row;
        });
        void Review()
        {
            error.Text = ""; review = null; apply.IsEnabled = false;
            try
            {
                var previous = choices.ToDictionary(choice => choice.Item.Id, choice => (choice.Item.SelectedGroupId, choice.Selected, choice.TargetEdited), StringComparer.Ordinal);
                review = ViewModel.PreviewMassLinks((scope.SelectedItem as MassScopeChoice)?.Scope);
                ViewModel.SelectMassLinkTargets(review, review.Rows.Where(row => previous.TryGetValue(row.Id, out var old)
                        && old.TargetEdited && (old.SelectedGroupId is null || review.Targets.Any(target => target.Id == old.SelectedGroupId)))
                    .Select(row => new KeyValuePair<string, string?>(row.Id, previous[row.Id].SelectedGroupId)));
                choices = review.Rows.Select(item => new SmartLinkChoice(item)
                {
                    Selected = item.CanApply && (!previous.TryGetValue(item.Id, out var old) || old.Selected),
                    TargetEdited = previous.TryGetValue(item.Id, out var before) && before.TargetEdited,
                }).ToArray();
                targets = new[] { new MassTargetChoice(null, W("MassTargetNone")) }
                    .Concat(review.Targets.Select(target => new MassTargetChoice(target.Id, $"{target.Address} · {target.Name} · {target.DatapointType ?? W("Unknown")}"))).ToArray();
                Filter(); UpdateSelection();
            }
            catch (Exception ex) { choices = []; list.ItemsSource = choices; summary.Text = W("SmartFound", 0, 0); error.Text = ex.Message; }
        }
        refresh.Click += (_, _) => Review();
        scope.SelectionChanged += (_, _) => Review();
        apply.Click += (_, _) =>
        {
            if (review is null) return;
            try { ViewModel.ApplyMassLinks(review, choices.Where(c => c.Selected && c.Item.CanApply).Select(c => c.Item.Id)); dialog.Close(); RefreshWorkspace(); }
            catch (Exception ex) { error.Text = ex.Message; apply.IsEnabled = false; }
        };
        dialog.Closed += (_, _) => { if (review is not null) ViewModel.CancelMassLinkReview(review); };
        Review(); dialog.ShowDialog(this); return dialog;
    }

    private string MassReason(MassLinkReviewRow row) => row.Validation is null
        ? W(row.State == MassLinkReviewState.Ambiguous ? "MassAmbiguousHint" : row.Candidates.Count > 0 ? "MassNoTarget" : "MassNoMatch")
        : row.Validation.Ok ? row.DatapointType ?? W("Unknown") : W(row.Validation.Reason switch
        {
            LinkValidationReason.Missing => "SmartMissing", LinkValidationReason.Inactive => "SmartInactive",
            LinkValidationReason.BitSize => "SmartBits", LinkValidationReason.DatapointType or LinkValidationReason.LongMainType => "SmartDpt",
            LinkValidationReason.SecureCapability => "SmartSecure", _ => "SmartUnknown",
        });

    private string MassStateLabel(MassLinkReviewState state) => state switch
    {
        MassLinkReviewState.Ready => W("MassStateReady"),
        MassLinkReviewState.Ambiguous => W("MassStateAmbiguous"),
        MassLinkReviewState.Incompatible => W("MassStateIncompatible"),
        MassLinkReviewState.Unmatched => W("MassStateUnmatched"),
        _ => W("MassStateUnverified"),
    };

    private Task ExportLabelMergeAsync(bool devices) => ExportAsync(devices ? "geraete-seriendruck.tsv" : "gruppenadressen-seriendruck.tsv",
        project => WorkbenchLabelMerge.Render(project, devices));
}
