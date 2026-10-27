using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Workbench;

internal static class NavigatorFocusChecks
{
    internal static async Task Run(WorkbenchWindow shell, string theme, Action<string, bool> check)
    {
        var workspace = shell.Workspace;
        var perspective = workspace.Perspective;
        var visible = workspace.NavigatorVisible;
        var state = workspace.Navigators["topology"];
        var query = state.Query;
        var selected = state.SelectedId;
        var expanded = state.ExpandedIds.ToArray();
        var oldTab = workspace.ActiveTab;
        var oldTabs = workspace.Tabs.ToArray();
        var project = shell.ViewModel.Project!;
        var before = Knxproj.ToXml(project).ToString();
        var undo = shell.ViewModel.CanUndo;
        var nav = shell.FindControl<ListBox>("NavTree")!;
        var search = shell.FindControl<TextBox>("NavSearch")!;
        ListBoxItem? Row(string key) => nav.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(row => row.GetVisualDescendants().OfType<Grid>()
                .Any(grid => grid.Tag is WorkbenchNavNode node && node.Key == key));
        string? FocusKey() => (shell.FocusManager!.GetFocusedElement() as Control)?
            .GetVisualDescendants().OfType<Grid>().Select(grid => grid.Tag)
            .OfType<WorkbenchNavNode>().FirstOrDefault()?.Key;
        KeyEventArgs Press(Key key)
        {
            var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key };
            (shell.FocusManager!.GetFocusedElement() as Interactive)?.RaiseEvent(args);
            return args;
        }
        async Task FocusRow(string key)
        {
            var row = Row(key)!;
            nav.SelectedItem = row.DataContext;
            await Task.Delay(50);
            Row(key)!.Focus(NavigationMethod.Tab);
            await Task.Delay(30);
        }
        try
        {
            var root = WorkbenchProjection.Navigation(project, "topology").Single();
            var area = root.Children.First(node => node.Model is Area && node.Children.Count > 0);
            var line = area.Children.First(node => node.Model is Line);
            state.Query = "";
            state.ExpandedIds.Clear();
            state.ExpandedIds.Add(root.Key);
            workspace.NavigatorVisible = true;
            shell.Navigate("topology");
            shell.RefreshWorkspace();
            await Task.Delay(100);
            await FocusRow(area.Key);
            check(theme + " navigator expansion starts at the actual area row", FocusKey() == area.Key);

            var right = Press(Key.Right);
            await Task.Delay(100);
            check(theme + " navigator Right expands and preserves the regenerated row focus",
                right.Handled && state.ExpandedIds.Contains(area.Key) && Row(line.Key) is not null && FocusKey() == area.Key);
            Press(Key.Right);
            await Task.Delay(80);
            check(theme + " navigator repeated Right retains row focus", FocusKey() == area.Key);

            Press(Key.Right);
            shell.RefreshWorkspace();
            await Task.Delay(100);
            check(theme + " navigator focus restore survives a second queued refresh", FocusKey() == area.Key);

            Press(Key.Down);
            await Task.Delay(100);
            if (FocusKey() != line.Key || state.SelectedId != line.Key)
                Console.WriteLine($"Navigator trace: focus={shell.FocusManager!.GetFocusedElement()?.GetType().Name}, key={FocusKey()}, selected={state.SelectedId}, index={nav.SelectedIndex}");
            check(theme + " navigator Down reaches the child through its editor refresh",
                FocusKey() == line.Key && state.SelectedId == line.Key);

            await FocusRow(area.Key);
            var left = Press(Key.Left);
            await Task.Delay(100);
            check(theme + " navigator Left collapses and preserves the regenerated row focus",
                left.Handled && !state.ExpandedIds.Contains(area.Key) && Row(line.Key) is null && FocusKey() == area.Key);

            await FocusRow(area.Key);
            Press(Key.Right);
            search.Focus(NavigationMethod.Tab);
            await Task.Delay(100);
            check(theme + " navigator deferred restore respects a subsequent focus change",
                ReferenceEquals(shell.FocusManager!.GetFocusedElement(), search));
            shell.RefreshWorkspace();
            await Task.Delay(100);
            check(theme + " navigator refresh never takes focus from the search editor",
                ReferenceEquals(shell.FocusManager!.GetFocusedElement(), search));
            check(theme + " navigator expansion and selection preserve project and history",
                Knxproj.ToXml(project).ToString() == before && shell.ViewModel.CanUndo == undo);
        }
        finally
        {
            state.Query = query;
            state.SelectedId = selected;
            state.ExpandedIds.Clear();
            foreach (var key in expanded) state.ExpandedIds.Add(key);
            workspace.Perspective = perspective;
            workspace.NavigatorVisible = visible;
            foreach (var tab in workspace.Tabs.Where(tab => !oldTabs.Contains(tab)).ToArray()) workspace.Close(tab);
            workspace.ActiveTab = oldTab;
            shell.RefreshWorkspace();
        }
    }
}
