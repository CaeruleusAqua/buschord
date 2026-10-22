using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Offline source scans behind the localization gates. Text-based on purpose: the gates must
/// stay deterministic and dependency-free (no Roslyn), identical on Windows and Linux, and
/// fast enough to run in both CI jobs.
/// </summary>
public static class SourceScan
{
    /// <summary>Repository root (the directory holding ets6.sln), found from the test binaries.</summary>
    public static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ets6.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        throw new InvalidOperationException("repository root (ets6.sln) not found");
    }

    /// <summary>Same length as the input, comments blanked out (newlines kept, line numbers stay valid).</summary>
    public static string StripComments(string code)
    {
        var lexer = new Lexer(code);
        lexer.Run();
        var sb = new StringBuilder(code);
        foreach (var (start, end) in lexer.Comments)
            for (var i = start; i < end && i < sb.Length; i++)
                if (sb[i] != '\n') sb[i] = ' ';
        return sb.ToString();
    }

    /// <summary>Same length as the input, &lt;!-- --&gt; comments blanked out (newlines kept).</summary>
    public static string StripXamlComments(string xaml) =>
        Regex.Replace(xaml, "<!--.*?-->", m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()),
            RegexOptions.Singleline);

    /// <summary>Repo-relative, forward-slashed paths of matching files, ordinal-sorted; bin/obj excluded.</summary>
    public static List<string> Files(string root, IEnumerable<string> relativeDirs, string pattern, params string[] excludedPrefixes)
    {
        var files = new List<string>();
        foreach (var relativeDir in relativeDirs)
        {
            var dir = Path.Combine(root, relativeDir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
            {
                var rel = Relative(root, file);
                if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)) continue;
                if (excludedPrefixes.Any(p => rel.StartsWith(p, StringComparison.Ordinal))) continue;
                files.Add(rel);
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    public static string Relative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    // ------------------------------------------------------------------ C# literals

    /// <summary>
    /// Every string literal in a C# file with its start line: normal, verbatim, interpolated
    /// (holes normalized to "{}", nested literals inside holes are extracted too). Comments and
    /// char literals are skipped, escapes are decoded, so German detection sees real characters.
    /// </summary>
    public static List<(int Line, string Text)> StringLiterals(string code)
    {
        var lexer = new Lexer(code);
        lexer.Run();
        return lexer.Literals;
    }

    private sealed class Lexer
    {
        private readonly string _s;
        private int _i;
        private int _line = 1;
        public List<(int Line, string Text)> Literals { get; } = new();
        public List<(int Start, int End)> Comments { get; } = new();

        public Lexer(string s) => _s = s;

        private bool AtEnd => _i >= _s.Length;
        private char Cur => _s[_i];
        private char Peek(int offset) => _i + offset < _s.Length ? _s[_i + offset] : '\0';

        public void Run()
        {
            while (!AtEnd)
            {
                char c = Cur;
                if (c == '\n') { _line++; _i++; }
                else if (c == '/' && Peek(1) == '/') SkipLineComment();
                else if (c == '/' && Peek(1) == '*') SkipBlockComment();
                else if (c == '\'') SkipCharLiteral();
                else if (c == '@' && Peek(1) == '"') { _i += 2; ScanVerbatim(); }
                else if (c == '$' && Peek(1) == '"') { _i += 2; ScanInterpolated(verbatim: false); }
                else if (c == '$' && Peek(1) == '@' && Peek(2) == '"') { _i += 3; ScanInterpolated(verbatim: true); }
                else if (c == '"') { _i++; ScanNormal(); }
                else _i++;
            }
        }

        private void SkipLineComment()
        {
            var start = _i;
            while (!AtEnd && Cur != '\n') _i++;
            Comments.Add((start, _i));
        }

        private void SkipBlockComment()
        {
            var start = _i;
            _i += 2;
            while (!AtEnd && !(Cur == '*' && Peek(1) == '/'))
            {
                if (Cur == '\n') _line++;
                _i++;
            }
            _i = Math.Min(_i + 2, _s.Length);
            Comments.Add((start, _i));
        }

        private void SkipCharLiteral()
        {
            _i++;
            while (!AtEnd && Cur != '\'' && Cur != '\n')
            {
                if (Cur == '\\') _i++;
                _i++;
            }
            if (!AtEnd && Cur == '\'') _i++;
        }

        private void ScanNormal()
        {
            var startLine = _line;
            var sb = new StringBuilder();
            while (!AtEnd && Cur != '"')
            {
                if (Cur == '\\') { AppendEscape(sb); continue; }
                if (Cur == '\n') _line++;
                sb.Append(Cur);
                _i++;
            }
            if (!AtEnd) _i++; // closing quote
            Literals.Add((startLine, sb.ToString()));
        }

        private void ScanVerbatim()
        {
            var startLine = _line;
            var sb = new StringBuilder();
            while (!AtEnd)
            {
                if (Cur == '"' && Peek(1) == '"') { sb.Append('"'); _i += 2; continue; }
                if (Cur == '"') { _i++; break; }
                if (Cur == '\n') _line++;
                sb.Append(Cur);
                _i++;
            }
            Literals.Add((startLine, sb.ToString()));
        }

        private void ScanInterpolated(bool verbatim)
        {
            var startLine = _line;
            var sb = new StringBuilder();
            while (!AtEnd)
            {
                if (!verbatim && Cur == '\\') { AppendEscape(sb); continue; }
                if (verbatim && Cur == '"' && Peek(1) == '"') { sb.Append('"'); _i += 2; continue; }
                if (!verbatim && Cur == '{' && Peek(1) == '{') { sb.Append('{'); _i += 2; continue; }
                if (!verbatim && Cur == '}' && Peek(1) == '}') { sb.Append('}'); _i += 2; continue; }
                if (Cur == '{') { _i++; ScanHole(); sb.Append("{}"); continue; }
                if (Cur == '"') { _i++; break; }
                if (Cur == '\n') _line++;
                sb.Append(Cur);
                _i++;
            }
            Literals.Add((startLine, sb.ToString()));
        }

        /// <summary>Skips an interpolation hole; nested string literals inside it are extracted.</summary>
        private void ScanHole()
        {
            var depth = 1;
            while (!AtEnd && depth > 0)
            {
                char c = Cur;
                if (c == '\n') { _line++; _i++; }
                else if (c == '{') { depth++; _i++; }
                else if (c == '}') { depth--; _i++; }
                else if (c == '\'') SkipCharLiteral();
                else if (c == '/' && Peek(1) == '/') SkipLineComment();
                else if (c == '/' && Peek(1) == '*') SkipBlockComment();
                else if (c == '@' && Peek(1) == '"') { _i += 2; ScanVerbatim(); }
                else if (c == '$' && Peek(1) == '"') { _i += 2; ScanInterpolated(verbatim: false); }
                else if (c == '"') { _i++; ScanNormal(); }
                else _i++;
            }
        }

        private void AppendEscape(StringBuilder sb)
        {
            _i++; // backslash
            if (AtEnd) return;
            char e = Cur;
            switch (e)
            {
                case 'n': sb.Append('\n'); _i++; break;
                case 'r': sb.Append('\r'); _i++; break;
                case 't': sb.Append('\t'); _i++; break;
                case '0': sb.Append('\0'); _i++; break;
                case '\\': sb.Append('\\'); _i++; break;
                case '"': sb.Append('"'); _i++; break;
                case '\'': sb.Append('\''); _i++; break;
                case 'u':
                    if (_i + 4 < _s.Length && int.TryParse(_s.AsSpan(_i + 1, 4), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var code))
                    {
                        sb.Append((char)code);
                        _i += 5;
                    }
                    else { sb.Append(e); _i++; }
                    break;
                default: sb.Append(e); _i++; break;
            }
        }
    }

    // ------------------------------------------------------------------ XAML values

    /// <summary>Attribute values of a XAML file with their line; comments and markup ({...}) are skipped.</summary>
    public static List<(int Line, string Text)> XamlAttributeValues(string xaml)
    {
        var clean = StripXamlComments(xaml);
        var values = new List<(int Line, string Text)>();
        var line = 1;
        var scanned = 0;
        foreach (Match m in Regex.Matches(clean, "=\"([^\"]*)\""))
        {
            while (scanned < m.Index)
            {
                if (clean[scanned] == '\n') line++;
                scanned++;
            }
            var value = m.Groups[1].Value;
            if (value.Length > 0 && value[0] != '{') values.Add((line, value));
        }
        return values;
    }

    // ------------------------------------------------------------------ German detection

    /// <summary>German-only words that are no valid English text; matched on letter boundaries.</summary>
    private const string GermanWords =
        "der|die|das|und|oder|nicht|mit|von|zur|zum|wird|werden|wurde|wurden|kein|keine|keinen|eine|einen|einem|eines|" +
        "dieser|diese|dieses|sind|ist|beim|f\u00fcr|\u00fcber|k\u00f6nnen|kann|muss|m\u00fcssen|darf|soll|wenn|dann|auch|" +
        "noch|nur|aus|bei|nach|vor|zwischen|sowie|mehr|schon|bitte|Etiketten|Datei|Ordner|Projekt|Katalog|Ger\u00e4t|Ger\u00e4te|" +
        "Adresse|Gruppenadresse|Gruppenbereich|Geb\u00e4udeknoten|Bereich|Linie|Vorschau|\u00c4nderung|Auswahl|Meldung|Hinweis|" +
        "Warnung|Fehler|Pr\u00fcfung|Befund|Bezeichnung|Zuordnung|Verkn\u00fcpfung|Speichern|Laden|\u00d6ffnen|Schlie\u00dfen|" +
        "Abbrechen|Bearbeiten|Suchen|Verbinden|Trennen|Verwerfen|\u00dcbernehmen|Anwenden|Standardwerte|Schreibgesch\u00fctzt|" +
        "Ungespeicherte|verworfen|angelegt|geladen|gespeichert|gepr\u00fcft|ge\u00f6ffnet|ung\u00fcltig|voll";

    private static readonly Regex GermanWord = new(
        @"(?<![\p{L}])(" + GermanWords + @")(?![\p{L}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Umlaut = new("[\u00e4\u00f6\u00fc\u00c4\u00d6\u00dc\u00df]", RegexOptions.CultureInvariant);
    private static readonly Regex LangGerman = new(@"lang\s*=\s*[""']?de[""']?(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FileName = new(@"^[\w./\\ -]+\.(json|csv|html|ttl|knxproj|xml|png|md|txt)$", RegexOptions.CultureInvariant);

    /// <summary>Why a literal counts as German, or null when it does not.</summary>
    public static string? GermanReason(string text)
    {
        if (Umlaut.IsMatch(text)) return "umlaut";
        var word = GermanWord.Match(text);
        if (word.Success) return "word:" + word.Value.ToLowerInvariant();
        if (LangGerman.IsMatch(text)) return "lang=de";
        return null;
    }

    /// <summary>Technical tokens that never reach the user as prose: file names and paths.</summary>
    public static bool IsTechnicalToken(string text) => FileName.IsMatch(text);

    // ------------------------------------------------------------------ exceptions file

    /// <summary>
    /// Explicit exceptions: "allow path|literal" (by design), "ratchet path|literal" (known
    /// German leak, shrink only) and "missing path|key" (known unresolved key reference).
    /// </summary>
    public sealed record ExceptionList(
        HashSet<string> Allow, HashSet<string> Ratchet, HashSet<string> Missing)
    {
        public static ExceptionList Load(string path)
        {
            var allow = new HashSet<string>(StringComparer.Ordinal);
            var ratchet = new HashSet<string>(StringComparer.Ordinal);
            var missing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var space = line.IndexOf(' ');
                if (space <= 0) throw new InvalidDataException($"exceptions.txt: bad line '{raw}'");
                var kind = line[..space];
                var rest = line[(space + 1)..];
                var bar = rest.IndexOf('|');
                if (bar <= 0) throw new InvalidDataException($"exceptions.txt: missing '|' in '{raw}'");
                // Only the path is normalized; the literal after the first '|' stays byte-exact.
                var entry = rest[..bar].Trim().Replace('\\', '/') + "|" + rest[(bar + 1)..].TrimEnd();
                var set = kind switch
                {
                    "allow" => allow,
                    "ratchet" => ratchet,
                    "missing" => missing,
                    _ => throw new InvalidDataException($"exceptions.txt: unknown kind '{kind}'"),
                };
                set.Add(entry);
            }
            return new ExceptionList(allow, ratchet, missing);
        }
    }
}
