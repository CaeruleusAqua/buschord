using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Knx;

namespace Knx.Bus;

public sealed record M112DownloadResult(bool DownloadedAndVerified, bool RollbackAttempted,
    bool OriginalConfigurationRestoredAndVerified, string RecoveryPath, string? Error)
{
    public string RecoveryScope => "CapturedConfigurationWithSourceLoadRecipe";
}

/// <summary>Explicit M112 reload or reviewed configuration change, with a durable before-image and independent rollback.</summary>
public static class M112ApplicationDownload
{
    public const int MemoryTransferBlockLength = 12;
    private sealed class VerificationMismatch(string message) : IOException(message);
    private sealed record SavedSegment(byte ObjectIndex, byte SegmentType, ushort Address, ushort Size,
        byte Access, byte MemoryType, byte Flags, byte[] Before, byte[] Desired);
    private sealed record Snapshot(int Version, string ImageFingerprint, ManagementDeviceIdentity Identity,
        byte PeiType, byte[] Hardware, byte Status, byte DeviceControl, SavedSegment[] Segments);
    private sealed record Envelope(string Sha256, Snapshot Snapshot);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<M112DownloadResult> RunAsync(Func<CancellationToken, Task<IManagementConnection>> openConnection,
        Project project, DeviceInstance device, M112ApplicationImage image, ManagementDeviceIdentity expected,
        string confirmedTarget, string journalDirectory, Action<string>? progress = null, CancellationToken ct = default)
        => await RunCoreAsync(openConnection, image, image, () => image.IsCurrent(project, device), () => image.AreInputsCurrent(project, device), null,
            expected, confirmedTarget, journalDirectory, progress, ct);

    /// <summary>Applies a reviewed changed configuration only after its separately bound baseline matches the device.</summary>
    public static async Task<M112DownloadResult> ApplyChangesAsync(Func<CancellationToken, Task<IManagementConnection>> openConnection,
        M112ApplicationChangePlan plan, string confirmedPlanFingerprint, ManagementDeviceIdentity expected,
        string confirmedTarget, string journalDirectory, Action<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (confirmedPlanFingerprint != plan.Fingerprint) throw new InvalidOperationException("The reviewed configuration change does not match its confirmation.");
        return await RunCoreAsync(openConnection, plan.Baseline, plan.Desired, plan.IsCurrent, plan.AreInputsCurrent, plan.Fingerprint,
            expected, confirmedTarget, journalDirectory, progress, ct);
    }

    private static async Task<M112DownloadResult> RunCoreAsync(Func<CancellationToken, Task<IManagementConnection>> openConnection,
        M112ApplicationImage baseline, M112ApplicationImage image, Func<bool> isCurrent, Func<bool> areInputsCurrent, string? changeFingerprint,
        ManagementDeviceIdentity expected, string confirmedTarget, string journalDirectory, Action<string>? progress, CancellationToken ct)
    {
        ValidateTarget(expected, confirmedTarget);
        if (image.Address != confirmedTarget || baseline.Address != confirmedTarget || baseline.LayoutFingerprint != image.LayoutFingerprint || !isCurrent()
            || expected.Application != ApplicationId(image)) throw new InvalidOperationException("Image or application identity is stale or mismatched.");
        var directory = NewJournalDirectory(journalDirectory);
        using var lease = Lease(journalDirectory, confirmedTarget);
        using var journal = new Journal(Path.Combine(directory, "events.jsonl"), progress);
        var recoveryPath = Path.Combine(directory, "before-image.json");
        Snapshot? saved = null; IManagementConnection? connection = null;
        var pending = false; var success = false; var attempted = false; var restored = false; string? error = null;
        try
        {
            journal.Record("preflight", new { image.Fingerprint, baseline = baseline.Fingerprint, changeFingerprint, expected });
            connection = await openConnection(ct);
            var client = connection.Client;
            await ValidateBeforeAsync(client, expected, image.HardwareType.ToArray(), ct);
            var status = (await client.ReadMemoryAsync(0x60, 1, ct)).Single();
            if ((status & 1) != 0 || ProgrammingModeCycle.StatusWithMode(status, false) != status)
                throw new IOException("Device is programming or its status parity is invalid.");
            var deviceControl = (await client.ReadPropertyAsync(0, 14, ct: ct)).Single();
            await ValidateTaskMetadataAsync(client, image.PeiType, image.Segments.Single(s => s.ObjectIndex == 1).Address,
                image.Segments.Single(s => s.ObjectIndex == 2).Address, ct);
            var segments = new List<SavedSegment>();
            foreach (var segment in image.Segments)
            {
                var before = await ReadRangeAsync(client, segment.Address, segment.Size, ct);
                var desired = segment.Data.ToArray(); var mask = segment.Validity.ToArray();
                var baselineSegment = baseline.Segments.Single(s => s.ObjectIndex == segment.ObjectIndex && s.Address == segment.Address);
                if (segment.MemoryType == 3 && Enumerable.Range(0, desired.Length).Any(i => ((baselineSegment.Data[i] ^ before[i]) & baselineSegment.Validity[i]) != 0))
                    throw new IOException("The device configuration differs from the reviewed baseline; no unload is allowed.");
                for (var i = 0; i < desired.Length; i++) desired[i] = (byte)((desired[i] & mask[i]) | (before[i] & ~mask[i]));
                if (segment.ObjectIndex == 1)
                {
                    var address = IndividualAddress.Parse(confirmedTarget).Raw;
                    if (before.Length < 3 || before[1] != address >> 8 || before[2] != (address & 255))
                        throw new IOException("The existing address-table identity is not the confirmed target.");
                    desired[1] = before[1]; desired[2] = before[2];
                }
                segments.Add(new(segment.ObjectIndex, segment.SegmentType, segment.Address, segment.Size,
                    segment.Access, segment.MemoryType, segment.Flags, before, desired));
            }
            if (changeFingerprint is not null)
                M112CommunicationMemory.Validate(baseline, image, segments.Select(s => (s.Address, s.Before, s.Desired)).ToArray());
            saved = new(1, image.Fingerprint, expected, image.PeiType, image.HardwareType.ToArray(), status, deviceControl, segments.ToArray());
            ValidateSnapshot(saved);
            SaveSnapshot(recoveryPath, saved);
            journal.Record("baseline_captured");
            // The captured persistent before-image must still be current immediately before destructive controls.
            // Full source evaluation may exceed a device's management idle timeout. Reconnect after it, never keep an idle session across it.
            await connection.DisposeAsync(); connection = null;
            journal.Record("source_revalidation");
            if (!isCurrent()) throw new IOException("The baseline or desired project changed during device preflight.");
            journal.Record("device_revalidation");
            connection = await openConnection(ct); client = connection.Client;
            await ValidateBeforeAsync(client, expected, saved.Hardware, ct);
            await ValidateTaskMetadataAsync(client, saved.PeiType, saved.Segments.Single(s => s.ObjectIndex == 1).Address,
                saved.Segments.Single(s => s.ObjectIndex == 2).Address, ct);
            if ((await client.ReadMemoryAsync(0x60, 1, ct)) is not [var currentStatus] || currentStatus != saved.Status
                || (await client.ReadPropertyAsync(0, 14, ct: ct)) is not [var currentControl] || currentControl != saved.DeviceControl)
                throw new VerificationMismatch("Device status or control changed during preflight.");
            await VerifyAsync(client, saved, original: true, ct);
            if (!areInputsCurrent()) throw new VerificationMismatch("The reviewed project inputs changed during final device verification.");
            ct.ThrowIfCancellationRequested();
            journal.Record("restore_required", new { recoveryPath, checksum = Hash(saved) });
            pending = true;
            await LoadAsync(client, saved, original: false, journal, ct);
            await client.RestartAsync(ct);
            journal.Record("restart_request_sent");
            await connection.DisposeAsync(); connection = null;
            await VerifyRestartAsync(openConnection, saved, original: false, ct);
            journal.Record("download_verified"); success = true; pending = false;
        }
        catch (Exception ex) { error = ex.Message; journal.TryRecord("download_failed", new { error }); }
        finally
        {
            if (connection is not null)
                try { await connection.DisposeAsync(); } catch (Exception ex) { error = Join(error, ex.Message); }
            if (pending && saved is not null)
            {
                attempted = true;
                // A caller cancellation cannot cancel restoration after an uncertain write or unload.
                using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                try { await RestoreAsync(openConnection, saved, journal, recovery.Token); restored = true; }
                catch (Exception ex) { error = Join(error, "Recovery requires attention: " + ex.Message); journal.TryRecord("recovery_failed", new { error }); }
            }
        }
        return new(success, attempted, restored, recoveryPath, error);
    }

    /// <summary>Resumes restoration from the saved before-image after process or connection failure.</summary>
    public static async Task<M112DownloadResult> RecoverAsync(Func<CancellationToken, Task<IManagementConnection>> openConnection,
        string recoveryPath, ManagementDeviceIdentity expected, string confirmedTarget, Action<string>? progress = null,
        CancellationToken ct = default)
    {
        ValidateTarget(expected, confirmedTarget);
        var (saved, _) = await ReadRecoveryFileAsync(recoveryPath, ct);
        if (saved.Identity != expected) throw new InvalidDataException("Recovery identity mismatch.");
        return await RecoverValidatedAsync(openConnection, Path.GetFullPath(recoveryPath), saved, confirmedTarget, progress, ct);
    }

    public static async Task<M112RecoveryInspection> InspectRecoveryAsync(string recoveryPath, CancellationToken ct = default)
    {
        var (saved, fileHash) = await ReadRecoveryFileAsync(recoveryPath, ct);
        return new(Path.GetFullPath(recoveryPath), fileHash, saved.Identity, saved.ImageFingerprint,
            Convert.ToHexString(SHA256.HashData(saved.Hardware)), Array.AsReadOnly(saved.Segments.Select(segment => new M112RecoverySegmentSummary(
                segment.ObjectIndex, segment.MemoryType, segment.Address, segment.Size, Convert.ToHexString(SHA256.HashData(segment.Before)))).ToArray()));
    }

    /// <summary>Restores exactly the reviewed local file; physical device checks are repeated before loading.</summary>
    public static async Task<M112DownloadResult> RecoverInspectedAsync(Func<CancellationToken, Task<IManagementConnection>> openConnection,
        M112RecoveryInspection inspection, string confirmedTarget, Action<string>? progress = null, CancellationToken ct = default)
    {
        ValidateTarget(inspection.Identity, confirmedTarget);
        var (saved, fileHash) = await ReadRecoveryFileAsync(inspection.RecoveryPath, ct);
        if (fileHash != inspection.FileSha256 || saved.Identity != inspection.Identity || saved.ImageFingerprint != inspection.ImageFingerprint)
            throw new InvalidDataException("The inspected recovery file changed.");
        return await RecoverValidatedAsync(openConnection, inspection.RecoveryPath, saved, confirmedTarget, progress, ct);
    }

    private static async Task<(Snapshot Saved, string FileHash)> ReadRecoveryFileAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        const int maximum = 2 * 1024 * 1024;
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("An absolute recovery path is required.");
        for (var entry = Path.GetFullPath(path); entry is not null; entry = Path.GetDirectoryName(entry))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Recovery links are unsupported.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length is < 1 or > maximum) throw new InvalidDataException("Missing or oversized recovery image.");
        using var bytes = new MemoryStream(); var buffer = new byte[65536];
        while (true)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximum + 1 - bytes.Length)), ct);
            if (read == 0) break;
            bytes.Write(buffer, 0, read);
            if (bytes.Length > maximum) throw new InvalidDataException("Oversized recovery image.");
        }
        var payload = bytes.ToArray();
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(payload, new JsonSerializerOptions { MaxDepth = 32 })
                ?? throw new InvalidDataException("Invalid recovery image.");
            var saved = envelope.Snapshot;
            if (saved is null || saved.Identity is null || saved.Hardware is null || saved.ImageFingerprint is null || saved.Segments is null
                || saved.Segments.Any(segment => segment is null || segment.Before is null || segment.Desired is null))
                throw new InvalidDataException("Incomplete recovery image.");
            ValidateSnapshot(saved);
            if (Hash(saved) != envelope.Sha256) throw new InvalidDataException("Recovery checksum mismatch.");
            return (saved, Convert.ToHexString(SHA256.HashData(payload)));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or OverflowException)
        { throw new InvalidDataException("Invalid recovery image.", error); }
    }

    private static async Task<M112DownloadResult> RecoverValidatedAsync(Func<CancellationToken, Task<IManagementConnection>> openConnection,
        string recoveryPath, Snapshot saved, string confirmedTarget, Action<string>? progress, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(recoveryPath))!;
        using var lease = Lease(Path.GetDirectoryName(directory)!, confirmedTarget);
        using var journal = new Journal(Path.Combine(directory, $"recovery-{Guid.NewGuid():N}.jsonl"), progress);
        try
        {
            await RestoreAsync(openConnection, saved, journal, ct);
            return new(false, true, true, recoveryPath, null);
        }
        catch (Exception ex) { journal.TryRecord("recovery_failed", new { error = ex.Message }); return new(false, true, false, recoveryPath, ex.Message); }
    }

    private static async Task RestoreAsync(Func<CancellationToken, Task<IManagementConnection>> open, Snapshot saved, Journal journal, CancellationToken ct)
    {
        journal.TryRecord("restoring_original");
        await using (var connection = await open(ct))
        {
            var client = connection.Client;
            await ValidatePhysicalAsync(client, saved.Identity, saved.Hardware, ct);
            await LoadAsync(client, saved, original: true, journal, ct);
            await client.RestartAsync(ct);
        }
        await VerifyRestartAsync(open, saved, original: true, ct);
        journal.TryRecord("original_restored_verified");
    }

    private static async Task LoadAsync(KnxManagementClient client, Snapshot saved, bool original, Journal journal, CancellationToken ct)
    {
        // Full load follows the compiled recipe: unload all three LSMs, then allocate, populate and complete each in order.
        for (byte oi = 1; oi <= 3; oi++) await ControlAsync(client, oi, SimpleControl(oi, 4), 0, saved, journal, ct);
        for (byte oi = 1; oi <= 3; oi++)
        {
            await ControlAsync(client, oi, SimpleControl(oi, 1), 2, saved, journal, ct);
            foreach (var segment in saved.Segments.Where(s => s.ObjectIndex == oi))
                await ControlAsync(client, oi, AbsoluteControl(oi, segment), 2, saved, journal, ct);
            var task = saved.Segments.First(s => s.ObjectIndex == oi && s.MemoryType == 3);
            await ControlAsync(client, oi, TaskControl(oi, task.Address, saved.PeiType, saved.Identity.Application), 2, saved, journal, ct);
            foreach (var segment in saved.Segments.Where(s => s.ObjectIndex == oi && s.MemoryType == 3))
            {
                journal.TryRecord("writing_segment", new { oi, segment.Address, segment.Size, original });
                var bytes = original ? segment.Before : segment.Desired;
                for (var offset = 0; offset < bytes.Length; offset += MemoryTransferBlockLength)
                    await client.WriteMemoryAsync(checked((ushort)(segment.Address + offset)),
                        bytes.AsMemory(offset, Math.Min(MemoryTransferBlockLength, bytes.Length - offset)), (saved.DeviceControl & 4) != 0, ct);
                var verify = await ReadRangeAsync(client, segment.Address, segment.Size, ct);
                if (!verify.AsSpan().SequenceEqual(bytes)) throw new IOException($"Segment verification failed at 0x{segment.Address:X4}.");
            }
            await ControlAsync(client, oi, SimpleControl(oi, 2), 1, saved, journal, ct);
        }
        journal.TryRecord(original ? "original_loaded_verified" : "image_loaded_verified");
    }

    private static async Task ControlAsync(KnxManagementClient client, byte oi, byte[] bytes, byte expected,
        Snapshot saved, Journal journal, CancellationToken ct)
    {
        journal.TryRecord("load_control", new { oi, command = Convert.ToHexString(bytes), expected });
        // This register is write-only. Its completion is verified through the separate LSM status resource.
        await client.WriteMemoryAsync(0x104, bytes, serverVerify: false, ct);
        using var transition = CancellationTokenSource.CreateLinkedTokenSource(ct);
        transition.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            var state = (await client.ReadMemoryAsync((ushort)(0xB6E9 + oi), 1, transition.Token)).Single();
            if (state == expected) return;
            if (state is 3 or > 6) throw new IOException($"LSM {oi} entered error/unsupported state {state}.");
            await Task.Delay(50, transition.Token);
        }
    }

    private static async Task VerifyRestartAsync(Func<CancellationToken, Task<IManagementConnection>> open, Snapshot saved, bool original, CancellationToken ct)
    {
        Exception? last = null;
        // Only read/reconnect is retried. The restart or any uncertain write is never repeated here.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 3 : 1), ct);
            try
            {
                await using var connection = await open(ct);
                await ValidateBeforeAsync(connection.Client, saved.Identity, saved.Hardware, ct);
                await ValidateTaskMetadataAsync(connection.Client, saved.PeiType, saved.Segments.Single(s => s.ObjectIndex == 1).Address,
                    saved.Segments.Single(s => s.ObjectIndex == 2).Address, ct);
                await VerifyAsync(connection.Client, saved, original, ct);
                var status = (await connection.Client.ReadMemoryAsync(0x60, 1, ct)).Single();
                if (status != saved.Status) throw new VerificationMismatch("Final programming/device status changed.");
                if ((await connection.Client.ReadPropertyAsync(0, 14, ct: ct)) is not [var control] || control != saved.DeviceControl)
                    throw new VerificationMismatch("Device control changed after loading.");
                return;
            }
            catch (Exception ex) when (ex is not VerificationMismatch && (ex is IOException or OperationCanceledException) && !ct.IsCancellationRequested) { last = ex; }
        }
        throw new IOException("The device did not resume with a matching verified application.", last);
    }

    private static async Task VerifyAsync(KnxManagementClient client, Snapshot saved, bool original, CancellationToken ct)
    {
        foreach (var segment in saved.Segments.Where(s => s.MemoryType == 3))
            if (!(await ReadRangeAsync(client, segment.Address, segment.Size, ct)).AsSpan().SequenceEqual(original ? segment.Before : segment.Desired))
                throw new VerificationMismatch($"Persistent verification mismatch at 0x{segment.Address:X4}.");
    }
    private static async Task ValidateBeforeAsync(KnxManagementClient client, ManagementDeviceIdentity expected, byte[] hardware, CancellationToken ct)
    {
        await ValidatePhysicalAsync(client, expected, hardware, ct);
        if (await client.ReadIdentityAsync(ct) != expected) throw new VerificationMismatch("The application identity does not match.");
        for (byte oi = 1; oi <= 3; oi++)
        {
            if ((await client.ReadPropertyAsync(oi, 5, ct: ct)) is not [1]
                || (await client.ReadMemoryAsync((ushort)(0xB6E9 + oi), 1, ct)) is not [1])
                throw new IOException("The declared load-state mappings are not both loaded.");
        }
        if ((await client.ReadPropertyAsync(3, 6, ct: ct)) is not [1]) throw new IOException("Application is not running.");
    }
    private static async Task ValidatePhysicalAsync(KnxManagementClient client, ManagementDeviceIdentity expected, byte[] hardware, CancellationToken ct)
    {
        // Application identity may be absent while unloaded. Recovery authenticates only stable physical fields here.
        var descriptor = await client.ReadDescriptorAsync(ct);
        var manufacturer = await client.ReadPropertyAsync(0, 12, ct: ct);
        var serial = await client.ReadPropertyAsync(0, 11, ct: ct);
        if (client.Target.ToString() != expected.Address || descriptor.Length != 2 || BinaryPrimitives.ReadUInt16BigEndian(descriptor) != expected.MaskVersion
            || Convert.ToHexString(manufacturer) != expected.Manufacturer || Convert.ToHexString(serial) != expected.Serial)
            throw new VerificationMismatch("Physical device identity changed; loading refused.");
        if (!(await client.ReadPropertyAsync(0, 78, ct: ct)).AsSpan().SequenceEqual(hardware)) throw new VerificationMismatch("Hardware type mismatch.");
    }
    private static async Task ValidateTaskMetadataAsync(KnxManagementClient client, byte pei, ushort addressTable, ushort associations, CancellationToken ct)
    {
        if ((await client.ReadPropertyAsync(3, 16, ct: ct)) is not [var requiredPei] || requiredPei != pei)
            throw new VerificationMismatch("The programmed PEI type differs from the source load recipe.");
        foreach (var (oi, address) in new[] { ((byte)1, addressTable), ((byte)2, associations) })
        {
            var pointer = await client.ReadPropertyAsync(oi, 7, ct: ct);
            if (pointer.Length != 2 || BinaryPrimitives.ReadUInt16BigEndian(pointer) != address)
                throw new VerificationMismatch("The existing table mapping differs from the source load recipe.");
        }
    }
    private static async Task<byte[]> ReadRangeAsync(KnxManagementClient client, ushort address, ushort size, CancellationToken ct)
    {
        var bytes = new byte[size];
        for (var offset = 0; offset < size; offset += MemoryTransferBlockLength)
            (await client.ReadMemoryAsync(checked((ushort)(address + offset)), (byte)Math.Min(MemoryTransferBlockLength, size - offset), ct)).CopyTo(bytes, offset);
        return bytes;
    }

    public static byte[] SimpleControl(byte oi, byte command)
    {
        if (oi is < 1 or > 3 || command is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(command));
        var result = new byte[11]; result[0] = (byte)((oi << 4) | command); return result;
    }
    private static byte[] AbsoluteControl(byte oi, SavedSegment segment)
    {
        var bytes = new byte[11]; bytes[0] = (byte)((oi << 4) | 3); bytes[1] = segment.SegmentType;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(3), segment.Address);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(5), segment.Size);
        bytes[7] = segment.Access; bytes[8] = segment.MemoryType; bytes[9] = segment.Flags; return bytes;
    }
    private static byte[] TaskControl(byte oi, ushort address, byte pei, string application)
    {
        var bytes = new byte[11]; bytes[0] = (byte)((oi << 4) | 3); bytes[1] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(3), address); bytes[5] = pei;
        Convert.FromHexString(application).CopyTo(bytes, 6); return bytes;
    }
    private static string ApplicationId(M112ApplicationImage image) => $"{image.Manufacturer:X4}{image.ApplicationNumber:X4}{image.ApplicationVersion:X2}";
    private static void ValidateTarget(ManagementDeviceIdentity expected, string target)
    {
        if (expected is null || expected.Address is null || expected.Manufacturer is null || expected.Serial is null || expected.Application is null
            || expected.Address != target || IndividualAddress.Parse(target).ToString() != target || expected.MaskVersion != 0x0701
            || expected.Manufacturer.Length != 4 || expected.Serial.Length != 12 || expected.Application.Length != 10
            || !expected.Serial.StartsWith(expected.Manufacturer, StringComparison.Ordinal)
            || !expected.Application.StartsWith(expected.Manufacturer, StringComparison.Ordinal))
            throw new ArgumentException("A confirmed matching M112 physical/application identity is required.");
    }
    private static void ValidateSnapshot(Snapshot saved)
    {
        ValidateTarget(saved.Identity, saved.Identity.Address);
        if (saved.Version != 1 || saved.Hardware.Length != 6 || saved.ImageFingerprint.Length != 64
            || saved.Segments.Length is < 3 or > 16 || saved.Segments.Sum(s => (int)s.Size) > 32768
            || (saved.Status & 1) != 0 || ProgrammingModeCycle.StatusWithMode(saved.Status, false) != saved.Status)
            throw new InvalidDataException("Unsupported recovery profile.");
        foreach (var segment in saved.Segments)
        {
            if (segment.ObjectIndex is < 1 or > 3 || segment.Address < 0x100 || segment.Size == 0 || segment.Address + segment.Size > 65536
                || segment.SegmentType > 1 || segment.MemoryType is not (2 or 3) || segment.Before.Length != segment.Size || segment.Desired.Length != segment.Size
                || segment.MemoryType == 3 && (segment.SegmentType != 0 || segment.Address < 0x4000 || segment.Address + segment.Size > 0x6000 || segment.Access != 255 || segment.Flags != 128)
                || segment.MemoryType == 2 && (segment.ObjectIndex != 3 || segment.Address < 0x700 || segment.Address + segment.Size > 0x800 || segment.Access != 0 || segment.Flags != 0)
                || saved.Segments.Any(s => !ReferenceEquals(s, segment) && s.Address < segment.Address + segment.Size && segment.Address < s.Address + s.Size))
                throw new InvalidDataException("Invalid recovery segment.");
        }
        for (byte oi = 1; oi <= 3; oi++)
            if (saved.Segments.Count(s => s.ObjectIndex == oi && s.MemoryType == 3) != 1) throw new InvalidDataException("Missing persistent task segment.");
        if (saved.Segments.Single(s => s.ObjectIndex == 1).Address != 0x4000 || saved.Segments.Single(s => s.ObjectIndex == 1).Size is < 3 or > 513
            || saved.Segments.Single(s => s.ObjectIndex == 2).Address != 0x4201 || saved.Segments.Single(s => s.ObjectIndex == 2).Size > 511
            || saved.Segments.Single(s => s.ObjectIndex == 3 && s.MemoryType == 3).Address != 0x4400)
            throw new InvalidDataException("Recovery uses unsupported table/task placement.");
        var ownAddress = IndividualAddress.Parse(saved.Identity.Address).Raw;
        var addressTable = saved.Segments.Single(s => s.ObjectIndex == 1);
        if (new[] { addressTable.Before, addressTable.Desired }.Any(data => data[1] != ownAddress >> 8 || data[2] != (ownAddress & 255)))
            throw new InvalidDataException("Recovery cannot change the confirmed individual address.");
    }
    private static string NewJournalDirectory(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("An absolute private journal directory is required.");
        var directory = Path.Combine(root, $"m112-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory); return directory;
    }
    private static FileStream Lease(string directory, string target) => new(Path.Combine(directory, "device-" + target + ".lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    private static string Hash(Snapshot saved) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(saved)));
    private static void SaveSnapshot(string path, Snapshot saved)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(file, new Envelope(Hash(saved), saved), Json); file.Flush(flushToDisk: true);
    }
    private static string Join(string? first, string second) => first is null ? second : first + "; " + second;
    private sealed class Journal : IDisposable
    {
        private readonly FileStream _file;
        private readonly StreamWriter _writer;
        private readonly Action<string>? _progress;
        internal Journal(string path, Action<string>? progress)
        {
            _file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            _writer = new(_file, new UTF8Encoding(false), leaveOpen: true); _progress = progress;
        }
        internal void Record(string phase, object? details = null)
        {
            _writer.WriteLine(JsonSerializer.Serialize(new { phase, timestamp = DateTimeOffset.UtcNow, details })); _writer.Flush(); _file.Flush(true);
            try { _progress?.Invoke(phase); } catch (Exception) { /* Observers do not affect restoration. */ }
        }
        internal void TryRecord(string phase, object? details = null) { try { Record(phase, details); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        public void Dispose() { _writer.Dispose(); _file.Dispose(); }
    }
}
