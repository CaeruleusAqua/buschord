using System.Text.Json;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Bus;
using Knx.Bus.Connections;
using Knx.Bus.Security;
using F = GroupConnectionFixture;

internal static class GroupSelectionChecks
{
    internal static async Task Run(Action<string, bool> check)
    {
        using (var fixture = new F())
        {
            await fixture.Provision(); var source = new Integrity(); await using var owner = new WorkbenchGroupSecurityOwner(source); var scope = new object();
            await using var session = new GroupSecuritySelectionSession(owner, scope, fixture.Connection()); session.SetSource(GroupHostConfigurationFixture.Write(fixture));
            await session.ReadCandidatesAsync(); check("group selection candidates read no integrity/password", session.Snapshot.Candidates?.Candidates.Count == 1 && source.Reads == 0 && fixture.PasswordSource.Reads == 0);
            await session.BindAsync(); check("group selection never confirms current unanchored state automatically", session.Snapshot.Selection is null && session.Snapshot.Status == "GroupSecurityStateNotReady" && source.Reads == 0);
            session.ConfirmCurrentState(true); await session.BindAsync(); var selection = session.Snapshot.Selection!;
            check("group selection binds exact explicit source without reading its password", selection is not null && source.Reads == 1 && fixture.PasswordSource.Reads == 0 && ReferenceEquals(selection.BaseConnection.PasswordSource, fixture.PasswordSource));
            check("group selection integrity input is cleared", source.Last!.All(value => value == 0));
            await session.DisposeAsync();
            var selected = await selection.PrepareNewOperationAsync(fixture.Connection(), default);
            await using (var operation = await fixture.Factory().AcquireOperationAsync(selected))
            {
                for (var index = 0; index < 2; index++) { await using var connected = await operation.OpenTunnelAsync(); check("group selection fresh subconnection retains one operation " + index, connected.Security.GatewayAuthenticated); }
            }
            check("group selection lives beyond dialog and one grant is consumed at factory acquire", !selection.CanStartNewOperation && fixture.PasswordSource.Reads == 1);
            var denied = false; try { await fixture.Factory().AcquireOperationAsync(selected); } catch (KnxConnectionException error) { denied = error.Failure == KnxConnectionFailure.GroupSecurityStateNotReady; }
            check("group selection old Options cannot bypass a consumed grant", denied);
            var renewed = await selection.ReconfirmNewOperationAsync(fixture.Connection(), true, default);
            await using (var operation = await fixture.Factory().AcquireOperationAsync(await renewed.PrepareNewOperationAsync(fixture.Connection(), default))) { await using var connected = await operation.OpenTunnelAsync(); }
            check("group selection explicit reconfirmation needs no file or profile reselection", source.Reads == 1 && fixture.PasswordSource.Reads == 2 && !renewed.CanStartNewOperation);
            check("group selection does not serialize integrity/password capabilities", !JsonSerializer.Serialize(renewed).Contains("PasswordSource") && !JsonSerializer.Serialize(renewed).Contains(fixture.State));
        }
        using (var fixture = new F())
        {
            await fixture.Provision(); var source = new Integrity { Hold = true }; await using var owner = new WorkbenchGroupSecurityOwner(source); var scope = new object();
            await using var session = new GroupSecuritySelectionSession(owner, scope, fixture.Connection()); var path = GroupHostConfigurationFixture.Write(fixture); session.SetSource(path); await session.ReadCandidatesAsync(); session.ConfirmCurrentState(true);
            var binding = session.BindAsync(); await source.Entered.Task; session.SetSource(path + ".changed");
            check("group selection source change leaves held read joined", !binding.IsCompleted && session.Snapshot.Busy && session.Snapshot.Selection is null);
            source.Release.TrySetResult(); await binding;
            check("group selection late result cannot publish into another generation", session.Snapshot.Selection is null && source.Last!.All(value => value == 0));
        }
        using (var fixture = new F())
        {
            await fixture.Provision(); var source = new Integrity { Hold = true }; await using var owner = new WorkbenchGroupSecurityOwner(source); var scope = new object();
            var session = new GroupSecuritySelectionSession(owner, scope, fixture.Connection()); session.SetSource(GroupHostConfigurationFixture.Write(fixture)); await session.ReadCandidatesAsync(); session.ConfirmCurrentState(true);
            var binding = session.BindAsync(); await source.Entered.Task; var closing = session.DisposeAsync().AsTask();
            check("group selection close waits for held integrity capability", !closing.IsCompleted && ReferenceEquals(closing, session.DisposeAsync().AsTask()));
            source.Release.TrySetResult(); await Task.WhenAll(binding, closing);
            check("group selection close clears delayed source and refuses publication", session.Snapshot.Selection is null && source.Last!.All(value => value == 0));
        }
        using (var fixture = new F())
        {
            await fixture.Provision(); var source = new Integrity(); await using var owner = new WorkbenchGroupSecurityOwner(source); var scope = new object();
            await using var session = new GroupSecuritySelectionSession(owner, scope, fixture.Connection()); session.SetSource(GroupHostConfigurationFixture.Write(fixture)); await session.ReadCandidatesAsync(); session.ConfirmCurrentState(true); await session.BindAsync();
            var selection = session.Snapshot.Selection!; var consumers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var closing = owner.ReleaseProjectAfterAsync(scope, consumers.Task);
            check("group selection project close retains registry until consumers drain", !closing.IsCompleted);
            var denied = false; try { await fixture.Factory().AcquireOperationAsync(selection.BoundProfile); } catch (KnxConnectionException error) { denied = error.Failure == KnxConnectionFailure.Disposed; }
            check("group selection project close immediately refuses new admission", denied && fixture.Peers.Count == 0);
            consumers.TrySetResult(); await closing;
        }
        using (var fixture = new F())
        {
            await using var owner = new WorkbenchGroupSecurityOwner(new Integrity()); var session = new GroupSecuritySelectionSession(owner, new object(), fixture.Connection()); session.SetSource(GroupHostConfigurationFixture.Write(fixture)); Task? closing = null;
            session.Changed += () => closing ??= session.DisposeAsync().AsTask();
            await session.ReadCandidatesAsync(); await closing!;
            check("group selection reentrant close sees a published completion", session.Snapshot.Selection is null);
        }
        var imported = JsonSerializer.Deserialize<WorkbenchTelegram>("{\"Timestamp\":\"2026-01-01T00:00:00Z\",\"Source\":\"1.1.1\",\"Destination\":\"1/1/1\",\"Service\":\"GroupValueWrite\",\"Value\":\"1\",\"Origin\":1,\"DataSecurity\":2}");
        check("group monitor imported row cannot assert live verification", imported!.DataSecurity == BusDataSecurity.Unverified);
        using (var fixture = new F())
        {
            await fixture.Provision(); await using var owner = new WorkbenchGroupSecurityOwner(new Integrity());
            await using var selectionSession = new GroupSecuritySelectionSession(owner, new object(), fixture.Connection()); selectionSession.SetSource(GroupHostConfigurationFixture.Write(fixture)); await selectionSession.ReadCandidatesAsync(); selectionSession.ConfirmCurrentState(true); await selectionSession.BindAsync();
            var selected = selectionSession.Snapshot.Selection!; await using var m112 = new M112CommissioningChecks.Fixture();
            var options = new ReadOnlyConnectionOptions(selected.BoundProfile, selected);
            await m112.Session.PrepareAsync(m112.Project, m112.BaselinePath, m112.Hash, options, m112.Root);
            var renewed = await selected.ReconfirmNewOperationAsync(fixture.Connection(), true, default);
            m112.Session.RenewEquivalentGroupGrant(new(renewed.BoundProfile, renewed));
            check("group M112 grant renewal preserves reviewed metadata and performs no read/write", m112.Session.Snapshot.Phase == M112CommissioningPhase.Reviewed && m112.Backend.Reads == 0 && m112.Backend.Applies == 0);
            var rejected = false; try { m112.Session.RenewEquivalentGroupGrant(m112.Options); } catch (KnxConnectionException) { rejected = true; }
            check("group M112 grant renewal refuses dropping the security policy", rejected);
            m112.Project.Comment = "Changed after offline review"; rejected = false; try { m112.Session.RenewEquivalentGroupGrant(new(renewed.BoundProfile, renewed)); } catch (InvalidOperationException) { rejected = true; }
            check("group M112 grant renewal does not promote a stale model", rejected && m112.Backend.Reads == 0 && m112.Backend.Applies == 0);
        }
    }
    internal sealed class Integrity : IGroupIntegrityCapabilitySource
    {
        internal int Reads; internal byte[]? Last; internal bool Hold;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<byte[]> ReadOwnedAsync(string name, CancellationToken ct) { Reads++; Entered.TrySetResult(); if (Hold) await Release.Task; return Last = F.Integrity.ToArray(); }
    }
}
