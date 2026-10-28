using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;

internal static class NewProjectAccessibilityChecks
{
    internal static async Task Run(WorkbenchWindow shell, string theme, Action<string, bool> check)
    {
        var dialog = new NewProjectWindow { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var closed = dialog.ShowDialog<bool>(shell);
        try
        {
            await Task.Delay(100);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var template = dialog.FindControl<ComboBox>("TemplateBox")!;
            var preview = dialog.FindControl<TextBlock>("TopologyPreview")!;
            check(theme + " new project defaults to explicit TP line 1.1",
                template.SelectedIndex == 0 && preview.Text?.Contains("1.1.1") == true
                && ControlAutomationPeer.CreatePeerForElement(template)?.GetName() == WorkbenchStrings.Get("NewTopologyTemplate"));
            template.SelectedIndex = 1;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            check(theme + " new project legacy template updates address preview and limits",
                preview.Text?.Contains("0.0.1") == true && dialog.FindControl<NumericUpDown>("AreasBox")!.Maximum == 16);
            template.SelectedIndex = 0;
            foreach (var (name, key) in new[]
            {
                ("NameBox", "ColName"), ("CommentBox", "NewProjectComment"),
                ("StyleBox", "NewProjectStyle"), ("AreasBox", "NewProjectAreas"), ("LinesBox", "NewProjectLines"),
            })
            {
                var control = dialog.FindControl<Control>(name);
                var label = Strings.Get(key);
                check(theme + " new project accessible field matches its visible localized label: " + name,
                    control is { IsEffectivelyVisible: true }
                    && ControlAutomationPeer.CreatePeerForElement(control)?.GetName() == label
                    && dialog.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == label && text.IsEffectivelyVisible));
                if (control is NumericUpDown numeric)
                {
                    var editor = numeric.GetVisualDescendants().OfType<TextBox>().Single(text => text.Name == "PART_TextBox");
                    editor.Focus();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    check(theme + " new project focused numeric editor has its field label: " + name,
                        editor.IsFocused && ControlAutomationPeer.CreatePeerForElement(editor)?.GetName() == label);
                }
            }
        }
        finally
        {
            dialog.Close(false);
            await closed;
        }
    }
}
