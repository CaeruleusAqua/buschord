using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Knx.Catalog;
using Knx.Recovery;
using Knx.Workbench;

namespace Knx;

// Recovery shares the encapsulated snapshot state of this partial UndoStack.
public sealed partial class UndoStack
{
    private sealed class SourceDigest(string value) { internal string Value { get; } = value; }
    private static readonly ConditionalWeakTable<byte[], SourceDigest> RecoverySourceHashes = new();
    private sealed class InventoryRecord(RecoveryDependencyInventory inventory) { internal RecoveryDependencyInventory Inventory { get; } = inventory; }
    private readonly ConditionalWeakTable<Dictionary<string, CatalogHardware>, InventoryRecord> _recoveryInventories = new();
    private Func<Project, RecoveryDependencyInventory>? _recoveryDependencyRecorder;

    /// <summary>Configure before Reset/Push. The recorder reports actual transaction sources, never a late re-resolution of old steps.</summary>
    public void SetRecoveryDependencyRecorder(Func<Project, RecoveryDependencyInventory>? recorder) => _recoveryDependencyRecorder = recorder;

    /// <summary>Capture on the owning project thread. Pending UI drafts are neither read nor serialized.</summary>
    public RecoveryAuditContext ExportRecoveryAuditContext(Project current, string sessionId, RecoverySource source)
    {
        ArgumentNullException.ThrowIfNull(current); RecoveryWire.Id(sessionId);
        RecoveryContractValidation.ValidateBinding(source, sessionId);
        if (Knxproj.IsPasswordProtected(current) || _snapshots.Any(snapshot => snapshot.Archive?.Encrypted == true))
            throw RecoveryWire.Reject(RecoveryFailure.ProtectedSource, "Recovery.ProtectedSource");
        if (current.Id != source.ProjectId || current.Guid != source.ProjectGuid)
            throw RecoveryWire.Reject(RecoveryFailure.ProjectIdentityMismatch, "Recovery.ProjectIdentityMismatch");
        if (Capacity is < 1 or > 60 || _snapshots.Count > Capacity)
            throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.InvalidLiveUndoState");
        var archives = _snapshots.Select(snapshot => snapshot.Archive).Append(current.ArchiveState).ToArray();
        return new(sessionId, source, Array.AsReadOnly(archives));
    }

    public RecoveryExport ExportRecovery(Project current, string sessionId, RecoverySource source)
    {
        ArgumentNullException.ThrowIfNull(current); RecoveryWire.Id(sessionId);
        RecoveryContractValidation.ValidateBinding(source, sessionId);
        if (Knxproj.IsPasswordProtected(current) || _snapshots.Any(snapshot => snapshot.Archive?.Encrypted == true))
            throw RecoveryWire.Reject(RecoveryFailure.ProtectedSource, "Recovery.ProtectedSource");
        if (current.Id != source.ProjectId || current.Guid != source.ProjectGuid || Capacity is < 1 or > 60
            || _snapshots.Count < 1 || _snapshots.Count > Capacity || Index < 0 || Index >= _snapshots.Count
            || _savedIndex < -1 || _savedIndex >= _snapshots.Count)
            throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.InvalidLiveUndoState");
        // Caller captures on the owning project thread. No mutable Project survives into background serialization.
        var snapshots = _snapshots.Select(snapshot => CaptureRecovery(snapshot.Xml, snapshot.Archive, snapshot.Hardware, snapshot.Texts) with
            { RecordedDependencies = _recoveryInventories.TryGetValue(snapshot.Hardware, out var recorded) ? recorded.Inventory : null }).ToArray();
        RecoverySnapshotCodec.CheckPlainSnapshots(snapshots);
        var live = CaptureRecovery(Knxproj.ToXml(current).ToString(SaveOptions.DisableFormatting), current.ArchiveState, current.Hardware, current.ObjectTextDefinitions);
        if (RecoveryIdentity(live) != RecoveryIdentity(snapshots[Index]))
            throw RecoveryWire.Reject(RecoveryFailure.StaleSession, "Recovery.LiveSnapshotChanged");
        return new(sessionId, current.Name, source, Capacity, Index,
            source.Kind == RecoveryBindingKind.UnsavedProject ? -1 : _savedIndex, snapshots);
    }

    // Equality includes overlays/hardware/texts. Saved-file binding changes remain outside archive/model identity.
    private bool RecoverySnapshotEquals(Project project, string xml)
    {
        var previous = _snapshots[Index];
        return RecoveryIdentity(CaptureRecovery(xml, project.ArchiveState, project.Hardware, project.ObjectTextDefinitions), bounded: false)
            == RecoveryIdentity(CaptureRecovery(previous.Xml, previous.Archive, previous.Hardware, previous.Texts), bounded: false);
    }

    // Optional provenance belongs to the newly committed snapshot at Index.
    private void RecordRecoverySnapshot(Project project)
    {
        if (Index < 0 || Index >= _snapshots.Count) return; // Preserve a pre-existing zero-capacity UndoStack's no-history behavior.
        try
        {
            var inventory = _recoveryDependencyRecorder is null ? RecoverySnapshotCodec.ModelInventory(project) : _recoveryDependencyRecorder(project);
            RecoverySnapshotCodec.ValidateInventory(project, inventory);
            inventory = RecoverySnapshotCodec.FreezeInventory(inventory);
            var key = _snapshots[Index].Hardware;
            _recoveryInventories.Remove(key); _recoveryInventories.Add(key, new(inventory));
        }
        catch (Exception)
        {
            // This optional metadata cannot fail an already committed snapshot. Leave its
            // inventory absent; explicit export later reports ModelOnly. Do not enumerate
            // the same failing model again from inside the catch path.
        }
    }

    /// <summary>New never-saved sessions stay dirty even when undo returns to the initial step.</summary>
    public void ResetUnsaved(Project project) { Reset(project); _savedIndex = -1; }
    public string GetRecoveryFingerprint()
    {
        if (Index < 0 || Index >= _snapshots.Count) throw new InvalidOperationException("Recovery.NoCurrentSnapshot");
        var snapshot = _snapshots[Index];
        return RecoveryIdentity(CaptureRecovery(snapshot.Xml, snapshot.Archive, snapshot.Hardware, snapshot.Texts), bounded: false);
    }

    internal static RestoredRecoverySession CreateRecoverySession(UndoRecoveryState wire, RecoverySource source,
        IReadOnlyList<RecoveryCapturedSnapshot> snapshots)
    {
        RecoveryContractValidation.ValidateUndoShape(wire, source.Kind);
        if (snapshots.Count != wire.Snapshots.Count) throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.SnapshotCountChanged");
        var undo = new UndoStack(wire.Capacity);
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            var validated = RecoverySnapshotCodec.Hydrate(snapshot);
            undo._snapshots.Add((snapshot.Xml, snapshot.Archive, new Dictionary<string, CatalogHardware>(validated.Hardware), new(validated.ObjectTextDefinitions)));
            var metadata = new RecoveryDependencyInventory(wire.Snapshots[index].Requirements, wire.Snapshots[index].References, wire.Snapshots[index].DependencyCoverage);
            undo._recoveryInventories.Add(undo._snapshots[^1].Hardware, new(RecoverySnapshotCodec.FreezeInventory(metadata)));
        }
        undo.Index = wire.Index; undo._savedIndex = wire.SavedIndex;
        return new(RecoverySnapshotCodec.Hydrate(snapshots[wire.Index]), undo, source);
    }

    private static RecoveryCapturedSnapshot CaptureRecovery(string xml, ArchiveState? archive,
        IReadOnlyDictionary<string, CatalogHardware> hardware, IReadOnlyDictionary<ObjectKey, ObjectTextDefinition> texts)
        => new(xml, archive,
            hardware.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => RecoverySnapshotCodec.Hardware(pair.Key, pair.Value)).ToArray(),
            texts.OrderBy(pair => pair.Key.DeviceId, StringComparer.Ordinal).ThenBy(pair => pair.Key.ObjectRefId, StringComparer.Ordinal)
                .Select(pair => RecoverySnapshotCodec.Text(pair.Key, pair.Value)).ToArray());

    internal static string RecoveryIdentity(RecoveryCapturedSnapshot snapshot, bool bounded = true)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Normal Undo/audit fingerprints include every fact but do not inherit optional
        // checkpoint limits. Both paths use identical JSON bytes for supported sizes.
        byte[] Serialize<T>(T value, int maximum) => bounded ? RecoveryWire.Encode(value, maximum)
            : System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, RecoveryWire.Json);
        void Append(byte[] bytes)
        {
            Span<byte> length = stackalloc byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        Append("buschord-undo-identity-v1"u8.ToArray()); Append(Encoding.UTF8.GetBytes(snapshot.Xml));
        if (snapshot.Archive is not { } archive) Append([]);
        else
        {
            string Digest(byte[] bytes) => RecoverySourceHashes.GetValue(bytes, data => new(RecoveryWire.Hash(data))).Value;
            Append(Serialize(new
            {
                Source = Digest(archive.Bytes), archive.NestedEntry, archive.Encrypted, Layout = archive.Layout?.Prefix ?? "",
                NativeIds = archive.NativeObjectIds?.Order(StringComparer.Ordinal).ToArray(),
                Applications = archive.ApplicationParts?.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { pair.Key, Sha256 = Digest(pair.Value) }).ToArray(),
                Generated = archive.GeneratedParts?.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { pair.Key, Sha256 = Digest(pair.Value) }).ToArray()
            }, RecoveryWire.ManifestLimit));
        }
        Append(Serialize(snapshot.Hardware, 32 * 1024 * 1024)); Append(Serialize(snapshot.Texts, 32 * 1024 * 1024));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
