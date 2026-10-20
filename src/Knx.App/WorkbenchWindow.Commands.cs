using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Knx.App.Workbench;
using Knx.Reports;

namespace Knx.App;

public partial class WorkbenchWindow
{
    private void BuildMenus()
    {
        MenuItem Item(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); return item; }
        MenuItem Async(string title, Func<Task> action) => Item(title, () => _ = GuardAsync(action));
        MainMenu.ItemsSource = new MenuItem[]
        {
            new() { Header = "Projekt", ItemsSource = new object[]
            {
                Async("Neu …", NewProjectAsync), Async("Öffnen …", OpenProjectAsync), Async("Speichern", async () => await SaveAsync()),
                Async("Speichern unter …", async () => await SaveAsync(true)), new Separator(),
                Item("Produktkatalog", ShowCatalog), Item("Neue Gruppenadresse", () => { ViewModel.AddGroupAddress(); RefreshWorkspace(); }),
                Async("Gruppenadressen · CSV …", () => ExportAsync("gruppenadressen.csv", ProjectCsvExporter.GroupAddresses)),
                Async("Geräte · CSV …", () => ExportAsync("geraete.csv", ProjectCsvExporter.Devices)),
                Async("Topologie · CSV …", () => ExportAsync("topologie.csv", ProjectCsvExporter.Topology)),
                Async("Projektbericht · HTML …", () => ExportAsync("projektbericht.html", p => ProjectReport.Render(p))),
                new Separator(), Item("Beenden", Close)
            } },
            new() { Header = "Bearbeiten", ItemsSource = new object[]
            {
                Item("Rückgängig    Ctrl Z", () => ViewModel.UndoCommand.Execute(null)),
                Item("Wiederherstellen    Ctrl Shift Z", () => ViewModel.RedoCommand.Execute(null)),
                Item("Zuordnungen bearbeiten", () => SetRepresentation("relations")),
                Item("Eigenschaften", () => Workspace.InspectorVisible = true)
            } },
            new() { Header = "Ansicht", ItemsSource = new object[]
            {
                Item("Navigator    Ctrl B", () => Workspace.NavigatorVisible = !Workspace.NavigatorVisible),
                Item("Monitor    Ctrl J", () => ShowTool("monitor")), Item("Inspektor", () => Workspace.InspectorVisible = !Workspace.InspectorVisible),
                Item("Fokuslayout", Workspace.FocusLayout), Item("Layout zurücksetzen", Workspace.ResetLayout),
                Item("Befehlssuche    Ctrl K", ShowPalette), Item("Optionen …", ShowSettings)
            } },
            new() { Header = "Werkzeuge", ItemsSource = new object[]
            {
                Item("Projektprüfung", () => ShowTool("problems")), Item("Monitor", () => ShowTool("monitor")),
                Item("Produktkatalog", ShowCatalog), Async("Aufzeichnung exportieren …", ExportRecordingAsync),
                Item("Bedienung und Grenzen", ShowHelp)
            } }
        };
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ViewModel.Status = ex.Message; await ConfirmDialog.ShowMessageAsync(this, "Aktion fehlgeschlagen", ex.Message); }
    }

    public void ShowCatalog() => OpenTarget(new("tool", "catalog"));
    private void ShowSettings() => _ = GuardAsync(async () => await new SettingsWindow(ViewModel.Settings).ShowDialog(this));
    private void ShowHelp() => _ = ConfirmDialog.ShowMessageAsync(this, "BusChord · Workbench", 
        "Raum, Linie, Gerät oder Gruppenadresse öffnen. Tabs behalten ihre eigenen Filter, Auswahl und Darstellung.\n\n" +
        "Ctrl+B: Navigator · Ctrl+J: Werkzeug · Ctrl+K: Suche · Ctrl+Tab: nächster Tab · Ctrl+Z / Ctrl+Shift+Z: Projekt-Undo/Redo.\n\n" +
        "Parameter ändern erst nach Übernehmen das Projekt. Speichern übernimmt keine Entwürfe.\n\n" +
        "Dieser Meilenstein nutzt native Projektarchive und den vorhandenen Herstellerkatalog. Externe Raum-/Kanalbezüge, atomare Mehrfachzuordnung, Sendeadresse, Wirkungsvorschau und unabhängige Monitor-/Programmiersitzungen werden noch integriert. Keine reale Busprogrammierung und keine Secure-Schlüsselverwaltung in dieser Shell.");

    private async Task OpenProjectAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "KNX-Projekt öffnen", AllowMultiple = false,
            FileTypeFilter = [new("KNX-Projekt") { Patterns = ["*.knxproj"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await OpenPathAsync(path);
    }

    private async Task OpenPathAsync(string path)
    {
        if (!await ConfirmDiscardAsync()) return;
        try { ViewModel.LoadProject(path); _parameters.DiscardDrafts(); RefreshWorkspace(); }
        catch (Exception ex) { ViewModel.Status = $"Projekt nicht ladbar: {ex.Message}"; await ConfirmDialog.ShowMessageAsync(this, "Projekt nicht ladbar", ex.Message); }
    }

    private async Task NewProjectAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        var wizard = new NewProjectWindow();
        if (await wizard.ShowDialog<bool>(this) != true) return;
        var project = ProjectWizard.Create(wizard.Options);
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Neues Projekt speichern", SuggestedFileName = project.Name + ".knxproj", DefaultExtension = "knxproj" });
        if (file?.TryGetLocalPath() is not { } path) return;
        try { ViewModel.CreateProject(project, path); _parameters.DiscardDrafts(); RefreshWorkspace(); }
        catch (Exception ex) { ViewModel.Status = ex.Message; await ConfirmDialog.ShowMessageAsync(this, "Projekt nicht anlegbar", ex.Message); }
    }

    private async Task<bool> SaveAsync(bool saveAs = false)
    {
        if (ViewModel.Project is not { } project) return false;
        try
        {
            if (saveAs || ViewModel.ProjectPath.Length == 0)
            {
                var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = project.Name + ".knxproj", DefaultExtension = "knxproj" });
                if (file?.TryGetLocalPath() is not { } path) return false;
                ViewModel.SaveProjectAs(path);
            }
            else ViewModel.SaveProject();
            if (_parameters.HasUnappliedChanges) ViewModel.Status = "Projekt gespeichert; nicht übernommene Parameterentwürfe wurden nicht gespeichert.";
            return true;
        }
        catch (Exception ex) { ViewModel.Status = $"Speichern fehlgeschlagen: {ex.Message}"; await ConfirmDialog.ShowMessageAsync(this, "Speichern fehlgeschlagen", ex.Message); return false; }
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (_parameters.HasUnappliedChanges && !await ConfirmDialog.AskDiscardDraftsAsync(this)) return false;
        if (!ViewModel.IsDirty) return true;
        return await ConfirmDialog.AskUnsavedAsync(this, ViewModel.Project?.Name ?? "") switch
        {
            ConfirmChoice.Discard => true, ConfirmChoice.Save => await SaveAsync(), _ => false
        };
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_shuttingDown && (ViewModel.IsDirty || _parameters.HasUnappliedChanges))
        {
            e.Cancel = true;
            if (_confirmingClose) return;
            _confirmingClose = true;
            Dispatcher.UIThread.Post(async () =>
            {
                try { if (await ConfirmDiscardAsync()) { _parameters.DiscardDrafts(); _shuttingDown = true; Close(); } }
                finally { _confirmingClose = false; }
            });
            return;
        }
        if (_layoutPath is not null)
        {
            try { SaveLayout(_layoutPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ViewModel.Status = "Layout nicht gespeichert: " + ex.Message; }
        }
        _shuttingDown = true;
    }

    private async Task ExportAsync(string fileName, Func<Project, string> render)
    {
        if (ViewModel.Project is not { } project) { ViewModel.Status = "Kein Projekt geladen"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = fileName });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(render(project));
        ViewModel.Status = fileName + " exportiert";
    }

    private async Task ExportRecordingAsync()
    {
        if (ViewModel.Recorder.Count == 0) { ViewModel.Status = "Keine Aufzeichnung vorhanden"; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = "telegramme.csv", DefaultExtension = "csv" });
        if (file?.TryGetLocalPath() is { } path) ViewModel.Recorder.SaveCsv(path);
    }

    private void ShowPalette()
    {
        var dialog = new Window { Title = "Arbeitsgegenstand oder Befehl suchen", Width = 660, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new(16) };
        var query = new TextBox { PlaceholderText = "Raum, Gerät, GA oder Befehl …", Margin = new(0, 0, 0, 12) };
        var list = new ListBox();
        DockPanel.SetDock(query, Avalonia.Controls.Dock.Top); panel.Children.Add(query); panel.Children.Add(list); dialog.Content = panel;
        var candidates = new List<(string Label, Action Run)>();
        if (ViewModel.Project is { } p)
        {
            candidates.Add((p.Name, () => OpenTarget(new("project", p.Id))));
            foreach (var s in WorkbenchProjection.Spaces(p)) candidates.Add((s.Path, () => OpenTarget(new("room", s.Id))));
            foreach (var l in WorkbenchProjection.Lines(p)) candidates.Add(("Linie · " + l.Name, () => OpenTarget(new("line", l.Id))));
            foreach (var d in ProjectQuery.DeviceList(p)) candidates.Add(($"{d.Address} · {d.Name}", () => OpenTarget(new("device", d.Id))));
            foreach (var g in ProjectQuery.GroupAddresses(p)) candidates.Add(($"{g.Address} · {g.Name}", () => OpenTarget(new("ga", g.Id))));
        }
        candidates.AddRange([("Produktkatalog", ShowCatalog), ("Projektprüfung", () => ShowTool("problems")), ("Monitor", () => ShowTool("monitor")),
            ("Layout zurücksetzen", Workspace.ResetLayout), ("Projekt öffnen", () => _ = GuardAsync(OpenProjectAsync)), ("Hilfe", ShowHelp)]);
        list.ItemTemplate = new FuncDataTemplate<PaletteEntry>((entry, _) => Label(entry?.Label ?? ""));
        void Filter()
        {
            list.ItemsSource = candidates.Where(c => c.Label.Contains(query.Text ?? "", StringComparison.OrdinalIgnoreCase)).Take(100).Select(c => new PaletteEntry(c.Label, c.Run)).ToList();
            list.SelectedIndex = 0;
        }
        void Activate()
        {
            if (list.SelectedItem is not PaletteEntry entry) return;
            dialog.Close(); entry.Run();
        }
        query.TextChanged += (_, _) => Filter(); list.DoubleTapped += (_, _) => Activate();
        dialog.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) dialog.Close(); else if (e.Key == Avalonia.Input.Key.Enter) Activate(); };
        Filter(); dialog.Opened += (_, _) => query.Focus(); dialog.ShowDialog(this);
    }
    private sealed record PaletteEntry(string Label, Action Run);
}
