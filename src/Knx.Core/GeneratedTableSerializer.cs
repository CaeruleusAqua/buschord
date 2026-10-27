using System.Buffers.Binary;

namespace Knx;

internal sealed record GeneratedTableSourceSpan(GeneratedTablePayloadOrigin Origin, string? ResourceId,
    ResourcePatchArena Arena, uint Offset, byte[]? Data = null, byte[]? Mask = null);

internal static class GeneratedTableSerializer
{
    internal static (int Address, int Association, int Objects) Lengths(GeneratedTableFormat format,
        NativeCommunicationWitness witness)
    {
        var small = format == GeneratedTableFormat.M112;
        return (checked((small ? 3 : 2) + 2 * witness.Groups.Count),
            checked((small ? 1 : 2) + (format == GeneratedTableFormat.SystemB4ByteAssociations ? 4 : 2) * witness.Associations.Count),
            checked(small ? 3 + 4 * witness.Slots.Count : 2 + 2 * Math.Max(0, witness.Slots.Count - 1)));
    }

    internal static GeneratedTablePayload[] Encode(GeneratedTableFormat format, NativeCommunicationWitness witness,
        GeneratedTableSourceSpan address, GeneratedTableSourceSpan association, GeneratedTableSourceSpan objects)
    {
        var lengths = Lengths(format, witness); var small = format == GeneratedTableFormat.M112;
        var ga = Seed(address, lengths.Address); var gaWrites = new byte[lengths.Address];
        if (small) { ga[0] = checked((byte)(witness.Groups.Count + 1)); gaWrites[0] = 255; }
        else { Word(ga, 0, checked((ushort)witness.Groups.Count)); gaWrites.AsSpan(0, 2).Fill(255); }
        for (var i = 0; i < witness.Groups.Count; i++)
        {
            var offset = (small ? 3 : 2) + 2 * i;
            Word(ga, offset, witness.Groups[i].Address); gaWrites.AsSpan(offset, 2).Fill(255);
        }

        var links = witness.Associations.OrderByDescending(a => a.Sending).ThenBy(a => a.ObjectNumber)
            .ThenBy(a => a.GroupAddressIndex).ToArray();
        var assoc = Seed(association, lengths.Association); var assocWrites = new byte[assoc.Length];
        assocWrites.AsSpan().Fill(255);
        if (small) assoc[0] = checked((byte)links.Length);
        else Word(assoc, 0, checked((ushort)links.Length));
        var width = format == GeneratedTableFormat.SystemB4ByteAssociations ? 4 : 2;
        for (var i = 0; i < links.Length; i++)
        {
            var offset = (small ? 1 : 2) + width * i; var link = links[i];
            if (width == 4)
            { Word(assoc, offset, checked((ushort)(link.GroupAddressIndex + 1))); Word(assoc, offset + 2, link.ObjectNumber); }
            else { assoc[offset] = checked((byte)(link.GroupAddressIndex + 1)); assoc[offset + 1] = checked((byte)link.ObjectNumber); }
        }

        var ko = Seed(objects, lengths.Objects); var koWrites = new byte[ko.Length]; koWrites.AsSpan().Fill(255);
        if (small) ko[0] = checked((byte)witness.Slots.Count);
        else Word(ko, 0, checked((ushort)Math.Max(0, witness.Slots.Count - 1)));
        var linked = links.Select(a => a.ObjectNumber).ToHashSet();
        for (var i = small ? 0 : 1; i < witness.Slots.Count; i++)
        {
            var slot = witness.Slots[i];
            if (!TrySizeCode(slot.SizeInBit, slot.IsPlaceholder, out var size))
                throw new InvalidDataException("Unsupported fixed object size.");
            var communication = slot.CommunicationEnabled && slot.Active && linked.Contains(slot.Number);
            var flags = PackFlags(slot, communication);
            if (small)
            {
                var offset = 3 + 4 * i;
                ko[offset + 2] = slot.Active ? (byte)((ko[offset + 2] & 0x20) | (flags & ~0x20))
                    : (byte)((ko[offset + 2] & ~4) | (flags & 4));
                ko[offset + 3] = (byte)((ko[offset + 3] & 0xC0) | (size & 0x3F));
            }
            else
            {
                var offset = 2 + 2 * (i - 1);
                ko[offset] = slot.Active ? flags : (byte)((ko[offset] & ~4) | (flags & 4));
                ko[offset + 1] = size;
            }
        }
        return [Payload(GeneratedTableRole.GroupAddress, address, ga, gaWrites),
            Payload(GeneratedTableRole.Association, association, assoc, assocWrites),
            Payload(GeneratedTableRole.CommunicationObject, objects, ko, koWrites)];
    }

    internal static bool TrySizeCode(uint bits, bool placeholder, out byte code)
    {
        code = 0;
        if (placeholder) return bits is 0 or 1;
        ReadOnlySpan<uint> sizes = [1, 2, 3, 4, 5, 6, 7, 8, 16, 24, 32, 48, 64, 80, 112, 40, 56, 72, 88, 96, 104];
        for (var i = 0; i < sizes.Length; i++) if (bits == sizes[i]) { code = (byte)i; return true; }
        if (bits % 8 == 0 && bits / 8 is >= 15 and <= 50) { code = (byte)(bits / 8 + 6); return true; }
        return false;
    }

    private static byte PackFlags(NativeCommunicationSlot slot, bool communication)
    {
        var priority = slot.Priority switch
        {
            NativeCommunicationPriority.Low => 3, NativeCommunicationPriority.High => 1,
            NativeCommunicationPriority.Alert => 2, _ => throw new InvalidDataException("Unsupported object priority."),
        };
        return (byte)(priority | (communication ? 4 : 0) | (slot.ReadEnabled ? 8 : 0) | (slot.WriteEnabled ? 16 : 0)
            | (slot.ReadOnInitEnabled ? 32 : 0) | (slot.TransmitEnabled ? 64 : 0) | (slot.UpdateEnabled ? 128 : 0));
    }
    private static void Word(byte[] data, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), value);
    private static byte[] Seed(GeneratedTableSourceSpan source, int length)
    {
        if (source.Origin == GeneratedTablePayloadOrigin.FreshGenerated) return new byte[length];
        if (source.Data?.Length != length) throw new InvalidDataException("Missing complete retained table bytes.");
        return (byte[])source.Data.Clone();
    }
    private static GeneratedTablePayload Payload(GeneratedTableRole role, GeneratedTableSourceSpan source, byte[] data, byte[] writes)
    {
        byte[]? resultMask;
        if (source.Origin == GeneratedTablePayloadOrigin.FreshGenerated) resultMask = (byte[])writes.Clone();
        else if (source.Mask is { } mask)
        {
            resultMask = (byte[])mask.Clone();
            for (var i = 0; i < writes.Length; i++) resultMask[i] |= writes[i];
        }
        else resultMask = writes.All(b => b == 255) ? (byte[])writes.Clone() : null;
        return new(role, source.Arena, source.Offset, source.Origin, source.ResourceId, data, writes,
            source.Data, source.Mask, resultMask);
    }
}
