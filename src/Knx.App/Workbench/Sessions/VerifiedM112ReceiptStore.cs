using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Xml.Linq;
using Knx.Bus;
using Knx.Workbench;

namespace Knx.App.Workbench;

public sealed record VerifiedM112Receipt(string DeviceId, string Address, string ConfigurationSignature, string PlanFingerprint,
    string BaselineFingerprint, string BatchFingerprint, ManagementDeviceIdentity Identity, string RecoveryPath,
    string RecoveryFingerprint, string InterfaceId, DateTimeOffset VerifiedAt);

/// <summary>Local historical verification receipts, never device-health, execution or transport authority.</summary>
public sealed class VerifiedM112ReceiptStore
{
    private const int MaximumBytes = 65536;
    private const string Kind = "BusChord.VerifiedM112History.1";
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 12, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record Payload(string Kind, ProgrammingStateBinding Binding, string SourceFileHash, string ModelHash, string JournalFingerprint, VerifiedM112Receipt? Receipt);
    private sealed record Envelope(string Sha256, JsonElement Payload);
    private readonly string _directory, _sourceHash, _modelHash;
    private readonly ProgrammingStateBinding _binding;
    private readonly Func<string, string, CancellationToken, Task<M112RecoveryInspection>> _inspect;
    public VerifiedM112ReceiptStore(string directory, ProgrammingStateBinding binding, string sourceFileHash, string modelHash,
        Func<string, string, CancellationToken, Task<M112RecoveryInspection>>? inspect = null)
    {
        _directory = PrivateDirectory(directory); _binding = binding; _sourceHash = sourceFileHash; _modelHash = modelHash;
        if (!Hash(sourceFileHash) || !Hash(modelHash)) throw new IOException("SourceChanged");
        M112PrivatePaths.ExistingFile(binding.ProjectPath);
        _inspect = inspect ?? ((_, path, ct) => M112ApplicationDownload.InspectRecoveryAsync(path, ct));
    }
    public async Task InvalidateBeforeAttemptAsync(Project project, string deviceId, CancellationToken ct = default)
    {
        await RequireSourceAsync(project, ct);
        Write(deviceId, new(Kind, _binding, _sourceHash, _modelHash, "", null));
    }
    public void InvalidateForRecovery(string deviceId) => Write(deviceId, new(Kind, _binding, _sourceHash, _modelHash, "", null));
    public async Task RecordVerifiedAsync(Project project, DeviceInstance device, VerifiedM112Receipt receipt, M112RecoveryInspection inspection,
        CancellationToken ct = default)
    {
        await RequireSourceAsync(project, ct);
        if (!Valid(receipt) || receipt.DeviceId != device.Id || receipt.Address != device.Address.ToString()
            || receipt.ConfigurationSignature != DeviceConfigurationSnapshot.Freeze(project, device).Signature
            || inspection.Identity != receipt.Identity || inspection.FileSha256 != receipt.RecoveryFingerprint
            || inspection.RecoveryPath != receipt.RecoveryPath || await M112PrivatePaths.HashAsync(receipt.RecoveryPath, ct) != receipt.RecoveryFingerprint)
            throw new IOException("SourceChanged");
        await RequireSourceAsync(project, ct);
        var journalFingerprint = await JournalFingerprintAsync(receipt.RecoveryPath, ct);
        Write(device.Id, new(Kind, _binding, _sourceHash, _modelHash, journalFingerprint, receipt));
    }
    public async Task<IReadOnlyList<DeviceProgrammingState>> ReadHistoryAsync(Project project, CancellationToken ct = default)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct); bounded.CancelAfter(TimeSpan.FromSeconds(20));
        var result = new List<DeviceProgrammingState>();
        try
        {
            await RequireSourceAsync(project, bounded.Token);
            foreach (var device in ProjectQuery.DeviceList(project).Take(10001))
            {
                bounded.Token.ThrowIfCancellationRequested();
                if (result.Count >= 10000) break;
                try
                {
                    var path = PathFor(device.Id); if (!File.Exists(path)) continue;
                    M112PrivatePaths.ExistingFile(path);
                    await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (input.Length is < 1 or > MaximumBytes) continue;
                    var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, bounded.Token);
                    if (input.ReadByte() != -1) continue;
                    using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 }); Unique(document.RootElement);
                    var envelope = document.RootElement.Deserialize<Envelope>(Json);
                    if (envelope is null || !Hash(envelope.Sha256) || !envelope.Sha256.Equals(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(envelope.Payload))), StringComparison.Ordinal)) continue;
                    var stored = envelope.Payload.Deserialize<Payload>(Json);
                    if (stored is not { Receipt: { } receipt } || stored.Kind != Kind || stored.Binding != _binding
                        || stored.SourceFileHash != _sourceHash || stored.ModelHash != _modelHash || !Valid(receipt)
                        || receipt.DeviceId != device.Id || receipt.Address != device.Address.ToString()
                        || receipt.ConfigurationSignature != DeviceConfigurationSnapshot.Freeze(project, device).Signature) continue;
                    var inspection = await _inspect(device.Id, receipt.RecoveryPath, bounded.Token);
                    if (inspection.FileSha256 != receipt.RecoveryFingerprint || inspection.Identity != receipt.Identity || !Hash(stored.JournalFingerprint)
                        || await JournalFingerprintAsync(receipt.RecoveryPath, bounded.Token) != stored.JournalFingerprint) continue;
                    result.Add(DeviceProgrammingState.Empty(device.Id).ApplySuccess(DeviceLoadFlags.ApplicationProgram | DeviceLoadFlags.Parameters | DeviceLoadFlags.GroupAddresses,
                        receipt.ConfigurationSignature, receipt.VerifiedAt, receipt.InterfaceId, ProgrammingOrigin.Real));
                }
                catch (OperationCanceledException) { throw; }
                catch { /* No historical facts are restored from malformed, missing or mismatched sources. */ }
            }
            await RequireSourceAsync(project, bounded.Token); return result.AsReadOnly();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Array.Empty<DeviceProgrammingState>(); }
    }
    public string PathFor(string deviceId) => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { _binding, deviceId }))) + ".json");
    private static async Task<string> JournalFingerprintAsync(string beforeImage, CancellationToken ct)
    {
        // Core owns a unique operation directory. Any later recovery creates a new journal there before its writes.
        // Bind its bounded top-level contents without guessing filenames, timestamps, success flags or a live device state.
        var directory = M112PrivatePaths.ExistingDirectory(Path.GetDirectoryName(M112PrivatePaths.ExistingFile(beforeImage))!);
        var entries = Directory.EnumerateFileSystemEntries(directory).Take(33).Order(StringComparer.Ordinal).ToArray();
        if (entries.Length > 32) throw new IOException("JournalUnavailable");
        long total = 0; var hashes = new List<object>();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(entry)) { M112PrivatePaths.ExistingDirectory(entry); hashes.Add(new { name = Path.GetFileName(entry), directory = true }); continue; }
            M112PrivatePaths.ExistingFile(entry); var length = new FileInfo(entry).Length;
            if (length < 0 || (total += length) > 16 * 1024 * 1024) throw new IOException("JournalUnavailable");
            string hash;
            if (length == 0) hash = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));
            else hash = await M112PrivatePaths.HashAsync(entry, ct);
            hashes.Add(new { name = Path.GetFileName(entry), directory = false, length, hash });
        }
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(hashes)));
    }
    private async Task RequireSourceAsync(Project project, CancellationToken ct)
    {
        if (project.Id != _binding.ProjectId || project.Guid != _binding.ProjectGuid || ModelFingerprint(project) != _modelHash
            || await M112PrivatePaths.HashAsync(_binding.ProjectPath, ct) != _sourceHash) throw new IOException("SourceChanged");
    }
    // Include every archived XML input consumed by the admitted M112 compiler, not only the serialized project.
    // Public archive readers use the immutable loaded source (including authenticated/generated overlays), never
    // the current path or installed data. Normalize native formatting/null representation without value heuristics.
    public static string ModelFingerprint(Project project)
    {
        long total = 0;
        string? DocumentHash(XDocument? document)
        {
            if (document is null) return null;
            if (document.DocumentType is not null) throw new IOException("SourceChanged");
            var canonical = XDocument.Parse(document.ToString(SaveOptions.DisableFormatting), LoadOptions.None).ToString(SaveOptions.DisableFormatting);
            if ((total += Encoding.UTF8.GetByteCount(canonical)) > 128 * 1024 * 1024) throw new IOException("SourceChanged");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
        var devices = ProjectQuery.DeviceList(project).Take(10001).ToArray();
        if (devices.Length > 10000) throw new IOException("SourceChanged");
        var references = devices.Select(device => device.Hardware2ProgramRefId ?? device.ProductRefId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (references.Length > 4096) throw new IOException("SourceChanged");
        var fingerprint = new
        {
            profile = "BusChord.M112ProjectAndArchiveSources.2",
            project = DocumentHash(Knxproj.ToXml(project)),
            master = DocumentHash(Knxproj.LoadMasterData(project)),
            applications = references.Select(reference => new { reference, document = DocumentHash(Knxproj.LoadApplicationProgram(project, reference)) }).ToArray(),
            hardware = references.Select(reference => reference.Length >= 6 ? reference[..6] : reference).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(reference => new { reference, document = DocumentHash(Knxproj.LoadHardwareData(project, reference)) }).ToArray(),
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fingerprint)));
    }
    private void Write(string id, Payload payload)
    {
        PrivateDirectory(_directory); Directory.CreateDirectory(_directory); M112PrivatePaths.ExistingDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var element = JsonSerializer.SerializeToElement(payload); var data = JsonSerializer.SerializeToUtf8Bytes(element);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(Convert.ToHexString(SHA256.HashData(data)), element));
        if (bytes.Length > MaximumBytes) throw new IOException("JournalUnavailable");
        var path = PathFor(id); if (File.Exists(path)) M112PrivatePaths.ExistingFile(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 4096, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool Valid(VerifiedM112Receipt receipt) => !string.IsNullOrWhiteSpace(receipt.DeviceId) && receipt.DeviceId.Length <= 4096
        && IndividualAddress.TryParse(receipt.Address, out var target) && target.Device is > 0 and < 255 && target.ToString() == receipt.Address
        && Hash(receipt.ConfigurationSignature) && Hash(receipt.PlanFingerprint) && Hash(receipt.BaselineFingerprint) && Hash(receipt.BatchFingerprint)
        && Hash(receipt.RecoveryFingerprint) && receipt.Identity is { MaskVersion: 0x0701 } identity && identity.Address == receipt.Address
        && identity.Manufacturer is { Length: 4 } && identity.Serial is { Length: 12 } && identity.Application is { Length: 10 }
        && identity.Manufacturer.All(char.IsAsciiHexDigit) && identity.Serial.All(char.IsAsciiHexDigit) && identity.Application.All(char.IsAsciiHexDigit)
        && receipt.RecoveryPath is { Length: > 0 and <= 4096 } && receipt.InterfaceId is { Length: > 0 and <= 4096 };
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static string PrivateDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("PrivatePath"); path = Path.GetFullPath(path);
        for (var item = path; item is not null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0
                || File.Exists(Path.Combine(item, ".git")) || Directory.Exists(Path.Combine(item, ".git"))) throw new IOException("PrivatePath");
        return path;
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 4096) throw new IOException("JournalUnavailable");
        if (value.ValueKind == JsonValueKind.Object)
        { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new IOException("JournalUnavailable"); Unique(property.Value); } }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Unique(child);
    }
}
