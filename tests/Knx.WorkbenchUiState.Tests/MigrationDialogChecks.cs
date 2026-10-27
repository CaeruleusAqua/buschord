using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.Workbench;

internal static class MigrationDialogChecks
{
    internal static async Task<(int Checks, int Failures)> Run(Window owner)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "Migration dialog: " + name); if (!ok) failures++; }
        const string old = "M-0001_A-dialog-old", next = "M-0001_A-dialog-new";
        var project = new Project { Id = "P-migration-dialog", Name = "Synthetic migration dialog" };
        var installation = new Installation { Id = "I-1" }; var area = new Area { Id = "A-1", Address = 1 }; var line = new Line { Id = "L-1", Address = 1 };
        project.Installations.Add(installation); installation.Areas.Add(area); area.Lines.Add(line);
        var device = new DeviceInstance { Id = "D-1", ProductRefId = old, Hardware2ProgramRefId = old, Name = "Synthetic", Address = IndividualAddress.Parse("1.1.1") };
        line.Devices.Add(device); device.ParameterValues[old + "_P-1_R-1"] = "7";
        var obj = new ComObjectInstanceRef { Id = "O-1_R-1", ComObjectId = "O-1", Number = 1, Text = "User text" };
        obj.Links.Add("G-1"); device.ComObjectRefs.Add(obj);
        var range = new GroupRange { Id = "GR-1", RangeStart = 0, RangeEnd = 65535 }; installation.MainRanges.Add(range);
        range.Addresses.Add(new() { Id = "G-1", Name = "Synthetic group", Address = GroupAddress.Parse("1/1/1"), DatapointType = "1.001" });
        project.Workbench.Groups["G-1"] = new() { Bits = 1, Secure = false };
        project.Workbench.Objects[new ObjectKey(device.Id, obj.Id).ToString()] = new() { Dpt = "1.001", Bits = 1, Flags = "CR" };
        var source = Definition(old, 1); var target = Definition(next, 2); var previews = 0;
        var window = new ReplaceProductWindow(input =>
        {
            previews++;
            return new[] { ProductReplacer.Preview(project, device, input.NewReference, input.NewProgram ?? next, source, target,
                input.ParameterMappings, input.ObjectMappings, input.ChannelMappings) };
        }, plans => ProductReplacer.Apply(project, plans.ToArray()));
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        TextBox Box(string name) => window.FindControl<TextBox>(name)!;
        var apply = window.FindControl<Button>("ApplyButton")!;
        var summary = window.FindControl<TextBlock>("SummaryText")!;
        var details = window.FindControl<ListBox>("MigrationDetails")!;
        var list = window.FindControl<ListBox>("ResultList")!;
        var parameterMapping = old + "_P-1_R-1=" + next + "_P-1_R-1";
        var objectMapping = old + "_O-1_R-1=" + next + "_O-1_R-1";
        try
        {
            window.Show(owner); await Task.Delay(80);
            Box("OldBox").Text = old; Box("NewBox").Text = next; Box("ProgramBox").Text = next;
            Box("ParameterMapBox").Text = parameterMapping; Box("ObjectMapBox").Text = objectMapping;
            // TextBox.TextChanged is dispatched asynchronously; a real click follows these edits.
            await Task.Delay(40);
            var preview = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Strings.Get("BtnPreview")));
            Click(preview); await Task.Delay(30);
            Check("real core preview exposes one device without changing the project", apply.IsEnabled && list.Items.Count == 1
                && device.ProductRefId == old && device.ComObjectRefs.Single().Number == 1 && previews == 1);
            if (!apply.IsEnabled || details.ItemsSource is null)
                throw new InvalidOperationException("Native migration preview failed: " + summary.Text + $" (callbacks={previews}, rows={list.Items.Count})");
            var rows = details.ItemsSource!.Cast<string>().ToArray();
            Check("review details expose source/target IDs, stored values, old/new numbers and retained links", rows.Any(r => r.Contains(old + "_P-1_R-1")
                    && r.Contains(next + "_P-1_R-1") && r.EndsWith(": 7", StringComparison.Ordinal))
                && rows.Any(r => r.Contains("O-1_R-1") && r.Contains("#1") && r.Contains("#2") && r.EndsWith("1", StringComparison.Ordinal)));
            Box("ParameterMapBox").Text = "unresolved=unresolved";
            await Task.Delay(30);
            Check("mapping edits invalidate the prepared plan and clear its review", !apply.IsEnabled && details.ItemsSource is null
                && list.ItemsSource is null && device.ProductRefId == old);
            Box("ParameterMapBox").Text = "malformed mapping";
            await Task.Delay(30);
            Click(preview); await Task.Delay(20);
            Check("malformed mapping is a controlled inline error before the core callback", window.IsVisible && !apply.IsEnabled
                && !string.IsNullOrWhiteSpace(summary.Text) && previews == 1 && device.ProductRefId == old);
            Box("ParameterMapBox").Text = parameterMapping + "\n" + parameterMapping;
            await Task.Delay(30);
            Click(preview); await Task.Delay(20);
            Check("duplicate source mappings are rejected without escaping the dialog", window.IsVisible && !apply.IsEnabled && previews == 1);
            Box("ParameterMapBox").Text = parameterMapping;
            await Task.Delay(30);
            Click(preview); await Task.Delay(20);
            Check("corrected mappings require a new successful preview", apply.IsEnabled && list.Items.Count == 1 && previews == 2);
            device.ParameterValues[old + "_P-1_R-1"] = "8";
            Click(apply); await Task.Delay(20);
            Check("stale-project apply is rejected inline without mutation or closing", window.IsVisible && !apply.IsEnabled
                && summary.Text?.Contains("stale", StringComparison.OrdinalIgnoreCase) == true && device.ProductRefId == old
                && device.ParameterValues[old + "_P-1_R-1"] == "8" && device.ComObjectRefs.Single().Number == 1);
            Click(preview); await Task.Delay(20); Click(apply); await Task.Delay(30);
            Check("fresh review applies once and preserves the real parameter/link state", !window.IsVisible && device.ProductRefId == next
                && device.ParameterValues[next + "_P-1_R-1"] == "8" && device.ComObjectRefs.Single().Number == 2
                && device.ComObjectRefs.Single().Links.SequenceEqual(["G-1"]) && !string.IsNullOrWhiteSpace(window.AppliedMessage));
        }
        finally { if (window.IsVisible) window.Close(); }
        return (checks, failures);
    }

    private static XDocument Definition(string program, int number) => XDocument.Parse($$"""
        <KNX xmlns="http://knx.org/xml/project/23"><ApplicationProgram Id="{{program}}" Name="Synthetic">
        <Static><ParameterTypes><ParameterType Id="{{program}}_T-1" Name="Number"><TypeNumber Type="unsignedInt" SizeInBit="16" minInclusive="0" maxInclusive="100" /></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="{{program}}_P-1" ParameterType="{{program}}_T-1" Value="1" /></Parameters>
        <ParameterRefs><ParameterRef Id="{{program}}_P-1_R-1" RefId="{{program}}_P-1" /></ParameterRefs>
        <ComObjects><ComObject Id="{{program}}_O-1" Number="{{number}}" ObjectSize="1 Bit" DatapointType="DPST-1-1" Text="Synthetic object" CommunicationFlag="Enabled" ReadFlag="Enabled" /></ComObjects>
        <ComObjectRefs><ComObjectRef Id="{{program}}_O-1_R-1" RefId="{{program}}_O-1" /></ComObjectRefs></Static>
        <Dynamic><ParameterRefRef RefId="{{program}}_P-1_R-1" /><ComObjectRefRef RefId="{{program}}_O-1_R-1" /></Dynamic>
        </ApplicationProgram></KNX>
        """);
}
