using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx.App.Services;
using Knx.App.Localization;
using Knx.App.Views;
using Knx.App.Workbench;
using Knx.Portfolio;
using Knx.ProjectCompare;
using Knx.Reports;
using Knx.Workbench;

namespace Knx.App;

public partial class WorkbenchWindow
{
    private HelpWindow? _helpWindow;
    private void BuildMenus()
    {
        MenuItem Item(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); return item; }
        MenuItem Async(string title, Func<Task> action) => Item(title, () => _ = GuardAsync(action));
        MainMenu.ItemsSource = new MenuItem[]
        {
            new() { Header = W("MenuProject"), ItemsSource = new object[]
            {
                Async(W("MenuNew"), NewProjectAsync), Async(W("MenuOpen"), OpenProjectAsync), Async(W("MenuSave"), async () => await SaveAsync()),
                Async(W("MenuSaveAs"), async () => await SaveAsync(true)), new Separator(),
                Async(W("Portfolio"), ShowPortfolioAsync),
                Item(W("Catalog"), ShowCatalog), Item(W("NewGroupAddress"), ShowNewGroupAddressDialog),
                Async(W("ExportGroupAddresses"), () => ExportAsync("gruppenadressen.csv", ProjectCsvExporter.GroupAddresses)),
                Async(W("ExportDevices"), () => ExportAsync("geraete.csv", ProjectCsvExporter.Devices)),
                Async(W("ExportTopology"), () => ExportAsync("topologie.csv", ProjectCsvExporter.Topology)),
                Async(W("ExportReport"), () => ExportAsync("projektbericht.html", p => ProjectReport.Render(p))),
                Async(W("ExportAssociations"), () => ExportAsync("zuordnungen.csv", ProjectCsvExporter.Associations)),
                Async(W("ExportLabels"), ExportLabelsAsync),
                Async(W("MergeDevices"), () => ExportLabelMergeAsync(true)),
                Async(W("MergeGroups"), () => ExportLabelMergeAsync(false)),
                Async(W("ExportSemanticTtl"), () => ExportSemanticAsync(SemanticExport.SemanticFormat.Turtle, "semantik.ttl", "ttl")),
                Async(W("ExportSemanticJsonLd"), () => ExportSemanticAsync(SemanticExport.SemanticFormat.JsonLd, "semantik.jsonld", "jsonld")),
                Async(W("CompareProjects"), CompareProjectsAsync),
                Async(W("ReplaceProduct"), ReplaceProductAsync),
                Item(W("CreateBackup"), CreateBackup),
                Async(W("Backups"), ShowBackupsAsync),
                Async(RecoveryUiStrings.Get("Title"), ShowRecoveryAsync),
                Async(W("ExportDemoJson"), ExportDemoJsonAsync),
                Async(W("ImportDemoJson"), ImportDemoJsonAsync),
                Async(W("ResetDemo"), ResetDemoAsync),
                new Separator(), Item(W("MenuExit"), Close)
            } },
            new() { Header = W("MenuEdit"), ItemsSource = new object[]
            {
                Item(W("MenuUndo"), () => ViewModel.UndoCommand.Execute(null)),
                Item(W("MenuRedo"), () => ViewModel.RedoCommand.Execute(null)),
                Item(W("EditLinks"), () => SetRepresentation("relations")),
                Item(W("LinkSelection"), ShowBatchLinkDialog),
                Item(W("SmartTitle"), () => ShowSmartLinkDialog()),
                Item(W("SelectAll"), SelectAllRows),
                Item(W("ClearSelection"), ClearSelection),
                Item(W("Properties"), () => Workspace.InspectorVisible = true)
            } },
            new() { Header = W("MenuView"), ItemsSource = new object[]
            {
                Item(W("MenuNavigator"), () => Workspace.NavigatorVisible = !Workspace.NavigatorVisible),
                Item(W("MenuMonitor"), () => ShowTool("monitor")), Item(W("MenuInspector"), () => Workspace.InspectorVisible = !Workspace.InspectorVisible),
                Item(W("FocusLayout"), Workspace.FocusLayout), Item(W("ResetLayout"), Workspace.ResetLayout),
                Item(W("MenuPalette"), ShowPalette), Item(W("MenuOptions"), ShowSettings)
            } },
            new() { Header = W("MenuTools"), ItemsSource = new object[]
            {
                Item(W("ToolsOverview"), ShowToolsOverview),
                Async(LiveConnectionStrings.Get("Title"), ShowConnectionAsync),
                Async(M112CommissioningStrings.Get("Title"), ShowM112Async),
                Async(AddressAssignmentStrings.Get("Title"), ShowAddressAssignmentAsync),
                Item(W("ProjectCheck"), () => ShowTool("problems")), Item(W("Monitor"), () => ShowTool("monitor")),
                Item(W("HistoryTitle"), ShowTelegramHistory),
                Item(W("Scan"), () => ShowTool("scan")), Item(W("Changes"), () => ShowTool("changes")),
                Item(W("SidebarSearch"), () => ShowTool("search")),
                Item(W("Catalog"), ShowCatalog), Async(W("ExportRecording"), ExportRecordingAsync),
                Item(W("SecureOverview"), ShowSecureOverview),
                Item(W("HelpTip"), ShowHelp)
            } }
        };
    }

    private Window CreateDialog(string title, double width, double height)
    {
        var dialog = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = Math.Min(width, 420),
            MinHeight = Math.Min(height, 220),
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        WorkbenchDialogChrome.Apply(dialog);
        // Unhandled Escape bubbles after editors/popups have their turn.
        dialog.Classes.Add("wb-dialog");
        dialog.KeyDown += (_, e) =>
        {
            if (!e.Handled && e.Key == Avalonia.Input.Key.Escape)
            { dialog.Close(); e.Handled = true; }
        };
        dialog.Opened += (_, _) => dialog.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsCancel)?.Focus();
        return dialog;
    }

    /// <summary>Shared V4 dialog layout: body above the 13 px footer, so actions stay reachable.</summary>
    private static Grid DialogLayout(Control body, Control actions, Control? message = null)
    {
        var grid = new Grid { RowDefinitions = new("*,Auto") };
        // Stack forms need a bounded viewport, not a footer over their last fields.
        grid.Children.Add(body is StackPanel ? new ScrollViewer
        {
            Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        } : body);
        var footer = new Border { Classes = { "wb-dialog-footer" }, Child = message is null ? actions
            : new StackPanel { Spacing = 8, Children = { message, actions } } };
        Grid.SetRow(footer, 1);
        grid.Children.Add(footer);
        return grid;
    }

    /// <summary>V4 action order: quiet Cancel first, primary last, right-aligned.</summary>
    private static WrapPanel DialogActions(params Control[] actions)
    {
        var row = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            ItemSpacing = 8, LineSpacing = 8
        };
        foreach (var action in actions) row.Children.Add(action);
        return row;
    }

    private static Button DialogCancel(Window dialog)
    {
        var button = ActionButton(W("Cancel"), dialog.Close, "quiet");
        button.IsCancel = true;
        return button;
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ViewModel.Status = ex.Message; await ConfirmDialog.ShowMessageAsync(this, W("ActionFailed"), ex.Message); }
    }

    public void ShowCatalog()
    {
        EnsureSessions();
        if (Workspace.ActiveTab?.Target != new WorkbenchTarget("tool", "catalog"))
        {
            _catalogProject = ViewModel.ProjectSessionIdentity;
            _catalogLineId = CurrentLine()?.Id;
            _catalogRoomId = Workspace.ActiveTab?.Target is { Kind: "room" } target ? target.Id : null;
        }
        OpenTarget(new("tool", "catalog"));
    }
    private void ShowSettings() => _ = GuardAsync(async () => await new SettingsWindow(ViewModel.Settings).ShowDialog(this));
    private void ShowHelp()
    {
        if (_helpWindow is { } open)
        {
            open.Activate();
            return;
        }
        var help = new HelpWindow();
        _helpWindow = help;
        help.Closed += (_, _) => { if (ReferenceEquals(_helpWindow, help)) _helpWindow = null; };
        _ = GuardAsync(async () => await help.ShowDialog(this));
    }

    private async Task OpenProjectAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = W("OpenProjectTitle"), AllowMultiple = false,
            FileTypeFilter = [new(W("KnxProjectFile")) { Patterns = ["*.knxproj"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await OpenPathAsync(path);
    }

    private async Task<bool> OpenPathAsync(string path)
    {
        if (!await ConfirmDiscardAsync()) return false;
        var replacementSession = ViewModel.ProjectSessionIdentity; var replacementRevision = _recovery?.CommitRevision;
        using var frozenEditors = FreezeReplacementEditors();
        try
        {
            await DrainRecoveryBeforeReplacementAsync();
            var recovery = await TryRecoverySourceStampAsync(path);
            Project loaded;
            string? password = null;
            while (true)
            {
                try
                {
                    loaded = Knxproj.Load(path, password, Knx.Scripting.IsolatedScriptRuntime.Shared);
                    break;
                }
                catch (InvalidDataException ex) when (Knxproj.IsPasswordError(ex))
                {
                    password = await PromptPasswordAsync(ex.Message);
                    if (password is null) return false;
                }
            }
            // Adoption prepares undo and persists settings before publishing. A failed
            // replacement must leave the old monitor and every hidden draft intact.
            if (recovery.Stamp is not null)
            {
                try { await recovery.Stamp.VerifyAsync(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { recovery = (null, "Recovery.SourceChanged"); }
            }
            if (!ReferenceEquals(replacementSession, ViewModel.ProjectSessionIdentity) || replacementRevision != _recovery?.CommitRevision) return false;
            ViewModel.AdoptProject(loaded, path);
            await BindRecoveryCurrentProjectAsync(recovery.Stamp, recovery.Reason);
            _parameters.DiscardDrafts();
            await ResetSessionsForReplacementAsync();
            ViewModel.Status = RecoveryAwareStatus(W("ProjectLoaded", ViewModel.Project?.Name ?? ""));
            RefreshWorkspace();
            return true;
        }
        catch (Exception ex)
        {
            ViewModel.Status = W("ProjectLoadFailedStatus", ex.Message);
            await ConfirmDialog.ShowMessageAsync(this, W("ProjectLoadFailed"), ex.Message);
            RefreshWorkspace();
            return false;
        }
    }

    private async Task<string?> PromptPasswordAsync(string message, bool saving = false, Window? owner = null)
    {
        var dialog = CreateDialog(W("Password"), 460, 230);
        var panel = new StackPanel { Margin = new(22, 22, 22, 14), Spacing = 10 };
        panel.Children.Add(Note(message));
        if (message != W("PasswordPrompt")) panel.Children.Add(Note(W("PasswordPrompt")));
        var box = new TextBox { PasswordChar = '●', Classes = { "wb-field" } };
        panel.Children.Add(box);
        string? result = null;
        var actions = DialogActions(
            DialogCancel(dialog),
            ActionButton(W(saving ? "MenuSave" : "Open"), () => { result = box.Text; dialog.Close(); }, "primary", isDefault: true));
        dialog.Content = DialogLayout(panel, actions);
        dialog.Opened += (_, _) => box.Focus();
        await dialog.ShowDialog(owner ?? this);
        box.Text = "";
        return result;
    }

    private async Task NewProjectAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        var wizard = new NewProjectWindow();
        if (await wizard.ShowDialog<bool>(this) != true) return;
        var project = ProjectWizard.Create(wizard.Options);
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = W("SaveNewProjectTitle"), SuggestedFileName = project.Name + ".knxproj", DefaultExtension = "knxproj" });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            await DrainRecoveryBeforeReplacementAsync();
            ViewModel.CreateProject(project, path);
            await BindRecoveryCurrentProjectAsync();
            _parameters.DiscardDrafts();
            await ResetSessionsForReplacementAsync();
            ViewModel.Status = RecoveryAwareStatus(W("ProjectCreated", project.Name));
            RefreshWorkspace();
        }
        catch (Exception ex) { ViewModel.Status = ex.Message; await ConfirmDialog.ShowMessageAsync(this, W("ProjectCreateFailed"), ex.Message); RefreshWorkspace(); }
    }

    private async Task<bool> SaveAsync(bool saveAs = false)
    {
        if (ViewModel.Project is not { } project) return false;
        // Finish audit/checkpoint work against the old file hash before the native save replaces it.
        if (_recovery is not null)
        {
            try { await _recovery.DrainAsync(); }
            catch (IOException) { ViewModel.Status = RecoveryUiStrings.Get("Failed"); }
        }
        string? backupWarning = null;
        void TryBackup(string path)
        {
            try { ProjectBackup.Create(path); }
            catch (Exception ex) { backupWarning = W("BackupFailed", ex.Message); }
        }
        try
        {
            string path;
            if (saveAs || ViewModel.ProjectPath.Length == 0)
            {
                var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = project.Name + ".knxproj", DefaultExtension = "knxproj" });
                if (file?.TryGetLocalPath() is not { } chosen) return false;
                path = chosen;
                if (File.Exists(path)) TryBackup(path);
            }
            else
            {
                path = ViewModel.ProjectPath;
                if (File.Exists(path)) TryBackup(path);
            }
            // Record the durable revision before writing, so the saved archive carries the
            // revision that describes the state being saved. The log is additive and undo-safe.
            string? password = null;
            if (Knxproj.IsPasswordProtected(project))
            {
                password = await PromptPasswordAsync(W("PasswordPrompt"), saving: true);
                if (password is null) return false;
            }
            while (true)
            {
                try
                {
                    if (!ReferenceEquals(ViewModel.Project, project)) return false;
                    var revision = ProjectRevisions.Capture(project, path);
                    try
                    {
                        if (saveAs || ViewModel.ProjectPath.Length == 0) ViewModel.SaveProjectAs(path, password);
                        else ViewModel.SaveProject(password);
                    }
                    catch { project.Workbench.Revisions.Remove(revision); throw; }
                    var savedRevision = _recovery?.CommitRevision;
                    if (_recovery is not null)
                    {
                        var wasEnabled = IsEnabled; IsEnabled = false;
                        try { await _recovery.DrainAsync(); }
                        catch (IOException) { ViewModel.Status = RecoveryUiStrings.Get("Failed"); }
                        finally { IsEnabled = wasEnabled; }
                    }
                    if (!ReferenceEquals(ViewModel.Project, project) || savedRevision != _recovery?.CommitRevision) return false;
                    break;
                }
                catch (InvalidDataException ex) when (Knxproj.IsPasswordError(ex))
                {
                    password = await PromptPasswordAsync(ex.Message, saving: true);
                    if (password is null) return false;
                }
            }
            if (backupWarning is not null) ViewModel.Status = backupWarning;
            else if (HasParameterDrafts) ViewModel.Status = W("SavedWithDrafts");
            return true;
        }
        catch (Exception ex) { ViewModel.Status = W("SaveFailedStatus", ex.Message); await ConfirmDialog.ShowMessageAsync(this, W("SaveFailed"), ex.Message); return false; }
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        await DrainRealProgrammingBeforeReplacementAsync();
        await DrainAddressAssignmentBeforeReplacementAsync();
        await DrainM112BeforeReplacementAsync();
        if (HasParameterDrafts && !await ConfirmDialog.AskDiscardDraftsAsync(this)) return false;
        if (!ViewModel.IsDirty) return true;
        return await ConfirmDialog.AskUnsavedAsync(this, ViewModel.Project?.Name ?? "") switch
        {
            ConfirmChoice.Discard => true, ConfirmChoice.Save => await SaveAsync(), _ => false
        };
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason == WindowCloseReason.OSShutdown && AppUpdateService.Current is { } updates)
            updates.SuppressApply = true;
        if (JoinRealProgrammingBeforeOwnerClose(e)) return;
        if (JoinAddressAssignmentBeforeOwnerClose(e)) return;
        if (JoinM112BeforeOwnerClose(e)) return;
        if (_closingSessionsForExit) { e.Cancel = true; return; }
        if (!_shuttingDown && (ViewModel.IsDirty || HasParameterDrafts))
        {
            e.Cancel = true;
            if (_confirmingClose) return;
            _confirmingClose = true;
            Dispatcher.UIThread.Post(async () =>
            {
                try { if (await ConfirmDiscardAsync()) { _parameters.DiscardDrafts(); _demoDrafts.Clear(); _shuttingDown = true; Close(); } }
                finally { _confirmingClose = false; }
            });
            return;
        }
        if (_recovery is not null && !_recoveryCloseApproved)
        {
            e.Cancel = true; _closingSessionsForExit = true; IsEnabled = false;
            Dispatcher.UIThread.Post(() => _recoveryCloseCompletion = FinishRecoveryCloseAsync());
            return;
        }
        _closingSessionsForExit = true;
        _ = DisposeSessionsAsync();
        var sessionsEnded = Task.WhenAll(_pendingSessionDisposals);
        sessionsEnded = CloseCredentialVaultAfterSessionsAsync(sessionsEnded);
        if (!sessionsEnded.IsCompletedSuccessfully) _pendingSessionDisposals.Add(sessionsEnded);
        if (!sessionsEnded.IsCompletedSuccessfully)
        {
            // Keep the dispatcher and application lease alive while cancellation unwinds.
            // Closing the native lifetime first could strand UI-bound continuations.
            e.Cancel = true;
            IsEnabled = false;
            Dispatcher.UIThread.Post(async () =>
            {
                try { await sessionsEnded; }
                catch
                {
                    _closingSessionsForExit = false;
                    _shuttingDown = false; IsEnabled = true;
                    ViewModel.Status = LiveConnectionStrings.Get("CleanupFailed");
                    return;
                }
                _closingSessionsForExit = false;
                _shuttingDown = true; Close();
            });
            return;
        }
        _closingSessionsForExit = false;
        if (_layoutPath is not null)
        {
            try
            {
                if (ProjectLayoutPath(Workspace.ProjectId) is { } projectLayout)
                {
                    SaveLayout(projectLayout);
                    if (!string.Equals(projectLayout, _layoutPath, StringComparison.OrdinalIgnoreCase))
                        SaveLayout(_layoutPath);
                }
                else SaveLayout(_layoutPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ViewModel.Status = W("LayoutSaveFailed", ex.Message); }
        }
        _shuttingDown = true;
    }

    private async Task ExportAsync(string fileName, Func<Project, string> render)
    {
        if (ViewModel.Project is not { } project) { ViewModel.Status = W("NoProjectLoaded"); return; }
        var content = render(project); // Rendering/validation must succeed before any destination is opened or truncated.
        var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = fileName });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content);
        ViewModel.Status = W("Exported", fileName);
    }

    private async Task ExportRecordingAsync()
    {
        if (_sessions is { } sessions && sessions.Monitor.RecordedCount > 0)
        { await ExportFullMonitorAsync(); return; }
        if (ViewModel.Recorder.Count == 0) { ViewModel.Status = W("NoRecording"); return; }
        var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = "telegramme.csv", DefaultExtension = "csv" });
        if (file?.TryGetLocalPath() is { } path) ViewModel.Recorder.SaveCsv(path);
    }

    private async Task ExportDemoJsonAsync()
    {
        if (ViewModel.Project is not { } project) { ViewModel.Status = W("NoProjectLoaded"); return; }
        var json = Knx.Workbench.WorkbenchJson.Export(project);
        var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = "demo-projekt.json", DefaultExtension = "json" });
        if (file?.TryGetLocalPath() is not { } path) return;
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false));
        ViewModel.Status = W("DemoExported");
    }

    private async Task ShowBackupsAsync()
    {
        var path = ViewModel.ProjectPath.Length == 0 ? null : ViewModel.ProjectPath;
        var backups = ProjectBackup.List(path);
        var dialog = CreateDialog(W("Backups"), 720, 460);
        var panel = new StackPanel { Margin = new(22, 22, 22, 14), Spacing = 8 };
        if (path is not null) panel.Children.Add(Note(path));
        if (backups.Count == 0) panel.Children.Add(Note(W("NoBackups")));
        foreach (var backup in backups)
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(Label($"{backup.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {backup.Name} · {backup.SizeBytes / 1024} KiB"));
            row.Children.Add(ActionButton(W("Restore"), () => _ = RestoreBackupAsync(dialog, backup), "quiet"));
            panel.Children.Add(row);
        }
        dialog.Content = DialogLayout(new ScrollViewer { Content = panel },
            DialogActions(DialogCancel(dialog)));
        await dialog.ShowDialog(this);
    }

    private PortfolioRegistry? _portfolio;

    /// <summary>
    /// Portfolio next to the effective settings file, seeded once from the recent list. It is a
    /// separate document: <see cref="AppSettings"/> never learns about it and stays single-project.
    /// </summary>
    internal PortfolioRegistry Portfolio
    {
        get
        {
            if (_portfolio is not null) return _portfolio;
            var registry = new PortfolioRegistry(WorkbenchPortfolio.StoragePath(AppSettings.Path));
            _portfolio = registry;
            try
            {
                registry.Seed(ViewModel.Settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                ViewModel.Status = W("PortfolioSeedFailed", ex.Message);
            }
            return registry;
        }
    }

    /// <summary>Project → portfolio: one list, one open project at a time, no parallel project tabs.</summary>
    internal async Task ShowPortfolioAsync() => await CreatePortfolioWindow().ShowDialog(this);

    /// <summary>Builds the portfolio surface without showing it, so screenshots can capture it.</summary>
    internal Window CreatePortfolioWindow()
    {
        var registry = Portfolio;
        var dialog = CreateDialog(W("PortfolioTitle"), 900, 640);
        dialog.MinWidth = 720;
        dialog.MinHeight = 420;

        var body = new Grid { RowDefinitions = new("Auto,*") };
        var header = new StackPanel { Margin = new(22, 18, 22, 10), Spacing = 4 };
        header.Children.Add(Note(W("PortfolioIntro")));
        var count = new TextBlock { FontSize = 10, Classes = { "wb-muted" } };
        header.Children.Add(count);
        var message = Note("");
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);
        header.Children.Add(message);
        body.Children.Add(header);

        var rows = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = rows, Margin = new(22, 0, 22, 8),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 1);
        body.Children.Add(scroll);

        // Hashing runs on the thread pool: the gate drops a late result after a newer action
        // rendered, and closing the dialog cancels the remaining files. Rows stay UI-thread only.
        var gate = new PortfolioRefreshGate();
        var checks = new CancellationTokenSource();
        dialog.Closed += (_, _) => checks.Cancel();

        void Report(string text) => message.Text = text;
        void Render()
        {
            gate.Begin();
            rows.Children.Clear();
            var entries = registry.Entries;
            count.Text = W("PortfolioCount", entries.Count);
            if (entries.Count == 0)
            {
                rows.Children.Add(EmptyState("folder", W("PortfolioEmpty"), W("PortfolioEmptyHint")));
                return;
            }
            foreach (var entry in entries)
                rows.Children.Add(BuildPortfolioRow(dialog, registry, entry, gate, Render, Report));
        }

        void AddCurrent()
        {
            var name = ViewModel.Project?.Name ?? "";
            var result = WorkbenchPortfolio.AddCurrent(registry, ViewModel.ProjectPath, name);
            Report(result.Status switch
            {
                PortfolioActionStatus.Done => W("PortfolioAdded", name),
                PortfolioActionStatus.AlreadyRegistered => W("PortfolioAlready"),
                PortfolioActionStatus.ProjectNotSaved => W("PortfolioNoProject"),
                PortfolioActionStatus.FileMissing => W("PortfolioFileMissingNow"),
                _ => PortfolioActionError(result, registry),
            });
            Render();
        }

        async Task CheckAllAsync()
        {
            var version = gate.Begin();
            var result = await WorkbenchPortfolio.CheckAllAsync(registry, checks.Token);
            // A cancelled run reports the files it did reach; a stale one is dropped entirely.
            if (!gate.IsCurrent(version)) return;
            Report(W("PortfolioChecked", result.Checked)
                + (result.Failed > 0 ? " · " + W("PortfolioFailed") : ""));
            Render();
        }

        Render();
        dialog.Content = DialogLayout(body, DialogActions(
            ActionButton(W("PortfolioCheckAll"), () => _ = GuardAsync(CheckAllAsync), "quiet"),
            ActionButton(W("Close"), dialog.Close, "quiet"),
            ActionButton(W("PortfolioAddCurrent"), AddCurrent, "primary")));
        dialog.Opened += (_, _) => Render();
        return dialog;
    }

    /// <summary>One portfolio row: status, path, revision/hash, last open and safe actions.</summary>
    private Control BuildPortfolioRow(Window owner, PortfolioRegistry registry, PortfolioEntry entry,
        PortfolioRefreshGate gate, Action render, Action<string> report)
    {
        var state = registry.State(entry);
        var left = new StackPanel { Spacing = 2 };
        var title = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock
        {
            Text = entry.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var stateLabel = new TextBlock
        {
            Text = W(WorkbenchPortfolio.StateKey(state)), FontSize = 10, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        stateLabel.Classes.Add(state switch
        {
            PortfolioState.Missing => "wb-error",
            PortfolioState.Changed => "wb-warning",
            _ => "wb-success",
        });
        title.Children.Add(stateLabel);
        left.Children.Add(title);
        left.Children.Add(new TextBlock
        {
            Text = entry.Path, FontSize = 11, Classes = { "wb-muted" },
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var stamp = WorkbenchPortfolio.TimestampLabel(entry.LastOpenedUtc);
        left.Children.Add(new TextBlock
        {
            Text = W("PortfolioRevision", entry.Revision, WorkbenchPortfolio.ShortHash(entry))
                + " · " + W("PortfolioLastOpened", stamp.Length == 0 ? W("PortfolioNeverOpened") : stamp),
            FontSize = 10, Classes = { "wb-subtle" },
        });
        if (state == PortfolioState.Missing)
            left.Children.Add(new TextBlock { Text = W("PortfolioMissingHint"), FontSize = 10, TextWrapping = TextWrapping.Wrap, Classes = { "wb-error" } });
        else if (state == PortfolioState.Changed)
            left.Children.Add(new TextBlock { Text = W("PortfolioChangedHint"), FontSize = 10, TextWrapping = TextWrapping.Wrap, Classes = { "wb-warning" } });

        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        if (state == PortfolioState.Missing)
        {
            actions.Children.Add(ActionButton(W("PortfolioLocate"), () => _ = GuardAsync(async () =>
            {
                var files = await owner.StorageProvider.OpenFilePickerAsync(new()
                {
                    Title = W("PortfolioLocateTitle"), AllowMultiple = false,
                    FileTypeFilter = [new(W("KnxProjectFile")) { Patterns = ["*.knxproj"] }],
                });
                if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
                var result = WorkbenchPortfolio.Locate(registry, entry.Id, path);
                report(result.Status switch
                {
                    PortfolioActionStatus.Done or PortfolioActionStatus.Unchanged => W("PortfolioLocated", path),
                    PortfolioActionStatus.DuplicatePath => W("PortfolioDuplicate", registry.Find(result.EntryId ?? "")?.Name ?? ""),
                    _ => PortfolioActionError(result, registry),
                });
                render();
            }), "quiet"));
        }
        else
        {
            actions.Children.Add(ActionButton(W("Open"), () =>
            {
                // A modal portfolio must not own the project-replacement prompts; open after it closed.
                owner.Close();
                Dispatcher.UIThread.Post(() => _ = GuardAsync(async () =>
                {
                    if (await OpenPortfolioEntryAsync(entry.Id)) ViewModel.Status = W("PortfolioOpened", entry.Name);
                }));
            }, "primary"));
            actions.Children.Add(ActionButton(W("PortfolioRefresh"), () => _ = GuardAsync(async () =>
            {
                // Hashing a project runs on the thread pool; a late result must not overwrite
                // what a newer action already rendered.
                var version = gate.Begin();
                var result = await WorkbenchPortfolio.RefreshAsync(registry, entry.Id);
                if (!gate.IsCurrent(version)) return;
                report(result.Status switch
                {
                    PortfolioActionStatus.Updated => W("PortfolioRefreshed", registry.Find(entry.Id)?.Revision ?? entry.Revision),
                    PortfolioActionStatus.Unchanged => W("PortfolioUnchanged"),
                    _ => PortfolioActionError(result, registry),
                });
                render();
            }), "quiet"));
        }
        actions.Children.Add(ActionButton(W("Rename"), () => _ = GuardAsync(async () =>
        {
            var name = await PromptPortfolioNameAsync(owner, entry.Name);
            if (name is null) return;
            var result = WorkbenchPortfolio.Rename(registry, entry.Id, name);
            report(result.Ok ? W("PortfolioRenamed") : PortfolioActionError(result, registry));
            render();
        }), "quiet"));
        actions.Children.Add(ActionButton(W("PortfolioRemoveEntry"), () => _ = GuardAsync(async () =>
        {
            if (!await ConfirmPortfolioRemoveAsync(owner, entry.Name)) return;
            var result = WorkbenchPortfolio.Remove(registry, entry.Id);
            report(result.Ok ? W("PortfolioRemoved", entry.Name) : PortfolioActionError(result, registry));
            render();
        }), "quiet"));

        var grid = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(left);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        var row = new Border { Child = grid, Padding = new(0, 10), BorderThickness = new(0, 0, 0, 1) };
        row.Bind(Border.BorderBrushProperty, row.GetResourceObservable("WbLine"));
        ToolTip.SetTip(row, state switch
        {
            PortfolioState.Missing => W("PortfolioMissingHint"),
            PortfolioState.Changed => W("PortfolioChangedHint"),
            _ => entry.Path,
        });
        return row;
    }

    private string PortfolioActionError(PortfolioResult result, PortfolioRegistry registry) => result.Status switch
    {
        PortfolioActionStatus.EntryMissing => W("PortfolioEntryMissing"),
        PortfolioActionStatus.FileMissing => W("PortfolioFileMissingNow"),
        PortfolioActionStatus.ProjectNotSaved => W("PortfolioNoProject"),
        PortfolioActionStatus.InvalidName => W("PortfolioNameRequired"),
        PortfolioActionStatus.DuplicatePath => W("PortfolioDuplicate", registry.Find(result.EntryId ?? "")?.Name ?? ""),
        PortfolioActionStatus.AlreadyRegistered => W("PortfolioAlready"),
        _ => W("PortfolioFailed"),
    };

    /// <summary>Opens a portfolio entry through the normal project replacement path.</summary>
    internal async Task<bool> OpenPortfolioEntryAsync(string id)
    {
        var target = WorkbenchPortfolio.OpenTarget(Portfolio, id);
        if (target.Status != PortfolioActionStatus.Done || target.Path is not { } path)
        {
            ViewModel.Status = target.Status == PortfolioActionStatus.FileMissing
                ? W("PortfolioFileMissingNow") : W("PortfolioEntryMissing");
            return false;
        }
        if (!await OpenPathAsync(path)) return false;
        WorkbenchPortfolio.MarkOpened(Portfolio, id);
        return true;
    }

    private async Task<string?> PromptPortfolioNameAsync(Window owner, string current)
    {
        var dialog = CreateDialog(W("PortfolioRenameTitle"), 460, 240);
        var panel = new StackPanel { Margin = new(22, 22, 22, 14), Spacing = 10 };
        panel.Children.Add(Note(W("PortfolioRenamePrompt")));
        var box = new TextBox { Text = current, Classes = { "wb-field" } };
        AutomationProperties.SetName(box, W("PortfolioRenamePrompt"));
        panel.Children.Add(box);
        string? result = null;
        dialog.Content = DialogLayout(panel, DialogActions(
            DialogCancel(dialog),
            ActionButton(W("Rename"), () => { result = box.Text; dialog.Close(); }, "primary", isDefault: true)));
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await dialog.ShowDialog(owner);
        return result;
    }

    private async Task<bool> ConfirmPortfolioRemoveAsync(Window owner, string name)
    {
        var dialog = CreateDialog(W("PortfolioRemoveTitle"), 460, 250);
        var panel = new StackPanel { Margin = new(22, 22, 22, 14), Spacing = 10 };
        panel.Children.Add(Note(W("PortfolioRemoveText", name)));
        var confirmed = false;
        var remove = ActionButton(W("PortfolioRemoveEntry"), () => { confirmed = true; dialog.Close(); });
        // A destructive remove never takes the primary class: it is the danger action, last.
        remove.Classes.Add("wb-danger");
        dialog.Content = DialogLayout(panel, DialogActions(
            DialogCancel(dialog), remove));
        await dialog.ShowDialog(owner);
        return confirmed;
    }

    private async Task RestoreBackupAsync(Window dialog, ProjectBackupInfo backup)
    {
        if (!await ConfirmDialog.AskRestoreAsync(this)) return;
        var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = backup.Name + "-restored.knxproj", DefaultExtension = "knxproj" });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            await DrainRecoveryBeforeReplacementAsync();
            ProjectBackup.Restore(backup.Id, path);
            ViewModel.Status = W("RestoreDone", path);
            dialog.Close();
        }
        catch (Exception ex) { ViewModel.Status = W("BackupFailed", ex.Message); }
    }

    private void CreateBackup()
    {
        if (ViewModel.Project is not { } project) { ViewModel.Status = W("NoProjectLoaded"); return; }
        try
        {
            var info = ProjectBackup.Capture(project, ViewModel.ProjectPath.Length == 0 ? null : ViewModel.ProjectPath);
            ViewModel.Status = W("BackupCreated", Path.GetFileName(info.BackupPath));
        }
        catch (Exception ex) { ViewModel.Status = W("BackupFailed", ex.Message); }
    }

    private async Task ExportLabelsAsync()
    {
        var options = new LabelOptions { SheetTitle = ViewModel.Project?.Name ?? "Etiketten" };
        await ExportAsync("etiketten.html", p => LabelCreator.ToHtml(LabelCreator.GroupAddressLabels(p, options), options));
    }

    private async Task ExportSemanticAsync(SemanticExport.SemanticFormat format, string fileName, string extension)
    {
        if (ViewModel.Project is not { } project) { ViewModel.Status = W("NoProjectLoaded"); return; }
        var file = await StorageProvider.SaveFilePickerAsync(new() { SuggestedFileName = fileName, DefaultExtension = extension });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            SemanticExport.Export(project, path, format);
            ViewModel.Status = W("SemanticExported", Path.GetFileName(path));
        }
        catch (Exception ex) { ViewModel.Status = W("ExportFailed", ex.Message); }
    }

    private async Task CompareProjectsAsync()
    {
        if (ViewModel.Project is not { } project) { ViewModel.Status = W("NoProjectLoaded"); return; }
        var files = await StorageProvider.OpenFilePickerAsync(new() { AllowMultiple = false,
            Title = W("CompareProjects"), FileTypeFilter = [new("KNX") { Patterns = ["*.knxproj"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            var report = ProjectComparer.CompareRevisions(project, Knxproj.Load(path));
            var window = new CompareWindow();
            window.Load(report);
            await window.ShowDialog(this);
            ViewModel.Status = W("CompareSummary", report.AddedCount, report.RemovedCount, report.ChangedCount);
        }
        catch (Exception ex) { ViewModel.Status = W("CompareFailed", ex.Message); }
    }

    private async Task ReplaceProductAsync()
    {
        if (ViewModel.Project is null) { ViewModel.Status = W("NoProjectLoaded"); return; }
        var window = new ReplaceProductWindow(ViewModel.PreviewProductMigration, ViewModel.ApplyProductMigration);
        if (ViewModel.SelectedDevice is { } device && device.Device.ProductRefId.Length > 0)
            window.SetOldReference(device.Device.ProductRefId);
        if (await window.ShowDialog<bool>(this) == true)
        {
            ViewModel.Status = W("ReplaceDone");
            RefreshWorkspace();
        }
    }

    private async Task ResetDemoAsync()
    {
        if (!await ConfirmDialog.AskDemoResetAsync(this)) return;
        if (!await ConfirmDiscardAsync()) return;
        await DrainRecoveryBeforeReplacementAsync();
        ViewModel.CreateProject(Knx.Workbench.WorkbenchJson.CreateDemo(), null);
        await BindRecoveryCurrentProjectAsync();
        _parameterSession.DiscardDrafts();
        await ResetSessionsForReplacementAsync();
        _audit.Clear();
        Workspace.ChangeProject(ViewModel.Project!.Id);
        RefreshWorkspace();
        ViewModel.Status = W("DemoResetDone");
    }

    private async Task ImportDemoJsonAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new() { AllowMultiple = false,
            FileTypeFilter = [new(W("DemoProjectFile")) { Patterns = ["*.json"] }] });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            var imported = Knx.Workbench.WorkbenchJson.Import(await File.ReadAllTextAsync(path));
            await DrainRecoveryBeforeReplacementAsync();
            ViewModel.CreateProject(imported, null);
            await BindRecoveryCurrentProjectAsync();
            _parameterSession.DiscardDrafts();
            await ResetSessionsForReplacementAsync();
            RefreshWorkspace();
            ViewModel.Status = W("DemoImported");
        }
        catch (Exception ex)
        {
            ViewModel.Status = W("DemoImportFailedStatus", ex.Message);
            await ConfirmDialog.ShowMessageAsync(this, W("DemoImportFailed"), ex.Message);
        }
    }

    private void ShowToolsOverview()
    {
        var dialog = CreateDialog(W("ToolsOverview"), 620, 420);
        var panel = new StackPanel { Margin = new(22, 22, 22, 14), Spacing = 10 };
        void Add(string title, Action run) => panel.Children.Add(ActionButton(title, () => { dialog.Close(); run(); }, "quiet"));
        Add(W("Monitor"), () => ShowTool("monitor"));
        Add(W("ProgrammingTab"), () => ShowTool("programming"));
        Add(W("Scan"), () => ShowTool("scan"));
        Add(W("SidebarSearch"), () => ShowTool("search"));
        Add(W("Changes"), () => ShowTool("changes"));
        Add(W("Problems"), () => ShowTool("problems"));
        Add(W("Catalog"), ShowCatalog);
        Add(W("SecureOverview"), ShowSecureOverview);
        Add(W("HelpTip"), ShowHelp);
        dialog.Content = DialogLayout(new ScrollViewer { Content = panel },
            DialogActions(DialogCancel(dialog)));
        dialog.ShowDialog(this);
    }

    private void ShowSecureOverview()
    {
        if (ViewModel.Project is not { } project) { ViewModel.Status = W("NoProjectLoaded"); return; }
        var devices = ProjectQuery.DeviceList(project);
        var capable = devices.Where(d => project.Workbench.Devices.GetValueOrDefault(d.Id)?.SecureCapable == true).ToList();
        var unknown = devices.Count(d => project.Workbench.Devices.GetValueOrDefault(d.Id)?.SecureCapable is null);
        var secureGroups = project.Workbench.Groups.Count(g => g.Value.Secure == true);
        var lines = string.Join("\n", capable.Take(40).Select(d => $"{d.Address} · {d.Name}"));
        string diagnostics = "";
        try
        {
            if (_sessions?.BuildDiagnosticSummary() is { } summary)
                diagnostics = "\n\n" + W("ProgrammingNeed", summary.Programming.NeedsProgrammingCount) +
                    "\n" + W("InterfaceConflicts") + ": " + summary.Interfaces.Count(i => i.HasConflict);
        }
        catch (InvalidOperationException) { }
        _ = ConfirmDialog.ShowMessageAsync(this, W("SecureOverviewTitle"),
            W("SecureSummary", capable.Count, devices.Count(), unknown) + "\n" +
            W("SecureGroups", secureGroups) + "\n\n" +
            (lines.Length > 0 ? lines : W("SecureNoEvidence")) + diagnostics +
            "\n\n" + W("SecureBoundary"));
    }

    private void ShowPalette()
    {
        var dialog = CreateDialog(W("FindWorkItemOrCommand"), 660, 480);
        var panel = new DockPanel { Margin = new(22) };
        var query = new TextBox { PlaceholderText = W("PaletteWatermark"), Margin = new(0, 0, 0, 12), Classes = { "wb-field" } };
        var list = new ListBox { Focusable = true };
        Avalonia.Automation.AutomationProperties.SetName(query, W("FindWorkItemOrCommand"));
        Avalonia.Automation.AutomationProperties.SetName(list, W("SearchResults"));
        DockPanel.SetDock(query, Avalonia.Controls.Dock.Top); panel.Children.Add(query); panel.Children.Add(list); dialog.Content = panel;
        var candidates = new List<(string Label, Action Run)>();
        if (ViewModel.Project is { } p)
        {
            candidates.Add((p.Name, () => OpenTarget(new("project", p.Id))));
            foreach (var s in WorkbenchProjection.Spaces(p)) candidates.Add((s.Path, () => OpenTarget(new("room", s.Id))));
            foreach (var l in WorkbenchProjection.Lines(p)) candidates.Add((W("PaletteLine", l.Name), () => OpenTarget(new("line", l.Id))));
            foreach (var d in ProjectQuery.DeviceList(p)) candidates.Add(($"{d.Address} · {d.Name}", () => OpenTarget(new("device", d.Id))));
            foreach (var g in ProjectQuery.GroupAddresses(p)) candidates.Add(($"{g.Address} · {g.Name}", () => OpenTarget(new("ga", g.Id))));
        }
        candidates.AddRange([(W("Catalog"), ShowCatalog), (W("ProjectCheck"), () => ShowTool("problems")), (W("Monitor"), () => ShowTool("monitor")),
            (W("SmartTitle"), () => ShowSmartLinkDialog()),
            (W("MergeDevices"), () => _ = GuardAsync(() => ExportLabelMergeAsync(true))),
            (W("MergeGroups"), () => _ = GuardAsync(() => ExportLabelMergeAsync(false))),
            (W("ProgrammingTab"), () => ShowTool("programming")), (W("ResetLayout"), Workspace.ResetLayout),
            (W("OpenProject"), () => _ = GuardAsync(OpenProjectAsync)), (W("Help"), ShowHelp)]);
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
        query.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Down) { list.Focus(); if (list.SelectedIndex < 0 && list.ItemCount > 0) list.SelectedIndex = 0; e.Handled = true; }
        };
        list.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { Activate(); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Escape) { dialog.Close(); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        dialog.KeyDown += (_, e) => { if (!e.Handled && e.Key == Avalonia.Input.Key.Enter) { Activate(); e.Handled = true; } };
        Filter(); dialog.Opened += (_, _) => query.Focus(); dialog.ShowDialog(this);
    }

    private void SelectAllRows()
    {
        if (Workspace.ActiveTab is not { } tab) return;
        tab.SelectedObjectIds.Clear();
        if (ObjectTable.ItemsSource is not IEnumerable<WorkbenchRow> rows) return;
        _syncingSelection = true;
        try
        {
            ObjectTable.SelectedItems?.Clear();
            foreach (var row in rows)
            {
                tab.SelectedObjectIds.Add(row.Key);
                ObjectTable.SelectedItems?.Add(row);
            }
        }
        finally { _syncingSelection = false; }
        if (CurrentDevice() is { } device) ViewModel.SelectedDevice = device;
        UpdateSelectionState();
        RefreshInspector();
    }

    private void ClearSelection()
    {
        _syncingSelection = true;
        try
        {
            ObjectTable.SelectedItems?.Clear();
            if (Workspace.ActiveTab is { } tab) tab.SelectedObjectIds.Clear();
        }
        finally { _syncingSelection = false; }
        UpdateSelectionState();
        RefreshInspector();
    }

    private void ShowNewGroupAddressDialog()
    {
        if (ViewModel.Project is not { } project) { ViewModel.Status = W("NoProjectLoaded"); return; }
        var dialog = CreateDialog(W("NewGroupAddress"), 480, 440);
        var panel = new StackPanel { Margin = new(22, 22, 22, 14), Spacing = 10 };
        var address = new TextBox { Text = ProjectQuery.NextFreeGroupAddress(project, 1, 1).ToString(), Classes = { "wb-field" } };
        var name = new TextBox { Classes = { "wb-field" } };
        var dpt = new TextBox { Text = "DPST-1-1", Classes = { "wb-field" } };
        var rooms = project.Installations.SelectMany(i => i.Locations.AllNodes).Where(n => n.Kind == SpaceKind.Room).ToList();
        var room = new ComboBox { ItemsSource = rooms, PlaceholderText = W("RoomOptional"), Classes = { "wb-field" } };
        room.ItemTemplate = new FuncDataTemplate<SpaceNode>((r, _) => Label(r is null ? "" : r.Path));
        AutomationProperties.SetName(address, W("GroupAddress"));
        AutomationProperties.SetName(name, W("NameLabel"));
        AutomationProperties.SetName(dpt, W("DatapointType"));
        AutomationProperties.SetName(room, W("RoomOptional"));
        panel.Children.Add(Label(W("GroupAddress"))); panel.Children.Add(address);
        panel.Children.Add(Label(W("NameLabel"))); panel.Children.Add(name);
        panel.Children.Add(Label(W("DatapointType"))); panel.Children.Add(dpt);
        panel.Children.Add(Label(W("RoomOptional"))); panel.Children.Add(room);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "wb-error" } };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        var actions = DialogActions(
            DialogCancel(dialog),
            ActionButton(W("Create"), () =>
            {
                GroupAddress parsed;
                try { parsed = GroupAddress.Parse(address.Text ?? ""); }
                catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or OverflowException)
                { error.Text = W("InvalidGroupAddress"); address.Focus(); return; }
                if (parsed.Raw == 0) { error.Text = W("InvalidGroupAddress"); address.Focus(); return; }
                if (!ViewModel.CreateGroupAddress(parsed, name.Text ?? "", dpt.Text,
                        (room.SelectedItem as SpaceNode)?.Id)) { error.Text = ViewModel.Status; return; }
                dialog.Close();
                RefreshWorkspace();
                if (ProjectQuery.FindGroupAddress(ViewModel.Project!, parsed) is { } created)
                    OpenTarget(new("ga", created.Id));
            }, "primary", isDefault: true));
        dialog.Content = DialogLayout(panel, actions, error);
        dialog.Opened += (_, _) => name.Focus();
        dialog.ShowDialog(this);
    }

    /// <summary>
    /// Immutable batch snapshot taken when the dialog opens: later row/selection changes cannot
    /// add, remove or reorder batch members. Keys are validated again against the live project.
    /// </summary>
    internal static IReadOnlyList<BatchLinkItem> BatchSelection(Knx.Project project, WorkbenchTab tab) =>
        WorkbenchProjection.Rows(project, tab)
            .Where(r => r.Object is not null && tab.SelectedObjectIds.Contains(r.Key))
            .Select(r => new BatchLinkItem(r.Device.Id, r.Object!.Id, r.Name))
            .ToArray();

    private void ShowBatchLinkDialog()
    {
        if (ViewModel.Project is not { } project || Workspace.ActiveTab is not { } tab) return;
        ShowBatchLinkDialogForSelection(BatchSelection(project, tab));
    }

    private void ShowBatchLinkDialogForSelection(IReadOnlyList<BatchLinkItem> selection)
    {
        if (ViewModel.Project is not { } project) return;
        IReadOnlyList<BatchLinkItem> selected = Array.AsReadOnly(selection.ToArray());
        var session = ViewModel.ProjectSessionIdentity;
        if (selected.Count == 0) { ViewModel.Status = W("BatchNeedSelection"); return; }
        var groups = ProjectQuery.GroupAddresses(project).OrderBy(g => g.Address.Raw).ToList();
        var dialog = CreateDialog(W("BatchTitle", selected.Count), 720, 560);
        dialog.Name = "BatchLinkDialog";
        var top = new StackPanel { Margin = new(22, 22, 22, 14), Spacing = 8 };
        var search = new TextBox { Name = "BatchGroupSearch", Watermark = W("BatchSearch"), Classes = { "wb-field" } };
        AutomationProperties.SetName(search, W("BatchSearch"));
        var count = Note(W("GroupAddressCount", groups.Count)); count.Name = "BatchGroupCount";
        AutomationProperties.SetLiveSetting(count, AutomationLiveSetting.Polite);
        var picker = new ComboBox { Name = "BatchGroupPicker", ItemsSource = groups, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            PlaceholderText = W("ChooseGroupAddress"), Classes = { "wb-field" } };
        AutomationProperties.SetName(picker, W("ChooseGroupAddress"));
        picker.ItemTemplate = new FuncDataTemplate<GroupAddressEntry>((g, _) => Label(g is null ? "" : $"{g.Address} · {g.Name} · {g.DatapointType ?? W("Unknown")}"));
        var preview = new ListBox { Name = "BatchLinkPreview", Height = 230 };
        var errors = new TextBlock { Name = "BatchLinkError", FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(errors, AutomationLiveSetting.Polite);
        var add = ActionButton(W("LinkAction"), () => { }, "primary");
        add.Name = "BatchLinkApply";
        add.IsEnabled = false;
        top.Children.Add(Label(W("BatchIntro")));
        top.Children.Add(search);
        top.Children.Add(count);
        top.Children.Add(picker);
        top.Children.Add(preview);
        var actions = DialogActions(
            DialogCancel(dialog),
            add);
        dialog.Content = DialogLayout(top, actions, errors);
        dialog.Opened += (_, _) => search.Focus();
        bool IsCurrent() => ReferenceEquals(project, ViewModel.Project) && ReferenceEquals(session, ViewModel.ProjectSessionIdentity);
        void Validate()
        {
            if (!IsCurrent())
            {
                preview.ItemsSource = null; errors.Text = Strings.Get("LinkProjectChanged"); add.IsEnabled = false;
                errors.Classes.Set("wb-success", false); errors.Classes.Set("wb-error", true); return;
            }
            if (picker.SelectedItem is not GroupAddressEntry ga)
            {
                preview.ItemsSource = null; errors.Text = ""; add.IsEnabled = false;
                errors.Classes.Set("wb-success", false); errors.Classes.Set("wb-error", false); return;
            }
            var live = ViewModel.Project;
            var checks = WorkbenchProject.CheckLinks(live!, selected.Select(item => new LinkCheckRequest(new(item.DeviceId, item.ObjectId), ga.Id)));
            var results = selected.Select((item, i) => new LinkPreview(item,
                checks[i].Validation.Ok ? null : checks[i].Validation.Message, checks[i].AlreadyLinked)).ToList();
            preview.ItemsSource = results;
            preview.ItemTemplate = new FuncDataTemplate<LinkPreview>((p, _) => Label(
                p is null ? "" : $"{(p.AlreadyLinked ? W("AlreadyLinked") : p.Error is null ? W("Ready") : W("Incompatible"))} · {p.Item.Name}"
                + (p.Error is null ? "" : " — " + p.Error)));
            var invalid = results.Where(r => r.Error is not null).ToList();
            add.IsEnabled = invalid.Count == 0 && results.Any(r => !r.AlreadyLinked);
            errors.Text = invalid.Count == 0
                ? results.All(r => r.AlreadyLinked) ? W("BatchAllAlreadyLinked")
                : W("BatchCompatible")
                : W("BatchIncompatible", invalid.Count);
            errors.Classes.Set("wb-success", invalid.Count == 0);
            errors.Classes.Set("wb-error", invalid.Count != 0);
        }
        picker.SelectionChanged += (_, _) => Validate();
        search.TextChanged += (_, _) =>
        {
            var query = search.Text?.Trim() ?? "";
            var previous = picker.SelectedItem as GroupAddressEntry;
            var filtered = groups.Where(g => $"{g.Address} {g.Name} {g.DatapointType}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            picker.ItemsSource = filtered;
            picker.SelectedItem = previous is not null && filtered.Contains(previous) ? previous : null;
            count.Text = W("GroupAddressCount", filtered.Length);
            Validate();
        };
        search.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Down) return;
            picker.Focus(); picker.IsDropDownOpen = true; e.Handled = true;
        };
        add.Click += (_, _) =>
        {
            try
            {
                if (!IsCurrent()) throw new InvalidOperationException(Strings.Get("LinkProjectChanged"));
                if (ViewModel.HistoryBlocked?.Invoke() == true) throw new InvalidOperationException(W("SmartBusy"));
                if (picker.SelectedItem is not GroupAddressEntry ga) return;
                Validate();
                if (!add.IsEnabled) { ViewModel.Status = errors.Text ?? W("LinkImpossible"); return; }
                var requests = selected.Select(r => new LinkCheckRequest(new(r.DeviceId, r.ObjectId), ga.Id));
                var result = ViewModel.ApplyLinkPairs(W("EditLink", ga.Address), requests);
                if (!result.Ok) throw new InvalidOperationException(result.Errors.FirstOrDefault()?.Reason ?? W("LinkImpossible"));
                ViewModel.Status = W("LinksAdded", result.Changed, ga.Address);
                dialog.Close(); RefreshWorkspace();
            }
            catch (Exception ex)
            {
                ViewModel.Status = W("LinkFailed", ex.Message);
                errors.Classes.Set("wb-success", false);
                errors.Classes.Set("wb-error", true);
                errors.Text = ViewModel.Status;
            }
        };
        dialog.ShowDialog(this);
    }

    /// <summary>
    /// Opens a read-only counterview beside the main window. The work item stays a tab in the
    /// main window, so the companion never owns selection, filters or the docking layout.
    /// </summary>
    internal void OpenCompanion(WorkbenchTarget target)
    {
        if (ViewModel.Project is not { } project || target.Kind is not ("device" or "ga")) return;
        if (WorkbenchProjection.Resolve(project, target) is not { } model) return;
        if (_companions.FirstOrDefault(pair => pair.Key == target).Value is { } open && open.IsVisible)
        {
            open.Activate();
            return;
        }
        var window = new Window
        {
            Title = $"{TargetTitle(model, target)} · {W("CompanionWindow")}",
            Width = 460, Height = 560, MinWidth = 320, MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = BuildCompanion(target)
        };
        PlaceBesideOwner(window);
        EventHandler<string> refresh = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (window.IsVisible) window.Content = BuildCompanion(target);
        });
        ViewModel.ProjectEdited += refresh;
        _companions[target] = window;
        window.Closed += (_, _) =>
        {
            ViewModel.ProjectEdited -= refresh;
            _companions.Remove(target);
            _toolWindows.Remove(window);
        };
        _toolWindows.Add(window);
        window.Show(this);
    }

    private void PlaceBesideOwner(Window window)
    {
        var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
        var x = Position.X + (int)(Bounds.Width * scaling) + 12;
        var y = Position.Y + 48;
        if (Screens?.ScreenFromWindow(this) is { } screen)
        {
            var area = screen.WorkingArea;
            x = Math.Max(area.X + 12, Math.Min(x, area.Right - (int)(window.Width * scaling) - 12));
            y = Math.Max(area.Y + 12, Math.Min(y, area.Bottom - (int)(window.Height * scaling) - 12));
        }
        window.Position = new PixelPoint(x, y);
    }

    /// <summary>Read-only GA or device facts; every navigation goes through the main window.</summary>
    private Control BuildCompanion(WorkbenchTarget target)
    {
        var panel = new StackPanel { Margin = new(16), Spacing = 10 };
        if (ViewModel.Project is not { } project || WorkbenchProjection.Resolve(project, target) is not { } model)
        {
            panel.Children.Add(Note(W("CompanionEmpty")));
            return new ScrollViewer { Content = panel };
        }
        panel.Children.Add(Label(TargetTitle(model, target)));
        panel.Children.Add(ActionButton(W("RevealInMain"), () => { OpenTarget(target); Activate(); }, "quiet"));
        switch (model)
        {
            case GroupAddressEntry ga:
                panel.Children.Add(Section(W("SectionGeneral"),
                    Note(W("DatapointType") + ": " + (ga.DatapointType ?? W("Unknown"))),
                    Note(W("GroupAddress") + ": " + ga.Address)));
                var linked = ProjectQuery.ComObjectRefs(project).Where(r => r.Ref.Links.Contains(ga.Id)).ToList();
                var links = new StackPanel { Spacing = 4 };
                links.Children.Add(Note(W("GaLinkedCount", linked.Count)));
                if (linked.Count == 0) links.Children.Add(Note(W("GaNoLinks")));
                foreach (var group in linked.GroupBy(r => r.Device).Take(40))
                {
                    links.Children.Add(ActionButton(W("GaLinkedDevice", group.Key.Address.ToString(), group.Key.Name ?? ""),
                        () => { OpenTarget(new("device", group.Key.Id), "relations"); Activate(); }, "quiet"));
                    foreach (var (device, comObject) in group.Take(10))
                    {
                        var sending = WorkbenchProject.Metadata(project, new(device.Id, comObject.Id))?.SendingGroupId == ga.Id;
                        links.Children.Add(Note($"#{comObject.Number} · {comObject.Text ?? comObject.Id}" + (sending ? " · " + W("SendingBadge") : "")));
                    }
                }
                panel.Children.Add(Section(W("SectionLinks"), links));
                break;
            case DeviceInstance device:
                var line = ProjectQuery.LocateDevice(project, device.Address)?.Line;
                var room = project.Installations.SelectMany(i => i.Locations.AllNodes).FirstOrDefault(n => n.DeviceRefs.Contains(device.Id));
                var application = device.Hardware2ProgramRefId ?? project.Workbench.Devices.GetValueOrDefault(device.Id)?.Application ?? "-";
                panel.Children.Add(Section(W("SectionGeneral"),
                    Note(W("DeviceFacts", device.Address.ToString(), device.ProductRefId, application)),
                    Note(W("Line") + ": " + (line is null ? W("Unknown") : $"{line.Address} · {line.Name ?? W("Line")}")),
                    Note(W("RoomOptional") + ": " + (room?.Path ?? W("Unknown")))));
                var objects = ProjectQuery.ComObjectRefs(project).Where(r => r.Device.Id == device.Id).ToList();
                var objectRows = new StackPanel { Spacing = 2 };
                objectRows.Children.Add(Note(W("TableFootnote", objects.Count)));
                foreach (var (_, comObject) in objects.Take(200))
                {
                    var addresses = comObject.Links.Select(id => ProjectQuery.GroupAddresses(project).FirstOrDefault(g => g.Id == id))
                        .Where(g => g is not null).Select(g => g!.Address.ToString()).ToList();
                    objectRows.Children.Add(Note($"#{comObject.Number} · {comObject.Text ?? comObject.Id} · {(addresses.Count == 0 ? W("NoLinks") : string.Join("  ", addresses))}"));
                }
                panel.Children.Add(Section(W("SectionObject"), objectRows));
                break;
        }
        return new ScrollViewer { Content = panel };
    }

    private sealed record LinkPreview(BatchLinkItem Item, string? Error, bool AlreadyLinked);
    private sealed record PaletteEntry(string Label, Action Run);
}
