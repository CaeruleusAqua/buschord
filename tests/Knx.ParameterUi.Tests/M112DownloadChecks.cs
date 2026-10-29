using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Knx;
using Knx.Bus;

internal static class M112DownloadChecks
{
    internal static async Task<int> RunAsync(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "M112Download " + name); if (!ok) failures++; }
        var fixture = M112UnionBuildChecks.Fixture(directory, fullLoad: true);
        M112ApplicationImage image;
        try { image = OfflineM112ApplicationBuild.Build(fixture.Project, fixture.Device); }
        catch (Exception ex) { Console.WriteLine(ex); Check("synthetic full-load source compiles", false); return failures; }
        Check("source-bound full-load image includes all 37 unions", image.UnionCount == 37 && image.Segments.Count == 5 && image.IsCurrent(fixture.Project, fixture.Device));
        Check("declared memory-control route is selected", image.LoadControlAddress == 0x104 && image.LoadStatusAddresses.SequenceEqual(new ushort[] { 0xB6EA, 0xB6EB, 0xB6EC }));
        Check("public unload vector", Convert.ToHexString(M112ApplicationDownload.SimpleControl(1, 4)) == "1400000000000000000000");
        async Task<M112DownloadResult> Run(FakeDevice device, CancellationToken ct = default)
            => await M112ApplicationDownload.RunAsync(device.OpenAsync, fixture.Project, fixture.Device, image, device.Identity, "1.1.1", directory, ct: ct);
        var normal = new FakeDevice(image); var normalBefore = (byte[])normal.Memory.Clone();
        var result = await Run(normal);
        Check("full download is independently verified", result.DownloadedAndVerified && !result.RollbackAttempted && result.Error is null);
        Check("offline reevaluation is separated by joined fresh management sessions", normal.Opens == 3 && normal.Disposals == 3);
        Check("all commands use declared route and exactly 11 bytes", normal.Controls.All(c => c.Length == 11) && normal.Controls.Count == 17);
        Check("full order unloads first, then completes one LSM at a time", normal.Controls.Select(c => c[0]).SequenceEqual(new byte[] { 0x14,0x24,0x34,0x11,0x13,0x13,0x12,0x21,0x23,0x23,0x22,0x31,0x33,0x33,0x33,0x33,0x32 }));
        Check("RAM and stack are allocated without writing captured runtime bytes", normal.DataWriteAddresses.All(a => a >= 0x4000) && normal.Memory.AsSpan(0x700,9).SequenceEqual(normalBefore.AsSpan(0x700,9)));
        Check("task metadata is transmitted in BE order", Convert.ToHexString(normal.Controls[5]) == "1302004000010001010010");
        Check("memory route reserves byte two before absolute payload", Convert.ToHexString(normal.Controls[4]) == "13000040000010FF038000");
        Check("individual address survives image composition", normal.Memory[0x4001] == 0x11 && normal.Memory[0x4002] == 1);
        Check("before-image is durable and separate from the repository", File.Exists(result.RecoveryPath) && new FileInfo(result.RecoveryPath).Length > 256);
        var recovered = await M112ApplicationDownload.RecoverAsync(normal.OpenAsync, result.RecoveryPath, normal.Identity, "1.1.1");
        Check("explicit recovery uses captured original bytes", recovered.OriginalConfigurationRestoredAndVerified && !recovered.DownloadedAndVerified
            && image.Segments.All(s => normal.Memory.AsSpan(s.Address, s.Size).SequenceEqual(normalBefore.AsSpan(s.Address, s.Size))));
        var asynchronous = new FakeDevice(image) { Transitions = true, AutoVerify = true };
        var asynchronousResult = await Run(asynchronous);
        Check("asynchronous unload/completion states are polled to completion", asynchronousResult.DownloadedAndVerified && asynchronous.PollsDuringTransition > 0);
        Check("write-only controls need no echo even with device AutoVerify", asynchronousResult.Error is null && asynchronous.Controls.Count == 17);

        foreach (var fault in new[] { "write", "verify", "complete", "restart", "postrestart", "pei", "pointer" })
        {
            var faulty = new FakeDevice(image) { Fault = fault }; var original = (byte[])faulty.Memory.Clone();
            var failed = await Run(faulty);
            Check(fault + " failure invokes a fresh rollback connection", !failed.DownloadedAndVerified && failed.RollbackAttempted && failed.OriginalConfigurationRestoredAndVerified && failed.Error is not null && faulty.Opens >= 2);
            Check(fault + " rollback restores all persistent bytes", image.Segments.All(s => faulty.Memory.AsSpan(s.Address, s.Size).SequenceEqual(original.AsSpan(s.Address, s.Size))) && faulty.Loaded);
        }
        var cancelled = new FakeDevice(image); using var cancel = new CancellationTokenSource(); cancelled.CancelOnWrite = cancel;
        var cancellation = await Run(cancelled, cancel.Token);
        Check("caller cancellation cannot cancel rollback", !cancellation.DownloadedAndVerified && cancellation.RollbackAttempted && cancellation.OriginalConfigurationRestoredAndVerified && cancelled.Loaded);
        var wrong = new FakeDevice(image) { WrongHardware = true }; var refused = await Run(wrong);
        Check("preflight mismatch sends no writes or unloads", !refused.DownloadedAndVerified && !refused.RollbackAttempted && wrong.Controls.Count == 0 && wrong.Writes == 0);
        var changed = new FakeDevice(image); changed.Memory[0x4420] ^= 1; var changedResult = await Run(changed);
        Check("changed persistent configuration is refused before unload", !changedResult.DownloadedAndVerified && !changedResult.RollbackAttempted && changed.Writes == 0);
        var blockedRecovery = new FakeDevice(image) { Fault = "write", WrongIdentityOnRecovery = true }; var blockedResult = await Run(blockedRecovery);
        Check("recovery identity mismatch retains a recoverable journal and reports failure", blockedResult.RollbackAttempted && !blockedResult.OriginalConfigurationRestoredAndVerified
            && File.Exists(blockedResult.RecoveryPath) && blockedRecovery.Controls.Count == 6 && blockedResult.Error!.Contains("Recovery requires attention"));
        var corruptPath = Path.Combine(Path.GetDirectoryName(result.RecoveryPath)!, "corrupt.json");
        var corrupt = JsonNode.Parse(await File.ReadAllTextAsync(result.RecoveryPath))!; corrupt["Snapshot"]!["PeiType"] = 2; await File.WriteAllTextAsync(corruptPath, corrupt.ToJsonString());
        var corruptRefused = false; var opens = normal.Opens;
        try { await M112ApplicationDownload.RecoverAsync(normal.OpenAsync, corruptPath, normal.Identity, "1.1.1"); } catch (InvalidDataException) { corruptRefused = true; }
        Check("tampered recovery data is refused before opening bus", corruptRefused && normal.Opens == opens);
        var wrongAddress = JsonNode.Parse(await File.ReadAllTextAsync(result.RecoveryPath))!;
        var wrongAddressBytes = Convert.FromBase64String(wrongAddress["Snapshot"]!["Segments"]![0]!["Before"]!.GetValue<string>());
        wrongAddressBytes[2] ^= 1; wrongAddress["Snapshot"]!["Segments"]![0]!["Before"] = Convert.ToBase64String(wrongAddressBytes);
        await File.WriteAllTextAsync(corruptPath, wrongAddress.ToJsonString());
        var addressRefused = false;
        try { await M112ApplicationDownload.RecoverAsync(normal.OpenAsync, corruptPath, normal.Identity, "1.1.1"); }
        catch (InvalidDataException ex) { addressRefused = ex.Message.Contains("individual address"); }
        Check("recovery rejects an address change independently of its checksum", addressRefused && normal.Opens == opens);
        fixture.Device.ParameterValues["Ref0"] = "6"; var staleRefused = false;
        try { await Run(normal); } catch (InvalidOperationException) { staleRefused = true; }
        Check("edited project cannot reuse a previously reviewed image", staleRefused && normal.Opens == opens);
        Console.WriteLine($"M112Download checks={checks}; failures={failures}");
        return failures + await RunChangesAsync(directory);
    }

    internal static async Task<int> RunChangesAsync(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "M112Changes " + name); if (!ok) failures++; }
        var baseline = M112UnionBuildChecks.Fixture(directory, fullLoad: true);
        var desired = M112UnionBuildChecks.Fixture(directory, fullLoad: true);
        desired.Device.ParameterValues["Ref0"] = "6";
        var range = new GroupRange { Id = "ChangeRange", Name = "Synthetic changes", RangeStart = 0, RangeEnd = 65535 };
        range.Addresses.Add(new GroupAddressEntry { Id = "ChangedGA", Name = "Changed group", Address = new(0x1234) });
        desired.Project.Installations.Single().MainRanges.Add(range);
        desired.Project.Workbench.Groups["ChangedGA"] = new() { Secure = false };
        var obj = new ComObjectInstanceRef { Id = "O-0_R-0", ComObjectId = "O-0", Number = 0 };
        obj.Links.Add("ChangedGA"); desired.Device.ComObjectRefs.Add(obj);
        var plan = M112ApplicationChangePlan.Prepare(baseline.Project, baseline.Device, desired.Project, desired.Device);
        Check("independent baseline and desired images share one immutable layout", plan.Baseline.LayoutFingerprint == plan.Desired.LayoutFingerprint && plan.IsCurrent());
        Check("review includes the changed parameter and generated tables", plan.HasChanges && plan.Changes.Any(c => c.Address <= 0x4420 && c.Address + c.Length > 0x4420)
            && plan.Changes.Any(c => c.Address == 0x4000) && plan.Changes.Any(c => c.Address == 0x4201));
        Check("a review cannot mutate owned image bytes", plan.Changes.All(c => c.Length == c.DesiredValidity.Length && c.Length == c.BaselineData.Length));
        var live = new FakeDevice(plan.Baseline); var original = (byte[])live.Memory.Clone();
        var applied = await M112ApplicationDownload.ApplyChangesAsync(live.OpenAsync, plan, plan.Fingerprint, live.Identity, "1.1.1", directory);
        Check("changed configuration is written and independently verified: " + applied.Error, applied.DownloadedAndVerified && !applied.RollbackAttempted && applied.Error is null);
        if (!applied.DownloadedAndVerified) return failures;
        Check("changed alias reaches EEPROM", live.Memory[0x4420] == 0x61 && live.Memory[0x4421] == 0xB2);
        Check("new group address reaches the actual table", live.Memory[0x4000] == 2 && live.Memory[0x4003] == 0x12 && live.Memory[0x4004] == 0x34);
        Check("new association is serialized", live.Memory[0x4201] == 1 && live.Memory[0x4202] == 1 && live.Memory[0x4203] == 0);
        Check("manufacturer RAM pointers remain unchanged", Enumerable.Range(0, plan.Desired.Objects.Count).All(n => live.Memory[0x4403+4*n] == original[0x4403+4*n]
            && live.Memory[0x4404+4*n] == original[0x4404+4*n]));
        Check("source project states are never overwritten by the download", !baseline.Device.ParameterValues.ContainsKey("Ref0") && desired.Device.ParameterValues["Ref0"] == "6");
        var restored = await M112ApplicationDownload.RecoverAsync(live.OpenAsync, applied.RecoveryPath, live.Identity, "1.1.1");
        Check("explicit recovery restores the distinct original configuration", restored.OriginalConfigurationRestoredAndVerified
            && plan.Baseline.Segments.Where(s => s.MemoryType == 3).All(s => live.Memory.AsSpan(s.Address,s.Size).SequenceEqual(original.AsSpan(s.Address,s.Size))));
        foreach (var fault in new[] { "write", "verify", "complete", "restart", "postrestart", "pei", "pointer" })
        {
            var broken = new FakeDevice(plan.Baseline) { Fault = fault }; var before = (byte[])broken.Memory.Clone();
            var result = await M112ApplicationDownload.ApplyChangesAsync(broken.OpenAsync, plan, plan.Fingerprint, broken.Identity, "1.1.1", directory);
            Check(fault + " restores a different baseline after a changed-image failure", !result.DownloadedAndVerified && result.RollbackAttempted && result.OriginalConfigurationRestoredAndVerified
                && plan.Baseline.Segments.Where(s => s.MemoryType == 3).All(s => broken.Memory.AsSpan(s.Address,s.Size).SequenceEqual(before.AsSpan(s.Address,s.Size))));
        }
        var drifted = new FakeDevice(plan.Baseline); drifted.Memory[0x4420] ^= 1;
        var refused = await M112ApplicationDownload.ApplyChangesAsync(drifted.OpenAsync, plan, plan.Fingerprint, drifted.Identity, "1.1.1", directory);
        Check("unreviewed live drift is refused before any write", !refused.DownloadedAndVerified && !refused.RollbackAttempted && drifted.Writes == 0);
        var wrongReview = new FakeDevice(plan.Baseline); var wrongReviewRefused = false;
        try { await M112ApplicationDownload.ApplyChangesAsync(wrongReview.OpenAsync, plan, "wrong", wrongReview.Identity, "1.1.1", directory); }
        catch (InvalidOperationException) { wrongReviewRefused = true; }
        Check("exact reviewed-plan confirmation is required before opening the bus", wrongReviewRefused && wrongReview.Opens == 0);
        var disposedMutation = new FakeDevice(plan.Baseline) { OnFirstDispose = () => baseline.Device.ParameterValues["Ref1"] = "9" };
        var disposedMutationResult = await M112ApplicationDownload.ApplyChangesAsync(disposedMutation.OpenAsync, plan, plan.Fingerprint,
            disposedMutation.Identity, "1.1.1", directory);
        Check("source drift after the initial session is joined prevents reopening and unloading", !disposedMutationResult.DownloadedAndVerified
            && !disposedMutationResult.RollbackAttempted && disposedMutation.Writes == 0 && disposedMutation.Opens == 1 && disposedMutation.Disposals == 1);
        baseline.Device.ParameterValues.Remove("Ref1");
        var typeDrift = new FakeDevice(plan.Baseline); typeDrift.Memory[0x4406] = 7;
        var typeDriftResult = await M112ApplicationDownload.ApplyChangesAsync(typeDrift.OpenAsync, plan, plan.Fingerprint, typeDrift.Identity, "1.1.1", directory);
        Check("active unlinked baseline types are checked from actual device bytes", !typeDriftResult.DownloadedAndVerified && !typeDriftResult.RollbackAttempted && typeDrift.Writes == 0
            && typeDriftResult.Error!.Contains("type differs"));
        foreach (var mutation in new[] { "baseline", "desired", "device" })
        {
            var concurrent = new FakeDevice(plan.Baseline);
            concurrent.OnSecondPersistentRead = () =>
            {
                if (mutation == "baseline") baseline.Device.ParameterValues["Ref1"] = "9";
                if (mutation == "desired") desired.Device.ParameterValues["Ref1"] = "9";
                if (mutation == "device") concurrent.Memory[0x4420] ^= 1;
            };
            var concurrentResult = await M112ApplicationDownload.ApplyChangesAsync(concurrent.OpenAsync, plan, plan.Fingerprint, concurrent.Identity, "1.1.1", directory);
            Check(mutation + " mutation during final readback is refused before unload", !concurrentResult.DownloadedAndVerified && !concurrentResult.RollbackAttempted && concurrent.Writes == 0);
            baseline.Device.ParameterValues.Remove("Ref1"); desired.Device.ParameterValues.Remove("Ref1");
        }

        var narrow = M112UnionBuildChecks.Fixture(directory, fullLoad: true, fullEdit: ObjectVariants);
        var wide = M112UnionBuildChecks.Fixture(directory, fullLoad: true, fullEdit: ObjectVariants); wide.Device.ParameterValues["Ref0"] = "6";
        var sizes = M112ApplicationChangePlan.Prepare(narrow.Project, narrow.Device, wide.Project, wide.Device);
        Check("parameter evaluation changes both object size and activation", sizes.Baseline.Objects[3].SizeInBit == 16 && sizes.Desired.Objects[3].SizeInBit == 32
            && !sizes.Baseline.Objects[4].Active && sizes.Desired.Objects[4].Active);
        var sizedDevice = new FakeDevice(sizes.Baseline);
        var sizedResult = await M112ApplicationDownload.ApplyChangesAsync(sizedDevice.OpenAsync, sizes, sizes.Fingerprint, sizedDevice.Identity, "1.1.1", directory);
        Check("unlinked C0 object size changes reach the real table writer", sizedResult.DownloadedAndVerified && (sizedDevice.Memory[0x4412] & 63) == 10
            && (sizedDevice.Memory[0x4411] & 4) == 0 && sizedDevice.Memory[0x440F] == 7 && sizedDevice.Memory[0x4410] == 0x1C);
        foreach (var failure in new[] { "status-overlap", "value-overlap", "ram-end", "selector" })
        {
            void BrokenLayout(System.Xml.Linq.XElement p)
            {
                ObjectVariants(p);
                var ns = p.Name.Namespace;
                var code = p.Descendants(ns + "AbsoluteSegment").Single(e => (string?)e.Attribute("Id") == "KO").Element(ns + "Data")!;
                var bytes = Convert.FromBase64String(code.Value);
                if (failure == "status-overlap") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(3), 0x0701);
                if (failure == "value-overlap") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(3 + 4 * 4), 0x071E);
                if (failure == "ram-end") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(3 + 4 * 3), 0x073E);
                if (failure == "selector") bytes[5] |= 0x20;
                code.Value = Convert.ToBase64String(bytes);
            }
            var start = M112UnionBuildChecks.Fixture(directory, fullLoad: true, fullEdit: BrokenLayout);
            var end = M112UnionBuildChecks.Fixture(directory, fullLoad: true, fullEdit: BrokenLayout); end.Device.ParameterValues["Ref0"] = "6";
            var badPlan = M112ApplicationChangePlan.Prepare(start.Project, start.Device, end.Project, end.Device);
            var badDevice = new FakeDevice(badPlan.Baseline);
            var badResult = await M112ApplicationDownload.ApplyChangesAsync(badDevice.OpenAsync, badPlan, badPlan.Fingerprint, badDevice.Identity, "1.1.1", directory);
            Check(failure + " is refused before unloading, including unlinked active objects", !badResult.DownloadedAndVerified && !badResult.RollbackAttempted
                && badDevice.Writes == 0 && badResult.Error is not null);
        }
        baseline.Device.ParameterValues["Ref1"] = "7"; var stale = false;
        try { await M112ApplicationDownload.ApplyChangesAsync(wrongReview.OpenAsync, plan, plan.Fingerprint, wrongReview.Identity, "1.1.1", directory); }
        catch (InvalidOperationException) { stale = true; }
        Check("editing the baseline invalidates the proposal", stale && wrongReview.Opens == 0);
        baseline.Device.ParameterValues.Remove("Ref1"); desired.Device.ParameterValues["Ref1"] = "8";
        Check("editing the target also invalidates the proposal", !plan.IsCurrent());
        Console.WriteLine($"M112Changes checks={checks}; failures={failures}"); return failures;

        static void ObjectVariants(System.Xml.Linq.XElement program)
        {
            var ns = program.Name.Namespace;
            var references = program.Element(ns + "Static")!.Element(ns + "ComObjectRefs")!;
            var original = references.Elements().Single(e => ((string?)e.Attribute("Id"))!.EndsWith("_O-3_R-0"));
            var originalId = (string)original.Attribute("Id")!;
            var wide = new System.Xml.Linq.XElement(original); wide.SetAttributeValue("Id", "WideRef3"); wide.SetAttributeValue("ObjectSize", "4 Bytes");
            var fourth = program.Descendants(ns + "ComObject").Single(e => (string?)e.Attribute("Number") == "4");
            references.Add(wide, new System.Xml.Linq.XElement(ns + "ComObjectRef", new System.Xml.Linq.XAttribute("Id", "ActiveRef4"),
                new System.Xml.Linq.XAttribute("RefId", (string)fourth.Attribute("Id")!)));
            var placement = program.Descendants(ns + "ComObjectRefRef").Single(e => (string?)e.Attribute("RefId") == originalId);
            placement.ReplaceWith(new System.Xml.Linq.XElement(ns + "choose", new System.Xml.Linq.XAttribute("ParamRefId", "Ref0"),
                new System.Xml.Linq.XElement(ns + "when", new System.Xml.Linq.XAttribute("test", "5"), new System.Xml.Linq.XElement(placement)),
                new System.Xml.Linq.XElement(ns + "when", new System.Xml.Linq.XAttribute("test", "6"),
                    new System.Xml.Linq.XElement(ns + "ComObjectRefRef", new System.Xml.Linq.XAttribute("RefId", "WideRef3")),
                    new System.Xml.Linq.XElement(ns + "ComObjectRefRef", new System.Xml.Linq.XAttribute("RefId", "ActiveRef4")))));
        }
    }

    internal sealed class FakeDevice
    {
        internal readonly byte[] Memory = new byte[65536];
        internal readonly List<byte[]> Controls = [];
        internal readonly List<ushort> DataWriteAddresses = [];
        internal readonly ManagementDeviceIdentity Identity = new("1.1.1", 0x0701, "0001", "000100000001", "0001010010");
        internal int Opens, Writes, Disposals;
        internal string? Fault;
        internal bool WrongHardware;
        internal bool RejectApplicationWhileUnloaded;
        internal int UnloadedApplicationReadAttempts;
        internal bool WrongIdentityOnRecovery, AutoVerify, Transitions;
        internal int PollsDuringTransition;
        internal CancellationTokenSource? CancelOnWrite;
        internal Action? OnSecondPersistentRead;
        internal Action? OnFirstDispose;
        internal bool Loaded => Memory[0xB6EA] == 1 && Memory[0xB6EB] == 1 && Memory[0xB6EC] == 1;
        private readonly M112ApplicationImage _image;
        private bool _faulted, _wrote, _restarted;
        private readonly Dictionary<int, (byte Final, int Remaining)> _transitions = [];
        private int _persistentReads;
        internal FakeDevice(M112ApplicationImage image)
        {
            _image = image;
            foreach (var segment in image.Segments)
                if (segment.MemoryType == 3) segment.Data.ToArray().CopyTo(Memory, segment.Address);
                else Array.Fill(Memory, (byte)0xA5, segment.Address, segment.Size);
            Memory[0x4001] = 0x11; Memory[0x4002] = 1;
            Memory[0xB6EA] = Memory[0xB6EB] = Memory[0xB6EC] = 1;
        }
        internal async Task<IManagementConnection> OpenAsync(CancellationToken ct)
        {
            Opens++;
            var client = new KnxManagementClient(new Transport(this), IndividualAddress.Parse(Identity.Address), TimeSpan.FromMilliseconds(100));
            await client.ConnectAsync(ct); return new Connection(this, client);
        }
        private sealed class Connection(FakeDevice owner, KnxManagementClient client) : IManagementConnection
        {
            public KnxManagementClient Client => client;
            public async ValueTask DisposeAsync() { await client.DisposeAsync(); if (++owner.Disposals == 1) owner.OnFirstDispose?.Invoke(); }
        }
        private sealed class Transport(FakeDevice device) : IManagementTransport
        {
            public IndividualAddress AssignedAddress => new(1, 1, 250);
            public bool IsConnected => true;
            public event Action<ManagementTransportFrame>? FrameReceived;
            private byte _responseSequence;
            public Task SendAsync(IndividualAddress target, byte[] tpdu, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (tpdu.Length == 1) return Task.CompletedTask;
                var service = ManagementApdu.Service(tpdu);
                var address = tpdu.Length >= 4 ? BinaryPrimitives.ReadUInt16BigEndian(tpdu.AsSpan(2)) : (ushort)0;
                if (service == 0x280 && address != 0x104 && device.Fault == "write" && !device._faulted)
                { device._faulted = true; throw new IOException("Injected uncertain write"); }
                if (service == 0x380 && device.Fault == "restart" && !device._faulted)
                { device._faulted = true; throw new IOException("Injected restart transport failure"); }
                Emit([(byte)(0xC2 | (tpdu[0] & 0x3C))]);
                if (service == 0x300) Response([0x03, 0x40, 0x07, 0x01]);
                else if (service == 0x200)
                {
                    if (address == 0x4000 && ++device._persistentReads == 2) device.OnSecondPersistentRead?.Invoke();
                    if (device._transitions.TryGetValue(address, out var transition))
                    {
                        if (transition.Remaining == 0) { device.Memory[address] = transition.Final; device._transitions.Remove(address); }
                        else { device.PollsDuringTransition++; device._transitions[address] = (transition.Final, transition.Remaining - 1); }
                    }
                    var count = tpdu[1] & 63; var payload = device.Memory.AsSpan(address, count).ToArray();
                    if (device._wrote && address != 0x104 && address < 0xB000 && device.Fault == "verify" && !device._faulted)
                    { device._faulted = true; payload[0] ^= 1; }
                    Response([0x02, (byte)(0x40 | count), tpdu[2], tpdu[3], .. payload]);
                }
                else if (service == 0x280)
                {
                    var payload = tpdu[4..]; device.Writes++;
                    if (address == 0x104)
                    {
                        if (payload.Length != 11 || payload[2] != 0) throw new IOException("Wrong M112 control layout.");
                        device.Controls.Add(payload);
                        var oi = payload[0] >> 4; var command = payload[0] & 15;
                        device.Memory[0xB6E9 + oi] = command switch { 4 => 0, 1 or 3 => 2, 2 => 1, _ => throw new IOException("Unknown command") };
                        if (device.Transitions && command is 2 or 4)
                        {
                            device._transitions[0xB6E9+oi] = (device.Memory[0xB6E9+oi], 2);
                            device.Memory[0xB6E9+oi] = command == 2 ? (byte)5 : (byte)4;
                        }
                        if (command == 2 && device.Fault == "complete" && !device._faulted)
                        { device._faulted = true; device.Memory[0xB6E9 + oi] = 3; }
                    }
                    else
                    {
                        device.DataWriteAddresses.Add(address);
                        payload.CopyTo(device.Memory, address); device._wrote = true;
                        if (device.CancelOnWrite is { } cancel) { device.CancelOnWrite = null; cancel.Cancel(); }
                        if (device.AutoVerify) Response([0x02,(byte)(0x40|payload.Length),tpdu[2],tpdu[3],..payload]);
                    }
                }
                else if (service == 0x380) { device._restarted = true; Emit([0x81]); }
                else if (service == ManagementApdu.PropertyRead)
                {
                    if (tpdu[2] == 3 && tpdu[3] == 13 && device.RejectApplicationWhileUnloaded && !device.Loaded)
                    { device.UnloadedApplicationReadAttempts++; throw new IOException("Application identity is unavailable while unloaded."); }
                    var oi = tpdu[2]; var pid = tpdu[3]; byte[] value = (oi, pid) switch
                    {
                        (0, 12) => [0,1], (0,11) => device.WrongIdentityOnRecovery && device.Opens > 2 ? [0,1,0,0,0,2] : [0,1,0,0,0,1],
                        (3,13) => [0,1,1,0,0x10], (0,14) => [device.AutoVerify ? (byte)4 : (byte)0],
                        (0,78) => device.WrongHardware ? [0,0,0,0,0,2] : device._image.HardwareType.ToArray(),
                        (1 or 2 or 3,5) => [device.Memory[0xB6E9+oi]], (1,7) => [0x40,0], (2,7) => [0x42,1],
                        (3,6) => [1], (3,16) => [1], _ => throw new IOException("Unexpected property read")
                    };
                    if (device._restarted && device.Fault == "postrestart" && !device._faulted && oi == 0 && pid == 78)
                    { device._faulted = true; value = [0,0,0,0,0,2]; }
                    if (device._restarted && device.Fault == "pei" && !device._faulted && oi == 3 && pid == 16)
                    { device._faulted = true; value = [2]; }
                    if (device._restarted && device.Fault == "pointer" && !device._faulted && oi == 2 && pid == 7)
                    { device._faulted = true; value = [0x42,2]; }
                    if (oi == 3 && pid == 13 && !device.Loaded) { Response([0x03,0xD6,oi,pid,(byte)(tpdu[4]&15),tpdu[5]]); return Task.CompletedTask; }
                    Response([0x03,0xD6,oi,pid,tpdu[4],tpdu[5],..value]);
                }
                return Task.CompletedTask;
                void Emit(byte[] bytes) => FrameReceived?.Invoke(new(target, AssignedAddress, bytes));
                void Response(byte[] bytes) { bytes[0] = (byte)((bytes[0]&3)|0x40|(_responseSequence++ << 2)); _responseSequence &= 15; Emit(bytes); }
            }
        }
    }
}
