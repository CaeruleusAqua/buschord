using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Linq;

namespace Knx;

internal static class NativeImagePreparation
{
    private const long MaxOperands = 4 * 1024 * 1024;
    private sealed record Candidate(int Ordinal, string Subtype, uint Mask, XElement Element, string Hash);
    private sealed record Pending(uint Options, NativeConditionalBooleanFact? ToolIp, NativeImageTaskPredicates Tasks,
        NativeImageProcedureSelection Selection, LoadProcedureRequest? Request, XElement? Template, NativeGeneralIssue? Issue);

    internal static NativeImageVariantPreparation Prepare(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs,
        NativeRootImageInventory inventory, IReadOnlyList<uint> optionAssumptions)
    {
        try
        {
            var options = CopyOptions(optionAssumptions);
            if (project is null || device is null || inputs is null || inventory is null || !inventory.IsAvailable
                || !inventory.BelongsTo(project, device, inputs) || !inventory.MatchesCurrent(project, device, inputs))
                Fail(NativeGeneralIssueCode.ContextMismatch);
            if (!inventory.PrimaryProgramOnly || inputs.Application is null || inputs.Manifest.Binding is null)
                Fail(NativeGeneralIssueCode.UnsupportedApplication);
            var app = Application(inputs.Application!, inventory.ApplicationId!);
            var style = (string?)app.Attribute("LoadProcedureStyle");
            if (style is not ("DefaultProcedure" or "ProductProcedure" or "MergedProcedure")) Fail(NativeGeneralIssueCode.InvalidDeclaration);
            var stat = One(app, "Static", true)!;
            var configuration = Configuration(inputs.Master, inventory.MaskVersionId!);
            var candidates = Candidates(configuration);
            var pending = new List<Pending>();
            foreach (var word in options)
            {
                if ((word & 255) == 8)
                {
                    pending.Add(Select(word, new(false, NativeConditionalBooleanOrigin.ConditionalAssumption), style!, candidates));
                    pending.Add(Select(word, new(true, NativeConditionalBooleanOrigin.ConditionalAssumption), style!, candidates));
                }
                else pending.Add(Select(word, null, style!, candidates));
            }
            if (pending.Count > 32) Fail(NativeGeneralIssueCode.LimitExceeded);
            // Every decoder invocation is budgeted BEFORE invoking any existing payload parser.
            var requests = pending.Where(p => p.Request is not null && p.Issue is null)
                .DistinctBy(p => p.Request).ToArray();
            long operands = 0;
            foreach (var item in requests)
            {
                operands = checked(operands + OperandVolume(stat, style == "ProductProcedure" ? null : item.Template));
                if (operands > MaxOperands) Fail(NativeGeneralIssueCode.LimitExceeded);
            }
            var preparedByRequest = new Dictionary<LoadProcedureRequest, PreparedConditionalLoadProcedure>();
            foreach (var item in requests)
                preparedByRequest.Add(item.Request!, LoadProcedureCompiler.PrepareResolved(inputs, item.Request!));
            var variants = new List<NativeImageVariant>(); var procedures = new Dictionary<int, PreparedConditionalLoadProcedure>();
            var controllers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in pending)
            {
                var issue = item.Issue; PreparedConditionalLoadProcedure? prepared = null;
                if (item.Request is not null && issue is null)
                {
                    prepared = preparedByRequest[item.Request];
                    if (!prepared.IsAvailable)
                        issue = new(NativeGeneralIssueCode.ProcedureUnavailable, ProcedureIssue: prepared.Issues.FirstOrDefault());
                }
                var ordinal = variants.Count;
                var variant = new NativeImageVariant(ordinal, item.Options, item.ToolIp, item.Tasks, item.Selection,
                    issue is null ? null : issue with { VariantOrdinal = ordinal });
                variants.Add(variant);
                if (prepared is { IsAvailable: true } && issue is null)
                {
                    procedures.Add(ordinal, prepared);
                    foreach (var id in prepared.ControllerReferences)
                        if (controllers.Add(id) && controllers.Count > 4096) Fail(NativeGeneralIssueCode.LimitExceeded);
                }
            }
            if (!inventory.MatchesCurrent(project, device, inputs)) Fail(NativeGeneralIssueCode.InputsChanged);
            return NativeImageVariantPreparation.Seal(project, device, inputs, inventory, options, variants.ToArray(), procedures,
                controllers.Order(StringComparer.Ordinal).ToArray(), operands);
        }
        catch (NativeGeneralRejected failure) { return NativeImageVariantPreparation.Unavailable(failure.Issue); }
        catch (Exception failure) when (failure is XmlException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { return NativeImageVariantPreparation.Unavailable(new(NativeGeneralIssueCode.InvalidDeclaration)); }
    }

    private static uint[] CopyOptions(IReadOnlyList<uint> values)
    {
        if (values is null || values.Count is < 1 or > 16) Fail(NativeGeneralIssueCode.InvalidRequest);
        var count = values!.Count; var result = new uint[count]; var seen = new HashSet<uint>(); var index = 0;
        foreach (var value in values)
        {
            if (index >= count || !seen.Add(value)) Fail(NativeGeneralIssueCode.InvalidRequest);
            result[index++] = value;
        }
        if (index != count || values.Count != count) Fail(NativeGeneralIssueCode.InvalidRequest);
        return result;
    }

    private static Pending Select(uint options, NativeConditionalBooleanFact? toolIp, string style, Candidate[] candidates)
    {
        var low = options & 255; var basic = low != 8 || toolIp is { Value: false };
        var tasks = new NativeImageTaskPredicates(basic, low != 0 && basic, (options & 1) != 0, (options & 2) != 0);
        var adjusted = options & ~64u;
        if (!basic) return new(options, toolIp, tasks, new(NativeImageProcedureState.BasicSkipped, adjusted, null, null, null, null, "None"), null, null, null);
        var parts = ((options & 128) != 0 ? LoadParts.Full : 0) | ((options & 1) != 0 ? LoadParts.Parameters : 0)
            | ((options & 2) != 0 ? LoadParts.Groups : 0);
        if (style == "ProductProcedure")
            return new(options, toolIp, tasks, new(NativeImageProcedureState.ProductRoot, adjusted, null, null, null, null, "ProductMergeAuto"),
                new("all", LoadProcedureAccess.Remote, parts), null, null);
        Candidate? selected = null; var best = -32;
        foreach (var candidate in candidates)
        {
            if ((candidate.Mask & adjusted) == 0) continue;
            var score = Score(adjusted, candidate.Mask);
            if (score > best) { selected = candidate; best = score; }
            if (candidate.Mask == adjusted) break;
        }
        if (low == 8 && (selected is null || best < 32))
            return new(options, toolIp, tasks, new(NativeImageProcedureState.NativeEmpty, adjusted, null, null, null, best, "MaskAll"), null, null, null);
        if (selected is null)
            return new(options, toolIp, tasks, new(NativeImageProcedureState.Unavailable, adjusted, null, null, null, null, "MaskAll"), null, null,
                new(NativeGeneralIssueCode.ProcedureUnavailable));
        var selection = new NativeImageProcedureSelection(NativeImageProcedureState.MaskTemplate, adjusted, selected.Subtype,
            selected.Ordinal, selected.Hash, best, "MaskAll");
        // Existing source/hash pairing stays original. Non-equivalent normalization is a later profile.
        if (selected.Element.Descendants().Any(e => e.Name.LocalName is "choose" or "when"
            || e.Attribute("AppliesTo") is { } applies && applies.Value is not ("auto" or "all")))
            return new(options, toolIp, tasks, selection, null, selected.Element, new(NativeGeneralIssueCode.UnsupportedControl));
        return new(options, toolIp, tasks, selection, new(selected.Subtype, LoadProcedureAccess.Remote, parts), selected.Element, null);
    }

    private static int Score(uint options, uint candidate)
    {
        if (options == candidate) return 32;
        const uint configuration = 0xC0008;
        if ((options & configuration) != 0 && (options & ~configuration) == 0 && (candidate & ~configuration) != 0) return -32;
        if ((candidate & configuration) != 0 && (candidate & ~configuration) == 0 && (options & ~configuration) != 0) return -32;
        var score = System.Numerics.BitOperations.PopCount(candidate & options) - System.Numerics.BitOperations.PopCount(candidate & ~options);
        if (((options & 1) != 0 && (candidate & 1) == 0) || ((options & 2) != 0 && (candidate & 2) == 0)) score -= 16;
        return score;
    }

    private static Candidate[] Candidates(XElement? configuration)
    {
        if (configuration is null) return [];
        var container = One(configuration, "Procedures", false); if (container is null) return [];
        var result = new List<Candidate>(); var keys = new HashSet<string>(StringComparer.Ordinal); var ordinal = 0;
        foreach (var procedure in container.Elements())
        {
            if (ordinal >= 64 || procedure.Name != container.Name.Namespace + "Procedure") Fail(NativeGeneralIssueCode.InvalidDeclaration);
            var type = (string?)procedure.Attribute("ProcedureType"); var subtype = (string?)procedure.Attribute("ProcedureSubType");
            var access = ((string?)procedure.Attribute("Access") ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (type is not ("Load" or "Unload") || subtype is not ("all" or "par" or "grp" or "par,grp" or "ap1" or "cfg")
                || access.Length == 0 || access.Any(t => t is not ("remote" or "local1" or "local2"))
                || access.Distinct(StringComparer.Ordinal).Count() != access.Length) Fail(NativeGeneralIssueCode.InvalidDeclaration);
            foreach (var token in access)
                if (!keys.Add(type + "|" + subtype + "|" + token)) Fail(NativeGeneralIssueCode.InvalidDeclaration);
            if (type == "Load" && access.Contains("remote", StringComparer.Ordinal))
                result.Add(new(ordinal, subtype!, subtype switch { "par" => 1, "grp" => 2, "par,grp" => 3, "ap1" => 191, "cfg" => 0xC0008, _ => 255 },
                    procedure, NativeGeneralHash.Document(procedure)));
            ordinal++;
        }
        return result.ToArray();
    }

    private static long OperandVolume(XElement stat, XElement? template)
    {
        long total = 0;
        void HexOperands(XElement? node)
        {
            if (node is null) return;
            foreach (var attr in node.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "InlineData" or "Mask"))
            {
                var raw = attr.Value.AsSpan().Trim(" \t\r\n");
                if ((raw.Length & 1) != 0 || raw.Length / 2 > 1024 * 1024) Fail(NativeGeneralIssueCode.LimitExceeded);
                foreach (var c in raw) if (!Uri.IsHexDigit(c)) Fail(NativeGeneralIssueCode.InvalidDeclaration);
                total = checked(total + raw.Length / 2);
                if (total > MaxOperands) Fail(NativeGeneralIssueCode.LimitExceeded);
            }
        }
        HexOperands(One(stat, "LoadProcedures", false)); HexOperands(template);
        var code = One(stat, "Code", false);
        if (code is not null)
            foreach (var segment in code.Elements())
                foreach (var data in segment.Elements().Where(e => e.Name.LocalName is "Data" or "Mask"))
                {
                    var count = 0; var padding = 0; var afterPadding = false;
                    foreach (var c in data.Value)
                    {
                        if (c is ' ' or '\t' or '\r' or '\n') continue;
                        if (c == '=') { padding++; afterPadding = true; }
                        else if (afterPadding || !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/'))
                            Fail(NativeGeneralIssueCode.InvalidDeclaration);
                        if (++count > 1_398_104) Fail(NativeGeneralIssueCode.LimitExceeded);
                    }
                    if ((count & 3) != 0 || padding > 2 || padding != 0 && count == 0) Fail(NativeGeneralIssueCode.InvalidDeclaration);
                    var size = checked(count / 4 * 3 - padding);
                    if (size < 0 || size > 1024 * 1024 || (total = checked(total + size)) > MaxOperands)
                        Fail(NativeGeneralIssueCode.LimitExceeded);
                }
        return total;
    }

    internal static XElement Application(XDocument document, string id)
    {
        var root = document.Root;
        if (root is not { Name.LocalName: "KNX" }
            || root.Name.NamespaceName is not ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23"))
            Fail(NativeGeneralIssueCode.UnsupportedApplication);
        var apps = root!.Descendants(root.Name.Namespace + "ApplicationProgram").Take(2).ToArray();
        if (apps.Length != 1 || (string?)apps[0].Attribute("Id") != id) Fail(NativeGeneralIssueCode.InvalidDeclaration);
        return apps[0];
    }
    internal static XElement? Configuration(XDocument? master, string mask)
    {
        if (master is null) return null;
        var masks = master.Descendants().Where(e => e.Name.LocalName == "MaskVersion" && (string?)e.Attribute("Id") == mask).Take(2).ToArray();
        if (masks.Length != 1) Fail(NativeGeneralIssueCode.SourceUnavailable);
        var sets = masks[0].Elements(masks[0].Name.Namespace + "HawkConfigurationData")
            .Where(e => e.Attribute("LegacyVersion") is null || uint.TryParse(e.Attribute("LegacyVersion")!.Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out var legacy) && legacy == 0).Take(2).ToArray();
        if (sets.Length != 1) Fail(NativeGeneralIssueCode.SourceUnavailable);
        return sets[0];
    }
    internal static XElement? One(XElement parent, string local, bool required)
    {
        var rows = parent.Elements().Where(e => e.Name.LocalName == local).Take(2).ToArray();
        if (rows.Length > 1 || required && rows.Length != 1 || rows.Any(e => e.Name.Namespace != parent.Name.Namespace)) Fail(NativeGeneralIssueCode.InvalidDeclaration);
        return rows.SingleOrDefault();
    }
    [DoesNotReturn] private static void Fail(NativeGeneralIssueCode code) => NativeGeneralHash.Fail(code);
}
