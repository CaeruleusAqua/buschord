using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Knx;
using Knx.App.Services;
using Knx.App.Views;
using Knx.Scripting;

internal static class TimeParameterChecks
{
    private const string ApplicationId = "M-0001_A-0100-10-0000";

    private static ParameterType TimeType(string unit = "Seconds", string min = "0", string max = "28800", string size = "16", string? hint = "Duration_hhmmss")
    {
        var element = XElement.Parse("<ParameterType Id='T' Name='Duration'><TypeTime /></ParameterType>");
        var shape = element.Element("TypeTime")!;
        shape.SetAttributeValue("Unit", unit); shape.SetAttributeValue("minInclusive", min);
        shape.SetAttributeValue("maxInclusive", max); shape.SetAttributeValue("SizeInBit", size);
        shape.SetAttributeValue("UIHint", hint);
        return Ksxml.ParseParameterType(element)!;
    }

    private static XDocument Document() => XDocument.Parse($$"""
        <KNX><ManufacturerData><Manufacturer RefId="M-0001"><ApplicationPrograms><ApplicationProgram Id="{{ApplicationId}}">
        <Static><ParameterTypes>
        <ParameterType Id="T" Name="Duration"><TypeTime SizeInBit="16" Unit="Seconds" minInclusive="0" maxInclusive="28800" UIHint="Duration_hhmmss" /></ParameterType>
        <ParameterType Id="IP4" Name="IPv4"><TypeIPAddress AddressType="HostAddress" Version="IPv4" /></ParameterType>
        <ParameterType Id="IP6" Name="IPv6"><TypeIPAddress AddressType="HostAddress" Version="IPv6" /></ParameterType>
        </ParameterTypes><Parameters>
        <Parameter Id="P-T" Name="Delay" ParameterType="T" Value="120" />
        <Parameter Id="P-IP4" Name="IPv4 address" ParameterType="IP4" Value="192.0.2.1" />
        <Parameter Id="P-IP6" Name="IPv6 address" ParameterType="IP6" Value="2001:db8::1" />
        </Parameters><ParameterRefs><ParameterRef Id="R-T" RefId="P-T" />
        <ParameterRef Id="R-IP4" RefId="P-IP4" /><ParameterRef Id="R-IP6" RefId="P-IP6" />
        </ParameterRefs></Static><Dynamic><ParameterRefRef RefId="R-T" />
        <ParameterRefRef RefId="R-IP4" /><ParameterRefRef RefId="R-IP6" /></Dynamic>
        </ApplicationProgram></ApplicationPrograms></Manufacturer></ManufacturerData></KNX>
        """);

    private static (MainWindowViewModel Vm, string Path) Fixture(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "time-parameter.knxproj");
        var project = new Project { Id = "P-TIME", Name = "Time and IP" };
        var installation = new Installation { Id = "I" };
        var area = new Area { Id = "A", Address = 1 };
        var line = new Line { Id = "L", Address = 1 };
        line.Devices.Add(new DeviceInstance { Id = "D", Address = new(1, 1, 1), ProductRefId = ApplicationId });
        area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
        Knxproj.Save(project, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        using (var stream = zip.CreateEntry("M-0001/" + ApplicationId + ".xml").Open()) Document().Save(stream);
        var vm = new MainWindowViewModel(); vm.CreateProject(Knxproj.Load(path), null); vm.ProjectPath = path;
        return (vm, path);
    }

    public static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "TIME " + name); if (!ok) failures++; }
        var time = TimeType();
        var copy = new ParameterType(time);
        Check("TypeTime preserves its distinct storage unit and original hint in detached copies", time.DataType == ParameterDataType.Time
            && time.TimeUnit == "Seconds" && time.TimeUIHint == "Duration_hhmmss" && time.Unit is null
            && copy.TimeUnit == time.TimeUnit && copy.TimeUIHint == time.TimeUIHint && copy.UnsupportedReason is null);
        Check("MDT Seconds duration accepts exact inclusive limits", ParameterValidation.TryValidate(time, "0", out _)
            && ParameterValidation.TryValidate(time, "28800", out _) && ParameterValidation.TryValidate(time, "3661", out _));
        Check("duration rejects out-of-range fractional and clock-formatted storage", new[] { "-1", "28801", "1.5", "01:01:01", "NaN", "9223372036854775808" }
            .All(value => !ParameterValidation.TryValidate(time, value, out _)));
        foreach (var (unit, label) in new[] { ("Hours", "h"), ("Minutes", "min"), ("Seconds", "s"), ("HundredMilliseconds", "100 ms"),
            ("TenMilliseconds", "10 ms"), ("Milliseconds", "ms"), ("PackedSecondsAndMilliseconds", "ms"),
            ("PackedDaysHoursMinutesAndSeconds", "s"), ("PackedMinutesSecondsAndMilliseconds", "ms") })
        {
            var type = TimeType(unit);
            var row = new ParameterItemViewModel(new ParameterInstance { Id = "T", Name = "Duration", Value = "120", ParameterType = type }, type);
            Check(unit + " retains canonical numeric storage and a matching unit label", type.UnsupportedReason is null && row.IsEnabled
                && row.Kind == ParameterKind.Duration && row.Value == "120" && row.Unit == label);
        }
        var exact = TimeType("Milliseconds", "9223372036854775806", "9223372036854775807", "64", null);
        Check("Int64 range comparison remains exact beyond double precision", ParameterValidation.TryValidate(exact, "9223372036854775806", out _)
            && ParameterValidation.TryValidate(exact, "9223372036854775807", out _)
            && !ParameterValidation.TryValidate(exact, "9223372036854775805", out _));
        Check("duration range labels retain the exact original Int64 limits", new ParameterItemViewModel(
            new ParameterInstance { Id = "LIMIT", Name = "Duration limits", Value = "9223372036854775806", ParameterType = exact }, exact)
            .RangeText == "9223372036854775806..9223372036854775807");
        var negative = TimeType("Seconds", "-2", "2", "64", null);
        Check("declared signed Int64 limits are preserved without inventing a clock domain", ParameterValidation.TryValidate(negative, "-2", out _)
            && !ParameterValidation.TryValidate(negative, "-3", out _));
        Check("schema declaration errors stay unsupported", new[] { TimeType(size: "7"), TimeType(size: "65"), TimeType(size: "0"),
            TimeType(unit: "Unknown"), TimeType(unit: ""), TimeType(min: "1.5"), TimeType(min: "10", max: "9"),
            TimeType(max: "9223372036854775808"), TimeType(hint: "DatePicker") }.All(type => type.UnsupportedReason is not null
                && !ParameterValidation.TryValidate(type, "1", out _)));
        var missingBounds = XElement.Parse("<ParameterType Id='T' Name='Duration'><TypeTime SizeInBit='16' Unit='Seconds'/></ParameterType>");
        Check("TypeTime requires both XML Int64 bounds", Ksxml.ParseParameterType(missingBounds)!.UnsupportedReason is not null);
        var legacy = new ParameterType { Id = "LEGACY", Name = "Legacy", DataType = ParameterDataType.TimeWithUnit, TimeUnit = "Minutes" };
        Check("legacy TimeWithUnit remains distinct and unsupported", !ParameterValidation.TryValidate(legacy, "1", out _)
            && new ParameterItemViewModel(new ParameterInstance { Id = "L", Name = "Legacy", ParameterType = legacy }, legacy).Kind == ParameterKind.ReadOnly);

        var parsed = Ksxml.Parse(Document());
        var rows = ParameterItemViewModel.Build(parsed.Instances, parsed.Types);
        Check("known IPv4 and IPv6 declarations enable their dedicated editors", rows.Where(row => row.Type!.DataType == ParameterDataType.IPAddress)
            .All(row => row.IsEnabled && row.Kind == ParameterKind.IpAddress));
        var ip4 = parsed.Types.Single(type => type.Id == "IP4"); var ip6 = parsed.Types.Single(type => type.Id == "IP6");
        Check("IP editor values obey the declared family", ParameterValidation.TryValidate(ip4, "192.0.2.7", out _)
            && ParameterValidation.TryValidate(ip6, "2001:db8::7", out _) && !ParameterValidation.TryValidate(ip4, "2001:db8::7", out _)
            && !ParameterValidation.TryValidate(ip6, "192.0.2.7", out _) && !ParameterValidation.TryValidate(ip4, "127.1", out _)
            && !ParameterValidation.TryValidate(ip6, "fe80::1%3", out _));

        using var runtime = new IsolatedScriptRuntime();
        var calculation = Document();
        var staticPart = calculation.Descendants("Static").Single();
        staticPart.Add(XElement.Parse("""
            <ParameterCalculations><ParameterCalculation Language="JavaScript"><LParameters><ParameterRefRef RefId="R-T" AliasName="duration" /></LParameters>
            <RParameters><ParameterRefRef RefId="R-OUT" AliasName="result" /></RParameters><LRTransformation>result=duration*2;</LRTransformation>
            <RLTransformation>duration=result/2;</RLTransformation></ParameterCalculation></ParameterCalculations>
            """));
        staticPart.Element("Parameters")!.Add(new XElement("Parameter", new XAttribute("Id", "P-OUT"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "0")));
        staticPart.Element("ParameterRefs")!.Add(new XElement("ParameterRef", new XAttribute("Id", "R-OUT"), new XAttribute("RefId", "P-OUT")));
        var evaluated = DynamicParameters.Evaluate(calculation.Root!, scripts: runtime);
        var reverse = DynamicParameters.Evaluate(calculation.Root!, new Dictionary<string, string?> { ["R-OUT"] = "7200" }, runtime, "R-OUT");
        Check("long durations enter LR/RL scripts as numbers in their declared units", evaluated.ScriptFailures == 0 && evaluated.Parameters["R-OUT"].Value == "240"
            && reverse.ScriptFailures == 0 && reverse.Parameters["R-T"].Value == "3600");
        var function = new XDocument(calculation);
        function.Descendants("Static").Single().Add(new XElement("Script",
            "function forward(input,output,context){output.result=input.duration*2;} function reverse(input,output,context){output.duration=input.result/2;}"));
        var functionDeclaration = function.Descendants("ParameterCalculation").Single();
        functionDeclaration.Elements().Where(element => element.Name.LocalName is "LRTransformation" or "RLTransformation").Remove();
        functionDeclaration.SetAttributeValue("LRTransformationFunc", "forward"); functionDeclaration.SetAttributeValue("RLTransformationFunc", "reverse");
        var functionState = DynamicParameters.Evaluate(function.Root!, scripts: runtime);
        Check("function-wrapper calculations use the same canonical long duration input", functionState.ScriptFailures == 0 && functionState.Parameters["R-OUT"].Value == "240");
        var outOfRange = new XDocument(calculation); outOfRange.Descendants("LRTransformation").Single().Value = "result=28801;";
        var invalidState = DynamicParameters.Evaluate(outOfRange.Root!, scripts: runtime);
        Check("calculated duration results pass the same range validator atomically", invalidState.ScriptFailures > 0 && invalidState.Parameters["R-OUT"].Value == "0");
        var imprecise = new XDocument(calculation);
        imprecise.Descendants("TypeTime").Single().SetAttributeValue("SizeInBit", "64");
        imprecise.Descendants("TypeTime").Single().SetAttributeValue("maxInclusive", long.MaxValue.ToString(CultureInfo.InvariantCulture));
        imprecise.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-T").SetAttributeValue("Value", "9007199254740992");
        Check("time inputs beyond exact JavaScript precision are refused", DynamicParameters.Evaluate(imprecise.Root!, scripts: runtime).ScriptFailures > 0);
        var unknown = new XDocument(calculation); unknown.Descendants("TypeTime").Single().Attribute("Unit")!.Remove();
        Check("unitless legacy Time does not silently enter numeric calculations", DynamicParameters.Evaluate(unknown.Root!, scripts: runtime).ScriptFailures > 0);

        var (vm, _) = Fixture(System.IO.Path.Combine(directory, "time-model"));
        var session = new ParameterEditingSession(vm); session.Load(vm.Devices[0]);
        foreach (var changed in new[] { "TimeUnit", "TimeUIHint", "AddressType", "AddressVersion" })
        {
            var row = session.Rows.Single(row => row.Type!.Id == (changed.StartsWith("Time", StringComparison.Ordinal) ? "T" : "IP4"));
            row.Value = changed.StartsWith("Time", StringComparison.Ordinal) ? "121" : "192.0.2.9";
            typeof(ParameterType).GetProperty(changed)!.SetValue(row.Type, changed switch
            { "TimeUnit" => "Minutes", "TimeUIHint" => "Time_ss", "AddressType" => "MulticastAddress", _ => "IPv6" });
            session.Refresh();
            Check(changed + " changes invalidate a detached draft on refresh", !session.HasUnappliedChanges && session.DraftNotice is { Length: > 0 }
                && vm.Devices[0].Device.ParameterValues.Count == 0 && session.Rows.Single(r => r.Type!.Id == row.Type!.Id).Value == (changed.StartsWith("Time", StringComparison.Ordinal) ? "120" : "192.0.2.1"));
            session.AcknowledgeNotice();
        }
        Console.WriteLine($"TIME CHECKS {failures} failure(s)");
        return failures;
    }

    public static async Task<int> RunNative(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "NATIVE TIME " + name); if (!ok) failures++; }
        var (vm, path) = Fixture(System.IO.Path.Combine(directory, "time-native"));
        using var pane = new ParameterPane(vm);
        var window = new Window { Content = pane, Width = 740, Height = 480 };
        try
        {
            pane.Load(vm.Devices[0]); window.Show(); await Task.Delay(200);
            var duration = pane.GetVisualDescendants().OfType<NumericUpDown>().Single();
            var ipv4 = pane.GetVisualDescendants().OfType<TextBox>().Single(text => Equals(text.Watermark, "0.0.0.0"));
            var ipv6 = pane.GetVisualDescendants().OfType<TextBox>().Single(text => Equals(text.Watermark, "::"));
            Check("duration uses integer numeric editing with the canonical unit", duration.FormatString == "0" && duration.Value == 120m
                && pane.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "s"));
            var wideType = TimeType("Milliseconds", "0", long.MaxValue.ToString(CultureInfo.InvariantCulture), "64", null);
            var wideRow = new ParameterItemViewModel(new ParameterInstance { Id = "W", Name = "Wide duration", ParameterType = wideType,
                Value = long.MaxValue.ToString(CultureInfo.InvariantCulture) }, wideType);
            var wideControl = (StackPanel)SessionParameterTemplate.Instance.Build(wideRow)!;
            var wideEditor = (NumericUpDown)wideControl.Children[0];
            wideEditor.Value = 9223372036854775806m;
            await Task.Delay(50);
            Check("duration numeric controls retain Int64 precision without double conversion", wideRow.Value == "9223372036854775806"
                && wideEditor.FormatString == "0");
            duration.Value = 28801m; await Task.Delay(80);
            Check("out-of-range native draft blocks Apply and leaves the project unchanged", pane.Session.HasValidationErrors && !pane.Apply()
                && vm.Devices[0].Device.ParameterValues.Count == 0);
            duration.Value = 3661m; ipv4.Text = "2001:db8::2"; await Task.Delay(80);
            Check("wrong IP family blocks the native draft", pane.Session.HasValidationErrors && !pane.Apply());
            ipv4.Text = "192.0.2.2"; ipv6.Text = "2001:db8::2"; await Task.Delay(80);
            var review = pane.Session.ReviewChanges();
            Check("native numeric/IP events retain wire values through reviewed Apply", !pane.Session.HasValidationErrors && review.IsValid && pane.Apply()
                && vm.Devices[0].Device.ParameterValues.GetValueOrDefault("R-T") == "3661"
                && vm.Devices[0].Device.ParameterValues.GetValueOrDefault("R-IP4") == "192.0.2.2"
                && vm.Devices[0].Device.ParameterValues.GetValueOrDefault("R-IP6") == "2001:db8::2");
            vm.UndoCommand.Execute(null); pane.Load(vm.Devices[0]);
            Check("one Undo restores the whole native duration/IP batch", vm.Devices[0].Device.ParameterValues.Count == 0
                && pane.Session.Rows.Single(row => row.Type!.Id == "T").Value == "120");
            vm.RedoCommand.Execute(null); vm.SaveProject();
            var reopened = ProjectQuery.DeviceList(Knxproj.Load(path)).Single();
            Check("save/reopen preserves canonical duration and IPv6 strings", reopened.ParameterValues.GetValueOrDefault("R-T") == "3661"
                && reopened.ParameterValues.GetValueOrDefault("R-IP6") == "2001:db8::2");
        }
        finally { window.Close(); }
        return failures;
    }
}
