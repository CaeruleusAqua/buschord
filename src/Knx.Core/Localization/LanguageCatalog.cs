using System.Reflection;
using System.Text.Json;

namespace Knx;

/// <summary>
/// Loads embedded flat message catalogs and resolves one key against the German/English
/// reference map plus any number of language overlays. Adding a language means adding a
/// catalog and registering its code; no lookup code changes.
/// </summary>
public static class LanguageCatalog
{
    /// <summary>Shared empty catalog; callers can expose it without allocating.</summary>
    public static readonly IReadOnlyDictionary<string, string> EmptyCatalog =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Reads the embedded catalog named <c>BusChord.i18n.{module}.{language}.json</c>.
    /// A missing or malformed resource yields an empty map; the caller falls back.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Load(Assembly assembly, string module, string language)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var resource = $"BusChord.i18n.{module}.{language}.json";
        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null) return EmptyCatalog;

        try
        {
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return EmptyCatalog;
            var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value)) catalog[property.Name] = value;
            }
            return catalog;
        }
        catch (JsonException)
        {
            // A broken embedded catalog must not take the app down; the parity test catches it.
            return EmptyCatalog;
        }
    }

    /// <summary>
    /// Loads one overlay per language that is not part of the German/English reference map.
    /// Unknown or missing languages are skipped, so the resolver falls back normally.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadOverlays(
        Assembly assembly, string module, IEnumerable<string> languages)
    {
        var overlays = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var language in languages.Distinct(StringComparer.Ordinal))
        {
            if (language is "de" or "en") continue;
            var catalog = Load(assembly, module, language);
            if (catalog.Count > 0) overlays[language] = catalog;
        }
        return overlays;
    }

    /// <summary>
    /// Accepts a BCP-47 tag and returns the registered code: exact tag first
    /// (<c>zh-CN</c>), then the base language (<c>de-DE</c> -> <c>de</c>), else German.
    /// </summary>
    public static string Normalize(string? code, IReadOnlyList<string> available)
    {
        if (string.IsNullOrWhiteSpace(code)) return "de";
        var tag = code.Trim().Replace('_', '-');
        var exact = available.FirstOrDefault(language =>
            string.Equals(language, tag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var baseCode = tag.Split('-', 2)[0].ToLowerInvariant();
        return available.FirstOrDefault(language =>
            string.Equals(language, baseCode, StringComparison.OrdinalIgnoreCase)) ?? "de";
    }

    /// <summary>
    /// Resolves a key for one active language. The reference map owns German and English;
    /// every other language is an overlay. Missing overlay keys fall back to English, then
    /// German, so a partial translation never exposes a raw key.
    /// </summary>
    public static string Resolve(
        IReadOnlyDictionary<string, (string De, string En)> reference,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> overlays,
        string key,
        string language,
        params object[] args)
    {
        if (!reference.TryGetValue(key, out var entry)) return key;

        string text;
        if (language == "de")
        {
            text = entry.De;
        }
        else if (language == "en")
        {
            text = entry.En;
        }
        else if (overlays.TryGetValue(language, out var overlay) && overlay.TryGetValue(key, out var translated))
        {
            text = translated;
        }
        else
        {
            text = entry.En;
        }

        if (args.Length == 0) return text;
        try { return string.Format(text, args); }
        catch (FormatException) { return text; }
    }
}
