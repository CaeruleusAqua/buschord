using Knx;
using Knx.App.Workbench;
using Knx.Bus.Commissioning;
using Knx.Workbench;

internal static class IndividualAddressSessionChecks
{
    internal static async Task Run(Action<string, bool> check)
    {
        await using (var f = new Fixture())
        {
            f.Project.Workbench.Devices[f.Device.Id] = new() { SecureCapable = true };
            await f.Session.ReviewAsync(f.Request(), f.Gateway.Root);
            check("address Workbench known Secure target is refused before transport", f.Session.Issue == "UnsupportedSecureProvisioning" && f.Gateway.Transports == 0);
        }
        await using (var f = new Fixture())
        {
            f.Programming.Enqueue(f.Device.Id); await f.Programming.RunAsync();
            check("address fixture begins with separately labelled demo flags", f.Programming.DeviceState(f.Device.Id)?.Flags == DeviceLoadFlags.All);
            await f.Session.ReviewAsync(f.Request(), f.Gateway.Root);
            check("address Workbench review alone sends no mutation and grants no real flag", f.Gateway.Writes == 0 && f.Session.Review is not null && !f.Session.AddressFlagRecorded);
            await f.Session.ApplyAsync(f.Session.Review!.Fingerprint, true, true);
            check("address Workbench verified bound target records only real Adr", f.Session.AddressFlagRecorded && f.Programming.DeviceState(f.Device.Id) is { Origin: ProgrammingOrigin.Real, Flags: DeviceLoadFlags.IndividualAddress } && f.Gateway.Writes == 1);
        }
        await using (var f = new Fixture())
        {
            await f.Session.ReviewAsync(f.Request(), f.Gateway.Root); f.Project.Comment = "modified after review";
            await f.Session.ApplyAsync(f.Session.Review!.Fingerprint, true, true);
            check("address Workbench stale review refuses before write", f.Session.Issue == "StaleReview" && f.Gateway.Writes == 0 && !f.Session.AddressFlagRecorded);
        }
        await using (var f = new Fixture())
        {
            await f.Session.ReviewAsync(f.Request(), f.Gateway.Root); f.Gateway.HoldVerification = true;
            var applying = f.Session.ApplyAsync(f.Session.Review!.Fingerprint, true, true); await f.Gateway.VerificationEntered.Task;
            f.Project.Comment = "changed during independent verify"; f.Gateway.ReleaseVerification.TrySetResult(); await applying;
            check("address Workbench late actual verify cannot mark a changed project", f.Session.Result?.AddressVerified == true && !f.Session.AddressFlagRecorded && f.Session.Issue == "VerifiedWithoutProjectFlag");
        }
        await using (var f = new Fixture())
        {
            f.Programming.Enqueue(f.Device.Id); await f.Programming.RunAsync(); await f.Session.ReviewAsync(f.Request(), f.Gateway.Root); f.Gateway.WrongIdentity = true;
            await f.Session.ApplyAsync(f.Session.Review!.Fingerprint, true, true);
            check("address Workbench uncertain outcome clears old flags and blocks ordinary exit", f.Session.RecoveryRequired && !f.Session.MayLeave && f.Programming.DeviceState(f.Device.Id)?.Flags == DeviceLoadFlags.None);
            f.Session.AcknowledgeUnknownStateExit(true);
            check("address unknown-state acknowledgement grants only exit", f.Session.MayLeave && f.Session.Result?.AddressVerified != true && !f.Session.AddressFlagRecorded);
        }
        await using (var f = new Fixture())
        {
            await f.Session.ReviewAsync(f.Request(), f.Gateway.Root); f.Gateway.HoldVerification = true;
            var applying = f.Session.ApplyAsync(f.Session.Review!.Fingerprint, true, true); await f.Gateway.VerificationEntered.Task;
            var closing = f.Session.DisposeAsync().AsTask();
            check("address Workbench close waits for independent fresh verification", !closing.IsCompleted && !applying.IsCompleted && ReferenceEquals(closing, f.Session.DisposeAsync().AsTask()));
            f.Gateway.ReleaseVerification.TrySetResult(); await closing; await applying;
            check("address Workbench close releases every owned transport after join", f.Gateway.Transports == f.Gateway.Disposals && f.Gateway.Writes == 1);
        }
        await using (var f = new Fixture())
        {
            Task? closing = null; f.Session.Changed += () => closing ??= f.Session.DisposeAsync().AsTask();
            await f.Session.ReviewAsync(f.Request(), f.Gateway.Root); await closing!;
            check("address reentrant close sees the prepublished operation and opens no transport", f.Gateway.Transports == 0 && f.Gateway.Writes == 0);
        }
        await using (var f = new Fixture())
        {
            f.Device.Address = f.Gateway.ExpectedTarget = new(1, 1, 0);
            var refused = false; try { await f.Session.ReviewAsync(f.Request(), f.Gateway.Root); } catch (AddressAssignmentException) { refused = true; }
            check("address zero target without actual project coupler hardware opens nothing", refused && f.Session.Review is null && f.Gateway.Transports == 0);
        }
        foreach (var target in new[] { new IndividualAddress(1, 1, 0), new IndividualAddress(1, 0, 0), new IndividualAddress(1, 1, 255) })
        await using (var f = new Fixture())
        {
            f.Device.Address = f.Gateway.ExpectedTarget = target;
            if (target.Device == 0) f.Project.Hardware[f.Device.ProductRefId] = new() { HardwareId = "synthetic-coupler", IsCoupler = true };
            await f.Session.ReviewAsync(f.Request(), f.Gateway.Root);
            await f.Session.ApplyAsync(f.Session.Review!.Fingerprint, true, true);
            check("address project coupler/255 role verifies real Adr only " + target, f.Session.AddressFlagRecorded && f.Gateway.Writes == 1 && f.Session.Result?.AddressVerified == true);
        }
        await using (var f = new Fixture())
        {
            f.Device.Address = f.Gateway.ExpectedTarget = new(1, 1, 0); f.Project.Hardware[f.Device.ProductRefId] = new() { HardwareId = "synthetic-coupler", IsCoupler = true };
            await f.Session.ReviewAsync(f.Request(), f.Gateway.Root);
            f.Project.Hardware[f.Device.ProductRefId] = new() { HardwareId = "synthetic-coupler", IsCoupler = false };
            await f.Session.ApplyAsync(f.Session.Review!.Fingerprint, true, true);
            check("address hardware-only coupler fact drift blocks write despite unchanged project XML", f.Session.Issue == "StaleReview" && f.Gateway.Writes == 0);
        }
        await using (var f = new Fixture())
        {
            var other = ProjectQuery.DeviceList(f.Project).Skip(1).First(); other.Address = f.Device.Address;
            await f.Session.ReviewAsync(f.Request(), f.Gateway.Root);
            check("address duplicate project reservation refuses before transport", f.Session.Issue == "ReservationRequired" && f.Gateway.Transports == 0);
        }
        await using (var f = new Fixture())
        {
            await f.Session.ReviewAsync(f.Request(), f.Gateway.Root); f.Gateway.HoldVerification = true;
            var applying = f.Session.ApplyAsync(f.Session.Review!.Fingerprint, true, true); await f.Gateway.VerificationEntered.Task;
            f.Lifetime = new(); f.Gateway.ReleaseVerification.TrySetResult(); await applying;
            check("address replacement lifetime cannot receive a late verified flag", f.Session.Result?.AddressVerified == true && !f.Session.AddressFlagRecorded);
        }
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        internal IndividualAddressGatewayFixture Gateway = new(); internal Project Project = WorkbenchJson.CreateDemo(); internal object Lifetime = new();
        internal DeviceInstance Device; internal ProgrammingSession Programming; internal IndividualAddressAssignmentSession Session;
        internal bool Drafts;
        internal Fixture()
        {
            Device = ProjectQuery.DeviceList(Project).First(); Device.Address = IndividualAddressGatewayFixture.Desired;
            Project.Workbench.Devices[Device.Id] = new() { SecureCapable = false };
            Programming = new(new(), () => Project, demoExecutor: (_, _) => Task.CompletedTask);
            Session = new(() => new(Project, Lifetime, Drafts, Programming.IsRunning), Programming, Device.Id, new AddressAssignmentTransport(Gateway.Factory()).OpenAsync);
        }
        internal AddressAssignmentRequest Request() => new(Gateway.Profile, Device.Address, AddressSelectionKind.ExactSerial, "explicit target in current project", IndividualAddressGatewayFixture.DeviceSerial,
            confirmPlainDevice: true, observationWindow: TimeSpan.FromMilliseconds(20), targetRole: Session.ProjectCouplerHardware is null ? AddressDeviceRole.EndDevice : AddressDeviceRole.Coupler,
            confirmCouplerHardware: Session.ProjectCouplerHardware is not null, couplerHardwareReference: Session.ProjectCouplerHardware);
        public async ValueTask DisposeAsync() { Gateway.ReleaseVerification.TrySetResult(); await Session.DisposeAsync(); await Programming.DisposeAsync(); Gateway.Dispose(); }
    }
}
