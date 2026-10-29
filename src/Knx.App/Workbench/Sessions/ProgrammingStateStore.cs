using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knx.Workbench;

namespace Knx.App.Workbench;

public sealed record ProgrammingStateBinding(string ProjectId, string? ProjectGuid, string ProjectPath)
{
    public static ProgrammingStateBinding? Create(Project project, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var canonical = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(project.Id) || project.Id.Length > 4096 || project.Guid?.Length > 4096 || canonical.Length > 4096)
            throw new FormatException("Invalid programming comparison project binding.");
        return new(project.Id, project.Guid, canonical);
    }
}

/// <summary>Local DEMO comparison facts, separate from project, layout, undo and real device state.</summary>
public sealed class ProgrammingStateStore(string directory)
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public const int MaximumDevices = 10_000;
    private const string Kind = "BusChord.DemoProgrammingComparison";
    private readonly string _directory = Path.GetFullPath(directory);
    internal string DirectoryPath => _directory;
    private sealed record Envelope(int Version, string Kind, string ProjectId, string? ProjectGuid, string ProjectPath, JsonElement States);

    public string PathFor(ProgrammingStateBinding binding)
    {
        var identity = JsonSerializer.Serialize(binding);
        return Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }

    public bool TryLoad(ProgrammingStateBinding binding, out string? stateJson, out string? error)
    {
        stateJson = null; error = null;
        try
        {
            var path = PathFor(binding);
            if (!File.Exists(path)) return false;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumBytes) throw new FormatException("DEMO comparison file exceeds its byte limit.");
            var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
            if (input.ReadByte() != -1) throw new FormatException("DEMO comparison file changed while reading.");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            CheckJson(document.RootElement);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 6
                || !root.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("Kind", out var kind) || kind.GetString() != Kind
                || !root.TryGetProperty("ProjectId", out var id) || id.GetString() != binding.ProjectId
                || !root.TryGetProperty("ProjectGuid", out var guid) || guid.GetString() != binding.ProjectGuid
                || !root.TryGetProperty("ProjectPath", out var pathValue) || pathValue.GetString() != binding.ProjectPath
                || !root.TryGetProperty("States", out var states))
                throw new FormatException("DEMO comparison file belongs to another project, path or format.");
            var json = states.GetRawText();
            ValidateStates(states, binding.ProjectId);
            stateJson = json;
            return true;
        }
        catch (Exception ex) when (IsFileError(ex)) { error = ex.Message; return false; }
    }

    public void Save(ProgrammingStateBinding binding, IEnumerable<DeviceProgrammingState> states)
    {
        var values = states.Take(MaximumDevices + 1).ToArray();
        if (values.Length > MaximumDevices) throw new FormatException("Too many DEMO comparison devices.");
        long metadataBytes = 0;
        foreach (var state in values)
            foreach (var text in new[] { state.DeviceId, state.ImageHash, state.LastError, state.InterfaceId })
            {
                if (text?.Length > 4096) throw new FormatException("DEMO comparison metadata exceeds its string limit.");
                metadataBytes += text is null ? 0 : Encoding.UTF8.GetByteCount(text);
                if (metadataBytes > MaximumBytes) throw new FormatException("DEMO comparison metadata exceeds its byte limit.");
            }
        var json = DeviceProgrammingStates.Serialize(binding.ProjectId, values);
        using var stateDocument = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        ValidateStates(stateDocument.RootElement, binding.ProjectId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, Kind, binding.ProjectId, binding.ProjectGuid, binding.ProjectPath, stateDocument.RootElement));
        if (bytes.Length > MaximumBytes) throw new FormatException("DEMO comparison file exceeds its byte limit.");
        var path = PathFor(binding);
        Directory.CreateDirectory(_directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateStates(JsonElement root, string projectId)
    {
        CheckJson(root);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Devices", out var devices)
            || devices.ValueKind != JsonValueKind.Array || devices.GetArrayLength() > MaximumDevices)
            throw new FormatException("Invalid or excessive DEMO comparison device list.");
        var parsed = DeviceProgrammingStates.Parse(root.GetRawText(), projectId);
        if (parsed.Values.Any(s => s.Origin == ProgrammingOrigin.Real || s.LastAttemptOrigin == ProgrammingOrigin.Real))
            throw new FormatException("This store accepts DEMO comparison facts only, never real-device state.");
    }

    private static void CheckJson(JsonElement root)
    {
        var nodes = 0;
        void Visit(JsonElement value)
        {
            if (++nodes > 200_000) throw new FormatException("DEMO comparison JSON exceeds its node limit.");
            if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 4096)
                throw new FormatException("DEMO comparison metadata exceeds its string limit.");
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                { if (!names.Add(property.Name)) throw new FormatException("Duplicate DEMO comparison JSON property."); Visit(property.Value); }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Visit(child);
        }
        Visit(root);
    }

    internal static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or FormatException
        or JsonException or ArgumentException or InvalidOperationException or NotSupportedException;
}
