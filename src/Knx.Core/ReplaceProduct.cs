using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Knx.Workbench;
using Knx.Catalog;

namespace Knx;

public sealed record ProductReplaceResult(DeviceInstance Device, string OldRefId, string NewRefId, int KeptLinks);
public sealed record ProductParameterMigration(string SourceId, string TargetId, string Value);
public sealed record ProductObjectMigration(string SourceId, string TargetId, int OldNumber, int NewNumber, int KeptLinks)
{
    public bool Added => SourceId.Length == 0;
}

/// <summary>Detached preview. Apply uses private prepared data, never mutable preview rows.</summary>
public sealed class ProductMigrationPlan
{
    public string DeviceId { get; }
    public string DeviceAddress { get; }
    public string DeviceName { get; }
    public string OldProductRefId { get; }
    public string NewProductRefId { get; }
    public string NewHardware2ProgramRefId { get; }
    public IReadOnlyList<ProductParameterMigration> Parameters { get; }
    public IReadOnlyList<ProductObjectMigration> Objects { get; }
    internal Project Project { get; }
    internal DeviceInstance Device { get; }
    internal string Baseline { get; }
    internal ArchiveState? OriginalArchive { get; }
    internal XDocument SourceApplication { get; }
    internal XDocument TargetApplication { get; }
    internal IReadOnlyDictionary<string, byte[]> TargetParts { get; }
    internal Action Commit { get; }
    internal bool Applied { get; set; }

    internal ProductMigrationPlan(Project project, DeviceInstance device, string product, string program, string baseline,
        IEnumerable<ProductParameterMigration> parameters, IEnumerable<ProductObjectMigration> objects, Action commit,
        XDocument sourceApplication, XDocument targetApplication, IReadOnlyDictionary<string, byte[]> targetParts)
    {
        Project = project; Device = device; DeviceId = device.Id; DeviceAddress = device.Address.ToString(); DeviceName = device.Name ?? ""; OldProductRefId = device.ProductRefId;
        NewProductRefId = product; NewHardware2ProgramRefId = program; Baseline = baseline; Commit = commit;
        OriginalArchive = project.ArchiveState; SourceApplication = new(sourceApplication); TargetApplication = new(targetApplication);
        TargetParts = targetParts;
        Parameters = Array.AsReadOnly(parameters.ToArray()); Objects = Array.AsReadOnly(objects.ToArray());
    }
}

/// <summary>
/// Validated product/application migration. Legacy Replace is retained for discovery and true no-ops;
/// changing references requires a detached Preview and Apply with resolved manufacturer definitions.
/// </summary>
public static class ProductReplacer
{
    /// <summary>
    /// Caller supplies the resolved source/target XML and exact target product/program references.
    /// Mappings use complete declaration IDs. Only identical complete IDs map automatically;
    /// names, numbers and suffixes never establish identity. Every saved value/object must migrate.
    /// Unsupported or unmapped state is rejected, never discarded. No project mutation.
    /// </summary>
    public static ProductMigrationPlan Preview(Project project, DeviceInstance device, string newProductRefId,
        string newHardware2ProgramRefId, XDocument sourceApplication, XDocument targetApplication,
        IReadOnlyDictionary<string, string>? parameterMappings = null, IReadOnlyDictionary<string, string>? objectMappings = null,
        IReadOnlyDictionary<string, string>? channelMappings = null, bool? targetSecureCapable = null, IParameterScriptRuntime? scripts = null,
        IReadOnlyDictionary<string, byte[]>? targetParts = null)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(device);
        if (ProjectQuery.DeviceList(project).Count(d => ReferenceEquals(d, device)) != 1
            || ProjectQuery.DeviceList(project).Count(d => d.Id == device.Id) != 1) Fail("Device identity is missing or ambiguous.");
        Required(newProductRefId, "Target product reference"); Required(newHardware2ProgramRefId, "Target program reference");
        if (targetParts is not null && (targetParts.Count > 4096 || targetParts.Any(p => p.Value is null || p.Value.LongLength > ArchiveEntryLimits.For(p.Key))
            || targetParts.Values.Sum(p => p.LongLength) > 256L * 1024 * 1024)) Fail("Target manufacturer parts exceed the archive safety limits.");
        var baseline = Fingerprint(project);
        var sourceDocument = new XDocument(sourceApplication); var targetDocument = new XDocument(targetApplication);
        ValidateProfile(sourceDocument); ValidateProfile(targetDocument);
        var parts = new System.Collections.ObjectModel.ReadOnlyDictionary<string, byte[]>((targetParts ?? new Dictionary<string, byte[]>())
            .ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal));
        var preparedArchive = parts.Count == 0 ? project.ArchiveState : ArchiveState.WithManufacturerParts(project.ArchiveState, parts);
        preparedArchive = ArchiveState.WithApplications(preparedArchive, sourceDocument, targetDocument);
        var sourceProgram = ProgramId(sourceDocument); var targetProgram = ProgramId(targetDocument);
        var binding = new Project { Id = project.Id, Name = project.Name, ArchiveState = preparedArchive };
        ValidateBinding(binding, device.Hardware2ProgramRefId ?? device.ProductRefId, sourceProgram);
        ValidateBinding(binding, newHardware2ProgramRefId, targetProgram);
        _ = PrepareHardware(project, preparedArchive);
        if (newProductRefId != targetProgram)
        {
            var hardware = newProductRefId.Length >= 6 ? ArchiveState.LoadPart(preparedArchive, newProductRefId[..6] + "/Hardware.xml") : null;
            var products = hardware?.Descendants().Where(e => e.Name.LocalName == "Product" && (string?)e.Attribute("Id") == newProductRefId).ToArray() ?? [];
            if (products.Length != 1 || products[0].Ancestors().FirstOrDefault(e => e.Name.LocalName == "Hardware")?.Descendants()
                .Any(e => e.Name.LocalName == "Hardware2Program" && (string?)e.Attribute("Id") == newHardware2ProgramRefId) != true)
                Fail("Target product/hardware relationship is unproven; retain its original Hardware.xml.");
        }
        // Native saved IDs may omit the application prefix. Only the actual expanded
        // declaration resolves that representation; Resolve below refuses ambiguous forms.
        var sourceInputs = new Dictionary<string, string>(device.ParameterValues, StringComparer.Ordinal);
        foreach (var (id, value) in device.ParameterValues)
            if (!id.StartsWith(sourceProgram + "_", StringComparison.Ordinal)) sourceInputs.TryAdd(sourceProgram + "_" + id, value);
        var source = Snapshot(sourceDocument, sourceInputs, scripts);
        if (sourceProgram != targetProgram)
        {
            if (device.BinaryData.Count != 0 || device.HasOpaqueBinaryData)
                Fail("Application migration requires an explicit mapping for native binary device data.");
            NativeProject.ValidateApplicationMigrationSource(project, device, source.ModuleInstances);
        }
        var sourceParameters = Unique(source.Instances, p => p.Id, "source parameter");
        if (parameterMappings is not null && (parameterMappings.Any(p => !sourceParameters.ContainsKey(p.Key) || string.IsNullOrWhiteSpace(p.Value))
            || parameterMappings.Values.Distinct(StringComparer.Ordinal).Count() != parameterMappings.Count)) Fail("Invalid or many-to-one parameter mappings.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var parameterRows = new List<ProductParameterMigration>();
        foreach (var (savedId, value) in device.ParameterValues)
        {
            var sourceId = Resolve(savedId, sourceProgram, sourceParameters, "saved parameter");
            // A target instance may not exist until its mapped Count/Choose values rebuild it.
            var targetId = parameterMappings?.GetValueOrDefault(sourceId) ?? sourceId;
            var old = sourceParameters[sourceId];
            if (source.Evaluation.AssignedParameters.Contains(sourceId))
                Fail($"Calculated parameter {sourceId} cannot migrate as a saved override.");
            if (!ParameterValidation.TryValidate(old.ParameterType, value, out var sourceError)) Fail($"Source parameter {sourceId}: {sourceError}");
            if (!values.TryAdd(targetId, value)) Fail($"Multiple saved parameters map to {targetId}.");
            parameterRows.Add(new(sourceId, targetId, value));
        }
        // Counts and activity describe migrated values, not the target's default structure.
        var target = Snapshot(targetDocument, values, scripts);
        var targetParameters = Unique(target.Instances, p => p.Id, "target parameter");
        ValidateMap(parameterMappings, sourceParameters, targetParameters, "parameter");
        foreach (var row in parameterRows)
        {
            if (!targetParameters.TryGetValue(row.TargetId, out var parameter)) Fail($"Migrated target parameter {row.TargetId} is absent from the resulting structure.");
            Compatible(sourceParameters[row.SourceId].ParameterType, parameter!.ParameterType, row.SourceId);
            if (target.Evaluation.AssignedParameters.Contains(row.TargetId)
                || !ParameterValidation.TryValidate(parameter!.ParameterType, row.Value, out _)) Fail($"Migrated target parameter {row.TargetId} is calculated or invalid.");
        }
        var sourceObjects = ObjectFacts(source); var targetObjects = ObjectFacts(target);
        ValidateMap(objectMappings, sourceObjects, targetObjects, "object");
        var sourceChannels = Channels(source); var targetChannels = Channels(target);
        ValidateMap(channelMappings, sourceChannels, targetChannels, "channel");
        var instanceChannelTargets = new Dictionary<string, (string InstanceId, string DeclarationId)>(StringComparer.Ordinal);
        string MigrateChannelDeclaration(string id) => instanceChannelTargets.TryGetValue(id, out var instance)
            ? instance.DeclarationId : Map(Resolve(id, sourceProgram, sourceChannels, "channel"), targetChannels, channelMappings, "channel");
        string MigrateChannel(string id) => instanceChannelTargets.TryGetValue(id, out var instance)
            ? instance.InstanceId : Native(MigrateChannelDeclaration(id), targetProgram);
        string MigrateNativePin(string id)
        {
            if (!instanceChannelTargets.TryGetValue(id, out var instance)) Fail("Native object channel pin does not identify an exact source channel instance.");
            return instance.InstanceId;
        }
        var seenChannels = new HashSet<string>(StringComparer.Ordinal);
        ChannelInstance CopyChannel(ChannelInstance channel, string? expectedParent = null)
        {
            var sourceId = Resolve(channel.RefId ?? channel.Id, sourceProgram, sourceChannels, "channel");
            var targetId = Map(sourceId, targetChannels, channelMappings, "channel");
            if (!seenChannels.Add(targetId)) Fail("Ambiguous saved channel migration.");
            var parent = targetChannels[targetId].Ancestors().FirstOrDefault(e => e.Name.LocalName == "Channel");
            if ((string?)parent?.Attribute("Id") != expectedParent) Fail($"Channel {channel.Id} has an incompatible target hierarchy.");
            var copy = new ChannelInstance { Id = channel.RefId is null ? Native(targetId, targetProgram) : channel.Id,
                RefId = channel.RefId is null ? null : Native(targetId, targetProgram), Name = channel.Name };
            if (!instanceChannelTargets.TryAdd(channel.Id, (copy.Id, targetId))) Fail("Ambiguous native channel instance identity.");
            copy.Channels.AddRange(channel.Channels.Select(child => CopyChannel(child, targetId))); return copy;
        }
        var channels = device.Channels.Select(channel => CopyChannel(channel)).ToArray();
        var targetChannelDevice = new DeviceInstance { Id = device.Id, ProductRefId = newProductRefId };
        targetChannelDevice.Channels.AddRange(channels);
        var objects = new List<ComObjectInstanceRef>(); var objectRows = new List<ProductObjectMigration>();
        var metadata = new Dictionary<string, ObjectMetadata>(StringComparer.Ordinal);
        var texts = new Dictionary<ObjectKey, ObjectTextDefinition>();
        var consumed = new HashSet<string>(StringComparer.Ordinal); var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var old in device.ComObjectRefs)
        {
            if (!sourceIds.Add(old.Id)) Fail($"Ambiguous saved object {old.Id}.");
            var sourceId = Resolve(old.Id, sourceProgram, sourceObjects, "saved object");
            var previous = sourceObjects[sourceId];
            if (old.ComObjectId != previous.DefinitionId && old.ComObjectId != Native(previous.DefinitionId, sourceProgram)
                && old.ComObjectId != previous.Id && old.ComObjectId != Native(previous.Id, sourceProgram))
                Fail($"Saved object {old.Id} does not refer to its source declaration.");
            var targetId = Map(sourceId, targetObjects, objectMappings, "object");
            if (!consumed.Add(targetId)) Fail($"Multiple saved objects map to {targetId}.");
            var next = targetObjects[targetId];
            if (previous.Bits != next.Bits || previous.Dpt != next.Dpt) Fail($"Object {sourceId} has incompatible target DPT or bit size.");
            var key = new ObjectKey(device.Id, old.Id);
            var facts = Copy(project.Workbench.Objects.GetValueOrDefault(key.ToString()) ?? new ObjectMetadata());
            if (facts.Dpt is not null && DptId.Normalize(facts.Dpt) != next.Dpt || facts.Bits is { } bits && bits != next.Bits)
                Fail($"Object {old.Id} has an incompatible saved DPT/size override.");
            if (facts.Flags is { } flags && (next.Flags is null || flags.Any(flag => !next.Flags.Contains(flag))))
                Fail($"Object {old.Id} has unsupported target communication flags.");
            var channelSource = facts.ChannelSource;
            if (facts.ChannelId is { } channelId) facts.ChannelId = MigrateChannel(channelId);
            facts.ChannelSource = channelSource;
            if (facts.StructuralDeclaredChannelId is { } declaredChannel)
            {
                facts.StructuralDeclaredChannelId = MigrateChannel(declaredChannel);
                facts.StructuralDeclaredChannelName = target.Evaluation.ResolvedNames.GetValueOrDefault(MigrateChannelDeclaration(declaredChannel));
            }
            var number = old.Number == previous.Number ? next.Number : old.Number;
            Add(next, number, facts, old);
            objectRows.Add(new(sourceId, targetId, old.Number, number, old.Links.Count));
        }
        foreach (var fact in targetObjects.Values.Where(f => !consumed.Contains(f.Id)))
        {
            Add(fact, fact.Number, new(), null);
            objectRows.Add(new("", fact.Id, -1, fact.Number, 0));
        }
        if (objects.Where(o => metadata[new ObjectKey(device.Id, o.Id).ToString()].Active).GroupBy(o => o.Number).Any(g => g.Count() > 1))
            Fail("The resulting active object numbers are ambiguous, including saved number overrides.");
        var prefix = device.Id.Length + ":" + device.Id;
        var knownKeys = device.ComObjectRefs.Select(o => new ObjectKey(device.Id, o.Id).ToString()).ToHashSet(StringComparer.Ordinal);
        if (project.Workbench.Objects.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal) && !knownKeys.Contains(k)))
            Fail("Device has orphan object metadata; resolve it before migration.");
        var deviceMetadata = project.Workbench.Devices.GetValueOrDefault(device.Id);
        var migratedDevice = new DeviceMetadata
        {
            Application = targetProgram, SecureCapable = targetSecureCapable, Demo = deviceMetadata?.Demo ?? false,
            Revision = checked((deviceMetadata?.Revision ?? 1) + 1), DownloadedRevision = deviceMetadata?.DownloadedRevision ?? 1,
        };
        new WorkbenchMetadata { Objects = metadata }.Validate();
        var detached = new Project { Id = project.Id, Name = project.Name };
        var detachedDevice = new DeviceInstance { Id = device.Id, ProductRefId = newProductRefId };
        detachedDevice.ComObjectRefs.AddRange(objects);
        foreach (var (id, value) in values) detachedDevice.ParameterValues.Add(id, value);
        foreach (var (key, text) in texts) detached.ObjectTextDefinitions.Add(key, text);
        if (ApplicationTexts.RefreshObjects(detached, detachedDevice,
            target.Evaluation.Parameters.ToDictionary(p => p.Key, p => p.Value.Value)).Count != 0) Fail("Target object texts contain unresolved parameter references.");
        return new(project, device, newProductRefId, newHardware2ProgramRefId, baseline, parameterRows, objectRows, () =>
        {
            device.ProductRefId = newProductRefId; device.Hardware2ProgramRefId = newHardware2ProgramRefId;
            device.ParameterValues.Clear(); foreach (var (id, value) in values) device.ParameterValues.Add(id, value);
            device.ComObjectRefs.Clear(); device.ComObjectRefs.AddRange(objects);
            device.Channels.Clear(); device.Channels.AddRange(channels);
            foreach (var key in knownKeys) project.Workbench.Objects.Remove(key);
            foreach (var (key, value) in metadata) project.Workbench.Objects.Add(key, value);
            foreach (var key in project.ObjectTextDefinitions.Keys.Where(k => k.DeviceId == device.Id).ToArray()) project.ObjectTextDefinitions.Remove(key);
            foreach (var (key, text) in texts) project.ObjectTextDefinitions.Add(key, text);
            project.Workbench.Devices[device.Id] = migratedDevice;
        }, sourceDocument, targetDocument, parts);

        void Add(ObjectFact fact, int number, ObjectMetadata facts, ComObjectInstanceRef? old)
        {
            if (number is < 0 or > 65535) Fail($"Invalid target object number for {fact.Id}.");
            var id = Native(fact.Id, targetProgram);
            var obj = new ComObjectInstanceRef
            {
                Id = id, ComObjectId = Native(fact.DefinitionId, targetProgram), Number = number,
                Text = old?.ExplicitText, FunctionText = old?.ExplicitFunctionText, Device = old?.Device,
                ChannelId = old?.ChannelId is { } nativeChannel ? MigrateNativePin(nativeChannel) : null,
            };
            if (old is not null) obj.Links.AddRange(old.Links);
            facts.Dpt ??= fact.Dpt; facts.Bits ??= fact.Bits; facts.Flags ??= fact.Flags;
            facts.StructuralDeclaredNumber = fact.Number;
            if (!fact.Active) { facts.StructuralPreviousActive ??= facts.Active; facts.Active = false; }
            else if (facts.StructuralPreviousActive is { } active) { facts.Active = active; facts.StructuralPreviousActive = null; }
            foreach (var groupId in obj.Links)
            {
                var groups = ProjectQuery.GroupAddresses(project).Where(g => g.Id == groupId).ToArray();
                if (groups.Length != 1) Fail($"Object {old!.Id} has missing or ambiguous group address {groupId}.");
                var group = groups[0]; var groupFacts = project.Workbench.Groups.GetValueOrDefault(groupId);
                if (DptId.Normalize(group.DatapointType) != fact.Dpt || groupFacts?.Bits != fact.Bits || groupFacts.Secure is null)
                    Fail($"Object {old!.Id} is incompatible with group address {groupId}, or its capabilities are unknown.");
                if (groupFacts!.Secure == true && targetSecureCapable != true) Fail($"Group address {groupId} requires proven target Secure capability.");
            }
            if (facts.SendingGroupId is { } sending && !obj.Links.Contains(sending)
                || facts.Connectors.Keys.Any(g => !obj.Links.Contains(g))) Fail($"Object {id} has connector facts without matching links.");
            objects.Add(obj); metadata.Add(new ObjectKey(device.Id, id).ToString(), facts);
            var targetText = fact.Text with { Channel = ApplicationChannels.Read(target.Evaluation, targetChannelDevice, fact.Id, targetProgram, obj.ChannelId) };
            if (targetText.Channel is { } channel) ApplicationChannels.Apply(facts, channel);
            texts.Add(new(device.Id, id), targetText);
        }
    }

    /// <summary>Validates every plan before editing. Caller then records one existing project undo snapshot.</summary>
    public static IReadOnlyList<ProductReplaceResult> Apply(Project project, params ProductMigrationPlan[] plans)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(plans);
        if (plans.Length == 0) return [];
        if (plans.Select(p => p.DeviceId).Distinct(StringComparer.Ordinal).Count() != plans.Length) Fail("A device has multiple migration plans.");
        var baseline = Fingerprint(project);
        foreach (var plan in plans)
            if (plan.Applied || !ReferenceEquals(project, plan.Project) || plan.Baseline != baseline
                || !ReferenceEquals(project.ArchiveState, plan.OriginalArchive)
                || !ProjectQuery.DeviceList(project).Any(d => ReferenceEquals(d, plan.Device)))
                throw new InvalidOperationException("Migration preview is stale or belongs to another project. Create a new preview.");
        var archive = project.ArchiveState;
        foreach (var plan in plans.Where(p => p.TargetParts.Count > 0)) archive = ArchiveState.WithManufacturerParts(archive, plan.TargetParts);
        if (plans.Length > 0) archive = ArchiveState.WithApplications(archive, plans.SelectMany(p => new[] { p.SourceApplication, p.TargetApplication }).ToArray());
        var hardware = PrepareHardware(project, archive);
        foreach (var plan in plans) { plan.Commit(); plan.Applied = true; }
        project.Hardware.Clear(); foreach (var (id, fact) in hardware) project.Hardware.Add(id, fact);
        project.ArchiveState = archive;
        return plans.Select(p => new ProductReplaceResult(p.Device, p.OldProductRefId, p.NewProductRefId, p.Objects.Sum(o => o.KeptLinks))).ToArray();
    }

    public static IReadOnlyList<ProductReplaceResult> Replace(
        Project project,
        string oldProductRefId,
        string newProductRefId,
        string? newHardware2ProgramRefId = null,
        bool dryRun = false)
    {
        if (!dryRun && Find(project, oldProductRefId).Any(d =>
            d.ProductRefId == oldProductRefId && d.ProductRefId != newProductRefId
            || newHardware2ProgramRefId is { Length: > 0 } && d.Hardware2ProgramRefId != newHardware2ProgramRefId
            || d.Hardware2ProgramRefId == oldProductRefId && newHardware2ProgramRefId is not { Length: > 0 } && d.Hardware2ProgramRefId != newProductRefId))
            throw new InvalidOperationException("Product/application changes require a validated ProductReplacer.Preview migration plan.");
        var results = new List<ProductReplaceResult>();

        foreach (var device in ProjectQuery.DeviceList(project))
        {
            var matchesProduct = device.ProductRefId == oldProductRefId;
            var matchesProgram = device.Hardware2ProgramRefId == oldProductRefId;
            if (!matchesProduct && !matchesProgram) continue;

            var keptLinks = device.ComObjectRefs.Sum(r => r.Links.Count);
            var old = matchesProduct ? device.ProductRefId : device.Hardware2ProgramRefId!;

            results.Add(new ProductReplaceResult(device, old, newProductRefId, keptLinks));
        }

        return results;
    }

    /// <summary>Devices that still use the given product reference.</summary>
    public static IReadOnlyList<DeviceInstance> Find(Project project, string productRefId)
        => ProjectQuery.DeviceList(project)
            .Where(d => d.ProductRefId == productRefId || d.Hardware2ProgramRefId == productRefId)
            .ToList();

    private static ApplicationSnapshot Snapshot(XDocument document, IReadOnlyDictionary<string, string> values, IParameterScriptRuntime? scripts)
    {
        var snapshot = ApplicationSnapshot.Evaluate(document, values.ToDictionary(p => p.Key, p => (string?)p.Value), scripts);
        if (!snapshot.IsValid || snapshot.Evaluation.Unsupported.Count != 0) Fail("Application snapshot is unsupported: " + string.Join("; ", snapshot.Evaluation.Unsupported));
        return snapshot;
    }
    private static void ValidateProfile(XDocument document)
    {
        if (document.Descendants().Where(e => e.Name.LocalName == "ApplicationProgram")
            .Attributes("Id").Any(a => Knx.Catalog.KnownDcaRequirementsRegistry.RequiresDca(a.Value)))
            Fail("The declared manufacturer DCA requires a dedicated migration implementation.");
        foreach (var element in document.Descendants())
        {
            if (element.Attributes().Any(a => a.Name.LocalName is "Broken" or "DownloadInfoIncomplete" or "RequiresExternalSoftware"
                && !string.IsNullOrWhiteSpace(a.Value) && a.Value is not ("0" or "false" or "False")))
                Fail("Broken, incomplete or external-software application profiles cannot be migrated.");
            if (element.Name.LocalName is "DCA" or "Dca" or "Plugin" or "EtsUiPlugin" or "EtsDataHandler"
                || element.Name.LocalName == "Extension" && (element.Attributes().Any(a => a.Name.LocalName.StartsWith("Ets", StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(a.Value)) || element.Elements().Any(e => e.Name.LocalName.StartsWith("Ets", StringComparison.Ordinal)))
                || element.Name.LocalName is "Parameter" or "ParameterRef" or "ParameterInstanceRef"
                    && !string.IsNullOrWhiteSpace((string?)element.Attribute("Plugin")))
                Fail("Manufacturer plug-ins/DCA require a dedicated migration implementation.");
        }
    }
    private static string ProgramId(XDocument document)
    {
        var programs = document.Descendants().Where(e => e.Name.LocalName == "ApplicationProgram").ToArray();
        if (programs.Length != 1 || string.IsNullOrWhiteSpace((string?)programs[0].Attribute("Id"))) Fail("One identified application program is required.");
        return (string)programs[0].Attribute("Id")!;
    }
    private static void ValidateBinding(Project project, string reference, string expectedProgram)
    {
        if (Knxproj.LoadApplicationProgram(project, reference) is not { } document || ProgramId(document) != expectedProgram)
            Fail("Application reference cannot be resolved from retained manufacturer parts: " + reference);
        if (reference == expectedProgram) return;
        var hardware = reference.Length >= 6 ? ArchiveState.LoadPart(project.ArchiveState!, reference[..6] + "/Hardware.xml") : null;
        var programs = hardware?.Descendants().Where(e => e.Name.LocalName == "Hardware2Program" && (string?)e.Attribute("Id") == reference).ToArray() ?? [];
        var applications = programs.SelectMany(e => e.Elements().Where(c => c.Name.LocalName == "ApplicationProgramRef"))
            .Attributes("RefId").Select(a => a.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (programs.Length != 1 || applications.Length != 1 || applications[0] != expectedProgram)
            Fail("Hardware/application binding is missing or ambiguous: " + reference);
    }
    private static Dictionary<string, CatalogHardware> PrepareHardware(Project project, ArchiveState? archive)
    {
        CatalogHardware CopyFact(CatalogHardware fact) => fact with { MediumTypeIds = Array.AsReadOnly(fact.MediumTypeIds.ToArray()) };
        bool SameFact(CatalogHardware a, CatalogHardware b) => a.HardwareId == b.HardwareId && a.Name == b.Name && a.BusCurrent == b.BusCurrent
            && a.IsPowerSupply == b.IsPowerSupply && a.IsChoke == b.IsChoke && a.IsCoupler == b.IsCoupler
            && a.IsPowerLineRepeater == b.IsPowerLineRepeater && a.IsIpEnabled == b.IsIpEnabled
            && a.MediumTypeIds.SequenceEqual(b.MediumTypeIds, StringComparer.Ordinal);
        var facts = project.Hardware.ToDictionary(p => p.Key, p => CopyFact(p.Value), StringComparer.Ordinal);
        foreach (var entry in archive?.GeneratedParts?.Keys.Where(k => k.EndsWith("/Hardware.xml", StringComparison.OrdinalIgnoreCase)) ?? [])
        {
            var declared = new Dictionary<string, CatalogHardware>(StringComparer.Ordinal);
            CatalogHardwareFacts.Read(ArchiveState.LoadPart(archive!, entry)!, declared);
            foreach (var (id, fact) in declared)
            {
                if (facts.TryGetValue(id, out var previous) && !SameFact(previous, fact)) Fail("Conflicting retained hardware planning facts: " + id);
                facts[id] = CopyFact(fact);
            }
        }
        return facts;
    }
    private static Dictionary<string, T> Unique<T>(IEnumerable<T> values, Func<T, string> id, string kind)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values) if (string.IsNullOrWhiteSpace(id(value)) || !result.TryAdd(id(value), value)) Fail($"Missing or ambiguous {kind} identity.");
        return result;
    }
    private static string Native(string id, string program) => id.StartsWith(program + "_", StringComparison.Ordinal) ? id[(program.Length + 1)..] : id;
    private static string Resolve<T>(string id, string program, Dictionary<string, T> declarations, string kind)
    {
        var matches = declarations.Keys.Where(key => key == id || key == program + "_" + id).ToArray();
        if (matches.Length != 1) Fail($"Missing or ambiguous {kind} declaration: {id}.");
        return matches[0];
    }
    private static string Map<T>(string id, Dictionary<string, T> target, IReadOnlyDictionary<string, string>? mapping, string kind)
    {
        var mapped = mapping?.GetValueOrDefault(id) ?? id;
        if (!target.ContainsKey(mapped)) Fail($"No proven target {kind} identity for {id}; an explicit mapping is required.");
        return mapped;
    }
    private static void ValidateMap<T>(IReadOnlyDictionary<string, string>? mapping, Dictionary<string, T> source, Dictionary<string, T> target, string kind)
    {
        if (mapping is null) return;
        if (mapping.Any(p => !source.ContainsKey(p.Key) || !target.ContainsKey(p.Value))
            || mapping.Values.Distinct(StringComparer.Ordinal).Count() != mapping.Count) Fail($"Invalid or many-to-one {kind} mappings.");
    }
    private static void Compatible(ParameterType? old, ParameterType? next, string id)
    {
        if (old is null || next is null || old.UnsupportedReason is not null || next.UnsupportedReason is not null
            || old.SizeInBit <= 0 && old.DataType != ParameterDataType.IPAddress
                && !(old.DataType == ParameterDataType.RawData && ParameterValidation.RawDataDeclarationError(old) is null)
            || old.DataType != next.DataType || old.SizeInBit != next.SizeInBit || old.Encoding != next.Encoding
            || old.NumberType != next.NumberType || old.Unit != next.Unit || old.AddressType != next.AddressType
            || old.AddressVersion != next.AddressVersion || old.TimeUnit != next.TimeUnit
            || old.TimeUIHint != next.TimeUIHint || old.RawDataMaxSize != next.RawDataMaxSize)
            Fail($"Parameter {id} has incompatible or unknown target storage type.");
        if (old!.DataType == ParameterDataType.Enumeration && !old.Restrictions.Where(r => r.EnumerationValue is not null)
            .Select(r => (r.EnumerationValue, r.EnumerationText)).OrderBy(r => r.EnumerationValue, StringComparer.Ordinal)
            .SequenceEqual(next!.Restrictions.Where(r => r.EnumerationValue is not null)
                .Select(r => (r.EnumerationValue, r.EnumerationText)).OrderBy(r => r.EnumerationValue, StringComparer.Ordinal)))
            Fail($"Parameter {id} changes enumeration meanings; explicit value conversion is not supported.");
    }
    private static Dictionary<string, XElement> Channels(ApplicationSnapshot snapshot) => Unique(snapshot.Evaluation.ExpandedRoot!.DescendantsAndSelf()
        .Where(e => e.Name.LocalName == "Channel"), e => (string?)e.Attribute("Id") ?? "", "channel");
    private sealed record ObjectFact(string Id, string DefinitionId, int Number, int Bits, string Dpt, string? Flags, bool Active, ObjectTextDefinition Text);
    private static Dictionary<string, ObjectFact> ObjectFacts(ApplicationSnapshot snapshot)
    {
        var root = snapshot.Evaluation.ExpandedRoot!;
        var definitions = Unique(root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ComObject"), e => (string?)e.Attribute("Id") ?? "", "object definition");
        var facts = new List<ObjectFact>();
        foreach (var reference in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ComObjectRef"))
        {
            var id = (string?)reference.Attribute("Id") ?? "";
            if (!definitions.TryGetValue((string?)reference.Attribute("RefId") ?? "", out var definition)) Fail($"Unresolved object declaration {id}.");
            string? Pick(string name) => (string?)reference.Attribute(name) ?? (string?)definition!.Attribute(name);
            var bits = int.TryParse(Pick("SizeInBit"), out var size) ? size : Ksxml.ParseObjectSize(Pick("ObjectSize"));
            var dpt = DptId.FromDeclaration(Pick("DatapointType"));
            var validNumber = int.TryParse(Pick("Number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number);
            if (bits is < 1 or > 65535 || dpt is null || dpt.EndsWith(".x", StringComparison.Ordinal)
                || !DptId.TryBitSize(dpt, out var dptBits) || bits != dptBits || !validNumber || number is < 0 or > 65535)
                Fail($"Object {id} has unknown DPT, size or number; migration cannot infer compatibility.");
            var flags = Pick("CommunicationFlag") == "Enabled" ? "C" + string.Concat(new[] { ("ReadFlag", "R"), ("WriteFlag", "W"), ("TransmitFlag", "T"), ("UpdateFlag", "U"), ("ReadOnInitFlag", "I") }
                .Where(f => Pick(f.Item1) == "Enabled").Select(f => f.Item2)) : null;
            var textReference = Pick("TextParameterRefId");
            var text = new ObjectTextDefinition(Pick("Text"), Pick("FunctionText"), textReference,
                textReference is null ? null : snapshot.Evaluation.Parameters.GetValueOrDefault(textReference)?.DeclaredValue,
                textReference is null || snapshot.Evaluation.Parameters.ContainsKey(textReference),
                reference.Attribute("TextParameterRefId") is not null ? reference.Annotation<TextArgumentBinding>() : definition!.Annotation<TextArgumentBinding>());
            facts.Add(new(id, (string)definition!.Attribute("Id")!, number, bits, dpt!, flags, snapshot.Evaluation.ActiveObjectReferences.Contains(id), text));
        }
        var placements = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ComObjectRefRef")
            .Attributes("RefId").Select(a => a.Value).ToHashSet(StringComparer.Ordinal);
        if (definitions.Keys.Except(facts.Select(f => f.DefinitionId), StringComparer.Ordinal).Any(placements.Contains))
            Fail("Object placements without explicit references cannot be migrated.");
        var result = Unique(facts, f => f.Id, "object reference");
        var program = (string)root.DescendantsAndSelf().Single(e => e.Name.LocalName == "ApplicationProgram").Attribute("Id")!;
        if (result.Keys.Select(id => Native(id, program)).Distinct(StringComparer.Ordinal).Count() != result.Count)
            Fail("Object references have ambiguous native identities.");
        return result;
    }
    private static ObjectMetadata Copy(ObjectMetadata value) => new()
    {
        Dpt = value.Dpt, Bits = value.Bits, Flags = value.Flags, Active = value.Active,
        StructuralPreviousActive = value.StructuralPreviousActive, StructuralDeclaredNumber = value.StructuralDeclaredNumber,
        StructuralDeclaredChannelId = value.StructuralDeclaredChannelId, StructuralDeclaredChannelName = value.StructuralDeclaredChannelName,
        ChannelId = value.ChannelId, ChannelSource = value.ChannelSource, Role = value.Role, SendingGroupId = value.SendingGroupId,
        Connectors = value.Connectors.ToDictionary(p => p.Key, p => p.Value with { }, StringComparer.Ordinal),
    };
    private static string Fingerprint(Project project) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting) + JsonSerializer.Serialize(project.Hardware)
        + JsonSerializer.Serialize(project.ObjectTextDefinitions.Select(p => new { Key = p.Key.ToString(), p.Value })))));
    private static void Required(string value, string name) { if (string.IsNullOrWhiteSpace(value)) Fail(name + " is required."); }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn] private static void Fail(string message) => throw new InvalidDataException(message);
}
