using System.Security.Cryptography;
using System.Text;

namespace Knx.Recovery;

/// <summary>Structural checks only; not a substitute for source/protection/XML/dependency validation.</summary>
public static class RecoveryContractValidation
{
    public static void ValidateBinding(RecoverySource source, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(source);
        Id(sessionId); ValidateBindingCore(source, sessionId);
    }
    internal static void ValidateSavedBinding(RecoverySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != RecoveryBindingKind.SavedArchive) throw Corrupt("Expected a saved source binding.");
        ValidateBindingCore(source, null);
    }
    private static void ValidateBindingCore(RecoverySource source, string? sessionId)
    {
        Text(source.ProjectId, 256, false);
        if (source.ProjectGuid is not null && (!Guid.TryParse(source.ProjectGuid, out var projectGuid) || projectGuid == Guid.Empty)) throw Corrupt("Invalid project GUID.");
        switch (source.Kind)
        {
            case RecoveryBindingKind.SavedArchive:
                Text(source.FullPath, 4096, false); Text(source.PathIdentity, 64, false);
                if (source.SavedFile is null || source.UnsavedSessionId is not null) throw Corrupt("Invalid saved source binding.");
                Blob(source.SavedFile);
                if (source.SavedFile.Length == 0 || !Path.IsPathFullyQualified(source.FullPath!)
                    || source.FullPath != Path.GetFullPath(source.FullPath!) || source.PathIdentity != PathIdentity(source.FullPath!))
                    throw Corrupt("Saved source identity is not canonical.");
                break;
            case RecoveryBindingKind.UnsavedProject:
                if (sessionId is null || source.FullPath is not null || source.PathIdentity is not null || source.SavedFile is not null
                    || source.UnsavedSessionId != sessionId || source.ProjectGuid is null)
                    throw Corrupt("Invalid unsaved project binding.");
                break;
            default: throw Corrupt("Unknown recovery source mode.");
        }
    }

    public static void ValidateUndoShape(UndoRecoveryState undo, RecoveryBindingKind binding)
    {
        ArgumentNullException.ThrowIfNull(undo);
        if (!Enum.IsDefined(binding)) throw Corrupt("Unknown recovery source mode.");
        if (undo.Schema != 1) throw new RecoveryRejectedException(RecoveryFailure.VersionMismatch, "Unsupported Undo recovery schema.");
        if (undo.Snapshots is null || undo.Capacity is < 1 or > 60 || undo.Snapshots.Count < 1 || undo.Snapshots.Count > undo.Capacity
            || undo.Index < 0 || undo.Index >= undo.Snapshots.Count || undo.SavedIndex < -1 || undo.SavedIndex >= undo.Snapshots.Count
            || binding == RecoveryBindingKind.UnsavedProject && undo.SavedIndex != -1)
            throw Corrupt("Invalid recovery Undo indices or capacity.");
        long xmlBytes = 0;
        foreach (var snapshot in undo.Snapshots)
        {
            if (snapshot is null || snapshot.Schema != 1) throw new RecoveryRejectedException(RecoveryFailure.VersionMismatch, "Unsupported or null recovery snapshot.");
            Blob(snapshot.Xml); Blob(snapshot.HardwareFacts); Blob(snapshot.TextDefinitions); Hash(snapshot.StateSha256);
            Hash(snapshot.SemanticSha256);
            if (!Enum.IsDefined(snapshot.DependencyCoverage)) throw Corrupt("Unknown dependency coverage.");
            if (snapshot.References is null || snapshot.References.Count > 100000) throw Corrupt("Invalid reference fingerprint list.");
            var devices = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in snapshot.References)
            {
                if (reference is null) throw Corrupt("Null reference fingerprint.");
                Text(reference.DeviceId, 256, false); Text(reference.ProductRefId, 512, true);
                if (!devices.Add(reference.DeviceId)) throw Corrupt("Duplicate reference fingerprint.");
                if ((reference.ApplicationProgramId is null) != (reference.ProgramSha256 is null)) throw Corrupt("Incomplete application fingerprint.");
                if (reference.ProgramSha256 is { } programHash) Hash(programHash);
            }
            if (snapshot.Xml.Length is < 1 or > 16L * 1024 * 1024) throw Limit("Snapshot XML limit exceeded.");
            if (snapshot.ArchiveEnvelope is { } envelope) { Blob(envelope); if (envelope.Length > 8L * 1024 * 1024) throw Limit("Archive envelope limit exceeded."); }
            xmlBytes = checked(xmlBytes + snapshot.Xml.Length);
            if (xmlBytes > 64L * 1024 * 1024) throw Limit("Aggregate snapshot XML limit exceeded.");
            if (snapshot.Requirements is null || snapshot.Requirements.Count > 4096) throw Corrupt("Invalid dependency list.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var requirement in snapshot.Requirements)
            {
                if (requirement is null) throw Corrupt("Null recovery dependency.");
                Text(requirement.ProgramId, 256, false); Blob(requirement.Program);
                if (!ids.Add(requirement.ProgramId)) throw Corrupt("Duplicate recovery programme dependency.");
                if (requirement.Kind == RecoveryDependencyKind.ExternalCatalogue)
                { Text(requirement.CatalogueIdentity, 256, false); Text(requirement.CatalogueVersion, 256, false); }
                else if (requirement.Kind != RecoveryDependencyKind.Embedded || requirement.CatalogueIdentity is not null || requirement.CatalogueVersion is not null)
                    throw Corrupt("Inconsistent recovery dependency origin.");
                if (requirement.RequiredResources is null || requirement.RequiredResources.Count > 4096) throw Corrupt("Invalid resource list.");
                var entries = new HashSet<string>(StringComparer.Ordinal);
                foreach (var part in requirement.RequiredResources)
                {
                    if (part is null) throw Corrupt("Null recovery resource.");
                    EntryName(part.EntryPath); Blob(part.Blob);
                    if (!entries.Add(part.EntryPath)) throw Corrupt("Duplicate recovery resource.");
                }
                Text(requirement.CapabilityNotice, 4096, true);
            }
        }
    }

    // No file is opened here. Load must compare against a separately caller-supplied expected path,
    // never read arbitrary FullPath values from a discovered manifest.
    public static string PathIdentity(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)));
    }
    public static void Blob(RecoveryBlob? blob)
    {
        if (blob is null) throw Corrupt("Missing recovery blob reference.");
        Hash(blob.Sha256);
        if (blob.Length is < 0 or > RecoveryBlobStore.MaximumBlobBytes) throw Limit("Invalid recovery blob length.");
    }
    private static void Hash(string hash)
    {
        if (hash is not { Length: 64 } || hash.Any(character => !char.IsAsciiHexDigit(character)) || hash != hash.ToUpperInvariant())
            throw Corrupt("Invalid canonical recovery hash.");
    }
    private static void Id(string value)
    {
        if (value is not { Length: 32 } || !Guid.TryParseExact(value, "N", out var id) || id == Guid.Empty) throw Corrupt("Invalid recovery session ID.");
    }
    private static void Text(string? value, int maximum, bool empty)
    {
        if (value is null || value.Length > maximum || !empty && string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) throw Corrupt("Invalid recovery metadata.");
    }
    private static void EntryName(string name)
    {
        Text(name, 1024, false);
        if (name.Split('/').Any(segment => segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' '))
            || name.Any(character => character is '\\' or ':' or '\0')) throw Corrupt("Invalid archive resource identity.");
    }
    private static RecoveryRejectedException Corrupt(string message) => new(RecoveryFailure.Corrupt, message);
    private static RecoveryRejectedException Limit(string message) => new(RecoveryFailure.LimitExceeded, message);
}
