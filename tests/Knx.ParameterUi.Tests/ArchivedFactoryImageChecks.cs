using System.Buffers.Binary;
using System.IO.Compression;
using Knx;

internal static class ArchivedFactoryImageChecks
{
    internal static int Run()
    {
        var failures = 0; var checks = 0;
        void Check(string label, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "Factory source: " + label); if (!ok) failures++; }
        void Refused(string label, byte[] blob, ArchivedFactoryImageStatus? status = null)
        {
            var result = ArchivedFactoryImage.Read(blob);
            Check(label, result.Status != ArchivedFactoryImageStatus.Decoded && result.Segments.Count == 0
                && result.DecodedHash is null && (status is null || result.Status == status));
        }

        // Literal little-endian container: one native object 4/PID27, offset7, two data/mask bytes.
        var literal = Convert.FromHexString("0100010001000000040000000700000004001B0002000000031234FFFF");
        var blob = Stored(literal); var original = (byte[])blob.Clone();
        var source = ArchivedFactoryImage.Read(blob); var segment = source.Segments.SingleOrDefault();
        Check("literal stored-block vector retains exact source selector and buffers", source.Status == ArchivedFactoryImageStatus.Decoded
            && segment?.Location == new FactoryImageLocation(FactoryImageAddressSpace.SystemProperty, FactoryImageComponent.Data, 7, NativeObjectIndex: 4, PropertyId: 27)
            && segment.Data.ToHexString() == "1234" && segment.Mask?.ToHexString() == "FFFF" && !source.StampVerified);
        Check("decoder retains input and exposes separate source hashes", blob.SequenceEqual(original)
            && source.BlobHash is { Length: 64 } && source.DecodedHash is { Length: 64 } && source.BlobHash != source.DecodedHash);
        if (segment is not null)
        {
            var copy = segment.Data.ToArray(); copy[0] = 0;
            var maskCopy = segment.Mask!.ToArray(); maskCopy[0] = 0;
            blob[^1] = 0;
            Check("owned buffers cannot be changed through source or copy-out arrays", segment.Data.ToHexString() == "1234" && segment.Mask.ToHexString() == "FFFF");
        }
        foreach (var compression in new[] { CompressionLevel.Optimal, CompressionLevel.Fastest, CompressionLevel.SmallestSize })
            Check("complete Deflate stream decodes with " + compression, ArchivedFactoryImage.Read(Compressed(literal, compression)).Status == ArchivedFactoryImageStatus.Decoded);
        Check("explicit missing source is unavailable", ArchivedFactoryImage.Read([]).Status == ArchivedFactoryImageStatus.Missing);
        Refused("reserved Deflate block type is a controlled data error", [0x07], ArchivedFactoryImageStatus.InvalidData);
        Check("explicit empty image is different from missing source", ArchivedFactoryImage.Read(Stored(Convert.FromHexString("0100010000000000"))) is { Status: ArchivedFactoryImageStatus.Decoded, Segments.Count: 0 });

        var arenaRecords = Container(
            Record(1, 100, 0, 0, [1, 2], null), Record(3, 100, 0, 0, [3], null),
            Record(4, 7, 0, 78, [4], null), Record(5, 7, 0, 78, [5], null),
            Record(6, 100, 0, 0, [6], null), Record(7, 100, 0, 0, [7], null),
            Record(11, 0x03000007, 123, 27, [8], null), Record(12, 7, 4, 0, [9], null),
            Record(12, 7, 0, 0, [10], null), Record(13, 0x04000007, 123, 0, [11], null));
        var arenas = ArchivedFactoryImage.Read(Stored(arenaRecords));
        Check("equal offsets in distinct declared arenas and selectors remain distinct", arenas.Status == ArchivedFactoryImageStatus.Decoded && arenas.Segments.Count == 10);
        Check("native/application indices and type occurrences never become interchangeable", arenas.Segments.Count == 10
            && arenas.Segments[2].Location.NativeObjectIndex == 0 && arenas.Segments[2].Location.ApplicationObjectIndex is null
            && arenas.Segments[3].Location.ApplicationObjectIndex == 0 && arenas.Segments[3].Location.NativeObjectIndex is null
            && arenas.Segments[6].Location is { ObjectType: 123, Occurrence: 3, ByteOffset: 7, NativeObjectIndex: null }
            && arenas.Segments[9].Location is { ObjectType: 123, Occurrence: 4, ByteOffset: 7 });
        Check("absent mask stays absent", arenas.Segments.All(s => s.Mask is null));
        var counts = ArchivedFactoryImage.Read(Stored(Container(
            Record(4, 0xFFFFFFFE, 4, 27, [0, 3], null), Record(4, 0, 4, 27, [0x42], null),
            Record(5, 0xFFFFFFFE, 0, 27, [0, 2], [0xFF, 0xFF]))));
        Check("property count is a separate two-byte component without a huge offset", counts.Status == ArchivedFactoryImageStatus.Decoded
            && counts.Segments[0].Location is { Component: FactoryImageComponent.PropertyElementCount, ByteOffset: null }
            && counts.Segments[1].Location is { Component: FactoryImageComponent.Data, ByteOffset: 0 }
            && counts.Segments[0].Data.ToHexString() == "0003" && counts.Segments[2].Mask?.ToHexString() == "FFFF");
        var typeCountLiterals = new[]
        {
            (Occurrence: (byte)0, Masked: "01000100010000000B000000FEFFFF007B000A0002000000030100FFFF", Unmasked: "01000100010000000B000000FEFFFF007B000A0002000000010100"),
            (Occurrence: (byte)7, Masked: "01000100010000000B000000FEFFFF077B000A0002000000030100FFFF", Unmasked: "01000100010000000B000000FEFFFF077B000A0002000000010100"),
            (Occurrence: (byte)255, Masked: "01000100010000000B000000FEFFFFFF7B000A0002000000030100FFFF", Unmasked: "01000100010000000B000000FEFFFFFF7B000A0002000000010100"),
        };
        foreach (var vector in typeCountLiterals)
        {
            var masked = ArchivedFactoryImage.Read(Stored(Convert.FromHexString(vector.Masked)));
            var unmasked = ArchivedFactoryImage.Read(Stored(Convert.FromHexString(vector.Unmasked)));
            var expected = new FactoryImageLocation(FactoryImageAddressSpace.PropertyByType, FactoryImageComponent.PropertyElementCount,
                null, ObjectType: 123, Occurrence: vector.Occurrence, PropertyId: 10);
            Check("literal type count retains the full occurrence byte " + vector.Occurrence, masked.Status == ArchivedFactoryImageStatus.Decoded
                && masked.Segments.Count == 1 && masked.Segments[0].Location == expected
                && masked.Segments[0].Data.ToHexString() == "0100" && masked.Segments[0].Mask?.ToHexString() == "FFFF");
            Check("literal type count preserves absent mask " + vector.Occurrence, unmasked.Status == ArchivedFactoryImageStatus.Decoded
                && unmasked.Segments.Count == 1 && unmasked.Segments[0].Location == expected
                && unmasked.Segments[0].Data.ToHexString() == "0100" && unmasked.Segments[0].Mask is null);
        }
        foreach (var tag in new uint[] { 4, 5, 11 })
            foreach (var lowOffset in new uint[] { 0xFFFFFD, 0xFFFFFF })
            {
                var rawOffset = tag == 11 ? 0x07000000 | lowOffset : lowOffset;
                var adjacent = ArchivedFactoryImage.Read(Stored(Container(Record(tag, rawOffset, 123, 10, [0xA5], null))));
                Check("property marker neighbor remains ordinary data " + tag + "/" + lowOffset, adjacent.Status == ArchivedFactoryImageStatus.Decoded
                    && adjacent.Segments.Count == 1 && adjacent.Segments[0].Location.Component == FactoryImageComponent.Data
                    && adjacent.Segments[0].Location.ByteOffset == lowOffset && adjacent.Segments[0].Data.ToHexString() == "A5");
            }
        foreach (var size in new[] { 0, 1, 3 })
            Refused("type count requires exactly two supplied bytes " + size,
                Stored(Container(Record(1, 100, 0, 0, [0x42], null), Record(11, 0x07FFFFFE, 123, 10, new byte[size], null))), ArchivedFactoryImageStatus.InvalidData);
        Refused("type count cannot publish a partial mask", Stored(Container(Record(11, 0x07FFFFFE, 123, 10, [1, 0], [0xFF]))), ArchivedFactoryImageStatus.InvalidData);
        var zeroMaskCount = ArchivedFactoryImage.Read(Stored(Container(Record(11, 0x07FFFFFE, 123, 10, [0, 0], [0, 0]))));
        Check("explicit zero count mask is preserved without replacing it", zeroMaskCount.Status == ArchivedFactoryImageStatus.Decoded
            && zeroMaskCount.Segments[0].Location.Component == FactoryImageComponent.PropertyElementCount
            && zeroMaskCount.Segments[0].Data.ToHexString() == "0000" && zeroMaskCount.Segments[0].Mask?.ToHexString() == "0000");
        var typeDataAndCount = ArchivedFactoryImage.Read(Stored(Container(
            Record(11, 0x07000000, 123, 10, [0xAA, 0xBB], null), Record(11, 0x07FFFFFE, 123, 10, [0, 2], null))));
        Check("type data and count coexist as separate components", typeDataAndCount.Status == ArchivedFactoryImageStatus.Decoded
            && typeDataAndCount.Segments.Count == 2 && typeDataAndCount.Segments[0].Location is { Component: FactoryImageComponent.Data, ByteOffset: 0 }
            && typeDataAndCount.Segments[1].Location is { Component: FactoryImageComponent.PropertyElementCount, ByteOffset: null }
            && typeDataAndCount.Segments[0].Data.ToHexString() == "AABB" && typeDataAndCount.Segments[1].Data.ToHexString() == "0002");
        Refused("duplicate type count cannot imply an overlay", Stored(Container(
            Record(11, 0x07FFFFFE, 123, 10, [0, 1], null), Record(11, 0x07FFFFFE, 123, 10, [0, 2], [0xFF, 0xFF]))), ArchivedFactoryImageStatus.AmbiguousLayout);
        var separateOccurrences = ArchivedFactoryImage.Read(Stored(Container(
            Record(11, 0x00FFFFFE, 123, 10, [0, 1], null), Record(11, 0x07FFFFFE, 123, 10, [0, 2], null))));
        Check("counts for distinct occurrences remain distinct", separateOccurrences.Status == ArchivedFactoryImageStatus.Decoded
            && separateOccurrences.Segments.Count == 2 && separateOccurrences.Segments[0].Location.Occurrence == 0
            && separateOccurrences.Segments[1].Location.Occurrence == 7
            && separateOccurrences.Segments.All(s => s.Location.Component == FactoryImageComponent.PropertyElementCount));
        var nonPropertyMarkers = ArchivedFactoryImage.Read(Stored(Container(Record(1, 0xFFFFFFFE, 0, 0, [1], null),
            Record(12, 0xFFFFFFFE, 3, 0, [2], null), Record(13, 0x07FFFFFE, 123, 0, [3], null))));
        Check("memory and relative markers never become property counts", nonPropertyMarkers.Status == ArchivedFactoryImageStatus.Decoded
            && nonPropertyMarkers.Segments.Count == 3 && nonPropertyMarkers.Segments.All(s => s.Location.Component == FactoryImageComponent.Data && s.Data.Length == 1)
            && nonPropertyMarkers.Segments[0].Location.ByteOffset == 0xFFFFFFFE && nonPropertyMarkers.Segments[1].Location.ByteOffset == 0xFFFFFFFE
            && nonPropertyMarkers.Segments[2].Location is { ByteOffset: 0xFFFFFE, Occurrence: 7 });
        foreach (var tag in new uint[] { 4, 5 })
            foreach (var highByte in new uint[] { 0, 7, 254 })
                Refused("noncanonical index count marker is explicitly unsupported " + tag + "/" + highByte,
                    Stored(Container(Record(tag, (highByte << 24) | 0xFFFFFE, 3, 10, [0, 1], null))), ArchivedFactoryImageStatus.UnsupportedFormat);

        Refused("wrong container version", Stored(Convert.FromHexString("0200010000000000")), ArchivedFactoryImageStatus.UnsupportedFormat);
        Refused("negative segment count", Stored(Convert.FromHexString("01000100FFFFFFFF")), ArchivedFactoryImageStatus.InvalidData);
        Refused("segment count budget", Stored(Convert.FromHexString("0100010001040000")), ArchivedFactoryImageStatus.LimitExceeded);
        Refused("unknown location kind", Stored(Container(Record(99, 0, 0, 0, [1], null))), ArchivedFactoryImageStatus.UnsupportedFormat);
        Refused("none is not fabricated address zero", Stored(Container(Record(0, 0, 0, 0, [1], null))), ArchivedFactoryImageStatus.UnsupportedFormat);
        foreach (var flags in new byte[] { 0, 2, 4, 255 })
        {
            var value = (byte[])literal.Clone(); value[24] = flags;
            Refused("no implicit data or reserved flags: " + flags, Stored(value), ArchivedFactoryImageStatus.UnsupportedFormat);
        }
        Refused("irrelevant memory selector cannot silently disappear", Stored(Container(Record(1, 0, 1, 0, [1], null))), ArchivedFactoryImageStatus.UnsupportedFormat);
        Refused("relative property selector is not inferred", Stored(Container(Record(12, 0, 4, 1, [1], null))), ArchivedFactoryImageStatus.UnsupportedFormat);
        Refused("count component requires exact supplied length", Stored(Container(Record(4, 0xFFFFFFFE, 4, 27, [1], null))), ArchivedFactoryImageStatus.InvalidData);
        Refused("span arithmetic cannot wrap", Stored(Container(Record(1, uint.MaxValue, 0, 0, [1, 2], null))), ArchivedFactoryImageStatus.InvalidData);
        Refused("same-arena intersecting spans are atomic refusals", Stored(Container(Record(1, 1, 0, 0, [1, 2], null), Record(2, 2, 0, 0, [3], null))), ArchivedFactoryImageStatus.AmbiguousLayout);
        Refused("duplicate selector cannot imply an overlay", Stored(Container(Record(4, 0, 4, 27, [1], null), Record(4, 0, 4, 27, [2], null))), ArchivedFactoryImageStatus.AmbiguousLayout);
        Refused("decoded trailing data", Stored(literal.Concat(new byte[] { 0 }).ToArray()), ArchivedFactoryImageStatus.InvalidData);
        Refused("compressed trailing data", original.Concat(new byte[] { 0 }).ToArray(), ArchivedFactoryImageStatus.InvalidData);
        var compressed = Compressed(literal, CompressionLevel.Optimal);
        Refused("concatenated compressed containers", compressed.Concat(compressed).ToArray(), ArchivedFactoryImageStatus.InvalidData);
        Check("every nonempty stored-block truncation is refused without partial output", Enumerable.Range(1, original.Length - 1)
            .All(n => ArchivedFactoryImage.Read(original.AsSpan(0, n)) is { Status: not ArchivedFactoryImageStatus.Decoded, Segments.Count: 0 }));
        Check("every nonempty compressed truncation is refused without partial output", Enumerable.Range(1, compressed.Length - 1)
            .All(n => ArchivedFactoryImage.Read(compressed.AsSpan(0, n)) is { Status: not ArchivedFactoryImageStatus.Decoded, Segments.Count: 0 }));
        Refused("compressed input budget precedes decompression", new byte[1024 * 1024 + 1], ArchivedFactoryImageStatus.LimitExceeded);
        Refused("decoded expansion budget", Compressed(new byte[4 * 1024 * 1024 + 1], CompressionLevel.SmallestSize), ArchivedFactoryImageStatus.LimitExceeded);
        var excessiveSize = (byte[])literal.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(excessiveSize.AsSpan(20), 1024 * 1024 + 1);
        Refused("declared payload cannot cause a large allocation", Stored(excessiveSize), ArchivedFactoryImageStatus.LimitExceeded);
        Console.WriteLine($"Factory source: {checks} checks, {failures} failures");
        return failures;
    }

    private static byte[] Stored(byte[] raw)
    {
        if (raw.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(raw));
        var result = new byte[raw.Length + 5]; result[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(1), (ushort)raw.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(3), (ushort)~raw.Length);
        raw.CopyTo(result, 5); return result;
    }
    private static byte[] Compressed(byte[] raw, CompressionLevel level)
    {
        using var output = new MemoryStream();
        using (var compressor = new DeflateStream(output, level, leaveOpen: true)) compressor.Write(raw);
        return output.ToArray();
    }
    private static byte[] Container(params byte[][] records)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(0x00010001u); writer.Write(records.Length);
        foreach (var record in records) writer.Write(record);
        return output.ToArray();
    }
    private static byte[] Record(uint tag, uint offset, ushort selector, ushort pid, byte[] data, byte[]? mask)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(tag); writer.Write(offset); writer.Write(selector); writer.Write(pid);
        writer.Write((uint)data.Length); writer.Write((byte)(mask is null ? 1 : 3)); writer.Write(data);
        if (mask is not null) writer.Write(mask);
        return output.ToArray();
    }
}
