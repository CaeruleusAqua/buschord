using Avalonia.Controls;
using Avalonia.Interactivity;
using Knx;
using Knx.App.Services;

namespace Knx.App.Views;

/// <summary>
/// Communication-object linking as an embedded pane. Project editing only; the domain service
/// validates every reference before mutation, so no bus traffic happens here.
/// </summary>
public partial class GroupLinksPane : UserControl
{
    private readonly MainWindowViewModel? _vm;
    private Project? _project;
    private IndividualAddress? _address;
    private string? _deviceId;
    private bool _suspendFilters;

    public GroupLinksPane() => InitializeComponent();

    public GroupLinksPane(MainWindowViewModel vm) : this()
    {
        _vm = vm;
        Refresh();
    }

    /// <summary>Device resolved in the current project; null while nothing is loaded.</summary>
    public DeviceInstance? Device { get; private set; }

    /// <summary>Points the pane at a device; null shows the empty state.</summary>
    public void Load(DeviceRow? device)
    {
        _address = device?.Address;
        _deviceId = device?.Device.Id;
        _suspendFilters = true;
        ObjectSearch.Text = "";
        AddressSearch.Text = "";
        _suspendFilters = false;
        Refresh();
    }

    /// <summary>Re-resolves project and device, e.g. after undo/redo replaced the project instance.</summary>
    public void Refresh()
    {
        _project = _vm?.Project;
        Device = Resolve(_project);
        Body.IsVisible = Device is not null;
        EmptyText.IsVisible = Device is null;
        if (Device is null)
        {
            ObjectList.ItemsSource = AddressList.ItemsSource = LinkList.ItemsSource = null;
            StatusText.Text = _vm?.Project is null || _address is null
                ? ""
                : Strings.Get("LinkProjectChanged");
            return;
        }

        DeviceText.Text = Device.Name is { Length: > 0 } name
            ? $"{Device.Address} — {name}"
            : Device.Address.ToString();
        StatusText.Text = "";
        FillObjects();
        FillAddresses();
    }

    private DeviceInstance? Resolve(Project? project)
    {
        if (project is null || _address is null) return null;
        var matches = ProjectQuery.DeviceList(project).Where(d => d.Address == _address).ToList();
        return matches.FirstOrDefault(d => d.Id == _deviceId) ?? (matches.Count == 1 ? matches[0] : null);
    }

    private ComObjectInstanceRef? SelectedObject => (ObjectList.SelectedItem as LinkObjectRow)?.Object;

    private void FillObjects()
    {
        var previous = SelectedObject?.Id;
        var query = ObjectSearch.Text?.Trim() ?? "";
        var datapointTypes = DatapointTypes();
        var rows = new List<LinkObjectRow>();
        IEnumerable<ComObjectInstanceRef> objects = Device is null ? [] : Device.ComObjectRefs;
        foreach (var obj in objects)
        {
            var row = new LinkObjectRow(obj, obj.Number.ToString(), obj.Text ?? obj.FunctionText ?? obj.Id,
                DatapointTypeOf(obj, datapointTypes));
            if (query.Length == 0
                || Matches(query, row.Number, obj.Text, obj.FunctionText, row.DatapointType, obj.Id, obj.ComObjectId))
                rows.Add(row);
        }

        ObjectList.ItemsSource = rows;
        ObjectList.SelectedItem = rows.FirstOrDefault(r => r.Object.Id == previous) ?? rows.FirstOrDefault();
        ObjectEmpty.IsVisible = rows.Count == 0 && query.Length > 0;
        FillLinks();
    }

    private void FillAddresses()
    {
        var previous = (AddressList.SelectedItem as GroupAddressRow)?.Id;
        var query = AddressSearch.Text?.Trim() ?? "";
        var rows = new List<GroupAddressRow>();
        if (_project is not null)
            foreach (var entry in ProjectQuery.GroupAddresses(_project).OrderBy(a => a.Address.Raw))
            {
                var row = new GroupAddressRow(entry.Address, entry.Name, entry.DatapointType, entry.Id);
                if (query.Length == 0 || Matches(query, row.AddressText, row.Name, row.DatapointType))
                    rows.Add(row);
            }

        AddressList.ItemsSource = rows;
        AddressList.SelectedItem = rows.FirstOrDefault(r => r.Id == previous);
        AddressEmpty.IsVisible = rows.Count == 0 && query.Length > 0;
        UpdateButtons();
    }

    private void FillLinks()
    {
        var previous = (LinkList.SelectedItem as LinkTargetRow)?.Id;
        var addresses = new Dictionary<string, GroupAddressEntry>();
        if (_project is not null)
            foreach (var entry in ProjectQuery.GroupAddresses(_project))
                addresses.TryAdd(entry.Id, entry);
        List<string> links = SelectedObject is { } obj ? obj.Links.Distinct().ToList() : [];
        var rows = links.Select(id => addresses.TryGetValue(id, out var entry)
            ? new LinkTargetRow(id, entry.Address.ToString(), entry.Name)
            : new LinkTargetRow(id, id, "")).ToList();

        LinkList.ItemsSource = rows;
        LinkList.SelectedItem = rows.FirstOrDefault(r => r.Id == previous);
        UpdateButtons();
    }

    /// <summary>DPT per linked group address id; a communication-object reference carries none of its own.</summary>
    private Dictionary<string, string> DatapointTypes()
    {
        var map = new Dictionary<string, string>();
        if (_project is null) return map;
        foreach (var entry in ProjectQuery.GroupAddresses(_project))
            if (!string.IsNullOrWhiteSpace(entry.DatapointType))
                map.TryAdd(entry.Id, entry.DatapointType);
        return map;
    }

    private static string? DatapointTypeOf(ComObjectInstanceRef obj, Dictionary<string, string> datapointTypes)
    {
        var types = obj.Links.Select(id => datapointTypes.GetValueOrDefault(id))
            .Where(type => type is not null).Distinct().ToList();
        return types.Count == 1 ? types[0] : null; // mixed links have no single DPT
    }

    private static bool Matches(string query, params string?[] fields)
        => fields.Any(field => field is not null && field.Contains(query, StringComparison.OrdinalIgnoreCase));

    private void UpdateButtons()
    {
        var hasObject = SelectedObject is not null;
        AddButton.IsEnabled = hasObject && AddressList.SelectedItem is not null;
        RemoveButton.IsEnabled = hasObject && LinkList.SelectedItem is not null;
    }

    private void OnObjectFilterChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_suspendFilters) FillObjects();
    }

    private void OnAddressFilterChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_suspendFilters) FillAddresses();
    }

    private void OnObjectSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suspendFilters) return;
        FillLinks();
        UpdateButtons();
    }

    private void OnAddressSelected(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnLinkSelected(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void OnAdd(object? sender, RoutedEventArgs e) => Change(remove: false);

    private void OnRemove(object? sender, RoutedEventArgs e) => Change(remove: true);

    private void Change(bool remove)
    {
        try
        {
            if (_vm is not { Project: { } project })
                throw new InvalidOperationException(Strings.Get("UxNeedsProject"));
            if (!ReferenceEquals(project, _project))
                throw new InvalidOperationException(Strings.Get("LinkProjectChanged"));
            if (SelectedObject is not { } obj) return;
            if (Device is not { } device || !ProjectQuery.DeviceList(project).Any(d => ReferenceEquals(d, device)))
                throw new InvalidOperationException(Strings.Get("LinkSelectDevice"));
            var groupAddressId = remove
                ? (LinkList.SelectedItem as LinkTargetRow)?.Id
                : (AddressList.SelectedItem as GroupAddressRow)?.Id;
            if (groupAddressId is null)
            {
                StatusText.Text = Strings.Get("LinkSelectAddress");
                return;
            }

            var changed = _vm.ChangeGroupLink(device, obj.Id, groupAddressId, remove);
            Refresh();
            StatusText.Text = changed ? _vm.Status : Strings.Get("LinkUnchanged");
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }
}

/// <summary>Row of the communication-object list.</summary>
public sealed record LinkObjectRow(ComObjectInstanceRef Object, string Number, string Text, string? DatapointType);

/// <summary>Row of the existing-links list; the target group address resolved to address and name.</summary>
public sealed record LinkTargetRow(string Id, string Address, string Name);
