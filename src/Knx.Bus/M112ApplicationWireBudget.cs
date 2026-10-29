using Knx.Bus.Security;

namespace Knx.Bus;

/// <summary>Complete current M112 target/before-image transport lengths; no mutation, nonce reservation or device-capacity discovery.</summary>
public sealed record M112ApplicationWireBudget(int MaximumPlainRequestLength, int MaximumPlainReplyLength, int RequiredToolWireTpduLength)
{
    public static M112ApplicationWireBudget ForImages(M112ApplicationImage baseline, M112ApplicationImage desired)
    {
        ArgumentNullException.ThrowIfNull(baseline); ArgumentNullException.ThrowIfNull(desired);
        return ForSegments(baseline.Segments.Select(s => (s.Address, s.Size, s.MemoryType))
            .Concat(desired.Segments.Select(s => (s.Address, s.Size, s.MemoryType))));
    }
    public static M112ApplicationWireBudget ForRecovery(M112RecoveryInspection inspection)
    { ArgumentNullException.ThrowIfNull(inspection); return ForSegments(inspection.Segments.Select(s => (s.Address, s.Size, s.MemoryType))); }
    public static M112ApplicationWireBudget IdentityAndStatus() => Seal(ManagementApdu.ReadProperty(0, 11, 1, 1).Length, 6 + 6);
    private static M112ApplicationWireBudget ForSegments(IEnumerable<(ushort Address, ushort Size, byte MemoryType)> segments)
    {
        var request = Math.Max(ManagementApdu.ReadProperty(0, 78, 1, 1).Length,
            ManagementApdu.WriteMemory(0x104, M112ApplicationDownload.SimpleControl(1, 4)).Length);
        // Largest fixed property result is serial/hardware (6 bytes + the 6-byte property response header).
        var reply = 6 + 6;
        foreach (var segment in segments)
        {
            if (segment.Size == 0 || (uint)segment.Address + segment.Size > 65536) throw new ArgumentException("Invalid complete M112 segment extent.");
            var count = (byte)Math.Min(M112ApplicationDownload.MemoryTransferBlockLength, (int)segment.Size);
            request = Math.Max(request, ManagementApdu.ReadMemory(segment.Address, count).Length);
            reply = Math.Max(reply, 4 + count);
            if (segment.MemoryType == 3)
                request = Math.Max(request, ManagementApdu.WriteMemory(segment.Address, new byte[count]).Length);
        }
        return Seal(request, reply);
    }
    private static M112ApplicationWireBudget Seal(int request, int reply)
    {
        var overhead = byte.MaxValue - KnxIndividualSecureCodec.MaximumPlainTpduLength;
        return new(request, reply, Math.Max(25, Math.Max(request, reply) + overhead));
    }
}
