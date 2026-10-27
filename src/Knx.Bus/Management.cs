using System.Buffers.Binary;
using Knx;

namespace Knx.Bus;

/// <summary>
/// Management-layer cEMI frames for device programming over a tunnel:
/// descriptor read, individual address read/write, restart, memory read/write.
/// Short frames use the standard APCI nibble in octet 7. Memory read/write need
/// extended frames (APDU length > 15); see <see cref="ExtendedCemi"/>.
/// Extended-frame length handling has not yet been validated against a real device.
/// </summary>
public static class Management
{
    public static byte[] DeviceDescriptorRead(IndividualAddress target, byte descriptorType = 0x00)
        => Cemi.LDataRequest(new Telegram
        {
            Source = IndividualAddress.Zero,
            Destination = target,
            Apci = Apci.DeviceDescriptorRead,
            Data = [descriptorType],
        });

    public static byte[] IndividualAddressRead()
        => Cemi.LDataRequest(new Telegram
        {
            Source = IndividualAddress.Zero,
            Destination = new IndividualAddress(0xFFFF),
            Apci = Apci.IndividualAddressRead,
            Data = [],
        });

    public static byte[] IndividualAddressWrite(IndividualAddress target)
        => Cemi.LDataRequest(new Telegram
        {
            Source = IndividualAddress.Zero,
            Destination = target,
            Apci = Apci.IndividualAddressWrite,
            Data = [(byte)(target.Raw >> 8), (byte)target.Raw],
        });

    public static byte[] Restart(IndividualAddress target, bool erase, byte eraseCode = 0x01)
        => Cemi.LDataRequest(new Telegram
        {
            Source = IndividualAddress.Zero,
            Destination = target,
            Apci = Apci.Restart,
            Data = erase ? [0x01, eraseCode] : [0x00, 0x00],
        });

    /// <summary>Memory write: 2-byte address + 1..12 payload bytes (extended frame).</summary>
    public static byte[] MemoryWrite(IndividualAddress target, ushort address, byte[] data)
    {
        if (data.Length is 0 or > 12)
            throw new ArgumentOutOfRangeException(nameof(data), "1..12 bytes per memory write frame");
        return ExtendedCemi.LDataRequest(new Telegram
        {
            Source = IndividualAddress.Zero,
            Destination = target,
            Apci = Apci.MemoryWrite,
            Data = [.. address.ToUInt16Big(), .. data],
        });
    }

    /// <summary>Memory read: 2-byte address + byte count.</summary>
    public static byte[] MemoryRead(IndividualAddress target, ushort address, byte count)
        => ExtendedCemi.LDataRequest(new Telegram
        {
            Source = IndividualAddress.Zero,
            Destination = target,
            Apci = Apci.MemoryRead,
            Data = [.. address.ToUInt16Big(), count],
        });
}

/// <summary>
/// Extended L_Data frame codec: APCI service nibble in octet 7, length in octet 6.
/// Extended-frame length handling still requires validation against a real device.
/// </summary>
internal static class ExtendedCemi
{
    public static byte[] LDataRequest(Telegram telegram)
    {
        var data = telegram.Data;
        var apduLen = 1 + data.Length; // apci octet + data
        var adp = new byte[2 + 7 + apduLen];
        adp[0] = (byte)CemiMessageCode.LDataReq;
        adp[1] = 0x00; // additional info length
        adp[2] = Cemi.Ctrl1Standard;
        adp[3] = Cemi.Ctrl2Individual;
        BinaryPrimitives.WriteUInt16BigEndian(adp.AsSpan(4), telegram.Source.Raw);
        BinaryPrimitives.WriteUInt16BigEndian(adp.AsSpan(6), telegram.Destination.Raw);
        adp[8] = (byte)(0x40 | (apduLen - 1)); // extended frame format + APDU length LSBs
        adp[9] = (byte)((((ushort)telegram.Apci >> 6) & 0x0F) << 4); // APCI service nibble
        data.CopyTo(adp, 10);
        return adp;
    }
}

internal static class UInt16Extensions
{
    public static byte[] ToUInt16Big(this ushort value) => [(byte)(value >> 8), (byte)value];
}
