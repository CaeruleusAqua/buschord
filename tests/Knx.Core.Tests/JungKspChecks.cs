using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knx.Manufacturer;

internal static class JungKspChecks
{
    private const string Header = "{\"configurator\":{\"major\":\"1\",\"minor\":\"2\",\"revision\":\"3\"}}";
    private const string Configuration = "{\"versions\":{\"dca_api\":\"1\",\"database\":\"3.0.0\"},\"items\":{\"objects_list\":{}},\"elements\":{\"objects_list\":{}}}";
    private const string Functions = "{\"psoe_fixture\":{\"switch\":{\"command\":{\"en\":\"Switch\",\"de\":\"Schalten\"}}}}";

    internal static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok)
        { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + "Jung KSP: " + name); if (!ok) failures++; }
        void Reject(string name, Func<byte[]> create, string fileName = "fixture.ksp")
        {
            try { JungKsp.Read(create(), fileName); Check(name, false); }
            catch (InvalidDataException) { Check(name, true); }
            catch (Exception ex) { Console.WriteLine("    Unexpected " + ex.GetType().Name + ": " + ex.Message); Check(name, false); }
        }
        byte[] Changed(int index, Func<byte[], byte[]> change)
        { var parts = Parts(); parts[index] = change(parts[index]); return Frame(parts); }

        var parts = Parts(); var source = Frame(parts); var document = JungKsp.Read(source, "fixture.ksp");
        Check("six-block header, origin, API and packed bit fields", document.Header is
            { Origin: JungKspVersionOrigin.Configurator, Major: 1, Minor: 2, Revision: 3, PackedVersion: 2179, ApiVersion: 1 }
            && document.Header.DatabaseVersion == new Version(3, 0, 0) && document.Blocks.Count == 6 && !document.FromZip);
        Check("raw blobs retain every original byte and SHA256", Enumerable.Range(0, parts.Count).All(index =>
            document.Blocks[index].Index == index && document.Blocks[index].Length == parts[index].Length
            && document.Blocks[index].GetBytes().SequenceEqual(parts[index])
            && document.Blocks[index].Sha256 == Convert.ToHexStringLower(SHA256.HashData(parts[index]))));
        Check("opaque blobs are not guessed as zlib or JSON", document.Blocks[2].Kind == JungKspBlockKind.Opaque2
            && document.Blocks[2].GetBytes().SequenceEqual(new byte[] { 255, 0, 1, 2 }) && document.Blocks[3].Length == 0);
        var copied = document.Blocks[1].GetBytes(); copied[0] ^= 255; Array.Fill(source, (byte)0);
        Check("mutating input and returned buffers cannot mutate the document", document.Blocks[1].GetBytes().SequenceEqual(parts[1])
            && document.ConfigurationJson.GetProperty("versions").GetProperty("dca_api").GetString() == "1");
        var collectionBlocked = false;
        try { ((IList<JungKspBlock>)document.Blocks)[0] = document.Blocks[1]; }
        catch (NotSupportedException) { collectionBlocked = true; }
        Check("block collection is read-only", collectionBlocked);
        Check("detached function-text JSON remains usable", document.FunctionTextsJson.GetProperty("psoe_fixture")
            .GetProperty("switch").GetProperty("command").GetProperty("de").GetString() == "Schalten");
        using (var input = new ChunkStream(Frame(Parts())))
        {
            var streamed = JungKsp.Read(input, "fixture.ksp");
            Check("non-seekable partial-read stream stays open", streamed.Header == document.Header && input.CanRead && !input.Disposed);
        }

        var seven = Parts(deviceInfo: "{\"project\":\"Synthetic\",\"location\":\"Room\",\"name\":\"Panel\",\"address\":\"1.1.9\",\"rooms\":[\"A\",\"B\"]}");
        var withInfo = JungKsp.Read(Frame(seven), "fixture.ksp");
        Check("seven-block device info is informational and typed", withInfo.Blocks.Count == 7
            && withInfo.DeviceInfo is { Project: "Synthetic", Location: "Room", Name: "Panel", Address: "1.1.9" }
            && withInfo.DeviceInfo.Rooms.SequenceEqual(["A", "B"]) && withInfo.DeviceInfoJson?.ValueKind == JsonValueKind.Object
            && withInfo.Blocks[4].Kind == JungKspBlockKind.DeviceInfo && withInfo.Blocks[5].Kind == JungKspBlockKind.FunctionTexts);
        Check("absent device info stays absent", document.DeviceInfo is null && document.DeviceInfoJson is null);
        foreach (var origin in new[] { "device", "dca", "configurator" })
        {
            var originDoc = JungKsp.Read(Frame(Parts(header: "{\"" + origin + "\":{\"major\":1}}")), "fixture.ksp");
            Check("version origin " + origin + " with absent minor/revision", originDoc.Header.Major == 1
                && originDoc.Header.Minor == 0 && originDoc.Header.Revision == 0);
        }
        Check("boundary version values use 5/5/6 bits", JungKsp.Read(Frame(Parts(header:
            "{\"device\":{\"major\":1,\"minor\":31,\"revision\":63}}")), "fixture.ksp").Header.PackedVersion == 4095);
        Check("missing API metadata defaults to API one", JungKsp.Read(Frame(Parts(configuration:
            "{\"items\":{\"objects_list\":{}},\"elements\":{\"objects_list\":{}}}")), "fixture.ksp").Header.ApiVersion == 1);
        Check("empty API metadata defaults to API one", JungKsp.Read(Frame(Parts(configuration:
            Configuration.Replace("\"dca_api\":\"1\"", "\"dca_api\":\"\""))), "fixture.ksp").Header.ApiVersion == 1);
        Check("API version zero is accepted", JungKsp.Read(Frame(Parts(configuration:
            Configuration.Replace("\"dca_api\":\"1\"", "\"dca_api\":0"))), "fixture.ksp").Header.ApiVersion == 0);
        Check("marker without optional NUL is accepted", JungKsp.Read(Changed(5, bytes => bytes[..^1]), "fixture.ksp").Blocks[5].Length == 12);
        Check("UTF-8 BOM in JSON matches StreamReader input", JungKsp.Read(Frame(Parts(header: "\uFEFF" + Header)), "fixture.ksp").Header.Major == 1);

        Reject("empty file", () => []);
        Reject("input byte cap", () => new byte[JungKsp.MaxInputBytes + 1]);
        Reject("truncated four-byte frame prefix", () => [1, 2, 3]);
        Reject("signed negative frame length", () => [255, 255, 255, 255]);
        Reject("truncated framed payload", () => [2, 0, 0, 0, 7]);
        Reject("big-endian frame length is not accepted", () => { var bytes = Frame(Parts()); BinaryPrimitives.WriteInt32BigEndian(bytes, parts[0].Length); return bytes; });
        Reject("trailing partial frame", () => Frame(Parts()).Concat(new byte[] { 0 }).ToArray());
        Reject("five-block profile", () => Frame(Parts().Take(5)));
        Reject("additional eighth block", () => Frame(Parts().Concat(new byte[][] { [], [] })));
        Reject("block count cap", () => Frame(Enumerable.Repeat(Array.Empty<byte>(), 33)));
        Reject("wrong filename extension", () => Frame(Parts()), "fixture.other");
        Reject("filename/marker extension case mismatch", () => Frame(Parts()), "fixture.KSP");
        Reject("foreign manufacturer marker", () => Changed(5, _ => Encoding.UTF8.GetBytes("##FF0008|ksp\0")));
        Reject("wrong extension marker", () => Changed(5, _ => Encoding.UTF8.GetBytes("##FF0004|fkp\0")));
        Reject("doubled marker terminator", () => Changed(5, bytes => bytes.Concat(new byte[] { 0 }).ToArray()));
        Reject("invalid marker UTF-8", () => Changed(5, _ => [0xC3, 0x28]));
        Reject("missing function-text marker", () => Changed(4, _ => Pack(Encoding.UTF8.GetBytes(Functions))));
        Reject("legacy unmarked device info is outside the strict profile", () => { var p = Parts(deviceInfo: "{}"); p[4] = Pack(Encoding.UTF8.GetBytes("{}")); return Frame(p); });
        Reject("wrong device-info position", () => { var p = Parts(deviceInfo: "{}"); (p[4], p[5]) = (p[5], p[4]); return Frame(p); });
        Reject("an earlier opaque block must not override device identity parsing", () => Changed(2, _ => Encoding.ASCII.GetBytes("##00").Concat(Pack(Encoding.UTF8.GetBytes("{}"))).ToArray()));
        Reject("device rooms must be strings", () => Frame(Parts(deviceInfo: "{\"rooms\":[1]}")));

        Reject("multiple nonempty version origins", () => Frame(Parts(header: "{\"device\":{\"major\":1},\"dca\":{\"major\":1}}")));
        Reject("missing version origin", () => Frame(Parts(header: "{}")));
        Reject("unsupported major version", () => Frame(Parts(header: "{\"device\":{\"major\":2}}")));
        Reject("minor overflow must not silently mask", () => Frame(Parts(header: "{\"device\":{\"major\":1,\"minor\":32}}")));
        Reject("revision overflow must not silently mask", () => Frame(Parts(header: "{\"device\":{\"major\":1,\"revision\":64}}")));
        Reject("fractional version component", () => Frame(Parts(header: "{\"device\":{\"major\":1.5}}")));
        Reject("unsupported API version", () => Frame(Parts(configuration: Configuration.Replace("\"dca_api\":\"1\"", "\"dca_api\":2"))));
        Reject("configuration requires item/element dictionaries", () => Frame(Parts(configuration: "{}")));

        Reject("zlib prefix byte order is big endian", () => Changed(0, bytes => { BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)Encoding.UTF8.GetByteCount(Header)); return bytes; }));
        Reject("inflate declaration exceeds block cap before allocation", () => Changed(0, bytes => { BinaryPrimitives.WriteUInt32BigEndian(bytes, JungKsp.MaxInflatedBlockBytes + 1u); return bytes; }));
        Reject("zlib output shorter than declared", () => Changed(0, bytes => { var size = BinaryPrimitives.ReadUInt32BigEndian(bytes); BinaryPrimitives.WriteUInt32BigEndian(bytes, size + 1); return bytes; }));
        Reject("zlib output longer than declared", () => Changed(0, bytes => { var size = BinaryPrimitives.ReadUInt32BigEndian(bytes); BinaryPrimitives.WriteUInt32BigEndian(bytes, size - 1); return bytes; }));
        Reject("zlib checksum mismatch", () => Changed(0, bytes => { bytes[^1] ^= 1; return bytes; }));
        Reject("truncated zlib trailer", () => Changed(0, bytes => bytes[..^1]));
        Reject("missing zlib trailer", () => Changed(0, bytes => bytes[..^4]));
        Reject("trailing bytes after zlib", () => Changed(0, bytes => bytes.Concat(new byte[] { 9 }).ToArray()));
        Reject("concatenated zlib streams", () => Changed(0, bytes => bytes.Concat(Pack(Encoding.UTF8.GetBytes("{}"))[4..]).ToArray()));
        Reject("invalid zlib header", () => Changed(0, bytes => { bytes[4] = 0; return bytes; }));
        Reject("invalid JSON UTF-8", () => Changed(0, _ => Pack([0xC3, 0x28])));
        Reject("JSON root must be object", () => Frame(Parts(header: "[]")));
        Reject("JSON trailing syntax", () => Frame(Parts(header: Header + "{}")));
        Reject("JSON comments are not accepted", () => Frame(Parts(header: "{/*comment*/\"device\":{\"major\":1}}")));
        Reject("JSON trailing comma is not accepted", () => Frame(Parts(header: "{\"device\":{\"major\":1},}")));
        Reject("JSON duplicate decoded property names", () => Frame(Parts(header: "{\"device\":{\"major\":1,\"\\u006dajor\":1}}")));
        Reject("JSON nested duplicates in opaque configuration extensions", () => Frame(Parts(configuration:
            Configuration[..^1] + ",\"extra\":{\"x\":1,\"x\":2}}")));
        Reject("JSON depth cap", () => Frame(Parts(functions: "{\"x\":" + new string('[', 65) + "0" + new string(']', 65) + "}")));
        Reject("JSON aggregate node cap", () => Frame(Parts(functions: "{\"x\":[" + string.Join(',', Enumerable.Repeat("0", JungKsp.MaxJsonNodes)) + "]}")));
        Reject("JSON node budget is shared by configuration and function-text blocks", () =>
        {
            var array = "[" + string.Join(',', Enumerable.Repeat("0", 60_000)) + "]";
            return Frame(Parts(configuration: Configuration[..^1] + ",\"x\":" + array + "}", functions: "{\"x\":" + array + "}"));
        });
        Reject("aggregate inflate cap across valid blocks", () =>
        {
            var pad = new string('x', 5 * 1024 * 1024);
            return Frame(Parts(header: Header[..^1] + ",\"pad\":\"" + pad + "\"}",
                configuration: Configuration[..^1] + ",\"pad\":\"" + pad + "\"}",
                deviceInfo: "{\"pad\":\"" + pad + "\"}", functions: "{\"pad\":\"" + pad + "\"}"));
        });

        var zip = Zip(("fixture.ksp", Frame(Parts())), ("notes.txt", Encoding.UTF8.GetBytes("Synthetic")));
        var zipped = JungKsp.Read(zip, "fixture.zip");
        Check("ZIP selects one root KSP without extracting files", zipped.FromZip && zipped.FileName == "fixture.ksp"
            && zipped.Header == document.Header && zipped.Blocks[1].Sha256 == document.Blocks[1].Sha256);
        Reject("ZIP without root KSP", () => Zip(("nested/fixture.ksp", Frame(Parts()))));
        Reject("ZIP with two root KSPs", () => Zip(("one.ksp", Frame(Parts())), ("two.ksp", Frame(Parts()))));
        Reject("ZIP traversal in any entry", () => Zip(("fixture.ksp", Frame(Parts())), ("../escape.txt", [])));
        Reject("ZIP absolute path", () => Zip(("fixture.ksp", Frame(Parts())), ("/escape.txt", [])));
        Reject("ZIP Windows backslash path", () => Zip(("fixture.ksp", Frame(Parts())), ("..\\escape.txt", [])));
        Reject("ZIP drive/alternate-stream path", () => Zip(("fixture.ksp", Frame(Parts())), ("C:escape", [])));
        Reject("ZIP duplicate case-insensitive names", () => Zip(("fixture.ksp", Frame(Parts())), ("notes", []), ("NOTES", [])));
        Reject("ZIP symbolic entry", () =>
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            { var entry = archive.CreateEntry("fixture.ksp"); entry.ExternalAttributes = unchecked((int)0xA1FF0000); using var stream = entry.Open(); stream.Write(Frame(Parts())); }
            return output.ToArray();
        });
        Reject("ZIP directory entry count cap before expansion", () => Zip(Enumerable.Range(0, 33).Select(n => ($"{n}.txt", Array.Empty<byte>())).ToArray()));
        Reject("ZIP aggregate expanded cap", () => Zip(("fixture.ksp", Frame(Parts())),
            ("one.bin", new byte[8 * 1024 * 1024]), ("two.bin", new byte[8 * 1024 * 1024])));
        Reject("ZIP entry CRC is checked before returning payload", () =>
        {
            var bytes = Zip(("fixture.ksp", Frame(Parts())));
            var directory = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 22 + 16)); bytes[directory + 16] ^= 1; return bytes;
        });
        Reject("split ZIP directory", () => { var bytes = zip.ToArray(); bytes[^18] = 1; return bytes; });
        Reject("ZIP trailing unclaimed bytes", () => zip.Concat(new byte[] { 7 }).ToArray());
        return failures;
    }

    private static List<byte[]> Parts(string header = Header, string configuration = Configuration, string? deviceInfo = null, string functions = Functions)
    {
        var parts = new List<byte[]> { Pack(Encoding.UTF8.GetBytes(header)), Pack(Encoding.UTF8.GetBytes(configuration)), new byte[] { 255, 0, 1, 2 }, Array.Empty<byte>() };
        if (deviceInfo is not null) parts.Add(Encoding.ASCII.GetBytes("##00").Concat(Pack(Encoding.UTF8.GetBytes(deviceInfo))).ToArray());
        parts.Add(Encoding.ASCII.GetBytes("##01").Concat(Pack(Encoding.UTF8.GetBytes(functions))).ToArray());
        parts.Add(Encoding.UTF8.GetBytes("##FF0004|ksp\0")); return parts;
    }
    private static byte[] Frame(IEnumerable<byte[]> parts)
    {
        using var output = new MemoryStream(); var prefix = new byte[4];
        foreach (var part in parts) { BinaryPrimitives.WriteInt32LittleEndian(prefix, part.Length); output.Write(prefix); output.Write(part); }
        return output.ToArray();
    }
    private static byte[] Pack(byte[] data)
    {
        using var output = new MemoryStream(); var prefix = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)data.Length); output.Write(prefix);
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, true)) zlib.Write(data);
        return output.ToArray();
    }
    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (name, data) in entries) { using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open(); stream.Write(data); }
        return output.ToArray();
    }
    private sealed class ChunkStream(byte[] bytes) : Stream
    {
        private int _position;
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { var size = Math.Min(Math.Min(count, 7), bytes.Length - _position); bytes.AsSpan(_position, size).CopyTo(buffer.AsSpan(offset)); _position += size; return size; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
