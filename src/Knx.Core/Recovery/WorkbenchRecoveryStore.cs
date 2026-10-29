using System.Security.Cryptography;

namespace Knx.Recovery;

/// <summary>Explicit local checkpoints. Does not open the UI, save a project, connect a bus, or adopt a candidate.</summary>
public sealed class WorkbenchRecoveryStore
{
    private sealed record Head(int Schema, string Id, string SessionId, DateTimeOffset CreatedUtc, RecoveryBlob Manifest);
    private sealed record Notice(int Schema, string Id, string SessionId, DateTimeOffset CreatedUtc, RecoveryFailure Failure);
    private readonly string _root;
    private readonly RecoveryBlobStore _blobs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const long MaximumRootBytes = 4L * 1024 * 1024 * 1024;
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusChord.UserData", "recovery");
    public WorkbenchRecoveryStore(string? directory = null)
    {
        _root = Path.GetFullPath(directory ?? DefaultDirectory); _blobs = new(_root);
        foreach (var name in new[] { "heads", "manifests", "audit", "notices" })
        { var path = Path.Combine(_root, name); RecoveryStoragePermissions.ManagedDirectory(path); RecoveryWire.NoLink(path); }
        RecoveryStoragePermissions.ExistingFile(Path.Combine(_root, "writer.lock"));
    }

    public Task<RecoveryCheckpoint> SaveAsync(RecoveryExport export, IReadOnlyList<RecoveryAuditDraft>? audit = null, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(export);
            RecoveryWire.Id(export.SessionId); RecoveryContractValidation.ValidateBinding(export.Source, export.SessionId);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var lease = await EnterWriterAsync(cancellationToken).ConfigureAwait(false);
                EnsureListingCapacity("heads", export.SessionId);
                // Reject every protected historical state BEFORE writing any XML or CAS bytes.
                RecoverySnapshotCodec.CheckPlainSnapshots(export.Snapshots);
                await VerifyExpectedSourceAsync(export.Source, cancellationToken).ConfigureAwait(false);
                var available = MaximumRootBytes - StorageBytes(out var entries) - 32L * 1024 * 1024;
                if (available < 0) throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.RootStorageBudget");
                var undo = await RecoverySnapshotCodec.EncodeAsync(export, _blobs, cancellationToken, available, Math.Max(0, 60000 - entries - 16)).ConfigureAwait(false);
                RecoveryContractValidation.ValidateUndoShape(undo, export.Source.Kind);
                var anchor = await new RecoveryAuditJournal(Part("audit", export.SessionId, ".log"), export.SessionId)
                    .AppendAsync(audit ?? [], cancellationToken).ConfigureAwait(false);
                var id = Guid.NewGuid().ToString("N"); var created = DateTimeOffset.UtcNow;
                var manifest = new RecoveryManifest(1, id, export.SessionId, created, export.Source, undo, anchor, "buschord-session-v1", export.ProjectName);
                var bytes = RecoveryWire.Encode(manifest, RecoveryWire.ManifestLimit);
                await RecoveryWire.AtomicAsync(Part("manifests", id, ".json"), bytes, false, cancellationToken).ConfigureAwait(false);
                var head = new Head(1, id, export.SessionId, created, new(RecoveryWire.Hash(bytes), bytes.Length));
                await RecoveryWire.AtomicAsync(Part("heads", export.SessionId, ".json"), RecoveryWire.Encode(head, 4096), true, cancellationToken).ConfigureAwait(false);
                return new RecoveryCheckpoint(id, created, anchor);
            }
            finally { _gate.Release(); }
        }, cancellationToken);

    /// <summary>Append committed-action audit without changing any checkpoint/head. A journal can be ahead of its last model checkpoint.</summary>
    public Task<RecoveryAuditAnchor> AppendAuditAsync(RecoveryAuditContext context, IReadOnlyList<RecoveryAuditDraft> audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(audit);
        if (audit.Count > 1024) throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.TooManyAuditEvents");
        var detached = audit.ToArray();
        return Task.Run(async () =>
        {
            RecoveryWire.Id(context.SessionId); RecoveryContractValidation.ValidateBinding(context.Source, context.SessionId);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var lease = await EnterWriterAsync(cancellationToken).ConfigureAwait(false);
                // Recheck actual immutable source containers before writing ANY event descriptions.
                RecoverySnapshotCodec.CheckPlainArchives(context.Archives);
                await VerifyExpectedSourceAsync(context.Source, cancellationToken).ConfigureAwait(false);
                var path = Part("audit", context.SessionId, ".log");
                var existingBytes = StorageBytes(out var entries);
                var maximumAppend = checked(detached.LongLength * (16384 + 36));
                if (existingBytes > MaximumRootBytes - maximumAppend || !File.Exists(path) && entries >= 60000)
                    throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.RootStorageBudget");
                return await new RecoveryAuditJournal(path, context.SessionId).AppendAsync(detached, cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }, cancellationToken);
    }

    /// <summary>Minimal omission record; never accepts protected project names, paths, descriptions, XML or bytes.</summary>
    public Task RecordProtectedOmissionAsync(string sessionId, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        RecoveryWire.Id(sessionId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await EnterWriterAsync(cancellationToken).ConfigureAwait(false);
            EnsureListingCapacity("notices", sessionId);
            if (StorageBytes(out _) > MaximumRootBytes - 8192) throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.RootStorageBudget");
            var id = Guid.NewGuid().ToString("N");
            var notice = new Notice(1, id, sessionId, DateTimeOffset.UtcNow, RecoveryFailure.ProtectedSource);
            await RecoveryWire.AtomicAsync(Part("notices", sessionId, ".json"), RecoveryWire.Encode(notice, 4096), true, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }, cancellationToken);

    public Task<RecoveryListPage> ListAsync(int pageSize = 50, string? continuation = null, RecoverySource? expectedSource = null, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
            if (continuation is not null && !RecoveryWire.IsId(continuation)) throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.InvalidContinuation");
            var items = new List<RecoveryListItem>(); var examined = 0; var limit = false;
            // IDs are GUIDs, not file paths. Directory discovery has its own I/O budget.
            foreach (var directory in new[] { "heads", "notices" })
            {
                var folder = Path.Combine(_root, directory); RecoveryWire.NoLink(folder);
                foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++examined > 4096) { limit = true; break; }
                    var fileId = Path.GetFileNameWithoutExtension(path);
                    if (!RecoveryWire.IsId(fileId)) continue;
                    try
                    {
                        RecoveryListItem item;
                        if (directory == "notices")
                        {
                            var notice = RecoveryWire.Decode<Notice>(await RecoveryWire.ReadAsync(path, 4096, cancellationToken).ConfigureAwait(false), 4096);
                            if (notice.Schema != 1 || notice.SessionId != fileId || notice.Failure != RecoveryFailure.ProtectedSource || !RecoveryWire.IsId(notice.Id)) continue;
                            item = new(notice.Id, notice.SessionId, notice.CreatedUtc, null, null, null, null, null, RecoveryListingStatus.ProtectedSource, "Recovery.ProtectedSource");
                        }
                        else
                        {
                            var head = RecoveryWire.Decode<Head>(await RecoveryWire.ReadAsync(path, 4096, cancellationToken).ConfigureAwait(false), 4096);
                            ValidateHead(head, fileId);
                            var manifest = await ReadManifestAsync(head.Id, head.Manifest, cancellationToken).ConfigureAwait(false);
                            var status = expectedSource is null || SameBinding(manifest.Source, expectedSource) ? RecoveryListingStatus.MetadataOnly : RecoveryListingStatus.SourceChanged;
                            item = Summary(manifest, status, status == RecoveryListingStatus.MetadataOnly ? "Recovery.NotYetValidated" : "Recovery.SourceChanged");
                        }
                        if (continuation is not null && StringComparer.Ordinal.Compare(item.Id, continuation) <= 0) continue;
                        items.Add(item);
                        if (items.Count > pageSize) items.Remove(items.MaxBy(entry => entry.Id, StringComparer.Ordinal)!);
                    }
                    catch (RecoveryRejectedException error)
                    {
                        // Bad metadata remains discoverable, without trusting names/paths from its payload.
                        if (continuation is null || StringComparer.Ordinal.Compare(fileId, continuation) > 0)
                            items.Add(new(fileId, fileId, null, null, null, null, null, null, ListingFailure(error.Failure), error.Code, error.Message));
                        if (items.Count > pageSize) items.Remove(items.MaxBy(entry => entry.Id, StringComparer.Ordinal)!);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        if (continuation is null || StringComparer.Ordinal.Compare(fileId, continuation) > 0)
                            items.Add(new(fileId, fileId, null, null, null, null, null, null, RecoveryListingStatus.Corrupt, "Recovery.MetadataUnreadable", error.GetType().Name));
                        if (items.Count > pageSize) items.Remove(items.MaxBy(entry => entry.Id, StringComparer.Ordinal)!);
                    }
                }
                if (limit) break;
            }
            var ordered = items.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
            // A hostile/excessive directory is an explicit truncated discovery, not a cursor promising unseen IDs.
            return new RecoveryListPage(Array.AsReadOnly(ordered), !limit && ordered.Length == pageSize ? ordered[^1].Id : null, limit);
        }, cancellationToken);

    public Task<RecoveryCandidate> LoadCandidateAsync(string id, RecoverySource expectedSource, IRecoveryDependencyResolver? dependencies = null, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            RecoveryWire.Id(id); ArgumentNullException.ThrowIfNull(expectedSource);
            var manifest = await ReadManifestAsync(id, null, cancellationToken).ConfigureAwait(false);
            var head = RecoveryWire.Decode<Head>(await RecoveryWire.ReadAsync(Part("heads", manifest.SessionId, ".json"), 4096, cancellationToken).ConfigureAwait(false), 4096);
            ValidateHead(head, manifest.SessionId);
            if (head.Id != id) throw RecoveryWire.Reject(RecoveryFailure.StaleSession, "Recovery.CheckpointSuperseded");
            manifest = await ReadManifestAsync(id, head.Manifest, cancellationToken).ConfigureAwait(false);
            if (!SameBinding(manifest.Source, expectedSource)) throw RecoveryWire.Reject(RecoveryFailure.SourceChanged, "Recovery.SourceChanged");
            await VerifyExpectedSourceAsync(expectedSource, cancellationToken).ConfigureAwait(false);
            var audit = await new RecoveryAuditJournal(Part("audit", manifest.SessionId, ".log"), manifest.SessionId).ReadAsync(manifest.Audit, cancellationToken).ConfigureAwait(false);
            var generation = dependencies?.Generation ?? 0;
            var restored = await RecoverySnapshotCodec.DecodeAsync(manifest, _blobs, dependencies, cancellationToken).ConfigureAwait(false);
            if (dependencies is not null && dependencies.Generation != generation) throw RecoveryWire.Reject(RecoveryFailure.DependencyChanged, "Recovery.DependencyGenerationChanged");
            await VerifyExpectedSourceAsync(expectedSource, cancellationToken).ConfigureAwait(false);
            var latest = RecoveryWire.Decode<Head>(await RecoveryWire.ReadAsync(Part("heads", manifest.SessionId, ".json"), 4096, cancellationToken).ConfigureAwait(false), 4096);
            if (latest != head) throw RecoveryWire.Reject(RecoveryFailure.StaleSession, "Recovery.CheckpointSuperseded");
            var notices = new List<string>();
            if (manifest.Source.Kind == RecoveryBindingKind.UnsavedProject) notices.Add("Recovery.RequiresSaveAs");
            if (manifest.Undo.Snapshots.Any(snapshot => snapshot.DependencyCoverage == RecoveryDependencyCoverage.ModelOnly)) notices.Add("Recovery.ModelOnlyDeclarations");
            if (audit.TailDamaged) notices.Add("Recovery.AuditUnverifiedSuffix");
            if (audit.Last.Sequence > manifest.Audit.Sequence) notices.Add("Recovery.AuditAheadOfCheckpoint");
            return new RecoveryCandidate(Summary(manifest, RecoveryListingStatus.Validated, "Recovery.Validated"), notices.AsReadOnly(), generation, () => restored);
        }, cancellationToken);

    private async Task<RecoveryManifest> ReadManifestAsync(string id, RecoveryBlob? expected, CancellationToken token)
    {
        var bytes = await RecoveryWire.ReadAsync(Part("manifests", id, ".json"), RecoveryWire.ManifestLimit, token).ConfigureAwait(false);
        if (expected is not null && (expected.Length != bytes.Length || expected.Sha256 != RecoveryWire.Hash(bytes)))
            throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.ManifestHashChanged");
        var manifest = RecoveryWire.Decode<RecoveryManifest>(bytes, RecoveryWire.ManifestLimit);
        if (manifest.Schema != 1 || manifest.ProducerFormat != "buschord-session-v1") throw RecoveryWire.Reject(RecoveryFailure.VersionMismatch, "Recovery.VersionMismatch");
        if (manifest.Id != id || !RecoveryWire.IsId(manifest.SessionId) || manifest.CreatedUtc.Offset != TimeSpan.Zero || manifest.Source is null || manifest.Undo is null || manifest.Audit is null)
            throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.InvalidManifest");
        RecoveryWire.PlainText(manifest.ProjectName, 512);
        RecoveryContractValidation.ValidateBinding(manifest.Source, manifest.SessionId); RecoveryContractValidation.ValidateUndoShape(manifest.Undo, manifest.Source.Kind);
        if (manifest.Audit.Sequence < 0 || manifest.Audit.CommittedLength < 0 || !RecoveryWire.IsHash(manifest.Audit.RecordSha256)) throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.InvalidAuditAnchor");
        return manifest;
    }
    private static void ValidateHead(Head head, string session)
    {
        if (head.Schema != 1) throw RecoveryWire.Reject(RecoveryFailure.VersionMismatch, "Recovery.VersionMismatch");
        if (head.SessionId != session || !RecoveryWire.IsId(head.Id) || head.CreatedUtc.Offset != TimeSpan.Zero || head.Manifest is null
            || head.Manifest.Length is < 1 or > RecoveryWire.ManifestLimit || !RecoveryWire.IsHash(head.Manifest.Sha256))
            throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.InvalidHead");
    }
    private static RecoveryListItem Summary(RecoveryManifest manifest, RecoveryListingStatus status, string reason)
        => new(manifest.Id, manifest.SessionId, manifest.CreatedUtc, manifest.ProjectName, manifest.Source.ProjectId,
            manifest.Undo.Index != manifest.Undo.SavedIndex, manifest.Undo.Index, manifest.Undo.Snapshots.Count - manifest.Undo.Index - 1, status, reason,
            BindingKind: manifest.Source.Kind, ProjectGuid: manifest.Source.ProjectGuid);
    private static RecoveryListingStatus ListingFailure(RecoveryFailure failure) => Enum.TryParse<RecoveryListingStatus>(failure.ToString(), out var value) ? value : RecoveryListingStatus.Corrupt;
    private static bool SameBinding(RecoverySource left, RecoverySource right) => left.Kind == right.Kind && left.ProjectId == right.ProjectId
        && left.ProjectGuid == right.ProjectGuid && left.SavedFile == right.SavedFile && left.PathIdentity == right.PathIdentity
        && string.Equals(left.FullPath, right.FullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && left.UnsavedSessionId == right.UnsavedSessionId;
    private string Part(string directory, string id, string extension) { RecoveryWire.Id(id); return Path.Combine(_root, directory, id + extension); }
    private async Task<FileStream> EnterWriterAsync(CancellationToken token)
    {
        var path = Path.Combine(_root, "writer.lock");
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested(); RecoveryWire.NoLink(_root); RecoveryWire.NoLink(path);
            try { return RecoveryStoragePermissions.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, FileOptions.None); }
            catch (IOException error)
            {
                if (started.Elapsed >= TimeSpan.FromSeconds(5)) throw RecoveryWire.Reject(RecoveryFailure.StorageBusy, "Recovery.WriterUnavailable", error);
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
    }
    private long StorageBytes(out int count)
    {
        var pending = new Stack<string>(); pending.Push(_root); count = 0; long bytes = 0;
        while (pending.TryPop(out var folder))
        {
            RecoveryWire.NoLink(folder);
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                if (++count > 60000) throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.RootEntryBudget");
                RecoveryWire.NoLink(path);
                if (Directory.Exists(path)) pending.Push(path); else bytes = checked(bytes + new FileInfo(path).Length);
                if (bytes > MaximumRootBytes) throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.RootStorageBudget");
            }
        }
        return bytes;
    }
    private void EnsureListingCapacity(string directory, string sessionId)
    {
        if (File.Exists(Part(directory, sessionId, ".json"))) return;
        var count = 0;
        foreach (var folder in new[] { "heads", "notices" })
            foreach (var _ in Directory.EnumerateFiles(Path.Combine(_root, folder), "*.json"))
                if (++count >= 4096) throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.ListingCapacity");
    }
    private static async Task VerifyExpectedSourceAsync(RecoverySource source, CancellationToken token)
    {
        if (source.Kind == RecoveryBindingKind.UnsavedProject) return;
        if (source.SavedFile is null || source.FullPath is null) throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.InvalidSource");
        if (!File.Exists(source.FullPath)) throw RecoveryWire.Reject(RecoveryFailure.SourceMissing, "Recovery.SourceMissing");
        RecoveryWire.NoLink(source.FullPath);
        await using var input = new FileStream(source.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (input.Length != source.SavedFile.Length || input.Length > RecoveryBlobStore.MaximumBlobBytes
            || Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) != source.SavedFile.Sha256)
            throw RecoveryWire.Reject(RecoveryFailure.SourceChanged, "Recovery.SourceChanged");
        input.Position = 0; var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (RecoveryWire.Hash(bytes) != source.SavedFile.Sha256) throw RecoveryWire.Reject(RecoveryFailure.SourceChanged, "Recovery.SourceChanged");
        var actual = RecoveryArchives.Inspect(bytes, null, inferNested: true);
        if (actual.ProjectId != source.ProjectId || actual.ProjectGuid != source.ProjectGuid) throw RecoveryWire.Reject(RecoveryFailure.ProjectIdentityMismatch, "Recovery.ProjectIdentityMismatch");
    }
}
