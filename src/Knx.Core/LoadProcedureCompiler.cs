using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Knx;

/// <summary>Bounded compilation of declared primary-program controls. It neither executes controls nor builds tables or images.</summary>
public static partial class LoadProcedureCompiler
{
    private const int BlobLimit = 1_048_576, PayloadLimit = 4 * BlobLimit, ControlLimit = 4096, ExpandedLimit = 8192;
    private sealed class Rejected(LoadProcedureIssue issue) : Exception { internal LoadProcedureIssue Issue => issue; }
    [DoesNotReturn]
    private static void Fail(LoadProcedureIssueCode code, string? id = null, int? ordinal = null)
        => throw new Rejected(new(code, Safe(id) ? id : null, ordinal));
    private static bool Safe(string? text) => text is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl);
    private static bool Native(string? ns) => ns is "http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23";
    private sealed record Parsed(LoadControlKind Kind, LoadControlCondition Condition, LoadControlArgument[] Arguments,
        LoadControlErrorPolicy[] Errors, ImmutablePatchBytes? Data, ImmutablePatchBytes? Mask, int Ordinal);
    private sealed record AttributeRule(string Name, LoadControlArgumentKind Kind, ulong Maximum = 0, bool Required = false, string? Default = null);

    public static LoadProcedurePlan Compile(XDocument application, XDocument? archivedMaster, LoadProcedureRequest request)
    {
        try { return Create(application, archivedMaster, request); }
        catch (Rejected failure) { return new(failure.Issue); }
        catch (Exception failure) when (failure is XmlException or FormatException or OverflowException or InvalidDataException or ArgumentException or InvalidOperationException)
        { return new(new LoadProcedureIssue(LoadProcedureIssueCode.InvalidApplication)); }
    }

    private static LoadProcedurePlan Create(XDocument document, XDocument? master, LoadProcedureRequest request)
    {
        if (request is null || request.NativeSubtype is not ("all" or "par" or "grp" or "par,grp" or "ap1" or "cfg")
            || !Enum.IsDefined(request.Access) || (request.Parts & ~(LoadParts.Full | LoadParts.Parameters | LoadParts.Groups)) != 0)
            Fail(LoadProcedureIssueCode.InvalidRequest);
        Budget(document, 128 * BlobLimit);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "KNX" || !Native(root.Name.NamespaceName))
            Fail(LoadProcedureIssueCode.UnsupportedApplicationNamespace);
        var ns = root!.Name.Namespace;
        if (root.DescendantsAndSelf().Any(e => e.Name.Namespace != ns)) Fail(LoadProcedureIssueCode.InvalidApplication);
        var apps = root.Descendants(ns + "ApplicationProgram").Take(2).ToArray();
        if (apps.Length != 1) Fail(LoadProcedureIssueCode.InvalidApplication);
        var app = apps[0]; var id = (string?)app.Attribute("Id"); var maskId = (string?)app.Attribute("MaskVersion");
        if (!Safe(id) || !Safe(maskId)) Fail(LoadProcedureIssueCode.InvalidApplication);
        var styleText = (string?)app.Attribute("LoadProcedureStyle");
        if (styleText is not ("DefaultProcedure" or "ProductProcedure" or "MergedProcedure")) Fail(LoadProcedureIssueCode.InvalidApplication);
        var style = Enum.Parse<LoadProcedureStyle>(styleText);
        var stat = One(app, "Static", true)!; var definitionHash = DocumentHash(document);
        var requirements = new List<LoadProcedureRequirement>
        {
            new(LoadProcedureRequirementCode.SecondaryProgramOutsideScope), new(LoadProcedureRequirementCode.FinalizationNotCompiled),
            new(LoadProcedureRequirementCode.GlobalLoadPolicyNotCompiled), new(LoadProcedureRequirementCode.FixupsNotCompiled),
            new(LoadProcedureRequirementCode.GeneratedTablesNotBuilt), new(LoadProcedureRequirementCode.ParameterActivationUnresolved),
        };
        if (app.Descendants(ns + "ModuleDef").Descendants(ns + "LoadProcedures").Any()) requirements.Add(new(LoadProcedureRequirementCode.ModuleProceduresNotCompiled));
        if (app.Descendants(ns + "ParameterCalculation").Any()) requirements.Add(new(LoadProcedureRequirementCode.ScriptEvaluationNotPerformed));
        var bytes = 0; var count = 0;
        var productGroups = Groups(One(stat, "LoadProcedures", false), ref bytes, ref count);
        XElement? selectedConfiguration = null; XElement? template = null;
        if (master is not null) Budget(master, 16 * BlobLimit);
        var context = ArchivedResourceContext.Read(master, maskId, ns.NamespaceName);
        string? profileHash = null;
        if (master is not null)
        {
            if (context.Status != ArchivedResourceContextStatus.Supported) Fail(LoadProcedureIssueCode.InvalidMaster);
            selectedConfiguration = Configuration(master, maskId!);
            if (selectedConfiguration.DescendantsAndSelf().Any(e => e.Name.Namespace != selectedConfiguration.Name.Namespace))
                Fail(LoadProcedureIssueCode.InvalidMaster);
            var procedures = One(selectedConfiguration, "Procedures", false)?.Elements().ToArray() ?? [];
            if (procedures.Length > 64 || count + procedures.Sum(p => (long)p.Elements().Count()) > ControlLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var procedure in procedures)
            {
                Shape(procedure, ["ProcedureType", "ProcedureSubType", "Access"], procedure: true);
                var type = (string?)procedure.Attribute("ProcedureType"); var subtype = (string?)procedure.Attribute("ProcedureSubType");
                var access = AccessTokens((string?)procedure.Attribute("Access"));
                if (procedure.Name != selectedConfiguration.Name.Namespace + "Procedure" || type is not ("Load" or "Unload")
                    || subtype is not ("all" or "par" or "grp" or "par,grp" or "ap1" or "cfg" or "full" or "full,par" or "full,grp" or "auto"))
                    Fail(LoadProcedureIssueCode.InvalidMaster);
                foreach (var token in access)
                    if (!keys.Add(type + "|" + subtype + "|" + token)) Fail(LoadProcedureIssueCode.AmbiguousProcedure);
                if (type == "Load" && subtype == request.NativeSubtype && access.Contains("remote", StringComparer.Ordinal)) template = procedure;
            }
            profileHash = Hash(new { context.Fingerprint, procedures = procedures.Select(ElementHash).ToArray() });
        }
        else requirements.Add(new(LoadProcedureRequirementCode.MissingArchivedMaster));
        Parsed[] source;
        if (style == LoadProcedureStyle.ProductProcedure)
        {
            if (!productGroups.TryGetValue(-1, out source!)) Fail(LoadProcedureIssueCode.MissingProcedure);
        }
        else
        {
            if (template is null) Fail(LoadProcedureIssueCode.MissingProcedure);
            source = ParseControls(template, ref bytes, ref count);
        }
        var controls = new List<LoadProcedureControl>(); var decisions = new List<LoadMergeDecision>();
        void Add(Parsed parsed, LoadControlOrigin origin, int? templateOrdinal, byte? mergeId, bool insertion)
        {
            var effective = insertion && parsed.Condition == LoadControlCondition.Auto ? LoadControlCondition.All : parsed.Condition;
            if (!Applies(effective, request.Parts)) return;
            if (parsed.Kind == LoadControlKind.Merge) Fail(LoadProcedureIssueCode.UnsupportedControl, ordinal: parsed.Ordinal);
            if (controls.Count >= ExpandedLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
            controls.Add(new(controls.Count, parsed.Kind, origin, parsed.Ordinal, templateOrdinal, mergeId, parsed.Condition, effective,
                parsed.Arguments, parsed.Errors, parsed.Data, parsed.Mask));
        }
        foreach (var parsed in source)
        {
            if (parsed.Kind != LoadControlKind.Merge) { Add(parsed, style == LoadProcedureStyle.ProductProcedure ? LoadControlOrigin.Product : LoadControlOrigin.Mask, null, null, false); continue; }
            if (style != LoadProcedureStyle.MergedProcedure) Fail(LoadProcedureIssueCode.UnsupportedControl, ordinal: parsed.Ordinal);
            var mergeId = checked((byte)parsed.Arguments.Single(a => a.Name == "MergeId").UnsignedValue!.Value);
            if (!productGroups.TryGetValue(mergeId, out var group)) { decisions.Add(new(mergeId, parsed.Ordinal, LoadMergeDecisionKind.MissingGroupSkipped, 0)); continue; }
            var before = controls.Count;
            foreach (var member in group) Add(member, LoadControlOrigin.ProductMerge, parsed.Ordinal, mergeId, true);
            decisions.Add(new(mergeId, parsed.Ordinal, controls.Count == before ? LoadMergeDecisionKind.AllControlsFiltered : LoadMergeDecisionKind.Expanded, controls.Count - before));
        }
        var resources = Resources(stat, ref bytes, requirements);
        var tables = Tables(stat, context, style, resources, requirements);
        if (DocumentHash(document) != definitionHash) Fail(LoadProcedureIssueCode.InvalidApplication);
        var witness = Hash(new
        {
            version = 1, application = id, mask = maskId, style, request, definitionHash, profileHash,
            controls = controls.Select(c => new { c.Ordinal, c.Kind, c.Origin, c.SourceOrdinal, c.TemplateOrdinal, c.MergeId,
                c.DeclaredAppliesTo, c.EffectiveAppliesTo, c.Arguments, c.OnError, data = c.InlineData?.Sha256, mask = c.InlineMask?.Sha256 }).ToArray(),
            decisions, resources, tables, requirements,
        });
        return new(id!, maskId, style, request, definitionHash, profileHash, witness, controls.ToArray(), decisions.ToArray(), resources,
            tables, requirements.ToArray());
    }

    private static Dictionary<int, Parsed[]> Groups(XElement? container, ref int bytes, ref int count)
    {
        var groups = new Dictionary<int, Parsed[]>(); if (container is null) return groups;
        Shape(container, [], procedure: true);
        var raw = container.Elements().ToArray(); if (raw.Length > 64) Fail(LoadProcedureIssueCode.LimitExceeded);
        foreach (var group in raw)
        {
            if (group.Name != container.Name.Namespace + "LoadProcedure") Fail(LoadProcedureIssueCode.InvalidApplication);
            Shape(group, ["MergeId"], procedure: true);
            var key = group.Attribute("MergeId") is { } value ? checked((int)Unsigned(value.Value, byte.MaxValue)) : -1;
            if (key == 0 || groups.ContainsKey(key)) Fail(LoadProcedureIssueCode.AmbiguousProcedure);
            groups.Add(key, ParseControls(group, ref bytes, ref count));
        }
        return groups;
    }

    private static Parsed[] ParseControls(XElement group, ref int bytes, ref int count)
    {
        var parsed = new List<Parsed>();
        foreach (var element in group.Elements())
        {
            if (++count > ControlLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
            var ordinal = parsed.Count;
            if (element.Name.Namespace != group.Name.Namespace) Fail(LoadProcedureIssueCode.UnsupportedControl, ordinal: ordinal);
            if (!Kinds.TryGetValue(element.Name.LocalName, out var kind)) Fail(LoadProcedureIssueCode.UnsupportedControl, ordinal: ordinal);
            var rules = Rules(kind);
            var allowed = rules.Select(r => r.Name).Concat(["AppliesTo", "InternalDescription", "InlineData", "Mask"]).ToArray();
            Shape(element, allowed, procedure: false);
            var condition = Condition((string?)element.Attribute("AppliesTo") ?? "auto");
            var args = new List<LoadControlArgument>();
            foreach (var rule in rules)
            {
                var value = (string?)element.Attribute(rule.Name) ?? rule.Default;
                if (value is null) { if (rule.Required) Fail(LoadProcedureIssueCode.InvalidControl, ordinal: ordinal); continue; }
                args.Add(rule.Kind switch
                {
                    LoadControlArgumentKind.Unsigned => new(rule.Name, rule.Kind, UnsignedValue: Unsigned(value, rule.Maximum)),
                    LoadControlArgumentKind.Boolean => new(rule.Name, rule.Kind, BooleanValue: Boolean(value)),
                    _ => new(rule.Name, rule.Kind, TextValue: Text(value)),
                });
                if (rule.Name == "Range" && !Regex.IsMatch(value, @"\A[\[\(](-?[0-9]+)?,(-?[0-9]+)?[\)\]][su]?\z", RegexOptions.CultureInvariant))
                    Fail(LoadProcedureIssueCode.InvalidControl, ordinal: ordinal);
            }
            if (element.Attribute("InternalDescription") is { } description) args.Add(new("InternalDescription", LoadControlArgumentKind.Text, TextValue: Text(description.Value)));
            if (kind is LoadControlKind.Load or LoadControlKind.Unload or LoadControlKind.LoadCompleted or LoadControlKind.AbsoluteSegment or LoadControlKind.RelativeSegment or LoadControlKind.TaskSegment)
                Selector(element, "LsmIdx");
            if (kind is LoadControlKind.CompareProperty or LoadControlKind.WriteProperty or LoadControlKind.WriteRelativeMemory or LoadControlKind.LoadImageProperty)
                Selector(element, "ObjIdx");
            if (element.Attribute("InlineData") is not null && kind is not (LoadControlKind.CompareProperty or LoadControlKind.WriteProperty or LoadControlKind.WriteRelativeMemory)
                || element.Attribute("Mask") is not null && kind != LoadControlKind.CompareProperty) Fail(LoadProcedureIssueCode.InvalidControl, ordinal: ordinal);
            var data = Hex((string?)element.Attribute("InlineData"), ref bytes); var mask = Hex((string?)element.Attribute("Mask"), ref bytes);
            var errors = new List<LoadControlErrorPolicy>();
            foreach (var error in element.Elements())
            {
                if (error.Name != element.Name.Namespace + "OnError" || errors.Count >= 64) Fail(LoadProcedureIssueCode.InvalidControl, ordinal: ordinal);
                Shape(error, ["Cause", "Ignore", "MessageRef"], procedure: false, noChildren: true);
                var causeText = (string?)error.Attribute("Cause");
                if (causeText is not ("ResourceNotFound" or "CompareMismatch")) Fail(LoadProcedureIssueCode.InvalidControl, ordinal: ordinal);
                var cause = Enum.Parse<LoadControlErrorCause>(causeText);
                var reference = (string?)error.Attribute("MessageRef"); if (reference is not null && !Safe(reference)) Fail(LoadProcedureIssueCode.InvalidControl);
                errors.Add(new(cause, error.Attribute("Ignore") is { } ignore && Boolean(ignore.Value), reference));
            }
            parsed.Add(new(kind, condition, args.ToArray(), errors.ToArray(), data, mask, ordinal));
        }
        return parsed.ToArray();
    }

    private static void Selector(XElement node, string index)
    {
        if ((node.Attribute(index) is null) == (node.Attribute("ObjType") is null)
            || node.Attribute(index) is not null && node.Attribute("Occurrence") is not null)
            Fail(LoadProcedureIssueCode.InvalidControl);
    }
    private static readonly IReadOnlyDictionary<string, LoadControlKind> Kinds = new Dictionary<string, LoadControlKind>(StringComparer.Ordinal)
    {
        ["LdCtrlConnect"] = LoadControlKind.Connect, ["LdCtrlDisconnect"] = LoadControlKind.Disconnect,
        ["LdCtrlLoad"] = LoadControlKind.Load, ["LdCtrlUnload"] = LoadControlKind.Unload,
        ["LdCtrlLoadCompleted"] = LoadControlKind.LoadCompleted, ["LdCtrlAbsSegment"] = LoadControlKind.AbsoluteSegment,
        ["LdCtrlRelSegment"] = LoadControlKind.RelativeSegment, ["LdCtrlTaskSegment"] = LoadControlKind.TaskSegment,
        ["LdCtrlCompareProp"] = LoadControlKind.CompareProperty, ["LdCtrlWriteProp"] = LoadControlKind.WriteProperty,
        ["LdCtrlWriteRelMem"] = LoadControlKind.WriteRelativeMemory, ["LdCtrlLoadImageProp"] = LoadControlKind.LoadImageProperty,
        ["LdCtrlRestart"] = LoadControlKind.Restart, ["LdCtrlMapError"] = LoadControlKind.MapError, ["LdCtrlMerge"] = LoadControlKind.Merge,
    };
    private static AttributeRule U(string name, ulong maximum, bool required = false, string? value = null) => new(name, LoadControlArgumentKind.Unsigned, maximum, required, value);
    private static AttributeRule B(string name, bool required = false, string? value = null) => new(name, LoadControlArgumentKind.Boolean, Required: required, Default: value);
    private static AttributeRule[] Rules(LoadControlKind kind)
    {
        AttributeRule[] selector = [U("LsmIdx", 255), U("ObjType", 65535), U("Occurrence", 65535, value: "0")];
        AttributeRule[] property = [U("ObjIdx", 255), U("ObjType", 65535), U("Occurrence", 65535, value: "0")];
        AttributeRule[] range = [U("PropId", 65535, true), U("StartElement", 65535, value: "1"), U("Count", 65535, value: "1")];
        return kind switch
        {
            LoadControlKind.Load or LoadControlKind.Unload or LoadControlKind.LoadCompleted => selector,
            LoadControlKind.AbsoluteSegment => [.. selector, U("SegType", 255, true), U("Address", 65535, true), U("Size", 65535, true), U("Access", 255, true), U("MemType", 255, true), U("SegFlags", 255, true)],
            LoadControlKind.RelativeSegment => [.. selector, U("Size", uint.MaxValue, true), U("Mode", 255, true), U("Fill", 255, true)],
            LoadControlKind.TaskSegment => [.. selector, U("Address", 65535, true)],
            LoadControlKind.CompareProperty => [.. property, .. range, B("AllowCachedValue", value: "false"), B("Invert", value: "false"), U("RetryInterval", 65535, value: "0"), U("TimeOut", 65535, value: "0"), new("Range", LoadControlArgumentKind.Text)],
            LoadControlKind.WriteProperty => [.. property, .. range, B("Verify", true)],
            LoadControlKind.WriteRelativeMemory => [.. property, U("Offset", uint.MaxValue, true), U("Size", uint.MaxValue, true), B("Verify", true)],
            LoadControlKind.LoadImageProperty => [.. property, .. range],
            LoadControlKind.MapError => [U("LdCtrlFilter", 255, value: "0"), U("OriginalError", uint.MaxValue, true), U("MappedError", uint.MaxValue, true)],
            LoadControlKind.Merge => [U("MergeId", 255, true)],
            _ => [],
        };
    }

    private static LoadControlCondition Condition(string value) => value switch
    {
        "auto" => LoadControlCondition.Auto, "all" => LoadControlCondition.All, "full" => LoadControlCondition.Full,
        "par" => LoadControlCondition.Parameters, "grp" => LoadControlCondition.Groups, "full,par" => LoadControlCondition.FullOrParameters,
        "full,grp" => LoadControlCondition.FullOrGroups, "par,grp" => LoadControlCondition.ParametersAndGroups,
        _ => throw new Rejected(new(LoadProcedureIssueCode.InvalidControl)),
    };
    private static bool Applies(LoadControlCondition condition, LoadParts parts)
    {
        var full = (parts & LoadParts.Full) != 0; var parameters = (parts & LoadParts.Parameters) != 0; var groups = (parts & LoadParts.Groups) != 0;
        return condition switch
        {
            LoadControlCondition.Auto or LoadControlCondition.All => true, LoadControlCondition.Full => full,
            LoadControlCondition.Parameters => !full && parameters, LoadControlCondition.Groups => !full && groups,
            LoadControlCondition.ParametersAndGroups => !full && parameters && groups,
            LoadControlCondition.FullOrParameters => full || parameters, LoadControlCondition.FullOrGroups => full || groups,
            _ => false,
        };
    }

    private static LoadProcedureResourceReference[] Resources(XElement stat, ref int bytes, List<LoadProcedureRequirement> requirements)
    {
        var resources = new List<LoadProcedureResourceReference>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        var code = One(stat, "Code", false); if (code is null) return [];
        foreach (var segment in code.Elements())
        {
            if (resources.Count >= 1024) Fail(LoadProcedureIssueCode.LimitExceeded);
            var id = (string?)segment.Attribute("Id"); if (!Safe(id) || !ids.Add(id!)) Fail(LoadProcedureIssueCode.InvalidResource);
            var relative = segment.Name.LocalName == "RelativeSegment";
            if (!relative && segment.Name.LocalName != "AbsoluteSegment") Fail(LoadProcedureIssueCode.InvalidResource, id);
            Shape(segment, relative ? ["Id", "Size", "Offset", "LoadStateMachine"] : ["Id", "Size", "Address", "MemoryType", "UserMemory"], procedure: true);
            if (segment.Attribute("UserMemory") is { } user && Boolean(user.Value)) Fail(LoadProcedureIssueCode.InvalidResource, id);
            if (segment.Attribute("MemoryType") is { Value: not ("RAM" or "EEPROM" or "FLASH") }) Fail(LoadProcedureIssueCode.InvalidResource, id);
            var basis = checked((uint)Unsigned((string?)segment.Attribute(relative ? "Offset" : "Address"), 1_048_575));
            var arena = relative ? new ResourcePatchArena(ResourcePatchArenaKind.RelativeMemory, checked((byte)Unsigned((string?)segment.Attribute("LoadStateMachine"), 255)))
                : new ResourcePatchArena(ResourcePatchArenaKind.AbsoluteMemory);
            uint? size = segment.Attribute("Size") is { } length ? checked((uint)Unsigned(length.Value, 1_048_575)) : null;
            var data = Base64(One(segment, "Data", false), ref bytes); var mask = Base64(One(segment, "Mask", false), ref bytes);
            if (segment.Elements().Any(e => e.Name.LocalName is not ("Data" or "Mask"))) Fail(LoadProcedureIssueCode.InvalidResource, id);
            if (data is not null && mask is not null && data.Length != mask.Length) Fail(LoadProcedureIssueCode.InvalidResource, id);
            resources.Add(new(id!, arena, basis, size, data?.Length, mask?.Length, data?.Sha256, mask?.Sha256));
            if (data is null) requirements.Add(new(LoadProcedureRequirementCode.MissingInitialResource, id));
            if (size is null) requirements.Add(new(LoadProcedureRequirementCode.UnknownResourceSize, id));
            else if (data is not null && (uint)data.Length < size) requirements.Add(new(LoadProcedureRequirementCode.IncompleteResourcePayload, id));
        }
        return resources.ToArray();
    }

    private static LoadProcedureTableRequirement[] Tables(XElement stat, ArchivedResourceContext context, LoadProcedureStyle style,
        LoadProcedureResourceReference[] resources, List<LoadProcedureRequirement> requirements)
    {
        var tables = new List<LoadProcedureTableRequirement>();
        foreach (var (role, local, native) in new[] { (LoadProcedureTableRole.GroupAddress, "AddressTable", "GroupAddressTable"),
            (LoadProcedureTableRole.Association, "AssociationTable", "GroupAssociationTable"), (LoadProcedureTableRole.CommunicationObject, "ComObjectTable", "GroupObjectTable") })
        {
            var node = One(stat, local, false); var id = (string?)node?.Attribute("CodeSegment");
            if (id is not null && !Safe(id)) Fail(LoadProcedureIssueCode.InvalidResource);
            uint? offset = node?.Attribute("Offset") is { } at ? checked((uint)Unsigned(at.Value, uint.MaxValue)) : null;
            uint? maximum = node?.Attribute("MaxEntries") is { } max ? checked((uint)Unsigned(max.Value, uint.MaxValue)) : null;
            var resource = id is null ? null : resources.SingleOrDefault(r => r.Id == id);
            if (id is not null && (resource is null || offset is null)) Fail(LoadProcedureIssueCode.InvalidResource, id);
            uint? position = resource is not null && offset is { } delta ? checked(resource.BaseOffset + delta) : null;
            var resolved = context.ResolveRemote(native, style == LoadProcedureStyle.DefaultProcedure ? "simple" : "lsm");
            if (resolved.Status == ArchivedResourceResolutionStatus.Ambiguous) Fail(LoadProcedureIssueCode.InvalidMaster);
            tables.Add(new(role, id, offset, maximum, resource?.Arena, position, resolved.Descriptor?.Flavour, resolved.Descriptor?.Location));
            if (id is null) requirements.Add(new(LoadProcedureRequirementCode.TablePlacementUnresolved, native));
            if (resource is not null && offset is { } resourceOffset && Extent(resource) is { } resourceExtent && resourceOffset >= resourceExtent)
                requirements.Add(new(LoadProcedureRequirementCode.TablePlacementUnresolved, native));
            if (resolved.Descriptor?.Location?.AddressSpace is ArchivedResourceAddressSpace.Pointer or ArchivedResourceAddressSpace.RelativeMemory or ArchivedResourceAddressSpace.None)
                requirements.Add(new(LoadProcedureRequirementCode.RuntimeResourceMappingUnresolved, native));
        }
        foreach (var selected in resources)
        {
            var start = (ulong)selected.BaseOffset; var extent = Extent(selected);
            foreach (var other in resources.Where(r => r.Id != selected.Id && r.Arena == selected.Arena))
            {
                var otherStart = (ulong)other.BaseOffset; var otherExtent = Extent(other);
                if ((extent is null || otherStart < start + extent.Value) && (otherExtent is null || start < otherStart + otherExtent.Value)
                    && extent != 0 && otherExtent != 0) Fail(LoadProcedureIssueCode.AmbiguousResourceOwnership, selected.Id);
            }
        }
        return tables.ToArray();
    }
    private static ulong? Extent(LoadProcedureResourceReference resource) => resource.DeclaredSize is { } size
        ? Math.Max((ulong)size, (ulong)Math.Max(resource.DataLength ?? 0, resource.MaskLength ?? 0)) : null;

    private static XElement Configuration(XDocument master, string id)
    {
        var root = master.Root;
        if (root is null || root.Name.LocalName != "KNX" || !Native(root.Name.NamespaceName)) Fail(LoadProcedureIssueCode.InvalidMaster);
        var masks = One(One(root!, "MasterData", true)!, "MaskVersions", true)!;
        var candidates = masks.Elements(root!.Name.Namespace + "MaskVersion").Where(e => (string?)e.Attribute("Id") == id).Take(2).ToArray();
        if (candidates.Length != 1) Fail(LoadProcedureIssueCode.InvalidMaster);
        var sets = candidates[0].Elements(root.Name.Namespace + "HawkConfigurationData").ToArray();
        if (sets.Length > 64) Fail(LoadProcedureIssueCode.LimitExceeded);
        var current = sets.Where(e => e.Attribute("LegacyVersion") is null || Unsigned((string)e.Attribute("LegacyVersion")!, uint.MaxValue) == 0).Take(2).ToArray();
        if (current.Length != 1) Fail(LoadProcedureIssueCode.InvalidMaster);
        return current[0];
    }
    private static XElement? One(XElement parent, string local, bool required)
    {
        var elements = parent.Elements(parent.Name.Namespace + local).Take(2).ToArray();
        if (elements.Length > 1 || required && elements.Length != 1) Fail(LoadProcedureIssueCode.InvalidApplication);
        return elements.SingleOrDefault();
    }
    private static string[] AccessTokens(string? raw)
    {
        var tokens = raw?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (tokens.Length is < 1 or > 16 || tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Length
            || tokens.Any(t => t is not ("remote" or "local1" or "local2"))) Fail(LoadProcedureIssueCode.InvalidMaster);
        return tokens;
    }
    private static void Shape(XElement element, string[] attributes, bool procedure, bool noChildren = false)
    {
        if (element.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || !attributes.Contains(a.Name.LocalName, StringComparer.Ordinal)))
            || element.Nodes().Any(n => n is not XElement and not XComment && (n is not XText text || !string.IsNullOrWhiteSpace(text.Value)))
            || noChildren && element.HasElements || !procedure && element.Elements().Any(e => e.Name != element.Name.Namespace + "OnError"))
            Fail(LoadProcedureIssueCode.InvalidControl);
    }
    private static ulong Unsigned(string? value, ulong maximum)
    {
        if (value is null || value.Length > 64) Fail(LoadProcedureIssueCode.InvalidControl);
        var parsed = XmlConvert.ToUInt64(value); if (parsed > maximum) Fail(LoadProcedureIssueCode.InvalidControl); return parsed;
    }
    private static bool Boolean(string value) => XmlConvert.ToBoolean(value);
    private static string Text(string value) { if (!Safe(value)) Fail(LoadProcedureIssueCode.InvalidControl); return value; }
    private static ImmutablePatchBytes? Hex(string? raw, ref int bytes)
    {
        if (raw is null) return null;
        if (raw.Length > 4 * BlobLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
        var compact = raw.Trim(' ', '\t', '\r', '\n');
        if (compact.Length % 2 != 0 || compact.Any(c => !char.IsAsciiHexDigit(c))) Fail(LoadProcedureIssueCode.InvalidControl);
        PayloadBudget(compact.Length / 2, ref bytes); return new(Convert.FromHexString(compact));
    }
    private static ImmutablePatchBytes? Base64(XElement? node, ref int bytes)
    {
        if (node is null) return null;
        if (node.HasElements || node.Attributes().Any(a => !a.IsNamespaceDeclaration)) Fail(LoadProcedureIssueCode.InvalidResource);
        var raw = node.Value; if (raw.Length > 4 * BlobLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
        var compact = new string(raw.Where(c => c is not (' ' or '\t' or '\r' or '\n')).ToArray());
        var padding = compact.EndsWith("==", StringComparison.Ordinal) ? 2 : compact.EndsWith('=') ? 1 : 0;
        if (compact.Length % 4 != 0) Fail(LoadProcedureIssueCode.InvalidResource);
        PayloadBudget(checked(compact.Length / 4 * 3 - padding), ref bytes); return new(Convert.FromBase64String(compact));
    }
    private static void PayloadBudget(int size, ref int bytes)
    { if (size < 0 || size > BlobLimit || (bytes = checked(bytes + size)) > PayloadLimit) Fail(LoadProcedureIssueCode.LimitExceeded); }
    private static void Budget(XDocument document, int limit)
    {
        if (document is null || document.Root is null || document.DocumentType is not null) Fail(LoadProcedureIssueCode.InvalidApplication);
        var pending = new Stack<(XNode Node, int Depth)>();
        foreach (var node in document.Nodes()) pending.Push((node, 0));
        long size = 0; var elements = 0;
        while (pending.TryPop(out var item))
        {
            if (item.Depth > 256 || ++elements > 2_000_000) Fail(LoadProcedureIssueCode.LimitExceeded);
            if (item.Node is XElement element)
            {
                size += Encoding.UTF8.GetByteCount(element.Name.ToString());
                foreach (var attribute in element.Attributes()) size += Encoding.UTF8.GetByteCount(attribute.Name.ToString()) + (long)Encoding.UTF8.GetByteCount(attribute.Value);
                foreach (var node in element.Nodes()) pending.Push((node, item.Depth + 1));
            }
            else if (item.Node is XText text) size += Encoding.UTF8.GetByteCount(text.Value);
            else if (item.Node is XComment comment) size += Encoding.UTF8.GetByteCount(comment.Value);
            else Fail(LoadProcedureIssueCode.InvalidApplication);
            if (size > limit) Fail(LoadProcedureIssueCode.LimitExceeded);
        }
    }
    private static string DocumentHash(XDocument document) => ParameterPatchSourceBinding.DefinitionFingerprint(document);
    private static string ElementHash(XElement element)
    {
        using var algorithm = SHA256.Create(); using var stream = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write);
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { CloseOutput = false, Indent = false })) element.WriteTo(writer);
        stream.FlushFinalBlock(); return Convert.ToHexStringLower(algorithm.Hash!);
    }
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
