using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Knx;

public enum ParameterResourceStatus { Available, Missing, Invalid, Unsupported, TooLarge }
public sealed record ParameterImageContent(ParameterResourceStatus Status, byte[]? Bytes = null, int Width = 0, int Height = 0);
public sealed record ParameterHelpContent(ParameterResourceStatus Status, string? Text = null, string? Language = null);

/// <summary>Reads declared manufacturer resources from the immutable project archive only.</summary>
public sealed class ParameterResourceReader
{
    public const int MaximumImageBytes = 4 * 1024 * 1024;
    public const int MaximumHelpArchiveBytes = 8 * 1024 * 1024;
    public const int MaximumHelpTextBytes = 128 * 1024;
    private readonly ArchiveState? _archive;
    private readonly string? _manufacturer, _helpId, _language;
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParameterImageContent> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParameterHelpContent> _help = new(StringComparer.Ordinal);

    public ParameterResourceReader(Project project, XDocument application)
    {
        _archive = project.ArchiveState;
        var programs = application.Descendants().Where(e => e.Name.LocalName == "ApplicationProgram").ToArray();
        if (programs.Length != 1 || (string?)programs[0].Attribute("Id") is not { Length: <= 256 } id
            || !Regex.IsMatch(id, @"^M-[0-9a-fA-F]{4}_A-[A-Za-z0-9_.-]+$")) return;
        _manufacturer = id[..6]; _helpId = (string?)programs[0].Attribute("ContextHelpFile");
        _language = (string?)programs[0].Attribute("DefaultLanguage") is { Length: <= 64 } language ? language : null;
        try
        {
            var baggage = _archive is null ? null : ArchiveState.LoadPart(_archive, _manufacturer + "/Baggages.xml");
            if (baggage is null) return;
            if (baggage.Descendants().Count(e => e.Name.LocalName == "Baggage") > 4096) return;
            foreach (var group in baggage.Descendants().Where(e => e.Name.LocalName == "Baggage")
                .Where(e => (string?)e.Attribute("Id") is { Length: > 0 }).GroupBy(e => (string)e.Attribute("Id")!, StringComparer.Ordinal))
            {
                if (group.Count() != 1 || group.Key.Length > 256 || !group.Key.StartsWith(_manufacturer + "_", StringComparison.Ordinal)) continue;
                var declaration = group.Single(); var name = (string?)declaration.Attribute("Name");
                var target = (string?)declaration.Attribute("TargetPath") ?? "";
                if (name is null || name.Contains('/') || !SafeRelative(name) || target.Length > 0 && !SafeRelative(target)) continue;
                _paths[group.Key] = _manufacturer + "/Baggages/" + (target.Length > 0 ? target + "/" : "") + name;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or NotSupportedException) { }
    }

    public ParameterImageContent ReadImage(string? resourceId)
    {
        if (resourceId is null) return new(ParameterResourceStatus.Missing);
        if (_images.TryGetValue(resourceId, out var cached)) return cached with { Bytes = cached.Bytes?.ToArray() };
        var content = Image(resourceId);
        if (_images.Count < 64 && _images.Values.Sum(i => (long)(i.Bytes?.Length ?? 0)) + (content.Bytes?.Length ?? 0) <= 16L * 1024 * 1024)
            _images[resourceId] = content;
        return content with { Bytes = content.Bytes?.ToArray() };
    }

    private ParameterImageContent Image(string id)
    {
        if (!_paths.TryGetValue(id, out var path)) return new(ParameterResourceStatus.Missing);
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return new(ParameterResourceStatus.Unsupported);
        try
        {
            var bytes = Read(path, MaximumImageBytes); if (bytes is null) return new(ParameterResourceStatus.Missing);
            if (!TryImageDimensions(bytes, out var width, out var height)) return new(ParameterResourceStatus.Invalid);
            if (width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > 8_000_000) return new(ParameterResourceStatus.TooLarge);
            return new(ParameterResourceStatus.Available, bytes, width, height);
        }
        catch (ResourceLimitException) { return new(ParameterResourceStatus.TooLarge); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { return new(ParameterResourceStatus.Invalid); }
    }

    public ParameterHelpContent ReadHelp(string? context)
    {
        if (string.IsNullOrWhiteSpace(context)) return new(ParameterResourceStatus.Missing, Language: _language);
        if (_help.TryGetValue(context, out var cached)) return cached;
        var content = Help(context);
        if (_help.Count < 256) _help[context] = content;
        return content;
    }

    private ParameterHelpContent Help(string context)
    {
        if (!SafeRelative(context) || context.Contains('/') || _helpId is null || !_paths.TryGetValue(_helpId, out var path))
            return new(ParameterResourceStatus.Missing, Language: _language);
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return new(ParameterResourceStatus.Unsupported, Language: _language);
        try
        {
            var bytes = Read(path, MaximumHelpArchiveBytes); if (bytes is null) return new(ParameterResourceStatus.Missing, Language: _language);
            using var input = new MemoryStream(bytes, false); using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            if (zip.Entries.Count > 1024) throw new ResourceLimitException();
            long expanded = 0;
            foreach (var entry in zip.Entries)
            { if (entry.Length > 32L * 1024 * 1024 - expanded) throw new ResourceLimitException(); expanded += entry.Length; }
            var entries = zip.Entries.Where(e => e.FullName == context + ".txt").ToArray();
            if (entries.Length != 1) return new(ParameterResourceStatus.Missing, Language: _language);
            var data = ReadEntry(entries[0], MaximumHelpTextBytes);
            var text = new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF');
            if (text.IndexOf('\0') >= 0) return new(ParameterResourceStatus.Invalid, Language: _language);
            // Plain text is displayed literally; no browser, markup renderer or external resource is used.
            return new(ParameterResourceStatus.Available, text, _language);
        }
        catch (ResourceLimitException) { return new(ParameterResourceStatus.TooLarge, Language: _language); }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException or NotSupportedException)
        { return new(ParameterResourceStatus.Invalid, Language: _language); }
    }

    private byte[]? Read(string path, int limit)
    {
        if (_archive is null || _manufacturer is null || !path.StartsWith(_manufacturer + "/Baggages/", StringComparison.Ordinal) || !SafeRelative(path)) return null;
        if (_archive.GeneratedParts?.TryGetValue(path, out var generated) == true) return Copy(generated, limit);
        if (_archive.ApplicationParts is not null)
            return _archive.ApplicationParts.TryGetValue(path, out var authenticated) ? Copy(authenticated, limit) : null;
        using var stream = new MemoryStream(_archive.Bytes, false); using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var matches = zip.Entries.Where(e => e.FullName == path).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Ambiguous manufacturer resource.");
        return matches.Length == 1 ? ReadEntry(matches[0], limit) : null;
    }

    private static byte[] Copy(byte[] bytes, int limit) => bytes.Length <= limit ? bytes.ToArray() : throw new ResourceLimitException();
    private static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
    {
        if (entry.Length > limit) throw new ResourceLimitException();
        using var input = entry.Open(); using var output = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        { if (output.Length + read > limit) throw new ResourceLimitException(); output.Write(buffer, 0, read); }
        return output.ToArray();
    }

    private static bool SafeRelative(string path) => path.Length is > 0 and <= 1024
        && path.Split('/').All(p => p is not ("" or "." or "..") && !p.Any(c => char.IsControl(c) || c is '\\' or ':' or '?' or '#'));
    private sealed class ResourceLimitException : IOException { }

    public static bool TryImageDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        { width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4)); height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4)); return true; }
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return false;
        var index = 2;
        while (index + 4 <= bytes.Length)
        {
            if (bytes[index++] != 0xff) return false;
            while (index < bytes.Length && bytes[index] == 0xff) index++;
            if (index >= bytes.Length) return false;
            var marker = bytes[index++]; if (marker is 0xd9 or 0xda) return false;
            if (marker is 0x01 or >= 0xd0 and <= 0xd8) continue;
            if (index + 2 > bytes.Length) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(index, 2));
            if (length < 2 || index + length > bytes.Length) return false;
            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
            {
                if (length < 8) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(index + 3, 2)); width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(index + 5, 2)); return true;
            }
            index += length;
        }
        return false;
    }
}
