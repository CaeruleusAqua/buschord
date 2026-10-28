using System.IO.Compression;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Views;
using Knx.Catalog;
using Knx.Workbench;

internal static class ParameterPresentationDialogChecks
{
    internal static async Task Run(WorkbenchWindow owner, string theme, Action<string,bool> check)
    {
        static async Task Settle() { await Task.Delay(100); await Dispatcher.UIThread.InvokeAsync(() => { },DispatcherPriority.ContextIdle); }
        var directory=Path.Combine(Path.GetTempPath(),"buschord-parameter-presentation-ui-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        Window? window=null; ParameterPane? pane=null;
        try
        {
            const string program="M-0001_A-table";
            var project=WorkbenchJson.CreateDemo(); var device=ProjectQuery.DeviceList(project).First(); device.ProductRefId=program; device.Hardware2ProgramRefId=null; device.ParameterValues.Clear();
            var path=Path.Combine(directory,"table.knxproj"); Knxproj.Save(project,path);
            var document=XDocument.Parse("""
                <KNX><ApplicationProgram Id="M-0001_A-table"><Static><ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8"/></ParameterType><ParameterType Id="I"><TypePicture RefId="M-0001_BG-missing"/></ParameterType></ParameterTypes>
                <Parameters><Parameter Id="P1" Text="Left" Description="Synthetic field help" ParameterType="T" Value="1"/><Parameter Id="P2" Text="Right" ParameterType="T" Value="2"/><Parameter Id="P3" Text="Diagram" ParameterType="I"/></Parameters>
                <ParameterRefs><ParameterRef Id="R1" RefId="P1"/><ParameterRef Id="R2" RefId="P2"/><ParameterRef Id="R3" RefId="P3"/></ParameterRefs></Static>
                <Dynamic><ParameterBlock Id="B" Text="Synthetic page" Layout="Table" HelpContext="Help"><Rows><Row/><Row/></Rows><Columns><Column Width="50%"/><Column Width="50%"/></Columns><ParameterRefRef RefId="R1" Cell="1,1"/><ParameterRefRef RefId="R2" Cell="1,2"/><ParameterRefRef RefId="R3" Cell="2,1"/></ParameterBlock></Dynamic></ApplicationProgram></KNX>
                """);
            using(var zip=ZipFile.Open(path,ZipArchiveMode.Update)) using(var output=zip.CreateEntry("M-0001/"+program+".xml").Open()) document.Save(output);
            var vm=new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(directory,"catalog")))); vm.CreateProject(Knxproj.Load(path),path);
            var session=new ParameterEditingSession(vm); session.Load(vm.Devices.Single(r=>r.Device.Id==device.Id));
            pane=new ParameterPane(session); window=new Window { Width=900,Height=650,Content=pane }; window.Show(owner); await Settle();
            check(theme+" parameter table uses selected dynamic placements",pane.FindControl<ScrollViewer>("TableScroll")!.IsVisible && !pane.FindControl<ScrollViewer>("ParameterScroll")!.IsVisible);
            var editors=pane.GetVisualDescendants().OfType<NumericUpDown>().Where(n=>n.IsEffectivelyVisible).ToArray();
            check(theme+" parameter table displays both native editors in distinct columns",editors.Length==2 && editors.Select(n=>n.GetVisualAncestors().OfType<Grid>().First(g=>Grid.GetColumn(g)>=0 && g.DataContext is ParameterItemViewModel)).Select(Grid.GetColumn).Distinct().Count()==2);
            var first=editors.Single(n=>(n.DataContext as ParameterItemViewModel)?.Instance.Id=="R1"); first.Value=3; await Settle();
            check(theme+" parameter table draft retains native editor identity and leaves project untouched",ReferenceEquals(first,pane.GetVisualDescendants().OfType<NumericUpDown>().Single(n=>n.IsEffectivelyVisible && (n.DataContext as ParameterItemViewModel)?.Instance.Id=="R1")) && session.Rows.Single(r=>r.Instance.Id=="R1").Value=="3" && !session.Device!.ParameterValues.ContainsKey("R1"));
            first.Focus(); first.RaiseEvent(new KeyEventArgs { RoutedEvent=InputElement.KeyDownEvent,Key=Key.F1 }); await Settle();
            check(theme+" parameter F1 opens readable context help for focused field",pane.FindControl<Border>("HelpPanel")!.IsVisible && pane.FindControl<TextBox>("HelpText")!.Text!.Contains("Synthetic field help"));
            pane.FindControl<Border>("HelpPanel")!.GetVisualDescendants().OfType<Button>().Single(button=>Equals(button.Content,Strings.Get("BtnClose"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            pane.FindControl<CheckBox>("TableLayoutToggle")!.IsChecked=false; await Settle();
            check(theme+" complete field-list fallback preserves drafts and picture is read-only",pane.FindControl<ScrollViewer>("ParameterScroll")!.IsVisible && session.Rows.Single(r=>r.Instance.Id=="R1").Value=="3" && session.Rows.Single(r=>r.Instance.Id=="R3") is {Kind:ParameterKind.Picture,IsEnabled:false});
            check(theme+" missing declared picture has a localized explanation",pane.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.IsEffectivelyVisible && t.Text==WorkbenchStrings.Get("ParameterResourceMissing")));
        }
        finally { window?.Close(); pane?.Dispose(); Directory.Delete(directory,true); }
    }
}
