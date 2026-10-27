using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Knx.Manufacturer;

public enum JungKspVersionOrigin { Device, Dca, Configurator }
public enum JungKspBlockKind { Version, Configuration, Opaque2, Opaque3, DeviceInfo, FunctionTexts, Extension }

/// <summary>Validated SP8 version metadata. Does not authorize a project import.</summary>
public sealed record JungKspHeader(JungKspVersionOrigin Origin, byte Major, byte Minor, byte Revision,
    ushort PackedVersion, byte ApiVersion, Version? DatabaseVersion);

/// <summary>Original framed payload, including its vendor marker/zlib size prefix where present.</summary>
public sealed class JungKspBlock
{
    private readonly byte[] _bytes;
    internal JungKspBlock(int index, JungKspBlockKind kind, byte[] bytes)
    {
        Index = index; Kind = kind; _bytes = (byte[])bytes.Clone();
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(_bytes));
    }
    public int Index { get; }
    public JungKspBlockKind Kind { get; }
    public int Length => _bytes.Length;
    public string Sha256 { get; }
    public byte[] GetBytes() => (byte[])_bytes.Clone();
}

/// <summary>Informational device identity from the file; never applied to a project by this parser.</summary>
public sealed class JungKspDeviceInfo
{
    internal JungKspDeviceInfo(JsonElement json, string? project, string? location, string? name, string? address, string[] rooms)
    { Json = json; Project = project; Location = location; Name = name; Address = address; Rooms = Array.AsReadOnly(rooms); }
    public JsonElement Json { get; }
    public string? Project { get; }
    public string? Location { get; }
    public string? Name { get; }
    public string? Address { get; }
    public IReadOnlyList<string> Rooms { get; }
}

/// <summary>Detached, immutable file data. Raw blocks are returned only as copies; JSON survives caller disposal.</summary>
public sealed class JungKspDocument
{
    internal JungKspDocument(string fileName, bool fromZip, JungKspHeader header, JungKspBlock[] blocks,
        JsonElement headerJson, JsonElement configurationJson, JungKspDeviceInfo? deviceInfo, JsonElement functionTextsJson)
    {
        FileName = fileName; FromZip = fromZip; Header = header; Blocks = Array.AsReadOnly(blocks);
        HeaderJson = headerJson; ConfigurationJson = configurationJson; DeviceInfo = deviceInfo; FunctionTextsJson = functionTextsJson;
    }
    public string FileName { get; }
    public bool FromZip { get; }
    public JungKspHeader Header { get; }
    public IReadOnlyList<JungKspBlock> Blocks { get; }
    public JsonElement HeaderJson { get; }
    public JsonElement ConfigurationJson { get; }
    public JungKspDeviceInfo? DeviceInfo { get; }
    public JsonElement? DeviceInfoJson => DeviceInfo?.Json;
    public JsonElement FunctionTextsJson { get; }
}

/// <summary>
/// Bounded, execution-free reader for the supported Jung SP8 KSP profile.
/// Accepts six blocks, or seven with marked device information. It does not mutate projects,
/// execute manufacturer code or imply compatibility with other Jung products.
/// </summary>
public static class JungKsp
{
    public const int MaxInputBytes = 16 * 1024 * 1024;
    public const int MaxBlocks = 32;
    public const int MaxInflatedBlockBytes = 8 * 1024 * 1024;
    public const int MaxInflatedTotalBytes = 16 * 1024 * 1024;
    public const int MaxJsonDepth = 64;
    public const int MaxJsonNodes = 100_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>Reads from the current stream position, leaving the caller's stream open.</summary>
    public static JungKspDocument Read(Stream input, string fileName)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Read(ReadBounded(input, MaxInputBytes, "KSP input"), fileName);
    }

    public static JungKspDocument Read(ReadOnlySpan<byte> input, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (input.Length > MaxInputBytes) throw Invalid("Input exceeds 16 MiB.");
        var bytes = input.ToArray();
        var fromZip = bytes.Length >= 2 && bytes[0] == 'P' && bytes[1] == 'K';
        if (fromZip) (bytes, fileName) = ReadZip(bytes);
        if (!string.Equals(Path.GetExtension(fileName), ".ksp", StringComparison.Ordinal))
            throw Invalid("The strict profile requires a .ksp filename matching its marker.");
        var frames = ReadFrames(bytes);
        if (frames.Count is not (6 or 7)) throw Invalid("Expected six blocks, or seven with device information.");
        var functionIndex = frames.Count - 2;
        RequireMarker(frames[functionIndex], "##01");
        if (frames.Count == 7) RequireMarker(frames[4], "##00");
        for (var index = 0; index < frames.Count; index++)
            if (frames[index].AsSpan().StartsWith("##00"u8) && !(frames.Count == 7 && index == 4))
                throw Invalid("Device-info marker outside its strict-profile position.");
        string extension;
        try { extension = Utf8.GetString(frames[^1]); }
        catch (DecoderFallbackException ex) { throw Invalid("Extension marker is not valid UTF-8.", ex); }
        if (extension.EndsWith('\0')) extension = extension[..^1];
        if (extension != "##FF0004|ksp") throw Invalid("Expected manufacturer/extension marker ##FF0004|ksp.");

        var budget = new Budget();
        var headerJson = ReadJson(frames[0], budget);
        var configuration = ReadJson(frames[1], budget);
        RequireObjectProperty(RequireObjectProperty(configuration, "items"), "objects_list");
        RequireObjectProperty(RequireObjectProperty(configuration, "elements"), "objects_list");
        var header = ReadHeader(headerJson, configuration);
        JungKspDeviceInfo? deviceInfo = null;
        if (frames.Count == 7) deviceInfo = ReadDeviceInfo(ReadJson(frames[4].AsSpan(4), budget));
        var functions = ReadJson(frames[functionIndex].AsSpan(4), budget);
        var blocks = frames.Select((frame, index) => new JungKspBlock(index, index switch
        {
            0 => JungKspBlockKind.Version, 1 => JungKspBlockKind.Configuration,
            2 => JungKspBlockKind.Opaque2, 3 => JungKspBlockKind.Opaque3,
            _ when index == frames.Count - 1 => JungKspBlockKind.Extension,
            _ when index == functionIndex => JungKspBlockKind.FunctionTexts,
            _ => JungKspBlockKind.DeviceInfo,
        }, frame)).ToArray();
        return new(fileName, fromZip, header, blocks, headerJson, configuration, deviceInfo, functions);
    }

    private static List<byte[]> ReadFrames(byte[] input)
    {
        var frames = new List<byte[]>(); var position = 0;
        while (position < input.Length)
        {
            if (frames.Count == MaxBlocks) throw Invalid("Block count exceeds 32.");
            if (input.Length - position < 4) throw Invalid("Truncated block length.");
            var length = BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(position, 4)); position += 4;
            if (length < 0 || length > input.Length - position) throw Invalid("Negative or truncated block length.");
            frames.Add(input.AsSpan(position, length).ToArray()); position += length;
        }
        return frames;
    }

    private sealed class Budget { public int InflatedBytes; public int JsonNodes; }

    private static JsonElement ReadJson(ReadOnlySpan<byte> block, Budget budget)
    {
        if (block.Length < 10) throw Invalid("Truncated length-prefixed zlib block.");
        var declared = BinaryPrimitives.ReadUInt32BigEndian(block);
        if (declared > MaxInflatedBlockBytes || declared > MaxInflatedTotalBytes - budget.InflatedBytes)
            throw Invalid("Inflated block/aggregate byte budget exceeded.");
        budget.InflatedBytes += (int)declared;
        var compressed = block[4..].ToArray();
        if ((compressed[0] & 15) != 8 || compressed[0] >> 4 > 7 || (compressed[0] * 256 + compressed[1]) % 31 != 0
            || (compressed[1] & 32) != 0) throw Invalid("Unsupported zlib header or preset dictionary.");
        var output = new byte[(int)declared];
        // One-byte reads prevent native inflater read-ahead from hiding trailing/concatenated data.
        // EOF throws: a truncated stream cannot masquerade as an ordinary decompressor EOF.
        using (var source = new ExactZlibInput(compressed))
        using (var zlib = new ZLibStream(source, CompressionMode.Decompress, leaveOpen: true))
        {
            var offset = 0;
            while (offset < output.Length)
            {
                var count = zlib.Read(output.AsSpan(offset));
                if (count == 0) throw Invalid("Zlib output is shorter than its declared length.");
                offset += count;
            }
            if (zlib.ReadByte() != -1) throw Invalid("Zlib output exceeds its declared length.");
            if (source.Position != compressed.Length) throw Invalid("Trailing or concatenated zlib data.");
        }
        if (Adler32(output) != BinaryPrimitives.ReadUInt32BigEndian(compressed.AsSpan(compressed.Length - 4)))
            throw Invalid("Invalid zlib checksum.");
        string json;
        try { json = Utf8.GetString(output); }
        catch (DecoderFallbackException ex) { throw Invalid("JSON is not valid UTF-8.", ex); }
        if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..];
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            CheckJson(document.RootElement, budget);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("JSON root must be an object.");
            return document.RootElement.Clone();
        }
        catch (JsonException ex) { throw Invalid("Invalid or excessively nested JSON.", ex); }
    }

    private static void CheckJson(JsonElement element, Budget budget)
    {
        CountNode(budget);
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                CountNode(budget);
                if (!names.Add(property.Name)) throw Invalid("Duplicate JSON property.");
                CheckJson(property.Value, budget);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckJson(child, budget);
    }
    private static void CountNode(Budget budget)
    { if (++budget.JsonNodes > MaxJsonNodes) throw Invalid("JSON node budget exceeds 100000."); }

    private static JungKspHeader ReadHeader(JsonElement header, JsonElement configuration)
    {
        (JungKspVersionOrigin Origin, JsonElement Json)? selected = null;
        foreach (var (name, origin) in new[] { ("device", JungKspVersionOrigin.Device), ("dca", JungKspVersionOrigin.Dca), ("configurator", JungKspVersionOrigin.Configurator) })
        {
            if (!header.TryGetProperty(name, out var version) || version.ValueKind == JsonValueKind.Null) continue;
            if (version.ValueKind != JsonValueKind.Object) throw Invalid("Version entry must be an object.");
            if (string.IsNullOrEmpty(Scalar(version, "major"))) continue;
            if (selected is not null) throw Invalid("Multiple nonempty version origins.");
            selected = (origin, version);
        }
        if (selected is not { } chosen) throw Invalid("Missing version origin.");
        var major = Component(chosen.Json, "major", 31);
        var minor = Component(chosen.Json, "minor", 31);
        var revision = Component(chosen.Json, "revision", 63);
        if (major != 1) throw Invalid("Only SP8 major version 1 is supported.");
        JsonElement? versions = null;
        if (configuration.TryGetProperty("versions", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.Object) throw Invalid("versions must be an object.");
            versions = value;
        }
        var apiText = versions is { } v ? Scalar(v, "dca_api") : null;
        byte api = 1;
        if (!string.IsNullOrEmpty(apiText) && (!byte.TryParse(apiText, NumberStyles.None, CultureInfo.InvariantCulture, out api) || api > 1))
            throw Invalid("Unsupported DCA API version.");
        var databaseText = versions is { } db ? Scalar(db, "database") : null;
        Version.TryParse(databaseText, out var database);
        return new(chosen.Origin, major, minor, revision, (ushort)((major << 11) | (minor << 6) | revision), api, database);
    }

    private static byte Component(JsonElement json, string name, byte max)
    {
        var text = Scalar(json, name);
        if (string.IsNullOrEmpty(text)) return 0;
        if (!byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > max)
            throw Invalid("Version component exceeds its bit width: " + name);
        return value;
    }
    private static string? Scalar(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(),
            _ => throw Invalid("Expected scalar value: " + name),
        };
    }
    private static JsonElement RequireObjectProperty(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw Invalid("Expected JSON object: " + name);
        return value;
    }
    private static JungKspDeviceInfo ReadDeviceInfo(JsonElement json)
    {
        var rooms = new List<string>();
        if (json.TryGetProperty("rooms", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.Array) throw Invalid("Device rooms must be an array.");
            foreach (var room in value.EnumerateArray())
            {
                if (room.ValueKind != JsonValueKind.String) throw Invalid("Device room must be a string.");
                rooms.Add(room.GetString()!);
            }
        }
        return new(json, Scalar(json, "project"), Scalar(json, "location"), Scalar(json, "name"), Scalar(json, "address"), rooms.ToArray());
    }

    private static (byte[] Bytes, string Name) ReadZip(byte[] bytes)
    {
        ValidateZipDirectory(bytes);
        using var memory = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxBlocks) throw Invalid("ZIP entry count exceeds 32.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0; ZipArchiveEntry? selected = null;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.Length == 0 || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl)
                || name.StartsWith('/') || name.Split('/').Any(part => part is "." or "..")
                || name.TrimEnd('/').Split('/').Any(part => part.Length == 0) || !names.Add(name)
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw Invalid("Unsafe, duplicate or symbolic ZIP entry.");
            if (entry.Length > MaxInputBytes - total) throw Invalid("ZIP expanded size exceeds 16 MiB.");
            total += entry.Length;
            if (!name.Contains('/') && string.Equals(Path.GetExtension(name), ".ksp", StringComparison.OrdinalIgnoreCase))
            {
                if (selected is not null) throw Invalid("ZIP contains multiple root KSP files.");
                selected = entry;
            }
        }
        if (selected is null) throw Invalid("ZIP must contain exactly one root KSP file.");
        byte[]? result = null; var actualTotal = 0;
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            var data = ReadBounded(stream, MaxInputBytes - actualTotal, "ZIP entry"); actualTotal += data.Length;
            if (data.LongLength != entry.Length || Crc32(data) != entry.Crc32) throw Invalid("ZIP entry size/checksum mismatch.");
            if (entry == selected) result = data;
        }
        return (result!, selected.FullName);
    }

    private static void ValidateZipDirectory(byte[] bytes)
    {
        for (var index = bytes.Length - 22; index >= Math.Max(0, bytes.Length - 65557); index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) != 0x06054B50
                || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index + 20, 2)) != bytes.Length - index - 22) continue;
            var end = bytes.AsSpan(index, 22);
            var count = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(end[4..]) != 0 || count > MaxBlocks
                || BinaryPrimitives.ReadUInt16LittleEndian(end[8..]) != count || (ulong)offset + size != (ulong)index)
                throw Invalid("ZIP64, split, oversized or inconsistent ZIP directory is unsupported.");
            return;
        }
        throw Invalid("Missing ZIP end directory.");
    }

    private static byte[] ReadBounded(Stream input, int maximum, string context)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = input.Read(buffer, 0, Math.Min(buffer.Length, maximum - (int)output.Length + 1));
            if (count == 0) return output.ToArray();
            if (count > maximum - output.Length) throw Invalid(context + " exceeds its byte budget.");
            output.Write(buffer, 0, count);
        }
    }
    private static void RequireMarker(byte[] block, string marker)
    { if (block.Length < 4 || !block.AsSpan(0, 4).SequenceEqual(Encoding.ASCII.GetBytes(marker))) throw Invalid("Missing marker " + marker + "."); }
    private static uint Adler32(byte[] bytes)
    {
        uint a = 1, b = 0;
        foreach (var value in bytes) { a = (a + value) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }
    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        { var value = index; for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xEDB88320u : 0u); table[index] = value; }
        return table;
    }
    private static uint Crc32(byte[] bytes)
    { uint crc = uint.MaxValue; foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8); return ~crc; }
    private static InvalidDataException Invalid(string message, Exception? inner = null) => new("Jung KSP: " + message, inner);

    private sealed class ExactZlibInput(byte[] bytes) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            if (_position == bytes.Length) throw Invalid("Truncated zlib stream.");
            buffer[0] = bytes[_position++]; return 1;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
