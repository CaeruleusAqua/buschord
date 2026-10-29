using System.Reflection;
using System.Net;
using System.Security.Cryptography;
using Knx;
using Knx.App;
using Knx.App.Workbench;
using Knx.Bus;

/// <summary>The real queue/compiler/download/receipt path over the existing byte-exact management fake, not a second transport or image engine.</summary>
internal static class RealProgrammingQueueCoreChecks
{
    internal static async Task<int> RunAsync(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "RealQueueCore " + name); if (!ok) failures++; }
        foreach (var failFirst in new[] { false, true })
        {
            var root = Path.Combine(directory, failFirst ? "recover" : "success"); Directory.CreateDirectory(root);
            var before = M112UnionBuildChecks.Fixture(root, fullLoad: true); var after = M112UnionBuildChecks.Fixture(root, fullLoad: true);
            var beforeSecond = Copy(before.Device, "Second", new(1, 1, 2)); var afterSecond = Copy(after.Device, "Second", new(1, 1, 2));
            before.Project.Installations.Single().Areas.Single().Lines.Single().Devices.Add(beforeSecond);
            after.Project.Installations.Single().Areas.Single().Lines.Single().Devices.Add(afterSecond);
            after.Device.ParameterValues["Ref0"] = "6"; afterSecond.ParameterValues["Ref0"] = "7";
            var range = new GroupRange { Id = "QueueRange", Name = "Synthetic queue groups", RangeStart = 0, RangeEnd = 65535 };
            range.Addresses.Add(new GroupAddressEntry { Id = "QueueGA", Name = "Synthetic queued group", Address = new(0x1234) });
            after.Project.Installations.Single().MainRanges.Add(range); after.Project.Workbench.Groups["QueueGA"] = new() { Secure = false };
            foreach (var d in new[] { after.Device, afterSecond }) { var obj = new ComObjectInstanceRef { Id = "O-0_R-0", ComObjectId = "O-0", Number = 0 }; obj.Links.Add("QueueGA"); d.ComObjectRefs.Add(obj); }
            var baselinePath = Path.Combine(root, "baseline.knxproj"); var targetPath = Path.Combine(root, "target.knxproj");
            Knxproj.Save(before.Project, baselinePath); Knxproj.Save(after.Project, targetPath);
            var baselineProject = Knxproj.Load(baselinePath); var targetProject = Knxproj.Load(targetPath);
            before = before with { Project = baselineProject, Device = ProjectQuery.DeviceList(baselineProject).Single(d => d.Id == before.Device.Id), Path = baselinePath };
            after = after with { Project = targetProject, Device = ProjectQuery.DeviceList(targetProject).Single(d => d.Id == after.Device.Id), Path = targetPath };
            beforeSecond = ProjectQuery.DeviceList(baselineProject).Single(d => d.Id == "Second"); afterSecond = ProjectQuery.DeviceList(targetProject).Single(d => d.Id == "Second");
            var images = new[] { before.Device, beforeSecond }.Select(d => OfflineM112ApplicationBuild.Build(before.Project, d)).ToArray();
            var fake = images.Select(i => new M112DownloadChecks.FakeDevice(i)).ToArray();
            // Existing single-device fake's transport/identity is reused unchanged; only its fixture target address is set.
            typeof(M112DownloadChecks.FakeDevice).GetField("Identity", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(fake[1], fake[1].Identity with { Address = "1.1.2" }); fake[1].Memory[0x4002] = 2;
            if (failFirst) fake[0].Fault = "write";
            var originals = fake.Select(d => d.Memory.ToArray()).ToArray();
            var lifetime = new object(); var order = new List<string>(); var adapters = new[] { new Adapter(fake[0], order), new Adapter(fake[1], order) };
            var receipts = new VerifiedM112ReceiptStore(Path.Combine(root, "history"), ProgrammingStateBinding.Create(after.Project, targetPath)!,
                await M112PrivatePaths.HashAsync(targetPath), VerifiedM112ReceiptStore.ModelFingerprint(after.Project));
            await using var programming = new ProgrammingSession(new(), () => after.Project);
            await using var queue = new RealM112ProgrammingQueue(() => new(after.Project, lifetime, false, false), programming,
                id => adapters[id == after.Device.Id ? 0 : 1], receipts);
            foreach (var d in new[] { after.Device, afterSecond }) await queue.EnqueueAsync(d.Id,
                new M112BaselineSelection(before.Project, baselinePath, await M112PrivatePaths.HashAsync(baselinePath)),
                new(IPAddress.Parse("192.0.2.192"), 3671, new(1, 1, 250)), root);
            Check("actual offline compiler enqueues both supported M112 targets with zero writes " + failFirst,
                queue.Snapshot.Jobs.All(j => j.State == RealM112JobState.Reviewed) && fake.All(d => d.Opens == 0 && d.Writes == 0));
            foreach (var job in queue.Snapshot.Jobs)
            {
                await queue.ReadIdentityAsync(job.Id); var ready = queue.Snapshot.Jobs.Single(j => j.Id == job.Id);
                queue.Confirm(job.Id, ready.Address, ready.Fingerprint!, true);
            }
            await queue.RunAsync(queue.Snapshot.BatchFingerprint);
            if (!failFirst)
            {
                Check("real compiler plan serially writes different parameter bytes for both targets", order.SequenceEqual(new[] { "1.1.1", "1.1.2" })
                    && fake[0].Memory[0x4420] == 0x61 && fake[1].Memory[0x4420] == 0x71 && queue.Snapshot.Jobs.All(j => j.State == RealM112JobState.Verified));
                Check("real generated group and association tables reach both fake EEPROMs", fake.All(d => d.Memory[0x4000] == 2 && d.Memory[0x4003] == 0x12
                    && d.Memory[0x4004] == 0x34 && d.Memory[0x4201] == 1 && d.Loaded));
                Check("both jobs use independent readback/fresh verification owners", fake.All(d => d.Opens >= 3 && d.Opens == d.Disposals));
                Check("production cold inspector validates both separate historical receipts", (await receipts.ReadHistoryAsync(after.Project)).Count == 2
                    && programming.DeviceStates.Count == 2 && programming.DeviceStates.Values.All(s => s.Origin == Knx.Workbench.ProgrammingOrigin.Real));
                var restore = queue.Snapshot.Jobs[0]; await queue.PrepareRecoveryAsync(restore.Id); await queue.ReadIdentityAsync(restore.Id);
                restore = queue.Snapshot.Jobs.Single(j => j.Id == restore.Id); queue.Confirm(restore.Id, restore.Address, restore.Fingerprint!, true);
                await queue.RecoverAsync(restore.Id, restore.Address, restore.Fingerprint!);
                Check("explicit original restoration clears desired flags and its durable receipt", queue.Snapshot.Jobs[0].State == RealM112JobState.Restored
                    && programming.DeviceState(after.Device.Id)?.Flags == Knx.Workbench.DeviceLoadFlags.None && (await receipts.ReadHistoryAsync(after.Project)).Count == 1
                    && images[0].Segments.Where(s => s.MemoryType == 3).All(s => fake[0].Memory.AsSpan(s.Address, s.Size).SequenceEqual(originals[0].AsSpan(s.Address, s.Size))));
            }
            else
            {
                Check("uncertain real write restores the complete captured original before queue stop", queue.Snapshot.Jobs[0].State == RealM112JobState.Restored
                    && queue.Snapshot.Jobs[0].Result is { OriginalConfigurationRestoredAndVerified: true } && images[0].Segments.Where(s => s.MemoryType == 3)
                        .All(s => fake[0].Memory.AsSpan(s.Address, s.Size).SequenceEqual(originals[0].AsSpan(s.Address, s.Size))));
                Check("failed real job never starts the second target or grants success history", order.SequenceEqual(new[] { "1.1.1" }) && fake[1].Writes == 0
                    && programming.DeviceStates.Values.All(s => s.Flags == Knx.Workbench.DeviceLoadFlags.None) && (await receipts.ReadHistoryAsync(after.Project)).Count == 0);
            }
        }
        return failures;
    }
    private static DeviceInstance Copy(DeviceInstance d, string id, IndividualAddress address)
    { var copy = new DeviceInstance { Id = id, Name = "Synthetic second target", Address = address, ProductRefId = d.ProductRefId, Hardware2ProgramRefId = d.Hardware2ProgramRefId };
        foreach (var p in d.ParameterValues) copy.ParameterValues[p.Key] = p.Value; return copy; }
    private sealed class Adapter(M112DownloadChecks.FakeDevice device, List<string> order) : IM112CommissioningBackend
    {
        private readonly M112CommissioningBackend _real = new();
        public Task<IM112PreparedChange> PrepareAsync(Project baseline, DeviceInstance baselineDevice, Project desired, DeviceInstance desiredDevice, CancellationToken token)
            => _real.PrepareAsync(baseline, baselineDevice, desired, desiredDevice, token);
        public async Task<ManagementDeviceIdentity> ReadIdentityAsync(ReadOnlyConnectionOptions options, IndividualAddress target, CancellationToken token)
        { await using var connection = await device.OpenAsync(token); return await connection.Client.ReadIdentityAsync(token); }
        public Task<M112RecoveryInspection> InspectRecoveryAsync(string path, CancellationToken token) => _real.InspectRecoveryAsync(path, token);
        public async Task<M112PhysicalIdentity> ReadPhysicalIdentityAsync(ReadOnlyConnectionOptions options, IndividualAddress target, CancellationToken token)
        {
            await using var connection = await device.OpenAsync(token); var identity = await connection.Client.ReadIdentityAsync(token);
            var hardware = await connection.Client.ReadPropertyAsync(0, 78, ct: token);
            return new(identity.Address, identity.MaskVersion, identity.Manufacturer, identity.Serial, Convert.ToHexString(SHA256.HashData(hardware)));
        }
        public async Task<M112DownloadResult> ApplyAsync(ReadOnlyConnectionOptions options, IM112PreparedChange review, ManagementDeviceIdentity expected,
            string fingerprint, string journalRoot, Action<string> progress, CancellationToken token)
        {
            order.Add(expected.Address);
            var plan = (M112ApplicationChangePlan)review.GetType().GetProperty("Plan", BindingFlags.Instance | BindingFlags.Public)!.GetValue(review)!;
            return await M112ApplicationDownload.ApplyChangesAsync(device.OpenAsync, plan, fingerprint, expected, review.Target, journalRoot, progress, token);
        }
        public Task<M112DownloadResult> RecoverAsync(ReadOnlyConnectionOptions options, M112RecoveryInspection inspection, string target, Action<string> progress, CancellationToken token)
            => M112ApplicationDownload.RecoverInspectedAsync(device.OpenAsync, inspection, target, progress, token);
    }
}
