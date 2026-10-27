using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Knx;
using Knx.App.Localization;
using Knx.App.Services;

namespace Knx.App.Views;

/// <summary>
/// Project-wide search and rename replacement as a dockable pane. Searches the loaded project through
/// <see cref="ProjectSearch"/>; replacing runs through the host's undo stack and never touches the bus
/// or the project file.
/// </summary>
public partial class SearchPane : UserControl
{
    private static readonly (SearchScope Scope, string Key)[] Scopes =
    [
        (SearchScope.All, "SearchScopeAll"),
        (SearchScope.Devices, "SearchScopeDevices"),
        (SearchScope.GroupAddresses, "SearchScopeGroupAddresses"),
        (SearchScope.Topology, "SearchScopeTopology"),
        (SearchScope.CommunicationObjects, "SearchScopeCommunicationObjects"),
        (SearchScope.Buildings, "SearchScopeBuildings"),
    ];

    private readonly MainWindowViewModel? _vm;
    private Project? _project;
    private ReplacePreview? _preview;
    private bool _suspendResults;

    public SearchPane() : this(null) { }

    /// <param name="vm">Project and undo host; may be null while docked without a project.</param>
    public SearchPane(MainWindowViewModel? vm)
    {
        _vm = vm;
        InitializeComponent();
        FillScopes();
        Refresh();
    }

    /// <summary>Raised when the user picks a result; the dock host navigates to it.</summary>
    public event Action<SearchHit>? NavigateRequested;

    /// <summary>Re-reads the project after load, undo/redo or a host edit; drops results that no longer match.</summary>
    public void Refresh()
    {
        var project = _vm?.Project;
        if (!ReferenceEquals(project, _project))
        {
            _project = project;
            ClearResults();
            ResetPreview();
        }

        Body.IsVisible = project is not null;
        EmptyText.IsVisible = project is null;
        SearchButton.IsEnabled = PreviewButton.IsEnabled = project is not null;
        ApplyButton.IsEnabled = project is not null && _preview is { FieldCount: > 0 };
        if (project is null) ClearResults();
    }

    // ---------------------------------------------------------------- search

    private void FillScopes()
    {
        ScopeBox.ItemsSource = Scopes.Select(scope => Strings.Get(scope.Key)).ToList();
        ScopeBox.SelectedIndex = 0;
    }

    private SearchScope SelectedScope
    {
        get
        {
            var index = ScopeBox.SelectedIndex;
            return index >= 0 && index < Scopes.Length ? Scopes[index].Scope : SearchScope.All;
        }
    }

    private SearchQuery BuildSearchQuery() => new()
    {
        Text = QueryBox.Text?.Trim() ?? "",
        Scope = SelectedScope,
        Regex = RegexCheck.IsChecked == true,
        CaseSensitive = CaseCheck.IsChecked == true,
    };

    private ReplaceQuery BuildReplaceQuery() => new()
    {
        Text = QueryBox.Text?.Trim() ?? "",
        Replacement = ReplaceBox.Text ?? "",
        Scope = SelectedScope,
        Regex = RegexCheck.IsChecked == true,
        CaseSensitive = CaseCheck.IsChecked == true,
    };

    private void OnSearch(object? sender, RoutedEventArgs e) => RunSearch();

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        RunSearch();
    }

    private void RunSearch()
    {
        ClearResults();
        if (_vm?.Project is not { } project)
        {
            ShowSearchMessage(Strings.Get("UxNeedsProject"));
            return;
        }

        _project = project;
        var query = BuildSearchQuery();
        if (query.Text.Length == 0)
        {
            ShowSearchMessage(Strings.Get("SearchEmptyQuery"));
            return;
        }

        try
        {
            var result = ProjectSearch.Search(project, query);
            var rows = result.Hits.Select(hit => new SearchHitRow(hit)).ToList();
            _suspendResults = true;
            ResultList.ItemsSource = rows;
            _suspendResults = false;
            ResultsCount.Text = result.Truncated
                ? Strings.Get("SearchTruncated", result.Total, rows.Count)
                : Strings.Get("SearchCount", rows.Count);
            ResultsEmpty.IsVisible = rows.Count == 0;
            if (rows.Count == 0) ResultsCount.Text = "";
        }
        catch (ArgumentException ex)
        {
            ShowSearchMessage(Strings.Get("SearchInvalidRegex", ex.Message));
        }
    }

    private void OnResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suspendResults) return;
        if (ResultList.SelectedItem is not SearchHitRow row) return;
        NavigateTo(row.Hit);
        NavigateRequested?.Invoke(row.Hit);
    }

    /// <summary>Selects the matching device or group address before the host gets the event.</summary>
    private void NavigateTo(SearchHit hit)
    {
        if (_vm is not { Project: { } project } || !ReferenceEquals(project, _project)) return;
        switch (hit.Kind)
        {
            case ProjectSearch.DeviceKind:
                _vm.SelectedDevice = _vm.Devices.FirstOrDefault(row => row.Device.Id == hit.Id);
                break;
            case ProjectSearch.GroupAddressKind:
                _vm.SelectedGroupAddress = _vm.GroupAddresses.FirstOrDefault(row => row.Id == hit.Id);
                break;
        }
    }

    private void ShowSearchMessage(string message)
    {
        SearchStatus.Text = message;
        SearchStatus.IsVisible = true;
    }

    private void ClearResults()
    {
        _suspendResults = true;
        ResultList.ItemsSource = null;
        _suspendResults = false;
        ResultsCount.Text = "";
        ResultsEmpty.IsVisible = false;
        SearchStatus.IsVisible = false;
    }

    // ---------------------------------------------------------------- replace

    private void OnPreview(object? sender, RoutedEventArgs e) => RunPreview();

    private void RunPreview()
    {
        ResetPreview();
        if (_vm?.Project is not { } project)
        {
            ShowPreviewMessage(Strings.Get("UxNeedsProject"));
            return;
        }

        _project = project;
        var query = BuildReplaceQuery();
        if (query.Text.Length == 0)
        {
            ShowPreviewMessage(Strings.Get("SearchEmptyQuery"));
            return;
        }

        ReplacePreview preview;
        try
        {
            preview = ProjectSearch.Preview(project, query);
        }
        catch (ArgumentException ex)
        {
            ShowPreviewMessage(Strings.Get("SearchInvalidRegex", ex.Message));
            return;
        }

        _preview = preview;
        SetPreviewRows(preview);
        ApplyButton.IsEnabled = preview.FieldCount > 0;
        if (preview.FieldCount == 0)
        {
            ShowPreviewMessage(Strings.Get("SearchPreviewNoMatch"));
            return;
        }

        PreviewCounts.Text = Strings.Get("SearchPreviewCounts", preview.FieldCount, preview.ItemCount);
    }

    private void OnApply(object? sender, RoutedEventArgs e) => RunApply();

    /// <summary>
    /// Recomputes the preview before applying: a changed query or an edited project yields different
    /// changes and is rejected as stale instead of applying a preview the user never saw.
    /// </summary>
    private void RunApply()
    {
        if (_vm?.Project is not { } project)
        {
            ShowPreviewMessage(Strings.Get("UxNeedsProject"));
            return;
        }

        if (_preview is not { FieldCount: > 0 } preview)
        {
            ShowPreviewMessage(Strings.Get("SearchPreviewNothing"));
            return;
        }

        if (!ReferenceEquals(project, _project))
        {
            ResetPreview();
            ShowPreviewMessage(Strings.Get("SearchPreviewStale"));
            return;
        }

        ReplacePreview fresh;
        try
        {
            fresh = ProjectSearch.Preview(project, BuildReplaceQuery());
        }
        catch (ArgumentException ex)
        {
            ResetPreview();
            ShowPreviewMessage(Strings.Get("SearchInvalidRegex", ex.Message));
            return;
        }

        if (!fresh.Changes.SequenceEqual(preview.Changes))
        {
            _preview = fresh;
            SetPreviewRows(fresh);
            ApplyButton.IsEnabled = false;
            PreviewCounts.Text = fresh.FieldCount > 0
                ? Strings.Get("SearchPreviewCounts", fresh.FieldCount, fresh.ItemCount)
                : "";
            ShowPreviewMessage(Strings.Get("SearchPreviewStale"));
            return;
        }

        ReplaceResult? applied = null;
        try
        {
            _vm.Edit(Strings.Get("SearchApplied", fresh.FieldCount, fresh.ItemCount),
                p => applied = ProjectSearch.Apply(p, fresh));
        }
        catch (InvalidOperationException)
        {
            ResetPreview();
            ShowPreviewMessage(Strings.Get("SearchPreviewStale"));
            return;
        }

        ResetPreview(keepMessage: true);
        var result = applied ?? new ReplaceResult(0, 0, []);
        ShowPreviewMessage(Strings.Get("SearchApplied", result.FieldCount, result.ItemCount));
        if ((QueryBox.Text?.Trim().Length ?? 0) > 0) RunSearch();
    }

    private void SetPreviewRows(ReplacePreview preview)
    {
        var rows = preview.Changes.Select(change => new ReplaceChangeRow(change)).ToList();
        PreviewList.ItemsSource = rows;
        PreviewEmpty.IsVisible = rows.Count == 0;
    }

    private void ResetPreview(bool keepMessage = false)
    {
        _preview = null;
        PreviewList.ItemsSource = null;
        PreviewCounts.Text = "";
        PreviewEmpty.IsVisible = true;
        ApplyButton.IsEnabled = false;
        if (keepMessage) return;
        PreviewStatus.IsVisible = false;
        PreviewStatus.Text = "";
    }

    private void ShowPreviewMessage(string message)
    {
        PreviewStatus.Text = message;
        PreviewStatus.IsVisible = true;
    }

    internal static string KindTextOf(string kind) => Strings.Get(kind switch
    {
        ProjectSearch.DeviceKind => "SearchKindDevice",
        ProjectSearch.GroupAddressKind => "SearchKindGroupAddress",
        ProjectSearch.TopologyKind => "SearchKindTopology",
        ProjectSearch.CommunicationObjectKind => "SearchKindCommunicationObject",
        ProjectSearch.BuildingKind => "SearchKindBuilding",
        _ => "SearchKindOther",
    });
}

/// <summary>Row of the result list: one typed match with its display label.</summary>
public sealed record SearchHitRow(SearchHit Hit)
{
    public string KindText => SearchPane.KindTextOf(Hit.Kind);
    public string Path => Hit.Path;
    public string Field => Hit.Field;
    public string Snippet => Hit.Snippet;
}

/// <summary>Row of the replace preview: what one field would become.</summary>
public sealed record ReplaceChangeRow(ReplaceChange Change)
{
    public string Path => Change.Path;
    public string Field => Change.Field;
    public string Before => Change.Before;
    public string After => Change.After;
}
