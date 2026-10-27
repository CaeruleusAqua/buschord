using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Knx.App.Workbench;

namespace Knx.App;

public partial class WorkbenchWindow
{
    private void BuildMultiObjectInspector(IReadOnlyList<WorkbenchRow> rows)
    {
        InspectorTitle.Text = W("MultiTitle");
        InspectorBody.Children.Add(Label(W("RowsSelected", rows.Count)));
        var types = string.Join(", ", rows.Select(r => r.Dpt ?? W("Unknown")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var flags = rows.Select(r => r.Flags ?? W("Unknown")).Distinct(StringComparer.Ordinal).ToArray();
        var deviceCount = rows.Select(r => r.Device.Id).Distinct(StringComparer.Ordinal).Count();
        InspectorBody.Children.Add(Section(W("SectionObject"),
            Field(W("DatapointType"), Note(types), "inspector.multi.types"),
            Field(W("ObjectFlags"), Note(flags.Length == 1 ? flags[0] : W("MultiMixed")), "inspector.multi.flags"),
            Field(W("MultiDevices"), Note(deviceCount.ToString(System.Globalization.CultureInfo.CurrentCulture)), "inspector.multi.devices")));
        InspectorBody.Children.Add(Note(W("MultiReadOnly")));
        var list = new ListBox { Name = "MultiInspectorObjects", Height = Math.Min(320, rows.Count * 52), ItemsSource = rows };
        AutomationProperties.SetName(list, W("MultiTitle"));
        list.ItemTemplate = new FuncDataTemplate<WorkbenchRow>((row, _) =>
        {
            if (row?.Object is not { } obj) return new TextBlock();
            var text = $"{row.Device.Address} · #{obj.Number} · {row.Name}";
            var button = ActionButton(text, () => RevealComObject(row.Device, obj.Id), "quiet", text);
            button.Content = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            return button;
        });
        InspectorBody.Children.Add(list);
        var frozen = rows.Select(r => new BatchLinkItem(r.Device.Id, r.Object!.Id, r.Name)).ToArray();
        var session = ViewModel.ProjectSessionIdentity;
        var assign = ActionButton(W("LinkSelection"), () =>
        {
            if (!ReferenceEquals(session, ViewModel.ProjectSessionIdentity)) { ViewModel.Status = W("SmartStale"); return; }
            ShowBatchLinkDialogForSelection(frozen);
        }, "primary");
        assign.Name = "MultiInspectorLink";
        FooterAction(assign);
    }
}
