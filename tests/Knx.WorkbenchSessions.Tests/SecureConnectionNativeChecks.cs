using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Workbench;
using Knx.Bus;
using Knx.Bus.Connections;

internal static class SecureConnectionNativeChecks
{
    [STAThread]
    public static int Run(string language)
    {
        var root=Path.Combine(Path.GetTempPath(),"buschord-secure-native-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS",Path.Combine(root,"settings.json"));
        var fixture=(string)typeof(ProtectedKnxKeyringFixture).GetField(nameof(ProtectedKnxKeyringFixture.Xml))!.GetRawConstantValue()!;
        var path=Path.Combine(root,"synthetic.knxkeys");File.WriteAllText(path,fixture);
        var checks=0;var failures=0;void Check(string name,bool ok){checks++;Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(!ok)failures++;}
        App.ForcedLanguage=language;App.ForcedTheme="light";App.TestWindowFactory=()=>new Window{Title="Synthetic secure connection checks",Width=420,Height=240};
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().AfterSetup(_=>DispatcherTimer.RunOnce(async()=>
        {
            var desktop=(IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;desktop.ShutdownMode=ShutdownMode.OnExplicitShutdown;var shell=desktop.MainWindow!;
            try
            {
                var profile=KnxConnectionProfile.IpSecure(new(IPAddress.Parse("192.0.2.194"),3671),new(path,"Synthetic",new(1,1,0),2,new(1,1,240)));
                var id=new object();var backend=new Backend();await using var connection=new ReadOnlyWorkbenchConnection(id,()=>id,_=>backend);
                var dialog=new ConnectionWindow(connection,new(profile),"Synthetic");var showing=dialog.ShowDialog(shell);await Until(()=>dialog.IsVisible);
                Check("secure mode and interface are explicit",Find<ComboBox>(dialog,"LiveTransportMode").SelectedIndex==1&&Find<TextBox>(dialog,"LiveTunnelAddress").IsReadOnly);
                Check("transport selector has localized accessible name",ControlAutomationPeer.CreatePeerForElement(Find<ComboBox>(dialog,"LiveTransportMode"))?.GetName()==SecureConnectionStrings.Get("Mode"));
                Check("unconnected secure mode never claims authentication",!Find<TextBlock>(dialog,"LiveConnectionStatus").Text!.Contains(SecureConnectionStrings.Get("Authenticated")));
                Click(Find<Button>(dialog,"LiveConnect"));await dialog.OperationCompletion;
                Check("authenticated status comes from backend proof",connection.Snapshot.GatewayAuthenticated&&Find<TextBlock>(dialog,"LiveConnectionStatus").Text!.Contains(SecureConnectionStrings.Get("Authenticated")));
                Check("connected transport cannot be switched silently",!Find<ComboBox>(dialog,"LiveTransportMode").IsEnabled&&!Find<Button>(dialog,"LiveConfigureSecure").IsEnabled);
                foreach(var theme in new[]{ThemeVariant.Light,ThemeVariant.Dark})
                {Application.Current.RequestedThemeVariant=theme;await Task.Delay(80);Save(dialog,Path.Combine(root,$"secure-connection-{language}-{theme}.png"));Check("secure dialog remains usable "+theme,Find<Button>(dialog,"LiveDisconnect").IsEnabled);}
                dialog.Close();await showing;await connection.DisconnectAsync();Check("secure close/disconnect joins backend",backend.Disposals==1);
                var project=Knx.Workbench.WorkbenchJson.CreateDemo();var device=ProjectQuery.DeviceList(project).First();var sid=new object();
                await using var m112=new M112CommissioningSession(()=>new(project,sid,false,false),device.Id,new M112CommissioningChecks.FakeBackend(device.Address.ToString()));
                var cold=new M112CommissioningWindow(m112,_=>Task.FromResult<M112BaselineSelection?>(null));var coldShowing=cold.ShowDialog(shell);await Until(()=>cold.IsVisible);
                Check("cold M112 requires explicit transport selection",Find<ComboBox>(cold,"M112TransportMode").SelectedIndex==-1&&OptionsRefused(cold));
                Find<ComboBox>(cold,"M112TransportMode").SelectedIndex=1;Check("secure M112 cannot fall back without a keyring",OptionsRefused(cold));cold.Close();await coldShowing;
                var ready=new M112CommissioningWindow(m112,_=>Task.FromResult<M112BaselineSelection?>(null),new(profile));var readyShowing=ready.ShowDialog(shell);await Until(()=>ready.IsVisible);
                Check("M112 retains exact secure tuple and readonly source",Options(ready).Profile.EquivalentTo(profile)&&Find<TextBox>(ready,"M112TunnelAddress").IsReadOnly);ready.Close();await readyShowing;
                var selector=new SecureConnectionWindow(shell,"BusChord synthetic keyring");var selectedShowing=selector.ShowDialog(shell);await Until(()=>selector.IsVisible);Find<TextBox>(selector,"SecureKeyringPath").Text=path;
                Click(Find<Button>(selector,"SecureVerify"));var prompt=await Prompt(desktop,shell,selector);var password=prompt.GetVisualDescendants().OfType<TextBox>().Single();
                Check("keyring input is masked and ephemeral",password.PasswordChar!='\0');password.Text="pwd";Click(prompt.GetVisualDescendants().OfType<Button>().Single(button=>button.IsDefault));await selector.OperationCompletion;
                Check("offline signature verification exposes only complete interface choices",Find<ComboBox>(selector,"SecureInterfaceChoice").ItemCount==1&&Find<Button>(selector,"SecureAccept").IsEnabled);
                var hash=Find<TextBox>(selector,"SecureKeyringFingerprint");Check("full source fingerprint is readonly and copyable",hash.IsReadOnly&&hash.Text?.Length==64);
                await Task.Delay(80);Check("selected host user and tunnel metadata is visible",selector.GetVisualDescendants().OfType<TextBlock>().Any(text=>text.Text?.Contains("Host 1.2.0") == true));
                Save(selector,Path.Combine(root,$"keyring-selection-{language}.png"));Click(Find<Button>(selector,"SecureAccept"));await selectedShowing;
                Check("selected tuple binds verified file fingerprint",selector.Selection is {UserId:4,HostAddress.Raw:0x1200,TunnelAddress.Raw:0x120a,ExpectedSourceFingerprint.Length:64});
                Check("masked text is cleared after prompt closes",password.Text=="");
                var cancelling=new SecureConnectionWindow(shell,"BusChord synthetic keyring");var cancelShowing=cancelling.ShowDialog(shell);await Until(()=>cancelling.IsVisible);Find<TextBox>(cancelling,"SecureKeyringPath").Text=path;Click(Find<Button>(cancelling,"SecureVerify"));await Prompt(desktop,shell,cancelling);
                cancelling.Close();await cancelShowing.WaitAsync(TimeSpan.FromSeconds(5));await cancelling.OperationCompletion;
                Check("closing offline selector cancels and joins masked prompt/import",!cancelling.IsVisible&&cancelling.Selection is null&&desktop.Windows.Count(window=>window!=shell&&window.IsVisible)==0);
            }
            catch(Exception error){failures++;Console.WriteLine("FAIL secure native exception: "+error);}
            finally {foreach(var window in desktop.Windows.Where(window=>window!=shell).ToArray())window.Close();shell.Close();Console.WriteLine($"Native secure {language}: {checks} checks, {failures} failures; screenshots {root}");desktop.Shutdown(failures==0?0:1);}
        },TimeSpan.FromMilliseconds(180))).StartWithClassicDesktopLifetime([]);
        return failures==0?0:1;
    }
    private static async Task<Window> Prompt(IClassicDesktopStyleApplicationLifetime desktop,Window shell,Window selector)
    {await Until(()=>desktop.Windows.Any(window=>window!=shell&&window!=selector&&window.IsVisible));return desktop.Windows.Last(window=>window!=shell&&window!=selector&&window.IsVisible);}
    private static ReadOnlyConnectionOptions Options(M112CommissioningWindow window)=>(ReadOnlyConnectionOptions)typeof(M112CommissioningWindow).GetMethod("Options",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
    private static bool OptionsRefused(M112CommissioningWindow window){try{Options(window);return false;}catch(TargetInvocationException error)when(error.InnerException is InvalidOperationException){return true;}}
    private static void Save(Window window,string path){using var bitmap=new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height));bitmap.Render(window);bitmap.Save(path,PngBitmapEncoderOptions.Default);}
    private static T Find<T>(Window window,string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(control=>control.Name==name);
    private static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition){for(var i=0;i<500&&!condition();i++)await Task.Delay(10);if(!condition())throw new TimeoutException("Synthetic native deadline.");}
    private sealed class Backend:IReadOnlyWorkbenchBackend
    {
        public int Disposals;public bool IsConnected{get;private set;}public IndividualAddress AssignedAddress=>new(1,1,240);
        public KnxConnectionSecurity? Security=>new(KnxTransportKind.IpSecureUdp,true,AssignedAddress,new string('A',64));
        public event Action<BusTelegram>? TelegramReceived{add{}remove{}}public event Action<BusConnectionState>? StateChanged{add{}remove{}}
        public Task ConnectAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();IsConnected=true;return Task.CompletedTask;}
        public ValueTask DisposeAsync(){IsConnected=false;Disposals++;return ValueTask.CompletedTask;}
    }
}

internal static class ProtectedKnxKeyringFixture
{
    // Existing independently signed synthetic protocol vector; labels and addresses are synthetic.
    public const string Xml="""
        <Keyring xmlns="http://knx.org/xml/keyring/1" Project="BusChord synthetic keyring" CreatedBy="Synthetic vector" Created="2019-06-11T06:45:22" Signature="O6g27O2hyVUL9PI0O3E8zA==">
          <Backbone MulticastAddress="224.0.23.12" Latency="1000" Key="VGnz2DbdiMqN5EE4I7tqLw==" />
          <Interface Type="Tunneling" Host="1.2.0" UserID="4" IndividualAddress="1.2.10" Password="DQJOj7CrytX+4EV27SINp3xXa7HEfJQu+XiCt9U5AU8=" Authentication="//pj5KplK+4H7gcYVefuuCumcPVI0BBR/MXfFtaat2k=">
            <Group Address="4660" Senders="1.2.1 1.2.2" />
          </Interface>
          <Interface Type="USB" IndividualAddress="1.2.11"><Group Address="4660" Senders="1.2.2" /></Interface>
          <Interface Type="Backbone" IndividualAddress="1.2.12"><Group Address="17185" /></Interface>
          <GroupAddresses><Group Address="4660" Key="iA2KpI19ZlW0jseoXSycAg==" /><Group Address="17185" Key="pvyOR3j9CpJw9R2/WWVCZQ==" /></GroupAddresses>
          <Devices><Device IndividualAddress="1.2.0" ToolKey="GI0PDDbpgQUZK2JkTvR8pA==" ManagementPassword="ezGR1sf4vsWKWS8JMIJuzmrtEG2N9WLdyTDrwIZbNuM=" Authentication="qEUJHMW48qXne6MySIUPlWwd7RHLmh6gnS/EIzaTIew=" SequenceNumber="108" /></Devices>
        </Keyring>
        """;
}
