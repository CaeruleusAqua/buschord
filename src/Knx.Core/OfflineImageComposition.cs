using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Knx;

/// <summary>Composes explicit root code contributions and missing-bit diagnostics, without applying writers or reading devices.</summary>
public static class OfflineImageComposition
{
    private const int MaxPayload = 1_048_576, MaxTotal = 4 * MaxPayload, MaxSegments = 1024, MaxSpans = 4096;
    private const uint NativeMaximum = 1_048_575;
    private sealed class Rejected(ImageCompositionIssue issue) : Exception { internal ImageCompositionIssue Issue => issue; }
    [DoesNotReturn]
    private static void Fail(ImageCompositionIssueCode code, string? id = null)
        => throw new Rejected(new(code, Safe(id) ? id : null));
    private static bool Safe(string? value) => value is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    internal static bool IdentityWithinBudget(Project project, DeviceInstance device)
    {
        if (project is null || device is null || !Safe(project.Id) || project.Guid is { } guid && !Safe(guid) || !Safe(device.Id)
            || !Safe(device.ProductRefId) || device.Hardware2ProgramRefId is { } hardware && !Safe(hardware)) return false;
        var count = 0; var matches = 0; var member = false;
        foreach (var candidate in ProjectQuery.DeviceList(project))
        {
            if (++count > 100000) return false;
            if (candidate.Id == device.Id) matches++;
            if (ReferenceEquals(candidate, device)) member = true;
        }
        return member && matches == 1;
    }
    private static ImageCompositionRequest? CopyRequest(ImageCompositionRequest request) => request is not null
        && request.MaskPolicy is StaticCodeMaskPolicy.SourceDefinedBits or StaticCodeMaskPolicy.NoStaticSourceBits ? request with { } : null;

    public static ImageCompositionPlan Preview(Project project, DeviceInstance device, ImageCompositionRequest request)
    {
        var owned = CopyRequest(request);
        if (owned is null || !IdentityWithinBudget(project, device))
            return new(new(ArchivedImageSourceStatus.InvalidRequest), new(ImageCompositionIssueCode.InvalidRequest));
        return Preview(project, device, ArchivedImageSources.ReadInputs(project, device), owned);
    }
    internal static ImageCompositionPlan Preview(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs, ImageCompositionRequest request)
    {
        var owned = CopyRequest(request);
        var sources = inputs?.Manifest ?? new(ArchivedImageSourceStatus.InvalidRequest);
        if (owned is null || inputs is null || !IdentityWithinBudget(project, device))
            return new(sources, new(ImageCompositionIssueCode.InvalidRequest));
        try { return Create(project, device, inputs, owned); }
        catch (Rejected failure) { return new(sources, failure.Issue); }
        catch (Exception failure) when (failure is XmlException or InvalidDataException or IOException or FormatException
            or OverflowException or ArgumentException or InvalidOperationException)
        { return new(sources, new(ImageCompositionIssueCode.InvalidDeclaration)); }
    }

    private sealed record Payload(XElement Element, int Length);
    private sealed record Source(string Id, ResourcePatchArena Arena, uint Offset, uint Size, Payload? Data, Payload? Mask)
    { internal int Length => checked((int)Math.Max(Size, (uint)(Data?.Length ?? 0))); }
    private sealed class Interval(ResourcePatchArena arena, uint start, ulong end)
    {
        internal ResourcePatchArena Arena = arena;
        internal uint Start = start;
        internal ulong End = end;
        internal List<ImageCompositionSourceSegment> Segments = [];
    }
    private static ImageCompositionPlan Create(Project project, DeviceInstance device, ArchivedImageSources.Inputs inputs, ImageCompositionRequest request)
    {
        var sources = inputs.Manifest;
        if (sources.Status == ArchivedImageSourceStatus.UnsupportedApplicationNamespace) Fail(ImageCompositionIssueCode.UnsupportedApplication);
        if (sources.Status == ArchivedImageSourceStatus.LimitExceeded) Fail(ImageCompositionIssueCode.LimitExceeded);
        if (sources.Status == ArchivedImageSourceStatus.InvalidDeclaration) Fail(ImageCompositionIssueCode.InvalidDeclaration);
        if (sources.Status != ArchivedImageSourceStatus.Inspected || inputs.Application is null || sources.Binding is null
            || sources.ApplicationDefinitionHash is null) Fail(ImageCompositionIssueCode.ApplicationUnavailable);
        if (!sources.Binding.MatchesIdentity(project, device)) Fail(ImageCompositionIssueCode.InputsChanged);
        if (device.Hardware2ProgramRefId is not null && sources.Hardware?.Status != ArchivedHardwareSourceStatus.Inspected)
            Fail(ImageCompositionIssueCode.ApplicationUnavailable);
        var document = inputs.Application!;
        var nodes = CheckedNodes(document);
        var definition = DocumentHash(document);
        if (definition != sources.ApplicationDefinitionHash) Fail(ImageCompositionIssueCode.InputsChanged);
        var root = document.Root!; var ns = root.Name.Namespace;
        if (root.Name.LocalName != "KNX" || ns.NamespaceName is not ("http://knx.org/xml/project/20" or "http://knx.org/xml/project/21" or "http://knx.org/xml/project/23")
            || ns.NamespaceName != sources.ApplicationNamespace) Fail(ImageCompositionIssueCode.UnsupportedApplication);
        var applications = nodes.Where(e => e.Name.LocalName == "ApplicationProgram").Take(2).ToArray();
        if (applications.Length != 1 || applications[0].Name != ns + "ApplicationProgram"
            || (string?)applications[0].Attribute("Id") != sources.ApplicationId) Fail(ImageCompositionIssueCode.InvalidDeclaration);
        var stat = One(applications[0], ns + "Static", true)!; Header(stat);
        var code = One(stat, ns + "Code", false);
        if (code is null || !code.HasElements) Fail(ImageCompositionIssueCode.NoCodeSources);
        Header(code!);
        var declarations = code!.Elements().Take(MaxSegments + 1).ToArray();
        if (declarations.Length > MaxSegments) Fail(ImageCompositionIssueCode.LimitExceeded);
        var allowed = declarations.ToHashSet();
        if (nodes.Any(e => e.Name.LocalName is "AbsoluteSegment" or "RelativeSegment"
            && !ModuleTemplate(e, applications[0], ns) && !allowed.Contains(e))) Fail(ImageCompositionIssueCode.InvalidDeclaration);
        var sourcePlans = new List<Source>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        long rawBytes = 0, allocationBytes = 0;
        foreach (var element in declarations)
        {
            var absolute = element.Name == ns + "AbsoluteSegment";
            if (!absolute && element.Name != ns + "RelativeSegment") Fail(ImageCompositionIssueCode.InvalidDeclaration);
            Header(element, absolute ? ["Id", "Name", "Size", "InternalDescription", "Address", "MemoryType", "UserMemory"]
                : ["Id", "Name", "Size", "InternalDescription", "Offset", "LoadStateMachine"]);
            var id = (string?)element.Attribute("Id");
            if (!Safe(id) || !ids.Add(id!)) Fail(ImageCompositionIssueCode.InvalidDeclaration);
            try { XmlConvert.VerifyNCName(id!); } catch (XmlException) { Fail(ImageCompositionIssueCode.InvalidDeclaration); }
            var size = UInt(element, "Size", NativeMaximum, id);
            var offset = UInt(element, absolute ? "Address" : "Offset", NativeMaximum, id);
            ResourcePatchArena arena;
            if (absolute)
            {
                if (element.Attribute("MemoryType") is { } memory && memory.Value is not ("RAM" or "EEPROM" or "FLASH")) Fail(ImageCompositionIssueCode.InvalidDeclaration, id);
                if (element.Attribute("UserMemory") is { } user)
                {
                    if (user.Value is "true" or "1") Fail(ImageCompositionIssueCode.UnsupportedArena, id);
                    if (user.Value is not ("false" or "0")) Fail(ImageCompositionIssueCode.InvalidDeclaration, id);
                }
                arena = new(ResourcePatchArenaKind.AbsoluteMemory);
            }
            else arena = new(ResourcePatchArenaKind.RelativeMemory, (byte)UInt(element, "LoadStateMachine", byte.MaxValue, id));
            if (element.Elements().Any(e => e.Name != ns + "Data" && e.Name != ns + "Mask")) Fail(ImageCompositionIssueCode.InvalidDeclaration, id);
            var data = PlanPayload(One(element, ns + "Data", false), id);
            var mask = PlanPayload(One(element, ns + "Mask", false), id);
            if (mask is not null && (data is null || mask.Length != data.Length)) Fail(ImageCompositionIssueCode.InvalidDeclaration, id);
            var plan = new Source(id!, arena, offset, size, data, mask);
            if (plan.Length > MaxPayload || (rawBytes += (data?.Length ?? 0) + (mask?.Length ?? 0)) > MaxTotal
                || (allocationBytes += plan.Length) > MaxTotal) Fail(ImageCompositionIssueCode.LimitExceeded, id);
            if ((ulong)offset + (uint)plan.Length > (1UL << 32)) Fail(ImageCompositionIssueCode.InvalidDeclaration, id);
            sourcePlans.Add(plan);
        }
        // All source/working payload totals are checked before the first decode or byte-array allocation.
        var segments = sourcePlans.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => new ImageCompositionSourceSegment(
            s.Id, s.Arena, s.Offset, s.Size, Decode(s.Data), Decode(s.Mask))).ToArray();
        var intervals = new List<Interval>();
        foreach (var group in segments.Where(s => s.AllocationLength != 0).GroupBy(s => s.Arena)
            .OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.LoadStateMachine))
        {
            Interval? current = null;
            foreach (var segment in group.OrderBy(s => s.ByteOffsetInArena).ThenBy(s => s.SourceId, StringComparer.Ordinal))
            {
                var end = (ulong)segment.ByteOffsetInArena + (uint)segment.AllocationLength;
                if (current is null || segment.ByteOffsetInArena > current.End)
                { current = new(group.Key, segment.ByteOffsetInArena, end); intervals.Add(current); }
                else current.End = Math.Max(current.End, end);
                current.Segments.Add(segment);
            }
        }
        var chunks = new List<ImageCompositionChunk>(); var spans = new List<ImageUnspecifiedSpan>(); ulong desired = 0, unspecified = 0;
        foreach (var interval in intervals)
        {
            var length = checked((int)(interval.End - interval.Start)); var data = new byte[length]; var mask = new byte[length];
            if (request.MaskPolicy == StaticCodeMaskPolicy.SourceDefinedBits)
                foreach (var segment in interval.Segments)
                    if (segment.RawData is { } raw)
                    {
                        var start = checked((int)(segment.ByteOffsetInArena - interval.Start));
                        for (var i = 0; i < raw.Length; i++)
                        {
                            var valid = segment.RawMask?[i] ?? byte.MaxValue; var at = start + i; var bits = (byte)(raw[i] & valid);
                            if ((mask[at] & valid & (data[at] ^ bits)) != 0) Fail(ImageCompositionIssueCode.ConflictingSourceBits, segment.SourceId);
                            data[at] |= bits; mask[at] |= valid;
                        }
                    }
            var chunk = new ImageCompositionChunk(interval.Arena, interval.Start, data, mask,
                interval.Segments.Select(s => s.SourceId).Order(StringComparer.Ordinal).ToArray());
            chunks.Add(chunk); var spanStart = -1; ulong spanBits = 0;
            for (var i = 0; i <= mask.Length; i++)
            {
                var missing = i == mask.Length ? 0 : 8 - BitOperations.PopCount((uint)mask[i]);
                if (missing != 0)
                { if (spanStart < 0) spanStart = i; spanBits += (uint)missing; unspecified += (uint)missing; }
                else if (spanStart >= 0)
                {
                    if (spans.Count == MaxSpans) Fail(ImageCompositionIssueCode.LimitExceeded);
                    spans.Add(new(chunk.Key, chunk.Arena, checked(chunk.ByteOffsetInArena + (uint)spanStart), i - spanStart, spanBits));
                    spanStart = -1; spanBits = 0;
                }
                if (i < mask.Length) desired += (uint)(8 - missing);
            }
        }
        var requirements = Enum.GetValues<ImageCompositionRequirementCode>()
            .Where(c => c != ImageCompositionRequirementCode.UnspecifiedBitsNeedExternalFacts || spans.Count != 0)
            .Select(c => new ImageCompositionRequirement(c)).ToArray();
        if (!IdentityWithinBudget(project, device) || !sources.Binding.MatchesIdentity(project, device)
            || DocumentHash(document) != definition) Fail(ImageCompositionIssueCode.InputsChanged);
        var output = chunks.ToArray(); var unknown = spans.ToArray();
        var binding = new ImageCompositionBinding(project, device, sources, request, segments, output, unknown, requirements);
        return new(sources, binding, segments, output, unknown, requirements, desired, unspecified);
    }

    private static Payload? PlanPayload(XElement? element, string? id)
    {
        if (element is null) return null;
        if (element.HasElements || element.Attributes().Any(a => !a.IsNamespaceDeclaration)) Fail(ImageCompositionIssueCode.InvalidDeclaration, id);
        var count = 0; var padding = 0; var whitespace = 0;
        foreach (var text in element.Nodes().OfType<XText>())
            foreach (var ch in text.Value)
            {
                if (ch is ' ' or '\t' or '\r' or '\n') { if (++whitespace > 4096) Fail(ImageCompositionIssueCode.LimitExceeded, id); continue; }
                if (++count > ((MaxPayload + 2) / 3) * 4) Fail(ImageCompositionIssueCode.LimitExceeded, id);
                if (ch == '=') { if (++padding > 2) Fail(ImageCompositionIssueCode.InvalidDeclaration, id); }
                else if (padding != 0 || !(char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/')) Fail(ImageCompositionIssueCode.InvalidDeclaration, id);
            }
        if (count % 4 != 0 || padding > count) Fail(ImageCompositionIssueCode.InvalidDeclaration, id);
        var length = count / 4 * 3 - padding;
        if (length > MaxPayload) Fail(ImageCompositionIssueCode.LimitExceeded, id);
        return new(element, length);
    }
    private static byte[]? Decode(Payload? payload)
    {
        if (payload is null) return null;
        var text = new StringBuilder((payload.Length + 2) / 3 * 4);
        foreach (var node in payload.Element.Nodes().OfType<XText>())
            foreach (var ch in node.Value) if (ch is not (' ' or '\t' or '\r' or '\n')) text.Append(ch);
        var bytes = Convert.FromBase64String(text.ToString());
        if (bytes.Length != payload.Length) Fail(ImageCompositionIssueCode.InvalidDeclaration);
        return bytes;
    }
    private static uint UInt(XElement element, string name, uint maximum, string? id)
    {
        if (!uint.TryParse((string?)element.Attribute(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > maximum)
            Fail(ImageCompositionIssueCode.InvalidDeclaration, id);
        return value;
    }
    private static XElement? One(XElement parent, XName name, bool required)
    {
        var rows = parent.Elements().Where(e => e.Name.LocalName == name.LocalName).Take(2).ToArray();
        if (rows.Length > 1 || rows.Any(e => e.Name != name) || required && rows.Length != 1) Fail(ImageCompositionIssueCode.InvalidDeclaration);
        return rows.FirstOrDefault();
    }
    private static bool ModuleTemplate(XElement element, XElement application, XNamespace ns)
    {
        var owner = element.Ancestors().FirstOrDefault(e => e.Name.LocalName == "ModuleDef");
        while (owner is not null)
        {
            if (owner.Name != ns + "ModuleDef") return false;
            var container = owner.Parent;
            if (container?.Name == ns + "ModuleDefs" && ReferenceEquals(container.Parent, application)) return true;
            if (container?.Name != ns + "SubModuleDefs" || container.Parent?.Name != ns + "ModuleDef") return false;
            owner = container.Parent;
        }
        return false;
    }
    private static void Header(XElement element, params string[] attributes)
    {
        if (element.Attributes().Any(a => !a.IsNamespaceDeclaration && (a.Name.Namespace != XNamespace.None || !attributes.Contains(a.Name.LocalName, StringComparer.Ordinal)))
            || element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value))) Fail(ImageCompositionIssueCode.InvalidDeclaration);
    }
    private static List<XElement> CheckedNodes(XDocument document)
    {
        if (document.DocumentType is not null || document.Root is null) Fail(ImageCompositionIssueCode.InvalidDeclaration);
        var result = new List<XElement>(); var stack = new Stack<(XNode Node, int Depth)>(); stack.Push((document, 0)); long characters = 0; var count = 0;
        while (stack.TryPop(out var entry))
        {
            if (++count > 500000 || entry.Depth > 128) Fail(ImageCompositionIssueCode.LimitExceeded);
            if (entry.Node is XContainer container)
                foreach (var child in container.Nodes())
                { if (stack.Count >= 500000) Fail(ImageCompositionIssueCode.LimitExceeded); stack.Push((child, entry.Depth + 1)); }
            if (entry.Node is XElement element)
            {
                result.Add(element); characters += element.Name.ToString().Length;
                foreach (var attribute in element.Attributes()) characters += attribute.Name.ToString().Length + attribute.Value.Length;
            }
            else if (entry.Node is XText text) characters += text.Value.Length;
            else if (entry.Node is XComment comment) characters += comment.Value.Length;
            else if (entry.Node is XProcessingInstruction instruction) characters += instruction.Target.Length + instruction.Data.Length;
            if (characters > 128 * 1024 * 1024) Fail(ImageCompositionIssueCode.LimitExceeded);
        }
        return result;
    }
    private static string DocumentHash(XDocument document)
    {
        using var hash = SHA256.Create(); using var output = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, CloseOutput = false })) document.WriteTo(writer);
        output.FlushFinalBlock(); return Convert.ToHexStringLower(hash.Hash!);
    }
}
