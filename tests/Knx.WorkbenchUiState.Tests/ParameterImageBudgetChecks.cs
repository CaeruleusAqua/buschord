using System.IO.Compression;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Views;
using Knx.Catalog;
using Knx.Workbench;

internal static class ParameterImageBudgetChecks
{
    internal static async Task Run(WorkbenchWindow owner, string theme, Action<string,bool> check)
    {
        static async Task Settle() { await Task.Delay(80); await Dispatcher.UIThread.InvokeAsync(() => { },DispatcherPriority.ContextIdle); }
        var directory=Path.Combine(Path.GetTempPath(),"buschord-image-budget-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        Window? window=null;
        try
        {
            byte[] png;
            using(var generated=new RenderTargetBitmap(new PixelSize(2000,4000),new Vector(96,96)))
            using(var buffer=new MemoryStream()) { generated.Save(buffer);png=buffer.ToArray(); }
            byte[] smallPng;
            using(var generated=new RenderTargetBitmap(new PixelSize(2,2),new Vector(96,96)))
            using(var buffer=new MemoryStream()) { generated.Save(buffer);smallPng=buffer.ToArray(); }
            var project=WorkbenchJson.CreateDemo();var device=ProjectQuery.DeviceList(project).First();device.ProductRefId="M-0001_A-budget";device.Hardware2ProgramRefId=null;
            var path=Path.Combine(directory,"images.knxproj");Knxproj.Save(project,path);
            var application=XDocument.Parse("""
                <KNX><ApplicationProgram Id="M-0001_A-budget"><Static><ParameterTypes>
                <ParameterType Id="I0"><TypePicture RefId="M-0001_BG-0"/></ParameterType><ParameterType Id="I1"><TypePicture RefId="M-0001_BG-1"/></ParameterType><ParameterType Id="I2"><TypePicture RefId="M-0001_BG-2"/></ParameterType></ParameterTypes>
                <Parameters><Parameter Id="P0" Text="Shared first" ParameterType="I0"/><Parameter Id="P1" Text="Shared second" ParameterType="I0"/><Parameter Id="P2" Text="Second resource" ParameterType="I1"/><Parameter Id="P3" Text="Budget fallback" ParameterType="I2"/></Parameters>
                <ParameterRefs><ParameterRef Id="R0" RefId="P0"/><ParameterRef Id="R1" RefId="P1"/><ParameterRef Id="R2" RefId="P2"/><ParameterRef Id="R3" RefId="P3"/></ParameterRefs></Static></ApplicationProgram></KNX>
                """);
            using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
            {
                using(var output=zip.CreateEntry("M-0001/M-0001_A-budget.xml").Open())application.Save(output);
                var nextApplication=new XDocument(application);nextApplication.Descendants("ApplicationProgram").Single().SetAttributeValue("Id","M-0001_A-budget2");
                var nextPicture=nextApplication.Descendants("ParameterType").Single(e=>(string?)e.Attribute("Id")=="I0").Element("TypePicture")!;
                nextPicture.SetAttributeValue("RefId","M-0001_BG-small");nextPicture.SetAttributeValue("HorizontalAlignment","Right");
                nextApplication.Descendants("Parameter").Single(e=>(string?)e.Attribute("Id")=="P0").SetAttributeValue("Text","New product picture");
                using(var output=zip.CreateEntry("M-0001/M-0001_A-budget2.xml").Open())nextApplication.Save(output);
                using(var output=zip.CreateEntry("M-0001/Baggages.xml").Open())XDocument.Parse("<KNX><Baggages><Baggage Id='M-0001_BG-0' Name='0.png'/><Baggage Id='M-0001_BG-1' Name='1.png'/><Baggage Id='M-0001_BG-2' Name='2.png'/><Baggage Id='M-0001_BG-small' Name='small.png'/></Baggages></KNX>").Save(output);
                for(var i=0;i<3;i++){using var output=zip.CreateEntry("M-0001/Baggages/"+i+".png").Open();output.Write(png);}
                using(var output=zip.CreateEntry("M-0001/Baggages/small.png").Open())output.Write(smallPng);
            }
            var vm=new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(directory,"catalog"))));vm.CreateProject(Knxproj.Load(path),path);
            var session=new ParameterEditingSession(vm);session.Load(vm.Devices.Single(r=>r.Device.Id==device.Id));
            var controls=session.Rows.Select(r=>ParameterTemplateSelector.Instance.Build(r)!).ToArray();
            var hosts=controls.Select(control=>new Border{Child=control}).ToArray();
            var grid=new Grid{ColumnDefinitions=new("*,*,*,*")};
            for(var i=0;i<hosts.Length;i++){Grid.SetColumn(hosts[i],i);grid.Children.Add(hosts[i]);}
            window=new(){Width=1000,Height=480,Content=grid};window.Show(owner);await Settle();
            var images=controls.Select(c=>c.GetVisualDescendants().OfType<Image>().Single()).ToArray();var first=images[0].Source;
            check(theme+" image budget: identical resources share one decoded bitmap",first is not null&&ReferenceEquals(first,images[1].Source));
            check(theme+" image budget: two 8M-pixel resources fit and third has localized fallback",images[2].Source is not null&&images[3].Source is null
                &&controls[3].GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text==WorkbenchStrings.Get("ParameterImageBudget")));
            grid.Children.Remove(hosts[0]);await Settle();
            check(theme+" image budget: detaching one borrower preserves the other",ReferenceEquals(first,images[1].Source));
            grid.Children.Remove(hosts[1]);grid.Children.Remove(hosts[3]);grid.Children.Add(hosts[3]);await Settle();
            check(theme+" image budget: last borrower releases capacity for the rejected resource",images[3].Source is not null);
            hosts[2].IsVisible=false;grid.Children.Add(hosts[0]);await Settle();
            check(theme+" image budget: hidden ancestors release capacity and reopened resource gets a fresh bitmap",images[2].Source is null&&images[0].Source is not null&&!ReferenceEquals(first,images[0].Source));
            controls[0].IsVisible=false;await Settle();
            check(theme+" image budget: hiding the picture panel itself also releases its lease",images[0].Source is null);
            var retained=session.Rows[0];session.Device!.ProductRefId="M-0001_A-budget2";session.Load(new DeviceRow(session.Device));controls[0].IsVisible=true;await Settle();
            check(theme+" image rebind: same row identity loads only the new product resource and updates its accessible presentation",
                ReferenceEquals(retained,session.Rows[0])&&images[0].Source is Bitmap{PixelSize.Width:2,PixelSize.Height:2}
                &&!ReferenceEquals(first,images[0].Source)&&images[0].HorizontalAlignment==Avalonia.Layout.HorizontalAlignment.Right
                &&AutomationProperties.GetName(images[0])=="New product picture");
        }
        finally{window?.Close();Directory.Delete(directory,true);}
    }
}
