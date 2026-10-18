using System.Diagnostics;
using Knx;
using Knx.Owl;

// Self-check for the ontology layer: Turtle subset, graph queries, DPT index,
// KIM mapping and semantic checks. Run: dotnet run --project tests/Knx.Owl.Tests
var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
    if (!ok) failures.Add(name + (detail is null ? "" : ": " + detail));
}
void PassSkipped(string name) => Console.WriteLine("  PASS-SKIPPED " + name);
int PredicateCount(Ontology graph, string predicate)
    => graph.Subjects.Sum(s => graph.Objects(s, predicate).Count);

const string Ex = "http://example.org/";
const string Rdfs = "http://www.w3.org/2000/01/rdf-schema#";
const string Xsd = "http://www.w3.org/2001/XMLSchema#";

// --- Mini-Turtle: alle vom Parser zugesicherten Konstrukte ---
var mini = "@prefix ex: <" + Ex + "> .\n"
    + "@prefix rdfs: <" + Rdfs + "> .\n"
    + "ex:Child a ex:Class ;\n"
    + "    rdfs:label \"Kind\"@de ;\n"
    + "    rdfs:subClassOf ex:Parent ;\n"
    + "    ex:count 2 ;\n"
    + "    ex:ratio 1.5 ;\n"
    + "    ex:ok true ;\n"
    + "    ex:note \"\"\"Zeile1\nZeile2\"\"\" .\n"
    + "ex:Parent rdfs:subClassOf ex:Top ; ex:child ex:Child .\n"
    + "[] a ex:Thing ; ex:value [ ex:inner \"x\" ] .\n"
    + "ex:Top ex:list ( ex:a ex:b ) .\n"
    + "ex:hasChild a ex:Property ; rdfs:domain ex:Parent ; rdfs:range ex:Child ; rdfs:label \"hat Kind\"@de .\n"
    + "ex:Child ex:quote \"a \\\"b\\\"\" ; ex:hash \"a # b\" .\n"
    + "@prefix knx: <" + SemanticExport.KnxNamespace + "> .\n"
    + "@prefix dct: <http://purl.org/dc/terms/> .\n"
    + "knx:switch a knx:DatapointType ; rdfs:label \"Switch\"@en ; dct:subject \"1.001\", \"1.1\" ;\n"
    + "    knx:dptMajorNumber 1 ; knx:dptMinorNumber 1 ; knx:dptBitSizeMax 1 .\n"
    + "knx:valueTemp a knx:DatapointType ; rdfs:label \"Value Temp\"@en ; dct:subject \"9.001\" ;\n"
    + "    knx:dptMajorNumber 9 ; knx:dptMinorNumber 1 ; knx:dptBitSizeMax 16 .\n";

var miniGraph = new Ontology();
var parser = new TurtleParser();
Exception? parserError = null;
try { parser.Parse(new StringReader(mini), miniGraph.Add); }
catch (Exception ex) { parserError = ex; }

Check("Parser: Mini-Turtle ohne Ausnahme", parserError is null, parserError?.Message);
Check("Parser: Triple gezählt", parser.TripleCount >= 15, parser.TripleCount.ToString());
Check("Parser: Blank Nodes", parser.BlankNodeCount >= 2, parser.BlankNodeCount.ToString());
Check("Parser: Collection übersprungen", parser.CollectionCount == 1, parser.CollectionCount.ToString());
Check("Parser: Collection-Warnung", parser.Warnings.Any(w => w.Contains("collection")), string.Join("; ", parser.Warnings));
Check("Parser: @de-Literal", miniGraph.Value(Ex + "Child", Rdfs + "label") == "Kind");
Check("Parser: Sprachmarke", miniGraph.Objects(Ex + "Child", Rdfs + "label").Any(t => t.Language == "de"));
Check("Parser: Integer-Datentyp", miniGraph.Objects(Ex + "Child", Ex + "count").Any(t => t.Datatype == Xsd + "integer"));
Check("Parser: Boolean", miniGraph.Value(Ex + "Child", Ex + "ok") == "true");
Check("Parser: escaptes Anführungszeichen", miniGraph.Value(Ex + "Child", Ex + "quote") == "a \"b\"",
    miniGraph.Value(Ex + "Child", Ex + "quote"));
Check("Parser: Raute im Literal", miniGraph.Value(Ex + "Child", Ex + "hash") == "a # b",
    miniGraph.Value(Ex + "Child", Ex + "hash"));
Check("Parser: mehrzeiliges Literal", miniGraph.Value(Ex + "Child", Ex + "note")?.Contains("Zeile1\nZeile2") == true,
    miniGraph.Value(Ex + "Child", Ex + "note"));
var thing = miniGraph.IndividualsOfClass(Ex + "Thing").FirstOrDefault();
var nested = thing is null ? null : miniGraph.IriObjects(thing, Ex + "value").FirstOrDefault();
Check("Parser: verschachtelter Blank Node", nested is not null && miniGraph.Value(nested, Ex + "inner") == "x", nested);

// --- Graph-Abfragen und transitive Hülle ---
Check("Ontologie: Label", miniGraph.Label(Ex + "Child") == "Kind", miniGraph.Label(Ex + "Child"));
Check("Ontologie: Superklassen (transitiv)", miniGraph.SuperClassesOf(Ex + "Child").Contains(Ex + "Top"),
    string.Join(", ", miniGraph.SuperClassesOf(Ex + "Child")));
Check("Ontologie: Superklassen (direkt)", miniGraph.SuperClassesOf(Ex + "Child").Contains(Ex + "Parent"));
Check("Ontologie: Subklassen (transitiv)", miniGraph.SubClassesOf(Ex + "Top").Contains(Ex + "Child"));
Check("Ontologie: Domain", miniGraph.DomainOf(Ex + "hasChild").Contains(Ex + "Parent"));
Check("Ontologie: Range", miniGraph.RangeOf(Ex + "hasChild").Contains(Ex + "Child"));
Check("Ontologie: Individuen der Klasse", miniGraph.IndividualsOfClass(Ex + "Class").Contains(Ex + "Child"));
Check("Ontologie: kein Literal als IRI-Objekt",
    !miniGraph.IriObjects(Ex + "Child", Rdfs + "label").Any());

// --- SPARQL-Direktive PREFIX (ohne Punkt) ---
var sparqlGraph = new Ontology();
var sparqlParser = new TurtleParser();
sparqlParser.Parse(new StringReader("PREFIX ex: <http://example.org/>\nex:A ex:b ex:c .\n"), sparqlGraph.Add);
Check("Parser: PREFIX-Direktive", sparqlGraph.Has(Ex + "A", Ex + "b", Ex + "c"));

// --- DPT-Index ---
var miniDpts = new DatapointTypes(miniGraph);
var sw = miniDpts.Find("1.001");
Check("DPT: 1.001 gefunden", sw is not null, "null");
Check("DPT: 1.001 Name", sw?.Name == "Switch", sw?.Name);
Check("DPT: 1.001 Bitgröße", sw?.BitSize == 1, sw?.BitSize?.ToString());
Check("DPT: DPST-1-1", miniDpts.Find("DPST-1-1")?.Code == "1.001");
Check("DPT: Major/Minor", miniDpts.Find(1, 1)?.Code == "1.001");
Check("DPT: 9.001 zwei Byte", miniDpts.Find("9.001")?.ByteSize == 2, miniDpts.Find("9.001")?.ByteSize?.ToString());
Check("DPT: unbekannt", miniDpts.Find("99.999") is null);
Check("DPT: Normalisierung 1.1", DatapointTypes.NormalizeCode("1.1") == "1.001");
Check("DPT: Normalisierung 1.x", DatapointTypes.NormalizeCode("DPST-1-x") == "1.x");

// --- KIM-Mapping gegen die Mini-Ontologie ---
Project MakeProject(string datapointType, string product = "M-00FA_A-0001")
{
    var project = new Project { Id = "P-KIM", Name = "KIM-Test" };
    var installation = new Installation { Id = "I-KIM", Name = "Haus", Address = new IndividualAddress(0, 0, 0) };
    var area = new Area { Id = "A-1", Address = 1 };
    var line = new Line { Id = "L-1", Address = 1 };
    var device = new DeviceInstance
    {
        Id = "D-1",
        ProductRefId = product,
        Address = new IndividualAddress(1, 0, 1),
        Name = "Schaltaktor",
    };
    var comObject = new ComObjectInstanceRef { Id = "CO-1", ComObjectId = "CO-1", Text = "Schalten" };
    comObject.Links.Add("GA-KIM");
    device.ComObjectRefs.Add(comObject);
    line.Devices.Add(device);
    area.Lines.Add(line);
    installation.Areas.Add(area);

    var main = new GroupRange { Id = "GR-1", Name = "Licht", RangeStart = 1, RangeEnd = 255 };
    main.Addresses.Add(new GroupAddressEntry
    {
        Id = "GA-KIM",
        Name = "Deckenlicht",
        Address = GroupAddress.ThreeLevel(1, 1, 1),
        DatapointType = datapointType,
    });
    installation.MainRanges.Add(main);
    project.Installations.Add(installation);
    return project;
}

var kim = KimGraph.Build(MakeProject("DPST-1-1"), miniGraph);
Check("KimGraph: Installation", kim.Installations.Count == 1, kim.Installations.Count.ToString());
Check("KimGraph: Gerät", kim.Devices.Count == 1);
Check("KimGraph: Individualadresse", kim.Devices.FirstOrDefault()?.Address == new IndividualAddress(1, 0, 1),
    kim.Devices.FirstOrDefault()?.IndividualAddressHex);
Check("KimGraph: Produkt", kim.Devices.FirstOrDefault()?.ProductRefId == "M-00FA_A-0001");
Check("KimGraph: Funktionspunkt", kim.FunctionPoints.Count == 1);
Check("KimGraph: Gruppenadresse numerisch", kim.FunctionPoints.FirstOrDefault()?.GroupAddressRaw == GroupAddress.ThreeLevel(1, 1, 1).Raw,
    kim.FunctionPoints.FirstOrDefault()?.GroupAddressRaw?.ToString());
Check("KimGraph: DPT aufgelöst", kim.FunctionPoints.FirstOrDefault()?.DatapointType?.Name == "Switch",
    kim.FunctionPoints.FirstOrDefault()?.DatapointTypeCode);
Check("KimGraph: hasPoint-Verknüpfung",
    kim.PointLinks.Count == 1 && kim.FunctionPoints.Any(p => p.Subject == kim.PointLinks[0].Target));

// --- KimCheck: unbekannter DPT und fehlendes Produkt ---
var unknownFindings = KimCheck.Run(MakeProject("DPST-99-999"), miniGraph);
Check("KimCheck: unbekannter DPT K001",
    unknownFindings.Any(f => f.Code == "K001" && f.Message.Contains("99.999")),
    string.Join("; ", unknownFindings.Select(f => f.Code + " " + f.Message)));
Check("KimCheck: bekannter DPT ohne K001",
    !KimCheck.Run(MakeProject("DPST-1-1"), miniGraph).Any(f => f.Code == "K001"));
Check("KimCheck: fehlendes Produkt K002",
    KimCheck.Run(MakeProject("DPST-1-1", ""), miniGraph).Any(f => f.Code == "K002"));
Check("KimCheck: Finding-Shape", unknownFindings.All(f => f is CheckFinding { Severity: CheckSeverity.Error }));

// --- Echte ETS6-Ontologie, wenn installiert ---
var ontologyPath = @"C:\Program Files (x86)\ETS6\ontology.ttl";
if (!File.Exists(ontologyPath))
{
    PassSkipped("Ontologie: echte ontology.ttl nicht gefunden (" + ontologyPath + ")");
}
else
{
    var loadWatch = Stopwatch.StartNew();
    Ontology? ontology = null;
    Exception? loadError = null;
    try { ontology = Ontology.Load(ontologyPath); }
    catch (Exception ex) { loadError = ex; }
    loadWatch.Stop();

    Check("Ontologie: 3,2 MB Datei ohne Ausnahme geparst", loadError is null, loadError?.Message);
    if (ontology is not null)
    {
        Console.WriteLine($"  Ontologie: {ontology.TripleCount} Triples, "
            + $"{ontology.CollectionsSkipped} Collections übersprungen, {loadWatch.ElapsedMilliseconds} ms");
        Check("Ontologie: Triples geladen", ontology.TripleCount > 50_000, ontology.TripleCount.ToString());
        Check("Ontologie: Collections gezählt", ontology.CollectionsSkipped > 0, ontology.CollectionsSkipped.ToString());
        Console.WriteLine($"  Prädikate: rdf:type={PredicateCount(ontology, Ontology.RdfType)}, "
            + $"rdfs:subClassOf={PredicateCount(ontology, Ontology.RdfsSubClassOf)}, "
            + $"rdfs:subPropertyOf={PredicateCount(ontology, Ontology.RdfsSubPropertyOf)}, "
            + $"dct:subject={PredicateCount(ontology, Ontology.DctSubject)}");

        var knxNs = SemanticExport.KnxNamespace;
        var owlClass = Ontology.OwlNamespace + "Class";
        Check("Ontologie: knx:DatapointType ist owl:Class",
            ontology.Has(knxNs + "DatapointType", Ontology.RdfType, owlClass));
        Check("Ontologie: knx:FunctionPoint existiert",
            ontology.Has(knxNs + "FunctionPoint", Ontology.RdfType, owlClass));
        Check("Ontologie: knx:FunctionPoint ⊑ core:Aspect",
            ontology.SuperClassesOf(knxNs + "FunctionPoint").Contains(SemanticExport.CoreNamespace + "Aspect"),
            string.Join(", ", ontology.SuperClassesOf(knxNs + "FunctionPoint").Take(5)));

        var realDpts = new DatapointTypes(ontology);
        Console.WriteLine($"  DPT-Individuen: {realDpts.All.Count}");
        Check("Ontologie: >= 400 DPT-Individuen", realDpts.All.Count >= 400, realDpts.All.Count.ToString());

        var switchDpt = realDpts.Find("1.001");
        Check("DPT 1.001: Name", !string.IsNullOrWhiteSpace(switchDpt?.Name), switchDpt?.Name);
        Check("DPT 1.001: Bitgröße 1", switchDpt?.BitSize == 1, switchDpt?.BitSize?.ToString());

        var tempDpt = realDpts.Find("9.001");
        Check("DPT 9.001: 2 Byte", tempDpt?.ByteSize == 2, tempDpt?.BitSize?.ToString());
        Check("DPT 9.001: Name", !string.IsNullOrWhiteSpace(tempDpt?.Name), tempDpt?.Name);

        var realUnknown = KimCheck.Run(MakeProject("DPST-99-999"), ontology);
        Check("KimCheck (echt): unbekannter DPT K001",
            realUnknown.Any(f => f.Code == "K001" && f.Message.Contains("99.999")),
            string.Join("; ", realUnknown.Select(f => f.Code + " " + f.Message)));

        var realKnown = KimCheck.Run(MakeProject("DPST-1-1"), ontology);
        Check("KimCheck (echt): DPT 1.001 bekannt",
            !realKnown.Any(f => f.Code == "K001"),
            string.Join("; ", realKnown.Select(f => f.Code + " " + f.Message)));
    }
}

Console.WriteLine(failures.Count == 0 ? "\nALLE CHECKS OK" : $"\n{failures.Count} FEHLER: {string.Join("; ", failures)}");
return failures.Count == 0 ? 0 : 1;
