using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Knx;
using Knx.App.Localization;
using Knx.App.Services;

// Offline localization gates. No product code, no network, no Roslyn: everything is derived
// from the string facades and a text scan of src/ and tests/. Run with
//   dotnet run --project tests/Knx.Localization.Tests
// See README.md for the gate list and how to add a key or a language.
var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
    if (!ok) failures.Add(name + (detail is null ? "" : ": " + detail));
}
void Info(string text) => Console.WriteLine("  INFO " + text);
string Join(IEnumerable<string> items)
{
    var list = items.ToList();
    // I18N_GATE_VERBOSE=1 prints every finding instead of the first eight (maintenance aid).
    var limit = Environment.GetEnvironmentVariable("I18N_GATE_VERBOSE") == "1" ? int.MaxValue : 8;
    return list.Count <= limit ? string.Join("; ", list) : string.Join("; ", list.Take(limit)) + $"; ... (+{list.Count - limit})";
}

var root = SourceScan.FindRepoRoot();
var exceptions = SourceScan.ExceptionList.Load(Path.Combine(root, "tests/Knx.Localization.Tests/exceptions.txt"));

// --- the literal scanner itself: comments, escapes, interpolation holes ---
{
    var sample = """
        var a = "Speichern"; // BtnSave "Öffnen"
        /* "Gerät" */
        var b = 'x'; var c = "\"Ger\u00e4t\""; var d = @"C:\Ordner";
        var e = $"Zahl: {count} und {other ?? "Gerät"}";
        var f = $@"roh {x}";
        var g = "{{literal}}";
        """;
    var got = SourceScan.StringLiterals(sample).Select(l => l.Text).ToList();
    // Nested literals of an interpolation hole precede their outer literal by construction.
    var want = new List<string> { "Speichern", "\"Gerät\"", @"C:\Ordner", "Gerät", "Zahl: {} und {}", "roh {}", "{{literal}}" };
    Check("scan: literals, comments, escapes and interpolation holes", got.SequenceEqual(want), string.Join(" | ", got));
}

// --- the string facades ---
static IReadOnlyDictionary<string, (string De, string En)> MapOf(Type type) =>
    type.GetField("Map", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
        as IReadOnlyDictionary<string, (string De, string En)>
    ?? throw new InvalidOperationException($"{type.Name}.Map not found; update the localization gates");

var maps = new (string Module, string File, IReadOnlyDictionary<string, (string De, string En)> Map)[]
{
    ("core", "src/Knx.Core/Localization.cs", Strings.Catalogue),
    ("workbench", "src/Knx.App/Localization/WorkbenchStrings.cs", MapOf(typeof(WorkbenchStrings))),
    ("catalog", "src/Knx.App/Services/CatalogBrowser.cs", MapOf(typeof(CatalogUiText))),
    ("catalog-downloads", "src/Knx.App/Localization/CatalogDownloadStrings.cs", MapOf(typeof(CatalogDownloadStrings))),
    ("updates", "src/Knx.App/Localization/UpdateUiStrings.cs", MapOf(typeof(UpdateUiStrings))),
    ("recovery", "src/Knx.App/Localization/RecoveryUiStrings.cs", RecoveryUiStrings.Keys.ToDictionary(
        key => key, key => (RecoveryUiStrings.GetForLanguage("de", key), RecoveryUiStrings.GetForLanguage("en", key)), StringComparer.Ordinal)),
};
var mapByModule = maps.ToDictionary(m => m.Module, m => m.Map, StringComparer.Ordinal);
Info($"i18n: {maps.Sum(m => m.Map.Count)} keys ({string.Join(", ", maps.Select(m => m.Module + " " + m.Map.Count))})");

// --- G1/G4: parity, empty values, duplicate keys in the source maps ---
foreach (var (module, file, map) in maps)
{
    var empty = map.Where(p => string.IsNullOrWhiteSpace(p.Value.De) || string.IsNullOrWhiteSpace(p.Value.En))
        .Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
    Check($"i18n[{module}]: every key has non-empty de and en text", empty.Count == 0, Join(empty));

    var sourceKeys = new List<string>();
    foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(root, file)), "\\[\"([A-Za-z][A-Za-z0-9]*)\"\\]\\s*=\\s*(?:\\(|\\[)"))
        sourceKeys.Add(m.Groups[1].Value);
    var duplicates = sourceKeys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1)
        .Select(g => g.Key + " x" + g.Count()).ToList();
    Check($"i18n[{module}]: no duplicate keys in the source map", duplicates.Count == 0, Join(duplicates));
    Check($"i18n[{module}]: source and runtime key sets match ({sourceKeys.Count} entries)",
        sourceKeys.Count == map.Count && sourceKeys.ToHashSet(StringComparer.Ordinal).SetEquals(map.Keys),
        $"{sourceKeys.Count} source entries vs {map.Count} runtime keys");
}
Check("i18n: P0/P1 languages are registered",
    new[] { "de", "en", "fr", "it", "es", "nl" }.All(Strings.Available.Contains),
    string.Join(",", Strings.Available));
var originalLanguage = Strings.Language;
Strings.Language = "en-US";
Check("i18n: en-US resolves to the English base catalog", Strings.Language == "en",
    Strings.Get("MenuFile"));
Strings.Language = "de-AT";
Check("i18n: de-AT resolves to the German base catalog", Strings.Language == "de",
    Strings.Get("MenuFile"));
foreach (var (tag, expected) in new[]
         {
             ("fr-FR", "fr"), ("it-IT", "it"), ("es-ES", "es"), ("nl-NL", "nl"),
             ("zh-cn", "zh-CN"),
             ("pl-PL", "pl"),
         })
{
    Strings.Language = tag;
    Check($"i18n: {tag} resolves to {expected}", Strings.Language == expected, Strings.Get("MenuFile"));
}
Strings.Language = "xx";
Check("i18n: unsupported language falls back to German", Strings.Language == "de",
    Strings.Get("MenuFile"));
Strings.Language = originalLanguage;

// --- G1x: every supported overlay has the same shape as the German/English maps ---
var supportedLanguages = Strings.Available.Where(language => language is not ("de" or "en")).ToArray();
var catalogModules = new (string Module, Assembly Assembly,
    IReadOnlyDictionary<string, (string De, string En)> Source)[]
{
    ("core", typeof(Strings).Assembly, Strings.Catalogue),
    ("workbench", typeof(WorkbenchStrings).Assembly, MapOf(typeof(WorkbenchStrings))),
    ("catalog", typeof(CatalogUiText).Assembly, MapOf(typeof(CatalogUiText))),
    ("catalog-downloads", typeof(CatalogDownloadStrings).Assembly, MapOf(typeof(CatalogDownloadStrings))),
    ("updates", typeof(UpdateUiStrings).Assembly, MapOf(typeof(UpdateUiStrings))),
};
var placeholderShape = new Regex(@"\{(\d+)(?:[^}]*)\}", RegexOptions.CultureInvariant);
foreach (var language in supportedLanguages)
{
    foreach (var (module, assembly, source) in catalogModules)
    {
        var overlay = LanguageCatalog.Load(assembly, module, language);
        var missingKeys = source.Keys.Where(k => !overlay.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        var emptyValues = overlay.Where(p => string.IsNullOrWhiteSpace(p.Value))
            .Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Check($"i18n[{language}:{module}]: every source key is translated",
            missingKeys.Count == 0, Join(missingKeys));
        Check($"i18n[{language}:{module}]: no empty values", emptyValues.Count == 0, Join(emptyValues));
        Check($"i18n[{language}:{module}]: exact source key set",
            source.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(overlay.Keys));

        var mismatch = new List<string>();
        foreach (var (key, value) in source.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!overlay.TryGetValue(key, out var translated)) continue;
            string Shape(string text) => string.Join(",",
                placeholderShape.Matches(text).Select(m => m.Groups[1].Value)
                    .OrderBy(i => i, StringComparer.Ordinal));
            var expected = Shape(value.De);
            var actual = Shape(translated);
            if (expected != actual) mismatch.Add($"{key}: de {{{expected}}} {language} {{{actual}}}");
            var maxIndex = placeholderShape.Matches(value.De)
                .Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max();
            var arguments = Enumerable.Range(0, maxIndex + 1).Select(i => (object)$"v{i}").ToArray();
            try { _ = string.Format(CultureInfo.InvariantCulture, translated, arguments); }
            catch (FormatException) { mismatch.Add($"{key}: invalid composite format"); }
        }
        Check($"i18n[{language}:{module}]: placeholders match German", mismatch.Count == 0, Join(mismatch));
    }
}

foreach (var language in RecoveryUiStrings.Languages)
{
    var mismatches = new List<string>();
    foreach (var key in RecoveryUiStrings.Keys)
    {
        var source = RecoveryUiStrings.GetForLanguage("de", key);
        var translated = RecoveryUiStrings.GetForLanguage(language, key);
        string Shape(string text) => string.Join(",", placeholderShape.Matches(text)
            .Select(match => match.Groups[1].Value).OrderBy(index => index, StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(translated) || Shape(source) != Shape(translated)) mismatches.Add(key);
        var count = placeholderShape.Matches(source).Select(match => int.Parse(match.Groups[1].Value)).DefaultIfEmpty(-1).Max() + 1;
        try { _ = RecoveryUiStrings.GetForLanguage(language, key, Enumerable.Range(0, count).Select(index => (object)$"v{index}").ToArray()); }
        catch (FormatException) { mismatches.Add(key + ": invalid format"); }
    }
    Check($"i18n[{language}:recovery]: every value and placeholder is valid", mismatches.Count == 0, Join(mismatches));
}

var originalCatalogLanguage = Strings.Language;
foreach (var language in supportedLanguages)
{
    Strings.Language = language;
    var core = Strings.Get("MenuFile");
    var workbench = WorkbenchStrings.Get("Project");
    var downloads = CatalogDownloadStrings.Get("Title");
    var updates = UpdateUiStrings.Get("Title");
    var expectedCore = LanguageCatalog.Load(typeof(Strings).Assembly, "core", language)["MenuFile"];
    var expectedWorkbench = LanguageCatalog.Load(typeof(WorkbenchStrings).Assembly, "workbench", language)["Project"];
    var expectedDownloads = LanguageCatalog.Load(typeof(CatalogDownloadStrings).Assembly, "catalog-downloads", language)["Title"];
    var expectedUpdates = LanguageCatalog.Load(typeof(UpdateUiStrings).Assembly, "updates", language)["Title"];
    Check($"i18n: {language} UI resolves", core == expectedCore && workbench == expectedWorkbench && downloads == expectedDownloads && updates == expectedUpdates,
        $"{core} / {workbench} / {downloads} / {updates}");
}
Strings.Language = originalCatalogLanguage;

var fallbackReference = new Dictionary<string, (string De, string En)>
{
    ["OnlyKey"] = ("Deutsch", "English"),
};
Check("i18n: missing overlay key falls back to English",
    LanguageCatalog.Resolve(fallbackReference, new Dictionary<string, IReadOnlyDictionary<string, string>>(),
        "OnlyKey", "fr") == "English");
Check("i18n: missing reference key falls back to the key",
    LanguageCatalog.Resolve(fallbackReference, new Dictionary<string, IReadOnlyDictionary<string, string>>(),
        "MissingKey", "fr") == "MissingKey");

// --- G2: every referenced key exists in the map its call site resolves against ---
var locTr = new Regex(@"\{loc:Tr\s+([A-Za-z][A-Za-z0-9]*)\s*\}", RegexOptions.CultureInvariant);
var locWb = new Regex(@"\{loc:Wb\s+([A-Za-z][A-Za-z0-9]*)\s*\}", RegexOptions.CultureInvariant);
var locUp = new Regex(@"\{loc:Up\s+([A-Za-z][A-Za-z0-9]*)\s*\}", RegexOptions.CultureInvariant);
var callCore = new Regex("\\bStrings\\.Get\\(\\s*\"([A-Za-z][A-Za-z0-9]*)\"", RegexOptions.CultureInvariant);
var callWb = new Regex("\\bWorkbenchStrings\\.Get\\(\\s*\"([A-Za-z][A-Za-z0-9]*)\"", RegexOptions.CultureInvariant);
var callCat = new Regex("\\bCatalogUiText\\.Get\\(\\s*\"([A-Za-z][A-Za-z0-9]*)\"", RegexOptions.CultureInvariant);
var callDownloads = new Regex("\\bCatalogDownloadStrings\\.Get\\(\\s*\"([A-Za-z][A-Za-z0-9]*)\"", RegexOptions.CultureInvariant);
var callUpdates = new Regex("\\bUpdateUiStrings\\.Get\\(\\s*\"([A-Za-z][A-Za-z0-9]*)\"", RegexOptions.CultureInvariant);
var callRecovery = new Regex("\\bRecoveryUiStrings\\.Get\\(\\s*\"([A-Za-z][A-Za-z0-9]*)\"", RegexOptions.CultureInvariant);
var callUpdateWrap = new Regex("\\bU\\(\\s*\"([A-Za-z][A-Za-z0-9]*)\"", RegexOptions.CultureInvariant);
var callWrap = new Regex("\\b(W|Wb)\\(\\s*\"([A-Za-z][A-Za-z0-9]*)\"", RegexOptions.CultureInvariant);
var dynCore = new Regex("\\b(Strings|WorkbenchStrings|CatalogUiText|CatalogDownloadStrings|UpdateUiStrings)\\.Get\\(\\s*(?!\"[A-Za-z])", RegexOptions.CultureInvariant);
var dynWrap = new Regex("\\b(W|Wb)\\(\\s*(?!\"[A-Za-z])", RegexOptions.CultureInvariant);

var referenced = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
{
    ["core"] = new(StringComparer.Ordinal),
    ["workbench"] = new(StringComparer.Ordinal),
    ["catalog"] = new(StringComparer.Ordinal),
    ["catalog-downloads"] = new(StringComparer.Ordinal),
    ["updates"] = new(StringComparer.Ordinal),
    ["recovery"] = new(StringComparer.Ordinal),
};
var missing = new List<(string Site, string Detail)>();
var dynamicLookups = 0;
int LineAt(string text, int index) => 1 + text.Take(index).Count(c => c == '\n');

void Use(string file, int line, string module, string key, string form)
{
    referenced[module].Add(key);
    if (!mapByModule[module].ContainsKey(key))
        missing.Add(($"{file}|{key}", $"{file}:{line} {module} '{key}' via {form}"));
}

foreach (var file in SourceScan.Files(root, ["src", "tests"], "*.cs"))
{
    var text = SourceScan.StripComments(File.ReadAllText(Path.Combine(root, file)));
    foreach (Match m in callCore.Matches(text)) Use(file, LineAt(text, m.Index), "core", m.Groups[1].Value, "Strings.Get");
    foreach (Match m in callWb.Matches(text)) Use(file, LineAt(text, m.Index), "workbench", m.Groups[1].Value, "WorkbenchStrings.Get");
    foreach (Match m in callCat.Matches(text)) Use(file, LineAt(text, m.Index), "catalog", m.Groups[1].Value, "CatalogUiText.Get");
    foreach (Match m in callDownloads.Matches(text)) Use(file, LineAt(text, m.Index), "catalog-downloads", m.Groups[1].Value, "CatalogDownloadStrings.Get");
    foreach (Match m in callUpdates.Matches(text)) Use(file, LineAt(text, m.Index), "updates", m.Groups[1].Value, "UpdateUiStrings.Get");
    foreach (Match m in callRecovery.Matches(text)) Use(file, LineAt(text, m.Index), "recovery", m.Groups[1].Value, "RecoveryUiStrings.Get");
    if (file.StartsWith("src/Knx.App/UpdateWindow", StringComparison.Ordinal))
        foreach (Match m in callUpdateWrap.Matches(text)) Use(file, LineAt(text, m.Index), "updates", m.Groups[1].Value, "U(...)");
    foreach (Match m in callWrap.Matches(text)) Use(file, LineAt(text, m.Index), "workbench", m.Groups[2].Value, m.Groups[1].Value + "(...)");
    dynamicLookups += dynCore.Matches(text).Count + dynWrap.Matches(text).Count;
}
foreach (var file in SourceScan.Files(root, ["src", "tests"], "*.axaml"))
{
    var text = SourceScan.StripXamlComments(File.ReadAllText(Path.Combine(root, file)));
    foreach (Match m in locTr.Matches(text)) Use(file, LineAt(text, m.Index), "core", m.Groups[1].Value, "{loc:Tr}");
    foreach (Match m in locWb.Matches(text)) Use(file, LineAt(text, m.Index), "workbench", m.Groups[1].Value, "{loc:Wb}");
    foreach (Match m in locUp.Matches(text)) Use(file, LineAt(text, m.Index), "updates", m.Groups[1].Value, "{loc:Up}");
}
var unexpectedMissing = missing.Where(m => !exceptions.Missing.Contains(m.Site)).ToList();
Check("i18n: every referenced key exists in its module", unexpectedMissing.Count == 0,
    Join(unexpectedMissing.Select(m => m.Detail)));
var staleMissing = exceptions.Missing.Where(e => !missing.Any(m => m.Site == e)).ToList();
Info($"i18n: {dynamicLookups} dynamic key lookups not statically checked; {missing.Count} known unresolved, {staleMissing.Count} stale");

// --- G3 (report-only): keys never referenced in src or tests ---
{
    var literals = new HashSet<string>(StringComparer.Ordinal);
    var dictionaries = maps.Select(m => m.File).ToHashSet(StringComparer.Ordinal);
    foreach (var file in SourceScan.Files(root, ["src", "tests"], "*.cs", "tests/Knx.Localization.Tests/"))
    {
        if (dictionaries.Contains(file)) continue;
        foreach (var (_, literal) in SourceScan.StringLiterals(File.ReadAllText(Path.Combine(root, file))))
            literals.Add(literal);
    }
    foreach (var (module, _, map) in maps)
    {
        var unused = map.Keys.Count(k => !literals.Contains(k) && !referenced[module].Contains(k));
        Info($"i18n[{module}]: {unused} of {map.Count} keys unused in src+tests (report-only, prune candidates)");
    }
}

// --- G6: placeholders and format dry-run for every key in every language ---
var placeholder = new Regex(@"\{(\d+)(?:[^}]*)\}", RegexOptions.CultureInvariant);
foreach (var (module, _, map) in maps)
{
    var mismatch = new List<string>();
    var broken = new List<string>();
    foreach (var (key, value) in map.OrderBy(p => p.Key, StringComparer.Ordinal))
    {
        List<string> Indexes(string text) => placeholder.Matches(text)
            .Select(m => m.Groups[1].Value).OrderBy(i => i, StringComparer.Ordinal).ToList();
        var deIndexes = Indexes(value.De);
        var enIndexes = Indexes(value.En);
        if (!deIndexes.SequenceEqual(enIndexes))
            mismatch.Add($"{key}: de {{{string.Join(",", deIndexes)}}} en {{{string.Join(",", enIndexes)}}}");
        foreach (var (lang, text) in new[] { ("de", value.De), ("en", value.En) })
        {
            var rest = placeholder.Replace(text, "");
            if (rest.Contains('{') || rest.Contains('}')) { broken.Add($"{key}.{lang}: unbalanced braces"); continue; }
            var max = Indexes(text).Select(int.Parse).DefaultIfEmpty(-1).Max();
            var dummyArgs = Enumerable.Range(0, max + 1).Select(i => (object)("v" + i)).ToArray();
            try
            {
                var rendered = string.Format(CultureInfo.InvariantCulture, text, dummyArgs);
                if (rendered.Contains('{') || rendered.Contains('}')) broken.Add($"{key}.{lang}: braces after format");
            }
            catch (FormatException) { broken.Add($"{key}.{lang}: FormatException"); }
        }
    }
    Check($"i18n[{module}]: de/en placeholders match", mismatch.Count == 0, Join(mismatch));
    Check($"i18n[{module}]: every value formats with dummy args", broken.Count == 0, Join(broken));
}

// --- G7: no new hardcoded German literals in product source ---
{
    // Dev-log and selftest output stays German by design (analysis §5, G7); the dictionaries
    // are the German source of truth itself.
    string[] skip =
    [
        "src/Knx.Bus/", "src/Knx.Mcp/", "src/Knx.App.Tests/",
        // Bilingual document templates carry their de/en prose inline; moving them into the
        // UI dictionaries is tracked separately (same class as the acceptance protocol).
        "src/Knx.Reports/HandoverPackage",
        "src/Knx.App/Smoke.cs", "src/Knx.App/Screenshot.cs", "src/Knx.App/Workbench/WorkbenchCheck.cs",
        "src/Knx.Core/Localization.cs", "src/Knx.App/Localization/WorkbenchStrings.cs", "src/Knx.App/Services/CatalogBrowser.cs",
        "src/Knx.App/Localization/CatalogDownloadStrings.cs",
        "src/Knx.App/Localization/UpdateUiStrings.cs",
        "src/Knx.App/Localization/RecoveryUiStrings.cs",
    ];
    var files = SourceScan.Files(root, ["src"], "*.cs", skip)
        .Concat(SourceScan.Files(root, ["src"], "*.axaml", skip)).OrderBy(f => f, StringComparer.Ordinal);
    var found = new HashSet<string>(StringComparer.Ordinal);
    var violations = new List<string>();
    foreach (var file in files)
    {
        var text = File.ReadAllText(Path.Combine(root, file));
        var items = file.EndsWith(".axaml", StringComparison.Ordinal)
            ? SourceScan.XamlAttributeValues(text)
            : SourceScan.StringLiterals(text);
        foreach (var (line, literal) in items)
        {
            var reason = SourceScan.GermanReason(literal);
            if (reason is null || SourceScan.IsTechnicalToken(literal)) continue;
            var entry = file + "|" + literal;
            found.Add(entry);
            if (!exceptions.Allow.Contains(entry) && !exceptions.Ratchet.Contains(entry))
                violations.Add($"{file}:{line} [{reason}] \"{literal}\"");
        }
    }
    Check("german: no new hardcoded German literals outside the allowlist", violations.Count == 0, Join(violations));
    var stale = exceptions.Allow.Concat(exceptions.Ratchet).Where(e => !found.Contains(e)).ToList();
    Info($"german: {exceptions.Allow.Count} allowed, {exceptions.Ratchet.Count} ratchet entries, {stale.Count} stale (prune): {Join(stale)}");
}

// --- G5: pseudo-localization (test-only qps view; proves every key can be marked) ---
{
    // qps is not a product language: this transform exists only here. It expands every German
    // value into a marked pseudo text and keeps placeholders intact, so a future
    // "--lang=qps" walk can prove that no UI text bypasses the dictionaries.
    static string Pseudo(string text)
    {
        var sb = new StringBuilder("[!! ");
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                var end = text.IndexOf('}', i);
                if (end > i) { sb.Append(text, i, end - i + 1); i = end; continue; }
            }
            sb.Append(text[i] switch
            {
                'a' => "aa", 'e' => "ee", 'i' => "ii", 'o' => "oo", 'u' => "uu",
                'A' => "AA", 'E' => "EE", 'I' => "II", 'O' => "OO", 'U' => "UU",
                _ => text[i].ToString(),
            });
        }
        return sb.Append(" !!]").ToString();
    }

    foreach (var (module, _, map) in maps)
    {
        var qps = map.ToDictionary(p => p.Key, p => Pseudo(p.Value.De), StringComparer.Ordinal);
        var unmarked = qps.Where(p => !p.Value.StartsWith("[!! ", StringComparison.Ordinal)
                || !p.Value.EndsWith(" !!]", StringComparison.Ordinal)
                || p.Value == map[p.Key].De || p.Value == map[p.Key].En)
            .Select(p => p.Key).ToList();
        Check($"qps[{module}]: every key expands to a marked pseudo value", unmarked.Count == 0, Join(unmarked));

        var broken = new List<string>();
        foreach (var (key, value) in map)
        {
            var max = placeholder.Matches(value.De).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max();
            var dummyArgs = Enumerable.Range(0, max + 1).Select(i => "v" + i).ToArray();
            try
            {
                var rendered = string.Format(CultureInfo.InvariantCulture, qps[key], dummyArgs);
                foreach (var arg in dummyArgs.Where(a => !rendered.Contains(a, StringComparison.Ordinal)))
                    broken.Add($"{key}: {arg} lost");
            }
            catch (FormatException) { broken.Add($"{key}: FormatException"); }
        }
        Check($"qps[{module}]: placeholders survive the pseudo transform", broken.Count == 0, Join(broken));
    }

    var language = Strings.Language;
    Strings.Language = "qps";
    Check("qps: test-only language falls back to German and leaves de/en untouched",
        Strings.Get("BtnSave") == "Speichern" && WorkbenchStrings.Get("Project") == "Projekt"
        && CatalogUiText.Get("SegmentLabel", 1) == "Segment 1");
    Strings.Language = "en";
    Check("qps: real languages keep their text", Strings.Get("BtnSave") == "Save" && WorkbenchStrings.Get("Project") == "Project");
    Strings.Language = language;
}

Console.WriteLine(failures.Count == 0 ? "\nALLE CHECKS OK" : $"\n{failures.Count} FEHLER: {string.Join("; ", failures)}");
return failures.Count == 0 ? 0 : 1;
