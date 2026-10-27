using System.Xml;
using System.Xml.Linq;

namespace Knx;

public static partial class LoadProcedureCompiler
{
    internal static PreparedConditionalLoadProcedure PrepareResolved(ArchivedImageSources.Inputs inputs, LoadProcedureRequest request)
    {
        try { return PrepareConditional(inputs, request); }
        catch (Rejected failure) { return PreparedConditionalLoadProcedure.Unavailable(failure.Issue); }
        catch (Exception failure) when (failure is XmlException or FormatException or OverflowException or InvalidDataException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { return PreparedConditionalLoadProcedure.Unavailable(new(LoadProcedureIssueCode.InvalidApplication)); }
    }

    internal static bool MatchesPreparedSources(PreparedConditionalLoadProcedure prepared, ArchivedImageSources.Inputs inputs)
    {
        if (!prepared.IsAvailable || inputs.Application is null || inputs.Manifest.Binding?.Fingerprint != prepared.SourceFingerprint
            || inputs.Manifest.ApplicationId != prepared.ApplicationId) return false;
        try
        {
            ConditionalSourceBudget(inputs.Application, 128 * BlobLimit);
            if (inputs.Master is not null) ConditionalSourceBudget(inputs.Master, 16 * BlobLimit);
            return ConditionalDocumentHash(inputs.Application) == prepared.ApplicationDefinitionHash
                && (inputs.Master is null ? null : ConditionalDocumentHash(inputs.Master)) == prepared.MasterDefinitionHash;
        }
        catch (Exception failure) when (failure is Rejected or XmlException or FormatException or OverflowException or InvalidDataException or ArgumentException or InvalidOperationException)
        { return false; }
    }

    private static PreparedConditionalLoadProcedure PrepareConditional(ArchivedImageSources.Inputs inputs, LoadProcedureRequest request)
    {
        if (request is null || request.NativeSubtype is not ("all" or "par" or "grp" or "par,grp" or "ap1" or "cfg")
            || !Enum.IsDefined(request.Access) || (request.Parts & ~(LoadParts.Full | LoadParts.Parameters | LoadParts.Groups)) != 0)
            Fail(LoadProcedureIssueCode.InvalidRequest);
        if (inputs.Application is null || inputs.Manifest.Status != ArchivedImageSourceStatus.Inspected || inputs.Manifest.Binding is null)
            Fail(LoadProcedureIssueCode.InvalidApplication);
        var document = inputs.Application!; ConditionalSourceBudget(document, 128 * BlobLimit);
        if (inputs.Master is not null) ConditionalSourceBudget(inputs.Master, 16 * BlobLimit);
        var root = document.Root!;
        if (root.Name.LocalName != "KNX" || !Native(root.Name.NamespaceName)) Fail(LoadProcedureIssueCode.UnsupportedApplicationNamespace);
        var ns = root.Name.Namespace;
        if (root.DescendantsAndSelf().Any(e => e.Name.Namespace != ns)) Fail(LoadProcedureIssueCode.InvalidApplication);
        var applications = root.Descendants(ns + "ApplicationProgram").Take(2).ToArray();
        if (applications.Length != 1) Fail(LoadProcedureIssueCode.InvalidApplication);
        var app = applications[0]; var id = (string?)app.Attribute("Id"); var maskId = (string?)app.Attribute("MaskVersion");
        if (!Safe(id) || !Safe(maskId) || id != inputs.Manifest.ApplicationId) Fail(LoadProcedureIssueCode.InvalidApplication);
        var definitionHash = ConditionalDocumentHash(document);
        if (definitionHash != inputs.Manifest.ApplicationDefinitionHash) Fail(LoadProcedureIssueCode.InputsChanged);
        var masterHash = inputs.Master is null ? null : ConditionalDocumentHash(inputs.Master);
        var styleText = (string?)app.Attribute("LoadProcedureStyle");
        if (styleText is not ("DefaultProcedure" or "ProductProcedure" or "MergedProcedure")) Fail(LoadProcedureIssueCode.InvalidApplication);
        var style = Enum.Parse<LoadProcedureStyle>(styleText!); var stat = One(app, "Static", true)!;
        var requirements = new List<LoadProcedureRequirement>
        {
            new(LoadProcedureRequirementCode.SecondaryProgramOutsideScope), new(LoadProcedureRequirementCode.FinalizationNotCompiled),
            new(LoadProcedureRequirementCode.GlobalLoadPolicyNotCompiled), new(LoadProcedureRequirementCode.FixupsNotCompiled),
            new(LoadProcedureRequirementCode.GeneratedTablesNotBuilt), new(LoadProcedureRequirementCode.ParameterActivationUnresolved),
        };
        if (app.Descendants(ns + "ModuleDef").Descendants(ns + "LoadProcedures").Any()) requirements.Add(new(LoadProcedureRequirementCode.ModuleProceduresNotCompiled));
        if (app.Descendants(ns + "ParameterCalculation").Any()) requirements.Add(new(LoadProcedureRequirementCode.ScriptEvaluationNotPerformed));
        var context = ArchivedResourceContext.Read(inputs.Master, maskId, ns.NamespaceName);
        XElement? template = null; string? profileHash = null;
        var indices = new Dictionary<XElement, int>();
        void Index(XElement sourceRoot)
        {
            foreach (var parent in sourceRoot.DescendantsAndSelf())
            {
                var index = 0; foreach (var child in parent.Elements()) indices.Add(child, index++);
            }
        }
        Index(root); if (inputs.Master?.Root is { } masterRoot) Index(masterRoot);
        PreparedLoadSource Source(XElement node, LoadProcedureSourceOrigin origin)
        {
            var path = new List<int>(); var cursor = node;
            while (cursor.Parent is not null)
            { path.Add(indices[cursor]); cursor = cursor.Parent; if (path.Count > 128) Fail(LoadProcedureIssueCode.LimitExceeded); }
            path.Reverse(); return new(origin, path, indices.GetValueOrDefault(node));
        }
        var bytes = 0; var count = 0; var astNodes = 0; var choices = 0; var extended = false;
        var declared = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<PreparedLoadNode> Parse(XElement owner, LoadProcedureSourceOrigin origin, bool productGroup, int depth)
        {
            if (depth > 128) Fail(LoadProcedureIssueCode.LimitExceeded);
            var result = new List<PreparedLoadNode>();
            foreach (var element in owner.Elements())
            {
                if (++astNodes > 500000) Fail(LoadProcedureIssueCode.LimitExceeded);
                var source = Source(element, origin);
                if (element.Name.Namespace != owner.Name.Namespace) Fail(LoadProcedureIssueCode.UnsupportedControl, ordinal: source.Ordinal);
                if (element.Name.LocalName == "choose")
                {
                    if (++choices > ControlLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
                    extended = true; Shape(element, ["ParamRefId", "InternalDescription"], procedure: true);
                    var reference = (string?)element.Attribute("ParamRefId");
                    if (!Safe(reference)) Fail(LoadProcedureIssueCode.InvalidControllerDeclaration);
                    declared.Add(reference!); var branches = new List<PreparedLoadBranch>(); var defaults = 0;
                    foreach (var branch in element.Elements())
                    {
                        if (++astNodes > 500000) Fail(LoadProcedureIssueCode.LimitExceeded);
                        if (branch.Name != element.Name.Namespace + "when") Fail(LoadProcedureIssueCode.InvalidControl, ordinal: source.Ordinal);
                        Shape(branch, ["test", "default", "InternalDescription"], procedure: true);
                        var fallback = branch.Attribute("default") is { } rawDefault && Boolean(rawDefault.Value);
                        var rawTest = (string?)branch.Attribute("test"); StrictIntegerCondition? test = null;
                        if (fallback && (++defaults > 1 || rawTest is not null) || !fallback && (rawTest is null || !StrictIntegerCondition.TryParse(rawTest, out test)))
                            Fail(LoadProcedureIssueCode.UnsupportedActivationCondition, ordinal: source.Ordinal);
                        branches.Add(new(Source(branch, origin), fallback, rawTest, test, Parse(branch, origin, productGroup, depth + 1)));
                    }
                    if (branches.Count == 0) Fail(LoadProcedureIssueCode.InvalidControl, ordinal: source.Ordinal);
                    result.Add(new PreparedLoadChoose(source, reference!, branches)); continue;
                }
                Parsed parsed;
                if (element.Name.LocalName == "LdCtrlCompareRelMem")
                {
                    extended = true; parsed = ParseRelativeComparison(element, source.Ordinal, ref bytes, ref count);
                }
                else
                {
                    var single = new XElement(owner.Name, new XElement(element));
                    try { parsed = ParseControls(single, ref bytes, ref count).Single() with { Ordinal = source.Ordinal }; }
                    catch (Rejected failure) { throw new Rejected(failure.Issue with { SourceOrdinal = source.Ordinal }); }
                }
                if (parsed.Kind == LoadControlKind.Merge && (productGroup || style != LoadProcedureStyle.MergedProcedure))
                    Fail(LoadProcedureIssueCode.UnsupportedControl, ordinal: source.Ordinal);
                result.Add(new PreparedLoadAction(source, parsed.Kind, parsed.Condition, parsed.Arguments, parsed.Errors, parsed.Data, parsed.Mask));
            }
            return Array.AsReadOnly(result.ToArray());
        }
        var groups = new Dictionary<int, IReadOnlyList<PreparedLoadNode>>(); var container = One(stat, "LoadProcedures", false);
        if (container is not null)
        {
            Shape(container, [], procedure: true); var raw = container.Elements().Take(65).ToArray();
            if (raw.Length > 64) Fail(LoadProcedureIssueCode.LimitExceeded);
            foreach (var group in raw)
            {
                if (group.Name != ns + "LoadProcedure") Fail(LoadProcedureIssueCode.InvalidApplication);
                Shape(group, ["MergeId"], procedure: true);
                var key = group.Attribute("MergeId") is { } merge ? checked((int)Unsigned(merge.Value, byte.MaxValue)) : -1;
                if (key == 0 || groups.ContainsKey(key)) Fail(LoadProcedureIssueCode.AmbiguousProcedure);
                groups.Add(key, Parse(group, LoadProcedureSourceOrigin.Application, true, 0));
            }
        }
        if (inputs.Master is not null)
        {
            if (context.Status != ArchivedResourceContextStatus.Supported) Fail(LoadProcedureIssueCode.InvalidMaster);
            var configuration = Configuration(inputs.Master, maskId!);
            if (configuration.DescendantsAndSelf().Any(e => e.Name.Namespace != configuration.Name.Namespace)) Fail(LoadProcedureIssueCode.InvalidMaster);
            var procedures = One(configuration, "Procedures", false)?.Elements().Take(65).ToArray() ?? [];
            if (procedures.Length > 64 || count + procedures.Sum(p => (long)p.Elements().Count()) > ControlLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var procedure in procedures)
            {
                Shape(procedure, ["ProcedureType", "ProcedureSubType", "Access"], procedure: true);
                var type = (string?)procedure.Attribute("ProcedureType"); var subtype = (string?)procedure.Attribute("ProcedureSubType");
                var access = AccessTokens((string?)procedure.Attribute("Access"));
                if (procedure.Name != configuration.Name.Namespace + "Procedure" || type is not ("Load" or "Unload")
                    || subtype is not ("all" or "par" or "grp" or "par,grp" or "ap1" or "cfg" or "full" or "full,par" or "full,grp" or "auto"))
                    Fail(LoadProcedureIssueCode.InvalidMaster);
                foreach (var token in access) if (!keys.Add(type + "|" + subtype + "|" + token)) Fail(LoadProcedureIssueCode.AmbiguousProcedure);
                if (type == "Load" && subtype == request.NativeSubtype && access.Contains("remote", StringComparer.Ordinal)) template = procedure;
            }
            profileHash = Hash(new { context.Fingerprint, procedures = procedures.Select(ElementHash).ToArray() });
        }
        else requirements.Add(new(LoadProcedureRequirementCode.MissingArchivedMaster));
        IReadOnlyList<PreparedLoadNode> source;
        if (style == LoadProcedureStyle.ProductProcedure)
        {
            if (!groups.TryGetValue(-1, out source!)) Fail(LoadProcedureIssueCode.MissingProcedure);
        }
        else
        {
            if (template is null) Fail(LoadProcedureIssueCode.MissingProcedure);
            source = Parse(template!, LoadProcedureSourceOrigin.ArchivedMaster, false, 0);
        }
        var resources = Resources(stat, ref bytes, requirements); var tables = Tables(stat, context, style, resources, requirements);
        var requested = new HashSet<string>(StringComparer.Ordinal);
        void Reach(IEnumerable<PreparedLoadNode> sequence)
        {
            foreach (var node in sequence)
                if (node is PreparedLoadChoose choose)
                { requested.Add(choose.ReferenceId); foreach (var branch in choose.Branches) Reach(branch.Children); }
                else if (node is PreparedLoadAction { Kind: LoadControlKind.Merge } action)
                {
                    var mergeId = checked((int)action.Arguments.Single(a => a.Name == "MergeId").UnsignedValue!.Value);
                    if (groups.TryGetValue(mergeId, out var members)) Reach(members);
                }
        }
        Reach(source);
        var declaredRefs = Array.AsReadOnly(declared.Order(StringComparer.Ordinal).ToArray());
        if (NativeProcedureEvaluationContext.ValidateDeclaredControllers(inputs, declaredRefs) is { } declarationIssue)
            return PreparedConditionalLoadProcedure.Unavailable(declarationIssue);
        var legacy = extended ? null : Compile(document, inputs.Master, request);
        if (legacy is { IsAvailable: false }) return PreparedConditionalLoadProcedure.Unavailable(legacy.Issues[0]);
        var graph = new PreparedLoadGraph(id!, maskId!, style, profileHash, groups, source, resources, tables, requirements, legacy);
        var graphHash = ConditionalHash(new { version = 1, source = inputs.Manifest.Binding!.Fingerprint, definitionHash, masterHash, profileHash, request, style,
            groups = groups.OrderBy(p => p.Key).Select(p => new { p.Key, nodes = ConditionalNodeFacts(p.Value) }).ToArray(),
            root = ConditionalNodeFacts(source), resources, tables, requirements,
            declared = declaredRefs, requested = requested.Order(StringComparer.Ordinal).ToArray() });
        var prepared = PreparedConditionalLoadProcedure.Seal(graph, inputs.Manifest.Binding.Fingerprint, definitionHash, masterHash,
            graphHash, request, choices != 0, extended, requested.Order(StringComparer.Ordinal).ToArray(), declaredRefs);
        if (!prepared.MatchesSources(inputs)) Fail(LoadProcedureIssueCode.InputsChanged);
        return prepared;
    }

    private static IReadOnlyList<object> ConditionalNodeFacts(IReadOnlyList<PreparedLoadNode> nodes)
    {
        var facts = new List<object>();
        void Walk(IReadOnlyList<PreparedLoadNode> sequence)
        {
            foreach (var node in sequence)
            {
                if (facts.Count >= 500000) Fail(LoadProcedureIssueCode.LimitExceeded);
                if (node is PreparedLoadAction action)
                    facts.Add(new { kind = "action", origin = action.Source.Origin, path = action.Source.Path, action.Source.Ordinal,
                        action.Kind, action.Condition, action.Arguments, action.Errors, data = action.Data?.Sha256, mask = action.Mask?.Sha256 });
                else if (node is PreparedLoadChoose choose)
                {
                    facts.Add(new { kind = "choose", origin = choose.Source.Origin, path = choose.Source.Path, choose.Source.Ordinal, choose.ReferenceId });
                    foreach (var branch in choose.Branches)
                    {
                        if (facts.Count >= 500000) Fail(LoadProcedureIssueCode.LimitExceeded);
                        facts.Add(new { kind = "when", origin = branch.Source.Origin, path = branch.Source.Path, branch.Source.Ordinal,
                            branch.Default, branch.RawTest, operation = branch.Test?.Operator, values = branch.Test?.Values });
                        Walk(branch.Children);
                    }
                }
                else throw new InvalidOperationException();
            }
        }
        Walk(nodes); return facts.AsReadOnly();
    }

    private static void ConditionalSourceBudget(XDocument document, int limit)
    {
        Budget(document, limit); var pending = new Stack<(XElement Element, int Depth)>(); pending.Push((document.Root!, 0)); var count = 0;
        while (pending.TryPop(out var item))
        {
            if (++count > 500000 || item.Depth > 128) Fail(LoadProcedureIssueCode.LimitExceeded);
            foreach (var child in item.Element.Elements()) pending.Push((child, item.Depth + 1));
        }
    }

    private static string ConditionalDocumentHash(XDocument document)
    {
        using var digest = System.Security.Cryptography.SHA256.Create();
        using var stream = new System.Security.Cryptography.CryptoStream(Stream.Null, digest, System.Security.Cryptography.CryptoStreamMode.Write);
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = false, CloseOutput = false }))
            document.WriteTo(writer);
        stream.FlushFinalBlock(); return Convert.ToHexStringLower(digest.Hash!);
    }

    private static string ConditionalHash<T>(T value)
    {
        using var digest = System.Security.Cryptography.SHA256.Create();
        using var stream = new System.Security.Cryptography.CryptoStream(Stream.Null, digest, System.Security.Cryptography.CryptoStreamMode.Write);
        System.Text.Json.JsonSerializer.Serialize(stream, value);
        stream.FlushFinalBlock(); return Convert.ToHexStringLower(digest.Hash!);
    }

    internal static LoadProcedurePlan CompileResolved(PreparedConditionalLoadProcedure prepared, NativeProcedureEvaluationContext context)
    {
        try
        {
            if (!prepared.IsAvailable) return new(prepared.Issues.FirstOrDefault() ?? new(LoadProcedureIssueCode.InvalidApplication));
            if (context is null || !context.IsAvailable)
                return new(context?.Issues.FirstOrDefault() ?? new(LoadProcedureIssueCode.ContextMismatch));
            if (!context.MatchesPrepared(prepared)) Fail(LoadProcedureIssueCode.ContextMismatch);
            if (!context.MatchesCurrent(prepared)) Fail(LoadProcedureIssueCode.InputsChanged);
            var graph = prepared.Graph!;
            if (!prepared.RequiresExtendedProfile)
            {
                if (graph.Legacy is not { IsAvailable: true } legacy) Fail(LoadProcedureIssueCode.ContextMismatch);
                if (!context.MatchesCurrent(prepared)) Fail(LoadProcedureIssueCode.InputsChanged);
                return graph.Legacy!;
            }
            foreach (var reference in prepared.ControllerReferences)
            {
                if (!context.Controllers.TryGetValue(reference, out var fact) || fact.RefId != reference) Fail(LoadProcedureIssueCode.UnknownController, reference);
                if (fact!.EverDeclaredOwnerCount <= 0) Fail(LoadProcedureIssueCode.NeverPlacedController, reference);
                if (fact.ActiveNormalOwnerCount < 0 || fact.Active != (fact.ActiveNormalOwnerCount > 0) || fact.Active != fact.ActiveValue.HasValue)
                    Fail(LoadProcedureIssueCode.ContextMismatch, reference);
            }
            var controls = new List<LoadProcedureControl>(); var decisions = new List<LoadProcedureBranchDecision>();
            var merges = new List<LoadMergeDecision>(); var visited = 0; var expansionOrdinal = 0;
            LoadProcedureSourceLocation Location(PreparedLoadSource source, PreparedLoadSource? template, byte? mergeId,
                int occurrence, IReadOnlyList<int> ancestors) => new(source.Origin, source.Path, template?.Origin, template?.Path,
                    template?.Ordinal, mergeId, occurrence, ancestors);
            void Resolve(IReadOnlyList<PreparedLoadNode> sequence, LoadControlOrigin origin, PreparedLoadSource? insertion,
                byte? insertionId, int occurrence, IReadOnlyList<int> ancestors)
            {
                foreach (var node in sequence)
                {
                    if (++visited > ExpandedLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
                    if (node is PreparedLoadChoose choose)
                    {
                        if (!context.Controllers.TryGetValue(choose.ReferenceId, out var controller)) Fail(LoadProcedureIssueCode.UnknownController, choose.ReferenceId);
                        var selected = new List<PreparedLoadBranch>(); var usedDefault = false;
                        if (controller!.Active)
                        {
                            if (controller.ActiveValue is not { } value) Fail(LoadProcedureIssueCode.InvalidControllerValue, choose.ReferenceId);
                            foreach (var branch in choose.Branches)
                            {
                                if (++visited > ExpandedLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
                                if (!branch.Default && branch.Test!.Matches(controller.ActiveValue!.Value)) selected.Add(branch);
                            }
                            if (selected.Count == 0 && choose.Branches.FirstOrDefault(b => b.Default) is { } fallback)
                            { selected.Add(fallback); usedDefault = true; }
                        }
                        if (decisions.Count >= ExpandedLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
                        var decisionOrdinal = decisions.Count;
                        decisions.Add(new(decisionOrdinal, Location(choose.Source, insertion, insertionId, occurrence, ancestors),
                            choose.ReferenceId, controller.Active, controller.ActiveValue, selected.Select(b => b.Source.Ordinal).ToArray(), usedDefault));
                        var childAncestors = ancestors.Append(decisionOrdinal).ToArray();
                        foreach (var branch in selected) Resolve(branch.Children, origin, insertion, insertionId, occurrence, childAncestors);
                        continue;
                    }
                    var action = (PreparedLoadAction)node;
                    if (action.Kind == LoadControlKind.Merge)
                    {
                        if (insertion is not null || graph.Style != LoadProcedureStyle.MergedProcedure) Fail(LoadProcedureIssueCode.UnsupportedControl);
                        var mergeId = checked((byte)action.Arguments.Single(a => a.Name == "MergeId").UnsignedValue!.Value);
                        if (++expansionOrdinal > ExpandedLimit || merges.Count >= ExpandedLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
                        var location = Location(action.Source, null, mergeId, expansionOrdinal, ancestors);
                        if (!graph.Groups.TryGetValue(mergeId, out var members))
                        {
                            merges.Add(new(mergeId, action.Source.Ordinal, LoadMergeDecisionKind.MissingGroupSkipped, 0) { Provenance = location });
                            continue;
                        }
                        var before = controls.Count;
                        Resolve(members, LoadControlOrigin.ProductMerge, action.Source, mergeId, expansionOrdinal, ancestors);
                        merges.Add(new(mergeId, action.Source.Ordinal, controls.Count == before ? LoadMergeDecisionKind.AllControlsFiltered : LoadMergeDecisionKind.Expanded,
                            controls.Count - before) { Provenance = location });
                        continue;
                    }
                    var effective = insertion is not null && action.Condition == LoadControlCondition.Auto ? LoadControlCondition.All : action.Condition;
                    if (!Applies(effective, prepared.Request!.Parts)) continue;
                    if (controls.Count >= ExpandedLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
                    controls.Add(new(controls.Count, action.Kind, origin, action.Source.Ordinal, insertion?.Ordinal, insertionId, action.Condition, effective,
                        action.Arguments.ToArray(), action.Errors.ToArray(), action.Data, action.Mask, Location(action.Source, insertion, insertionId, occurrence, ancestors)));
                }
            }
            Resolve(graph.Source, graph.Style == LoadProcedureStyle.ProductProcedure ? LoadControlOrigin.Product : LoadControlOrigin.Mask, null, null, 0, []);
            var selection = prepared.HasConditions ? new LoadProcedureConditionalSelection(context.Fingerprint!, decisions) : null;
            var witness = ConditionalHash(new
            {
                version = 2, prepared.SourceFingerprint, prepared.ApplicationDefinitionHash, prepared.SelectedProfileHash, prepared.GraphFingerprint,
                request = prepared.Request, controllerWitness = context.Fingerprint,
                controls = controls.Select(c => new { c.Ordinal, c.Kind, c.Origin, c.SourceOrdinal, c.TemplateOrdinal, c.MergeId, c.DeclaredAppliesTo,
                    c.EffectiveAppliesTo, c.Arguments, c.OnError, data = c.InlineData?.Sha256, mask = c.InlineMask?.Sha256, c.Provenance }).ToArray(),
                decisions, merges, graph.Resources, graph.Tables, graph.Requirements,
            });
            if (!context.MatchesCurrent(prepared)) Fail(LoadProcedureIssueCode.InputsChanged);
            return new(graph.ApplicationId, graph.MaskVersionId, graph.Style, prepared.Request!, prepared.ApplicationDefinitionHash!,
                graph.SelectedProfileHash, witness, controls.ToArray(), merges.ToArray(), graph.Resources.ToArray(), graph.Tables.ToArray(), graph.Requirements.ToArray(), selection);
        }
        catch (Rejected failure) { return new(failure.Issue); }
        catch (Exception failure) when (failure is XmlException or FormatException or OverflowException or InvalidDataException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { return new(new LoadProcedureIssue(LoadProcedureIssueCode.InvalidApplication)); }
    }

    private static Parsed ParseRelativeComparison(XElement element, int ordinal, ref int bytes, ref int count)
    {
        if (++count > ControlLimit) Fail(LoadProcedureIssueCode.LimitExceeded);
        AttributeRule[] rules = [U("ObjIdx", 255), U("ObjType", 65535), U("Occurrence", 65535, value: "0"),
            U("Offset", uint.MaxValue, true), U("Size", uint.MaxValue, true), B("AllowCachedValue", value: "false"), B("Invert", value: "false"),
            U("RetryInterval", 65535, value: "0"), U("TimeOut", 65535, value: "0"), new("Range", LoadControlArgumentKind.Text)];
        Shape(element, rules.Select(r => r.Name).Concat(["AppliesTo", "InternalDescription", "InlineData", "Mask"]).ToArray(), procedure: false);
        Selector(element, "ObjIdx"); var arguments = new List<LoadControlArgument>();
        foreach (var rule in rules)
        {
            var value = (string?)element.Attribute(rule.Name) ?? rule.Default;
            if (value is null) { if (rule.Required) Fail(LoadProcedureIssueCode.InvalidControl, ordinal: ordinal); continue; }
            arguments.Add(rule.Kind switch
            {
                LoadControlArgumentKind.Unsigned => new(rule.Name, rule.Kind, UnsignedValue: Unsigned(value, rule.Maximum)),
                LoadControlArgumentKind.Boolean => new(rule.Name, rule.Kind, BooleanValue: Boolean(value)),
                _ => new(rule.Name, rule.Kind, TextValue: Text(value)),
            });
            if (rule.Name == "Range" && !System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[\[\(](-?[0-9]+)?,(-?[0-9]+)?[\)\]][su]?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                Fail(LoadProcedureIssueCode.InvalidControl, ordinal: ordinal);
        }
        if (element.Attribute("InternalDescription") is { } description) arguments.Add(new("InternalDescription", LoadControlArgumentKind.Text, TextValue: Text(description.Value)));
        var data = Hex((string?)element.Attribute("InlineData"), ref bytes); var mask = Hex((string?)element.Attribute("Mask"), ref bytes);
        var errors = new List<LoadControlErrorPolicy>();
        foreach (var error in element.Elements())
        {
            if (error.Name != element.Name.Namespace + "OnError" || errors.Count >= 64) Fail(LoadProcedureIssueCode.InvalidControl, ordinal: ordinal);
            Shape(error, ["Cause", "Ignore", "MessageRef"], procedure: false, noChildren: true);
            var causeText = (string?)error.Attribute("Cause");
            if (causeText is not ("ResourceNotFound" or "CompareMismatch")) Fail(LoadProcedureIssueCode.InvalidControl, ordinal: ordinal);
            var reference = (string?)error.Attribute("MessageRef"); if (reference is not null && !Safe(reference)) Fail(LoadProcedureIssueCode.InvalidControl, ordinal: ordinal);
            errors.Add(new(Enum.Parse<LoadControlErrorCause>(causeText!), error.Attribute("Ignore") is { } ignore && Boolean(ignore.Value), reference));
        }
        return new(LoadControlKind.CompareRelativeMemory, Condition((string?)element.Attribute("AppliesTo") ?? "auto"), arguments.ToArray(), errors.ToArray(), data, mask, ordinal);
    }
}
