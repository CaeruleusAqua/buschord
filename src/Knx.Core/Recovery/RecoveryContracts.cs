namespace Knx.Recovery;

// Versioned typed contracts for local session recovery.
public enum RecoveryFailure
{
    ProtectedSource, VersionMismatch, SourceMissing, SourceChanged, ProjectIdentityMismatch,
    CatalogMissing, DependencyChanged, DependencyUnknown, Corrupt, LimitExceeded, StaleSession, StorageBusy
}

public sealed record RecoveryBlob(string Sha256, long Length);
public enum RecoveryBindingKind { SavedArchive, UnsavedProject }
public sealed record RecoverySource(RecoveryBindingKind Kind, string ProjectId, string? ProjectGuid,
    string? FullPath, string? PathIdentity, RecoveryBlob? SavedFile, string? UnsavedSessionId);
public sealed record RecoveryPart(string EntryPath, RecoveryBlob Blob);
public sealed record RecoveryArchive(int Schema, RecoveryBlob Source, string? NestedEntry,
    string? LayoutPrefix, string ProjectFormat, IReadOnlyList<string>? NativeObjectIds,
    IReadOnlyList<RecoveryPart> ApplicationParts, IReadOnlyList<RecoveryPart> GeneratedParts, bool HasApplicationCache, bool HasGeneratedParts);
public sealed record RecoveryHardware(string Key, string HardwareId, string Name, double? BusCurrent,
    bool IsPowerSupply, bool IsChoke, bool IsCoupler, bool IsPowerLineRepeater, bool IsIpEnabled,
    IReadOnlyList<string> MediumTypeIds);
public sealed record RecoveryTextArgument(string Name, string Value);
public sealed record RecoveryObjectText(string DeviceId, string ObjectRefId, string? Text,
    string? FunctionText, string? ReferenceId, string? DefaultValue, bool KnownReference,
    IReadOnlyList<RecoveryTextArgument>? NamedArguments, bool HasArgumentValue, string? ArgumentValue,
    bool HasChannelDefinition, string? ChannelId, string? ChannelName, string? ChannelDeclarationId, string? NativeChannelId);
public enum RecoveryDependencyKind { Embedded, ExternalCatalogue }
public sealed record RecoveryDependency(string ProgramId, RecoveryBlob Program,
    RecoveryDependencyKind Kind, string? CatalogueIdentity, string? CatalogueVersion,
    IReadOnlyList<RecoveryPart> RequiredResources, string CapabilityNotice);
public sealed record RecoveryReference(string DeviceId, string ProductRefId, string? Hardware2ProgramRefId,
    string? ApplicationProgramId, string? ProgramSha256);
public enum RecoveryDependencyCoverage { RecordedRequirements, ModelOnly }
public sealed record RecoveryDependencyInventory(IReadOnlyList<RecoveryDependency> Requirements,
    IReadOnlyList<RecoveryReference> References, RecoveryDependencyCoverage Coverage);
public sealed record RecoverySnapshot(int Schema, RecoveryBlob Xml, RecoveryBlob? ArchiveEnvelope,
    RecoveryBlob HardwareFacts, RecoveryBlob TextDefinitions, IReadOnlyList<RecoveryDependency> Requirements,
    string StateSha256, IReadOnlyList<RecoveryReference> References, RecoveryDependencyCoverage DependencyCoverage, string SemanticSha256);
public sealed record UndoRecoveryState(int Schema, int Capacity, int Index, int SavedIndex,
    IReadOnlyList<RecoverySnapshot> Snapshots);
public sealed record RecoveryAuditAnchor(long Sequence, long CommittedLength, string RecordSha256);
public sealed record RecoveryManifest(int Schema, string Id, string SessionId, DateTimeOffset CreatedUtc,
    RecoverySource Source, UndoRecoveryState Undo, RecoveryAuditAnchor Audit, string ProducerFormat, string ProjectName);
public enum RecoveryOperationKind { Edit, Undo, Redo, Save, SaveAs, Restore }
public sealed record RecoveryAuditEvent(int Schema, string Id, string SessionId, long Sequence,
    DateTimeOffset TimestampUtc, RecoveryOperationKind Kind, string Description,
    string BeforeStateSha256, string AfterStateSha256, string PreviousRecordSha256);
public sealed record RecoveryAuditDraft(string Id, DateTimeOffset TimestampUtc, RecoveryOperationKind Kind,
    string Description, string BeforeStateSha256, string AfterStateSha256);
public enum RecoveryListingStatus
{
    MetadataOnly, Validated, ProtectedSource, VersionMismatch, SourceMissing, SourceChanged,
    ProjectIdentityMismatch, CatalogMissing, DependencyChanged, DependencyUnknown, Corrupt, LimitExceeded, StaleSession, StorageBusy
}
public sealed record RecoveryListItem(string Id, string SessionId, DateTimeOffset? CreatedUtc, string? ProjectName,
    string? ProjectId, bool? Dirty, int? UndoCount, int? RedoCount, RecoveryListingStatus Status, string ReasonCode, string? Detail = null,
    RecoveryBindingKind? BindingKind = null, string? ProjectGuid = null);
public sealed record RecoveryListPage(IReadOnlyList<RecoveryListItem> Items, string? Continuation, bool ScanLimitReached);
public sealed record RecoveryCheckpoint(string Id, DateTimeOffset CreatedUtc, RecoveryAuditAnchor Audit);
public sealed record RestoredRecoverySession(Project Project, UndoStack Undo, RecoverySource Source);
public sealed class RecoveryCandidate
{
    private Func<RestoredRecoverySession>? _take;
    public RecoveryListItem Summary { get; }
    public IReadOnlyList<string> Notices { get; }
    public long DependencyGeneration { get; }
    internal RecoveryCandidate(RecoveryListItem summary, IReadOnlyList<string> notices, long generation, Func<RestoredRecoverySession> take)
    { Summary = summary; Notices = notices; DependencyGeneration = generation; _take = take; }
    /// <summary>Only the application adoption gate consumes this once, after checking its live session/source/catalogue guards.</summary>
    public RestoredRecoverySession Take() => (Interlocked.Exchange(ref _take, null)
        ?? throw new InvalidOperationException("Recovery.CandidateAlreadyConsumed"))();
}

/// <summary>Local dependencies only. Returned bytes are independently length/hash checked by recovery.</summary>
public interface IRecoveryDependencyResolver
{
    long Generation { get; }
    ValueTask<Stream?> OpenProgramAsync(RecoveryDependency requirement, CancellationToken cancellationToken = default);
    ValueTask<Stream?> OpenResourceAsync(RecoveryDependency requirement, RecoveryPart resource, CancellationToken cancellationToken = default);
}

public sealed class RecoveryRejectedException(RecoveryFailure failure, string message) : IOException(message)
{
    public RecoveryFailure Failure { get; } = failure;
    public string Code { get; } = message.StartsWith("Recovery.", StringComparison.Ordinal) ? message.Split(' ')[0] : "Recovery." + failure;
}
