using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Reports;
using Knx.Workbench;

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
                Async("Objektzuordnungen · CSV …", () => ExportAsync("zuordnungen.csv", AssociationsCsv)),
                Async("Demo-Projekt · JSON exportieren …", ExportDemoJsonAsync),
                Async("Demo-Projekt · JSON importieren …", ImportDemoJsonAsync),
                new Separator(), Item("Beenden", Close)
            } },
            new() { Header = "Bearbeiten", ItemsSource = new object[]
            {
                Item("Rückgängig    Ctrl Z", () => ViewModel.UndoCommand.Execute(null)),
                Item("Wiederherstellen    Ctrl Shift Z", () => ViewModel.RedoCommand.Execute(null)),
                Item("Zuordnungen bearbeiten", () => SetRepresentation("relations")),
                Item("Auswahl verknüpfen …", ShowBatchLinkDialog),
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
                Item("Secure-Übersicht", ShowSecureOverview),
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
    private void ShowHelp() => _ = ConfirmDialog.ShowMessageAsync(this, "BusChord · Workbench V4",
        "Raum, Linie, Gerät oder Gruppenadresse öffnen. Tabs behalten ihre eigenen Filter, Auswahl, Aufklappzustände und Darstellung.\n\n" +
        "Ctrl+B: Navigator · Ctrl+J: Werkzeug · Ctrl+K: Befehlssuche · Ctrl+Tab: nächster Tab · Ctrl+Z / Ctrl+Shift+Z: Projekt-Undo/Redo.\n\n" +
        "Mehrfachzuordnung prüft DPT, Länge und explizite Secure-Fähigkeit atomar. Die Sendeadresse wird ausdrücklich aus bestehenden Zuordnungen gewählt; beim Entfernen wird keine Ersatzadresse still bestimmt.\n\n" +
        "Parameter ändern erst nach Prüfen und Übernehmen das Projekt. Speichern übernimmt keine Entwürfe. Demo-Applikationen zeigen eine reine Wirkungsvorschau; unbekannte Herstellerdynamik und DCA werden nicht ausgeführt.\n\n" +
        "Monitor und Programmierwarteschlange sind projektgebundene Sitzungen: Ausblenden beendet sie nicht. Programmierung ist in dieser Fassung ausschließlich eine gekennzeichnete Simulation, kein realer Download. Die vollständige Aufzeichnung liegt getrennt vom Anzeigepuffer.\n\n" +
        "Native Projektarchive bleiben das Speicherformat; das V4-Demo-JSON ist ein eigenes, streng geprüftes Austauschformat und verweigert den Export nicht darstellbarer Herstellerdaten. ETS-Kompatibilität, Secure-Schlüsselverwaltung und reale Busprogrammierung werden nicht behauptet.");

    private async Task OpenProjectAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "KNX-Projekt öffnen", AllowMultiple = false,
            FileTypeFilter = [new("KNX-Projekt") { Patterns = ["*.knxproj"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await OpenPathAsync(path);
    }

    private async Task OpenPathAsync(string path)
    {
        if (!await ConfirmDiscardAsync()) return;
        await ResetSessionsForReplacementAsync();
        try { ViewModel.LoadProject(path); _parameters.DiscardDrafts(); RefreshWorkspace(); }
        catch (Exception ex) { ViewModel.Status = $"Projekt nicht ladbar: {ex.Message}"; await ConfirmDialog.ShowMessageAsync(this, "Projekt nicht ladbar", ex.Message); RefreshWorkspace(); }
    }

    private async Task NewProjectAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        var wizard = new NewProjectWindow();
        if (await wizard.ShowDialog<bool>(this) != true) return;
        var project = ProjectWizard.Create(wizard.Options);
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "Neues Projekt speichern", SuggestedFileName = project.Name + ".knxproj", DefaultExtension = "knxproj" });
        if (file?.TryGetLocalPath() is not { } path) return;
        await ResetSessionsForReplacementAsync();
        try { ViewModel.CreateProject(project, path); _parameters.DiscardDrafts(); RefreshWorkspace(); }
        catch (Exception ex) { ViewModel.Status = ex.Message; await ConfirmDialog.ShowMessageAsync(this, "Projekt nicht anlegbar", ex.Message); RefreshWorkspace(); }
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

    private static string AssociationsCsv(Project project)
    {
        var builder = new StringBuilder("Geräteadresse;Gerät;Objektnummer;Objekt;DPT;Länge;Flags;Aktiv;GA-Adresse;GA-Name;Sendend;Einbauort\r\n");
        foreach (var a in Knx.Workbench.WorkbenchProject.Associations(project))
        {
            foreach (var value in new[] { a.DeviceAddress, a.DeviceName, a.Number.ToString(),
                a.ObjectName, a.Dpt ?? "", a.Bits?.ToString() ?? "", a.Flags ?? "", a.Active ? "ja" : "nein",
                a.GroupAddress, a.GroupName, a.Sending ? "ja" : "nein", a.InstalledRoomId ?? "" })
                builder.Append(GroupMonitor.CsvField(value)).Append(';');
            builder.Length -= 1;
            builder.Append("\r\n");
        }
        return builder.ToString();
    }

    private async Task ExportDemoJsonAsync()
    {
        if (ViewModel.Project is not { } project) { ViewModel.Status = "Kein Projekt geladen"; return; }
        var json = Knx.Workbench.WorkbenchJson.Export(project);
        var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = "demo-projekt.json", DefaultExtension = "json" });
        if (file?.TryGetLocalPath() is not { } path) return;
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false));
        ViewModel.Status = "Demo-Projekt exportiert";
    }

    private async Task ImportDemoJsonAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new() { AllowMultiple = false,
            FileTypeFilter = [new("V4-Demo-Projekt") { Patterns = ["*.json"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            var imported = Knx.Workbench.WorkbenchJson.Import(await File.ReadAllTextAsync(path));
            await ResetSessionsForReplacementAsync();
            ViewModel.CreateProject(imported, null);
            _parameterSession.DiscardDrafts();
            RefreshWorkspace();
            ViewModel.Status = "Demo-Projekt importiert; als knxproj speichern für einen nativen Projektstand.";
        }
        catch (Exception ex)
        {
            ViewModel.Status = "Demo-Import fehlgeschlagen: " + ex.Message;
            await ConfirmDialog.ShowMessageAsync(this, "Demo-Import fehlgeschlagen", ex.Message);
        }
    }

    private void ShowSecureOverview()
    {
        if (ViewModel.Project is not { } project) { ViewModel.Status = "Kein Projekt geladen"; return; }
        var devices = ProjectQuery.DeviceList(project);
        var capable = devices.Where(d => project.Workbench.Devices.GetValueOrDefault(d.Id)?.SecureCapable == true).ToList();
        var unknown = devices.Count(d => project.Workbench.Devices.GetValueOrDefault(d.Id)?.SecureCapable is null);
        var secureGroups = project.Workbench.Groups.Count(g => g.Value.Secure == true);
        var lines = string.Join("\n", capable.Take(40).Select(d => $"{d.Address} · {d.Name}"));
        _ = ConfirmDialog.ShowMessageAsync(this, "KNX Secure · lesbare Übersicht",
            $"{capable.Count} von {devices.Count()} Geräten ausdrücklich Secure-fähig; {unknown} ohne belegte Fähigkeit.\n" +
            $"{secureGroups} Gruppenadressen sind als Secure markiert.\n\n" +
            (lines.Length > 0 ? lines : "Keine Secure-Fähigkeit in den Zusatzdaten belegt.") +
            "\n\nDiese Übersicht verwaltet keine Schlüssel oder Zertifikate und behauptet keine sichere Inbetriebnahme.");
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
            ("Programmierung", () => ShowTool("programming")), ("Layout zurücksetzen", Workspace.ResetLayout),
            ("Projekt öffnen", () => _ = GuardAsync(OpenProjectAsync)), ("Hilfe", ShowHelp)]);
        list.ItemTemplate = new FuncDataTemplate<PaletteEntry>((entry, _) => Label(entry?.Label ?? ""));
        void Filter()
        {
            list.ItemsSource = candidates.Where(c => c.Label.Contains(query.Text ?? "", StringComparison.OrdinalIgnoreCase)).Take(100)
                .Select(c => new PaletteEntry(c.Label, c.Run)).ToList();
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

    private void ShowBatchLinkDialog()
    {
        if (ViewModel.Project is not { } project || Workspace.ActiveTab is not { } tab) return;
        var selected = WorkbenchProjection.Rows(project, tab)
            .Where(r => r.Object is not null && tab.SelectedObjectIds.Contains(r.Key)).ToList();
        if (selected.Count == 0) { ViewModel.Status = "Zuerst mindestens ein Kommunikationsobjekt auswählen."; return; }
        var groups = ProjectQuery.GroupAddresses(project).OrderBy(g => g.Address.Raw).ToList();
        var dialog = new Window { Title = $"Zuordnen · {selected.Count} Objekt(e)", Width = 720, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new(16) };
        var top = new StackPanel { Spacing = 8 };
        var picker = new ComboBox { ItemsSource = groups, MinWidth = 420, PlaceholderText = "Gruppenadresse wählen" };
        picker.ItemTemplate = new FuncDataTemplate<GroupAddressEntry>((g, _) => Label(g is null ? "" : $"{g.Address} · {g.Name} · {g.DatapointType ?? "DPT?"}"));
        var preview = new ListBox { Height = 320 };
        var errors = new TextBlock { FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var add = ActionButton("Zuordnen", () => { }, "primary");
        add.IsEnabled = false;
        top.Children.Add(Label("Mehrfachzuordnung wird vollständig geprüft und atomar übernommen."));
        top.Children.Add(picker);
        top.Children.Add(preview);
        top.Children.Add(errors);
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top); panel.Children.Add(top);
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        actions.Children.Add(add);
        actions.Children.Add(ActionButton("Abbrechen", dialog.Close, "quiet"));
        DockPanel.SetDock(actions, Avalonia.Controls.Dock.Bottom); panel.Children.Add(actions);
        dialog.Content = panel;
        dialog.Opened += (_, _) => picker.Focus();
        dialog.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) dialog.Close(); };
        void Validate()
        {
            if (picker.SelectedItem is not GroupAddressEntry ga) { preview.ItemsSource = null; errors.Text = ""; add.IsEnabled = false; return; }
            var results = selected.Select(r =>
            {
                var reason = WorkbenchProject.ValidateLink(project, new(r.Device.Id, r.Object!.Id), ga.Id);
                var already = r.Object.Links.Contains(ga.Id);
                return new LinkPreview(r, reason, already);
            }).ToList();
            preview.ItemsSource = results;
            preview.ItemTemplate = new FuncDataTemplate<LinkPreview>((p, _) => Label(
                p is null ? "" : $"{(p.AlreadyLinked ? "bereits verknüpft" : p.Error is null ? "bereit" : "unverträglich")} · {p.Row.Name}"
                + (p.Error is null ? "" : " — " + p.Error)));
            var invalid = results.Where(r => r.Error is not null).ToList();
            add.IsEnabled = invalid.Count == 0;
            errors.Text = invalid.Count == 0
                ? results.All(r => r.AlreadyLinked) ? "Alle gewählten Objekte sind bereits mit dieser Adresse verknüpft; es wird nichts doppelt angelegt."
                : "Alle Objekte sind verträglich. Bestehende Zuordnungen bleiben erhalten."
                : $"{invalid.Count} Objekt(e) sind unverträglich; es wird nichts teilweise übernommen.";
            errors.Foreground = Avalonia.Media.Brush.Parse(invalid.Count == 0 ? "#268175" : "#b45054");
        }
        picker.SelectionChanged += (_, _) => Validate();
        add.Click += (_, _) =>
        {
            if (picker.SelectedItem is not GroupAddressEntry ga) return;
            try
            {
                var keys = selected.Select(r => new Knx.Workbench.ObjectKey(r.Device.Id, r.Object!.Id)).ToList();
                MutationResult result = new(false, 0, [], []);
                ViewModel.Edit($"Zuordnung {ga.Address}", p => result = WorkbenchProject.AddLinks(p, keys, ga.Id));
                if (!result.Ok)
                {
                    ViewModel.Status = result.Errors.FirstOrDefault()?.Reason ?? "Zuordnung nicht möglich";
                    return;
                }
                ViewModel.Status = $"{result.Changed} Zuordnung(en) zu {ga.Address} ergänzt; bestehende Links erhalten.";
                dialog.Close(); RefreshWorkspace();
            }
            catch (Exception ex) { ViewModel.Status = "Zuordnung fehlgeschlagen: " + ex.Message; }
        };
        dialog.ShowDialog(this);
    }

    private sealed record LinkPreview(WorkbenchRow Row, string? Error, bool AlreadyLinked);
    private sealed record PaletteEntry(string Label, Action Run);
}
