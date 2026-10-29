namespace Knx.Recovery;

/// <summary>Core-owned detached protection/source context. Carries no live model, draft data or snapshot XML.</summary>
public sealed class RecoveryAuditContext
{
    public string SessionId { get; }
    public RecoverySource Source { get; }
    internal IReadOnlyList<ArchiveState?> Archives { get; }
    internal RecoveryAuditContext(string sessionId, RecoverySource source, IReadOnlyList<ArchiveState?> archives)
    { SessionId = sessionId; Source = source; Archives = archives; }

    /// <summary>Rebind a successfully saved source without dropping the transaction's frozen archive protection scope.</summary>
    public RecoveryAuditContext WithSource(RecoverySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RecoveryContractValidation.ValidateBinding(source, SessionId);
        if (source.ProjectId != Source.ProjectId || source.ProjectGuid != Source.ProjectGuid)
            throw RecoveryWire.Reject(RecoveryFailure.ProjectIdentityMismatch, "Recovery.ProjectIdentityMismatch");
        if (Source.Kind == RecoveryBindingKind.SavedArchive && source.Kind != RecoveryBindingKind.SavedArchive)
            throw RecoveryWire.Reject(RecoveryFailure.SourceChanged, "Recovery.SourceBindingDowngrade");
        return new(SessionId, source, Archives);
    }
}
