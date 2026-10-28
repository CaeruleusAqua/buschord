using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Knx;
using Knx.App.Services;
using Knx.Catalog;

namespace Knx.App.Views;

/// <summary>
/// Product catalogue browser: loads .knxprod files from the local library, shows products
/// and inserts a device (with its communication objects) into the open project.
/// Embedded as a tab of the Workbench.
/// </summary>
public partial class CatalogPane : UserControl
{
    private readonly MainWindowViewModel _vm;
    private readonly CatalogLibrary _library;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<KnxProductCatalog> _catalogs = [];
    private CatalogProduct? _shownProduct;
    private string? _suggestion;
    private string? _suggestedName;
    private string _message = "";
    private Line? _preferredLine;
    private string? _preferredRoomId;
    private CancellationTokenSource? _operation;
    private bool _watchingBackground;
    private bool _busy;
    private int _filterGeneration;
    private CancellationTokenSource? _filterOperation;
    private bool _bindScheduled;
    private CatalogSnapshot? _latestSnapshot;

    public CatalogPane() : this(new MainWindowViewModel()) { }

    public CatalogPane(MainWindowViewModel vm, CatalogLibrary? library = null)
    {
        _vm = vm;
        _library = library ?? CatalogLibrary.Shared;
        InitializeComponent();
        DownloadCatalogsButton.Content = Localization.CatalogDownloadStrings.Get("Title");
        RemoveCatalogButton.Content = CatalogUiText.Get("RemoveCatalog");
        ToolTip.SetTip(RemoveCatalogButton, CatalogUiText.Get("RemoveCatalogTip"));
        SegmentCaption.Text = CatalogUiText.Get("TargetSegment");
        Loaded += OnLoaded;
        DetachedFromVisualTree += (_, _) =>
        {
            UnwatchBackground();
            _filterOperation?.Cancel();
        };
        FillLines();
        UpdateRoomContext();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        ApplyResponsiveLayout();
    }

    /// <summary>At 900 DIP the three fixed columns leave no room for the product name.</summary>
    private void ApplyResponsiveLayout()
    {
        if (CatalogRoot?.ColumnDefinitions is not { Count: 3 } columns) return;
        var narrow = Bounds.Width is > 0 and < 1000;
        columns[0].Width = new GridLength(narrow ? 150 : 200);
        columns[2].Width = new GridLength(narrow ? 220 : 290);
    }

    /// <summary>Completes once the catalogue data is bound (or the load failed); used by screenshots.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Line preselected as insert target, e.g. by the caller that opened the catalogue.</summary>
    public Line? PreferredLine
    {
        get => _preferredLine;
        set
        {
            if (ReferenceEquals(_preferredLine, value)) return;
            _preferredLine = value;
            // Set right after construction in practice: refresh the target line selection.
            if (_vm.Project is not null) FillLines();
        }
    }

    /// <summary>Room that receives a newly inserted device; null inserts only into the line.</summary>
    public string? PreferredRoomId
    {
        get => _preferredRoomId;
        set
        {
            if (_preferredRoomId == value) return;
            _preferredRoomId = value;
            UpdateRoomContext();
        }
    }

    /// <summary>Moves keyboard focus to the product search, for a workspace shortcut.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Re-reads target lines and the address suggestion after the project changed (load, undo, redo).</summary>
    public void ReloadProject()
    {
        // A preferred line belongs to the previous project instance and must not survive.
        _preferredLine = null;
        AddressBox.Text = "";
        FillLines();
    }

    private KnxProductCatalog? SelectedCatalog => CatalogList.SelectedItem as KnxProductCatalog;
    private CatalogProduct? SelectedProduct => ProductList.SelectedItem as CatalogProduct;

    /// <summary>A standalone catalogue window closes itself; embedded in the workspace it hides the button.</summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        CloseButton.IsVisible = TopLevel.GetTopLevel(this) is Window window
            && ReferenceEquals(window.Content, this);
        // Loading starts with the first visibility, never in the constructor: start-up stays I/O free.
        _ = LoadAsync(reload: false);
    }

    /// <summary>
    /// Shows the library while it is read; the thread-safe service does the parsing off the UI
    /// thread. Cancel abandons only this waiter: the shared parse finishes in the background
    /// and the pane rebinds through <see cref="CatalogLibrary.Changed"/>.
    /// </summary>
    private async Task LoadAsync(bool reload)
    {
        if (_busy) return;
        _busy = true;
        var operation = new CancellationTokenSource();
        _operation = operation;
        SetLoading(true);
        // Progressive snapshots keep the list usable while the index fills in; the pane binds
        // them as soon as they arrive and replaces them with the stable snapshot at the end.
        _library.SnapshotPublished += OnSnapshotPublished;
        try
        {
            var progress = new Progress<CatalogProgress>(ShowProgress);
            var snapshot = reload
                ? await _library.ReloadAsync(progress, operation.Token)
                : await _library.GetAsync(progress, operation.Token);
            Bind(snapshot);
        }
        catch (OperationCanceledException)
        {
            WatchBackgroundLoad();
            SetMessage(CatalogUiText.Get("LoadCancelled"));
        }
        catch (Exception ex)
        {
            SetMessage(Strings.Get("CatalogLoadError", ex.Message));
        }
        finally
        {
            _library.SnapshotPublished -= OnSnapshotPublished;
            _busy = false;
            if (ReferenceEquals(_operation, operation)) _operation = null;
            operation.Dispose();
            SetLoading(false);
            _ready.TrySetResult();
        }
    }

    /// <summary>Binds a partial library view; bindings are coalesced on the UI thread.</summary>
    private void OnSnapshotPublished(object? sender, CatalogSnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        if (Interlocked.Exchange(ref _bindScheduled, true)) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _bindScheduled, false);
            if (_latestSnapshot is { } latest) Bind(latest, live: true);
        });
    }

    private void SetLoading(bool loading)
    {
        LoadingState.IsVisible = loading;
        ImportButton.IsEnabled = !loading;
        DownloadCatalogsButton.IsEnabled = !loading;
        ReloadButton.IsEnabled = !loading;
        CancelButton.IsVisible = loading;
        CancelButton.IsEnabled = loading;
        if (loading)
        {
            if (_catalogs.Count == 0) LibraryHeader.Text = Strings.Get("CatalogLibrary");
            LoadingText.Text = Strings.Get("CatalogLoading");
        }
        UpdateInsertState();
    }

    private void ShowProgress(CatalogProgress progress) =>
        LoadingText.Text = progress.File is { Length: > 0 } file
            ? CatalogUiText.Get("LoadingFile", progress.Loaded, progress.Total, file)
            : Strings.Get("CatalogLoadingCount", progress.Loaded, progress.Total);

    private void OnCancelLoad(object? sender, RoutedEventArgs e)
    {
        // Do not kill the shared parse: cancel the waiter, keep loading in the background.
        CancelButton.IsEnabled = false;
        _operation?.Cancel();
    }

    /// <summary>A cancelled wait still binds the snapshot once the shared parse publishes it.</summary>
    private void WatchBackgroundLoad()
    {
        if (_watchingBackground) return;
        _watchingBackground = true;
        _library.Changed += OnBackgroundLoaded;
        // The load may have published between the cancellation and this subscription: bind
        // the current snapshot now instead of waiting for an event that already happened.
        if (_library.Current is not null) Bind(_library.Current);
    }

    private void OnBackgroundLoaded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        UnwatchBackground();
        Bind(_library.Current);
        if (_library.Current is { Errors.Count: 0 }) SetMessage(CatalogUiText.Get("LoadContinued"));
    });

    private void UnwatchBackground()
    {
        if (!_watchingBackground) return;
        _watchingBackground = false;
        _library.Changed -= OnBackgroundLoaded;
    }

    private void Bind(CatalogSnapshot? snapshot, IEnumerable<string>? importErrors = null, bool live = false)
    {
        // The stable service snapshot always wins; a live partial snapshot is shown while the
        // first load fills in (and is replaced when the stable one is published).
        snapshot = live ? snapshot : _library.Current ?? snapshot;
        if (snapshot is null) return;
        _catalogs = snapshot.Catalogs.ToList();
        LibraryHeader.Text = $"{Strings.Get("CatalogLibrary")} · {_catalogs.Count}";
        LibraryEmpty.IsVisible = _catalogs.Count == 0;
        SetMessage(string.Join(Environment.NewLine, (importErrors ?? [])
            .Concat(snapshot.Errors.Select(error => Strings.Get("CatalogLoadError",
                Path.GetFileName(error.File) + ": " + error.Message)))));
        var previous = SelectedCatalog;
        CatalogList.ItemsSource = _catalogs;
        CatalogList.SelectedItem = previous is not null && _catalogs.Contains(previous)
            ? previous
            : _catalogs.FirstOrDefault();
        RemoveCatalogButton.IsEnabled = SelectedCatalog is not null;
        if (_catalogs.Count == 0) RefreshSections();
        // Same selection but enriched metadata: refresh the product list and details.
        else if (ReferenceEquals(previous, SelectedCatalog)) ApplyFilters();
        ShowImportReport();
    }

    /// <summary>Summary of the last import: products/programs plus gating flags, after import and reload.</summary>
    private void ShowImportReport()
    {
        var report = _library.Store.LastImportReport;
        ImportReport.IsVisible = report is not null;
        if (report is null) return;
        var flags = new List<string>();
        if (report.HasBrokenProgram) flags.Add(CatalogUiText.Get("BadgeBroken"));
        if (report.HasDcaProgram) flags.Add(CatalogUiText.Get("BadgeDca"));
        if (report.HasExternalSoftware) flags.Add(CatalogUiText.Get("BadgeExternal"));
        var text = CatalogUiText.Get("LastImport", Path.GetFileName(report.File)) + " · "
            + CatalogUiText.Get("ImportSummary", report.Products, report.Programs)
            + (flags.Count > 0 ? " · " + string.Join(" · ", flags) : "");
        ImportReport.Text = text;
        ToolTip.SetTip(ImportReport, report.File);
    }

    private void FillLines()
    {
        var lines = CatalogBrowser.Lines(_vm.Project);
        LineBox.ItemsSource = lines;
        if (PreferredLineOrFallback() is { } selected) SelectLine(selected);
        else if (lines.Count > 0) LineBox.SelectedIndex = 0;
        RefreshSegments();
        SuggestAddress();
        SuggestName();
        UpdateRoomContext();
        UpdateInsertState();
    }

    /// <summary>Fills the segment target from the selected line; hidden when the line has none.</summary>
    private void RefreshSegments()
    {
        var previous = (SegmentBox.SelectedItem as SegmentChoice)?.Id;
        var segments = CatalogBrowser.Segments(_vm.Project, LineBox.SelectedItem as LineChoice);
        SegmentBox.ItemsSource = segments;
        SegmentBox.SelectedItem = segments.FirstOrDefault(segment => segment.Id == previous)
            ?? segments.FirstOrDefault();
        SegmentTarget.IsVisible = segments.Count > 0;
        SegmentBox.IsEnabled = _vm.Project is not null && segments.Count > 0;
    }

    /// <summary>Shows the room context the caller passed in (e.g. opened from a room tab).</summary>
    private void UpdateRoomContext()
    {
        if (RoomContext is null) return;
        var room = _preferredRoomId is not null && _vm.Project is { } project
            ? project.Installations.SelectMany(installation => installation.Locations.AllNodes)
                .FirstOrDefault(node => node.Id == _preferredRoomId && node.Kind == SpaceKind.Room)
            : null;
        RoomContext.Text = _preferredRoomId is null
            ? CatalogUiText.Get("NoRoomContext")
            : room is not null
                ? CatalogUiText.Get("RoomContext", room.Name)
                : CatalogUiText.Get("NeedRoom");
        RoomContext.Classes.Set("wb-error", _preferredRoomId is not null && room is null);
    }

    private Line? PreferredLineOrFallback() => _preferredLine
        ?? SelectedDeviceLine()
        ?? _vm.SelectedTreeNode?.Tag as Line;

    private Line? SelectedDeviceLine() => _vm.Project is { } project && _vm.SelectedDevice is { } device
        && ProjectQuery.LocateDevice(project, device.Address) is { } located ? located.Line : null;

    private void SelectLine(Line selected)
    {
        if (_vm.Project is not { } project || LineBox.ItemsSource is not List<LineChoice> lines) return;
        var matches = (from installation in project.Installations
                       from area in installation.Areas
                       where area.Lines.Contains(selected)
                       from choice in lines
                       where choice.InstallationId == installation.Id && choice.AreaId == area.Id
                           && choice.LineId == selected.Id
                       select choice).ToList();
        if (matches.Count == 1) LineBox.SelectedItem = matches[0];
        // A stale topology node lacks its installation context: require a fresh line selection.
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_busy || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = true,
                Title = Strings.Get("CatalogImport"),
                FileTypeFilter = [new FilePickerFileType(Strings.Get("CatalogFileTypes")) { Patterns = ["*.knxprod", "*.zip"] }],
            });
            if (files.Count == 0) return;

            var paths = new List<string>();
            var errors = new List<string>();
            foreach (var file in files)
            {
                if (file.TryGetLocalPath() is { } path) paths.Add(path);
                else errors.Add(Strings.Get("CatalogLocalFileRequired", file.Name));
            }

            _busy = true;
            var operation = new CancellationTokenSource();
            _operation = operation;
            SetLoading(true);
            var needsProgramMetadata = false;
            try
            {
                var progress = new Progress<CatalogImportProgress>(item =>
                    LoadingText.Text = CatalogUiText.Get("Importing", item.Loaded, item.Total, item.File));
                var result = await _library.ImportAsync(paths, progress, operation.Token);
                errors.AddRange(result.Errors.Select(error =>
                    Strings.Get("CatalogLoadError", Path.GetFileName(error.File) + ": " + error.Message)));
                Bind(result.Snapshot, errors);
                // Split archives import with product metadata only; the follow-up read indexes
                // their program metadata so search covers program names and datapoint types.
                needsProgramMetadata = result.Catalogs.Any(catalog => !catalog.MetadataComplete);
                if (result.Canceled)
                    AddError(CatalogUiText.Get("ImportCancelled", result.Catalogs.Count, paths.Count));
                else if (result.Catalogs.Count > 0 && errors.Count == 0)
                    SetMessage(Strings.Get("CatalogImported", result.Catalogs.Count));
            }
            catch (OperationCanceledException)
            {
                SetMessage(CatalogUiText.Get("LoadCancelled"));
            }
            finally
            {
                _busy = false;
                if (ReferenceEquals(_operation, operation)) _operation = null;
                operation.Dispose();
                SetLoading(false);
            }
            if (needsProgramMetadata) await LoadAsync(reload: true);
        }
        catch (Exception ex)
        {
            // A picker or library failure must not take the process down.
            SetMessage(Strings.Get("CatalogLoadError", ex.Message));
        }
    }

    private async void OnReload(object? sender, RoutedEventArgs e) => await LoadAsync(reload: true);

    private async void OnDownloadCatalogs(object? sender, RoutedEventArgs e)
    {
        if (_busy || TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            DownloadCatalogsButton.IsEnabled = false;
            var dialog = new CatalogDownloadsWindow(_library);
            await dialog.ShowDialog(owner);
            if (dialog.LibraryReloadContinues && dialog.LibraryReloadTask is { } reload)
            {
                WatchBackgroundLoad();
                _ = ObserveDownloadedCatalogReloadAsync(reload);
            }
            if (_library.Current is { } snapshot) Bind(snapshot);
        }
        catch (Exception error) { SetMessage(Strings.Get("CatalogLoadError", error.Message)); }
        finally { DownloadCatalogsButton.IsEnabled = !_busy; }
    }

    private async Task ObserveDownloadedCatalogReloadAsync(Task<CatalogSnapshot> reload)
    {
        try { await reload; }
        catch (Exception error)
        {
            UnwatchBackground();
            SetMessage(Strings.Get("CatalogLoadError", error.Message));
        }
    }

    /// <summary>
    /// Removes the selected catalogue file from the local library after confirmation.
    /// <see cref="CatalogStore.Remove"/> refuses every path outside the library directory.
    /// </summary>
    private async void OnRemoveCatalog(object? sender, RoutedEventArgs e)
    {
        if (_busy || SelectedCatalog is not { } catalog) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            if (!await ConfirmDialog.AskDeleteAsync(owner, catalog.Name)) return;
            if (!_library.Store.Remove(catalog.Path))
            {
                SetMessage(CatalogUiText.Get("RemoveFailed", catalog.Name));
                return;
            }
            await LoadAsync(reload: true);
            SetMessage(CatalogUiText.Get("Removed", catalog.Name));
        }
        catch (Exception ex)
        {
            SetMessage(Strings.Get("CatalogLoadError", ex.Message));
        }
    }

    private void OnCatalogChanged(object? sender, SelectionChangedEventArgs e) => RefreshSections();

    private void RefreshSections()
    {
        RemoveCatalogButton.IsEnabled = SelectedCatalog is not null;
        SectionBox.ItemsSource = new[] { Strings.Get("CatalogAllSections") }
            .Concat(CatalogBrowser.Sections(SelectedCatalog?.Products ?? [])).ToList();
        SectionBox.SelectedIndex = 0;
        ApplyFilters();
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e) => ApplyFilters();
    private void OnSectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyFilters();
    private void OnLineChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshSegments();
        SuggestAddress();
        SuggestName();
        UpdateRoomContext();
        UpdateInsertState();
    }

    private void OnSegmentChanged(object? sender, SelectionChangedEventArgs e) => UpdateInsertState();

    private void OnAddressChanged(object? sender, TextChangedEventArgs e) => UpdateInsertState();

    private void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SectionBox.SelectedIndex = 0;
    }

    private void ApplyFilters()
    {
        if (ProductList is null || SectionBox is null) return;
        var generation = Interlocked.Increment(ref _filterGeneration);
        _filterOperation?.Cancel();
        _filterOperation?.Dispose();
        var operation = new CancellationTokenSource();
        _filterOperation = operation;
        var catalog = SelectedCatalog;
        var query = SearchBox?.Text;
        var section = SectionBox.SelectedIndex > 0 ? SectionBox.SelectedItem as string : null;

        _ = Task.Run(async () =>
        {
            // Debounce and filter off the UI thread; a newer keystroke cancels this request.
            var result = await CatalogSearch.RunAsync(catalog, query, section, 90, operation.Token)
                .ConfigureAwait(false);
            if (result is null || generation != Volatile.Read(ref _filterGeneration)) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _filterGeneration) return;
                PublishFiltered(catalog, result.Products);
            });
        });
    }

    /// <summary>Applies a finished background search to the lists; stale results never arrive here.</summary>
    private void PublishFiltered(KnxProductCatalog? catalog, IReadOnlyList<CatalogProduct> filtered)
    {
        if (ProductList is null) return;
        var products = catalog?.Products ?? [];
        var previous = SelectedProduct;
        ProductList.ItemsSource = filtered;
        ProductList.SelectedItem = previous is not null && filtered.Contains(previous) ? previous : filtered.FirstOrDefault();
        ProductsHeader.Text = $"{Strings.Get("CatalogProducts")} · {filtered.Count}";
        ProductsEmpty.IsVisible = filtered.Count == 0;
        ProductsEmptyText.Text = products.Count == 0
            ? Strings.Get("CatalogNoProducts")
            : Strings.Get("CatalogNoResults");
        ClearSearchButton.IsVisible = products.Count > 0;
        RefreshProduct();
    }

    private void OnProductChanged(object? sender, SelectionChangedEventArgs e) => RefreshProduct();

    private void RefreshProduct()
    {
        var product = SelectedProduct;
        if (!ReferenceEquals(product, _shownProduct))
        {
            _shownProduct = product;
            NameBox.Text = product?.Name ?? "";
            _suggestedName = product?.Name;
            SuggestAddress();
            SuggestName();
        }
        ShowDetails(product);
        UpdateInsertState();
    }

    private void ShowDetails(CatalogProduct? product)
    {
        Details.Children.Clear();
        UxProduct.Text = product?.Name ?? "";
        DetailsEmpty.IsVisible = product is null;
        if (product is null || SelectedCatalog is not { } catalog) return;
        var program = FindProgramSummary(catalog, product);
        Add(Strings.Get("CatalogManufacturer"),
            catalog.Manufacturer?.Name ?? CatalogUiText.Get("UnknownManufacturer"));
        Add(Strings.Get("ColName"), product.Name);
        Add(Strings.Get("ColMessage"), product.Description);
        Add(Strings.Get("CatalogOrderNumber"), product.OrderNumber);
        Add(Strings.Get("CatalogProgram"), program?.Name);
        Add(CatalogUiText.Get("DatapointTypes"), string.Join(" · ", CatalogBrowser.DatapointTypes(catalog, product)));
        Add(CatalogUiText.Get("Programs"), product.ProgramRefIds.Count.ToString());
        Add(Strings.Get("CatalogComObjects"), program?.ComObjects.Count.ToString());
        Add(Strings.Get("CatalogPaths"), product.CatalogPaths.Count > 0 ? string.Join(" · ", product.CatalogPaths) : null);
        Add("Id", product.Id);
        AddApplicationStatus(program);
        if (program is null)
        {
            Details.Children.Add(new TextBlock
            {
                Text = CatalogUiText.Get("UnknownVendorData"),
                Classes = { "muted" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
        }
        else
        {
            AddComObjects(program);
        }

        void Add(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Details.Children.Add(new TextBlock { Text = label, Classes = { "muted" } });
            Details.Children.Add(new TextBlock { Text = value, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        }

        // Application status determines whether the product may be used.
        void AddApplicationStatus(CatalogApplicationProgram? application)
        {
            Details.Children.Add(new TextBlock { Text = CatalogUiText.Get("ApplicationStatus"), Classes = { "muted" } });
            if (application is null)
            {
                Details.Children.Add(new TextBlock { Text = CatalogUiText.Get("NoApplication"), Classes = { "muted" } });
                return;
            }

            var badges = new WrapPanel();
            AddBadge(badges, application.Broken, "BadgeBroken", "wb-error");
            AddBadge(badges, application.DownloadInfoIncomplete, "BadgeIncomplete", "wb-error");
            AddBadge(badges, application.RequiresDca, "BadgeDca", "wb-warning");
            AddBadge(badges, application.RequiresExternalSoftware, "BadgeExternal", "wb-muted");
            if (badges.Children.Count > 0) Details.Children.Add(badges);

            // The strongest restriction decides the sentence; external software only informs.
            var reason = CatalogBrowser.Blocked(application, CatalogUse.EditParameters);
            var text = reason != CatalogBlockReason.None ? CatalogUiText.Reason(reason)
                : application.RequiresExternalSoftware ? CatalogUiText.Get("ExternalSoftware")
                : CatalogUiText.Get("StaticReady");
            var classes = reason switch
            {
                CatalogBlockReason.Broken or CatalogBlockReason.DownloadInfoIncomplete => "wb-error",
                CatalogBlockReason.DcaUnsupported => "wb-warning",
                _ => "wb-muted",
            };
            Details.Children.Add(new TextBlock
            {
                Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { classes },
            });

            Add(CatalogUiText.Get("RegistrationStatus"), application.RegistrationStatus);
            Add(CatalogUiText.Get("MinEtsVersion"), application.MinEtsVersion);
            Add(CatalogUiText.Get("SchemaVersion"), application.SchemaVersion);
            Add(CatalogUiText.Get("MaskVersion"), application.MaskVersion);
        }

        // Bounded preview of the application's communication objects with their DPT metadata.
        void AddComObjects(CatalogApplicationProgram application)
        {
            Details.Children.Add(new TextBlock
            {
                Text = CatalogUiText.Get("ComObjectsPreview"), Classes = { "muted" }, Margin = new Avalonia.Thickness(0, 4, 0, 0),
            });
            if (application.ComObjects.Count == 0)
            {
                Details.Children.Add(new TextBlock { Text = CatalogUiText.Get("NoComObjects"), Classes = { "muted" } });
                return;
            }
            const int limit = 20;
            foreach (var comObject in application.ComObjects.Take(limit))
            {
                var parts = new List<string> { $"#{comObject.Number}", comObject.Name };
                if (!string.IsNullOrWhiteSpace(comObject.DatapointType)) parts.Add("DPT " + comObject.DatapointType);
                if (comObject.SizeInBit > 0) parts.Add($"{comObject.SizeInBit} {CatalogUiText.Get("Bits")}");
                Details.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", parts), Classes = { "catalog-com" },
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                });
            }
            if (application.ComObjects.Count > limit)
                Details.Children.Add(new TextBlock
                {
                    Text = CatalogUiText.Get("MoreComObjects", application.ComObjects.Count - limit),
                    Classes = { "muted" },
                });
        }

        static void AddBadge(WrapPanel panel, bool shown, string key, string classes)
        {
            if (!shown) return;
            var text = new TextBlock { Text = CatalogUiText.Get(key) };
            text.Classes.Add(classes);
            var badge = new Border { Margin = new Avalonia.Thickness(0, 0, 4, 4), Child = text };
            badge.Classes.Add("catalog-badge");
            panel.Children.Add(badge);
        }
    }

    private CatalogApplicationProgram? FindProgramSummary(KnxProductCatalog catalog, CatalogProduct product)
    {
        try { return catalog.FindProgramSummaryFor(product); }
        catch (Exception ex)
        {
            AddError(Strings.Get("CatalogLoadError", product.Name + ": " + ex.Message));
            return null;
        }
    }

    /// <summary>Fills the address field with the lowest free address, without replacing typed input.</summary>
    private void SuggestAddress()
    {
        var previous = _suggestion;
        _suggestion = CatalogBrowser.SuggestAddress(_vm.Project, LineBox.SelectedItem as LineChoice);
        var current = AddressBox.Text?.Trim() ?? "";
        if (current.Length == 0 || current == previous) AddressBox.Text = _suggestion ?? "";
    }

    /// <summary>Fills a free device name, without replacing a name the user typed.</summary>
    private void SuggestName()
    {
        if (SelectedProduct is not { } product) { _suggestedName = null; return; }
        var previous = _suggestedName;
        _suggestedName = CatalogBrowser.UniqueName(_vm.Project, LineBox.SelectedItem as LineChoice, product.Name);
        var current = NameBox.Text?.Trim() ?? "";
        if (current.Length == 0 || current == previous) NameBox.Text = _suggestedName;
    }

    private void UpdateInsertState()
    {
        var hasProject = _vm.Project is not null;
        var blocker = hasProject ? InsertBlocker() : null;
        UxNeedsProject.IsVisible = !hasProject;
        LineBox.IsEnabled = hasProject && !_busy;
        SegmentBox.IsEnabled = hasProject && !_busy && SegmentBox.ItemsSource is List<SegmentChoice> { Count: > 0 };
        AddressBox.IsEnabled = hasProject && !_busy;
        NameBox.IsEnabled = hasProject && !_busy;
        InsertButton.IsEnabled = hasProject && !_busy && blocker is null;
        SetInsertStatus(hasProject ? blocker ?? "" : "", error: SelectedProduct is not null);
    }

    private string? InsertBlocker()
    {
        if (_vm.Project is not { } project) return null;
        if (SelectedProduct is null) return Strings.Get("CatalogNeedProduct");
        // Broken or incomplete applications must not enter the project at all (metadata only).
        var program = SelectedCatalog is { } catalog ? FindProgramSummary(catalog, SelectedProduct) : null;
        var blocked = CatalogBrowser.Blocked(program, CatalogUse.Insert);
        if (blocked != CatalogBlockReason.None) return CatalogUiText.Reason(blocked);
        if (LineBox.SelectedItem is not LineChoice choice || ResolveLine(choice) is not { } target)
            return Strings.Get("CatalogNeedLine");
        var text = AddressBox.Text?.Trim() ?? "";
        if (!IndividualAddress.TryParse(text, out var address)) return Strings.Get("CatalogInvalidAddress");
        if (address.Area != target.Area.Address || address.Line != target.Line.Address || address.Device == 0)
            return Strings.Get("CatalogInvalidAddress");
        if (ProjectQuery.FindDevice(project, address) is not null)
            return Strings.Get("CatalogDuplicateAddress", address);
        return null;
    }

    private (Area Area, Line Line)? ResolveLine(LineChoice choice)
    {
        if (_vm.Project is not { } project || choice.ProjectId != project.Id || choice.ProjectGuid != project.Guid)
            return null;
        var targets = (from installation in project.Installations
                       where installation.Id == choice.InstallationId
                       from area in installation.Areas where area.Id == choice.AreaId
                       from line in area.Lines where line.Id == choice.LineId
                       select (Area: area, Line: line)).ToList();
        return targets.Count == 1 ? targets[0] : null;
    }

    private void SetInsertStatus(string text, bool error = false)
    {
        InsertStatus.Text = text;
        // Validation blockers and failures read red; a completed insert reads as success.
        InsertStatus.Classes.Set("wb-error", error && text.Length > 0);
        InsertStatus.Classes.Set("wb-success", !error && text.Length > 0);
        ToolTip.SetTip(InsertStatus, text);
    }

    private void SetMessage(string text)
    {
        _message = text;
        ErrorText.Text = text.ReplaceLineEndings(" · ");
        ErrorText.IsVisible = ErrorText.Text.Length > 0;
        ToolTip.SetTip(ErrorText, text);
    }

    private void AddError(string error) => SetMessage(
        string.Join(Environment.NewLine, new[] { _message, error }
            .Where(text => !string.IsNullOrWhiteSpace(text))));

    private void OnInsert(object? sender, RoutedEventArgs e)
    {
        var product = SelectedProduct;
        CatalogApplicationProgram? program = null;
        if (product is not null && SelectedCatalog is { } catalog) program = FindProgramSummary(catalog, product);
        var inserted = CatalogBrowser.Insert(_vm, product, program, LineBox.SelectedItem as LineChoice,
            AddressBox.Text, NameBox.Text, out var message, PreferredRoomId,
            (SegmentBox.SelectedItem as SegmentChoice)?.Id);
        _vm.Status = message;
        if (inserted)
        {
            AddressBox.Text = "";
            SuggestAddress();
            SuggestName();
            UpdateInsertState();
        }
        SetInsertStatus(inserted ? message + " · " + CatalogUiText.Get("InsertUndoHint") : message,
            error: !inserted);
    }

    private void OnClose(object? sender, RoutedEventArgs e) =>
        (TopLevel.GetTopLevel(this) as Window)?.Close();
}
