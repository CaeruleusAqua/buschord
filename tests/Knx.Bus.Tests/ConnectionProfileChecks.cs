using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knx;
using Knx.Bus;
using Knx.Bus.Connections;
using Knx.Bus.Security;

internal static class ConnectionProfileChecks
{
    private static readonly IPEndPoint Gateway = new(IPAddress.Loopback, 3671), Local = new(IPAddress.Loopback, 43123);
    private static readonly IndividualAddress Assigned = new(1, 1, 240);
    private static readonly byte[] User = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray(), Device = Enumerable.Range(32, 16).Select(i => (byte)i).ToArray();
    private static string Fingerprint => new('A', 64);
    internal static async Task<int> RunAsync()
    {
        var failures = 0;
        foreach (var (name, run) in new (string, Func<Task>)[]
        {
            ("immutable explicit profiles and secret-safe serialization", Profiles),
            ("fresh Linux state directory never falls back to the working directory", DefaultStateDirectory),
            ("span KDF agrees and rejects ambiguous credentials", Kdf),
            ("atomic client identity publication joins parallel initializers", Serials),
            ("protected importer selection and buffer ownership", Credentials),
            ("rejected credentials open zero network transports", RejectedCredentials),
            ("authenticated tunnel and fresh recovery opens share one capability", FreshOpens),
            ("wrong gateway authentication never falls back to plain", AuthenticationFailure),
            ("actual assigned address must equal signed interface", AddressMismatch),
            ("joined concurrent operation disposal retains cross-mode reservation", JoinedDisposal),
            ("facade concurrent close joins and diagnostics reuse its actual tunnel", Facade),
            ("known protected groups cannot leave or enter as clear values", GroupBoundary),
            ("factory tunnel enforces boundary on public send APIs", TunnelBoundary),
        })
        {
            try { await run().WaitAsync(TimeSpan.FromSeconds(12)); Console.WriteLine("PASS ConnectionProfile " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL ConnectionProfile " + name + ": " + error); }
        }
        return failures;
    }
    private static void Require(bool value, string detail) { if (!value) throw new Exception(detail); }
    private static async Task<T> Failure<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
    private static SecureKeyringReference Reference(string path, IndividualAddress? address = null) => new(path, "Synthetic factory fixture", new(1,1,0), 2, address ?? Assigned, expectedSourceFingerprint: Fingerprint);
    private static Task DefaultStateDirectory()
    {
        if (!OperatingSystem.IsLinux()) return Task.CompletedTask;
        using var temporary = new Temporary();
        var missing = Path.Combine(temporary.Root, "uncreated-data");
        var previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", missing);
            var actual = GatewayLeaseCoordinator.DefaultDirectory;
            Require(Path.IsPathFullyQualified(actual) && actual == Path.Combine(missing, "BusChord", "network-leases"),
                "A missing user data folder must not select a working-directory-relative lease domain.");
            Require(!Directory.Exists(missing), "Resolving the state path must not create the data folder.");
        }
        finally { Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous); }
        return Task.CompletedTask;
    }
    private static KnxConnectionProfile Profile(string path, IndividualAddress? address = null) => KnxConnectionProfile.IpSecure(Gateway, Reference(path, address));
    private static Task Profiles()
    {
        var gateway = new IPEndPoint(IPAddress.Parse("192.0.2.11"), 3671); var original = gateway.Address;
        var profile = KnxConnectionProfile.Plain(gateway, Assigned); gateway.Address = IPAddress.Parse("192.0.2.99"); gateway.Port = 99;
        var copy = profile.Gateway; copy.Port = 98;
        Require(profile.Gateway.Address.Equals(original) && profile.Port == 3671, "Mutable caller/getter endpoint redirected the profile.");
        var secretPath = Path.Combine(Path.GetTempPath(), "secret-reference.knxkeys"); var reference = Reference(secretPath);
        var secure = KnxConnectionProfile.IpSecure(Gateway, reference, new Password("NeverPrinted"));
        var json = JsonSerializer.Serialize(secure) + JsonSerializer.Serialize(reference) + secure + reference;
        Require(!json.Contains(secretPath) && !json.Contains("NeverPrinted") && !secure.EquivalentTo(KnxConnectionProfile.Plain(Gateway, Assigned)), "Secrets leaked or transport kind compared equal.");
        return Task.CompletedTask;
    }
    private static Task Kdf()
    {
        var bytes = Encoding.ASCII.GetBytes("user4"); var a = KnxIpSecureCodec.DeriveUserKey(bytes); var b = KnxIpSecureCodec.DeriveUserKey("user4");
        Require(a.SequenceEqual(b), "Span KDF changed the wire key.");
        foreach (var bad in new byte[][] { [0x1f], [0x7f], [0xc3, 0xa4], new byte[257] })
        { var refused = false; try { KnxIpSecureCodec.DeriveDeviceAuthenticationKey(bad); } catch (ArgumentException) { refused = true; } Require(refused, "Invalid password bytes accepted."); }
        CryptographicOperations.ZeroMemory(a); CryptographicOperations.ZeroMemory(b); return Task.CompletedTask;
    }
    private static async Task Serials()
    {
        using var temp = new Temporary();
        var outputs = await Task.WhenAll(Enumerable.Range(0, 48).Select(_ => Task.Run(() => new FileKnxClientSerialProvider(temp.Root).ReadOrCreate())));
        Require(outputs.All(value => value.Length == 6 && value.SequenceEqual(outputs[0])) && File.ReadAllBytes(Path.Combine(temp.Root,"client.serial")).SequenceEqual(outputs[0]), "Concurrent callers observed different or incomplete identities.");
        File.WriteAllBytes(Path.Combine(temp.Root,"client.serial"), [1,2]);
        await Failure<KnxConnectionException>(() => Task.Run(() => new FileKnxClientSerialProvider(temp.Root).ReadOrCreate()));
        Require(File.ReadAllBytes(Path.Combine(temp.Root,"client.serial")).SequenceEqual(new byte[] {1,2}), "Malformed identity was silently reset.");
    }
    private static string SignedFixture() => (string)typeof(ProtectedKnxKeyringChecks).GetField("Rich", BindingFlags.Static|BindingFlags.NonPublic)!.GetRawConstantValue()!;
    private static async Task Credentials()
    {
        using var temp = new Temporary(); var path = Path.Combine(temp.Root,"test.knxkeys"); await File.WriteAllTextAsync(path, SignedFixture()); var password = new Password("pwd");
        var reference = new SecureKeyringReference(path,"BusChord synthetic keyring",new(1,2,0),4,new(1,2,10));
        using var scope = await new ProtectedKeyringCredentialProvider().AcquireAsync(reference,password,default);
        Require(password.Owned!.All(value => value == '\0') && scope.ProtectedGroups.OrderBy(group=>group.Raw).SequenceEqual(new GroupAddress[] { new(4660), new(17185) }), "Password copy retained or known protected group outside selected interface was lost.");
        await using(var boundary=new KeyringGroupBoundary(scope.ProtectedGroups))
            await Failure<NotSupportedException>(()=>boundary.PrepareOutgoingAsync(Group(17185,[0,0]),Assigned,default));
        Require(scope.SourceFingerprint == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))), "Source hash not exact.");
        var fields = typeof(KnxSecureCredentialScope).GetFields(BindingFlags.NonPublic|BindingFlags.Instance).Where(field => field.FieldType == typeof(byte[])).Select(field => (byte[])field.GetValue(scope)!).ToArray();
        scope.Dispose(); Require(fields.All(bytes => bytes.All(value => value == 0)), "Derived capability survived disposal.");
        await Failure<ObjectDisposedException>(() => Task.Run(() => scope.CreateSession(new byte[6])));
    }
    private static async Task RejectedCredentials()
    {
        using var temp = new Temporary(); var path = Path.Combine(temp.Root,"test.knxkeys"); await File.WriteAllTextAsync(path,SignedFixture()); var transports=0;
        var factory = new KnxConnectionFactory(new(temp.Root),createTransport:_=>{transports++;throw new Exception("Transport must not open.");});
        foreach(var item in new[] { ("wrong",new IndividualAddress(1,2,0), (byte)4, new IndividualAddress(1,2,10)), ("pwd",new IndividualAddress(1,2,1),(byte)4,new IndividualAddress(1,2,10)) })
        {
            var reference=new SecureKeyringReference(path,"BusChord synthetic keyring",item.Item2,item.Item3,item.Item4);
            await Failure<KnxConnectionException>(async()=>await factory.AcquireOperationAsync(KnxConnectionProfile.IpSecure(Gateway,reference,new Password(item.Item1))));
        }
        Require(transports==0,"Invalid credentials reached the network factory.");
    }
    private static async Task FreshOpens()
    {
        using var temp=new Temporary(); var peers=new List<Peer>(); var provider=new CredentialsProvider(); var factory=Factory(temp,provider,()=>{var p=new Peer();peers.Add(p);return p;});
        using var initial=new CancellationTokenSource(); await using var operation=await factory.AcquireOperationAsync(Profile(temp.File),initial.Token);
        Require(peers.Count==0 && provider.Acquisitions==1,"Acquisition emitted network frames."); initial.Cancel();
        await using(var first=await operation.OpenTunnelAsync()) Require(first.Security.GatewayAuthenticated && first.Security.AssignedAddress==Assigned,"Secure handshake was not authenticated.");
        await using(var second=await operation.OpenTunnelAsync()) Require(second.Security.Transport==KnxTransportKind.IpSecureUdp,"Fresh recovery open downgraded.");
        await using(var management=await operation.OpenManagementAsync(new(1,1,1),readOnly:true))
            Require(management.Client.IsReadOnly && management.Client.Target==new IndividualAddress(1,1,1),"Management bypassed selected operation or lost read-only mode.");
        Require(provider.Acquisitions==1 && peers.Count==3 && peers.All(p=>p.Count("PlainSends")==1 && p.Count("Disposals")==1),"Fresh opens reprompted, fell back or leaked transport.");
    }
    private static async Task AuthenticationFailure()
    {
        using var temp=new Temporary();var peer=new Peer();peer.Set("BadGateway",true);await using var operation=await Factory(temp,new(),()=>peer).AcquireOperationAsync(Profile(temp.File));
        var error=await Failure<KnxConnectionException>(()=>operation.OpenTunnelAsync());
        Require(error.Failure==KnxConnectionFailure.AuthenticationFailed && peer.Count("PlainSends")==1 && peer.Count("ApplicationSends")==0 && peer.Count("Disposals")==1,"Auth error fell back, leaked or was misclassified.");
    }
    private static async Task AddressMismatch()
    {
        using var temp=new Temporary();var peer=new Peer();await using var operation=await Factory(temp,new(),()=>peer).AcquireOperationAsync(Profile(temp.File,new(1,1,241)));
        var error=await Failure<KnxConnectionException>(()=>operation.OpenTunnelAsync());Require(error.Failure==KnxConnectionFailure.AssignedAddressMismatch && peer.Count("Disposals")==1,"Assigned interface mismatch accepted.");
    }
    private static async Task JoinedDisposal()
    {
        using var temp=new Temporary();var peer=new Peer();var factory=Factory(temp,new(),()=>peer);var operation=await factory.AcquireOperationAsync(Profile(temp.File));await operation.OpenTunnelAsync();
        peer.Set("HoldReceiveUnwind",true);var first=operation.DisposeAsync().AsTask();var second=operation.DisposeAsync().AsTask();await peer.Signal("ReceiveCancelled").Task;
        var error=await Failure<KnxConnectionException>(async()=>await factory.AcquireOperationAsync(KnxConnectionProfile.Plain(Gateway,Assigned)));
        Require(!first.IsCompleted && !second.IsCompleted && error.Failure==KnxConnectionFailure.Occupied,"Lease escaped before I/O joined.");
        peer.Signal("ReleaseReceive").TrySetResult();await Task.WhenAll(first,second);Require(peer.Count("Disposals")==1,"Concurrent disposal closed twice.");
        await using var next=await factory.AcquireOperationAsync(KnxConnectionProfile.Plain(Gateway,Assigned));
    }
    private static async Task Facade()
    {
        using var temp=new Temporary();var peer=new Peer();var factory=Factory(temp,new(),()=>peer);var bus=new KnxBus(factory);await bus.ConnectAsync(Profile(temp.File));
        var count=peer.Count("TotalSends");var reused=await bus.WithDiagnosticsAsync((_,_)=>Task.FromResult(bus.Security?.AssignedAddress==Assigned));
        Require(reused && peer.Count("TotalSends")==count && !KnxBus.WritesEnabled,"Diagnostics replaced the authenticated session or enabled writes.");
        peer.Set("HoldReceiveUnwind",true);var a=bus.DisposeAsync().AsTask();var b=bus.DisposeAsync().AsTask();await peer.Signal("ReceiveCancelled").Task;
        await Failure<KnxConnectionException>(()=>bus.ConnectAsync(Profile(temp.File)));Require(!a.IsCompleted&&!b.IsCompleted,"Facade abandoned joined cleanup.");
        peer.Signal("ReleaseReceive").TrySetResult();await Task.WhenAll(a,b);Require(peer.Count("Disposals")==1 && bus.State==BusConnectionState.Disconnected,"Facade did not close once.");
    }
    private static byte[] Group(ushort address,byte[] apdu,CemiMessageCode code=CemiMessageCode.LDataReq)=>Cemi.EncodeLDataFrame(new(code,Cemi.Ctrl1Standard,Cemi.Ctrl2Group,Assigned,address,apdu));
    private static async Task GroupBoundary()
    {
        await using var policy=new KeyringGroupBoundary([new(4660),new(17185)]);
        foreach(var group in new ushort[]{4660,17185})
        foreach(var apdu in new byte[][] { [0,0], [0,0x40,1], [0,0x80,1], [3,0xf1,0,0] })
        {
            await Failure<NotSupportedException>(()=>policy.PrepareOutgoingAsync(Group(group,apdu),Assigned,default));
            Require(await policy.ProcessIncomingAsync(Group(group,apdu,CemiMessageCode.LDataInd),Assigned,default) is null,"Protected incoming value escaped without group verification.");
        }
        Require(await policy.ProcessIncomingAsync(Group(4661,[3,0xf1,0,0],CemiMessageCode.LDataInd),Assigned,default) is null,"Unverified S-A_Data was dispatched.");
        Require((await policy.PrepareOutgoingAsync(Group(4661,[0,0]),Assigned,default)).Length>0,"Explicitly plain group was unnecessarily blocked.");
    }
    private static async Task TunnelBoundary()
    {
        using var temp=new Temporary();var peer=new Peer();await using var operation=await Factory(temp,new([new(4660),new(17185)]),()=>peer).AcquireOperationAsync(Profile(temp.File));await using var owned=await operation.OpenTunnelAsync();var sends=peer.Count("ApplicationSends");
        foreach(var group in new ushort[]{4660,17185})
        {
            await Failure<NotSupportedException>(()=>owned.Tunnel.GroupValueReadAsync(new(group)));
            await Failure<NotSupportedException>(()=>owned.Tunnel.GroupValueWriteAsync(new(group),[1]));
            await Failure<NotSupportedException>(()=>owned.Tunnel.SendTelegramAsync(new Telegram {Source=Assigned,Destination=new(group),IsGroupAddress=true,Apci=Apci.GroupValueResponse,Data=[1]}));
        }
        Require(peer.Count("ApplicationSends")==sends,"A known protected group was sent in clear through a public tunnel API.");
    }
    private static KnxConnectionFactory Factory(Temporary temp,CredentialsProvider provider,Func<Peer> peer)=>new(new(temp.Root),provider,new Serial(),_=>peer().Transport,(_,_)=>Local);
    private sealed class Serial:IKnxClientSerialProvider { public byte[] ReadOrCreate()=>[1,2,3,4,5,6]; }
    private sealed class CredentialsProvider(IEnumerable<GroupAddress>? groups=null):IKnxSecureCredentialProvider
    {
        public int Acquisitions;
        public Task<KnxSecureCredentialScope> AcquireAsync(SecureKeyringReference r,ISecurePasswordSource? source,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();Acquisitions++;return Task.FromResult(new KnxSecureCredentialScope(r.HostAddress,r.TunnelAddress,r.UserId,Fingerprint,User,Device,groups));}
    }
    private sealed class Password(string value):ISecurePasswordSource
    {public char[]? Owned;public ValueTask<char[]> ReadOwnedAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();return ValueTask.FromResult(Owned=value.ToCharArray());}public override string ToString()=>"[test password source]";}
    private sealed class Temporary:IDisposable
    {public string Root{get;}=Path.Combine(Path.GetTempPath(),"buschord-connection-tests-"+Guid.NewGuid().ToString("N"));public string File=>Path.Combine(Root,"fixture.knxkeys");public Temporary()=>Directory.CreateDirectory(Root);public void Dispose()=>Directory.Delete(Root,true);}
    private sealed class Peer
    {
        private static readonly Type Type=typeof(IndependentSecureGateway);
        private readonly object _peer=Activator.CreateInstance(Type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[null],null)!;
        public IUdpTransport Transport=>(IUdpTransport)_peer;
        public void Set(string name,bool value)=>Type.GetField(name)!.SetValue(_peer,value);
        public int Count(string name)=>(int)Type.GetField(name)!.GetValue(_peer)!;
        public TaskCompletionSource Signal(string name)=>(TaskCompletionSource)Type.GetProperty(name)!.GetValue(_peer)!;
    }
}
