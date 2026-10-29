using System.Numerics;
using System.Text;
using System.Text.Json;
using Knx;

namespace Knx.Bus;

public sealed record ManagementDeviceIdentity(string Address, ushort MaskVersion, string Manufacturer, string Serial, string Application);
public sealed record ProgrammingModeCycleResult(bool ActivatedAndVerified, bool RestoredAndVerified, string JournalPath, string? Error);

public interface IManagementConnection : IAsyncDisposable
{
    KnxManagementClient Client { get; }
}

/// <summary>A single M112 programming-mode bit cycle with a durable recovery journal and a fresh restoration connection.</summary>
public static class ProgrammingModeCycle
{
    public static byte StatusWithMode(byte status, bool enabled)
    {
        var low = (byte)((status & 0x7E) | (enabled ? 1 : 0));
        return (byte)(low | ((BitOperations.PopCount(low) & 1) << 7));
    }

    public static async Task<ProgrammingModeCycleResult> RunAsync(
        Func<CancellationToken, Task<IManagementConnection>> openConnection, ManagementDeviceIdentity expected,
        string confirmedTarget, string journalDirectory, CancellationToken ct = default)
    {
        if (IndividualAddress.Parse(confirmedTarget).ToString() != confirmedTarget)
            throw new ArgumentException("A canonical individual target address is required.");
        if (expected.Address != confirmedTarget || expected.MaskVersion != 0x0701 || expected.Manufacturer != "0083"
            || expected.Serial.Length != 12 || !expected.Serial.StartsWith(expected.Manufacturer, StringComparison.Ordinal)
            || expected.Application.Length != 10 || !expected.Application.StartsWith(expected.Manufacturer, StringComparison.Ordinal))
            throw new InvalidOperationException("An explicitly confirmed matching MDT M112 device identity is required.");
        if (!Path.IsPathFullyQualified(journalDirectory)) throw new ArgumentException("Recovery journal directory must be absolute.");
        Directory.CreateDirectory(journalDirectory);
        using var lease = new FileStream(Path.Combine(journalDirectory, "device-" + confirmedTarget + ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        var path = Path.Combine(journalDirectory, $"mode-cycle-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.jsonl");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(file, new UTF8Encoding(false), leaveOpen: true);
        void Record(object entry)
        {
            writer.WriteLine(JsonSerializer.Serialize(entry));
            writer.Flush();
            file.Flush(flushToDisk: true);
        }

        IManagementConnection? connection = null;
        byte original = 0;
        var mayHaveWritten = false;
        var activated = false;
        var restored = false;
        string? error = null;
        void RecoveryRecord(object entry)
        {
            try { Record(entry); }
            catch (Exception ex) { error = Join(error, "Recovery journal update failed: " + ex.Message); }
        }
        Record(new { phase = "preflight", timestamp = DateTimeOffset.UtcNow, expected, profile = "mdt-m112-status-mode-v1" });
        try
        {
            connection = await openConnection(ct);
            var client = connection.Client;
            if (await client.ReadIdentityAsync(ct) != expected) throw new IOException("Device identity changed; no write allowed.");
            original = (await client.ReadMemoryAsync(0x60, 1, ct)).Single();
            if ((original & 1) != 0 || StatusWithMode(original, false) != original)
                throw new IOException("Device is already programming or status parity is invalid; no write allowed.");
            var control = await client.ReadPropertyAsync(0, 14, ct: ct);
            if (control.Length != 1) throw new IOException("Unknown device-control profile; no write allowed.");
            var desired = StatusWithMode(original, true);
            if ((await client.ReadMemoryAsync(0x60, 1, ct)).Single() != original)
                throw new IOException("Status changed during review; no write allowed.");
            ct.ThrowIfCancellationRequested();
            Record(new { phase = "restore_required", timestamp = DateTimeOffset.UtcNow, original, desired, serverVerify = (control[0] & 4) != 0 });
            mayHaveWritten = true;
            await client.WriteProgrammingStatusAsync(desired, (control[0] & 4) != 0, ct);
            activated = (await client.ReadMemoryAsync(0x60, 1, ct)).Single() == desired;
            if (!activated) throw new IOException("Programming-mode readback differs from the requested status.");
            Record(new { phase = "activated_verified", timestamp = DateTimeOffset.UtcNow, desired });
        }
        catch (Exception ex) { error = ex.Message; }
        finally
        {
            if (connection is not null)
            {
                try { await connection.DisposeAsync(); }
                catch (Exception ex) { error ??= "Closing initial connection: " + ex.Message; }
            }
            if (mayHaveWritten)
            {
                // Caller cancellation must not cancel restoration after an uncertain write.
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                try
                {
                    await using var cleanup = await openConnection(recovery.Token);
                    var client = cleanup.Client;
                    if (await client.ReadIdentityAsync(recovery.Token) != expected)
                        throw new IOException("Recovery identity mismatch; restoration refused.");
                    var current = (await client.ReadMemoryAsync(0x60, 1, recovery.Token)).Single();
                    if ((current & 1) != 0)
                    {
                        var control = await client.ReadPropertyAsync(0, 14, ct: recovery.Token);
                        if (control.Length != 1) throw new IOException("Recovery device control is unknown.");
                        var desired = StatusWithMode(current, false);
                        RecoveryRecord(new { phase = "restoring", timestamp = DateTimeOffset.UtcNow, current, desired });
                        await client.WriteProgrammingStatusAsync(desired, (control[0] & 4) != 0, recovery.Token);
                    }
                    var final = (await client.ReadMemoryAsync(0x60, 1, recovery.Token)).Single();
                    restored = final == original;
                    RecoveryRecord(new { phase = restored ? "restored_verified" : "restore_mismatch", timestamp = DateTimeOffset.UtcNow, original, final });
                    if (!restored) error = Join(error, "Final device status differs from the saved original.");
                }
                catch (Exception ex)
                {
                    error = Join(error, "Recovery requires attention: " + ex.Message);
                    RecoveryRecord(new { phase = "recovery_failed", timestamp = DateTimeOffset.UtcNow, error });
                }
            }
            else Record(new { phase = "no_write", timestamp = DateTimeOffset.UtcNow, error });
        }
        return new(activated, restored, path, error);
    }

    private static string Join(string? first, string second) => first is null ? second : first + "; " + second;
}
