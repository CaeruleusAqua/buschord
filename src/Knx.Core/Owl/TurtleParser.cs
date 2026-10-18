using System.Globalization;
using System.Text;

namespace Knx.Owl;

/// <summary>Malformed Turtle input; carries the position for diagnostics.</summary>
public sealed class TurtleParseException : Exception
{
    public TurtleParseException(string message, int line, int column)
        : base($"{message} (line {line}, column {column})")
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }
    public int Column { get; }
}

/// <summary>
/// Streaming parser for the Turtle subset used by the KNX ontology (ontology.ttl):
/// @prefix/PREFIX, IRIs, prefixed names, literals with language/datatype, "a",
/// ";" and "," lists, blank nodes and "[ ... ]" property lists. RDF collections
/// "( ... )" are skipped with a placeholder blank node and counted, because the
/// KIM layer never reasons over rdf:List structures.
/// </summary>
public sealed class TurtleParser
{
    private const int None = -2;
    private const string RdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
    private const string XsdInteger = "http://www.w3.org/2001/XMLSchema#integer";
    private const string XsdDecimal = "http://www.w3.org/2001/XMLSchema#decimal";
    private const string XsdDouble = "http://www.w3.org/2001/XMLSchema#double";
    private const string XsdBoolean = "http://www.w3.org/2001/XMLSchema#boolean";

    private readonly Dictionary<string, string> _prefixes = new(StringComparer.Ordinal);
    private readonly Stack<int> _pushback = new();
    private readonly List<string> _warnings = [];
    private TextReader _reader = TextReader.Null;
    private Action<string, string, OwlTerm> _sink = (_, _, _) => { };
    private int _peeked = None;
    private int _line = 1;
    private int _column;
    private int _blankCounter;
    private string? _base;

    /// <summary>Triples handed to the sink.</summary>
    public int TripleCount { get; private set; }

    /// <summary>Anonymous blank nodes created for "[ ... ]" constructs.</summary>
    public int BlankNodeCount { get; private set; }

    /// <summary>RDF collections skipped (rdf:List is recorded as a single blank node).</summary>
    public int CollectionCount { get; private set; }

    /// <summary>Prefix/base directives seen.</summary>
    public int DirectiveCount { get; private set; }

    /// <summary>Non-fatal findings about constructs that were skipped.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Parses the whole document and reports each triple to <paramref name="onTriple"/>.</summary>
    public void Parse(TextReader reader, Action<string, string, OwlTerm> onTriple)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _sink = onTriple ?? throw new ArgumentNullException(nameof(onTriple));
        _prefixes.Clear();
        _pushback.Clear();
        _warnings.Clear();
        _peeked = None;
        _line = 1;
        _column = 0;
        _blankCounter = 0;
        _base = null;
        TripleCount = 0;
        BlankNodeCount = 0;
        CollectionCount = 0;
        DirectiveCount = 0;

        while (true)
        {
            SkipWhitespace();
            var c = Peek();
            if (c < 0) break;

            if (c == '@')
            {
                ParseAtDirective();
                continue;
            }

            var subject = c switch
            {
                '[' => ParseBlankNodePropertyList(),
                '(' => ParseCollection(),
                '<' => ParseIriRef(),
                '_' => ParseBlankNodeLabel(),
                _ => ParseNameOrDirective(),
            };
            if (subject is null) continue; // directive consumed

            ParsePredicateObjectList(subject, '.');
            SkipWhitespace();
            Expect('.');
        }

        if (CollectionCount > 0)
            _warnings.Add($"{CollectionCount} RDF collection(s) skipped (represented as blank nodes).");
    }

    private string? ParseNameOrDirective()
    {
        var name = ReadName();
        if (name.Length == 0) throw Error($"unexpected character '{Describe(Peek())}'");

        if (!name.Contains(':') && name.Equals("PREFIX", StringComparison.OrdinalIgnoreCase))
        {
            ParsePrefixBody();
            return null;
        }
        if (!name.Contains(':') && name.Equals("BASE", StringComparison.OrdinalIgnoreCase))
        {
            ParseBaseBody();
            return null;
        }
        return Expand(name);
    }

    private void ParseAtDirective()
    {
        ReadChar(); // '@'
        var name = ReadName();
        if (name.Equals("prefix", StringComparison.OrdinalIgnoreCase))
        {
            ParsePrefixBody();
            SkipWhitespace();
            Expect('.');
        }
        else if (name.Equals("base", StringComparison.OrdinalIgnoreCase))
        {
            ParseBaseBody();
            SkipWhitespace();
            Expect('.');
        }
        else
        {
            throw Error($"unsupported directive '@{name}'");
        }
    }

    private void ParsePrefixBody()
    {
        SkipWhitespace();
        var label = ReadName();
        if (!label.EndsWith(':'))
            throw Error($"prefix label '{label}' must end with ':'");
        SkipWhitespace();
        var iri = ParseIriRef();
        _prefixes[label[..^1]] = iri;
        DirectiveCount++;
    }

    private void ParseBaseBody()
    {
        SkipWhitespace();
        _base = ParseIriRef();
        DirectiveCount++;
    }

    private void ParsePredicateObjectList(string subject, char terminator)
    {
        while (true)
        {
            SkipWhitespace();
            var c = Peek();
            if (c < 0 || c == terminator || c == '.') return;

            var predicate = ParsePredicate();
            while (true)
            {
                SkipWhitespace();
                var obj = ParseObject();
                _sink(subject, predicate, obj);
                TripleCount++;
                SkipWhitespace();
                if (Peek() != ',') break;
                ReadChar();
            }

            SkipWhitespace();
            if (Peek() != ';') break;
            ReadChar();
        }
    }

    private string ParsePredicate()
    {
        SkipWhitespace();
        if (Peek() == '<') return ParseIriRef();
        var name = ReadName();
        if (name.Length == 0) throw Error("expected predicate");
        return name == "a" ? RdfType : Expand(name);
    }

    private OwlTerm ParseObject()
    {
        SkipWhitespace();
        var c = Peek();
        if (c < 0) throw Error("unexpected end of file, expected object");

        if (c == '[') return OwlTerm.Iri(ParseBlankNodePropertyList());
        if (c == '(') return OwlTerm.Iri(ParseCollection());
        if (c == '<') return OwlTerm.Iri(ParseIriRef());
        if (c == '_') return OwlTerm.Iri(ParseBlankNodeLabel());
        if (c is '"' or '\'') return ParseLiteral(c);
        if (c is '+' or '-' || char.IsDigit((char)c)) return ParseNumber();

        var name = ReadName();
        if (name.Length == 0) throw Error($"unexpected character '{Describe(c)}', expected object");
        if (name is "true" or "false")
            return new OwlTerm(OwlTermKind.Literal, name, Datatype: XsdBoolean);
        return OwlTerm.Iri(Expand(name));
    }

    private string ParseBlankNodePropertyList()
    {
        ReadChar(); // '['
        var id = NewBlank();
        ParsePredicateObjectList(id, ']');
        SkipWhitespace();
        Expect(']');
        return id;
    }

    /// <summary>Skips an RDF collection and returns one placeholder blank node.</summary>
    private string ParseCollection()
    {
        ReadChar(); // '('
        CollectionCount++;
        var id = NewBlank();
        while (true)
        {
            SkipWhitespace();
            var c = Peek();
            if (c < 0) throw Error("unterminated collection");
            if (c == ')')
            {
                ReadChar();
                return id;
            }
            ParseObject(); // discard, inner lists are skipped recursively
        }
    }

    private string ParseBlankNodeLabel()
    {
        var name = ReadName();
        if (!name.StartsWith("_:", StringComparison.Ordinal) || name.Length == 2)
            throw Error($"invalid blank node label '{name}'");
        return name;
    }

    private string NewBlank()
    {
        BlankNodeCount++;
        return "_:g" + _blankCounter++.ToString(CultureInfo.InvariantCulture);
    }

    private OwlTerm ParseLiteral(int quote)
    {
        ReadChar(); // opening quote

        var longForm = false;
        if (Peek() == quote)
        {
            ReadChar();
            if (Peek() == quote)
            {
                ReadChar();
                longForm = true;
            }
            else
            {
                Unread(quote); // a single quote is the closing delimiter of a short literal
            }
        }

        var sb = new StringBuilder();
        while (true)
        {
            var ch = ReadChar();
            if (ch < 0) throw Error("unterminated string literal");

            if (ch == '\\')
            {
                AppendEscape(sb);
                continue;
            }

            if (ch == quote)
            {
                if (!longForm) break;
                if (Peek() == quote)
                {
                    ReadChar();
                    if (Peek() == quote)
                    {
                        ReadChar();
                        break;
                    }
                    Unread(quote);
                }
                sb.Append((char)ch);
                continue;
            }

            sb.Append((char)ch);
        }

        SkipWhitespace();
        string? language = null;
        string? datatype = null;

        if (Peek() == '@')
        {
            ReadChar();
            var tag = new StringBuilder();
            while (true)
            {
                var t = Peek();
                if (t < 0 || (!char.IsLetterOrDigit((char)t) && t != '-')) break;
                tag.Append((char)ReadChar());
            }
            language = tag.ToString();
        }
        else if (Peek() == '^')
        {
            ReadChar();
            Expect('^');
            SkipWhitespace();
            datatype = Peek() == '<' ? ParseIriRef() : Expand(ReadName());
        }

        return new OwlTerm(OwlTermKind.Literal, sb.ToString(), language, datatype);
    }

    private void AppendEscape(StringBuilder sb)
    {
        var e = ReadChar();
        switch (e)
        {
            case 't': sb.Append('\t'); return;
            case 'b': sb.Append('\b'); return;
            case 'n': sb.Append('\n'); return;
            case 'r': sb.Append('\r'); return;
            case 'f': sb.Append('\f'); return;
            case '"': sb.Append('"'); return;
            case '\'': sb.Append('\''); return;
            case '\\': sb.Append('\\'); return;
            case 'u': AppendCodePoint(sb, 4); return;
            case 'U': AppendCodePoint(sb, 8); return;
            case < 0: throw Error("unterminated escape sequence");
            default: sb.Append((char)e); return; // lenient: keep unknown escapes
        }
    }

    private void AppendCodePoint(StringBuilder sb, int digits)
    {
        var hex = new char[digits];
        for (var i = 0; i < digits; i++)
        {
            var c = ReadChar();
            if (c < 0) throw Error("unterminated unicode escape");
            hex[i] = (char)c;
        }
        if (!int.TryParse(new string(hex), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            throw Error("invalid unicode escape");
        sb.Append(char.ConvertFromUtf32(code));
    }

    private OwlTerm ParseNumber()
    {
        var sb = new StringBuilder();
        if (Peek() is '+' or '-') sb.Append((char)ReadChar());
        while (char.IsDigit((char)Peek())) sb.Append((char)ReadChar());

        var datatype = XsdInteger;
        if (Peek() == '.' && char.IsDigit((char)PeekNext()))
        {
            sb.Append((char)ReadChar());
            while (char.IsDigit((char)Peek())) sb.Append((char)ReadChar());
            datatype = XsdDecimal;
        }

        if (Peek() is 'e' or 'E')
        {
            sb.Append((char)ReadChar());
            if (Peek() is '+' or '-') sb.Append((char)ReadChar());
            if (!char.IsDigit((char)Peek()))
                throw Error("invalid number: missing exponent digits");
            while (char.IsDigit((char)Peek())) sb.Append((char)ReadChar());
            datatype = XsdDouble;
        }

        return new OwlTerm(OwlTermKind.Literal, sb.ToString(), Datatype: datatype);
    }

    private string ParseIriRef()
    {
        Expect('<');
        var sb = new StringBuilder();
        while (true)
        {
            var c = ReadChar();
            if (c < 0) throw Error("unterminated IRI");
            if (c == '>') break;
            if (c is '\n' or '\r') throw Error("newline inside IRI");
            if (c == '\\')
            {
                var e = ReadChar();
                if (e is 'u' or 'U')
                {
                    // \uXXXX and \UXXXXXXXX IRIs are rare but legal
                    AppendCodePoint(sb, e == 'u' ? 4 : 8);
                    continue;
                }
                sb.Append((char)e);
                continue;
            }
            sb.Append((char)c);
        }

        var iri = sb.ToString();
        if (_base is not null && !iri.Contains(':')) iri = _base + iri;
        return iri;
    }

    private string Expand(string name)
    {
        var colon = name.IndexOf(':');
        if (colon < 0) throw Error($"invalid prefixed name '{name}'");
        var prefix = name[..colon];
        if (!_prefixes.TryGetValue(prefix, out var ns))
            throw Error($"unknown prefix '{prefix}:'");
        return ns + name[(colon + 1)..];
    }

    /// <summary>Reads a prefixed name, IRI scheme word or keyword; never consumes the trailing '.' of a statement.</summary>
    private string ReadName()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var c = Peek();
            if (c == '\\')
            {
                ReadChar();
                var e = ReadChar();
                if (e < 0) throw Error("unterminated name escape");
                sb.Append((char)e);
                continue;
            }
            if (!IsNameChar(c)) break;

            if (c == '.')
            {
                ReadChar();
                var next = Peek();
                if (IsNameChar(next) && next != '.')
                {
                    sb.Append('.');
                    continue;
                }
                Unread('.');
                break;
            }

            sb.Append((char)ReadChar());
        }
        return sb.ToString();
    }

    private static bool IsNameChar(int c)
        => c >= 0 && (char.IsLetterOrDigit((char)c) || c is '_' or '-' or ':' or '.' or '%' or '\\');

    private void SkipWhitespace()
    {
        while (true)
        {
            var c = Peek();
            if (c < 0) return;
            if (char.IsWhiteSpace((char)c))
            {
                ReadChar();
                continue;
            }
            if (c == '#')
            {
                while (true)
                {
                    var comment = ReadChar();
                    if (comment < 0 || comment == '\n') break;
                }
                continue;
            }
            return;
        }
    }

    private int Peek()
    {
        if (_pushback.Count > 0) return _pushback.Peek();
        if (_peeked == None) _peeked = _reader.Read();
        return _peeked;
    }

    private int PeekNext()
    {
        var c = ReadChar();
        var next = Peek();
        Unread(c);
        return next;
    }

    private int ReadChar()
    {
        int c;
        if (_pushback.Count > 0)
        {
            c = _pushback.Pop();
        }
        else
        {
            c = _peeked != None ? _peeked : _reader.Read();
            _peeked = None;
        }

        if (c == '\n')
        {
            _line++;
            _column = 0;
        }
        else if (c >= 0)
        {
            _column++;
        }
        return c;
    }

    private void Unread(int c)
    {
        if (c >= 0) _pushback.Push(c);
    }

    private void Expect(char expected)
    {
        var c = ReadChar();
        if (c != expected)
            throw Error($"expected '{expected}' but found '{Describe(c)}'");
    }

    private TurtleParseException Error(string message) => new(message, _line, _column);

    private static string Describe(int c) => c < 0 ? "end of file" : ((char)c).ToString();
}
