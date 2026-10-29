namespace Knx.Recovery;

/// <summary>A Core-owned detached export. File metadata cannot manufacture its protection proof.</summary>
public sealed class RecoveryExport
{
    internal IReadOnlyList<RecoveryCapturedSnapshot> Snapshots { get; }
    internal int Capacity { get; }
    internal int Index { get; }
    internal int SavedIndex { get; }
    public string SessionId { get; }
    public string ProjectName { get; }
    public RecoverySource Source { get; }
    internal RecoveryExport(string sessionId, string name, RecoverySource source, int capacity, int index, int savedIndex,
        IReadOnlyList<RecoveryCapturedSnapshot> snapshots)
    {
        SessionId = sessionId; ProjectName = name; Source = source; Capacity = capacity; Index = index;
        SavedIndex = savedIndex; Snapshots = snapshots;
    }
}

internal sealed record RecoveryCapturedSnapshot(string Xml, ArchiveState? Archive,
    IReadOnlyList<RecoveryHardware> Hardware, IReadOnlyList<RecoveryObjectText> Texts, RecoveryDependencyInventory? RecordedDependencies = null);
