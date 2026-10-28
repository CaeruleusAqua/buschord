using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.Core.Catalog.Downloads;

namespace Knx.App;

/// <summary>User-initiated catalogue acquisition, with source-specific permission and local reports.</summary>
public sealed class CatalogDownloadsWindow : Window
{
    private readonly CatalogLibrary _library;
    private readonly CatalogDownloadService _service;
    private readonly bool _ownsService;
    private readonly IReadOnlyDictionary<string, string> _sourceNames;
    private readonly List<Action> _localized = [];
    private readonly TextBox _search = new() { Name = "ManufacturerSearch", Classes = { "wb-field" } };
    private readonly ListBox _manufacturers = new() { Name = "ManufacturerList", Classes = { "wb-list" } };
    private readonly ListBox _sources = new() { Name = "DownloadSourceList", SelectionMode = SelectionMode.Multiple, MaxHeight = 150, MinHeight = 70, Classes = { "wb-list" } };
    private readonly TextBlock _registrySummary = new() { Name = "RegistrySummary", TextWrapping = TextWrapping.Wrap, Classes = { "wb-muted" } };
    private readonly TextBlock _sourceDetails = new() { Name = "DownloadSourceDetails", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _sourceNotice = new() { Name = "DownloadSourceNotice", TextWrapping = TextWrapping.Wrap, Classes = { "wb-muted" } };
    private readonly TextBlock _sourceNoticeLabel;
    private readonly TextBlock _rights = new() { Name = "DownloadRightsNotice", TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock _sourceUrl = new() { Name = "DownloadSourceUrl", TextWrapping = TextWrapping.Wrap, Classes = { "wb-muted" } };
    private readonly CheckBox _accept = new() { Name = "DownloadPermissionCheck" };
    private readonly TextBox _evidence = new() { Name = "DownloadPermissionEvidence", Classes = { "wb-field" }, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 68, MaxLength = 4000 };
    private readonly StackPanel _evidencePanel = new() { Spacing = 6 };
    private readonly StackPanel _sourceBody = new() { Spacing = 10 };
    private readonly TextBlock _selectedCount = new() { Name = "DownloadSelectionSummary", TextWrapping = TextWrapping.Wrap, Classes = { "wb-muted" } };
    private readonly TextBlock _message = new() { Name = "DownloadMessage", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _progressText = new() { Name = "DownloadProgressText", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Name = "DownloadProgress", Height = 4, Minimum = 0, Maximum = 100 };
    private readonly ListBox _reports = new() { Name = "DownloadReports", MinHeight = 60, MaxHeight = 150, Classes = { "wb-list" } };
    private readonly Button _refresh;
    private readonly Button _website;
    private readonly Button _openSource;
    private readonly Button _openTerms;
    private readonly Button _saveConsent;
    private readonly Button _revokeConsent;
    private readonly Button _downloadSelected;
    private readonly Button _downloadAll;
    private readonly Button _cancel;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ManufacturerRegistrySnapshot? _registry;
    private IReadOnlyList<CatalogDownloadBatchReport> _reportHistory = [];
    private IReadOnlyList<ManufacturerRow> _rows = [];
    private CancellationTokenSource? _operation;
    private bool _binding;
    private bool _busy;
    private bool _closeAfterCancel;
    private bool _closed;
    private bool _opened;
    private bool _updatingLanguage;
    private bool _serviceDisposed;
    private string? _permissionSourceId;

    public CatalogDownloadsWindow(CatalogLibrary library, CatalogDownloadService? service = null)
    {
        _library = library;
        _ownsService = service is null;
        _service = service ?? new CatalogDownloadService(library.Store);
        _sourceNames = _service.Sources.ToDictionary(source => source.Id, source => source.Name, StringComparer.Ordinal);
        Width = 1080; Height = 800; MinWidth = 680; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("wb-dialog");
        _manufacturers.ItemTemplate = new FuncDataTemplate<ManufacturerRow>((row, _) => row is null ? null : new TextBlock
        {
            Text = row.ToString(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4),
        });
        _sources.ItemTemplate = new FuncDataTemplate<SourceRow>((row, _) => row is null ? null : new TextBlock
        {
            Text = row.ToString(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4),
        });
        _reports.ItemTemplate = new FuncDataTemplate<string>((row, _) => new TextBlock { Text = row, TextWrapping = TextWrapping.Wrap });
        ScrollViewer.SetHorizontalScrollBarVisibility(_manufacturers, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_sources, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_reports, ScrollBarVisibility.Disabled);
        _localized.Add(() => Title = T("Title"));
        _localized.Add(() =>
        {
            _search.PlaceholderText = T("Search");
            AutomationProperties.SetName(_search, T("Search"));
            AutomationProperties.SetName(_manufacturers, T("Manufacturers"));
            AutomationProperties.SetName(_sources, T("Sources"));
            AutomationProperties.SetName(_evidence, T("Evidence"));
            AutomationProperties.SetName(_reports, T("Report"));
        });

        _refresh = Button("RefreshManufacturerRegistry", "RefreshRegistry", async () => await RefreshRegistryAsync());
        _sourceNoticeLabel = Text("SourceNoteOriginal", "wb-muted");
        _website = Button("OpenManufacturerWebsite", "ManufacturerWebsite", async () => await OpenUriAsync(SelectedManufacturer?.Website));
        _openSource = Button("OpenDownloadSource", "OfficialSource", async () => await OpenUriAsync(SelectedSource?.SourcePage));
        _openTerms = Button("OpenDownloadTerms", "SourceConditions", async () => await OpenUriAsync(SelectedSource?.TermsUri));
        _saveConsent = Button("SaveDownloadPermission", "SaveConsent", SaveConsentAsync);
        _revokeConsent = Button("RevokeDownloadPermission", "RevokeConsent", RevokeConsentAsync);
        _downloadSelected = Button("DownloadSelectedSources", "DownloadSelected", () => DownloadAsync(SelectedSources.Select(source => source.Id).ToArray()));
        _downloadSelected.Classes.Add("wb-primary");
        _downloadAll = Button("DownloadAuthorizedSources", "DownloadAuthorized", () => DownloadAsync(_service.Sources.Where(IsAuthorized).Select(source => source.Id).ToArray()));
        _cancel = Button("CancelCatalogueDownload", "", () => { CancelOperation(); return Task.CompletedTask; });
        _localized.Add(() => _cancel.Content = Strings.Get("BtnCancel"));
        var close = Button("CloseCatalogueDownloads", "", () => { Close(); return Task.CompletedTask; });
        close.IsCancel = true;
        _localized.Add(() => close.Content = Strings.Get("BtnClose"));

        var registryToolbar = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        registryToolbar.Children.Add(_search);
        Grid.SetColumn(_refresh, 1); registryToolbar.Children.Add(_refresh);
        var header = new StackPanel { Spacing = 8, Margin = new Thickness(18, 12, 18, 12), Children = { Text("Intro"), registryToolbar, _registrySummary } };

        var manufacturerPanel = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 8 };
        manufacturerPanel.Children.Add(Text("Manufacturers", "wb-label"));
        Grid.SetRow(_manufacturers, 1); manufacturerPanel.Children.Add(_manufacturers);
        Grid.SetRow(_website, 2); manufacturerPanel.Children.Add(_website);

        _evidencePanel.Children.Add(Text("Evidence", "wb-label"));
        _evidencePanel.Children.Add(_evidence);
        _evidencePanel.Children.Add(Text("EvidenceHint", "wb-muted"));
        var consentActions = new WrapPanel { Orientation = Orientation.Horizontal };
        AddSpaced(consentActions, _saveConsent, _revokeConsent);
        var links = new WrapPanel { Orientation = Orientation.Horizontal };
        AddSpaced(links, _openSource, _openTerms);
        _sourceBody.Children.Add(_sourceDetails);
        _sourceBody.Children.Add(_sourceUrl);
        _sourceBody.Children.Add(links);
        _sourceBody.Children.Add(_sourceNoticeLabel);
        _sourceBody.Children.Add(_sourceNotice);
        _sourceBody.Children.Add(_rights);
        _sourceBody.Children.Add(_accept);
        _sourceBody.Children.Add(_evidencePanel);
        _sourceBody.Children.Add(consentActions);

        var sourcePanel = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 8 };
        sourcePanel.Children.Add(Text("Sources", "wb-label"));
        Grid.SetRow(_sources, 1); sourcePanel.Children.Add(_sources);
        var detailScroll = new ScrollViewer { Content = _sourceBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(detailScroll, 2); sourcePanel.Children.Add(detailScroll);
        var body = new Grid { ColumnDefinitions = new("240,*"), ColumnSpacing = 18, Margin = new Thickness(18, 0, 18, 10) };
        body.Children.Add(manufacturerPanel);
        Grid.SetColumn(sourcePanel, 1); body.Children.Add(sourcePanel);

        var reportPanel = new StackPanel { Spacing = 6, Children = { Text("ConflictHint", "wb-muted"), _reports } };
        var reportExpander = new Expander { Name = "DownloadReportExpander", Content = reportPanel, Margin = new Thickness(18, 0, 18, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        _localized.Add(() => reportExpander.Header = T("Report"));
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        AddSpaced(actions, _downloadSelected, _downloadAll, _cancel, close);
        var footer = new Border
        {
            Classes = { "wb-dialog-footer" },
            Child = new StackPanel { Spacing = 7, Children = { _selectedCount, _message, _progressText, _progress, actions } }
        };
        var root = new Grid { RowDefinitions = new("Auto,*,Auto,Auto") };
        root.Children.Add(header);
        Grid.SetRow(body, 1); root.Children.Add(body);
        Grid.SetRow(reportExpander, 2); root.Children.Add(reportExpander);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root;
        _search.TextChanged += (_, _) => RefreshManufacturers();
        _manufacturers.SelectionChanged += (_, _) => { if (!_binding) RefreshSources(); };
        _sources.SelectionChanged += (_, _) => { if (!_binding) ShowSource(); };
        _accept.IsCheckedChanged += (_, _) => UpdateActions();
        _evidence.TextChanged += (_, _) => UpdateActions();
        KeyDown += (_, args) =>
        {
            if (!args.Handled && args.Key == Key.Escape) { Close(); args.Handled = true; }
        };
        Closing += (_, args) =>
        {
            if (!_busy) return;
            args.Cancel = true;
            _closeAfterCancel = true;
            CancelOperation();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            Strings.Instance.PropertyChanged -= OnLanguageChanged;
            _operation?.Cancel();
            DisposeOwnedServiceWhenIdle();
        };
        Opened += async (_, _) =>
        {
            _opened = true;
            FitWorkingArea();
            await LoadLocalAsync();
            if (!_closed) _search.Focus();
        };
        ScalingChanged += (_, _) => { if (_opened) FitWorkingArea(); };
        Strings.Instance.PropertyChanged += OnLanguageChanged;
        ApplyLanguage();
        WorkbenchDialogChrome.Apply(this);
    }

    public Task Ready => _ready.Task;
    /// <summary>A cancelled view waiter does not cancel the shared library rebuild.</summary>
    public bool LibraryReloadContinues { get; private set; }
    public Task<CatalogSnapshot>? LibraryReloadTask { get; private set; }
    private static string T(string key, params object[] args) => CatalogDownloadStrings.Get(key, args);
    private ManufacturerRow? SelectedManufacturer => _manufacturers.SelectedItem as ManufacturerRow;
    private CatalogDownloadSource? SelectedSource => (_sources.SelectedItem as SourceRow)?.Source;
    private IReadOnlyList<CatalogDownloadSource> SelectedSources => _sources.SelectedItems?.OfType<SourceRow>().Select(row => row.Source).ToArray() ?? [];
    private bool IsAuthorized(CatalogDownloadSource source) => _service.GetAuthorization(source) is CatalogSourceAuthorization.ManufacturerTerms or CatalogSourceAuthorization.UserDeclaredAuthorization;

    private TextBlock Text(string key, string? classes = null)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (classes is not null) text.Classes.Add(classes);
        _localized.Add(() => text.Text = T(key));
        return text;
    }

    private Button Button(string name, string key, Func<Task> action)
    {
        var button = new Button { Name = name, Classes = { "wb-button" } };
        if (key.Length > 0) _localized.Add(() => button.Content = T(key));
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception error) { if (!_closed) ShowError(error); }
        };
        return button;
    }

    private static void AddSpaced(Panel panel, params Control[] children)
    {
        foreach (var child in children) { child.Margin = new Thickness(0, 0, 6, 4); panel.Children.Add(child); }
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != "Item[]") return;
        if (Dispatcher.UIThread.CheckAccess()) ApplyLanguage();
        else Dispatcher.UIThread.Post(ApplyLanguage);
    }

    private void ApplyLanguage()
    {
        if (_closed || _updatingLanguage) return;
        _updatingLanguage = true;
        try
        {
            foreach (var setter in _localized) setter();
            RefreshManufacturers();
            ShowReports();
        }
        finally { _updatingLanguage = false; }
    }

    private async Task LoadLocalAsync()
    {
        try
        {
            var local = await Task.Run(() => (_service.Registry.Load(), _service.LoadReports()));
            if (_closed) return;
            _registry = local.Item1;
            _reportHistory = local.Item2;
            RefreshManufacturers();
            ShowReports();
        }
        catch (Exception error) { if (!_closed) ShowError(error); }
        finally { _ready.TrySetResult(); }
    }

    private void RefreshManufacturers()
    {
        if (_closed) return;
        var previous = SelectedManufacturer?.Key;
        var rows = new List<ManufacturerRow>();
        var profiles = _service.Sources.GroupBy(source => source.ManufacturerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, index) in (_registry?.Entries ?? []).Select((entry, index) => (entry, index)))
        {
            var key = entry.ManufacturerId is { Length: > 0 } id ? "code:" + id : "directory:" + index;
            var sources = entry.ManufacturerId is { } manufacturerId && profiles.TryGetValue(manufacturerId, out var linked) ? linked : [];
            if (sources.Length > 0) matched.Add(entry.ManufacturerId!);
            rows.Add(new ManufacturerRow(key, entry.Name, entry.ManufacturerId, ParsePublicUri(entry.Website) ?? sources.FirstOrDefault()?.SourcePage, sources, entry.MemberStatus));
        }
        foreach (var profile in profiles.Where(pair => !matched.Contains(pair.Key)))
        {
            var first = profile.Value[0];
            rows.Add(new ManufacturerRow("profile:" + profile.Key, string.IsNullOrWhiteSpace(first.ManufacturerName) ? first.ManufacturerId : first.ManufacturerName,
                first.ManufacturerId, first.SourcePage, profile.Value, null));
        }
        var query = _search.Text?.Trim() ?? "";
        _rows = rows.Where(row => query.Length == 0 || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.ManufacturerId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                || row.Sources.Any(source => source.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
        _binding = true;
        try
        {
            _manufacturers.ItemsSource = _rows;
            _manufacturers.SelectedItem = _rows.FirstOrDefault(row => row.Key == previous) ?? _rows.FirstOrDefault(row => row.Sources.Count > 0) ?? _rows.FirstOrDefault();
        }
        finally { _binding = false; }
        _registrySummary.Text = _registry is { } snapshot
            ? T("RegistrySummary", snapshot.RetrievedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), snapshot.DirectoryCount, snapshot.CodeCount)
            : T("RegistryEmpty");
        RefreshSources();
    }

    private void RefreshSources()
    {
        var selected = SelectedSources.Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
        var rows = SelectedManufacturer?.Sources.Select(source => new SourceRow(source, T("Auth" + _service.GetAuthorization(source)))).ToArray() ?? [];
        _binding = true;
        try
        {
            _sources.ItemsSource = rows;
            _sources.SelectedItems?.Clear();
            foreach (var row in rows.Where(row => selected.Contains(row.Source.Id))) _sources.SelectedItems?.Add(row);
            if (_sources.SelectedItems?.Count == 0 && rows.Length > 0) _sources.SelectedItem = rows[0];
        }
        finally { _binding = false; }
        _website.IsEnabled = !_busy && SelectedManufacturer?.Website is not null;
        ShowSource();
    }

    private void ShowSource()
    {
        var source = SelectedSource;
        _sourceDetails.Text = source is null ? T(SelectedManufacturer?.Sources.Count == 0 ? "NoSource" : "SelectSource")
            : T("SourceDetails", source.Name, source.Formats, source.VerifiedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture), source.TermsRevision);
        _sourceUrl.Text = source?.DownloadUri?.AbsoluteUri ?? source?.SourcePage.AbsoluteUri ?? "";
        _sourceNotice.Text = source?.Notice ?? "";
        _sourceNoticeLabel.IsVisible = !string.IsNullOrWhiteSpace(source?.Notice);
        _rights.Text = source is null ? "" : T(source.Rights switch
        {
            CatalogSourceRights.Restricted => "RestrictedRights",
            CatalogSourceRights.ManufacturerGranted => "ManufacturerRights",
            _ => "UnknownRights",
        });
        var consent = source is null ? null : _service.GetConsent(source.Id);
        var own = source?.Rights == CatalogSourceRights.Unknown;
        _accept.Content = new TextBlock { Text = T(own ? "OwnAuthorization" : "AcceptTerms"), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(_accept, T(own ? "OwnAuthorization" : "AcceptTerms"));
        _accept.IsVisible = source is not null;
        _evidencePanel.IsVisible = source is not null && own;
        if (_permissionSourceId != source?.Id)
        {
            _permissionSourceId = source?.Id;
            _accept.IsChecked = consent is not null && IsAuthorized(source!);
            _evidence.Text = consent?.EvidenceNote ?? "";
        }
        UpdateActions();
    }

    private void UpdateActions()
    {
        var source = SelectedSource;
        var eligible = source is { CanDownload: true } && source.Rights != CatalogSourceRights.Restricted;
        var selected = SelectedSources;
        var authorized = _service.Sources.Count(IsAuthorized);
        _accept.IsEnabled = !_busy && eligible;
        _evidence.IsEnabled = !_busy && eligible;
        _saveConsent.IsEnabled = !_busy && eligible && _accept.IsChecked == true
            && (source!.Rights != CatalogSourceRights.Unknown || (_evidence.Text?.Trim().Length ?? 0) >= 20);
        _revokeConsent.IsEnabled = !_busy && source is not null && _service.GetConsent(source.Id) is not null;
        _openSource.IsEnabled = !_busy && source is not null;
        _openTerms.IsEnabled = !_busy && source?.TermsUri is not null;
        _downloadSelected.IsEnabled = !_busy && selected.Count > 0 && selected.All(IsAuthorized);
        _downloadAll.IsEnabled = !_busy && authorized > 0;
        _selectedCount.Text = T("AuthorizedCount", selected.Count, authorized);
        _refresh.IsEnabled = !_busy;
        _search.IsEnabled = !_busy;
        _manufacturers.IsEnabled = !_busy;
        _sources.IsEnabled = !_busy;
        _website.IsEnabled = !_busy && SelectedManufacturer?.Website is not null;
        _cancel.IsVisible = _busy;
        _cancel.IsEnabled = _busy && _operation?.IsCancellationRequested != true;
        _progress.IsVisible = _busy;
        _progressText.IsVisible = _busy;
    }

    private async Task SaveConsentAsync()
    {
        if (_busy || SelectedSource is not { } source || !_saveConsent.IsEnabled) return;
        var note = _evidence.Text?.Trim() ?? "";
        await RunOperationAsync(async token =>
        {
            await _service.RecordConsentAsync(source.Id, source.Rights == CatalogSourceRights.ManufacturerGranted
                    ? CatalogConsentKind.ManufacturerTerms : CatalogConsentKind.UserDeclaredAuthorization, note, token);
            if (_closed) return;
            _permissionSourceId = null;
            RefreshSources();
            _message.Text = T("ConsentSaved");
        });
    }

    private async Task RevokeConsentAsync()
    {
        if (_busy || SelectedSource is not { } source) return;
        await RunOperationAsync(async token =>
        {
            await _service.RevokeConsentAsync(source.Id, token);
            if (_closed) return;
            _permissionSourceId = null;
            RefreshSources();
            _message.Text = T("ConsentRevoked");
        });
    }

    private async Task RefreshRegistryAsync()
    {
        if (_busy || !await ConfirmRegistryAsync()) return;
        await RunOperationAsync(async token =>
        {
            var progress = new Progress<ManufacturerRegistryProgress>(item =>
            {
                if (!_closed) _progressText.Text = T("RegistryProgress", item.CompletedPages, item.TotalPages, item.Entries);
            });
            _registry = await _service.Registry.RefreshAsync(true, progress, token);
            if (!_closed) RefreshManufacturers();
        });
    }

    private async Task<bool> ConfirmRegistryAsync()
    {
        var dialog = new Window { Title = T("RegistryConsent"), Width = 620, Height = 400, MinWidth = 400, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner, Classes = { "wb-dialog" } };
        var acceptance = new CheckBox
        {
            Name = "RegistryPermissionCheck",
            Content = new TextBlock { Text = T("RegistryAccept"), TextWrapping = TextWrapping.Wrap },
        };
        AutomationProperties.SetName(acceptance, T("RegistryAccept"));
        var fetch = new Button { Name = "ConfirmRegistryFetch", Content = T("RegistryFetch"), IsEnabled = false, IsDefault = true, Classes = { "wb-button", "wb-primary" } };
        fetch.Click += (_, _) => dialog.Close(true);
        acceptance.IsCheckedChanged += (_, _) => fetch.IsEnabled = acceptance.IsChecked == true;
        var cancel = new Button { Content = Strings.Get("BtnCancel"), IsCancel = true, Classes = { "wb-button" } };
        cancel.Click += (_, _) => dialog.Close(false);
        var links = new WrapPanel();
        foreach (var uri in new[] { new Uri("https://www.knx.org/community/manufacturers"), new Uri("https://support.knx.org/hc/en-us/article_attachments/17389755651474") })
        {
            var link = new Button { Content = uri.Host, Classes = { "wb-button" }, Margin = new Thickness(0, 0, 6, 4) };
            link.Click += async (_, _) => await OpenUriAsync(uri);
            links.Children.Add(link);
        }
        var form = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children = { new TextBlock { Text = T("RegistryNotice"), TextWrapping = TextWrapping.Wrap }, links, acceptance } };
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        AddSpaced(footer, cancel, fetch);
        var grid = new Grid { RowDefinitions = new("*,Auto") };
        grid.Children.Add(new ScrollViewer { Content = form, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var footerBorder = new Border { Classes = { "wb-dialog-footer" }, Child = footer };
        Grid.SetRow(footerBorder, 1); grid.Children.Add(footerBorder);
        dialog.Content = grid;
        dialog.Opened += (_, _) => acceptance.Focus();
        WorkbenchDialogChrome.Apply(dialog);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task DownloadAsync(IReadOnlyList<string> ids)
    {
        if (_busy || ids.Count == 0) return;
        await RunOperationAsync(async token =>
        {
            var progress = new Progress<CatalogDownloadProgress>(ShowProgress);
            var report = await _service.DownloadAsync(ids, progress, token);
            _reportHistory = new[] { report }.Concat(_reportHistory.Where(item => item.Id != report.Id)).ToArray();
            if (!_closed)
            {
                ShowReports();
                _message.Text = Summary(report) + (report.PersistenceWarning is { } warning
                    ? Environment.NewLine + T("ReportPersistenceWarning", warning) : "");
            }
            if (report.CatalogsAdded > 0)
            {
                // Start the shared rebuild even when acquisition was just cancelled after
                // an import. Only this window's wait is cancelled; other panes still get it.
                var reload = _library.ReloadAsync();
                LibraryReloadTask = reload;
                try { await reload.WaitAsync(token); LibraryReloadContinues = false; }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    LibraryReloadContinues = true;
                    if (!_closed) _message.Text += Environment.NewLine + T("LibraryReloadContinues");
                    _ = ObserveLibraryReloadAsync(reload);
                }
                catch (Exception error) { if (!_closed) _message.Text = T("LibraryRefreshFailed", error.Message); }
            }
        });
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true;
        _operation = new CancellationTokenSource();
        _progress.IsIndeterminate = true;
        _progressText.Text = "";
        _message.Text = "";
        UpdateActions();
        try { await action(_operation.Token); }
        catch (OperationCanceledException) when (_operation.IsCancellationRequested) { if (!_closed) _message.Text = T("CancelPending"); }
        catch (Exception error) { if (!_closed) ShowError(error); }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _busy = false;
            if (_closed) DisposeOwnedServiceWhenIdle();
            if (!_closed)
            {
                UpdateActions();
                if (_closeAfterCancel) Close();
            }
        }
    }

    private void ShowProgress(CatalogDownloadProgress item)
    {
        if (_closed || !_busy) return;
        var source = _sourceNames.GetValueOrDefault(item.SourceId) ?? item.SourceId;
        _progressText.Text = T("Progress", source, T("Phase" + item.Phase), MiB(item.BytesReceived),
            item.TotalBytes is > 0 ? T("TotalBytes", MiB(item.TotalBytes.Value)) : "", item.CompletedSources, item.TotalSources);
        _progress.IsIndeterminate = item.TotalBytes is not > 0 || item.Phase != CatalogDownloadPhase.Downloading;
        if (!_progress.IsIndeterminate) _progress.Value = Math.Clamp(100.0 * item.BytesReceived / item.TotalBytes!.Value, 0, 100);
    }

    private async Task ObserveLibraryReloadAsync(Task<CatalogSnapshot> reload)
    {
        try { await reload; }
        catch (Exception error) { if (!_closed) _message.Text = T("LibraryRefreshFailed", error.Message); }
    }

    private void ShowReports()
    {
        var rows = _reportHistory.OrderByDescending(report => report.StartedAt).Take(20).SelectMany(report =>
            new[] { Summary(report) }
                .Concat(report.PersistenceWarning is { } warning ? new[] { T("ReportPersistenceWarning", warning) } : [])
                .Concat(report.Items.Take(200).Select(item =>
                $"  {_sourceNames.GetValueOrDefault(item.SourceId) ?? item.SourceId} · {T("Status" + item.Status)} · {item.File} · {item.Detail}"))
                .Concat(report.Items.Count > 200 ? new[] { T("MoreReportItems", report.Items.Count - 200, Path.Combine(_service.DataDirectory, "reports")) } : []));
        _reports.ItemsSource = _reportHistory.Count == 0 ? new[] { T("NoReports") } : rows.ToArray();
    }

    private static string Summary(CatalogDownloadBatchReport report) => T("ReportSummary",
        report.FinishedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), report.CatalogsAdded,
        report.Items.Count, MiB(report.BytesReceived), report.Cancelled ? T("CancelledSuffix") : "");
    private static string MiB(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture);
    private void ShowError(Exception error) => _message.Text = T("OperationFailed", error.Message);

    private void CancelOperation()
    {
        _operation?.Cancel();
        _message.Text = T("CancelPending");
        UpdateActions();
    }

    private void DisposeOwnedServiceWhenIdle()
    {
        // A forced owner shutdown can bypass the normal Closing delay. The observed
        // operation still completes its cancellation/report path before resources leave.
        if (!_ownsService || _busy || _serviceDisposed) return;
        _serviceDisposed = true;
        _service.Dispose();
    }

    private async Task OpenUriAsync(Uri? uri)
    {
        if (uri is null || ParsePublicUri(uri.AbsoluteUri) is null) return;
        try { await Task.Run(() => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })); }
        catch (Exception error) { if (!_closed) ShowError(error); }
    }

    private static Uri? ParsePublicUri(string? text) => Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;

    private void FitWorkingArea()
    {
        if (Screens?.ScreenFromWindow(this) is not { } screen || !double.IsFinite(RenderScaling) || RenderScaling <= 0) return;
        var width = Math.Max(1, Math.Floor(screen.WorkingArea.Width / RenderScaling - 20));
        var height = Math.Max(1, Math.Floor(screen.WorkingArea.Height / RenderScaling - 32));
        MinWidth = Math.Min(680, width); MinHeight = Math.Min(420, height);
        MaxWidth = width; MaxHeight = height;
        Width = Math.Clamp(Width, MinWidth, width); Height = Math.Clamp(Height, MinHeight, height);
    }

    private sealed record ManufacturerRow(string Key, string Name, string? ManufacturerId, Uri? Website, IReadOnlyList<CatalogDownloadSource> Sources, string? MemberStatus)
    {
        public override string ToString() => string.Join(" · ", new[] { Name, ManufacturerId, MemberStatus }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }
    private sealed record SourceRow(CatalogDownloadSource Source, string Authorization)
    {
        public override string ToString() => $"{Source.Name} · {Authorization}";
    }
}
