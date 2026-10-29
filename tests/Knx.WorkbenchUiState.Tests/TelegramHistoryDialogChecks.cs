using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using System.Globalization;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Workbench;
using Knx.Bus.History;
using Knx.Workbench;

internal static class TelegramHistoryDialogChecks
{
    internal static async Task Run(WorkbenchWindow owner,string theme,Action<string,bool> check)
    {
        static async Task Settle(){await Task.Delay(80);await Dispatcher.UIThread.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);}
        static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void Shot(Window window,string name)
        {
            if(Environment.GetEnvironmentVariable("BUSCHORD_HISTORY_SHOTS") is not { Length:>0 } directory)return;
            Directory.CreateDirectory(directory);
            using var bitmap=new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width),(int)Math.Ceiling(window.Bounds.Height)),new Vector(96,96));
            bitmap.Render(window);bitmap.Save(Path.Combine(directory,name+".png"));
        }
        async Task Until(Func<bool> predicate,Func<string>? diagnostic=null)
        {for(var i=0;i<80;i++){if(predicate())return;await Settle();}throw new TimeoutException("History dialog did not settle. "+diagnostic?.Invoke());}
        var directory=Path.Combine(Path.GetTempPath(),"buschord-history-dialog-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        TelegramHistoryWindow? window=null;
        using var store=new FileTelegramHistoryStore(directory);
        var project=ProjectWizard.Create(new(){TopologyTemplate=NewProjectTopologyTemplate.TpFromOne});object identity=new();
        project.Installations[0].MainRanges[0].Ranges[0].Addresses.Add(new(){Id="history-group",Name="History group",Address=GroupAddress.Parse("1/1/1"),DatapointType="1.001"});
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new(){Id="history-device",Name="History source",ProductRefId="Synthetic",Address=new(1,1,1)});
        WorkbenchTarget? navigated=null;
        var source=new DelayedStore(store);var capture=new MonitorHistoryCapture(source);
        using var monitor=new MonitorSession(new(),()=>project,null,capture);
        try
        {
            var start=DateTimeOffset.UtcNow.AddMinutes(-5);
            var interval=await store.BeginCaptureAsync("synthetic-interface",start.AddSeconds(-1));
            for(var i=0;i<205;i++)await store.AppendAsync(new(start.AddSeconds(i),"1.1.1","1/1/1",true,i%2==0?"GroupValueWrite":"GroupValueRead","01","Real","synthetic-interface",ProjectId:project.Guid,CaptureId:interval.Id));
            await store.EndCaptureAsync(interval.Id,start.AddSeconds(206),"synthetic-stop");
            window=new(monitor,()=>project,()=>identity,navigate:target=>navigated=target,store:source);window.Show(owner);
            T Control<T>(string name)where T:Control=>window.GetVisualDescendants().OfType<T>().Single(c=>c.Name==name);
            await Until(()=>Control<ListBox>("HistoryRows").Items.Count>0,()=>Control<TextBlock>("HistoryStatus").Text??"");
            var list=Control<ListBox>("HistoryRows");
            check(theme+" history table keeps a useful viewport and always-visible capture coverage ("+list.Bounds.Height+" px)",list.Bounds.Height>=120&&Control<TextBlock>("HistoryCoverageCaption").IsEffectivelyVisible);
            Shot(window,theme+"-history-default");
            window.Width=760;window.Height=520;await Settle();
            var searchPosition=Control<Button>("HistorySearch").TranslatePoint(new Avalonia.Point(0,0),window);
            check(theme+" history controls and table remain usable in a small logical viewport ("+list.Bounds.Height+" px)",list.Bounds.Height>=100&&searchPosition is { } position&&position.Y+Control<Button>("HistorySearch").Bounds.Height<=window.ClientSize.Height);
            Shot(window,theme+"-history-small");
            window.Width=1180;window.Height=850;await Settle();
            check(theme+" history opens existing local data without starting monitor/capture",list.Items.Count>=100&&!monitor.IsRunning&&!capture.IsEnabled&&Control<CheckBox>("HistoryCapture").IsEnabled==false);
            var selectedContext=list.Items.Cast<TelegramHistoryRow>().First(r=>!r.IsMatch);list.SelectedItem=selectedContext;
            Click(Control<Button>("HistoryNext"));await Until(()=>list.Items.Count>=200);
            var rows=list.Items.Cast<TelegramHistoryRow>().ToArray();
            check(theme+" history pagination merges context and retains selection by persistent event id",rows.Length==rows.Select(r=>r.Entry.Id).Distinct().Count()&&rows.Count(r=>r.IsMatch)==200
                &&list.SelectedItem is TelegramHistoryRow{IsMatch:true} selected&&selected.Entry.Id==selectedContext.Entry.Id);
            Control<TextBox>("HistoryServices").Text="GroupValueWrite";await Settle();Click(Control<Button>("HistorySearch"));await Until(()=>list.Items.Cast<TelegramHistoryRow>().Any(r=>!r.IsMatch));
            check(theme+" history context rows are explicitly distinct from filtered matches",list.Items.Cast<TelegramHistoryRow>().Where(r=>r.IsMatch).All(r=>r.Entry.Service=="GroupValueWrite")&&list.Items.Cast<TelegramHistoryRow>().Any(r=>!r.IsMatch&&r.Entry.Service=="GroupValueRead"));
            Control<Expander>("HistoryDetailsExpander").IsExpanded=true;list.SelectedIndex=0;await Settle();
            check(theme+" history displays full date and independent coverage/loaded interval",Control<TextBlock>("HistoryLoadedRange").Text!.Contains(start.Year.ToString())
                &&Control<TextBlock>("HistoryCoverage").Text!.Contains(WorkbenchStrings.Get("HistoryCoverageScope"))&&Control<TextBox>("HistoryDetails").Text!.Contains(WorkbenchStrings.Get("Unknown")));
            Click(Control<Button>("HistoryOpenGroup"));var groupTarget=navigated;Click(Control<Button>("HistoryOpenDevice"));
            check(theme+" history opens matching group and source device in the current project",groupTarget==new WorkbenchTarget("ga","history-group")&&navigated==new WorkbenchTarget("device","history-device")&&!monitor.IsRunning);
            window.Width=760;window.Height=520;await Settle();
            check(theme+" history retains its data viewport while metadata is expanded ("+list.Bounds.Height+" px)",list.Bounds.Height>=100);
            Shot(window,theme+"-history-small-metadata");
            window.Width=1180;window.Height=850;await Settle();
            source.BlockNext();Click(Control<Button>("HistorySearch"));await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Control<TextBox>("HistoryServices").Text="GroupValueRead";await Settle();Click(Control<Button>("HistorySearch"));await Until(()=>list.Items.Cast<TelegramHistoryRow>().Any(r=>r.IsMatch&&r.Entry.Service=="GroupValueRead"));
            source.Release.TrySetResult();await Settle();
            check(theme+" history discards stale filter answers even when an older provider ignores cancellation",list.Items.Cast<TelegramHistoryRow>().Where(r=>r.IsMatch).All(r=>r.Entry.Service=="GroupValueRead"));
            var boundary=start.AddSeconds(100);Control<TextBox>("HistoryFrom").Text=boundary.ToString("O",CultureInfo.InvariantCulture);await Settle();Click(Control<Button>("HistoryRetention"));await Settle();
            var promptVisible=Control<Button>("HistoryConfirmRetention").IsEffectivelyVisible&&Control<TextBlock>("HistoryRetentionText").Text!.Contains(boundary.ToString("O",CultureInfo.InvariantCulture));Click(Control<Button>("HistoryCancelRetention"));
            var cancelledRows=(await store.GetCoverageAsync(DateTimeOffset.UnixEpoch,DateTimeOffset.MaxValue)).StoredTelegrams;
            Click(Control<Button>("HistoryRetention"));await Settle();Click(Control<Button>("HistoryConfirmRetention"));
            await Until(()=>Control<TextBlock>("HistoryCoverage").Text?.Contains(WorkbenchStrings.Get("HistoryRetainedFrom",boundary.ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'",CultureInfo.InvariantCulture)))==true);
            check(theme+" history retention requires confirmation, supports cancel and refreshes after deletion",promptVisible&&cancelledRows==205
                &&(await store.GetCoverageAsync(DateTimeOffset.UnixEpoch,DateTimeOffset.MaxValue)).StoredTelegrams==105&&(await store.QueryAsync(new())).Rows.All(r=>r.Entry.TimestampUtc>=boundary));
            await store.AppendAsync(new(DateTimeOffset.UtcNow,"1.1.1","1/1/1",true,"IdentityProbe","01","Real","synthetic-interface",ProjectId:Guid.NewGuid().ToString()));
            await store.AppendAsync(new(DateTimeOffset.UtcNow,"1.1.1","1/1/1",true,"IdentityProbe","01","Real","synthetic-interface"));
            Control<TextBox>("HistoryServices").Text="IdentityProbe";await Settle();Click(Control<Button>("HistorySearch"));await Until(()=>list.Items.Cast<TelegramHistoryRow>().Count(r=>r.IsMatch)==2);
            var unboundSafe=true;var beforeLookup=navigated;
            foreach(var row in list.Items.Cast<TelegramHistoryRow>().Where(r=>r.IsMatch).ToArray())
            {list.SelectedItem=row;await Settle();unboundSafe&=!Control<Button>("HistoryOpenGroup").IsEnabled&&!Control<Button>("HistoryOpenDevice").IsEnabled&&Control<TextBox>("HistoryDetails").Text!.Contains(WorkbenchStrings.Get("HistoryProjectLookupUnavailable"));Click(Control<Button>("HistoryOpenGroup"));Click(Control<Button>("HistoryOpenDevice"));}
            check(theme+" history blocks address lookups for another project GUID and unknown identity",unboundSafe&&navigated==beforeLookup);
            source.BlockNext();Click(Control<Button>("HistorySearch"));await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));identity=new();source.Release.TrySetResult();
            await Until(()=>Control<TextBlock>("HistoryStatus").Text==WorkbenchStrings.Get("HistoryProjectChanged"));
            check(theme+" history cannot publish old project results into a replacement session",list.Items.Count==0&&!Control<Button>("HistoryOpenGroup").IsEnabled);
            identity=new();window.Close();await window.CloseCompletion;
            source.BlockNext();window=new(monitor,()=>project,()=>identity,store:source);window.Show(owner);await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Close();var awaitingClose=!window.CloseCompletion.IsCompleted;source.Release.TrySetResult();await window.CloseCompletion;
            check(theme+" history close waits for a delayed local query without blocking the window",awaitingClose);
        }
        finally
        {
            source.Release.TrySetResult();window?.Close();if(window is not null)await window.CloseCompletion;
            monitor.Dispose();await monitor.HistoryCompletion;store.Dispose();Directory.Delete(directory,true);
        }
    }

    private sealed class DelayedStore(ITelegramHistoryStore inner):ITelegramHistoryStore
    {
        private int _block;
        public TaskCompletionSource Entered{get;private set;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release{get;private set;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void BlockNext(){Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);Release=new(TaskCreationOptions.RunContinuationsAsynchronously);Interlocked.Exchange(ref _block,1);}
        public TelegramHistoryCapabilities Capabilities=>inner.Capabilities;public string StoreId=>inner.StoreId;public long Generation=>inner.Generation;
        public async Task<TelegramQueryPage> QueryAsync(TelegramQuery query,CancellationToken cancellationToken=default)
        {if(Interlocked.Exchange(ref _block,0)==1){Entered.TrySetResult();await Release.Task;return await inner.QueryAsync(query);}return await inner.QueryAsync(query,cancellationToken);}
        public Task<TelegramHistoryEntry> AppendAsync(TelegramHistoryDraft draft,CancellationToken cancellationToken=default)=>inner.AppendAsync(draft,cancellationToken);
        public Task<TelegramCoverage> GetCoverageAsync(DateTimeOffset fromUtc,DateTimeOffset untilUtc,CancellationToken cancellationToken=default)=>inner.GetCoverageAsync(fromUtc,untilUtc,cancellationToken);
        public Task<TelegramCaptureInterval> BeginCaptureAsync(string sourceId,DateTimeOffset fromUtc,CancellationToken cancellationToken=default)=>inner.BeginCaptureAsync(sourceId,fromUtc,cancellationToken);
        public Task EndCaptureAsync(string id,DateTimeOffset untilUtc,string reason,CancellationToken cancellationToken=default)=>inner.EndCaptureAsync(id,untilUtc,reason,cancellationToken);
        public Task<TelegramRetentionResult> RetainFromAsync(DateTimeOffset fromUtc,CancellationToken cancellationToken=default)=>inner.RetainFromAsync(fromUtc,cancellationToken);
        public Task BackupAsync(string destinationFile,CancellationToken cancellationToken=default)=>inner.BackupAsync(destinationFile,cancellationToken);
        public void Dispose(){}
    }
}
