using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Knx.Workbench;

namespace Knx.Manufacturer;

public enum JungKspLinkAction { None, OptionalUnlink, RequiredUnlink }
public sealed record JungKspParameterChange(string ReferenceId, string? OldValue, string NewValue, int? RawByteLength);
public sealed record JungKspLink(string ReferenceId, string GroupAddressId, bool Sending, bool Acknowledge, int? Priority, int? Slot);
public sealed record JungKspObjectChange(int Number, string? SourceReferenceId, string? TargetReferenceId,
    string? OldDpt, string? NewDpt, int? OldBits, int? NewBits, string? OldText, string? NewText,
    string? OldFunctionText, string? NewFunctionText, string? OldFlags, string? NewFlags,
    JungKspLinkAction LinkAction, string LinkReason, IReadOnlyList<JungKspLink> ExistingLinks)
{ public bool WasActive { get; init; } public bool WillBeActive { get; init; } }
public sealed record JungKspBinaryDataChange(string Name, int OldLength, int NewLength, string? OldSha256, string NewSha256);
public sealed record JungKspImportResult(string DeviceId, int ParameterWrites, int ObjectChanges, int RemovedLinks);

/// <summary>Immutable review rows; only private prepared state is committed. A plan is single-use.</summary>
public sealed class JungKspImportPlan
{
    public string DeviceId { get; }
    public string ApplicationProgramId => JungKspImport.ApplicationProgramId;
    public string DefinitionSha256 { get; }
    public string ProjectSha256 { get; }
    public IReadOnlyList<JungKspParameterChange> Parameters { get; }
    public IReadOnlyList<JungKspObjectChange> Objects { get; }
    public JungKspBinaryDataChange FunctionTexts { get; }
    public IReadOnlyList<string> FrameSha256 { get; }
    public bool IsApplied { get; internal set; }
    internal Project Project { get; }
    internal DeviceInstance Device { get; }
    internal ArchiveState? Archive { get; }
    internal Func<IReadOnlyDictionary<int, bool>, (Action Commit, int RemovedLinks)> PrepareCommit { get; }
    internal JungKspImportPlan(Project project, DeviceInstance device, string definitionHash, string projectHash,
        IEnumerable<JungKspParameterChange> parameters, IEnumerable<JungKspObjectChange> objects,
        JungKspBinaryDataChange functionTexts, IEnumerable<string> frameHashes,
        Func<IReadOnlyDictionary<int, bool>, (Action Commit, int RemovedLinks)> prepare)
    {
        Project = project; Device = device; Archive = project.ArchiveState; DeviceId = device.Id;
        DefinitionSha256 = definitionHash; ProjectSha256 = projectHash;
        Parameters = Array.AsReadOnly(parameters.ToArray()); Objects = Array.AsReadOnly(objects.ToArray());
        FunctionTexts = functionTexts; FrameSha256 = Array.AsReadOnly(frameHashes.ToArray()); PrepareCommit = prepare;
    }
}

/// <summary>
/// Offline import for the supported Jung SP8 application. It never runs DCA code.
/// Removed or incompatible links require an explicit review decision.
/// Hosts record one undo snapshot after Apply.
/// </summary>
public static class JungKspImport
{
    public const string ApplicationProgramId = "M-0004_A-5018-15-17D9";
    private static readonly string Prefix = ApplicationProgramId + "_";
    private static readonly Regex Word = new("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private sealed record Fact(string Id, string DefinitionId, int Number, int Bits, string? Dpt, string? Text, string? FunctionText, string Flags, bool Active);
    private sealed record Desired(int Number, string Text, int Main, int? Subtype, string Dpt, int Bits, string Flags, string? FunctionText);
    private sealed record Item(int Id, int? Command, int? Status, string Name, int Main, int? Subtype, uint Flags);

    public static JungKspImportPlan Preview(Project project, DeviceInstance device, XDocument application,
        JungKspDocument document, IReadOnlyList<DatapointType> masterData, string language = "en")
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(application); ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(masterData);
        ValidateOwner(project, device);
        if (device.HasOpaqueBinaryData) Fail("Opaque device binary-data metadata cannot be imported safely.");
        if (device.BinaryData.Values.Any(v => v is null || v.Length > 8 * 1024 * 1024)
            || device.BinaryData.Values.Sum(v => (long)v.Length) > 8L * 1024 * 1024) Fail("Existing device binary-data budget exceeded.");
        if (language.Length != 2 || language.Any(c => c is < 'a' or > 'z')) Fail("Language must be a two-letter lowercase identifier.");
        if (document.Header.ApiVersion != 1 || document.Header.DatabaseVersion != new Version(3, 0, 0))
            Fail("Only the explicit DCA API 1 / database 3.0.0 import profile is supported.");
        var definition = new XDocument(application); ValidateDefinition(definition);
        ValidateBinding(project, device, definition);
        var definitionHash = HashDefinition(definition); var baseline = Fingerprint(project);
        var preparedArchive = ArchiveState.WithApplications(project.ArchiveState, definition);
        var parameterRefs = Unique(definition.Descendants().Where(e => e.Name.LocalName == "ParameterRef"), "parameter reference");
        var sourceValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, value) in device.ParameterValues)
        {
            var full = Full(id);
            if (!parameterRefs.ContainsKey(full) || !sourceValues.TryAdd(full, value)) Fail("Unresolved or ambiguous saved parameter: " + id);
        }
        var source = Snapshot(definition, sourceValues);
        foreach (var instance in source.Instances)
            if (sourceValues.TryGetValue(instance.Id, out var value) && !ParameterValidation.TryValidate(instance.ParameterType, value, out var error))
                Fail("Invalid saved parameter " + instance.Id + ": " + error);
        var sourceFacts = Facts(source); var sourceActive = ActiveByNumber(sourceFacts);
        var desired = ReadDesired(document, masterData, language);
        if (device.ComObjectRefs.Count > 20_000 || device.ComObjectRefs.Sum(o => (long)o.Links.Count) > 100_000)
            Fail("Existing object/link state exceeds the import budget.");
        var saved = new Dictionary<string, ComObjectInstanceRef>(StringComparer.Ordinal);
        foreach (var obj in device.ComObjectRefs)
        {
            var id = Full(obj.Id);
            if (!sourceFacts.TryGetValue(id, out var fact) || !saved.TryAdd(id, obj) || obj.Number != fact.Number
                || Full(obj.ComObjectId) != fact.DefinitionId && Full(obj.ComObjectId) != fact.Id)
                Fail("Saved object identity/number is unresolved or ambiguous: " + obj.Id);
            if (obj.Links.Distinct(StringComparer.Ordinal).Count() != obj.Links.Count) Fail("Duplicate saved object links.");
        }
        var targetValues = new Dictionary<string, string>(sourceValues, StringComparer.Ordinal);
        var writes = new Dictionary<string, (string Value, int? Bytes)>(StringComparer.Ordinal);
        var sourceParameters = source.Instances.ToDictionary(p => p.Id, StringComparer.Ordinal);
        void Set(int number, string value, int? rawBytes = null)
        {
            var id = ParameterId(number);
            if (!parameterRefs.ContainsKey(id)) Fail("Missing exact SP8 parameter reference: " + id);
            if (!sourceParameters.TryGetValue(id, out var parameter)) Fail("Missing exact SP8 parameter reference: " + id);
            if (!ParameterValidation.TryValidate(parameter!.ParameterType, value, out var error)) Fail("Parameter " + id + ": " + error);
            targetValues[id] = value; writes[id] = (value, rawBytes);
        }
        Set(1, document.Header.PackedVersion.ToString(CultureInfo.InvariantCulture));
        for (var index = 1; index <= 3; index++)
        {
            var bytes = document.Blocks[index].GetBytes();
            var id = ParameterId(index + 1);
            if (!sourceParameters.TryGetValue(id, out var parameter) || parameter.ParameterType?.DataType != ParameterDataType.RawData
                || parameter.ParameterType.RawDataMaxSize != (index == 1 ? 30716 : 16380))
                Fail("The SP8 raw parameter declaration differs from the supported profile: " + id);
            if (bytes.Length != 0) Set(index + 1, ParameterValidation.EncodeRawData(parameter!.ParameterType!, bytes), bytes.Length);
        }
        foreach (var number in sourceActive.Keys.Except(desired.Keys)) { Set(2 * number + 14, "0"); Set(2 * number + 15, "0"); }
        foreach (var item in desired.Values)
        { Set(2 * item.Number + 14, item.Bits.ToString(CultureInfo.InvariantCulture)); Set(2 * item.Number + 15, item.Main.ToString(CultureInfo.InvariantCulture)); }
        var target = Snapshot(definition, targetValues); var targetFacts = Facts(target); var targetActive = ActiveByNumber(targetFacts);
        if (!targetActive.Keys.ToHashSet().SetEquals(desired.Keys)) Fail("Activation does not produce exactly the requested SP8 object numbers.");
        foreach (var item in desired.Values)
            if (targetActive[item.Number].Bits != item.Bits) Fail("Activated object size differs from the master-data DPT size: " + item.Number);
        foreach (var (id, write) in writes)
        {
            var parameter = target.Instances.SingleOrDefault(p => p.Id == id);
            if (parameter is null || target.Evaluation.AssignedParameters.Contains(id)
                || !ParameterValidation.TryValidate(parameter.ParameterType, write.Value, out var error))
                Fail("Imported parameter is absent, calculated or invalid after activation: " + id);
        }
        var stagedObjects = device.ComObjectRefs.Select(CopyObject).ToList();
        var groupCounts = ProjectQuery.GroupAddresses(project).GroupBy(g => g.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var stagedFacts = device.ComObjectRefs.ToDictionary(o => o.Id,
            o => CopyMetadata(project.Workbench.Objects.GetValueOrDefault(new ObjectKey(device.Id, o.Id).ToString()) ?? Metadata(sourceFacts[Full(o.Id)])), StringComparer.Ordinal);
        var objectRows = new List<JungKspObjectChange>();
        foreach (var number in sourceActive.Keys.Union(desired.Keys).Order())
        {
            sourceActive.TryGetValue(number, out var before);
            desired.TryGetValue(number, out var next);
            var after = next is null ? null : targetActive[number];
            var old = saved.GetValueOrDefault(before?.Id ?? after?.Id ?? "");
            var oldFact = before ?? (old is null ? null : sourceFacts[Full(old.Id)]);
            var oldMeta = old is null ? oldFact is null ? null : Metadata(oldFact) : CopyMetadata(stagedFacts[old.Id]);
            var sameNumber = stagedObjects.Where(o => o.Number == number).ToArray();
            var links = sameNumber.SelectMany(o => o.Links.Select(group =>
            {
                if (groupCounts.GetValueOrDefault(group) != 1) Fail("Object link points to a missing or ambiguous group-address id: " + group);
                var connector = stagedFacts[o.Id].Connector(group);
                return new JungKspLink(o.Id, group, connector.Send, connector.Acknowledge, connector.Priority, connector.Slot);
            })).ToArray();
            foreach (var obj in sameNumber)
            {
                var facts = stagedFacts[obj.Id];
                if (facts.SendingGroupId is { } sending && !obj.Links.Contains(sending) || facts.Connectors.Keys.Any(g => !obj.Links.Contains(g)))
                    Fail("Connector metadata has no matching object link: " + obj.Id);
                facts.Active = false; facts.StructuralPreviousActive = null;
            }
            var action = JungKspLinkAction.None; var reason = "";
            var oldDpt = DptId.Normalize(oldMeta?.Dpt ?? oldFact?.Dpt);
            var oldBits = oldMeta?.Bits ?? oldFact?.Bits;
            var oldText = old?.Text ?? oldFact?.Text;
            var oldFunction = old?.FunctionText ?? oldFact?.FunctionText;
            string? newFlags = null, newFunction = null;
            if (next is null) { action = JungKspLinkAction.RequiredUnlink; reason = "Object removed; BusChord requires explicit unlink review."; }
            else
            {
                var mainChanged = before is not null && Main(oldDpt) != next.Main;
                var incompatibleHistory = links.Any(link =>
                {
                    var reference = sameNumber.Single(o => o.Id == link.ReferenceId);
                    var metadata = stagedFacts[reference.Id];
                    return Full(reference.Id) != after!.Id || Main(metadata.Dpt ?? sourceFacts[Full(reference.Id)].Dpt) != next.Main
                        || (metadata.Bits ?? sourceFacts[Full(reference.Id)].Bits) != next.Bits
                        || !GroupCompatible(project, link.GroupAddressId, next, masterData);
                });
                if (mainChanged || before is not null && oldBits != next.Bits || incompatibleHistory)
                { action = JungKspLinkAction.RequiredUnlink; reason = "DPT, size, reference variant or existing link compatibility changes."; }
                else if (oldText != next.Text && before is not null || before is not null && oldDpt != next.Dpt && oldDpt is not null && !oldDpt.EndsWith(".x", StringComparison.Ordinal))
                { action = JungKspLinkAction.OptionalUnlink; reason = "Name or subtype changes; compatible links are retained by default."; }
                else if (before is null && links.Length > 0)
                { action = JungKspLinkAction.OptionalUnlink; reason = "Compatible inactive target-reference links will reactivate; retain by default or explicitly unlink."; }
                var current = stagedObjects.SingleOrDefault(o => Full(o.Id) == after!.Id);
                if (current is null)
                {
                    current = new() { Id = Native(after!.Id), ComObjectId = Native(after.DefinitionId), Number = number };
                    stagedObjects.Add(current); stagedFacts.Add(current.Id, Metadata(after));
                }
                var metadata = stagedFacts[current.Id];
                var preserveFlags = before is not null && oldText == next.Text && oldDpt == next.Dpt && oldBits == next.Bits;
                var flags = preserveFlags ? oldMeta?.Flags ?? oldFact?.Flags ?? next.Flags : next.Flags;
                // Keep the target reference's existing I state.
                newFlags = string.Concat("CRWTU".Where(c => flags?.Contains(c) == true)) + ((metadata.Flags ?? after!.Flags).Contains('I') ? "I" : "");
                newFunction = next.FunctionText ?? after!.FunctionText;
                current.Text = next.Text; current.FunctionText = newFunction;
                metadata.Dpt = next.Dpt; metadata.Bits = next.Bits; metadata.Flags = newFlags; metadata.Active = true;
                metadata.StructuralPreviousActive = null; metadata.StructuralDeclaredNumber = number;
            }
            objectRows.Add(new(number, old?.Id ?? (before is null ? null : Native(before.Id)), after is null ? null : Native(after.Id),
                oldDpt, next?.Dpt, oldBits, next?.Bits, oldText, next?.Text, oldFunction, newFunction,
                oldMeta?.Flags ?? oldFact?.Flags, newFlags, action, reason, Array.AsReadOnly(links)) { WasActive = before is not null, WillBeActive = next is not null });
        }
        var binaryData = device.BinaryData.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
        var functionBytes = document.Blocks.Single(b => b.Kind == JungKspBlockKind.FunctionTexts).GetBytes();
        var oldFunctionBytes = binaryData.GetValueOrDefault("FunctionTexts"); binaryData["FunctionTexts"] = functionBytes;
        if (binaryData.Values.Sum(v => (long)v.Length) > 8L * 1024 * 1024) Fail("Resulting device binary-data budget exceeds the native 8 MiB limit.");
        // Use the exact native writer as a detached preflight, including retained identity metadata.
        var binaryCandidate = new DeviceInstance { Id = device.Id, ProductRefId = device.ProductRefId };
        foreach (var (name, bytes) in binaryData) binaryCandidate.BinaryData.Add(name, bytes);
        foreach (var (name, info) in device.BinaryDataInfo) binaryCandidate.BinaryDataInfo.Add(name, info);
        _ = DeviceBinaryData.Write(binaryCandidate, Knxproj.ProjectNamespace);
        var binaryRow = new JungKspBinaryDataChange("FunctionTexts", oldFunctionBytes?.Length ?? 0, functionBytes.Length,
            oldFunctionBytes is null ? null : Hash(oldFunctionBytes), Hash(functionBytes));
        var parameterRows = writes.Select(p => new JungKspParameterChange(p.Key,
            sourceValues.GetValueOrDefault(p.Key) ?? sourceParameters[p.Key].Value, p.Value.Value, p.Value.Bytes)).ToArray();
        var deviceMeta = project.Workbench.Devices.GetValueOrDefault(device.Id);
        var updatedDeviceMeta = new DeviceMetadata { Application = deviceMeta?.Application, Demo = deviceMeta?.Demo ?? false,
            SecureCapable = deviceMeta?.SecureCapable, Revision = checked((deviceMeta?.Revision ?? 1) + 1), DownloadedRevision = deviceMeta?.DownloadedRevision ?? 1 };
        return new(project, device, definitionHash, baseline, parameterRows, objectRows, binaryRow, document.Blocks.Select(b => b.Sha256), decisions =>
        {
            var objects = stagedObjects.Select(CopyObject).ToArray();
            var metadata = stagedFacts.ToDictionary(p => p.Key, p => CopyMetadata(p.Value), StringComparer.Ordinal);
            var removed = 0;
            foreach (var obj in objects.Where(o => decisions.GetValueOrDefault(o.Number)))
            { removed += obj.Links.Count; obj.Links.Clear(); metadata[obj.Id].Connectors.Clear(); metadata[obj.Id].SendingGroupId = null; }
            var binary = binaryData.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
            var texts = objects.ToDictionary(o => new ObjectKey(device.Id, o.Id), o =>
            { var f = targetFacts[Full(o.Id)]; return new ObjectTextDefinition(f.Text, f.FunctionText, null, null, true, null,
                ApplicationChannels.Read(target.Evaluation, device, Full(o.Id), ApplicationProgramId, o.ChannelId)); });
            foreach (var obj in objects)
                if (texts[new ObjectKey(device.Id, obj.Id)].Channel is { } channel) ApplicationChannels.Apply(metadata[obj.Id], channel);
            void Install(Project owner, DeviceInstance targetDevice, ComObjectInstanceRef[] installedObjects,
                Dictionary<string, ObjectMetadata> installedMetadata, Dictionary<string, byte[]> installedBinary)
            {
                targetDevice.ParameterValues.Clear(); foreach (var (id, value) in targetValues) targetDevice.ParameterValues[id] = value;
                targetDevice.ComObjectRefs.Clear(); targetDevice.ComObjectRefs.AddRange(installedObjects);
                foreach (var (id, value) in installedMetadata) owner.Workbench.Objects[new ObjectKey(device.Id, id).ToString()] = value;
                foreach (var (key, text) in texts) owner.ObjectTextDefinitions[key] = text;
                targetDevice.BinaryData.Clear(); foreach (var (name, bytes) in installedBinary) targetDevice.BinaryData[name] = bytes;
                owner.Workbench.Devices[device.Id] = updatedDeviceMeta; owner.ArchiveState = preparedArchive;
            }
            var candidate = Knxproj.FromXml(Knxproj.ToXml(project));
            foreach (var (id, hardware) in project.Hardware) candidate.Hardware[id] = hardware;
            var candidateDevice = ProjectQuery.DeviceList(candidate).Single(d => d.Id == device.Id);
            Install(candidate, candidateDevice, objects.Select(CopyObject).ToArray(),
                metadata.ToDictionary(p => p.Key, p => CopyMetadata(p.Value), StringComparer.Ordinal),
                binary.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal));
            _ = Knxproj.ToXml(candidate); // Same serialization used by the host's subsequent single undo snapshot.
            return (() => Install(project, device, objects, metadata, binary), removed);
        });
    }

    public static JungKspImportResult Apply(Project project, JungKspImportPlan plan, XDocument currentApplication,
        IReadOnlyDictionary<int, bool>? unlinkDecisions = null)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(currentApplication);
        if (plan.Device.HasOpaqueBinaryData) Fail("Opaque device binary-data metadata cannot be imported safely.");
        if (plan.IsApplied || !ReferenceEquals(project, plan.Project) || !ReferenceEquals(project.ArchiveState, plan.Archive)
            || !ProjectQuery.DeviceList(project).Any(d => ReferenceEquals(d, plan.Device)) || Fingerprint(project) != plan.ProjectSha256
            || HashDefinition(currentApplication) != plan.DefinitionSha256)
            throw new InvalidOperationException("KSP preview is stale or belongs to another project/definition. Create a new preview.");
        ValidateOwner(project, plan.Device); ValidateBinding(project, plan.Device, currentApplication);
        var choices = new Dictionary<int, bool>(unlinkDecisions ?? new Dictionary<int, bool>());
        if (choices.Keys.Any(n => !plan.Objects.Any(o => o.Number == n && o.LinkAction != JungKspLinkAction.None)))
            Fail("Unlink decisions contain an unreviewed object number.");
        foreach (var row in plan.Objects.Where(o => o.LinkAction == JungKspLinkAction.RequiredUnlink))
            if (!choices.TryGetValue(row.Number, out var unlink) || !unlink) Fail("Explicit unlink approval is required for object " + row.Number + ".");
        var prepared = plan.PrepareCommit(new ReadOnlyDictionary<int, bool>(choices));
        prepared.Commit(); plan.IsApplied = true;
        return new(plan.DeviceId, plan.Parameters.Count, plan.Objects.Count, prepared.RemovedLinks);
    }

    private static Dictionary<int, Desired> ReadDesired(JungKspDocument document, IReadOnlyList<DatapointType> master, string language)
    {
        var config = document.ConfigurationJson; var functions = document.FunctionTextsJson;
        CheckKeys(Object(config, "items"), ["objects_list"]); CheckKeys(Object(config, "elements"), ["objects_list"]);
        var allowedRoots = new HashSet<string>(["versions", "information", "items", "elements", "general", "notify", "rules", "scenary", "scheduler", "users"], StringComparer.Ordinal);
        if (config.EnumerateObject().Any(p => !allowedRoots.Contains(p.Name))) Fail("Unrecognized/SYSTEM/thermo configuration root is outside the ordinary psoe profile.");
        foreach (var root in functions.EnumerateObject())
        {
            if (root.Name is "version" or "versions") continue;
            if (root.Name == "dca_generic")
            { CheckKeys(root.Value, ["com_status_label"]); CheckLocalized(Object(root.Value, "com_status_label")); continue; }
            if (!root.Name.StartsWith("psoe_", StringComparison.Ordinal) || root.Name == "psoe_thermo_logic" || !Word.IsMatch(root.Name))
                Fail("Unrecognized/SYSTEM/thermo function-text class.");
            if (root.Value.ValueKind != JsonValueKind.Object) Fail("psoe text class must be an object.");
            foreach (var function in root.Value.EnumerateObject())
            {
                if (!Word.IsMatch(function.Name)) Fail("Unrecognized function-text key.");
                CheckKeys(function.Value, ["command", "status"]);
                foreach (var direction in function.Value.EnumerateObject()) CheckLocalized(direction.Value);
            }
        }
        var statusPostfix = "Status";
        if (functions.TryGetProperty("dca_generic", out var generic) && generic.TryGetProperty("com_status_label", out var labels)
            && labels.TryGetProperty(language, out var label) && !string.IsNullOrEmpty(label.GetString())) statusPostfix = label.GetString()!;
        var items = new Dictionary<int, Item>();
        foreach (var property in Object(Object(config, "items"), "objects_list").EnumerateObject())
        {
            var id = Number(property.Name, "item id"); var value = property.Value;
            CheckKeys(value, ["feedback", "status", "command", "name", "type", "typeformat", "flags", "id_driver"]);
            _ = Text(value, "feedback");
            if (string.IsNullOrEmpty(Text(value, "id_driver"))) Fail("Item driver is missing.");
            var name = Text(value, "name") ?? throw Invalid("Item name is missing.");
            if (name.Contains("SYSTEM", StringComparison.Ordinal)) Fail("SYSTEM items are outside this import profile.");
            var command = OptionalNumber(Text(value, "command")); var status = OptionalNumber(Text(value, "status"));
            if (command is null && status is null) Fail("Item has neither command nor status object.");
            var type = Text(value, "type") ?? "";
            if (!type.StartsWith("dtp_", StringComparison.Ordinal)) Fail("Only the dtp_ main type syntax is supported.");
            var main = Number(type[4..], "DPT main"); if (main == 0) Fail("DPT zero is outside the import profile.");
            var subtype = OptionalNumber(Text(value, "typeformat"));
            uint flags = 0;
            if (value.TryGetProperty("flags", out var flagElement) && !uint.TryParse(Scalar(flagElement), NumberStyles.None, CultureInfo.InvariantCulture, out flags)) Fail("Invalid flags.");
            if (!items.TryAdd(id, new(id, command, status, name, main, subtype, flags))) Fail("Ambiguous numeric item id.");
        }
        var mappings = new Dictionary<int, (string Class, string Function)>();
        foreach (var element in Object(Object(config, "elements"), "objects_list").EnumerateObject())
        {
            CheckKeys(element.Value, ["@class", "function_map"]);
            var @class = Text(element.Value, "@class") ?? "";
            if (!@class.StartsWith("psoe_", StringComparison.Ordinal) || @class == "psoe_thermo_logic" || !Word.IsMatch(@class))
                Fail("Unrecognized/SYSTEM/thermo element class.");
            foreach (var mapping in Object(element.Value, "function_map").EnumerateObject())
            {
                if (!Word.IsMatch(mapping.Name)) Fail("Unrecognized function-map key.");
                var id = Number(Scalar(mapping.Value), "function-map item id");
                if (!items.ContainsKey(id) || !mappings.TryAdd(id, (@class, mapping.Name))) Fail("Missing or multiply mapped item.");
            }
        }
        if (mappings.Count != items.Count) Fail("Every item requires exactly one ordinary psoe function mapping.");
        var desired = new Dictionary<int, Desired>();
        foreach (var item in items.Values)
        {
            var (dpt, bits, subtype) = ResolveDpt(master, item.Main, item.Subtype);
            var mapping = mappings[item.Id];
            void Add(int number, bool status)
            {
                if (number is < 1 or > 1000) Fail("SP8 object number must be 1..1000.");
                var flags = string.Concat(new[] { ('C', 2), ('T', 4), ('U', 6), ('R', 8), ('W', 10) }
                    .Where(p => (item.Flags & (1u << (p.Item2 + (status ? 1 : 0)))) != 0).Select(p => p.Item1));
                if (flags.Length == 0) flags = "CTUW";
                var text = FunctionText(functions, mapping.Class, mapping.Function, status ? "status" : "command", language);
                var name = item.Name + (status ? " " + statusPostfix : ""); ValidateText(name); if (text is not null) ValidateText(text);
                if (!desired.TryAdd(number, new(number, name, item.Main, subtype, dpt, bits, flags, text)))
                    Fail("Multiple items/directions map to the same object number: " + number);
            }
            if (item.Status is { } status) Add(status, true);
            if (item.Command is { } command) Add(command, false);
        }
        return desired;
    }

    private static string? FunctionText(JsonElement root, string @class, string function, string direction, string language)
    {
        if (!root.TryGetProperty(@class, out var texts)) return null;
        if (!texts.TryGetProperty(function, out var entry))
        {
            var index = function.LastIndexOf('_');
            if (index < 0 || !int.TryParse(function[(index + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || !texts.TryGetProperty(function[..(index + 1)], out entry)) return null;
        }
        if (!entry.TryGetProperty(direction, out var localized)) return null;
        if (localized.TryGetProperty(language, out var value) && !string.IsNullOrEmpty(value.GetString())) return value.GetString();
        return localized.TryGetProperty("en", out value) && !string.IsNullOrEmpty(value.GetString()) ? value.GetString() : null;
    }
    private static void CheckLocalized(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => p.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
            Fail("Localized text must contain string/null language entries.");
    }
    private static void CheckKeys(JsonElement value, string[] allowed)
    { if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal))) Fail("Unrecognized JSON fields in the bounded import profile."); }
    private static JsonElement Object(JsonElement parent, string name)
    { if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) throw Invalid("Missing object: " + name); return value; }
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null ? Scalar(property) : null;
    private static string Scalar(JsonElement value) => value.ValueKind switch
    { JsonValueKind.String => value.GetString()!, JsonValueKind.Number => value.GetRawText(), _ => throw Invalid("Expected a string/integer scalar.") };
    private static int Number(string value, string context)
    { if (!ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) throw Invalid("Invalid " + context + "."); return number; }
    private static int? OptionalNumber(string? text) => string.IsNullOrEmpty(text) ? null : Number(text, "object/subtype number");

    private static (string Dpt, int Bits, int? Subtype) ResolveDpt(IReadOnlyList<DatapointType> types, int main, int? subtype)
    {
        var matches = types.Where(t => t.Id.Equals("DPT-" + main, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].SizeInBit is < 1 or > 65535) throw Invalid("Missing/ambiguous DPT master data for " + main + ".");
        if (subtype is not null)
        {
            if (main == 232 || main == 251 && subtype == 699) subtype = 600;
            var subs = matches[0].Subtypes.Where(s => s.Id.Equals($"DPST-{main}-{subtype}", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (subs.Length != 1 || subs[0].SizeInBit != matches[0].SizeInBit) throw Invalid("Missing/ambiguous DPT subtype master data.");
        }
        return (subtype is { } sub ? $"{main}.{sub:000}" : $"{main}.x", matches[0].SizeInBit, subtype);
    }
    private static bool GroupCompatible(Project project, string id, Desired next, IReadOnlyList<DatapointType> master)
    {
        var group = ProjectQuery.FindGroupAddressById(project, id)!;
        var metadata = project.Workbench.Groups.GetValueOrDefault(id);
        var code = DptId.Normalize(group.DatapointType);
        var bits = metadata?.Bits;
        if (bits is null && Main(code) is { } main)
        { var types = master.Where(t => t.Id.Equals("DPT-" + main, StringComparison.OrdinalIgnoreCase)).ToArray(); if (types.Length == 1) bits = types[0].SizeInBit; }
        return Main(code) == next.Main && bits == next.Bits;
    }
    private static int? Main(string? dpt)
    { var normalized = DptId.Normalize(dpt); return normalized is null ? null : int.Parse(normalized.Split('.')[0], CultureInfo.InvariantCulture); }

    private static ApplicationSnapshot Snapshot(XDocument document, Dictionary<string, string> values)
    {
        var snapshot = ApplicationSnapshot.Evaluate(document, values.ToDictionary(p => p.Key, p => (string?)p.Value));
        if (!snapshot.IsValid || snapshot.Evaluation.Unsupported.Count != 0) throw Invalid("Application snapshot is unsupported or invalid.");
        return snapshot;
    }
    private static Dictionary<string, Fact> Facts(ApplicationSnapshot snapshot)
    {
        var root = snapshot.Evaluation.ExpandedRoot!;
        var definitions = Unique(root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ComObject"), "object definition");
        var result = new Dictionary<string, Fact>(StringComparer.Ordinal);
        foreach (var reference in root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ComObjectRef"))
        {
            var id = RequiredId(reference);
            if (!definitions.TryGetValue((string?)reference.Attribute("RefId") ?? "", out var definition)) Fail("Unresolved object reference.");
            string? Pick(string name) => (string?)reference.Attribute(name) ?? (string?)definition!.Attribute(name);
            var number = Number(Pick("Number") ?? "", "declared object number");
            var bits = Ksxml.ParseObjectSize(Pick("ObjectSize"));
            if (number is < 1 or > 1000 || bits is < 1 or > 65535) Fail("Unknown object number/size.");
            var flags = string.Concat(new[] { ("CommunicationFlag", 'C'), ("ReadFlag", 'R'), ("WriteFlag", 'W'), ("TransmitFlag", 'T'), ("UpdateFlag", 'U'), ("ReadOnInitFlag", 'I') }
                .Where(p => Pick(p.Item1) == "Enabled").Select(p => p.Item2));
            if (!result.TryAdd(id, new(id, RequiredId(definition!), number, bits, DptId.FromDeclaration(Pick("DatapointType")), Pick("Text"), Pick("FunctionText"), flags,
                snapshot.ActiveObjects.Contains(Native(id))))) Fail("Ambiguous object reference.");
        }
        return result;
    }
    private static Dictionary<int, Fact> ActiveByNumber(Dictionary<string, Fact> facts)
    {
        var active = new Dictionary<int, Fact>();
        foreach (var fact in facts.Values.Where(f => f.Active))
            if (!active.TryAdd(fact.Number, fact)) Fail("Multiple active reference variants for object number " + fact.Number + ".");
        return active;
    }
    private static void ValidateDefinition(XDocument definition)
    {
        var programs = definition.Descendants().Where(e => e.Name.LocalName == "ApplicationProgram").ToArray();
        if (definition.DocumentType is not null || definition.Root?.Name.LocalName != "KNX" || programs.Length != 1
            || (string?)programs[0].Attribute("Id") != ApplicationProgramId || definition.Descendants().Take(100_001).Count() > 100_000
            || Encoding.UTF8.GetByteCount(definition.ToString(SaveOptions.DisableFormatting)) > 8 * 1024 * 1024)
            Fail("Only the bounded exact SP8 5018-15-17D9 application definition is supported.");
        if (definition.Descendants().Any(e => e.Name.LocalName is "Script" or "ParameterCalculation" or "Module" or "ModuleDef" or "Repeat"))
            Fail("Script/module/repeat application variants are outside the supported SP8 import profile.");
    }
    private static void ValidateBinding(Project project, DeviceInstance device, XDocument definition)
    {
        var resolved = Knxproj.LoadApplicationProgram(project, device.Hardware2ProgramRefId ?? device.ProductRefId);
        if (resolved is not null)
        { if (HashDefinition(resolved) != HashDefinition(definition)) Fail("Project application differs from the reviewed definition."); }
        else if ((device.Hardware2ProgramRefId ?? device.ProductRefId) != ApplicationProgramId)
            Fail("Hardware reference has no proven SP8 application resolution.");
        if (definition.Descendants().Count(e => e.Name.LocalName == "ApplicationProgram" && (string?)e.Attribute("Id") == ApplicationProgramId) != 1)
            Fail("Device does not resolve to the exact supported application.");
    }
    private static void ValidateOwner(Project project, DeviceInstance device)
    { if (ProjectQuery.DeviceList(project).Count(d => ReferenceEquals(d, device)) != 1 || ProjectQuery.DeviceList(project).Count(d => d.Id == device.Id) != 1) Fail("Missing/ambiguous device identity."); }
    private static Dictionary<string, XElement> Unique(IEnumerable<XElement> elements, string context)
    {
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in elements) if (!result.TryAdd(RequiredId(element), element)) Fail("Ambiguous " + context + ".");
        return result;
    }
    private static string RequiredId(XElement element)
    { var id = (string?)element.Attribute("Id") ?? ""; if (!id.StartsWith(Prefix, StringComparison.Ordinal)) Fail("A declaration lacks its exact full application id."); return id; }
    private static string ParameterId(int number) => Prefix + $"P-{number}_R-{number}";
    private static string Full(string id) => id.StartsWith(Prefix, StringComparison.Ordinal) ? id : Prefix + id;
    private static string Native(string id) => id.StartsWith(Prefix, StringComparison.Ordinal) ? id[Prefix.Length..] : id;
    private static ComObjectInstanceRef CopyObject(ComObjectInstanceRef value)
    {
        var copy = new ComObjectInstanceRef { Id = value.Id, ComObjectId = value.ComObjectId, Number = value.Number, Text = value.ExplicitText,
            FunctionText = value.ExplicitFunctionText, InheritedText = value.InheritedText, InheritedFunctionText = value.InheritedFunctionText,
            Device = value.Device, ChannelId = value.ChannelId };
        copy.Links.AddRange(value.Links); return copy;
    }
    private static ObjectMetadata Metadata(Fact fact) => new() { Dpt = fact.Dpt, Bits = fact.Bits, Flags = fact.Flags, Active = fact.Active, StructuralDeclaredNumber = fact.Number };
    private static ObjectMetadata CopyMetadata(ObjectMetadata value) => new()
    {
        Dpt = value.Dpt, Bits = value.Bits, Flags = value.Flags, Active = value.Active, StructuralPreviousActive = value.StructuralPreviousActive,
        StructuralDeclaredNumber = value.StructuralDeclaredNumber, ChannelId = value.ChannelId, ChannelSource = value.ChannelSource,
        Role = value.Role, SendingGroupId = value.SendingGroupId,
        StructuralDeclaredChannelId = value.StructuralDeclaredChannelId, StructuralDeclaredChannelName = value.StructuralDeclaredChannelName,
        Connectors = value.Connectors.ToDictionary(p => p.Key, p => p.Value with { }, StringComparer.Ordinal),
    };
    private static string HashDefinition(XDocument document) => Hash(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)));
    private static string Fingerprint(Project project) => Hash(Encoding.UTF8.GetBytes(Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting)
        + JsonSerializer.Serialize(project.Hardware) + JsonSerializer.Serialize(project.ObjectTextDefinitions.Select(p => new { Key = p.Key.ToString(), p.Value }))));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void ValidateText(string value)
    {
        if (value.Length > 255) Fail("Object/function text exceeds the native 255-character bound.");
        try { XmlConvert.VerifyXmlChars(value); } catch (XmlException) { Fail("Object/function text contains invalid XML characters."); }
    }
    private static InvalidDataException Invalid(string message) => new("Jung KSP import: " + message);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn] private static void Fail(string message) => throw Invalid(message);
}
