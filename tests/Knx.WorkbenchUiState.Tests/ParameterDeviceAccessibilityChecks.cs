using System.Reflection;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Services;

internal static class ParameterDeviceAccessibilityChecks
{
    internal static async Task Run(WorkbenchWindow shell, string theme, Action<string, bool> check)
    {
        var project = shell.ViewModel.Project!;
        var device = ProjectQuery.DeviceList(project).First();
        var oldTabs = shell.Workspace.Tabs.ToHashSet();
        var oldActive = shell.Workspace.ActiveTab;
        var before = Knxproj.ToXml(project).ToString();
        var canUndo = shell.ViewModel.CanUndo;
        try
        {
            shell.OpenTarget(new("device", device.Id), "params");
            await Task.Delay(100);
            var picker = (ComboBox)typeof(WorkbenchWindow).GetField("_parameterDevice", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;
            picker.Focus(NavigationMethod.Tab);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            var peer = ControlAutomationPeer.CreatePeerForElement(picker);
            var row = (DeviceRow)picker.SelectedItem!;
            var expected = $"{row.AddressText} · {row.Name}";
            check(theme + " parameter device picker has a localized name at actual focus",
                picker.IsFocused && peer?.GetName() == Strings.Get("ColDevice"));
            var value = peer?.GetProvider<IValueProvider>();
            check(theme + " selected device value uses the same friendly caption as its visible item",
                row.ToString() == expected && (value is null || value.Value == expected)
                && picker.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == expected && text.IsEffectivelyVisible));
        }
        finally
        {
            foreach (var tab in shell.Workspace.Tabs.Where(tab => !oldTabs.Contains(tab)).ToArray()) shell.Workspace.Close(tab);
            shell.Workspace.ActiveTab = oldActive;
            shell.RefreshWorkspace();
        }
        check(theme + " parameter device accessibility inspection preserves model and undo",
            before == Knxproj.ToXml(project).ToString() && canUndo == shell.ViewModel.CanUndo);
    }
}
