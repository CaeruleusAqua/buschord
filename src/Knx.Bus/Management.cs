using Knx;

namespace Knx.Bus;

/// <summary>
/// Management-layer cEMI frames for device programming over a tunnel:
/// descriptor read, individual address read/write, restart, memory read/write.
/// Wire layout (verified against the KNX APCI encoding): the service code lives in
/// octet 7 bits 1..0 and octet 8 bits 7..6; 1..6 bit parameters sit inline in octet 8.
/// </summary>
public static class Management
{
    /// <summary>
    /// DeviceDescriptorRead: the descriptor type is the 4-bit management parameter in
    /// octet 8, not an appended data byte. Types 0..3 are defined for mask/descriptor reads.
    /// </summary>
    public static byte[] DeviceDescriptorRead(IndividualAddress target, byte descriptorType = 0x00)
        => Cemi.LDataRequest(DeviceDescriptorReadTelegram(target, descriptorType));

    /// <summary>Same frame as a telegram, so a caller that sends telegrams builds it the same way.</summary>
    public static Telegram DeviceDescriptorReadTelegram(IndividualAddress target, byte descriptorType = 0x00)
    {
        if (descriptorType > 0x03)
            throw new ArgumentOutOfRangeException(nameof(descriptorType), "descriptor type must be 0..3");
        return new Telegram
        {
            Source = IndividualAddress.Zero,
            Destination = target,
            Apci = Apci.DeviceDescriptorRead,
            Data = [descriptorType],
        };
    }

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

    /// <summary>Memory write: 2-byte address + 1..12 payload bytes.</summary>
    public static byte[] MemoryWrite(IndividualAddress target, ushort address, byte[] data)
    {
        if (data.Length is 0 or > 12)
            throw new ArgumentOutOfRangeException(nameof(data), "1..12 bytes per memory write frame");
        return Cemi.LDataRequest(new Telegram
        {
            Source = IndividualAddress.Zero,
            Destination = target,
            Apci = Apci.MemoryWrite,
            Data = [.. address.ToUInt16Big(), .. data],
        });
    }

    /// <summary>Memory read: 2-byte address + byte count.</summary>
    public static byte[] MemoryRead(IndividualAddress target, ushort address, byte count)
        => Cemi.LDataRequest(new Telegram
        {
            Source = IndividualAddress.Zero,
            Destination = target,
            Apci = Apci.MemoryRead,
            Data = [.. address.ToUInt16Big(), count],
        });
}

internal static class UInt16Extensions
{
    public static byte[] ToUInt16Big(this ushort value) => [(byte)(value >> 8), (byte)value];
}
