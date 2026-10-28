using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Knx.Catalog;

namespace Knx;

internal sealed record NativeProcedureControllerFact(string RefId, string ParameterId, string ParameterTypeId,
    int EverDeclaredOwnerCount, int ActiveNormalOwnerCount, bool Active, long? ActiveValue,
    string DeclarationHash, string PlacementHash);

/// <summary>Owned root controller facts with live freshness checks; no GUI snapshot or interpreter is used.</summary>
internal sealed partial class NativeProcedureEvaluationContext
{
    private const int StateLimit = 100000, ControllerLimit = 4096;
    private readonly Project? _project;
    private readonly DeviceInstance? _device;
    private readonly ArchivedImageSources.Inputs? _inputs;
    private readonly PreparedConditionalLoadProcedure? _prepared;
    private readonly string? _savedHash, _applicationHash, _masterHash;
    internal bool IsAvailable => Fingerprint is not null && Issues.Count == 0;
    internal IReadOnlyList<LoadProcedureIssue> Issues { get; }
    internal IReadOnlyDictionary<string, NativeProcedureControllerFact> Controllers { get; }
    internal string? Fingerprint { get; }
    internal string? SourceFingerprint { get; }
    internal string? SavedValuesHash => _savedHash;
    private sealed class Rejected(LoadProcedureIssue issue) : Exception { internal LoadProcedureIssue Issue => issue; }
    [DoesNotReturn]
    private static void Fail(LoadProcedureIssueCode code, string? id = null) => throw new Rejected(new(code, Safe(id) ? id : null));
    private static bool Safe(string? value) => value is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private static string Hash(object value) => ParameterPatchSourceBinding.Hash(value);
    private NativeProcedureEvaluationContext(LoadProcedureIssue issue)
    {
        Issues = Array.AsReadOnly(new[] { issue });
        Controllers = new Dictionary<string, NativeProcedureControllerFact>().ToFrozenDictionary(StringComparer.Ordinal);
    }
    private NativeProcedureEvaluationContext(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        PreparedConditionalLoadProcedure prepared, string savedHash, string applicationHash, string? masterHash,
        Dictionary<string, NativeProcedureControllerFact> controllers, string? activityHash)
    {
        _project = project; _device = device; _inputs = inputs; _prepared = prepared;
        _savedHash = savedHash; _applicationHash = applicationHash; _masterHash = masterHash;
        SourceFingerprint = inputs.Manifest.Binding!.Fingerprint;
        Controllers = controllers.ToFrozenDictionary(StringComparer.Ordinal); Issues = Array.AsReadOnly(Array.Empty<LoadProcedureIssue>());
        Fingerprint = Hash(new { version = 1, SourceFingerprint, applicationHash, masterHash, savedHash,
            prepared.GraphFingerprint, prepared.SelectedProfileHash, prepared.Request, activityHash,
            controllers = controllers.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray() });
    }
    internal static NativeProcedureEvaluationContext Create(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        PreparedConditionalLoadProcedure prepared)
    {
        try
        {
            if (!prepared.IsAvailable || !prepared.MatchesSources(inputs) || inputs.Application is null || inputs.Manifest.Binding is null
                || !inputs.Manifest.Binding.MatchesIdentity(project, device)) Fail(LoadProcedureIssueCode.ContextMismatch);
            var applicationHash = DocumentHash(inputs.Application, 128 * 1024 * 1024);
            if (applicationHash != inputs.Manifest.ApplicationDefinitionHash) Fail(LoadProcedureIssueCode.ContextMismatch);
            var masterHash = inputs.Master is null ? null : DocumentHash(inputs.Master, 16 * 1024 * 1024);
            var saved = Capture(project, device); var savedHash = Hash(saved.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray());
            var document = new XDocument(inputs.Application);
            ProfileGate(document.Root!, inputs.Manifest.ApplicationId!);
            var controllers = new Dictionary<string, NativeProcedureControllerFact>(StringComparer.Ordinal); string? activityHash = null;
            if (prepared.DeclaredControllerReferences.Count != 0 || prepared.ControllerReferences.Count != 0)
            {
                var source = new Source(document);
                foreach (var id in prepared.DeclaredControllerReferences) source.Declaration(id);
                if (prepared.ControllerReferences.Count != 0)
                    (controllers, activityHash) = source.Evaluate(prepared.ControllerReferences, saved);
            }
            var result = new NativeProcedureEvaluationContext(project, device, inputs, prepared, savedHash, applicationHash, masterHash, controllers, activityHash);
            if (!result.MatchesCurrent(prepared)) Fail(LoadProcedureIssueCode.InputsChanged);
            return result;
        }
        catch (Rejected failure) { return new(failure.Issue); }
        catch (Exception failure) when (failure is XmlException or FormatException or OverflowException or InvalidDataException or ArgumentException or InvalidOperationException)
        { return new(new(LoadProcedureIssueCode.InvalidApplication)); }
    }
    internal static LoadProcedureIssue? ValidateDeclaredControllers(ArchivedImageSources.Inputs inputs, IReadOnlyList<string> refs)
    {
        try
        {
            if (refs.Count > ControllerLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
            if (refs.Count == 0) return null;
            if (inputs.Application is null) Fail(LoadProcedureIssueCode.InvalidApplication);
            var source = new Source(inputs.Application!);
            foreach (var id in refs) source.Declaration(id);
            return null;
        }
        catch (Rejected failure) { return failure.Issue; }
        catch (Exception failure) when (failure is XmlException or FormatException or OverflowException or InvalidDataException or ArgumentException or InvalidOperationException)
        { return new(LoadProcedureIssueCode.InvalidControllerDeclaration); }
    }
    internal bool MatchesPrepared(PreparedConditionalLoadProcedure prepared)
        => IsAvailable && ReferenceEquals(prepared, _prepared) && prepared.IsAvailable
            && prepared.SourceFingerprint == SourceFingerprint && prepared.MatchesSources(_inputs!);
    internal bool MatchesCurrent(PreparedConditionalLoadProcedure prepared)
        => _project is not null && _device is not null && _inputs is not null && MatchesInputs(_project, _device, _inputs, prepared);
    internal bool MatchesInputs(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs, PreparedConditionalLoadProcedure prepared)
    {
        if (!ReferenceEquals(project, _project) || !ReferenceEquals(device, _device) || !MatchesPrepared(prepared)
            || inputs.Application is null || inputs.Manifest.Binding is null || !inputs.Manifest.Binding.MatchesIdentity(project, device)
            || inputs.Manifest.Binding.Fingerprint != SourceFingerprint || !prepared.MatchesSources(inputs)) return false;
        try
        {
            return DocumentHash(inputs.Application, 128 * 1024 * 1024) == _applicationHash
                && (inputs.Master is null ? null : DocumentHash(inputs.Master, 16 * 1024 * 1024)) == _masterHash
                && Hash(Capture(project, device).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray()) == _savedHash
                && (_sharedApplication is null || _sharedApplication.MatchesInputs(project, device, inputs));
        }
        catch (Exception failure) when (failure is Rejected or XmlException or FormatException or OverflowException or InvalidDataException or ArgumentException or InvalidOperationException)
        { return false; }
    }
    private static Dictionary<string, string> Capture(Project project, DeviceInstance device)
    {
        if (device.HasOpaqueBinaryData || device.BinaryData.Count != 0) Fail(LoadProcedureIssueCode.UnaccountedBinaryData);
        if (device.ParameterValues.Count > 100000) Fail(LoadProcedureIssueCode.LimitExceeded);
        var devices = ProjectQuery.DeviceList(project).Take(100001).ToArray();
        if (devices.Length > 100000) Fail(LoadProcedureIssueCode.LimitExceeded);
        if (devices.Count(d => d.Id == device.Id) != 1 || !devices.Any(d => ReferenceEquals(d, device))) Fail(LoadProcedureIssueCode.ContextMismatch);
        var result = new Dictionary<string, string>(StringComparer.Ordinal); long characters = 0;
        foreach (var pair in device.ParameterValues)
        {
            if (!Safe(pair.Key) || pair.Value is null || pair.Value.Length > 65536
                || (characters += pair.Key.Length + pair.Value.Length) > 4 * 1024 * 1024) Fail(LoadProcedureIssueCode.LimitExceeded);
            result.Add(pair.Key, pair.Value);
        }
        return result;
    }
    private static void ProfileGate(XElement root, string id)
    {
        if (KnownDcaRequirementsRegistry.RequiresDca(id)) Fail(LoadProcedureIssueCode.ExternalConfigurationUnaccounted);
        foreach (var element in root.DescendantsAndSelf())
            if (element.Attributes().Any(a => a.Name.LocalName is "Broken" or "DownloadInfoIncomplete" or "RequiresExternalSoftware"
                    && !string.IsNullOrWhiteSpace(a.Value) && a.Value is not ("false" or "False" or "0"))
                || element.Name.LocalName is "DCA" or "Dca" or "Plugin" or "EtsUiPlugin" or "EtsDataHandler" or "BinaryDataRef"
                || element.Name.LocalName == "Extension" && (element.Attributes().Any(a => a.Name.LocalName.StartsWith("Ets", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(a.Value))
                    || element.Elements().Any(c => c.Name.LocalName.StartsWith("Ets", StringComparison.Ordinal)))
                || element.Name.LocalName is "Parameter" or "ParameterRef" or "ParameterType" && !string.IsNullOrWhiteSpace((string?)element.Attribute("Plugin")))
                Fail(LoadProcedureIssueCode.ExternalConfigurationUnaccounted);
    }
    private static string DocumentHash(XNode node, int maximum)
    {
        var nodes = 0; long chars = 0; var stack = new Stack<(XNode Node, int Depth)>(); stack.Push((node, 0));
        while (stack.TryPop(out var entry))
        {
            if (++nodes > 1000000 || entry.Depth > 128) Fail(LoadProcedureIssueCode.LimitExceeded);
            if (entry.Node is XContainer container)
                foreach (var child in container.Nodes())
                { if (stack.Count >= 1000000) Fail(LoadProcedureIssueCode.LimitExceeded); stack.Push((child, entry.Depth + 1)); }
            if (entry.Node is XElement element)
            { chars += element.Name.ToString().Length; foreach (var attr in element.Attributes()) chars += attr.Name.ToString().Length + attr.Value.Length; }
            else if (entry.Node is XText text) chars += text.Value.Length;
            else if (entry.Node is XComment comment) chars += comment.Value.Length;
            else if (entry.Node is XProcessingInstruction pi) chars += pi.Target.Length + pi.Data.Length;
            if (chars > maximum) Fail(LoadProcedureIssueCode.LimitExceeded);
        }
        using var hash = SHA256.Create(); using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using (var writer = XmlWriter.Create(stream, new() { Encoding = new UTF8Encoding(false), CloseOutput = false, Indent = false })) node.WriteTo(writer);
        stream.FlushFinalBlock(); return Convert.ToHexStringLower(hash.Hash!);
    }

    private sealed record Declaration(string RefId, string ParameterId, string TypeId, XElement Parameter, XElement Reference,
        XElement Type, long Minimum, long Maximum, HashSet<long>? Choices, string Hash);
    private sealed class State(string kind, XElement? element = null, int parent = -1)
    {
        internal string Kind = kind;
        internal XElement? Element = element;
        internal int Parent = parent, Controller = -1;
        internal string? RefId;
        internal StrictIntegerCondition? Test;
        internal bool Default, Matches, Active;
        internal readonly List<int> Children = [], Dependencies = [], Dependents = [];
    }
    private sealed record ActivityPreparation(List<State> States, Dictionary<string, List<int>> Owners,
        HashSet<string> Relevant, HashSet<int> Used, Dictionary<string, long> Values);
    private sealed partial class Source
    {
        private readonly XElement _app, _static;
        private readonly XNamespace _ns;
        private readonly XElement[] _nodes;
        private readonly Dictionary<string, XElement> _parameters, _references, _types;
        private readonly Dictionary<string, Declaration> _declarations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (long Minimum, long Maximum, HashSet<long>? Choices, string Hash)> _typeFacts = new(StringComparer.Ordinal);
        private readonly Dictionary<XElement, int> _keys;
        internal Source(XDocument document)
        {
            _ = DocumentHash(document, 128 * 1024 * 1024);
            var root = document.Root;
            if (root is null || root.Name.LocalName != "KNX" || root.Name.NamespaceName is not
                ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23")) Fail(LoadProcedureIssueCode.InvalidApplication);
            _ns = root!.Name.Namespace; _nodes = root.DescendantsAndSelf().Take(500001).ToArray();
            if (_nodes.Length > 500000) Fail(LoadProcedureIssueCode.LimitExceeded);
            _keys = _nodes.Select((element, key) => (element, key)).ToDictionary(p => p.element, p => p.key);
            var apps = _nodes.Where(e => e.Name.LocalName == "ApplicationProgram").Take(2).ToArray();
            if (apps.Length != 1 || apps[0].Name != _ns + "ApplicationProgram") Fail(LoadProcedureIssueCode.InvalidApplication);
            _app = apps[0]; _static = One(_app, "Static", true)!;
            _parameters = Index(One(_static, "Parameters", false)?.Descendants(_ns + "Parameter") ?? [], "Parameter");
            _references = Index(One(_static, "ParameterRefs", false)?.Elements() ?? [], "ParameterRef");
            _types = Index(One(_static, "ParameterTypes", false)?.Elements() ?? [], "ParameterType");
            foreach (var pair in _references)
            {
                var target = (string?)pair.Value.Attribute("RefId");
                if (!Safe(target) || !_parameters.ContainsKey(target!) || pair.Value.HasElements) Fail(LoadProcedureIssueCode.InvalidControllerDeclaration, pair.Key);
            }
        }
        private XElement? One(XElement parent, string name, bool required)
        {
            var rows = parent.Elements().Where(e => e.Name.LocalName == name).Take(2).ToArray();
            if (rows.Length > 1 || rows.Any(e => e.Name != _ns + name) || required && rows.Length != 1) Fail(LoadProcedureIssueCode.InvalidApplication);
            return rows.FirstOrDefault();
        }
        private Dictionary<string, XElement> Index(IEnumerable<XElement> elements, string kind)
        {
            var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var e in elements)
            {
                var id = (string?)e.Attribute("Id");
                if (e.Name != _ns + kind || !Safe(id) || result.Count >= 100000 || !result.TryAdd(id!, e)) Fail(LoadProcedureIssueCode.InvalidControllerDeclaration, id);
            }
            return result;
        }
        internal Declaration Declaration(string id)
        {
            if (!Safe(id)) Fail(LoadProcedureIssueCode.InvalidControllerDeclaration);
            if (_declarations.TryGetValue(id, out var existing)) return existing;
            if (_declarations.Count >= ControllerLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
            if (!_references.TryGetValue(id, out var reference)) Fail(LoadProcedureIssueCode.UnknownController, id);
            var parameterId = (string?)reference!.Attribute("RefId");
            XElement? parameter = null;
            if (parameterId is null || !_parameters.TryGetValue(parameterId, out parameter)) Fail(LoadProcedureIssueCode.InvalidControllerDeclaration, id);
            var typeId = (string?)parameter!.Attribute("ParameterType");
            XElement? type = null;
            if (parameter.Parent?.Name != _ns + "Parameters" || parameter.Parent.Parent != _static || parameter.Attribute("BaseValue") is not null
                || parameter.Attribute("ParameterTypeId") is not null || reference.Attribute("ParameterType") is not null || reference.Attribute("ParameterTypeId") is not null
                || !Safe(typeId) || !_types.TryGetValue(typeId!, out type)) Fail(LoadProcedureIssueCode.InvalidControllerDeclaration, id);
            if (!_typeFacts.TryGetValue(typeId!, out var bounds))
            {
                var parsed = ReadType(type!, id);
                bounds = (parsed.Minimum, parsed.Maximum, parsed.Choices, DocumentHash(type!, 4 * 1024 * 1024));
                _typeFacts.Add(typeId!, bounds);
            }
            var declaration = new Declaration(id, parameterId!, typeId!, parameter, reference, type!, bounds.Minimum, bounds.Maximum, bounds.Choices,
                Hash(new { parameter = DocumentHash(parameter, 1024 * 1024), reference = DocumentHash(reference, 1024 * 1024), type = bounds.Hash }));
            _declarations.Add(id, declaration); return declaration;
        }
        private void Attributes(XElement element, LoadProcedureIssueCode code, params string[] allowed)
        {
            if (element.Name.Namespace != _ns || element.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || !allowed.Contains(a.Name.LocalName, StringComparer.Ordinal)))
                || element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))) Fail(code);
        }
        private (long Minimum, long Maximum, HashSet<long>? Choices) ReadType(XElement type, string id)
        {
            void Invalid() => Fail(LoadProcedureIssueCode.InvalidControllerDeclaration, id);
            Attributes(type, LoadProcedureIssueCode.InvalidControllerDeclaration, "Id", "Name", "InternalDescription", "Plugin", "ValidationErrorRef");
            if (type.Elements().Take(2).Count() != 1) Invalid();
            var shape = type.Elements().Single(); var bits = 0;
            if (!int.TryParse((string?)shape.Attribute("SizeInBit"), NumberStyles.None, CultureInfo.InvariantCulture, out bits)) Invalid();
            if (shape.Name == _ns + "TypeNumber")
            {
                Attributes(shape, LoadProcedureIssueCode.InvalidControllerDeclaration, "Type", "SizeInBit", "minInclusive", "maxInclusive", "Increment", "UIHint", "DisplayOffset", "DisplayFactor");
                var minimum = 0L; var maximum = 0L; var signed = (string?)shape.Attribute("Type") == "signedInt";
                if (shape.HasElements || bits is < 1 or > 32 || (string?)shape.Attribute("Type") is not ("signedInt" or "unsignedInt")
                    || !Canonical((string?)shape.Attribute("minInclusive"), out minimum) || !Canonical((string?)shape.Attribute("maxInclusive"), out maximum) || minimum > maximum) Invalid();
                var widthMinimum = signed ? -(1L << (bits - 1)) : 0; var widthMaximum = signed ? (1L << (bits - 1)) - 1 : (1L << bits) - 1;
                if (shape.Attribute("Increment") is { } increment && !Canonical(increment.Value, out _)) Invalid();
                if (shape.Attribute("UIHint") is { } hint && hint.Value is not ("Slider" or "CheckBox" or "ProgressBar")) Invalid();
                foreach (var name in new[] { "DisplayOffset", "DisplayFactor" })
                    if (shape.Attribute(name) is { } value && !double.IsFinite(XmlConvert.ToDouble(value.Value))) Invalid();
                var effectiveMinimum = Math.Max(minimum, widthMinimum); var effectiveMaximum = Math.Min(maximum, widthMaximum);
                if (effectiveMinimum > effectiveMaximum) Invalid();
                return (effectiveMinimum, effectiveMaximum, null);
            }
            Attributes(shape, LoadProcedureIssueCode.InvalidControllerDeclaration, "Base", "SizeInBit", "UIHint");
            if (shape.Name != _ns + "TypeRestriction" || (string?)shape.Attribute("Base") != "Value" || bits is < 1 or > 1048575
                || shape.Attribute("UIHint") is { } enumHint && enumHint.Value is not ("Text" or "DropDown" or "Buttons" or "Segmented")) Invalid();
            var count = shape.Elements().Take(4097).Count();
            if (count > 4096) Fail(LoadProcedureIssueCode.LimitExceeded, id);
            if (count == 0) Invalid();
            var choices = new HashSet<long>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var choice in shape.Elements())
            {
                Attributes(choice, LoadProcedureIssueCode.InvalidControllerDeclaration, "Id", "Value", "Text", "Icon", "PictureAlignment", "DisplayOrder");
                var choiceId = (string?)choice.Attribute("Id"); var value = 0L;
                if (choice.Name != _ns + "Enumeration" || choice.HasElements || !Safe(choiceId) || !ids.Add(choiceId!)
                    || !Canonical((string?)choice.Attribute("Value"), out value) || value < 0 || value > uint.MaxValue || bits < 32 && value >= (1L << bits) || !choices.Add(value)) Invalid();
                XmlConvert.VerifyNCName(choiceId!);
                if (choice.Attribute("DisplayOrder") is { } display && !int.TryParse(display.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) Invalid();
                if (choice.Attribute("PictureAlignment") is { } alignment && alignment.Value is not ("Left" or "Center" or "Right")) Invalid();
            }
            return (0, uint.MaxValue, choices);
        }
        private static bool Canonical(string? raw, out long value)
        {
            value = 0;
            return raw is { Length: > 0 and <= 64 } && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value)
                && value.ToString(CultureInfo.InvariantCulture) == raw;
        }
        internal ActivityPreparation PrepareActivity(IReadOnlyList<string> seeds, IReadOnlyDictionary<string, string> saved)
        {
            if (seeds.Count > ControllerLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
            if (_nodes.Any(e => e.Name.LocalName == "ParameterCalculation")) Fail(LoadProcedureIssueCode.UnsupportedActivationDependency);
            var dynamic = One(_app, "Dynamic", true)!; var states = new List<State> { new("Root") };
            var owners = new Dictionary<string, List<int>>(StringComparer.Ordinal); var assignments = new List<XElement>();
            var objectDefinitions = Index(One(_static, "ComObjectTable", false)?.Elements() ?? [], "ComObject");
            var objectRefs = Index(One(_static, "ComObjectRefs", false)?.Elements() ?? [], "ComObjectRef");
            foreach (var pair in objectRefs)
                if (!objectDefinitions.ContainsKey((string?)pair.Value.Attribute("RefId") ?? "")) Fail(LoadProcedureIssueCode.InvalidControllerDeclaration, pair.Key);
            int Add(State state)
            {
                if (states.Count >= StateLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
                var key = states.Count; states.Add(state); states[state.Parent].Children.Add(key); return key;
            }
            void Walk(IEnumerable<XElement> children, int parent, int depth)
            {
                if (depth > 64) Fail(LoadProcedureIssueCode.LimitExceeded);
                foreach (var element in children)
                {
                    if (element.Name.Namespace != _ns) Fail(LoadProcedureIssueCode.UnsupportedStructureActivation);
                    var kind = element.Name.LocalName;
                    switch (kind)
                    {
                        case "Channel": case "ChannelIndependentBlock": case "ParameterBlock":
                            var wrapper = Add(new(kind, element, parent)); Walk(element.Elements(), wrapper, depth + 1); break;
                        case "choose":
                            Attributes(element, LoadProcedureIssueCode.UnsupportedActivationCondition, "ParamRefId", "InternalDescription");
                            var selector = (string?)element.Attribute("ParamRefId");
                            if (selector is null || !_references.ContainsKey(selector)) Fail(LoadProcedureIssueCode.UnknownController, selector);
                            var choose = Add(new(kind, element, parent) { RefId = selector });
                            if (!element.Elements().Any() || element.Elements().Any(e => e.Name != _ns + "when")) Fail(LoadProcedureIssueCode.UnsupportedActivationCondition, selector);
                            Walk(element.Elements(), choose, depth + 1);
                            if (states[choose].Children.Count(k => states[k].Default) > 1) Fail(LoadProcedureIssueCode.UnsupportedActivationCondition, selector);
                            break;
                        case "when":
                            Attributes(element, LoadProcedureIssueCode.UnsupportedActivationCondition, "test", "default", "InternalDescription");
                            if (states[parent].Kind != "choose") Fail(LoadProcedureIssueCode.UnsupportedActivationCondition);
                            var fallback = (string?)element.Attribute("default") switch
                            { null or "false" or "0" => false, "true" or "1" => true, _ => throw new Rejected(new(LoadProcedureIssueCode.UnsupportedActivationCondition)) };
                            var text = (string?)element.Attribute("test"); StrictIntegerCondition? test = null;
                            if (fallback && text is not null || !fallback && (text is null || !StrictIntegerCondition.TryParse(text, out test))) Fail(LoadProcedureIssueCode.UnsupportedActivationCondition);
                            var branch = Add(new(kind, element, parent) { Default = fallback, Test = test }); Walk(element.Elements(), branch, depth + 1); break;
                        case "ParameterRefRef":
                            Attributes(element, LoadProcedureIssueCode.UnsupportedStructureActivation, "RefId", "HelpContext", "IndentLevel", "InternalDescription", "Cell", "Icon");
                            var id = (string?)element.Attribute("RefId");
                            if (element.HasElements || id is null || !_references.ContainsKey(id)) Fail(LoadProcedureIssueCode.UnknownController, id);
                            var placement = Add(new(kind, element, parent) { RefId = id });
                            if (!owners.TryGetValue(id!, out var list)) owners.Add(id!, list = []); list.Add(placement); break;
                        case "ComObjectRefRef":
                            Attributes(element, LoadProcedureIssueCode.UnsupportedStructureActivation, "RefId", "InternalDescription");
                            if (element.HasElements || !objectRefs.ContainsKey((string?)element.Attribute("RefId") ?? "")) Fail(LoadProcedureIssueCode.InvalidControllerDeclaration);
                            break;
                        case "Assign":
                            Attributes(element, LoadProcedureIssueCode.UnsupportedActivationDependency, "TargetParamRefRef", "SourceParamRefRef", "Value", "InternalDescription");
                            var target = (string?)element.Attribute("TargetParamRefRef"); var source = (string?)element.Attribute("SourceParamRefRef");
                            if (element.HasElements || target is null || !_references.ContainsKey(target) || source is not null && !_references.ContainsKey(source)
                                || source is null && element.Attribute("Value") is null) Fail(LoadProcedureIssueCode.InvalidControllerDeclaration);
                            if (source is not null && element.Attribute("Value") is not null) Fail(LoadProcedureIssueCode.UnsupportedActivationDependency, target);
                            assignments.Add(element); break;
                        case "Rows": case "Columns": case "Row": case "Column": Metadata(element, depth); break;
                        case "ParameterSeparator": case "Rename": case "ParameterBlockRename":
                            if (element.HasElements) Fail(LoadProcedureIssueCode.UnsupportedStructureActivation); break;
                        default: Fail(LoadProcedureIssueCode.UnsupportedStructureActivation); break;
                    }
                }
            }
            void Metadata(XElement element, int depth)
            {
                if (depth > 64 || element.Name.Namespace != _ns || element.Name.LocalName is not ("Rows" or "Columns" or "Row" or "Column")) Fail(LoadProcedureIssueCode.UnsupportedStructureActivation);
                foreach (var child in element.Elements()) Metadata(child, depth + 1);
            }
            Walk(dynamic.Elements(), 0, 0);
            var relevant = seeds.ToHashSet(StringComparer.Ordinal); var queue = new Queue<string>(relevant); var used = new HashSet<int> { 0 };
            while (queue.TryDequeue(out var id))
            {
                if (relevant.Count > ControllerLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
                _ = Declaration(id);
                if (!owners.TryGetValue(id, out var placements) || placements.Count == 0) Fail(LoadProcedureIssueCode.NeverPlacedController, id);
                foreach (var owner in placements!)
                    for (var at = owner; at >= 0; at = states[at].Parent)
                    {
                        if (!used.Add(at)) break;
                        if (states[at].Kind == "choose" && relevant.Add(states[at].RefId!)) queue.Enqueue(states[at].RefId!);
                    }
            }
            var watchedParameters = relevant.Select(id => Declaration(id).ParameterId).ToHashSet(StringComparer.Ordinal);
            var writers = _references.Where(p => watchedParameters.Contains((string)p.Value.Attribute("RefId")!)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var assign in assignments)
                if (writers.Contains((string)assign.Attribute("TargetParamRefRef")!) || assign.Attribute("SourceParamRefRef") is { } source && writers.Contains(source.Value))
                    Fail(LoadProcedureIssueCode.UnsupportedActivationDependency, (string?)assign.Attribute("TargetParamRefRef"));
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var id in relevant)
            {
                var declaration = Declaration(id); var raw = (string?)declaration.Reference.Attribute("Value") ?? (string?)declaration.Parameter.Attribute("Value");
                var local = id.StartsWith((string)_app.Attribute("Id")! + "_", StringComparison.Ordinal) ? id[(((string)_app.Attribute("Id")!).Length + 1)..] : id;
                if (saved.TryGetValue(id, out var full)) raw = full;
                if (local != id && saved.TryGetValue(local, out var localValue))
                { if (full is not null && full != localValue) Fail(LoadProcedureIssueCode.InvalidControllerValue, id); raw = localValue; }
                if (!Canonical(raw, out var value) || value < declaration.Minimum || value > declaration.Maximum
                    || declaration.Choices is not null && !declaration.Choices.Contains(value)) Fail(LoadProcedureIssueCode.InvalidControllerValue, id);
                values.Add(id, value);
            }
            var referenceNodes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var id in relevant.Order(StringComparer.Ordinal))
            {
                if (states.Count >= StateLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
                var key = states.Count; states.Add(new("Reference") { RefId = id }); referenceNodes.Add(id, key); used.Add(key);
                states[key].Dependencies.AddRange(owners[id]);
            }
            foreach (var key in used.ToArray())
            {
                var state = states[key]; if (state.Parent >= 0) state.Dependencies.Add(state.Parent);
                if (state.Kind != "choose") continue;
                state.Controller = referenceNodes[state.RefId!]; state.Dependencies.Add(state.Controller);
                var any = state.Children.Any(k => !states[k].Default && states[k].Test!.Matches(values[state.RefId!]));
                foreach (var child in state.Children) states[child].Matches = states[child].Default ? !any : states[child].Test!.Matches(values[state.RefId!]);
            }
            return new(states, owners, relevant, used, values);
        }
        internal (Dictionary<string, NativeProcedureControllerFact>, string) Evaluate(IReadOnlyList<string> seeds, Dictionary<string, string> saved)
        {
            var prepared = PrepareActivity(seeds, saved);
            var states = prepared.States; var owners = prepared.Owners; var relevant = prepared.Relevant;
            var used = prepared.Used; var values = prepared.Values;
            var degrees = new Dictionary<int, int>();
            foreach (var key in used)
            {
                degrees[key] = states[key].Dependencies.Count;
                foreach (var dependency in states[key].Dependencies) states[dependency].Dependents.Add(key);
            }
            var ready = new Queue<int>(used.Where(k => degrees[k] == 0)); var processed = 0;
            while (ready.TryDequeue(out var key))
            {
                processed++; var state = states[key];
                state.Active = state.Kind switch
                {
                    "Root" => true, "Reference" => state.Dependencies.Any(k => states[k].Active),
                    "choose" => states[state.Parent].Active && states[state.Controller].Active,
                    "when" => states[state.Parent].Active && state.Matches, _ => states[state.Parent].Active,
                };
                foreach (var dependent in state.Dependents) if (--degrees[dependent] == 0) ready.Enqueue(dependent);
            }
            if (processed != used.Count) Fail(LoadProcedureIssueCode.ActivationCycle);
            var facts = new Dictionary<string, NativeProcedureControllerFact>(StringComparer.Ordinal);
            foreach (var id in relevant.Order(StringComparer.Ordinal))
            {
                var declaration = Declaration(id); var activeCount = owners[id].Count(k => states[k].Active);
                var placementHash = Hash(owners[id].Select(k => new { key = k, state = DocumentHash(states[k].Element!, 1024 * 1024) }).ToArray());
                facts.Add(id, new(id, declaration.ParameterId, declaration.TypeId, owners[id].Count, activeCount, activeCount != 0,
                    activeCount == 0 ? null : values[id], declaration.Hash, placementHash));
            }
            // The application hash binds declarations; state keys avoid repeatedly hashing shared ancestor subtrees.
            return (facts, Hash(used.Order().Select(k => new { key = k, states[k].Kind, states[k].RefId, states[k].Active, states[k].Matches,
                sourceKey = states[k].Element is null ? (int?)null : _keys[states[k].Element!] }).ToArray()));
        }
    }
}
