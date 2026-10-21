using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Knx;
using Knx.App.Services;
using Knx.Catalog;

namespace Knx.App.Views;

/// <summary>
/// Product catalogue browser: loads .knxprod files from the local library, shows products
/// and inserts a device (with its communication objects) into the open project.
/// Embedded as a tab of the ETS-style workspace.
/// </summary>
public partial class CatalogPane : UserControl
{
    private readonly MainWindowViewModel _vm;
    private readonly CatalogLibrary _library;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<KnxProductCatalog> _catalogs = [];
    private CatalogProduct? _shownProduct;
    private CatalogApplicationProgram? _shownProgram;
    private string? _suggestion;
    private Line? _preferredLine;
    private bool _busy;

    public CatalogPane() : this(new MainWindowViewModel()) { }

    public CatalogPane(MainWindowViewModel vm, CatalogLibrary? library = null)
    {
        _vm = vm;
        _library = library ?? CatalogLibrary.Shared;
        InitializeComponent();
        RemoveCatalogButton.Content = CatalogUiText.Get("RemoveCatalog");
        ToolTip.SetTip(RemoveCatalogButton, CatalogUiText.Get("RemoveCatalogTip"));
        Loaded += OnLoaded;
        FillLines();
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

    /// <summary>Shows the library while it is read; the thread-safe service does the parsing off the UI thread.</summary>
    private async Task LoadAsync(bool reload)
    {
        if (_busy) return;
        _busy = true;
        SetLoading(true);
        try
        {
            var progress = new Progress<CatalogProgress>(ShowProgress);
            var snapshot = reload
                ? await _library.ReloadAsync(progress)
                : await _library.GetAsync(progress);
            Bind(snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetMessage(Strings.Get("CatalogLoadError", ex.Message));
        }
        finally
        {
            _busy = false;
            SetLoading(false);
            _ready.TrySetResult();
        }
    }

    private void SetLoading(bool loading)
    {
        LoadingState.IsVisible = loading;
        if (loading)
        {
            if (_catalogs.Count == 0) LibraryHeader.Text = Strings.Get("CatalogLibrary");
            LoadingText.Text = Strings.Get("CatalogLoading");
        }
    }

    private void ShowProgress(CatalogProgress progress) =>
        LoadingText.Text = Strings.Get("CatalogLoadingCount", progress.Loaded, progress.Total);

    private void Bind(CatalogSnapshot? snapshot, IEnumerable<string>? importErrors = null)
    {
        // The service snapshot is always the newest (it may include an import that finished
        // while this bind was scheduled); fall back to the caller's when it is still loading.
        snapshot = _library.Current ?? snapshot;
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
        SuggestAddress();
        UpdateInsertState();
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
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = true,
                Title = Strings.Get("CatalogImport"),
                FileTypeFilter = [new FilePickerFileType(Strings.Get("CatalogFileTypes")) { Patterns = ["*.knxprod", "*.zip"] }],
            });
            if (files.Count == 0) return;

            var imported = new List<KnxProductCatalog>();
            var errors = new List<string>();
            foreach (var file in files)
            {
                if (file.TryGetLocalPath() is not { } path)
                {
                    errors.Add(Strings.Get("CatalogLocalFileRequired", file.Name));
                    continue;
                }
                try
                {
                    // Import parses the new file; keep that off the UI thread like the library load.
                    imported.Add(await Task.Run(() => _library.Store.Import(path)));
                }
                catch (Exception ex)
                {
                    errors.Add(Strings.Get("CatalogLoadError", Path.GetFileName(path) + ": " + ex.Message));
                }
            }
            // Merge into the cached snapshot instead of re-parsing the whole library.
            var snapshot = _library.Add(imported);
            if (snapshot is null)
            {
                await _library.GetAsync();
                snapshot = _library.Add(imported);
            }
            Bind(snapshot, errors);
            if (imported.Count > 0 && errors.Count == 0) SetMessage(Strings.Get("CatalogImported", imported.Count));
        }
        catch (Exception ex)
        {
            // A picker or library failure must not take the process down.
            SetMessage(Strings.Get("CatalogLoadError", ex.Message));
        }
    }

    private async void OnReload(object? sender, RoutedEventArgs e) => await LoadAsync(reload: true);

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
        SuggestAddress();
        UpdateInsertState();
    }

    private void OnAddressChanged(object? sender, TextChangedEventArgs e) => UpdateInsertState();

    private void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SectionBox.SelectedIndex = 0;
    }

    private void ApplyFilters()
    {
        if (ProductList is null || SectionBox is null) return;
        var products = SelectedCatalog?.Products ?? [];
        var previous = SelectedProduct;
        var filtered = CatalogBrowser.Filter(products, SearchBox?.Text,
            SectionBox.SelectedIndex > 0 ? SectionBox.SelectedItem as string : null);
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
            SuggestAddress();
        }
        ShowDetails(product);
        UpdateInsertState();
    }

    private void ShowDetails(CatalogProduct? product)
    {
        Details.Children.Clear();
        UxProduct.Text = product?.Name ?? "";
        DetailsEmpty.IsVisible = product is null;
        _shownProgram = null;
        if (product is null || SelectedCatalog is not { } catalog) return;

        var program = FindProgram(catalog, product);
        _shownProgram = program;
        Add(Strings.Get("CatalogManufacturer"), catalog.Manufacturer?.Name);
        Add(Strings.Get("ColName"), product.Name);
        Add(Strings.Get("ColMessage"), product.Description);
        Add(Strings.Get("CatalogOrderNumber"), product.OrderNumber);
        Add(Strings.Get("CatalogProgram"), program?.Name);
        Add(CatalogUiText.Get("Programs"), product.ProgramRefIds.Count.ToString());
        Add(Strings.Get("CatalogComObjects"), program?.ComObjects.Count.ToString());
        Add(Strings.Get("CatalogPaths"), product.CatalogPaths.Count > 0 ? string.Join(" · ", product.CatalogPaths) : null);
        Add("Id", product.Id);
        AddApplicationStatus(program);

        void Add(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Details.Children.Add(new TextBlock { Text = label, Classes = { "muted" } });
            Details.Children.Add(new TextBlock { Text = value, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        }

        // ETS-derived status first: it decides whether the product may be used at all.
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

    private CatalogApplicationProgram? FindProgram(KnxProductCatalog catalog, CatalogProduct product)
    {
        try { return catalog.FindProgramFor(product); }
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

    private void UpdateInsertState()
    {
        var hasProject = _vm.Project is not null;
        var blocker = hasProject ? InsertBlocker() : null;
        UxNeedsProject.IsVisible = !hasProject;
        LineBox.IsEnabled = hasProject;
        AddressBox.IsEnabled = hasProject;
        NameBox.IsEnabled = hasProject;
        InsertButton.IsEnabled = hasProject && blocker is null;
        SetInsertStatus(hasProject ? blocker ?? "" : "", error: true);
    }

    private string? InsertBlocker()
    {
        if (_vm.Project is not { } project) return null;
        if (SelectedProduct is null) return Strings.Get("CatalogNeedProduct");
        // Broken or incomplete applications must not enter the project at all.
        var blocked = CatalogBrowser.Blocked(_shownProgram, CatalogUse.Insert);
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
        ErrorText.Text = text.ReplaceLineEndings(" · ");
        ErrorText.IsVisible = ErrorText.Text.Length > 0;
        ToolTip.SetTip(ErrorText, text);
    }

    private void AddError(string error) => SetMessage(
        string.Join(Environment.NewLine, new[] { ToolTip.GetTip(ErrorText) as string, error }
            .Where(text => !string.IsNullOrWhiteSpace(text))));

    private void OnInsert(object? sender, RoutedEventArgs e)
    {
        var product = SelectedProduct;
        CatalogApplicationProgram? program;
        try { program = product is null ? null : SelectedCatalog?.FindProgramFor(product); }
        catch (Exception ex)
        {
            _vm.Status = Strings.Get("CatalogLoadError", ex.Message);
            SetInsertStatus(_vm.Status, error: true);
            return;
        }
        var inserted = CatalogBrowser.Insert(_vm, product, program, LineBox.SelectedItem as LineChoice,
            AddressBox.Text, NameBox.Text, out var message);
        _vm.Status = message;
        if (inserted)
        {
            AddressBox.Text = "";
            SuggestAddress();
            UpdateInsertState();
        }
        SetInsertStatus(message, error: !inserted);
    }

    private void OnClose(object? sender, RoutedEventArgs e) =>
        (TopLevel.GetTopLevel(this) as Window)?.Close();
}
