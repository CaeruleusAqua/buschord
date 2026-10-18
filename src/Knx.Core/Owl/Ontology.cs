using System.Text;

namespace Knx.Owl;

/// <summary>Kind of an RDF term in the loaded graph.</summary>
public enum OwlTermKind { Iri, BlankNode, Literal }

/// <summary>One RDF term: an IRI, a blank node label ("_:g1") or a literal.</summary>
public sealed record OwlTerm(OwlTermKind Kind, string Value, string? Language = null, string? Datatype = null)
{
    public bool IsLiteral => Kind == OwlTermKind.Literal;
    public bool IsBlank => Kind == OwlTermKind.BlankNode;

    public static OwlTerm Iri(string value) => new(OwlTermKind.Iri, value);

    public override string ToString() => Kind switch
    {
        OwlTermKind.Literal => "\"" + Value + "\""
            + (Language is not null ? "@" + Language : Datatype is not null ? "^^<" + Datatype + ">" : ""),
        _ => Value,
    };
}

/// <summary>
/// RDF graph loaded from Turtle: subject → predicate → objects. Provides the
/// class/property queries the KIM layer needs, with the rdfs:subClassOf and
/// rdfs:subPropertyOf transitive closures computed once and cached.
/// </summary>
public sealed class Ontology
{
    public const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    public const string Rdfs = "http://www.w3.org/2000/01/rdf-schema#";
    public const string OwlNamespace = "http://www.w3.org/2002/07/owl#";
    public const string Xsd = "http://www.w3.org/2001/XMLSchema#";
    public const string Dct = "http://purl.org/dc/terms/";
    public const string DctSubject = Dct + "subject";
    public const string DctDescription = Dct + "description";
    public const string RdfType = Rdf + "type";
    public const string RdfsLabel = Rdfs + "label";
    public const string RdfsComment = Rdfs + "comment";
    public const string RdfsSubClassOf = Rdfs + "subClassOf";
    public const string RdfsSubPropertyOf = Rdfs + "subPropertyOf";
    public const string RdfsRange = Rdfs + "range";
    public const string RdfsDomain = Rdfs + "domain";
    public const string KnxLabel = SemanticExport.KnxNamespace + "label";

    private readonly Dictionary<string, Dictionary<string, List<OwlTerm>>> _spo = new(StringComparer.Ordinal);
    private Dictionary<string, HashSet<string>>? _superClasses;
    private Dictionary<string, HashSet<string>>? _subClasses;
    private Dictionary<string, HashSet<string>>? _superProperties;
    private Dictionary<string, HashSet<string>>? _subProperties;
    private bool _closureDirty = true;

    /// <summary>RDF collections skipped while loading; exposed for diagnostics.</summary>
    public int CollectionsSkipped { get; private set; }

    /// <summary>Non-fatal parser warnings.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public int TripleCount => _spo.Values.Sum(p => p.Values.Sum(o => o.Count));

    public IEnumerable<string> Subjects => _spo.Keys;

    /// <summary>Loads a Turtle file from disk.</summary>
    public static Ontology Load(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Read(reader);
    }

    /// <summary>Parses Turtle from a string.</summary>
    public static Ontology FromTurtle(string turtle) => Read(new StringReader(turtle));

    /// <summary>Parses Turtle from a reader.</summary>
    public static Ontology Read(TextReader reader)
    {
        var ontology = new Ontology();
        var parser = new TurtleParser();
        parser.Parse(reader, ontology.Add);
        ontology.CollectionsSkipped = parser.CollectionCount;
        ontology.Warnings = parser.Warnings;
        return ontology;
    }

    /// <summary>Adds one triple; invalidates the cached closures.</summary>
    public void Add(string subject, string predicate, OwlTerm obj)
    {
        if (!_spo.TryGetValue(subject, out var predicates))
            _spo[subject] = predicates = new Dictionary<string, List<OwlTerm>>(StringComparer.Ordinal);
        if (!predicates.TryGetValue(predicate, out var objects))
            predicates[predicate] = objects = [];
        objects.Add(obj);
        _closureDirty = true;
    }

    public IReadOnlyList<OwlTerm> Objects(string subject, string predicate)
        => _spo.TryGetValue(subject, out var predicates) && predicates.TryGetValue(predicate, out var objects)
            ? objects
            : [];

    public IEnumerable<string> IriObjects(string subject, string predicate)
        => Objects(subject, predicate).Where(o => !o.IsLiteral).Select(o => o.Value);

    public IEnumerable<string> LiteralValues(string subject, string predicate)
        => Objects(subject, predicate).Where(o => o.IsLiteral).Select(o => o.Value);

    /// <summary>First literal value, or null.</summary>
    public string? Value(string subject, string predicate)
        => Objects(subject, predicate).FirstOrDefault(o => o.IsLiteral)?.Value;

    public bool Has(string subject, string predicate, string value)
        => Objects(subject, predicate).Any(o => o.Value == value);

    public bool IsA(string subject, string classIri)
        => Objects(subject, RdfType).Any(t => t.Value == classIri)
           || Objects(subject, RdfType).Any(t => SuperClassesOf(t.Value).Contains(classIri));

    /// <summary>Label: rdfs:label, then knx:label, then the IRI local name.</summary>
    public string? Label(string subject)
    {
        foreach (var predicate in new[] { RdfsLabel, KnxLabel })
        {
            var labels = Objects(subject, predicate).Where(o => o.IsLiteral).ToList();
            if (labels.Count == 0) continue;
            var preferred = labels.FirstOrDefault(l => l.Language is null)
                            ?? labels.FirstOrDefault(l => l.Language == "en")
                            ?? labels[0];
            return preferred.Value;
        }
        return LocalName(subject);
    }

    public string? Comment(string subject)
        => Value(subject, RdfsComment) ?? Value(subject, DctDescription);

    /// <summary>Classes that are (transitively) superclasses of <paramref name="classIri"/>.</summary>
    public IReadOnlySet<string> SuperClassesOf(string classIri)
    {
        EnsureClosures();
        return _superClasses!.TryGetValue(classIri, out var set) ? set : EmptySet;
    }

    /// <summary>Classes that are (transitively) subclasses of <paramref name="classIri"/>.</summary>
    public IReadOnlySet<string> SubClassesOf(string classIri)
    {
        EnsureClosures();
        return _subClasses!.TryGetValue(classIri, out var set) ? set : EmptySet;
    }

    public IReadOnlySet<string> SuperPropertiesOf(string propertyIri)
    {
        EnsureClosures();
        return _superProperties!.TryGetValue(propertyIri, out var set) ? set : EmptySet;
    }

    public IReadOnlySet<string> SubPropertiesOf(string propertyIri)
    {
        EnsureClosures();
        return _subProperties!.TryGetValue(propertyIri, out var set) ? set : EmptySet;
    }

    /// <summary>rdfs:range values of a property.</summary>
    public IReadOnlyList<string> RangeOf(string propertyIri) => IriObjects(propertyIri, RdfsRange).ToList();

    /// <summary>rdfs:domain values of a property.</summary>
    public IReadOnlyList<string> DomainOf(string propertyIri) => IriObjects(propertyIri, RdfsDomain).ToList();

    /// <summary>Subjects typed with the class itself or one of its subclasses.</summary>
    public IEnumerable<string> IndividualsOfClass(string classIri)
    {
        var classes = new HashSet<string>(SubClassesOf(classIri), StringComparer.Ordinal) { classIri };
        foreach (var (subject, predicates) in _spo)
            if (predicates.TryGetValue(RdfType, out var types) && types.Any(t => classes.Contains(t.Value)))
                yield return subject;
    }

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    private void EnsureClosures()
    {
        if (!_closureDirty) return;
        _superClasses = BuildClosure(RdfsSubClassOf);
        _subClasses = Reverse(_superClasses);
        _superProperties = BuildClosure(RdfsSubPropertyOf);
        _subProperties = Reverse(_superProperties);
        _closureDirty = false;
    }

    private Dictionary<string, HashSet<string>> BuildClosure(string predicate)
    {
        var direct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (subject, predicates) in _spo)
        {
            if (!predicates.TryGetValue(predicate, out var objects)) continue;
            foreach (var obj in objects)
            {
                if (obj.IsLiteral || obj.IsBlank) continue;
                if (!direct.TryGetValue(subject, out var set))
                    direct[subject] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(obj.Value);
            }
        }

        var closure = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (start, directSupers) in direct)
        {
            var all = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(directSupers);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!all.Add(current)) continue;
                if (direct.TryGetValue(current, out var next))
                    foreach (var n in next) stack.Push(n);
            }
            all.Remove(start);
            closure[start] = all;
        }
        return closure;
    }

    private static Dictionary<string, HashSet<string>> Reverse(Dictionary<string, HashSet<string>> closure)
    {
        var reverse = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (subject, supers) in closure)
            foreach (var super in supers)
            {
                if (!reverse.TryGetValue(super, out var subs))
                    reverse[super] = subs = new HashSet<string>(StringComparer.Ordinal);
                subs.Add(subject);
            }
        return reverse;
    }

    private static string LocalName(string iri)
    {
        var cut = Math.Max(iri.LastIndexOf('#'), iri.LastIndexOf('/'));
        return cut >= 0 && cut < iri.Length - 1 ? iri[(cut + 1)..] : iri;
    }
}
