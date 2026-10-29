using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Knx.Recovery;

internal sealed class RecoveryAuditJournal(string path, string sessionId)
{
    private const long MaximumBytes = 64L * 1024 * 1024;
    private const int MaximumFrame = 16384;
    private const int MaximumRecords = 100000;
    private static readonly string InitialHash = RecoveryWire.Hash([]);
    internal sealed record Scan(RecoveryAuditAnchor Last, bool TailDamaged, bool AnchorFound, long Records,
        IReadOnlyDictionary<string, string> EventFingerprints);

    internal async Task<Scan> ReadAsync(RecoveryAuditAnchor? expected, CancellationToken token)
    {
        RecoveryWire.Id(sessionId);
        var last = new RecoveryAuditAnchor(0, 0, InitialHash);
        var found = expected is null || expected == last;
        var events = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            if (!found) throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.AuditMissing");
            return new(last, false, found, 0, events);
        }
        RecoveryWire.NoLink(path);
        await using var input = RecoveryStoragePermissions.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var end = input.Length;
        if (end > MaximumBytes) throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.AuditTooLarge");
        var damaged = false;
        while (input.Position < end)
        {
            token.ThrowIfCancellationRequested();
            if (events.Count >= MaximumRecords) throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.AuditTooManyRecords");
            try
            {
                if (end - input.Position < 4) throw new InvalidDataException();
                var prefix = new byte[4]; await input.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                if (length is < 2 or > MaximumFrame || end - input.Position < length + 32L) throw new InvalidDataException();
                var payload = new byte[length]; await input.ReadExactlyAsync(payload, token).ConfigureAwait(false);
                var hash = new byte[32]; await input.ReadExactlyAsync(hash, token).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), hash)) throw new InvalidDataException();
                var record = RecoveryWire.Decode<RecoveryAuditEvent>(payload, MaximumFrame);
                Validate(record);
                if (record.SessionId != sessionId || record.Sequence != last.Sequence + 1 || record.PreviousRecordSha256 != last.RecordSha256
                    || !events.TryAdd(record.Id, Fingerprint(new(record.Id, record.TimestampUtc, record.Kind, record.Description, record.BeforeStateSha256, record.AfterStateSha256))))
                    throw new InvalidDataException();
                last = new(record.Sequence, input.Position, Convert.ToHexString(hash));
                if (last == expected) found = true;
            }
            catch (Exception error) when (error is InvalidDataException || error is RecoveryRejectedException rejected && rejected.Failure != RecoveryFailure.VersionMismatch)
            { damaged = true; break; }
        }
        if (!found) throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.AuditAnchorMissing");
        return new(last, damaged, found, events.Count, events);
    }

    internal async Task<RecoveryAuditAnchor> AppendAsync(IReadOnlyList<RecoveryAuditDraft> drafts, CancellationToken token)
    {
        if (drafts.Count > 1024) throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.TooManyAuditEvents");
        var scan = await ReadAsync(null, token).ConfigureAwait(false);
        if (scan.TailDamaged) throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.AuditDamagedTailRequiresReview");
        var last = scan.Last;
        var fingerprints = new Dictionary<string, string>(scan.EventFingerprints, StringComparer.Ordinal);
        RecoveryStoragePermissions.Directory(Path.GetDirectoryName(path)!); RecoveryWire.NoLink(Path.GetDirectoryName(path)!); RecoveryWire.NoLink(path);
        await using var output = RecoveryStoragePermissions.Open(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (output.Length != last.CommittedLength) throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.AuditChanged");
        output.Position = last.CommittedLength;
        foreach (var draft in drafts)
        {
            token.ThrowIfCancellationRequested();
            if (draft is null) throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.NullAuditEvent");
            var record = new RecoveryAuditEvent(1, draft.Id, sessionId, checked(last.Sequence + 1), draft.TimestampUtc.ToUniversalTime(),
                draft.Kind, draft.Description, draft.BeforeStateSha256, draft.AfterStateSha256, last.RecordSha256);
            Validate(record);
            var fingerprint = Fingerprint(draft with { TimestampUtc = draft.TimestampUtc.ToUniversalTime() });
            if (fingerprints.TryGetValue(record.Id, out var previous))
            { if (previous != fingerprint) throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.AuditIdConflict"); continue; }
            if (fingerprints.Count >= MaximumRecords) throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.AuditTooManyRecords");
            var payload = RecoveryWire.Encode(record, MaximumFrame); var hash = SHA256.HashData(payload);
            if (payload.LongLength + 36 > MaximumBytes - output.Position) throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.AuditTooLarge");
            var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
            await output.WriteAsync(prefix, token).ConfigureAwait(false); await output.WriteAsync(payload, token).ConfigureAwait(false);
            await output.WriteAsync(hash, token).ConfigureAwait(false);
            last = new(record.Sequence, output.Position, Convert.ToHexString(hash)); fingerprints.Add(record.Id, fingerprint);
        }
        await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
        return last;
    }
    private static string Fingerprint(RecoveryAuditDraft draft) => RecoveryWire.Hash(RecoveryWire.Encode(draft, MaximumFrame));
    private static void Validate(RecoveryAuditEvent record)
    {
        if (record.Schema != 1) throw RecoveryWire.Reject(RecoveryFailure.VersionMismatch, "Recovery.AuditVersionMismatch");
        RecoveryWire.Id(record.Id); RecoveryWire.Id(record.SessionId); RecoveryWire.PlainText(record.Description, 4096);
        if (record.Sequence < 1 || record.TimestampUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(record.Kind)
            || !RecoveryWire.IsHash(record.BeforeStateSha256) || !RecoveryWire.IsHash(record.AfterStateSha256) || !RecoveryWire.IsHash(record.PreviousRecordSha256))
            throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.InvalidAuditEvent");
    }
}
