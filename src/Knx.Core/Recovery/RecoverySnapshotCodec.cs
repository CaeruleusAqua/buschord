using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Runtime.CompilerServices;
using Knx.Catalog;
using Knx.Workbench;

namespace Knx.Recovery;

internal static class RecoverySnapshotCodec
{
    private static readonly ConditionalWeakTable<byte[], RecoveryArchiveInspection> Inspections = new();
    private static RecoveryArchiveInspection Inspect(ArchiveState archive) => Inspections.GetValue(archive.Bytes, data => RecoveryArchives.Inspect(data, archive.NestedEntry));
    internal static void CheckPlainSnapshots(IReadOnlyList<RecoveryCapturedSnapshot> snapshots)
        => CheckPlainArchives(snapshots.Select(snapshot => snapshot.Archive));
    internal static void CheckPlainArchives(IEnumerable<ArchiveState?> archives)
    {
        var seen = new HashSet<ArchiveState>(ReferenceEqualityComparer.Instance);
        var buffers = new HashSet<byte[]>(ReferenceEqualityComparer.Instance); long inputBytes = 0;
        foreach (var archive in archives)
        {
            if (archive is null || !seen.Add(archive)) continue;
            if (archive.Encrypted) throw Fail(RecoveryFailure.ProtectedSource, "Recovery.ProtectedSource");
            foreach (var bytes in new[] { archive.Bytes }.Concat(archive.ApplicationParts?.Values ?? []).Concat(archive.GeneratedParts?.Values ?? []))
            {
                if (bytes is null) throw Fail(RecoveryFailure.Corrupt, "Recovery.NullArchivePart");
                if (buffers.Add(bytes)) inputBytes = checked(inputBytes + bytes.LongLength);
                if (inputBytes > 256L * 1024 * 1024) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.HydrationArchiveBudget");
            }
            var inspected = Inspect(archive);
            if (inspected.NestedEntry != archive.NestedEntry) throw Fail(RecoveryFailure.Corrupt, "Recovery.ArchiveNestedChanged");
            if (archive.Layout is { } layout && layout.Prefix != inspected.Layout?.Prefix)
                throw Fail(RecoveryFailure.Corrupt, "Recovery.ArchiveLayoutChanged");
            if (!SameIds(archive.NativeObjectIds, inspected.NativeIds)) throw Fail(RecoveryFailure.Corrupt, "Recovery.NativeSourceIdsChanged");
        }
    }

    internal static async Task<UndoRecoveryState> EncodeAsync(RecoveryExport export, RecoveryBlobStore blobs, CancellationToken token, long maximumAdditionalBytes, int maximumAdditionalFiles)
    {
        var writer = new Writer(blobs, maximumAdditionalBytes, maximumAdditionalFiles); var snapshots = new List<RecoverySnapshot>();
        var archives = new Dictionary<ArchiveState, RecoveryBlob>(ReferenceEqualityComparer.Instance);
        long xmlBytes = 0; long factBytes = 0;
        foreach (var captured in export.Snapshots)
        {
            token.ThrowIfCancellationRequested();
            var project = Hydrate(captured); ValidateIdentity(project, export.Source);
            var xml = Encoding.UTF8.GetBytes(captured.Xml);
            xmlBytes = checked(xmlBytes + xml.Length);
            if (xml.Length > 16 * 1024 * 1024 || xmlBytes > 64L * 1024 * 1024) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.SnapshotXmlBudget");
            var roundTrip = Knxproj.ToXml(project);
            if (!SameWireXml(RecoveryArchives.ReadXml(xml), roundTrip)) throw Fail(RecoveryFailure.Corrupt, "Recovery.SnapshotRoundTripChanged");
            RecoveryBlob? archiveBlob = null;
            if (captured.Archive is { } archive)
            {
                if (!archives.TryGetValue(archive, out archiveBlob))
                {
                    var inspection = Inspect(archive);
                    var envelope = new RecoveryArchive(1, await writer.Add(archive.Bytes, token, true).ConfigureAwait(false), inspection.NestedEntry,
                        inspection.Layout?.Prefix, inspection.Format, inspection.NativeIds?.Order(StringComparer.Ordinal).ToArray(),
                        await WriteParts(archive.ApplicationParts, writer, token).ConfigureAwait(false), await WriteParts(archive.GeneratedParts, writer, token).ConfigureAwait(false),
                        archive.ApplicationParts is not null, archive.GeneratedParts is not null);
                    archiveBlob = await writer.Add(RecoveryWire.Encode(envelope, RecoveryWire.ManifestLimit), token).ConfigureAwait(false);
                    archives.Add(archive, archiveBlob);
                }
            }
            var facts = RecoveryWire.Encode(captured.Hardware, 32 * 1024 * 1024);
            var texts = RecoveryWire.Encode(captured.Texts, 32 * 1024 * 1024);
            factBytes = checked(factBytes + facts.Length + texts.Length);
            if (factBytes > 32L * 1024 * 1024) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.FactBudget");
            var inventory = captured.RecordedDependencies ?? ModelInventory(project);
            ValidateInventory(project, inventory);
            inventory = FreezeInventory(inventory);
            foreach (var requirement in inventory.Requirements)
            {
                writer.Declare(requirement.Program);
                foreach (var resource in requirement.RequiredResources) writer.Declare(resource.Blob);
            }
            foreach (var requirement in inventory.Requirements.Where(requirement => requirement.Kind == RecoveryDependencyKind.Embedded))
            {
                var path = Knxproj.ApplicationProgramEntryPath(requirement.ProgramId) ?? throw Fail(RecoveryFailure.Corrupt, "Recovery.InvalidProgramId");
                var raw = RawPart(captured.Archive, path) ?? throw Fail(RecoveryFailure.CatalogMissing, "Recovery.EmbeddedProgramMissing");
                VerifyBytes(raw, requirement.Program); VerifyProgramIdentity(raw, requirement.ProgramId);
                await writer.Add(raw, token).ConfigureAwait(false);
                foreach (var resource in requirement.RequiredResources)
                {
                    var bytes = RawPart(captured.Archive, resource.EntryPath) ?? throw Fail(RecoveryFailure.CatalogMissing, "Recovery.EmbeddedResourceMissing");
                    VerifyBytes(bytes, resource.Blob); await writer.Add(bytes, token).ConfigureAwait(false);
                }
            }
            var snapshot = new RecoverySnapshot(1, await writer.Add(xml, token).ConfigureAwait(false), archiveBlob,
                await writer.Add(facts, token).ConfigureAwait(false), await writer.Add(texts, token).ConfigureAwait(false),
                inventory.Requirements.ToArray(), "", inventory.References.ToArray(), inventory.Coverage, UndoStack.RecoveryIdentity(captured));
            snapshots.Add(snapshot with { StateSha256 = StateHash(snapshot) });
        }
        return new(1, export.Capacity, export.Index, export.SavedIndex, snapshots.AsReadOnly());
    }

    internal static async Task<RestoredRecoverySession> DecodeAsync(RecoveryManifest manifest, RecoveryBlobStore blobs,
        IRecoveryDependencyResolver? dependencies, CancellationToken token)
    {
        var reader = new Reader(blobs); var captured = new List<RecoveryCapturedSnapshot>();
        var archives = new Dictionary<string, ArchiveState>(StringComparer.Ordinal);
        var checkedDependencies = new HashSet<string>(StringComparer.Ordinal);
        long factsBudget = 0;
        foreach (var snapshot in manifest.Undo.Snapshots)
        {
            token.ThrowIfCancellationRequested();
            if (snapshot.StateSha256 != StateHash(snapshot with { StateSha256 = "" })) throw Fail(RecoveryFailure.Corrupt, "Recovery.SnapshotHashChanged");
            var xmlBytes = await reader.Read(snapshot.Xml, 16 * 1024 * 1024, false, token).ConfigureAwait(false);
            var xml = RecoveryArchives.ReadXml(xmlBytes);
            ArchiveState? archive = null;
            if (snapshot.ArchiveEnvelope is { } archiveBlob)
            {
                if (!archives.TryGetValue(archiveBlob.Sha256, out archive))
                {
                    var envelope = RecoveryWire.Decode<RecoveryArchive>(await reader.Read(archiveBlob, RecoveryWire.ManifestLimit, false, token).ConfigureAwait(false), RecoveryWire.ManifestLimit);
                    if (envelope.Schema != 1) throw Fail(RecoveryFailure.VersionMismatch, "Recovery.ArchiveVersionMismatch");
                    if (envelope.Source is null || envelope.ApplicationParts is null || envelope.GeneratedParts is null) throw Fail(RecoveryFailure.Corrupt, "Recovery.InvalidArchiveEnvelope");
                    var source = await reader.Read(envelope.Source, (int)RecoveryBlobStore.MaximumBlobBytes, true, token).ConfigureAwait(false);
                    var inspected = RecoveryArchives.Inspect(source, envelope.NestedEntry);
                    if (envelope.NativeObjectIds is { } nativeIds && nativeIds.Count != nativeIds.Distinct(StringComparer.Ordinal).Count())
                        throw Fail(RecoveryFailure.Corrupt, "Recovery.DuplicateNativeSourceId");
                    if (inspected.Format != envelope.ProjectFormat || inspected.Layout?.Prefix != envelope.LayoutPrefix
                        || !SameIds(inspected.NativeIds, envelope.NativeObjectIds)) throw Fail(RecoveryFailure.Corrupt, "Recovery.ArchiveSourceScopeChanged");
                    var applicationParts = await ReadParts(envelope.ApplicationParts, reader, token).ConfigureAwait(false);
                    var generatedParts = await ReadParts(envelope.GeneratedParts, reader, token).ConfigureAwait(false);
                    if (!envelope.HasApplicationCache && applicationParts.Count != 0) throw Fail(RecoveryFailure.Corrupt, "Recovery.ApplicationCachePresenceChanged");
                    if (!envelope.HasGeneratedParts && generatedParts.Count != 0) throw Fail(RecoveryFailure.Corrupt, "Recovery.GeneratedPartsPresenceChanged");
                    archive = new(source, inspected.NestedEntry, false, envelope.HasApplicationCache ? applicationParts : null,
                        inspected.NativeIds, envelope.HasGeneratedParts ? generatedParts : null, inspected.Layout);
                    archives.Add(archiveBlob.Sha256, archive);
                }
            }
            var hardwareBytes = await reader.Read(snapshot.HardwareFacts, 32 * 1024 * 1024, false, token).ConfigureAwait(false);
            var textBytes = await reader.Read(snapshot.TextDefinitions, 32 * 1024 * 1024, false, token).ConfigureAwait(false);
            factsBudget = checked(factsBudget + hardwareBytes.Length + textBytes.Length);
            if (factsBudget > 32L * 1024 * 1024) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.FactBudget");
            var hardware = RecoveryWire.Decode<RecoveryHardware[]>(hardwareBytes, 32 * 1024 * 1024);
            var texts = RecoveryWire.Decode<RecoveryObjectText[]>(textBytes, 32 * 1024 * 1024);
            var step = new RecoveryCapturedSnapshot(Encoding.UTF8.GetString(xmlBytes), archive, hardware, texts);
            var project = Hydrate(step); ValidateIdentity(project, manifest.Source);
            if (snapshot.SemanticSha256 != UndoStack.RecoveryIdentity(step)) throw Fail(RecoveryFailure.Corrupt, "Recovery.SemanticFingerprintChanged");
            var inventory = new RecoveryDependencyInventory(snapshot.Requirements, snapshot.References, snapshot.DependencyCoverage);
            ValidateInventory(project, inventory);
            if (!SameWireXml(xml, Knxproj.ToXml(project))) throw Fail(RecoveryFailure.Corrupt, "Recovery.SnapshotRoundTripChanged");
            foreach (var requirement in snapshot.Requirements)
            {
                var identity = RecoveryWire.Hash(RecoveryWire.Encode(requirement, RecoveryWire.ManifestLimit));
                var checkedIdentity = requirement.Kind == RecoveryDependencyKind.Embedded ? snapshot.ArchiveEnvelope?.Sha256 + ":" + identity : identity;
                if (!checkedDependencies.Add(checkedIdentity)) continue;
                if (requirement.Kind == RecoveryDependencyKind.Embedded)
                {
                    var path = Knxproj.ApplicationProgramEntryPath(requirement.ProgramId) ?? throw Fail(RecoveryFailure.Corrupt, "Recovery.InvalidProgramId");
                    var raw = RawPart(archive, path) ?? throw Fail(RecoveryFailure.CatalogMissing, "Recovery.EmbeddedProgramMissing");
                    VerifyBytes(raw, requirement.Program); VerifyProgramIdentity(raw, requirement.ProgramId);
                    // The separately retained content must match the source/overlay, not just its declared hash.
                    await reader.Read(requirement.Program, (int)RecoveryBlobStore.MaximumBlobBytes, false, token).ConfigureAwait(false);
                    foreach (var resource in requirement.RequiredResources)
                    {
                        VerifyBytes(RawPart(archive, resource.EntryPath) ?? throw Fail(RecoveryFailure.CatalogMissing, "Recovery.EmbeddedResourceMissing"), resource.Blob);
                        await reader.Read(resource.Blob, (int)RecoveryBlobStore.MaximumBlobBytes, false, token).ConfigureAwait(false);
                    }
                }
                else
                {
                    if (dependencies is null) throw Fail(RecoveryFailure.CatalogMissing, "Recovery.CatalogMissing");
                    await using var program = await dependencies.OpenProgramAsync(requirement, token).ConfigureAwait(false)
                        ?? throw Fail(RecoveryFailure.CatalogMissing, "Recovery.CatalogProgramMissing");
                    var raw = await ReadExternal(program, requirement.Program, reader, token).ConfigureAwait(false); VerifyProgramIdentity(raw, requirement.ProgramId);
                    foreach (var resource in requirement.RequiredResources)
                    {
                        await using var input = await dependencies.OpenResourceAsync(requirement, resource, token).ConfigureAwait(false)
                            ?? throw Fail(RecoveryFailure.CatalogMissing, "Recovery.CatalogResourceMissing");
                        await ReadExternal(input, resource.Blob, reader, token).ConfigureAwait(false);
                    }
                }
            }
            captured.Add(step); // Validated XML strings + shared archive bytes; discard this hydrated model.
        }
        return UndoStack.CreateRecoverySession(manifest.Undo, manifest.Source, captured);
    }

    internal static Project Hydrate(RecoveryCapturedSnapshot snapshot)
    {
        try { return HydrateCore(snapshot); }
        catch (Exception error) when (error is InvalidDataException or XmlException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        { throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.InvalidSnapshot", error); }
    }
    private static Project HydrateCore(RecoveryCapturedSnapshot snapshot)
    {
        var document = RecoveryArchives.ReadXml(Encoding.UTF8.GetBytes(snapshot.Xml));
        var project = Knxproj.FromXml(document);
        project.ArchiveState = snapshot.Archive;
        if (snapshot.Hardware.Count > 100000 || snapshot.Texts.Count > 100000) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.FactCountLimit");
        foreach (var fact in snapshot.Hardware)
        {
            if (fact is null || string.IsNullOrEmpty(fact.Key) || fact.MediumTypeIds is null || fact.MediumTypeIds.Count > 64
                || fact.BusCurrent is { } current && !double.IsFinite(current)) throw Fail(RecoveryFailure.Corrupt, "Recovery.InvalidHardwareFact");
            RecoveryWire.PlainText(fact.Key, 1024); RecoveryWire.PlainText(fact.HardwareId, 1024); RecoveryWire.PlainText(fact.Name, 65536);
            foreach (var id in fact.MediumTypeIds) RecoveryWire.PlainText(id, 256);
            if (!project.Hardware.TryAdd(fact.Key, new() { HardwareId = fact.HardwareId, Name = fact.Name, BusCurrent = fact.BusCurrent,
                IsPowerSupply = fact.IsPowerSupply, IsChoke = fact.IsChoke, IsCoupler = fact.IsCoupler, IsPowerLineRepeater = fact.IsPowerLineRepeater,
                IsIpEnabled = fact.IsIpEnabled, MediumTypeIds = fact.MediumTypeIds.ToArray() })) throw Fail(RecoveryFailure.Corrupt, "Recovery.DuplicateHardwareFact");
        }
        foreach (var text in snapshot.Texts)
        {
            if (text is null) throw Fail(RecoveryFailure.Corrupt, "Recovery.NullTextDefinition");
            RecoveryWire.PlainText(text.DeviceId, 1024); RecoveryWire.PlainText(text.ObjectRefId, 1024);
            foreach (var value in new[] { text.Text, text.FunctionText, text.ReferenceId, text.DefaultValue, text.ArgumentValue, text.ChannelId, text.ChannelName, text.ChannelDeclarationId, text.NativeChannelId })
                RecoveryWire.PlainText(value, 65536, true);
            if (text.NamedArguments is null && (text.HasArgumentValue || text.ArgumentValue is not null)
                || !text.HasChannelDefinition && new[] { text.ChannelId, text.ChannelName, text.ChannelDeclarationId, text.NativeChannelId }.Any(value => value is not null))
                throw Fail(RecoveryFailure.Corrupt, "Recovery.InvalidTextBinding");
            TextArgumentBinding? arguments = null;
            if (text.NamedArguments is { } named)
            {
                if (named.Count > 4096) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.TextArgumentCount");
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var argument in named)
                {
                    if (argument is null) throw Fail(RecoveryFailure.Corrupt, "Recovery.NullTextArgument");
                    RecoveryWire.PlainText(argument.Name, 1024); RecoveryWire.PlainText(argument.Value, 65536);
                    if (!values.TryAdd(argument.Name, argument.Value)) throw Fail(RecoveryFailure.Corrupt, "Recovery.DuplicateTextArgument");
                }
                arguments = new(values, text.HasArgumentValue, text.ArgumentValue);
            }
            var channel = text.HasChannelDefinition ? new ObjectChannelDefinition(text.ChannelId, text.ChannelName, text.ChannelDeclarationId, text.NativeChannelId) : null;
            if (!project.ObjectTextDefinitions.TryAdd(new(text.DeviceId, text.ObjectRefId), new(text.Text, text.FunctionText, text.ReferenceId, text.DefaultValue, text.KnownReference, arguments, channel)))
                throw Fail(RecoveryFailure.Corrupt, "Recovery.DuplicateTextDefinition");
        }
        foreach (var device in ProjectQuery.DeviceList(project)) ApplicationTexts.RefreshObjects(project, device);
        RestoreWorkbenchSnapshot(project, document);
        return project;
    }

    // FromXml refreshes derived channel facts. A complete Undo snapshot must retain the
    // explicitly recorded Workbench state after those refreshes, including declaration facts.
    // Only the existing public, version-1 Workbench contract is decoded; no CLR type names,
    // archive internals, polymorphic metadata or arbitrary annotations are accepted.
    internal static void RestoreWorkbenchSnapshot(Project project, XDocument document, bool bounded = true)
    {
        var root = document.Root ?? throw Fail(RecoveryFailure.Corrupt, "Recovery.MissingSnapshotRoot");
        var body = root.Element(root.Name.Namespace + "Project") ?? throw Fail(RecoveryFailure.Corrupt, "Recovery.MissingSnapshotProject");
        var entries = body.Elements(Knxproj.ExtensionNamespace + "Workbench").Take(bounded ? 2 : 1).ToArray();
        if (entries.Length > 1) throw Fail(RecoveryFailure.Corrupt, "Recovery.AmbiguousWorkbenchState");
        if (entries.Length == 0) { project.Workbench = new(); return; }
        var entry = entries[0];
        if ((int?)entry.Attribute("Version") != 1) throw Fail(RecoveryFailure.VersionMismatch, "Recovery.WorkbenchVersionMismatch");
        var json = (string?)entry.Attribute("Data") ?? "";
        var metadata = bounded ? RecoveryWire.Decode<WorkbenchMetadata>(Encoding.UTF8.GetBytes(json), 16 * 1024 * 1024)
            : System.Text.Json.JsonSerializer.Deserialize<WorkbenchMetadata>(json) ?? throw Fail(RecoveryFailure.Corrupt, "Recovery.InvalidWorkbenchState");
        metadata.Validate();
        // Ordinary Undo has already passed Core's normal XML/parser contract. Do not
        // impose the optional recovery checkpoint limits on existing larger projects.
        if (!bounded) { project.Workbench = metadata; return; }
        if (metadata.Objects.Count > 100000 || metadata.Devices.Count > 100000 || metadata.Groups.Count > 100000 || metadata.FloorShortNames.Count > 100000)
            throw Fail(RecoveryFailure.LimitExceeded, "Recovery.WorkbenchCountLimit");
        foreach (var (key, value) in metadata.Objects)
        {
            RecoveryWire.PlainText(key, 1024);
            foreach (var text in new[] { value.Dpt, value.Flags, value.ChannelId, value.StructuralDeclaredChannelId, value.StructuralDeclaredChannelName, value.Role, value.SendingGroupId })
                RecoveryWire.PlainText(text, 65536, true);
            if (value.Connectors.Count > 100000) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.ConnectorCountLimit");
            foreach (var group in value.Connectors.Keys) RecoveryWire.PlainText(group, 1024);
        }
        foreach (var (key, value) in metadata.Devices) { RecoveryWire.PlainText(key, 1024); RecoveryWire.PlainText(value.Application, 65536, true); }
        foreach (var (key, value) in metadata.Groups) { RecoveryWire.PlainText(key, 1024); RecoveryWire.PlainText(value.EffectRoomId, 1024, true); }
        foreach (var (key, value) in metadata.FloorShortNames) { RecoveryWire.PlainText(key, 1024); RecoveryWire.PlainText(value, 65536); }
        project.Workbench = metadata;
    }

    internal static RecoveryHardware Hardware(string key, CatalogHardware value) => new(key, value.HardwareId, value.Name, value.BusCurrent,
        value.IsPowerSupply, value.IsChoke, value.IsCoupler, value.IsPowerLineRepeater, value.IsIpEnabled, value.MediumTypeIds.ToArray());
    internal static RecoveryObjectText Text(ObjectKey key, ObjectTextDefinition value) => new(key.DeviceId, key.ObjectRefId, value.Text,
        value.FunctionText, value.ReferenceId, value.DefaultValue, value.KnownReference,
        value.Arguments?.Named.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new RecoveryTextArgument(pair.Key, pair.Value)).ToArray(),
        value.Arguments?.HasValue ?? false, value.Arguments?.Value, value.Channel is not null, value.Channel?.Id, value.Channel?.Name,
        value.Channel?.DeclarationId, value.Channel?.NativeChannelId);
    internal static string StateHash(RecoverySnapshot snapshot) => RecoveryWire.Hash(RecoveryWire.Encode(snapshot with { StateSha256 = "" }, RecoveryWire.ManifestLimit));
    // Generated elements acquire implicit xmlns declarations only when serialized.
    // Compare two parsed wire documents so that an identical native Workbench element
    // is not rejected merely because one in-memory declaration was synthesized.
    private static bool SameWireXml(XDocument original, XDocument generated) => XNode.DeepEquals(original,
        RecoveryArchives.ReadXml(Encoding.UTF8.GetBytes(generated.ToString(SaveOptions.DisableFormatting))));
    internal static RecoveryDependencyInventory ModelInventory(Project project) => new([], ProjectQuery.DeviceList(project)
        .OrderBy(device => device.Id, StringComparer.Ordinal).Select(device => new RecoveryReference(device.Id, device.ProductRefId, device.Hardware2ProgramRefId, null, null)).ToArray(),
        ProjectQuery.DeviceList(project).Any() ? RecoveryDependencyCoverage.ModelOnly : RecoveryDependencyCoverage.RecordedRequirements);
    internal static RecoveryDependencyInventory FreezeInventory(RecoveryDependencyInventory inventory) => inventory with
    {
        Requirements = Array.AsReadOnly(inventory.Requirements.Select(requirement => requirement with { RequiredResources = Array.AsReadOnly(requirement.RequiredResources.ToArray()) }).ToArray()),
        References = Array.AsReadOnly(inventory.References.ToArray())
    };
    internal static void ValidateInventory(Project project, RecoveryDependencyInventory inventory)
    {
        if (inventory is null || inventory.Requirements is null || inventory.References is null || !Enum.IsDefined(inventory.Coverage)
            || inventory.Requirements.Count > 4096 || inventory.References.Count > 100000) throw Fail(RecoveryFailure.Corrupt, "Recovery.InvalidDependencyInventory");
        var programs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in inventory.Requirements)
        {
            if (requirement is null || !Enum.IsDefined(requirement.Kind) || !programs.Add(requirement.ProgramId)) throw Fail(RecoveryFailure.Corrupt, "Recovery.InvalidProgramRequirement");
            RecoveryWire.PlainText(requirement.ProgramId, 256); RecoveryContractValidation.Blob(requirement.Program);
            if (requirement.RequiredResources is null || requirement.RequiredResources.Count > 4096) throw Fail(RecoveryFailure.Corrupt, "Recovery.InvalidResourceRequirements");
            var resources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var resource in requirement.RequiredResources)
            {
                if (resource is null || !resources.Add(resource.EntryPath)) throw Fail(RecoveryFailure.Corrupt, "Recovery.DuplicateResourceRequirement");
                PartName(resource.EntryPath); RecoveryContractValidation.Blob(resource.Blob);
            }
        }
        var devices = ProjectQuery.DeviceList(project).ToDictionary(device => device.Id, StringComparer.Ordinal);
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in inventory.References)
        {
            if (reference is null) throw Fail(RecoveryFailure.Corrupt, "Recovery.NullReferenceFingerprint");
            RecoveryWire.PlainText(reference.DeviceId, 1024); RecoveryWire.PlainText(reference.ProductRefId, 1024);
            if (!references.Add(reference.DeviceId) || !devices.TryGetValue(reference.DeviceId, out var device)
                || device.ProductRefId != reference.ProductRefId || device.Hardware2ProgramRefId != reference.Hardware2ProgramRefId)
                throw Fail(RecoveryFailure.DependencyChanged, "Recovery.ReferenceFingerprintChanged");
            if ((reference.ApplicationProgramId is null) != (reference.ProgramSha256 is null)) throw Fail(RecoveryFailure.Corrupt, "Recovery.IncompleteProgramFingerprint");
            if (reference.ApplicationProgramId is not null && !inventory.Requirements.Any(requirement => requirement.ProgramId == reference.ApplicationProgramId && requirement.Program.Sha256 == reference.ProgramSha256))
                throw Fail(RecoveryFailure.DependencyUnknown, "Recovery.ProgramFingerprintWithoutRequirement");
        }
        if (references.Count != devices.Count) throw Fail(RecoveryFailure.DependencyChanged, "Recovery.ReferenceFingerprintMissing");
        if (inventory.Coverage == RecoveryDependencyCoverage.RecordedRequirements && inventory.References.Any(reference => reference.ApplicationProgramId is null))
            throw Fail(RecoveryFailure.DependencyUnknown, "Recovery.ModelOnlyCoverageRequired");
    }
    private static void ValidateIdentity(Project project, RecoverySource source)
    {
        if (project.Id != source.ProjectId || project.Guid != source.ProjectGuid) throw Fail(RecoveryFailure.ProjectIdentityMismatch, "Recovery.ProjectIdentityMismatch");
    }
    private static bool SameIds(IEnumerable<string>? first, IEnumerable<string>? second) => first is null ? second is null
        : second is not null && new HashSet<string>(first, StringComparer.Ordinal).SetEquals(second);
    private static async Task<IReadOnlyList<RecoveryPart>> WriteParts(IReadOnlyDictionary<string, byte[]>? parts, Writer writer, CancellationToken token)
    {
        if (parts is null) return [];
        if (parts.Count > 4096 || parts.Values.Sum(value => (long)value.Length) > 128L * 1024 * 1024) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.OverlayBudget");
        var result = new List<RecoveryPart>();
        foreach (var (path, bytes) in parts.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { PartName(path); result.Add(new(path, await writer.Add(bytes, token, true).ConfigureAwait(false))); }
        return result.AsReadOnly();
    }
    private static async Task<Dictionary<string, byte[]>> ReadParts(IReadOnlyList<RecoveryPart> parts, Reader reader, CancellationToken token)
    {
        if (parts.Count > 4096) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.OverlayBudget");
        long size = 0; var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (part is null) throw Fail(RecoveryFailure.Corrupt, "Recovery.NullArchivePart"); PartName(part.EntryPath);
            RecoveryContractValidation.Blob(part.Blob); size = checked(size + part.Blob.Length);
            if (size > 128L * 1024 * 1024 || !result.TryAdd(part.EntryPath, await reader.Read(part.Blob, ArchiveEntryLimits.For(part.EntryPath), true, token).ConfigureAwait(false)))
                throw Fail(RecoveryFailure.Corrupt, "Recovery.DuplicateOrExcessiveArchivePart");
        }
        return result;
    }
    private static void PartName(string path)
    {
        RecoveryWire.PlainText(path, 1024);
        var parts = path.Split('/');
        if (path.Any(char.IsControl) || parts.Any(part => part is "" or "." or ".." || part.Contains('\\') || part.Contains(':'))
            || path != "knx_master.xml" && (parts.Length < 2 || parts[0].Length != 6 || !parts[0].StartsWith("M-", StringComparison.Ordinal) || !parts[0][2..].All(char.IsAsciiHexDigit)))
            throw Fail(RecoveryFailure.Corrupt, "Recovery.InvalidArchivePartName");
    }
    private static byte[]? RawPart(ArchiveState? archive, string path)
    {
        PartName(path); if (archive is null) return null;
        if (archive.GeneratedParts?.TryGetValue(path, out var generated) == true) return generated;
        if (archive.ApplicationParts is { } completeCache) return completeCache.TryGetValue(path, out var cached) ? cached : null;
        using var input = new MemoryStream(archive.Bytes, false); using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var entry = zip.GetEntry(path); if (entry is null) return null;
        if (entry.Length > ArchiveEntryLimits.For(path)) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.PartTooLarge");
        using var content = entry.Open(); var bytes = new byte[checked((int)entry.Length)]; content.ReadExactly(bytes);
        if (content.ReadByte() != -1) throw Fail(RecoveryFailure.Corrupt, "Recovery.PartLengthChanged"); return bytes;
    }
    private static void VerifyBytes(byte[] bytes, RecoveryBlob expected)
    { if (bytes.LongLength != expected.Length || RecoveryWire.Hash(bytes) != expected.Sha256) throw Fail(RecoveryFailure.DependencyChanged, "Recovery.DependencyContentChanged"); }
    private static void VerifyProgramIdentity(byte[] bytes, string id)
    {
        using var input = new MemoryStream(bytes, false); using var xml = XmlReader.Create(input,
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = RecoveryBlobStore.MaximumBlobBytes });
        var count = 0; var nodes = 0;
        while (xml.Read())
        {
            nodes = checked(nodes + 1 + xml.AttributeCount);
            if (xml.Depth > 128 || nodes > 1000000) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.ProgramXmlComplexity");
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "ApplicationProgram")
            { if (++count != 1 || xml.GetAttribute("Id") != id) throw Fail(RecoveryFailure.DependencyChanged, "Recovery.ProgramIdentityChanged"); }
        }
        if (count != 1) throw Fail(RecoveryFailure.DependencyChanged, "Recovery.ProgramIdentityMissing");
    }
    private static async Task<byte[]> ReadExternal(Stream input, RecoveryBlob expected, Reader budget, CancellationToken token)
    {
        budget.ReserveExternal(expected); var bytes = new byte[checked((int)expected.Length)];
        try { await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false); }
        catch (EndOfStreamException) { throw Fail(RecoveryFailure.DependencyChanged, "Recovery.DependencyLengthChanged"); }
        if (await input.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw Fail(RecoveryFailure.DependencyChanged, "Recovery.DependencyLengthChanged");
        VerifyBytes(bytes, expected); return bytes;
    }
    private sealed class Writer(RecoveryBlobStore blobs, long maximumAdditionalBytes, int maximumAdditionalFiles)
    {
        private readonly Dictionary<byte[], RecoveryBlob> _arrays = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, RecoveryBlob> _hashes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _identities = new(StringComparer.Ordinal);
        private readonly HashSet<string> _shared = new(StringComparer.Ordinal);
        private long _bytes, _sharedBytes, _addedBytes; private int _addedFiles;
        internal void Declare(RecoveryBlob blob)
        {
            RecoveryContractValidation.Blob(blob);
            if (_identities.TryGetValue(blob.Sha256, out var length))
            { if (length != blob.Length) throw Fail(RecoveryFailure.Corrupt, "Recovery.BlobLengthChanged"); return; }
            _bytes = checked(_bytes + blob.Length);
            if (_bytes > RecoveryWire.UniqueBlobLimit) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.UniqueBlobBudget");
            _identities.Add(blob.Sha256, blob.Length);
        }
        internal async Task<RecoveryBlob> Add(byte[] bytes, CancellationToken token, bool share = false)
        {
            if (_arrays.TryGetValue(bytes, out var prior)) return prior;
            var blob = new RecoveryBlob(RecoveryWire.Hash(bytes), bytes.LongLength);
            Declare(blob);
            if (share && _shared.Add(blob.Sha256))
            {
                _sharedBytes = checked(_sharedBytes + blob.Length);
                if (_sharedBytes > 256L * 1024 * 1024) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.HydrationArchiveBudget");
            }
            if (!_hashes.TryGetValue(blob.Sha256, out var stored))
            {
                if (!blobs.Exists(blob))
                {
                    _addedBytes = checked(_addedBytes + blob.Length);
                    if (_addedBytes > maximumAdditionalBytes || ++_addedFiles > maximumAdditionalFiles) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.RootStorageBudget");
                }
                using var input = new MemoryStream(bytes, false); stored = await blobs.AddAsync(input, bytes.Length, token).ConfigureAwait(false);
                _hashes.Add(stored.Sha256, stored);
            }
            if (share) _arrays.Add(bytes, stored); return stored;
        }
    }
    private sealed class Reader(RecoveryBlobStore blobs)
    {
        private readonly Dictionary<string, byte[]> _shared = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _identities = new(StringComparer.Ordinal);
        private long _sharedBytes, _uniqueBytes, _readBytes;
        private void Track(RecoveryBlob blob)
        {
            RecoveryContractValidation.Blob(blob);
            if (_identities.TryGetValue(blob.Sha256, out var length))
            { if (length != blob.Length) throw Fail(RecoveryFailure.Corrupt, "Recovery.BlobLengthChanged"); return; }
            _uniqueBytes = checked(_uniqueBytes + blob.Length);
            if (_uniqueBytes > RecoveryWire.UniqueBlobLimit) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.UniqueBlobBudget");
            _identities.Add(blob.Sha256, blob.Length);
        }
        private void Charge(long bytes)
        {
            _readBytes = checked(_readBytes + bytes);
            if (_readBytes > 8L * 1024 * 1024 * 1024) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.ReadWorkBudget");
        }
        internal void ReserveExternal(RecoveryBlob blob) { Track(blob); Charge(blob.Length); }
        internal async Task<byte[]> Read(RecoveryBlob blob, int maximum, bool share, CancellationToken token)
        {
            Track(blob);
            if (blob.Length > maximum) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.BlobReadBudget");
            if (share && _shared.TryGetValue(blob.Sha256, out var previous))
            { if (previous.LongLength != blob.Length) throw Fail(RecoveryFailure.Corrupt, "Recovery.SharedBlobLengthChanged"); return previous; }
            if (share && checked(_sharedBytes + blob.Length) > 256L * 1024 * 1024) throw Fail(RecoveryFailure.LimitExceeded, "Recovery.HydrationArchiveBudget");
            Charge(checked(blob.Length * 2)); // Complete verification and then the bounded content read.
            await using var input = await blobs.OpenVerifiedAsync(blob, token).ConfigureAwait(false); var bytes = new byte[checked((int)blob.Length)];
            await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (share) { _shared.Add(blob.Sha256, bytes); _sharedBytes += bytes.Length; } return bytes;
        }
    }
    private static RecoveryRejectedException Fail(RecoveryFailure failure, string code) => RecoveryWire.Reject(failure, code);
}
