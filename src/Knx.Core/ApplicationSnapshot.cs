using System.Globalization;
using System.Xml.Linq;
using Knx.Workbench;

namespace Knx;

/// <summary>A complete, detached evaluation. Callers publish it only after all structural and value checks pass.</summary>
public sealed class ApplicationSnapshot
{
    public List<ParameterInstance> Instances { get; }
    public List<ParameterType> Types { get; }
    public List<ComObject> ComObjects { get; }
    public DynamicEvaluation Evaluation { get; }
    public bool SupportsLiveStructure { get; private set; } = true;
    public bool IsValid => SupportsLiveStructure && !Evaluation.StructureFailed && Evaluation.Converged && !Evaluation.DepthLimitReached
        && Evaluation.ScriptFailures == 0 && Evaluation.InvalidAssignments.Count == 0 && Evaluation.ObjectActivityComplete;
    public IReadOnlyDictionary<string, int> ModuleObjectNumbers { get; }
    /// <summary>Declared object references, excluding unreferenced inactive object definitions.</summary>
    public IReadOnlyDictionary<string, int> ObjectReferenceNumbers { get; }
    public IReadOnlySet<string> ActiveObjects { get; }
    public IReadOnlyList<ApplicationModuleInstance> ModuleInstances { get; }
    private readonly string? _programId;
    private readonly Dictionary<string, (XElement Definition, XElement? Reference)> _objects = new(StringComparer.Ordinal);

    private ApplicationSnapshot(XDocument document, IReadOnlyDictionary<string, string?> values,
        IParameterScriptRuntime? scripts, string? edited, ParameterProfileContext? profile)
    {
        var parsed = Ksxml.Parse(document, out var evaluation, values, scripts, edited, editableStructure: true, profile: profile);
        Instances = parsed.Instances.Where(i => i.ParameterTypeId is not null).ToList();
        Types = parsed.Types;
        ComObjects = parsed.ComObjects;
        Evaluation = evaluation;
        if (Instances.Any(i => string.IsNullOrWhiteSpace(i.Id)) || Instances.Select(i => i.Id).Distinct().Count() != Instances.Count)
            throw new InvalidDataException("Missing or ambiguous parameter reference ids.");
        var root = evaluation.ExpandedRoot!;
        ModuleInstances = ApplicationModules.ReadInstances(root);
        _programId = (string?)root.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "ApplicationProgram")?.Attribute("Id");
        var placements = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ComObjectRefRef").ToList();
        var placedIds = placements.Attributes("RefId").Select(a => LocalId(a.Value)).ToHashSet(StringComparer.Ordinal);
        var declaredObjects = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ComObject"
            && !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef"))
            .ToList();
        if (declaredObjects.GroupBy(e => (string?)e.Attribute("Id")).Any(g => g.Count() != 1))
            throw new InvalidDataException("Ambiguous object ids.");
        var definitions = declaredObjects.ToDictionary(e => (string?)e.Attribute("Id") ?? throw new InvalidDataException("Object id required."));
        var references = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ComObjectRef"
            && !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef")).ToList();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var unresolvedReferences = false;
        foreach (var reference in references)
        {
            var id = (string?)reference.Attribute("Id") ?? throw new InvalidDataException("Object reference id required.");
            var target = (string?)reference.Attribute("RefId") ?? "";
            if (!definitions.TryGetValue(target, out var definition))
            {
                unresolvedReferences |= id.Contains("_MI-", StringComparison.Ordinal) || placedIds.Contains(LocalId(id));
                continue;
            }
            referenced.Add(target);
            if ((id.Contains("_MI-", StringComparison.Ordinal) || placedIds.Contains(LocalId(id)))
                && !_objects.TryAdd(LocalId(id), (definition, reference)))
                throw new InvalidDataException("Ambiguous module object reference ids.");
        }
        foreach (var (id, definition) in definitions)
            if (!referenced.Contains(id) && id.Contains("_MI-", StringComparison.Ordinal) && !_objects.TryAdd(LocalId(id), (definition, null)))
                throw new InvalidDataException("Ambiguous module object ids.");
        ModuleObjectNumbers = _objects.ToDictionary(p => p.Key, p => Number(p.Value.Definition, p.Value.Reference));
        ObjectReferenceNumbers = _objects.Where(p => p.Value.Reference is not null)
            .ToDictionary(p => p.Key, p => ModuleObjectNumbers[p.Key]);
        ActiveObjects = evaluation.ActiveObjectReferences.Select(LocalId).ToHashSet(StringComparer.Ordinal);
        var knownPlacements = placements.Where(SupportedPlacement).Attributes("RefId").Select(a => LocalId(a.Value)).ToHashSet(StringComparer.Ordinal);
        // A bare module definition has no activation state to evaluate. Retain its facts so saved
        // inactive objects can be retired, but never treat it as a missing dynamic reference.
        if (evaluation.StructuralParameters.Count > 0 && (!evaluation.ObjectActivityComplete || unresolvedReferences
            || placements.Any(e => !SupportedPlacement(e)
                || !ObjectReferenceNumbers.ContainsKey(LocalId((string?)e.Attribute("RefId") ?? "")))
            || ObjectReferenceNumbers.Keys.Any(id => !knownPlacements.Contains(id))))
        {
            SupportsLiveStructure = false;
            evaluation.Unsupported = evaluation.Unsupported.Append("Object activation is not supported for live structure editing.").ToArray();
            foreach (var instance in Instances.Where(i => evaluation.StructuralParameters.Contains(i.Id))) instance.IsEnabled = false;
        }
    }

    public static ApplicationSnapshot Evaluate(XDocument document, IReadOnlyDictionary<string, string?> values,
        IParameterScriptRuntime? scripts = null, string? editedReference = null, ParameterProfileContext? profile = null)
        => new(document, values, scripts, editedReference, profile);

    private string LocalId(string id) => _programId is { Length: > 0 } && id.StartsWith(_programId + "_", StringComparison.Ordinal)
        ? id[(_programId.Length + 1)..] : id;

    private static bool SupportedPlacement(XElement reference)
    {
        foreach (var parent in reference.Ancestors())
        {
            if (parent.Name.LocalName == "Dynamic") return true;
            if (parent.Name.LocalName is not ("ParameterBlock" or "Channel" or "ChannelIndependentBlock" or "choose" or "when")) return false;
        }
        return false;
    }

    private static int Number(XElement definition, XElement? reference) =>
        int.TryParse((string?)reference?.Attribute("Number") ?? (string?)definition.Attribute("Number"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number is >= 0 and <= 65535
            ? number : throw new InvalidDataException("Invalid module object number.");

    /// <summary>Validates the complete object transition before returning its commit action. No project mutation during preparation.</summary>
    public Action PrepareObjectUpdate(Project project, DeviceInstance device, ApplicationSnapshot previous)
    {
        if (!IsValid || !previous.IsValid) throw new InvalidDataException("Unsupported application structure.");
        if (device.ComObjectRefs.GroupBy(o => LocalId(o.Id)).Any(g => g.Count() != 1))
            throw new InvalidDataException("Ambiguous saved module object ids.");
        var existing = device.ComObjectRefs.ToDictionary(o => LocalId(o.Id), StringComparer.Ordinal);
        var updates = new List<(ComObjectInstanceRef Object, ObjectMetadata Metadata, bool Added, bool Present, int Number,
            int? DeclaredNumber, ObjectTextDefinition? Text, Action? Initialize)>();
        foreach (var id in previous._objects.Keys.Concat(_objects.Keys).Distinct(StringComparer.Ordinal))
        {
            var declared = _objects.TryGetValue(id, out var declaration);
            var present = declared && ActiveObjects.Contains(id);
            var added = !existing.TryGetValue(id, out var instance);
            if (added && !present) continue;
            instance ??= new ComObjectInstanceRef { Id = id, ComObjectId = LocalId((string)declaration.Definition.Attribute("Id")!) };
            var key = new ObjectKey(device.Id, instance.Id);
            var metadata = project.Workbench.Objects.GetValueOrDefault(key.ToString()) ?? new ObjectMetadata();
            ObjectTextDefinition? text = null;
            Action? initialize = null;
            if (declared)
            {
                string? Pick(string name) => (string?)declaration.Reference?.Attribute(name) ?? (string?)declaration.Definition.Attribute(name);
                var reference = Pick("TextParameterRefId");
                var defaults = Evaluation.Parameters;
                text = new(Pick("Text"), Pick("FunctionText"), reference,
                    reference is null ? null : defaults.GetValueOrDefault(reference)?.DeclaredValue,
                    reference is null || defaults.ContainsKey(reference),
                    declaration.Reference?.Attribute("TextParameterRefId") is not null
                        ? declaration.Reference.Annotation<TextArgumentBinding>()
                        : declaration.Definition.Annotation<TextArgumentBinding>() ?? declaration.Reference?.Annotation<TextArgumentBinding>(),
                    ApplicationChannels.Read(Evaluation, device, (string?)declaration.Reference?.Attribute("Id") ?? id, _programId, instance.ChannelId));
                // A new declaration owns these facts. Existing user connector/role metadata stays untouched.
                if (added)
                {
                    var dpt = DptId.FromDeclaration(Pick("DatapointType"));
                    var bits = Ksxml.ParseObjectSize(Pick("ObjectSize"));
                    var flags = Pick("CommunicationFlag") == "Enabled" ? "C" + string.Concat(
                        new[] { ("ReadFlag", "R"), ("WriteFlag", "W"), ("TransmitFlag", "T"), ("UpdateFlag", "U"), ("ReadOnInitFlag", "I") }
                            .Where(f => Pick(f.Item1) == "Enabled").Select(f => f.Item2)) : metadata.Flags;
                    initialize = () => { metadata.Dpt = dpt; metadata.Bits = bits > 0 ? bits : null; metadata.Flags = flags; };
                }
            }
            var beforeNumber = previous.ModuleObjectNumbers.TryGetValue(id, out var before)
                ? before : metadata.StructuralDeclaredNumber;
            var number = declared && (added || instance.Number == beforeNumber) ? ModuleObjectNumbers[id] : instance.Number;
            updates.Add((instance, metadata, added, present, number, declared ? ModuleObjectNumbers[id] : beforeNumber, text, initialize));
        }
        return () =>
        {
            foreach (var update in updates)
            {
                var key = new ObjectKey(device.Id, update.Object.Id);
                update.Initialize?.Invoke();
                if (update.Added) device.ComObjectRefs.Add(update.Object);
                project.Workbench.Objects[key.ToString()] = update.Metadata;
                update.Metadata.StructuralDeclaredNumber = update.DeclaredNumber;
                update.Object.Number = update.Number;
                if (update.Text is not null) project.ObjectTextDefinitions[key] = update.Text;
                if (update.Text?.Channel is { } channel) ApplicationChannels.Apply(update.Metadata, channel);
                if (update.Present)
                {
                    if (update.Metadata.StructuralPreviousActive is { } active)
                    {
                        update.Metadata.Active = active;
                        update.Metadata.StructuralPreviousActive = null;
                    }
                }
                else
                {
                    update.Metadata.StructuralPreviousActive ??= update.Metadata.Active;
                    update.Metadata.Active = false;
                }
            }
        };
    }
}
