using Knx;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Bus;
using Knx.Bus.History;

internal static class MonitorHistoryCaptureChecks
{
    internal static async Task Run(Action<string,bool> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"buschord-history-capture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var project=ProjectWizard.Create(new());
        WorkbenchTelegram Frame(string data="01")=>new(DateTimeOffset.UtcNow,"1.1.1","1/1/1","GroupValueWrite",null,data,SessionOrigin.Demo,DataHex:data);
        async Task Case(string name,Func<Task<bool>> body)
        {try{check("history adapter: "+name,await body().WaitAsync(TimeSpan.FromSeconds(15)));}catch(Exception ex){Console.WriteLine(ex.GetType().Name+": "+ex.Message);check("history adapter: "+name,false);}}
        try
        {
            await Case("default capture is off and explicit enable records only subsequent frames",async()=>
            {
                using var store=new FileTelegramHistoryStore(Path.Combine(root,"opt-in"));var capture=new MonitorHistoryCapture(store);
                using var monitor=new MonitorSession(new(),()=>project,null,capture);monitor.Start();monitor.TickDemo(Frame());await capture.FlushAsync();
                if((await store.QueryAsync(new())).Rows.Count!=0)return false;
                await monitor.SetHistoryEnabledAsync(true);monitor.TickDemo(Frame());await capture.FlushAsync();monitor.Stop();await capture.FlushAsync();
                var rows=(await store.QueryAsync(new())).Rows;
                monitor.Dispose();await monitor.HistoryCompletion;
                return rows.Count==1&&rows[0].Entry is {Origin:"Demo",SourceId:"demo-ip",HopCount:null,RawCemiHex:null}&&rows[0].Entry.ProjectId==project.Guid;
            });
            await Case("pause and resume produce separate coverage intervals and no paused data",async()=>
            {
                using var store=new FileTelegramHistoryStore(Path.Combine(root,"pause"));var capture=new MonitorHistoryCapture(store);
                using var monitor=new MonitorSession(new(),()=>project,null,capture);await monitor.SetHistoryEnabledAsync(true);monitor.Start();monitor.TickDemo(Frame());
                monitor.Pause();monitor.TickDemo(Frame("02"));await Task.Delay(10);monitor.Resume();monitor.TickDemo(Frame("03"));monitor.Stop();await capture.FlushAsync();
                var coverage=await store.GetCoverageAsync(DateTimeOffset.UtcNow.AddHours(-1),DateTimeOffset.UtcNow.AddHours(1));
                var rows=(await store.QueryAsync(new())).Rows;monitor.Dispose();await monitor.HistoryCompletion;
                return rows.Count==2&&rows.All(r=>r.Entry.DataHex!="02")&&coverage.CaptureIntervals.Count==2
                    &&coverage.CaptureIntervals.All(i=>i.UntilUtc is not null&&!i.EndUnknown)&&coverage.CaptureIntervals.Any(i=>i.Reason=="paused");
            });
            await Case("close drains accepted frames and persists a closed interval without blocking caller",async()=>
            {
                using var store=new FileTelegramHistoryStore(Path.Combine(root,"drain"));var slow=new ControlledStore(store){BlockFirst=true};var capture=new MonitorHistoryCapture(slow);
                using var monitor=new MonitorSession(new(),()=>project,null,capture);monitor.Start();await monitor.SetHistoryEnabledAsync(true);monitor.TickDemo(Frame());
                await slow.Entered.Task;monitor.Stop();monitor.Dispose();var waiting=!monitor.HistoryCompletion.IsCompleted;slow.Release.TrySetResult();await monitor.HistoryCompletion;
                var coverage=await store.GetCoverageAsync(DateTimeOffset.UtcNow.AddHours(-1),DateTimeOffset.UtcNow.AddHours(1));
                return waiting&&(await store.QueryAsync(new())).Rows.Count==1&&coverage.CaptureIntervals.Single() is {UntilUtc:not null,EndUnknown:false};
            });
            await Case("queue overflow stops history visibly while full monitor recording continues",async()=>
            {
                using var store=new FileTelegramHistoryStore(Path.Combine(root,"overflow"));var slow=new ControlledStore(store){BlockFirst=true};var capture=new MonitorHistoryCapture(slow,1);
                using var monitor=new MonitorSession(new(),()=>project,null,capture);monitor.Start();await monitor.SetHistoryEnabledAsync(true);monitor.TickDemo(Frame());await slow.Entered.Task;
                monitor.TickDemo(Frame("02"));monitor.TickDemo(Frame("03"));monitor.TickDemo(Frame("04"));
                var independent=monitor.IsRunning&&monitor.RecordedCount==4&&!capture.IsEnabled&&capture.Failure==MonitorHistoryFailure.QueueOverflow&&capture.DroppedCount==2;
                slow.Release.TrySetResult();await capture.FlushAsync();monitor.Stop();monitor.Dispose();await monitor.HistoryCompletion;
                var coverage=await store.GetCoverageAsync(DateTimeOffset.UtcNow.AddHours(-1),DateTimeOffset.UtcNow.AddHours(1));
                return independent&&(await store.QueryAsync(new())).Rows.Count==2&&coverage.CaptureIntervals.Single().Reason=="queue-overflow";
            });
            await Case("real metadata and notified disconnect preserve known facts without raw-cEMI invention",async()=>
            {
                using var store=new FileTelegramHistoryStore(Path.Combine(root,"real"));var capture=new MonitorHistoryCapture(store);var source=new Source();
                using var monitor=new MonitorSession(new(),()=>project,null,capture);monitor.ConfigureReal("test-interface",source);monitor.Start();await monitor.SetHistoryEnabledAsync(true);
                source.Send(new(){Timestamp=DateTimeOffset.UtcNow,Source="1.1.1",Telegram=new(){Source=new(1,1,1),Destination=new(GroupAddress.Parse("1/1/1").Raw),IsGroupAddress=true,Apci=Apci.GroupValueWrite,Data=[1],HopCount=3}});
                source.Disconnect();monitor.FlushDisplay();await capture.FlushAsync();
                var row=(await store.QueryAsync(new())).Rows.Single();var coverage=await store.GetCoverageAsync(DateTimeOffset.UtcNow.AddHours(-1),DateTimeOffset.UtcNow.AddHours(1));
                monitor.Dispose();await monitor.HistoryCompletion;
                return !monitor.IsRunning&&row.Entry is{Origin:"Real",SourceId:"test-interface",HopCount:3,RawCemiHex:null}&&coverage.CaptureIntervals.Single().Reason=="disconnected";
            });
            await Case("history write failure cannot stop or silently relabel the live CSV monitor",async()=>
            {
                using var store=new FileTelegramHistoryStore(Path.Combine(root,"failure"));var failing=new ControlledStore(store){FailFirst=true};var capture=new MonitorHistoryCapture(failing);
                using var monitor=new MonitorSession(new(),()=>project,null,capture);monitor.Start();await monitor.SetHistoryEnabledAsync(true);monitor.TickDemo(Frame());await capture.FlushAsync();
                var ok=monitor.IsRunning&&monitor.RecordedCount==1&&capture.Failure==MonitorHistoryFailure.WriteFailed&&!capture.IsEnabled;
                monitor.Stop();monitor.Dispose();await monitor.HistoryCompletion;return ok&&(await store.QueryAsync(new())).Rows.Count==0;
            });
            await Case("a frame delivered synchronously while subscribing follows the persisted start anchor",async()=>
            {
                using var store=new FileTelegramHistoryStore(Path.Combine(root,"subscribe"));var capture=new MonitorHistoryCapture(store);var source=new Source(true);
                using var monitor=new MonitorSession(new(),()=>project,null,capture);monitor.ConfigureReal("subscribe-interface",source);await monitor.SetHistoryEnabledAsync(true);monitor.Start();await capture.FlushAsync();
                monitor.Stop();monitor.Dispose();await monitor.HistoryCompletion;
                return monitor.RecordedCount==1&&(await store.QueryAsync(new())).Rows.Count==1&&capture.Failure==MonitorHistoryFailure.None;
            });
            await Case("pre-anchor samples are counted and skipped without timestamp changes or a recording failure",async()=>
            {
                using var store=new FileTelegramHistoryStore(Path.Combine(root,"pre-anchor"));var capture=new MonitorHistoryCapture(store);var source=new Source();
                using var monitor=new MonitorSession(new(),()=>project,null,capture);monitor.ConfigureReal("anchor-interface",source);monitor.Start();await monitor.SetHistoryEnabledAsync(true);
                source.Send(Source.Telegram(DateTimeOffset.UtcNow.AddSeconds(-1)));source.Send(Source.Telegram(DateTimeOffset.UtcNow));await capture.FlushAsync();
                var ok=monitor.RecordedCount==2&&capture.IsEnabled&&capture.SkippedBeforeAnchorCount==1&&capture.Failure==MonitorHistoryFailure.None;
                monitor.Stop();monitor.Dispose();await monitor.HistoryCompletion;return ok&&(await store.QueryAsync(new())).Rows.Count==1;
            });
            await Case("control floods share the bounded mailbox and flush requests share one watermark",async()=>
            {
                using var store=new FileTelegramHistoryStore(Path.Combine(root,"control-budget"));var slow=new ControlledStore(store){BlockFirst=true};var capture=new MonitorHistoryCapture(slow,2);
                using var monitor=new MonitorSession(new(),()=>project,null,capture);monitor.Start();await monitor.SetHistoryEnabledAsync(true);monitor.TickDemo(Frame());await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for(var i=0;i<1000;i++){monitor.Pause();monitor.Resume();}
                var flushes=Enumerable.Range(0,1000).Select(_=>capture.FlushAsync()).ToArray();var bounded=capture.Failure==MonitorHistoryFailure.QueueOverflow&&!capture.IsEnabled&&flushes.Distinct().Count()==1;
                slow.Release.TrySetResult();await Task.WhenAll(flushes);monitor.Stop();monitor.Dispose();await monitor.HistoryCompletion;
                return bounded&&(await store.QueryAsync(new())).Rows.Count==1;
            });
            await Case("pool leases share one writer and release the file after the last asynchronous close",async()=>
            {
                var directory=Path.Combine(root,"pool");var first=await TelegramHistoryStorePool.AcquireAsync(directory);var second=await TelegramHistoryStorePool.AcquireAsync(directory);
                var shared=ReferenceEquals(first.Store,second.Store);await first.DisposeAsync();await second.Store.AppendAsync(new(DateTimeOffset.UtcNow,"1.1.1","1/1/1",true,"GroupValueWrite","01"));await second.DisposeAsync();
                using var reopened=new FileTelegramHistoryStore(directory);return shared&&(await reopened.QueryAsync(new())).Rows.Count==1;
            });
            var parsed=new TelegramHistoryQueryInput("2026-01-01T13:00:00+01:00","2026-01-01T14:00:00+01:00","1.1.1; 1.1.2;1.1.1",Context:3).Parse(41);
            check("history input: UTC offsets normalize and repeated OR values deduplicate",parsed.FromUtc==new DateTimeOffset(2026,1,1,12,0,0,TimeSpan.Zero)&&parsed.Sources.Count==2&&parsed.ContextAfter==3&&parsed.RequestGeneration==41);
            check("history input: missing time zone is rejected",!TelegramHistoryQueryInput.TryUtc("2026-01-01T12:00:00",out _));
            var context=new TelegramHistoryQueryInput("2026-01-01T12:00:00Z","2026-01-01T13:00:00Z",Context:20).Parse(1);
            check("history input: maximum context stays within the store result bound",context.PageSize*(1+context.ContextBefore+context.ContextAfter)<=4096);
            var limited=false;try{new TelegramHistoryQueryInput("2026-01-01T12:00:00Z","2026-01-01T13:00:00Z",Sources:string.Join(';',Enumerable.Range(0,65))).Parse(1);}catch(FormatException){limited=true;}
            check("history input: filter count follows the core maximum of sixty-four",limited);
            var unknown=new TelegramHistoryRow(new(1,"stable-id",DateTimeOffset.UnixEpoch,"1.1.1","1/1/1",true,"GroupValueWrite","01","Real","interface",null,null,null,null,null,null,null),false);
            var csv=TelegramHistoryExport.Csv([unknown]);
            check("history export: unknown metadata stays empty and APDU is not exported as raw cEMI",csv.Split('\n')[1].TrimEnd('\r').Split(',').Skip(10).Take(5).All(v=>v=="")&&csv.Split('\n')[1].Contains(",01,Real,")&&csv.Contains("APDU,")&&csv.Contains("RawCemi,"));
        }
        finally{Directory.Delete(root,true);}
    }
    private sealed class Source(bool sendOnSubscribe=false) : IMonitorTelegramSource,IMonitorConnectionNotifications
    {
        public bool IsConnected{get;private set;}=true;
        private Action<BusTelegram>? _received;
        public event Action<BusTelegram> TelegramReceived
        {add{_received+=value;if(sendOnSubscribe)value(Telegram(DateTimeOffset.UtcNow));}remove=>_received-=value;}
        public event Action<BusConnectionState>? StateChanged;
        internal static BusTelegram Telegram(DateTimeOffset timestamp)=>new(){Timestamp=timestamp,Source="1.1.1",Telegram=new(){Source=new(1,1,1),Destination=new(GroupAddress.Parse("1/1/1").Raw),IsGroupAddress=true,Apci=Apci.GroupValueWrite,Data=[1],HopCount=3}};
        public void Send(BusTelegram telegram)=>_received?.Invoke(telegram);
        public void Disconnect(){IsConnected=false;StateChanged?.Invoke(BusConnectionState.Disconnected);}
    }
    private sealed class ControlledStore(ITelegramHistoryStore inner) : ITelegramHistoryStore
    {
        public bool BlockFirst{get;init;}public bool FailFirst{get;init;}private int _appends;
        public TaskCompletionSource Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TelegramHistoryCapabilities Capabilities=>inner.Capabilities;public string StoreId=>inner.StoreId;public long Generation=>inner.Generation;
        public async Task<TelegramHistoryEntry> AppendAsync(TelegramHistoryDraft draft,CancellationToken cancellationToken=default)
        {if(Interlocked.Increment(ref _appends)==1){Entered.TrySetResult();if(BlockFirst)await Release.Task.WaitAsync(cancellationToken);if(FailFirst)throw new IOException("Synthetic write failure");}return await inner.AppendAsync(draft,cancellationToken);}
        public Task<TelegramQueryPage> QueryAsync(TelegramQuery query,CancellationToken cancellationToken=default)=>inner.QueryAsync(query,cancellationToken);
        public Task<TelegramCoverage> GetCoverageAsync(DateTimeOffset fromUtc,DateTimeOffset untilUtc,CancellationToken cancellationToken=default)=>inner.GetCoverageAsync(fromUtc,untilUtc,cancellationToken);
        public Task<TelegramCaptureInterval> BeginCaptureAsync(string sourceId,DateTimeOffset fromUtc,CancellationToken cancellationToken=default)=>inner.BeginCaptureAsync(sourceId,fromUtc,cancellationToken);
        public Task EndCaptureAsync(string id,DateTimeOffset untilUtc,string reason,CancellationToken cancellationToken=default)=>inner.EndCaptureAsync(id,untilUtc,reason,cancellationToken);
        public Task<TelegramRetentionResult> RetainFromAsync(DateTimeOffset fromUtc,CancellationToken cancellationToken=default)=>inner.RetainFromAsync(fromUtc,cancellationToken);
        public Task BackupAsync(string destinationFile,CancellationToken cancellationToken=default)=>inner.BackupAsync(destinationFile,cancellationToken);
        public void Dispose(){}
    }
}
