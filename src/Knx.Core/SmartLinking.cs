namespace Knx;

/// <summary>A proposed link between a communication object and a group address.</summary>
public sealed record LinkSuggestion(
    DeviceInstance Device,
    ComObjectInstanceRef ComObject,
    GroupAddressEntry GroupAddress,
    int Score,
    string Reason);

/// <summary>
/// "Intelligente Verknüpfung": proposes group addresses for unlinked communication
/// objects. The score combines name token overlap with a datapoint type match; the
/// caller stays in control and applies the suggestions explicitly.
/// </summary>
public static class SmartLinking
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "der", "die", "das", "und", "oder", "mit", "von", "für", "fuer", "the", "and", "of",
        "akt", "aktor", "sensor", "kanal", "channel", "obj", "objekt", "object", "status",
    };

    /// <param name="datapointTypeOf">Optional DPT lookup per com-object id (from the device's KSXML).</param>
    public static IReadOnlyList<LinkSuggestion> Suggest(
        Project project,
        Func<DeviceInstance, ComObjectInstanceRef, string?>? datapointTypeOf = null,
        int minimumScore = 40,
        int perObject = 3)
        => SuggestCore(project, datapointTypeOf, minimumScore, perObject, false, null);

    /// <summary>Includes named conflicting candidates so an explicit review can explain them.</summary>
    public static IReadOnlyList<LinkSuggestion> SuggestForReview(Project project,
        Func<DeviceInstance, ComObjectInstanceRef, string?>? datapointTypeOf = null,
        int minimumScore = 40, int perObject = 16, IReadOnlySet<Workbench.ObjectKey>? objectKeys = null)
        => SuggestCore(project, datapointTypeOf, minimumScore, perObject, true, objectKeys);

    private static IReadOnlyList<LinkSuggestion> SuggestCore(Project project,
        Func<DeviceInstance, ComObjectInstanceRef, string?>? datapointTypeOf, int minimumScore,
        int perObject, bool includeIncompatible, IReadOnlySet<Workbench.ObjectKey>? objectKeys)
    {
        var candidates = ProjectQuery.GroupAddresses(project).ToList();
        var result = new List<LinkSuggestion>();

        foreach (var (device, comObject) in ProjectQuery.ComObjectRefs(project))
        {
            if (objectKeys is not null && !objectKeys.Contains(new(device.Id, comObject.Id))) continue;
            if (comObject.Links.Count > 0) continue;

            var objectText = $"{device.Name} {comObject.Text} {comObject.FunctionText}";
            var objectTokens = Tokens(objectText);
            var expectedDpt = ParseDatapointType(datapointTypeOf?.Invoke(device, comObject));

            var scored = new List<(GroupAddressEntry Entry, int Score, string Reason)>();
            foreach (var candidate in candidates)
            {
                var candidateDpt = ParseDatapointType(candidate.DatapointType);
                var matchesDpt = false;
                if (expectedDpt is { } expected && candidateDpt is { } actual)
                {
                    var compatible = expected.Main == actual.Main &&
                        (expected.Sub is null || actual.Sub is null || expected.Sub == actual.Sub);
                    if (!compatible && !includeIncompatible) continue;
                    matchesDpt = compatible;
                }

                var gaTokens = Tokens(candidate.Name);
                var shared = objectTokens.Intersect(gaTokens, StringComparer.OrdinalIgnoreCase).ToList();
                var score = shared.Count == 0
                    ? 0
                    : (int)(100.0 * shared.Count / Math.Max(1, Math.Min(objectTokens.Count, gaTokens.Count)));

                var reason = shared.Count > 0 ? "Name: " + string.Join(", ", shared) : "";
                if (matchesDpt)
                {
                    score += 40;
                    reason = reason.Length > 0 ? reason + " · DPT " + candidate.DatapointType : "DPT " + candidate.DatapointType;
                }

                if (score >= minimumScore) scored.Add((candidate, score, reason));
            }

            result.AddRange(scored
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Entry.Address.Raw)
                .Take(perObject)
                .Select(s => new LinkSuggestion(device, comObject, s.Entry, s.Score, s.Reason)));
        }

        return result;
    }

    /// <summary>Applies suggestions by adding the group address id to the com object links.</summary>
    public static int Apply(IEnumerable<LinkSuggestion> suggestions)
    {
        var applied = 0;
        foreach (var s in suggestions)
        {
            if (s.ComObject.Links.Contains(s.GroupAddress.Id)) continue;
            s.ComObject.Links.Add(s.GroupAddress.Id);
            applied++;
        }
        return applied;
    }

    /// <summary>A missing subtype permits any subtype of the same main type; malformed metadata stays unknown.</summary>
    private static (int Main, int? Sub)? ParseDatapointType(string? dpt)
    {
        if (string.IsNullOrWhiteSpace(dpt)) return null;
        var text = dpt.Trim();
        string[] parts;
        if (text.StartsWith("DPST-", StringComparison.OrdinalIgnoreCase))
        {
            parts = text[5..].Split('-');
            if (parts.Length != 2) return null;
        }
        else if (text.StartsWith("DPT-", StringComparison.OrdinalIgnoreCase))
        {
            parts = [text[4..]];
        }
        else
        {
            parts = text.Split('.');
            if (parts.Length > 2) return null;
        }

        if (!int.TryParse(parts[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var main) || main <= 0) return null;
        if (parts.Length == 1) return (main, null);
        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var sub)) return null;
        return (main, sub);
    }

    private static HashSet<string> Tokens(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return set;
        foreach (var raw in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().ToLowerInvariant();
            if (token.Length < 3 || StopWords.Contains(token) || token.All(char.IsDigit)) continue;
            set.Add(token);
        }
        return set;
    }

    private static readonly char[] Separators = [' ', '_', '-', '/', '\\', '.', ',', ';', ':', '(', ')', '[', ']', '#', '+'];
}
