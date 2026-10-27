using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Knx;

public enum ArchivedFactoryImageStatus { Decoded, Missing, InvalidData, UnsupportedFormat, LimitExceeded, AmbiguousLayout }
public enum FactoryImageAddressSpace
{
    StandardMemory, UserMemory, SystemProperty, AppProperty, LcSlaveMemory, LcFilterMemory,
    PropertyByType, RelativeMemoryByIndex, RelativeMemoryByType,
}
public enum FactoryImageComponent { Data, PropertyElementCount }

/// <summary>Serialized source coordinates. No physical mapping or index rebasing is implied.</summary>
public sealed record FactoryImageLocation(FactoryImageAddressSpace AddressSpace, FactoryImageComponent Component,
    uint? ByteOffset, ushort? NativeObjectIndex = null, ushort? ApplicationObjectIndex = null,
    ushort? ObjectType = null, byte? Occurrence = null, ushort? PropertyId = null);
public sealed record FactoryImageSegment(FactoryImageLocation Location, ImmutablePatchBytes Data, ImmutablePatchBytes? Mask);

/// <summary>Decoded factory source, not present device state or an authenticated programming image.</summary>
public sealed class ArchivedFactoryImageSource
{
    public int ProfileVersion => 1;
    public ArchivedFactoryImageStatus Status { get; }
    public string? BlobHash { get; }
    public string? DecodedHash { get; }
    public IReadOnlyList<FactoryImageSegment> Segments { get; }
    public bool StampVerified => false;
    internal ArchivedFactoryImageSource(ArchivedFactoryImageStatus status, string? blobHash = null,
        string? decodedHash = null, FactoryImageSegment[]? segments = null)
    {
        Status = status; BlobHash = blobHash; DecodedHash = decodedHash;
        Segments = Array.AsReadOnly(segments is null ? [] : (FactoryImageSegment[])segments.Clone());
    }
}

/// <summary>Bounded decoder for explicitly supplied compressed factory buffers; never supplies missing bytes.</summary>
public static class ArchivedFactoryImage
{
    private const int MaxCompressed = 1024 * 1024, MaxDecoded = 4 * 1024 * 1024;
    private const int MaxSegments = 1024, MaxPayload = 1024 * 1024;
    private const uint Format = 0x00010001;

    public static ArchivedFactoryImageSource Read(ReadOnlySpan<byte> blob)
    {
        if (blob.IsEmpty) return new(ArchivedFactoryImageStatus.Missing);
        if (blob.Length > MaxCompressed) return new(ArchivedFactoryImageStatus.LimitExceeded);
        var hash = Convert.ToHexStringLower(SHA256.HashData(blob));
        try
        {
            using var input = new ExactDeflateInput(blob.ToArray());
            using var output = new MemoryStream();
            using (var inflater = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true))
            {
                Span<byte> buffer = stackalloc byte[8192];
                int length;
                while ((length = inflater.Read(buffer)) != 0)
                {
                    if (output.Length + length > MaxDecoded) Fail(ArchivedFactoryImageStatus.LimitExceeded);
                    output.Write(buffer[..length]);
                }
            }
            if (input.Consumed != blob.Length) Fail(ArchivedFactoryImageStatus.InvalidData);
            var decoded = output.ToArray();
            var cursor = new Cursor(decoded);
            if (cursor.UInt32() != Format) Fail(ArchivedFactoryImageStatus.UnsupportedFormat);
            var count = cursor.Int32();
            if (count < 0) Fail(ArchivedFactoryImageStatus.InvalidData);
            if (count > MaxSegments) Fail(ArchivedFactoryImageStatus.LimitExceeded);
            var segments = new List<FactoryImageSegment>(count);
            var locations = new HashSet<FactoryImageLocation>();
            var spans = new Dictionary<(FactoryImageAddressSpace, FactoryImageComponent, ushort?, ushort?, ushort?, byte?, ushort?), List<(ulong Start, ulong End)>>();
            long copied = 0;
            for (var i = 0; i < count; i++)
            {
                var tag = cursor.UInt32(); var offset = cursor.UInt32();
                var objectRef = cursor.UInt16(); var pid = cursor.UInt16();
                var size = cursor.UInt32(); var flags = cursor.Byte();
                if (flags is not (1 or 3)) Fail(ArchivedFactoryImageStatus.UnsupportedFormat);
                if (size > MaxPayload || (copied += size * (flags == 3 ? 2L : 1L)) > MaxDecoded)
                    Fail(ArchivedFactoryImageStatus.LimitExceeded);
                var location = Location(tag, offset, objectRef, pid);
                if (location.Component == FactoryImageComponent.PropertyElementCount && size != 2)
                    Fail(ArchivedFactoryImageStatus.InvalidData);
                var start = (ulong)(location.ByteOffset ?? 0);
                var end = start + size;
                if (end > (1UL << 32)) Fail(ArchivedFactoryImageStatus.InvalidData);
                if (!locations.Add(location)) Fail(ArchivedFactoryImageStatus.AmbiguousLayout);
                var arena = (location.AddressSpace, location.Component, location.NativeObjectIndex,
                    location.ApplicationObjectIndex, location.ObjectType, location.Occurrence, location.PropertyId);
                if (!spans.TryGetValue(arena, out var intervals)) spans.Add(arena, intervals = []);
                if (intervals.Any(span => start < span.End && span.Start < end)) Fail(ArchivedFactoryImageStatus.AmbiguousLayout);
                intervals.Add((start, end));
                var data = new ImmutablePatchBytes(cursor.Bytes((int)size));
                var mask = flags == 3 ? new ImmutablePatchBytes(cursor.Bytes((int)size)) : null;
                segments.Add(new(location, data, mask));
            }
            if (!cursor.AtEnd) Fail(ArchivedFactoryImageStatus.InvalidData);
            return new(ArchivedFactoryImageStatus.Decoded, hash,
                Convert.ToHexStringLower(SHA256.HashData(decoded)), segments.ToArray());
        }
        catch (DecodeFailure failure) { return new(failure.Status, hash); }
        catch (Exception failure) when (failure is IOException or InvalidDataException)
        { return new(ArchivedFactoryImageStatus.InvalidData, hash); }
    }

    private static FactoryImageLocation Location(uint tag, uint offset, ushort objectRef, ushort pid)
    {
        var propertyOffset = offset & 0xFFFFFF;
        if ((tag is 4 or 5) && propertyOffset == 0xFFFFFE && offset != 0xFFFFFFFE)
            Fail(ArchivedFactoryImageStatus.UnsupportedFormat);
        var component = (tag is 4 or 5) && offset == 0xFFFFFFFE || tag == 11 && propertyOffset == 0xFFFFFE
            ? FactoryImageComponent.PropertyElementCount : FactoryImageComponent.Data;
        uint? byteOffset = component == FactoryImageComponent.Data ? offset : null;
        switch (tag)
        {
            case 1: case 2: case 3: case 6: case 7:
                if (objectRef != 0 || pid != 0) Fail(ArchivedFactoryImageStatus.UnsupportedFormat);
                return new(tag switch { 3 => FactoryImageAddressSpace.UserMemory, 6 => FactoryImageAddressSpace.LcSlaveMemory,
                    7 => FactoryImageAddressSpace.LcFilterMemory, _ => FactoryImageAddressSpace.StandardMemory }, component, byteOffset);
            case 4: return new(FactoryImageAddressSpace.SystemProperty, component, byteOffset, NativeObjectIndex: objectRef, PropertyId: pid);
            case 5: return new(FactoryImageAddressSpace.AppProperty, component, byteOffset, ApplicationObjectIndex: objectRef, PropertyId: pid);
            case 11: return new(FactoryImageAddressSpace.PropertyByType, component, byteOffset is null ? null : propertyOffset,
                ObjectType: objectRef, Occurrence: (byte)(offset >> 24), PropertyId: pid);
            case 12:
                if (pid != 0) Fail(ArchivedFactoryImageStatus.UnsupportedFormat);
                return new(FactoryImageAddressSpace.RelativeMemoryByIndex, component, offset, NativeObjectIndex: objectRef);
            case 13:
                if (pid != 0) Fail(ArchivedFactoryImageStatus.UnsupportedFormat);
                return new(FactoryImageAddressSpace.RelativeMemoryByType, component, offset & 0xFFFFFF,
                    ObjectType: objectRef, Occurrence: (byte)(offset >> 24));
            default: Fail(ArchivedFactoryImageStatus.UnsupportedFormat); return null!;
        }
    }

    private static void Fail(ArchivedFactoryImageStatus status) => throw new DecodeFailure(status);
    private sealed class DecodeFailure(ArchivedFactoryImageStatus status) : Exception
    { internal ArchivedFactoryImageStatus Status { get; } = status; }

    private sealed class Cursor(byte[] bytes)
    {
        private int _position;
        internal bool AtEnd => _position == bytes.Length;
        internal ReadOnlySpan<byte> Bytes(int count)
        {
            if (count < 0 || count > bytes.Length - _position) Fail(ArchivedFactoryImageStatus.InvalidData);
            var result = bytes.AsSpan(_position, count); _position += count; return result;
        }
        internal uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
        internal int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));
        internal ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));
        internal byte Byte() => Bytes(1)[0];
    }

    // One byte per read prevents the inflater from swallowing trailing input. An input request
    // after EOF fails even when the process-wide compression validation setting is permissive.
    private sealed class ExactDeflateInput(byte[] bytes) : Stream
    {
        internal int Consumed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => Consumed; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            if (Consumed == bytes.Length) throw new EndOfStreamException();
            buffer[0] = bytes[Consumed++]; return 1;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
