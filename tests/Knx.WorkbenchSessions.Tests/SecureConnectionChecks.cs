using System.Net;
using Knx;
using Knx.App.Workbench;
using Knx.Bus;
using Knx.Bus.Connections;

internal static class SecureConnectionChecks
{
    public static async Task Run(Action<string,bool> check)
    {
        var path=Path.Combine(Path.GetTempPath(),"synthetic-reference.knxkeys");
        var reference=new SecureKeyringReference(path,"Synthetic",new(1,1,0),2,new(1,1,240));
        var profile=KnxConnectionProfile.IpSecure(new(IPAddress.Parse("192.0.2.194"),3671),reference);
        var options=new ReadOnlyConnectionOptions(profile);var lifetime=new object();object current=lifetime;
        var backend=new Backend(false);
        await using(var connection=new ReadOnlyWorkbenchConnection(lifetime,()=>current,_=>backend))
        {
            try{await connection.ConnectAsync(options);}catch{ }
            check("secure Workbench rejects a backend without authenticated transport proof",!connection.IsConnected && backend.Disposals==1 && connection.Snapshot.Failure==KnxConnectionFailure.AuthenticationFailed);
        }
        backend=new Backend(true);
        var rejectedBackend=new Backend(true);
        await using(var connection=new ReadOnlyWorkbenchConnection(lifetime,()=>current,_=>backend))
        {
            await connection.ConnectAsync(options);
            await using(var competing=new ReadOnlyWorkbenchConnection(lifetime,()=>current,_=>rejectedBackend))
            {
                try{await competing.ConnectAsync(options);}catch{ }
                check("rejected competing backend is disposed even before connect",rejectedBackend.Disposals==1&&!rejectedBackend.IsConnected&&competing.Snapshot.Issue==ReadOnlyConnectionIssue.Occupied);
            }
            check("secure Workbench publishes exact authenticated assigned interface",connection.IsConnected && connection.Snapshot.GatewayAuthenticated && connection.Snapshot.Transport==KnxTransportKind.IpSecureUdp);
            var captured=backend.Receive;var received=0;connection.TelegramReceived+=_=>received++;
            current=new object();captured?.Invoke(new(){Source="synthetic",Telegram=new(){Source=new(1,1,1),Destination=new(1),IsGroupAddress=true,Apci=Apci.GroupValueWrite,Data=[1]}});
            check("secure same-id project replacement rejects captured late frames",received==0&&!connection.IsConnected);
            await connection.DisconnectAsync();
            check("secure project teardown joins backend",backend.Disposals==1);
        }
        var root=Path.Combine(Path.GetTempPath(),"buschord-secure-m112-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var project=Knx.Workbench.WorkbenchJson.CreateDemo();var device=ProjectQuery.DeviceList(project).First();var basepath=Path.Combine(root,"baseline.knxproj");await File.WriteAllTextAsync(basepath,"synthetic");
            var hash=await M112PrivatePaths.HashAsync(basepath);var fake=new M112CommissioningChecks.FakeBackend(device.Address.ToString());var wrapped=new M112Backend(fake);var sessionId=new object();
            await using var session=new M112CommissioningSession(()=>new(project,sessionId,false,false),device.Id,wrapped);
            await session.PrepareAsync(project,basepath,hash,options,root);
            check("secure M112 offline preview requests no credentials or network",fake.Prepares==1 && fake.Reads==0 && fake.Applies==0 && session.Snapshot.Phase==M112CommissioningPhase.Reviewed);
            await session.ReadIdentityAsync();session.ConfirmIdentity(true);await session.ApplyAsync(device.Address.ToString(),fake.Review.Fingerprint);
            check("cancelled secure acquisition before writes never claims recovery required",!session.RecoveryRequired&&fake.Applies==0&&session.Snapshot.Phase==M112CommissioningPhase.Cancelled&&session.Snapshot.ConnectionFailure==KnxConnectionFailure.Cancelled);
            check("M112 identity request retains exact selected secure profile",wrapped.Seen?.Profile.EquivalentTo(profile)==true);
        }
        finally {Directory.Delete(root,true);}
        var scanRoot=Path.Combine(Path.GetTempPath(),"buschord-secure-scan-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scanRoot);
        try
        {
            var project=new Project {Id="P",Name="Synthetic scan",Installations={new Installation {Id="I",Areas={new Area {Id="A",Address=1,Lines={new Line {Id="L",Address=1}}}}}}};
            var credentials=new RejectedProvider();var transports=0;
            var factory=new KnxConnectionFactory(new(scanRoot),credentials,createTransport:_=>{transports++;throw new Exception("No network expected.");});
            await using var scan=new LineScanSession(new(),()=>project);
            var plan=new ReadOnlyLineScanPlan(options.Gateway,options.RequestedAddress,[new(1,1,1)]);
            await scan.RunProfileAsync("L",plan,profile,factory);
            check("secure scan validates selected credentials before any network with no plain fallback",transports==0&&credentials.Reference?.TunnelAddress==profile.RequestedAddress&&scan.Results.Count==0&&!scan.IsRunning&&scan.LastError is not null);
        }
        finally{Directory.Delete(scanRoot,true);}
        var drainRoot=Path.Combine(Path.GetTempPath(),"buschord-secure-scan-drain-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(drainRoot);
        try
        {
            static Project ScanProject()=>new(){Id="P",Name="Synthetic scan drain",Installations={new Installation {Id="I",Areas={new Area {Id="A",Address=1,Lines={new Line {Id="L",Address=1}}}}}}};
            var project=ScanProject();var interfaces=new InterfaceCoordinator();var plan=new ReadOnlyLineScanPlan(options.Gateway,options.RequestedAddress,[new(1,1,1)]);
            var credentials=new RejectedProvider();var factory=new KnxConnectionFactory(new(drainRoot),credentials);Task? disposal=null;var joinedAtNotify=false;
            await using(var scan=new LineScanSession(interfaces,()=>project))
            {
                scan.PropertyChanged+=(_,args)=>{if(args.PropertyName==nameof(scan.IsRunning)&&scan.IsRunning&&disposal is null){disposal=scan.DisposeAsync().AsTask();joinedAtNotify=!disposal.IsCompleted;}};
                await scan.RunProfileAsync("L",plan,profile,factory);await disposal!;
                using var endpoint=RealEndpointReservations.TryAcquire(plan.InterfaceId);var available=interfaces.TryAcquire(plan.InterfaceId,InterfaceActivity.Scan,out var lease);lease?.Dispose();
                check("reentrant scan close joins prepublished task and releases interface reservation",joinedAtNotify&&available&&endpoint is not null&&credentials.Reference is null);
            }
            LineScanSession? heldScan=null;disposal=null;
            var holding=new HoldingProvider(()=>disposal=heldScan!.DisposeAsync().AsTask());var transports=0;
            factory=new(new(drainRoot),holding,new FileKnxClientSerialProvider(drainRoot),createTransport:_=>{transports++;throw new Exception("No transport expected.");});
            await using(var scan=heldScan=new LineScanSession(new(),()=>project))
            {
                var running=scan.RunProfileAsync("L",plan,profile,factory);await holding.Entered.Task;var occupied=false;
                try{await using var competing=await factory.AcquireOperationAsync(KnxConnectionProfile.Plain(options.Gateway,options.RequestedAddress));}catch(KnxConnectionException error){occupied=error.Failure==KnxConnectionFailure.Occupied;}
                var pending=disposal is {IsCompleted:false};holding.Release.TrySetResult();await running;await disposal!;
                await using var next=await factory.AcquireOperationAsync(KnxConnectionProfile.Plain(options.Gateway,options.RequestedAddress));
                check("scan close joins reentrant credential unwind before releasing gateway lease",pending&&occupied&&transports==0&&!scan.IsRunning);
            }
        }
        finally{Directory.Delete(drainRoot,true);}
    }
    private sealed class Backend(bool authenticated):IReadOnlyWorkbenchBackend
    {
        public int Disposals;public Action<BusTelegram>? Receive;public bool IsConnected{get;private set;}
        public IndividualAddress AssignedAddress=>new(1,1,240);
        public KnxConnectionSecurity? Security=>authenticated?new(KnxTransportKind.IpSecureUdp,true,AssignedAddress,new string('A',64)):null;
        public event Action<BusTelegram>? TelegramReceived{add=>Receive+=value;remove=>Receive-=value;}
        public event Action<BusConnectionState>? StateChanged{add{}remove{}}
        public Task ConnectAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();IsConnected=true;return Task.CompletedTask;}
        public ValueTask DisposeAsync(){Disposals++;IsConnected=false;return ValueTask.CompletedTask;}
    }
    private sealed class RejectedProvider:Knx.Bus.Security.IKnxSecureCredentialProvider
    {
        public SecureKeyringReference? Reference;
        public Task<Knx.Bus.Security.KnxSecureCredentialScope> AcquireAsync(SecureKeyringReference reference,Knx.Bus.Security.ISecurePasswordSource? source,CancellationToken ct)
        {Reference=reference;throw new KnxConnectionException(KnxConnectionFailure.KeyringRejected);}
    }
    private sealed class HoldingProvider(Action entered):Knx.Bus.Security.IKnxSecureCredentialProvider
    {
        public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Knx.Bus.Security.KnxSecureCredentialScope> AcquireAsync(SecureKeyringReference reference,Knx.Bus.Security.ISecurePasswordSource? source,CancellationToken ct)
        {
            entered();Entered.TrySetResult();await Release.Task;
            return new(reference.HostAddress,reference.TunnelAddress,reference.UserId,new string('A',64),new byte[16],new byte[16]);
        }
    }
    private sealed class M112Backend(M112CommissioningChecks.FakeBackend inner):IM112CommissioningBackend
    {
        public ReadOnlyConnectionOptions? Seen;
        public Task<IM112PreparedChange> PrepareAsync(Project a,DeviceInstance b,Project c,DeviceInstance d,CancellationToken ct)=>inner.PrepareAsync(a,b,c,d,ct);
        public Task<ManagementDeviceIdentity> ReadIdentityAsync(ReadOnlyConnectionOptions o,IndividualAddress a,CancellationToken ct){Seen=o;return inner.ReadIdentityAsync(o,a,ct);}
        public Task<M112RecoveryInspection> InspectRecoveryAsync(string p,CancellationToken ct)=>inner.InspectRecoveryAsync(p,ct);
        public Task<M112PhysicalIdentity> ReadPhysicalIdentityAsync(ReadOnlyConnectionOptions o,IndividualAddress a,CancellationToken ct)=>inner.ReadPhysicalIdentityAsync(o,a,ct);
        public Task<M112DownloadResult> ApplyAsync(ReadOnlyConnectionOptions o,IM112PreparedChange p,ManagementDeviceIdentity e,string f,string j,Action<string> progress,CancellationToken ct)=>throw new M112ConnectionPreflightException(KnxConnectionFailure.Cancelled);
        public Task<M112DownloadResult> RecoverAsync(ReadOnlyConnectionOptions o,M112RecoveryInspection i,string t,Action<string> progress,CancellationToken ct)=>inner.RecoverAsync(o,i,t,progress,ct);
    }
}
