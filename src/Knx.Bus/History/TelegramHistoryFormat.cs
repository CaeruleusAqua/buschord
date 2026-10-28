using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Knx.Bus.History;

internal sealed record HistoryFrame(int Schema, string Kind, TelegramHistoryEntry? Telegram = null,
    TelegramCaptureInterval? Capture = null);

internal sealed record HistoryHeader(Guid Id, long Generation, DateTimeOffset? RetainedFromUtc, bool Recovered = false);

internal static class TelegramHistoryFormat
{
    internal const int HeaderSize = 80;
    private static ReadOnlySpan<byte> Magic => "BCHTH001"u8;
    internal static readonly JsonSerializerOptions Json = new() { MaxDepth = 16 };

    internal static void WriteHeader(Stream stream, HistoryHeader header)
    {
        Span<byte> data = stackalloc byte[HeaderSize];
        Magic.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data[8..], 1);
        header.Id.TryWriteBytes(data[12..28]);
        BinaryPrimitives.WriteInt64LittleEndian(data[28..], header.Generation);
        BinaryPrimitives.WriteInt64LittleEndian(data[36..], header.RetainedFromUtc?.UtcTicks ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(data[44..], header.Recovered ? 1 : 0);
        SHA256.HashData(data[..48], data[48..]);
        stream.Position = 0;
        stream.Write(data);
    }

    internal static HistoryHeader ReadHeader(Stream stream)
    {
        Span<byte> data = stackalloc byte[HeaderSize];
        stream.Position = 0;
        stream.ReadExactly(data);
        if (!data[..8].SequenceEqual(Magic) || BinaryPrimitives.ReadInt32LittleEndian(data[8..]) != 1
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(data[..48]), data[48..]))
            throw new InvalidDataException("Unrecognized or damaged telegram history header.");
        var id = new Guid(data[12..28]);
        var generation = BinaryPrimitives.ReadInt64LittleEndian(data[28..]);
        var retainedTicks = BinaryPrimitives.ReadInt64LittleEndian(data[36..]);
        var flags = BinaryPrimitives.ReadInt32LittleEndian(data[44..]);
        if (id == Guid.Empty || generation < 1 || retainedTicks < -1 || retainedTicks > DateTimeOffset.MaxValue.UtcTicks || flags is < 0 or > 1)
            throw new InvalidDataException("Invalid telegram history header values.");
        return new(id, generation, retainedTicks == -1 ? null : new DateTimeOffset(retainedTicks, TimeSpan.Zero), flags == 1);
    }

    internal static byte[] Encode(HistoryFrame frame, int maximumFrameBytes)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        if (payload.Length > maximumFrameBytes) throw new InvalidDataException("Telegram history frame exceeds its size limit.");
        var result = new byte[payload.Length + 36];
        BinaryPrimitives.WriteInt32LittleEndian(result, payload.Length);
        payload.CopyTo(result.AsSpan(4));
        SHA256.HashData(payload, result.AsSpan(payload.Length + 4));
        return result;
    }

    internal static HistoryFrame ReadFrame(Stream stream, long end, int maximumFrameBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Span<byte> lengthBytes = stackalloc byte[4];
        if (end - stream.Position < 4) throw new InvalidDataException("Incomplete frame length.");
        stream.ReadExactly(lengthBytes);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length < 2 || length > maximumFrameBytes || end - stream.Position < length + 32L)
            throw new InvalidDataException("Incomplete or oversized history frame.");
        var payload = new byte[length];
        stream.ReadExactly(payload);
        Span<byte> hash = stackalloc byte[32];
        stream.ReadExactly(hash);
        token.ThrowIfCancellationRequested();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), hash))
            throw new InvalidDataException("Telegram history frame checksum mismatch.");
        HistoryFrame frame;
        try { frame = JsonSerializer.Deserialize<HistoryFrame>(payload, Json) ?? throw new InvalidDataException("Empty history frame."); }
        catch (JsonException error) { throw new InvalidDataException("Invalid history frame JSON.", error); }
        if (frame.Schema != 1) throw new NotSupportedException("Unsupported telegram history record version.");
        switch (frame.Kind)
        {
            case "telegram" when frame.Telegram is not null && frame.Capture is null:
                ValidateEntry(frame.Telegram);
                break;
            case "capture" when frame.Telegram is null && frame.Capture is not null:
                ValidateCapture(frame.Capture);
                break;
            default: throw new InvalidDataException("Unknown or inconsistent telegram history record.");
        }
        return frame;
    }

    internal static void ValidateEntry(TelegramHistoryEntry entry)
    {
        if (entry.Schema != 1 || !IsId(entry.Id) || entry.TimestampUtc.Offset != TimeSpan.Zero || entry.HopCount > 7)
            throw new InvalidDataException("Invalid telegram identity, time, or hop count.");
        Text(entry.Source, 256); Text(entry.Destination, 256); Text(entry.Service, 256);
        Text(entry.Origin, 256); Text(entry.SourceId, 256); Text(entry.ProjectId, 256, true);
        Text(entry.Flags, 256, true); Text(entry.Priority, 256, true);
        if (entry.CaptureId is not null && !IsId(entry.CaptureId)) throw new InvalidDataException("Invalid capture identity.");
        Hex(entry.DataHex, 8192, false); Hex(entry.RawCemiHex, 8192, true);
    }

    internal static void ValidateCapture(TelegramCaptureInterval capture)
    {
        if (!IsId(capture.Id) || capture.FromUtc.Offset != TimeSpan.Zero
            || capture.UntilUtc is { } until && (until.Offset != TimeSpan.Zero || until < capture.FromUtc)
            || capture.IsActive && (capture.UntilUtc is not null || capture.EndUnknown)
            || capture.EndUnknown && capture.UntilUtc is not null)
            throw new InvalidDataException("Invalid capture interval.");
        Text(capture.SourceId, 256); Text(capture.Reason, 512);
        if (string.IsNullOrWhiteSpace(capture.SourceId)) throw new InvalidDataException("Capture source identity is required.");
    }

    internal static bool IsId(string? value) => value is { Length: 32 } && Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;

    internal static void Text(string? value, int maximum, bool nullable = false)
    {
        if (value is null ? !nullable : value.Length > maximum || value.Any(char.IsControl))
            throw new InvalidDataException("Invalid or oversized telegram metadata.");
    }

    private static void Hex(string? value, int maximum, bool nullable)
    {
        if (value is null) { if (!nullable) throw new InvalidDataException("Missing payload."); return; }
        if (value.Length > maximum || value.Length % 2 != 0 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Invalid or oversized hexadecimal payload.");
    }
}
