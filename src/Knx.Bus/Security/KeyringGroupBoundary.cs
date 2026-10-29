namespace Knx.Bus.Security;

/// <summary>
/// Gateway authentication does not authenticate KNX group payloads. Until an explicitly provisioned
/// Data Secure context is available, known protected groups and all unverified S-A_Data are withheld.
/// No export sequence is used to create a runtime counter state.
/// </summary>
public sealed class KeyringGroupBoundary(IEnumerable<GroupAddress> protectedGroups) : ICemiSecurityProcessor
{
    private readonly HashSet<ushort> _protected = protectedGroups.Select(group => group.Raw).ToHashSet();
    private bool _disposed;
    public Task<byte[]> PrepareOutgoingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assignedAddress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this);
        var frame = Cemi.ParseLDataFrame(cemi.ToArray());
        if (Denied(frame)) throw new NotSupportedException("This destination requires an explicitly provisioned Data Secure context.");
        return Task.FromResult(cemi.ToArray());
    }
    public Task<byte[]?> ProcessIncomingAsync(ReadOnlyMemory<byte> cemi, IndividualAddress assignedAddress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this);
        try { return Task.FromResult<byte[]?>(Denied(Cemi.ParseLDataFrame(cemi.ToArray())) ? null : cemi.ToArray()); }
        catch (Exception error) when (error is FormatException or ArgumentException) { return Task.FromResult<byte[]?>(null); }
    }
    private bool Denied(CemiLDataFrame frame) => ((frame.Control2 & 0x80) != 0 && _protected.Contains(frame.DestinationRaw))
        || (frame.Apdu.Length >= 2 && (frame.Apdu[0] & 3) == 3 && frame.Apdu[1] == 0xF1);
    public void ResetConnection() { }
    public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
}
