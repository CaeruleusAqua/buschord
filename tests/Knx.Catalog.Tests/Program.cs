using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Knx;
using Knx.Catalog;

// Self-check for the Knxprod catalogue reader: synthetic catalogue (ZIP, loose XML,
// directory), translations, store round trip and device creation.
// Run: dotnet run --project tests/Knx.Catalog.Tests
// Opt-in timing harness: KNX_CATALOG_BENCH=<library> runs Bench.cs instead of the checks.
if (Knx.Catalog.Tests.CatalogBench.TryRun() is { } benchExit) return benchExit;

var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
    if (!ok) failures.Add(name + (detail is null ? "" : ": " + detail));
}

var root = Path.Combine(Path.GetTempPath(), "knx_catalog_selftest");
if (Directory.Exists(root)) Directory.Delete(root, true);
Directory.CreateDirectory(root);

// Synthetic product database: 2 hardware items, 2 products, nested catalog, 2 programs,
// Catalogue fixture with German and English translations.
var fixture = """
<?xml version="1.0" encoding="utf-8"?>
<KNX xmlns="http://knx.org/xml/prod/20">
  <ManufacturerData>
    <Manufacturer RefId="M-00FA">
      <Catalog>
        <CatalogSection Id="CS-1" Name="Lighting" Number="1">
          <CatalogSection Id="CS-11" Name="Switching" Number="1">
            <CatalogItem Id="CI-1" Name="Actuator 4" Number="1"
                         ProductRefId="M-00FA_H-1_P-1" Hardware2ProgramRefId="M-00FA_H-1_HP-1"/>
          </CatalogSection>
          <CatalogItem Id="CI-2" Name="Dimmer" Number="2" ProductRefId="M-00FA_H-2_P-2"/>
        </CatalogSection>
        <CatalogSection Id="CS-3" Name="Restposten" Number="5"/>
      </Catalog>
      <Hardware>
        <Hardware Id="M-00FA_H-1" Name="Actuator" SerialNumber="1" VersionNumber="1"
                  HasIndividualAddress="true" HasApplicationProgram="true">
          <Products>
            <Product Id="M-00FA_H-1_P-1" Text="Actuator 4" OrderNumber="AX-4"
                     IsRailMounted="true" VisibleDescription="Fourfold switch actuator"/>
          </Products>
          <Hardware2Programs>
            <Hardware2Program Id="M-00FA_H-1_HP-1">
              <ApplicationProgramRef RefId="M-00FA_A-1"/>
            </Hardware2Program>
          </Hardware2Programs>
        </Hardware>
        <Hardware Id="M-00FA_H-2" Name="Dimmer" SerialNumber="2" VersionNumber="1"
                  HasIndividualAddress="true" HasApplicationProgram="true">
          <Products>
            <Product Id="M-00FA_H-2_P-2" Text="Dimmer" OrderNumber="DX-1" IsRailMounted="true"/>
          </Products>
          <Hardware2Programs>
            <Hardware2Program Id="M-00FA_H-2_HP-1">
              <ApplicationProgramRef RefId="M-00FA_A-2"/>
            </Hardware2Program>
          </Hardware2Programs>
        </Hardware>
      </Hardware>
      <ApplicationPrograms>
        <ApplicationProgram Id="M-00FA_A-1" Name="Switch actuator" ApplicationNumber="008A"
                            ApplicationVersion="25">
          <Static>
            <ParameterTypes>
              <ParameterType Id="PT-1" Name="Brightness" SizeInBit="8" DataType="Number"/>
              <ParameterType Id="PT-2" Name="Mode" SizeInBit="1" DataType="Enumeration"/>
            </ParameterTypes>
            <Parameters>
              <Parameter Id="P-1" Name="Brightness" ParameterType="PT-1" Text="Brightness" Value="100"/>
              <Parameter Id="P-2" Name="Mode" ParameterType="PT-2" Text="Mode" Value="0"/>
            </Parameters>
            <ComObjectTable>
              <ComObject Id="CO-1" Name="Switch" Text="Switch" FunctionText="Switching"
                         Number="1" ObjectSize="1 Bit" DatapointType="DPST-1-1"/>
              <ComObject Id="CO-2" Name="Status" Text="Status" FunctionText="Status"
                         Number="2" ObjectSize="1 Bit" DatapointType="DPST-1-1"/>
            </ComObjectTable>
          </Static>
        </ApplicationProgram>
        <ApplicationProgram Id="M-00FA_A-2" Name="Dimmer" ApplicationNumber="008B"
                            ApplicationVersion="1">
          <Static>
            <ComObjectTable>
              <ComObject Id="CO-3" Name="Dimming" Text="Dimming" FunctionText="Dimming"
                         Number="1" ObjectSize="1 Byte"/>
            </ComObjectTable>
          </Static>
        </ApplicationProgram>
      </ApplicationPrograms>
      <Languages>
        <Language Identifier="en-US">
          <TranslationUnit RefId="M-00FA">
            <TranslationElement RefId="CS-3">
              <Translation AttributeName="Name" Text="Clearance"/>
            </TranslationElement>
            <TranslationElement RefId="M-00FA_A-1">
              <Translation AttributeName="Name" Text="Switch actuator"/>
            </TranslationElement>
          </TranslationUnit>
        </Language>
        <Language Identifier="de-DE">
          <TranslationUnit RefId="M-00FA">
            <TranslationElement RefId="M-00FA">
              <Translation AttributeName="Name" Text="Testhersteller"/>
            </TranslationElement>
            <TranslationElement RefId="CS-1">
              <Translation AttributeName="Name" Text="Beleuchtung"/>
            </TranslationElement>
            <TranslationElement RefId="CS-11">
              <Translation AttributeName="Name" Text="Schaltaktoren"/>
            </TranslationElement>
            <TranslationElement RefId="CI-1">
              <Translation AttributeName="Name" Text="Schaltaktor 4-fach (Katalog)"/>
            </TranslationElement>
            <TranslationElement RefId="M-00FA_H-1_P-1">
              <Translation AttributeName="Text" Text="Schaltaktor 4-fach"/>
            </TranslationElement>
            <TranslationElement RefId="M-00FA_H-1_P-1">
              <Translation AttributeName="VisibleDescription" Text="Vierfach-Schaltaktor"/>
            </TranslationElement>
            <TranslationElement RefId="M-00FA_A-1">
              <Translation AttributeName="Name" Text="Schaltaktor-Applikation"/>
            </TranslationElement>
            <TranslationElement RefId="CO-1">
              <Translation AttributeName="Text" Text="Schalten"/>
            </TranslationElement>
            <TranslationElement RefId="CO-1">
              <Translation AttributeName="FunctionText" Text="Schaltfunktion"/>
            </TranslationElement>
          </TranslationUnit>
        </Language>
      </Languages>
    </Manufacturer>
  </ManufacturerData>
</KNX>
""";

var archive = Path.Combine(root, "test.knxprod");
using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
{
    using (var writer = new StreamWriter(zip.CreateEntry("knxprod.xml").Open(), new UTF8Encoding(false)))
        writer.Write(fixture);
    using (var image = zip.CreateEntry("M-00FA/preview.png").Open())
        image.Write([0x89, (byte)'P', (byte)'N', (byte)'G']);
}

var catalog = KnxProductCatalog.Open(archive);

// --- manufacturer and products ---
Check("Katalog-Name", catalog.Name == "test", catalog.Name);
Check("Hersteller-Id", catalog.Manufacturer?.Id == "M-00FA", catalog.Manufacturer?.Id);
Check("Hersteller-Name (de)", catalog.Manufacturer?.Name == "Testhersteller", catalog.Manufacturer?.Name);
Check("Produkte", catalog.Products.Count == 2, catalog.Products.Count.ToString());

var product1 = catalog.FindProduct("M-00FA_H-1_P-1");
Check("FindProduct", product1 is not null);
Check("Produkt-Name (de vor Basistext)", product1?.Name == "Schaltaktor 4-fach", product1?.Name);
Check("Produkt-Bestellnummer", product1?.OrderNumber == "AX-4", product1?.OrderNumber);
Check("Produkt-HardwareRefId", product1?.HardwareRefId == "M-00FA_H-1", product1?.HardwareRefId);
Check("Produkt-Beschreibung (de)", product1?.Description == "Vierfach-Schaltaktor", product1?.Description);
Check("Produkt-ProgramRefIds", product1?.ProgramRefIds.SequenceEqual(["M-00FA_A-1"]) == true,
    string.Join(",", product1?.ProgramRefIds ?? []));
Check("Produkt-CatalogPath", product1?.CatalogPaths.SequenceEqual(["Beleuchtung/Schaltaktoren"]) == true,
    string.Join(",", product1?.CatalogPaths ?? []));
Check("FindProduct unbekannt", catalog.FindProduct("M-9999") is null);

var product2 = catalog.FindProduct("M-00FA_H-2_P-2");
Check("Produkt 2 Basistext", product2?.Name == "Dimmer", product2?.Name);
Check("Produkt 2 Bestellnummer", product2?.OrderNumber == "DX-1", product2?.OrderNumber);
Check("Produkt 2 CatalogPath", product2?.CatalogPaths.SequenceEqual(["Beleuchtung"]) == true,
    string.Join(",", product2?.CatalogPaths ?? []));

// --- catalog tree ---
Check("Katalog-Wurzeln", catalog.CatalogSections.Count == 2, catalog.CatalogSections.Count.ToString());
var section = catalog.CatalogSections[0];
Check("Bereich-Name (de)", section.Name == "Beleuchtung", section.Name);
Check("Bereich-Nummer", section.Number == 1, section.Number.ToString());
Check("Unterbereich", section.Sections.Count == 1 && section.Sections[0].Name == "Schaltaktoren",
    section.Sections.FirstOrDefault()?.Name);
Check("Unterbereich-Items", section.Sections[0].Items.Count == 1);
Check("Item-Name (de)", section.Sections[0].Items[0].Name == "Schaltaktor 4-fach (Katalog)",
    section.Sections[0].Items[0].Name);
Check("Item-ProductRefId", section.Sections[0].Items[0].ProductRefId == "M-00FA_H-1_P-1");
Check("Item-Hardware2ProgramRefId", section.Sections[0].Items[0].Hardware2ProgramRefId == "M-00FA_H-1_HP-1");
Check("Item auf Wurzelebene", section.Items.Count == 1 && section.Items[0].ProductRefId == "M-00FA_H-2_P-2");
Check("Rueckfall auf en-US", catalog.CatalogSections[1].Name == "Clearance", catalog.CatalogSections[1].Name);

// --- application programs ---
Check("Applikationen", catalog.Programs.Count == 2, catalog.Programs.Count.ToString());
var program1 = catalog.FindProgram("M-00FA_A-1");
Check("FindProgram", program1 is not null);
Check("Applikations-Name (de)", program1?.Name == "Schaltaktor-Applikation", program1?.Name);
Check("Applikationsnummer", program1?.ApplicationNumber == "008A", program1?.ApplicationNumber);
Check("Applikationsversion", program1?.ApplicationVersion == "25", program1?.ApplicationVersion);
Check("ComObjects", program1?.ComObjects.Count == 2, program1?.ComObjects.Count.ToString());
Check("ParameterTypes", program1?.ParameterTypes.Count == 2, program1?.ParameterTypes.Count.ToString());
Check("ParameterInstances", program1?.ParameterInstances.Count == 2,
    program1?.ParameterInstances.Count.ToString());
Check("Parameter-Name (Basistext)", program1?.ParameterInstances[0].Name == "Brightness",
    program1?.ParameterInstances[0].Name);
Check("ComObject-Funktionstext (de)",
    program1?.ComObjects[0].Name == "Schalten", program1?.ComObjects[0].Name);
Check("ComObject-DPT-Metadaten",
    program1?.ComObjects[0].DatapointType == "DPST-1-1" && program1.ComObjects[0].SizeInBit > 0,
    program1?.ComObjects[0].DatapointType);
Check("FindProgram ueber Hardware2Program", ReferenceEquals(catalog.FindProgram("M-00FA_H-1_HP-1"), program1));
Check("FindProgramFor ueber CatalogItem", ReferenceEquals(catalog.FindProgramFor(product1!), program1));
Check("FindProgramFor Produkt 2",
    ReferenceEquals(catalog.FindProgramFor(product2!), catalog.FindProgram("M-00FA_A-2")));

// --- images ---
Check("Bilder (nur Namen)", catalog.Images.SequenceEqual(["M-00FA/preview.png"]),
    string.Join(",", catalog.Images));

// --- loose XML and directory ---
var loose = Path.Combine(root, "loose");
Directory.CreateDirectory(loose);
File.WriteAllText(Path.Combine(loose, "knxprod.xml"), fixture, new UTF8Encoding(false));
var fromDirectory = KnxProductCatalog.Open(loose);
Check("Verzeichnis mit knxprod.xml", fromDirectory.Products.Count == 2 && fromDirectory.Name == "knxprod",
    fromDirectory.Name);
Check("Loses knxprod.xml", KnxProductCatalog.Open(Path.Combine(loose, "knxprod.xml")).Products.Count == 2);

var noCatalogue = Path.Combine(root, "kein-katalog.xml");
File.WriteAllText(noCatalogue, """<?xml version="1.0"?><KNX xmlns="http://knx.org/xml/prod/20"><Nothing/></KNX>""");
try
{
    KnxProductCatalog.Open(noCatalogue);
    Check("Ohne Manufacturer wirft", false, "kein Fehler");
}
catch (InvalidDataException)
{
    Check("Ohne Manufacturer wirft", true);
}

// --- DCA/plug-in metadata (ApplicationProgram/Static/Extension, ParameterRef@Plugin) ---
var dcaFixture = """
<?xml version="1.0" encoding="utf-8"?>
<KNX xmlns="http://knx.org/xml/prod/20">
  <ManufacturerData>
    <Manufacturer RefId="M-00FB">
      <Hardware>
        <Hardware Id="M-00FB_H-1" Name="Panel" SerialNumber="1" VersionNumber="1"
                  HasIndividualAddress="true" HasApplicationProgram="true">
          <Products><Product Id="M-00FB_H-1_P-1" Text="DCA panel" OrderNumber="DCA-1"/></Products>
          <Hardware2Programs>
            <Hardware2Program Id="M-00FB_H-1_HP-1"><ApplicationProgramRef RefId="M-00FB_A-DCA"/></Hardware2Program>
          </Hardware2Programs>
        </Hardware>
      </Hardware>
      <ApplicationPrograms>
        <ApplicationProgram Id="M-00FB_A-DCA" Name="DCA panel" ApplicationNumber="0001" ApplicationVersion="1">
          <Static>
            <Extension EtsUiPlugin="{11111111-1111-1111-1111-111111111111}"
                       EtsDownloadPlugin="Vendor.Download, Vendor, Version=1.0.0.0"
                       RequiresExternalSoftware="1">
              <Baggage RefId="M-00FB_BG--panel.2Edll"/>
            </Extension>
            <ParameterTypes>
              <ParameterType Id="PT-1" Name="Brightness" SizeInBit="8" DataType="Number"/>
            </ParameterTypes>
            <Parameters>
              <Parameter Id="P-1" Name="Brightness" ParameterType="PT-1" Value="50"/>
            </Parameters>
            <ParameterRefs>
              <ParameterRef Id="PR-1" RefId="P-1" Plugin="Vendor.ParameterEditor, Vendor"/>
            </ParameterRefs>
          </Static>
        </ApplicationProgram>
        <ApplicationProgram Id="M-00FB_A-STATIC" Name="Static UI" ApplicationNumber="0002" ApplicationVersion="1">
          <Static>
            <Extension EtsUiPlugin="" RequiresExternalSoftware="false"/>
            <Parameters>
              <Parameter Id="P-2" Name="Mode" ParameterType="PT-1" Value="0"/>
            </Parameters>
          </Static>
        </ApplicationProgram>
        <ApplicationProgram Id="M-00FB_A-PLAIN" Name="Plain" ApplicationNumber="0003" ApplicationVersion="1"
                            SchemaVersion="21" Broken="true" RegistrationStatus="Registered"
                            MinEtsVersion="6.0" MaskVersion="MV-0705" DownloadInfoIncomplete="true">
          <Static>
            <Parameters>
              <Parameter Id="P-3" Name="Level" ParameterType="PT-1" Value="1"/>
            </Parameters>
          </Static>
        </ApplicationProgram>
      </ApplicationPrograms>
    </Manufacturer>
  </ManufacturerData>
</KNX>
""";
var dcaPath = Path.Combine(root, "dca.knxprod");
File.WriteAllText(dcaPath, dcaFixture, new UTF8Encoding(false));
var dcaCatalog = KnxProductCatalog.Open(dcaPath);
var dcaProgram = dcaCatalog.FindProgram("M-00FB_A-DCA")!;
Check("DCA: Extensions roh", dcaProgram.Extensions.SequenceEqual(["EtsUiPlugin", "EtsDownloadPlugin"]),
    string.Join(",", dcaProgram.Extensions));
Check("DCA: RequiresDca", dcaProgram.RequiresDca);
Check("DCA: RequiresExternalSoftware", dcaProgram.RequiresExternalSoftware);
Check("DCA: nicht statisch editierbar", !dcaProgram.IsStaticallyEditable);
var pluginInstance = dcaProgram.ParameterInstances.Single(i => i.Id == "PR-1");
Check("DCA: PluginId am ParameterRef", dcaProgram.PluginIdFor(pluginInstance) == "Vendor.ParameterEditor, Vendor",
    dcaProgram.PluginIdFor(pluginInstance));
Check("DCA: PluginId am ParameterReference",
    dcaProgram.PluginIdFor(dcaProgram.ParameterReferences.Single()) == "Vendor.ParameterEditor, Vendor");
Check("DCA: PluginId direkt", dcaProgram.PluginIdFor("PR-1") == "Vendor.ParameterEditor, Vendor" &&
    dcaProgram.PluginIdFor("P-1") == "Vendor.ParameterEditor, Vendor");

var staticProgram = dcaCatalog.FindProgram("M-00FB_A-STATIC")!;
Check("Leeres EtsUiPlugin zaehlt nicht", staticProgram.Extensions.Count == 0,
    string.Join(",", staticProgram.Extensions));
Check("Ohne DCA statisch editierbar", staticProgram.IsStaticallyEditable && !staticProgram.RequiresDca);
Check("RequiresExternalSoftware=false", !staticProgram.RequiresExternalSoftware);
var plainProgram = dcaCatalog.FindProgram("M-00FB_A-PLAIN")!;
Check("Ohne Extension editierbar", plainProgram.Extensions.Count == 0 && plainProgram.IsStaticallyEditable);
Check("Broken/incomplete applications are blocked", plainProgram.Broken && plainProgram.DownloadInfoIncomplete && !plainProgram.IsEditable);
Check("Application metadata is retained", plainProgram.SchemaVersion == "21" && plainProgram.RegistrationStatus == "Registered"
    && plainProgram.MinEtsVersion == "6.0" && plainProgram.MaskVersion == "MV-0705");

// --- store round trip ---
var library = Path.Combine(root, "library");
var store = new CatalogStore(library);
var imported = store.Import(archive);
Check("Import kopiert", File.Exists(imported.Path), imported.Path);
Check("Import report summarizes products/programs and gating", store.LastImportReport is { } report
    && report.Products > 0 && report.Programs > 0 && report.File == imported.Path
    && report.HasDcaProgram == imported.Programs.Any(p => p.RequiresDca)
    && report.HasBrokenProgram == imported.Programs.Any(p => p.Broken || p.DownloadInfoIncomplete));
Check("Import-Dateiname (Hersteller + stabile Identität)",
    Path.GetFileName(imported.Path).StartsWith("M-00FA_", StringComparison.Ordinal) &&
    Path.GetFileName(imported.Path).EndsWith(".knxprod", StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(imported.Path).Length == "M-00FA_".Length + 64 + ".knxprod".Length,
    Path.GetFileName(imported.Path));
Check("Import im Bibliotheksordner", Path.GetFullPath(imported.Path).StartsWith(Path.GetFullPath(library)),
    imported.Path);
var reimported = store.Import(archive);
Check("Re-Import ueberschreibt", Path.GetFileName(reimported.Path) == Path.GetFileName(imported.Path));
var wrapperPath = Path.Combine(root, "vendor-wrapper.zip");
using (var wrapperZip = System.IO.Compression.ZipFile.Open(wrapperPath, System.IO.Compression.ZipArchiveMode.Create))
    wrapperZip.CreateEntryFromFile(archive, "inner.knxprod");
var wrapped = store.Import(wrapperPath);
Check("Vendor-ZIP landet auf demselben Bibliothekseintrag",
    Path.GetFileName(wrapped.Path) == Path.GetFileName(imported.Path), Path.GetFileName(wrapped.Path));

var loaded = store.LoadAll();
Check("LoadAll findet Katalog", loaded.Count == 1 && loaded[0].Products.Count == 2, loaded.Count.ToString());
Check("LoadAll ohne Fehler", store.LoadErrors.Count == 0, string.Join("; ", store.LoadErrors));

File.WriteAllText(Path.Combine(library, "broken.knxprod"), "kein XML");
File.WriteAllText(Path.Combine(library, "leer.knxprod"), "<KNX><Nothing/></KNX>");
var withBroken = store.LoadAll();
Check("Defekte Datei bricht LoadAll nicht ab", withBroken.Count == 1, withBroken.Count.ToString());
Check("LoadErrors gesammelt", store.LoadErrors.Count == 2, store.LoadErrors.Count.ToString());
Check("LoadError nennt Datei",
    store.LoadErrors.Any(e => Path.GetFileName(e.File) == "broken.knxprod"),
    string.Join("; ", store.LoadErrors.Select(e => Path.GetFileName(e.File))));
Check("LoadError nennt Ursache",
    store.LoadErrors.Any(e => e.Message.Contains("Manufacturer", StringComparison.OrdinalIgnoreCase)),
    string.Join("; ", store.LoadErrors.Select(e => e.Message)));

Check("Remove loescht Datei", store.Remove(imported.Path) && !File.Exists(imported.Path));
Check("Remove unbekannte Datei", !store.Remove(imported.Path));

// Removal is bounded to direct children of the library directory.
var outsideCopy = Path.Combine(root, "outside-copy.knxprod");
File.Copy(archive, outsideCopy);
Check("Remove ausserhalb der Bibliothek abgelehnt", !store.Remove(outsideCopy) && File.Exists(outsideCopy));
var nestedDirectory = Path.Combine(library, "nested");
Directory.CreateDirectory(nestedDirectory);
var nestedCopy = Path.Combine(nestedDirectory, "nested.knxprod");
File.Copy(archive, nestedCopy);
Check("Remove in Unterordner abgelehnt", !store.Remove(nestedCopy) && File.Exists(nestedCopy));
Check("Remove Verzeichnis abgelehnt", !store.Remove(library));

// Import report carries the gating flags of the imported catalogue.
var gatedStore = new CatalogStore(Path.Combine(root, "gated-library"));
var gatedImport = gatedStore.Import(dcaPath);
Check("Importbericht flaggt DCA, defekt und Fremdsoftware",
    gatedStore.LastImportReport is { } gatedReport
    && gatedReport.Products == 1 && gatedReport.Programs == 3
    && gatedReport.HasDcaProgram && gatedReport.HasBrokenProgram && gatedReport.HasExternalSoftware
    && gatedReport.File == gatedImport.Path,
    gatedStore.LastImportReport?.ToString());

// --- device creation ---
var device = CatalogInstaller.CreateDevice(product1!, program1, new IndividualAddress(1, 2, 3), "Aktor");
Check("Device-Adresse", device.Address == new IndividualAddress(1, 2, 3), device.Address.ToString());
Check("Device-Name", device.Name == "Aktor");
Check("Device-ProductRefId", device.ProductRefId == "M-00FA_H-1_P-1", device.ProductRefId);
Check("Device-Hardware2ProgramRefId", device.Hardware2ProgramRefId == "M-00FA_H-1_HP-1",
    device.Hardware2ProgramRefId);
Check("Device-ComObjectRefs", device.ComObjectRefs.Count == 2, device.ComObjectRefs.Count.ToString());
Check("Device-ComObject-Text (de)", device.ComObjectRefs[0].Text == "Schalten", device.ComObjectRefs[0].Text);
Check("Device-ComObject-FunctionText (de)",
    device.ComObjectRefs[0].FunctionText == "Schaltfunktion", device.ComObjectRefs[0].FunctionText);
Check("Device ohne Links", device.ComObjectRefs.All(r => r.Links.Count == 0));
var withoutProgram = CatalogInstaller.CreateDevice(product2!, null, new IndividualAddress(1, 2, 4), "Dimmer");
Check("Device ohne Applikation", withoutProgram.ComObjectRefs.Count == 0 && !string.IsNullOrEmpty(withoutProgram.ProductRefId));

// --- lazy parameter model (monolithic) ---
var lazyMonolithic = KnxProductCatalog.Open(archive);
Check("Monolithische Metadaten ohne Parameter",
    lazyMonolithic.Programs.Count == 2 && lazyMonolithic.MetadataComplete && lazyMonolithic.LoadedProgramModels == 0,
    $"programs={lazyMonolithic.Programs.Count} models={lazyMonolithic.LoadedProgramModels}");
Check("Monolithische Parameter erst bei Bedarf",
    lazyMonolithic.FindProgram("M-00FA_A-1") is { ParameterInstances.Count: 2 }
    && lazyMonolithic.LoadedProgramModels == 1);

// --- persistent metadata index: cold/warm, invalidation, corruption ---
var indexRoot = Path.Combine(root, "index");
var indexLibrary = Path.Combine(indexRoot, "library");
var indexCache = Path.Combine(indexRoot, "cache");
Directory.CreateDirectory(indexLibrary);
var indexFile = Path.Combine(indexLibrary, "indexed.knxprod");
File.Copy(archive, indexFile);

var coldStore = new CatalogStore(indexLibrary, indexCache);
var cold = coldStore.LoadAll();
Check("Index: kalter Lauf parst XML",
    coldStore.LastIndexStatistics is { IndexHits: 0, IndexMisses: 1, XmlCatalogFiles: 1 } && cold.Count == 1,
    coldStore.LastIndexStatistics?.ToString());
Check("Index: Cache wird geschrieben", File.Exists(coldStore.Index.IndexPathFor(indexFile)));

var warmStore = new CatalogStore(indexLibrary, indexCache);
var warm = warmStore.LoadAll();
// Memory bound: a warm load must not build the communication objects of programs nobody opened.
var packedField = typeof(CatalogApplicationProgram).GetField("_packedComObjects",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
var objectsField = typeof(CatalogApplicationProgram).GetField("_comObjects",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
Check("Index: COM-Objekte bleiben bis zum Zugriff gepackt",
    warm.Count == 1 && warm[0].Programs.Count == 2
    && warm[0].Programs.All(program => packedField?.GetValue(program) is string { Length: > 0 })
    && warm[0].Programs.All(program => objectsField?.GetValue(program) is null)
    && warm[0].Programs.All(program => program.SearchText.Length > 0),
    $"packed={warm[0].Programs.Count(p => packedField?.GetValue(p) is string)} "
    + $"materialised={warm[0].Programs.Count(p => objectsField?.GetValue(p) is not null)}");
Check("Index: warmer Lauf parst kein XML",
    warmStore.LastIndexStatistics is { IndexHits: 1, IndexMisses: 0, XmlCatalogFiles: 0, XmlProgramParts: 0 }
    && warm.Count == 1,
    warmStore.LastIndexStatistics?.ToString());
var warmProduct = warm[0].Products.Single(product => product.Id == "M-00FA_H-1_P-1");
Check("Index: Produktmetadaten aus dem Cache",
    warmProduct.Name == "Schaltaktor 4-fach" && warmProduct.OrderNumber == "AX-4"
    && warm[0].CatalogSections.Count == 2 && warm[0].Programs.Count == 2);
Check("Index: Suche aus dem Cache",
    warmProduct.SearchText.Contains("schaltaktor", StringComparison.Ordinal)
    && warm[0].Programs.Single(program => program.Id == "M-00FA_A-1").NormalizedDatapointTypes.Contains(";dpst-1-1;"));
var warmProgram = warm[0].FindProgramFor(warmProduct)!;
Check("Index: Programmparameter weiterhin lazy",
    warmProgram.ParameterInstances.Count == 2 && warmProgram.ComObjects.Count == 2
    && warm[0].LoadedProgramModels == 1);
var warmDevice = CatalogInstaller.CreateDevice(warmProduct, warmProgram, new IndividualAddress(1, 1, 5), "Warm");
Check("Index: Gerätetexte aus dem Cache",
    warmDevice.ComObjectRefs[0].Text == "Schalten"
    && warmDevice.ComObjectRefs[0].FunctionText == "Schaltfunktion",
    warmDevice.ComObjectRefs[0].Text);

File.SetLastWriteTimeUtc(indexFile, DateTime.UtcNow.AddMinutes(1));
var touchedStore = new CatalogStore(indexLibrary, indexCache);
_ = touchedStore.LoadAll();
Check("Index: Änderungszeit invalidiert",
    touchedStore.LastIndexStatistics is { IndexMisses: 1, XmlCatalogFiles: 1 },
    touchedStore.LastIndexStatistics?.ToString());

var corruptFile = Path.Combine(indexLibrary, "corrupt.knxprod");
File.Copy(archive, corruptFile);
var corruptSeed = new CatalogStore(indexLibrary, indexCache);
_ = corruptSeed.LoadAll();
var corruptIndex = corruptSeed.Index.IndexPathFor(corruptFile);
File.WriteAllText(corruptIndex, "kein gzip/json");
var corruptStore = new CatalogStore(indexLibrary, indexCache);
var rebuilt = corruptStore.LoadAll();
Check("Index: defekter Cache wird neu gebaut",
    corruptStore.LastIndexStatistics is { XmlCatalogFiles: >= 1 } && rebuilt.Count == 2,
    corruptStore.LastIndexStatistics?.ToString());

// A cache document is untrusted: a source entry pointing outside the archive must be
// discarded and the catalogue rebuilt, never opened as an arbitrary file.
var poisonFile = Path.Combine(indexLibrary, "poison.knxprod");
File.Copy(archive, poisonFile);
var poisonSeed = new CatalogStore(indexLibrary, indexCache);
_ = poisonSeed.LoadAll();
var poisonIndex = poisonSeed.Index.IndexPathFor(poisonFile);
JsonNode poison;
using (var gz = new GZipStream(File.OpenRead(poisonIndex), CompressionMode.Decompress))
    poison = JsonNode.Parse(gz)!;
var poisonPrograms = poison["Programs"]?.AsArray();
if (poisonPrograms is { Count: > 0 }) poisonPrograms[0]!["SourceEntry"] = "../../outside.xml";
using (var gz = new GZipStream(File.Create(poisonIndex), CompressionMode.Compress))
using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
    writer.Write(poison.ToJsonString());
var poisonedStore = new CatalogStore(indexLibrary, indexCache);
var poisoned = poisonedStore.LoadAll();
Check("Index: Cache-Quelle außerhalb des Archivs wird verworfen",
    poisonedStore.LastIndexStatistics is { XmlCatalogFiles: >= 1 } && poisoned.Count == 3,
    poisonedStore.LastIndexStatistics?.ToString());

var removeStore = new CatalogStore(indexLibrary, indexCache);
var removedIndex = removeStore.Index.IndexPathFor(corruptFile);
Check("Index: Entfernen löscht den Cache",
    removeStore.Remove(corruptFile) && !File.Exists(removedIndex));
if (Directory.Exists(indexLibrary))
{
    foreach (var file in Directory.GetFiles(indexLibrary)) File.Delete(file);
}

// --- split archives: metadata part, no parameter model until requested ---
var splitZipPath = Path.Combine(root, "split-lazy.knxprod");
using (var zip = ZipFile.Open(splitZipPath, ZipArchiveMode.Create))
{
    void Part(string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
    Part("M-00FA/Hardware.xml", """
        <KNX><ManufacturerData><Manufacturer RefId="M-00FA"><Hardware>
        <Hardware Id="H-SPLIT"><Products><Product Id="P-SPLIT" Text="Split device" OrderNumber="SP-1"/></Products>
        <Hardware2Programs><Hardware2Program Id="HP-SPLIT"><ApplicationProgramRef RefId="A-SPLIT"/></Hardware2Program></Hardware2Programs>
        </Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
        """);
    Part("M-00FA/Catalog.xml", """
        <KNX><ManufacturerData><Manufacturer RefId="M-00FA"><Catalog>
        <CatalogSection Id="CS-SPLIT" Name="Split section"><CatalogItem Id="CI-SPLIT" Name="Split entry"
        ProductRefId="P-SPLIT" Hardware2ProgramRefId="HP-SPLIT"/></CatalogSection>
        </Catalog></Manufacturer></ManufacturerData></KNX>
        """);
    Part("M-00FA/M-00FA_A-SPLIT.xml", """
        <KNX><ManufacturerData><Manufacturer RefId="M-00FA"><ApplicationPrograms>
        <ApplicationProgram Id="A-SPLIT" Name="Split application" ApplicationNumber="00AA" ApplicationVersion="7"
        MaskVersion="MV-0705"><Static>
        <ParameterTypes><ParameterType Id="PT-S" Name="Level" SizeInBit="8" DataType="Number"/></ParameterTypes>
        <Parameters><Parameter Id="P-S" Name="Level" ParameterType="PT-S" Text="Level" Value="1"/></Parameters>
        <ComObjectTable><ComObject Id="CO-S" Name="Switch" Text="Switch" Number="1"
        ObjectSize="1 Bit" DatapointType="DPST-1-1"/></ComObjectTable>
        </Static></ApplicationProgram></ApplicationPrograms>
        <Languages><Language Identifier="de-DE"><TranslationUnit RefId="M-00FA">
        <TranslationElement RefId="A-SPLIT"><Translation AttributeName="Name" Text="Split-Applikation"/></TranslationElement>
        </TranslationUnit></Language></Languages></Manufacturer></ManufacturerData></KNX>
        """);
}

var split = KnxProductCatalog.Open(splitZipPath);
var splitProduct = split.Products.Single();
Check("Split: Produktmetadaten ohne Programmpart",
    split.Products.Count == 1 && split.Programs.Count == 0 && !split.MetadataComplete
    && split.LoadedProgramModels == 0);
Check("Split: Suche hat vor dem Index keine Programmdaten",
    split.FindProgramSummaryFor(splitProduct) is null);
var splitParts = split.LoadProgramMetadata();
var splitSummary = split.FindProgramSummaryFor(splitProduct);
Check("Split: Metadatenpart wird gelesen",
    splitParts == 1 && split.MetadataComplete && split.Programs.Count == 1
    && splitSummary is { Name: "Split-Applikation", ApplicationNumber: "00AA", ApplicationVersion: "7", MaskVersion: "MV-0705" },
    splitSummary?.Name);
Check("Split: Suchtext enthält COM/DPT",
    splitSummary!.SearchText.Contains("switch", StringComparison.Ordinal)
    && splitSummary.NormalizedDatapointTypes.Contains(";dpst-1-1;"));
Check("Split: Parameter bleiben lazy", split.LoadedProgramModels == 0);
var splitFull = split.FindProgramFor(splitProduct)!;
Check("Split: Parameter erst beim Öffnen",
    splitFull.ParameterInstances.Count == 1 && splitFull.ParameterTypes.Count == 1
    && split.LoadedProgramModels == 1);

// Split cold/warm: the index carries the program part and its metadata.
var splitLibrary = Path.Combine(indexRoot, "split-library");
var splitCache = Path.Combine(indexRoot, "split-cache");
Directory.CreateDirectory(splitLibrary);
var splitLibraryFile = Path.Combine(splitLibrary, "split.knxprod");
File.Copy(splitZipPath, splitLibraryFile);
var splitColdStore = new CatalogStore(splitLibrary, splitCache);
var splitCold = splitColdStore.LoadAll();
Check("Split-Index: kalter Lauf liest Basis und Programmpart",
    splitColdStore.LastIndexStatistics is { XmlCatalogFiles: 1, XmlProgramParts: 1 }
    && splitCold.Single().Programs.Count == 1,
    splitColdStore.LastIndexStatistics?.ToString());
var splitWarmStore = new CatalogStore(splitLibrary, splitCache);
var splitWarm = splitWarmStore.LoadAll();
var splitWarmProgram = splitWarm.Single().FindProgramSummaryFor(splitWarm.Single().Products.Single());
Check("Split-Index: warmer Lauf parst kein XML",
    splitWarmStore.LastIndexStatistics is { IndexHits: 1, XmlCatalogFiles: 0, XmlProgramParts: 0 }
    && splitWarmProgram is { Name: "Split-Applikation" }
    && splitWarm.Single().LoadedProgramModels == 0,
    splitWarmStore.LastIndexStatistics?.ToString());
Check("Split-Index: volles Modell weiterhin aus dem Archiv",
    splitWarm.Single().FindProgramFor(splitWarm.Single().Products.Single())?.ParameterInstances.Count == 1);

// DCA/plug-in metadata survives the index; the full model re-reads the per-parameter plug-ins.
var pluginLibrary = Path.Combine(indexRoot, "plugin-library");
var pluginCache = Path.Combine(indexRoot, "plugin-cache");
Directory.CreateDirectory(pluginLibrary);
var pluginFile = Path.Combine(pluginLibrary, "dca.knxprod");
File.Copy(dcaPath, pluginFile);
var pluginCold = new CatalogStore(pluginLibrary, pluginCache);
_ = pluginCold.LoadAll();
var pluginWarmCatalog = new CatalogStore(pluginLibrary, pluginCache).LoadAll().Single();
var pluginWarmProgram = pluginWarmCatalog.FindProgram("M-00FB_A-DCA")!;
Check("Index: DCA-Metadaten aus dem Cache",
    pluginWarmProgram.RequiresDca && pluginWarmProgram.Extensions.Contains("EtsUiPlugin")
    && pluginWarmProgram.PluginIdFor("P-1") == "Vendor.ParameterEditor, Vendor",
    pluginWarmProgram.PluginIdFor("P-1"));

// A tampered cache is rejected even when name, size and mtime still match.
var hashLibrary = Path.Combine(indexRoot, "hash-library");
var hashCache = Path.Combine(indexRoot, "hash-cache");
Directory.CreateDirectory(hashLibrary);
var hashFile = Path.Combine(hashLibrary, "hashed.knxprod");
File.Copy(archive, hashFile);
var hashStore = new CatalogStore(hashLibrary, hashCache);
_ = hashStore.LoadAll();
var hashIndex = hashStore.Index.IndexPathFor(hashFile);
string tamperedJson;
using (var input = File.OpenRead(hashIndex))
using (var gzip = new GZipStream(input, CompressionMode.Decompress))
using (var reader = new StreamReader(gzip))
    tamperedJson = reader.ReadToEnd();
{
    var marker = "\"ContentHash\":\"";
    var start = tamperedJson.IndexOf(marker, StringComparison.Ordinal);
    if (start < 0) throw new Exception("cache document has no content hash");
    start += marker.Length;
    tamperedJson = tamperedJson[..start] + new string('0', 64) + tamperedJson[(start + 64)..];
}
using (var output = File.Create(hashIndex))
using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
using (var writer = new StreamWriter(gzip))
    writer.Write(tamperedJson);
var tamperedStore = new CatalogStore(hashLibrary, hashCache);
_ = tamperedStore.LoadAll();
Check("Index: Inhalt-Hash wird geprüft",
    tamperedStore.LastIndexStatistics is { IndexMisses: 1, XmlCatalogFiles: 1 },
    tamperedStore.LastIndexStatistics?.ToString());

// --- verified identity: the hash is remembered, and every change is re-verified ---
var identityRoot = Path.Combine(indexRoot, "identity");
var identityLibrary = Path.Combine(identityRoot, "library");
var identityCache = Path.Combine(identityRoot, "cache");
Directory.CreateDirectory(identityLibrary);
var identityFile = Path.Combine(identityLibrary, "identity.knxprod");
File.Copy(archive, identityFile);
var identitySeed = new CatalogStore(identityLibrary, identityCache);
_ = identitySeed.LoadAll();
var identityPath = identitySeed.Index.IdentityPathFor(identityFile);
Check("Identität: verifizierte Identität wird abgelegt", File.Exists(identityPath));
var identityStamp = File.GetLastWriteTimeUtc(identityPath);
var identityLine = File.ReadAllText(identityPath);
var identityWarm = new CatalogStore(identityLibrary, identityCache);
var identityWarmCatalogs = identityWarm.LoadAll();
Check("Identität: warmer Lauf nutzt sie ohne erneutes Lesen der Datei",
    identityWarm.LastIndexStatistics is { IndexHits: 1, IndexMisses: 0, XmlCatalogFiles: 0 }
    && File.GetLastWriteTimeUtc(identityPath) == identityStamp
    && File.ReadAllText(identityPath) == identityLine
    && identityWarmCatalogs.Single().Products.Count == 2,
    identityWarm.LastIndexStatistics?.ToString());
File.Delete(identityPath);
var identityRebuilt = new CatalogStore(identityLibrary, identityCache);
_ = identityRebuilt.LoadAll();
Check("Identität: fehlende Identität kostet nur den Hash, kein XML",
    identityRebuilt.LastIndexStatistics is { IndexHits: 1, IndexMisses: 0, XmlCatalogFiles: 0 }
    && File.Exists(identityPath),
    identityRebuilt.LastIndexStatistics?.ToString());

// Same size and same last-write time, different content: only the content check can decide.
var sameRoot = Path.Combine(indexRoot, "same");
var sameLibrary = Path.Combine(sameRoot, "library");
var sameCache = Path.Combine(sameRoot, "cache");
Directory.CreateDirectory(sameLibrary);
var sameFile = Path.Combine(sameLibrary, "knxprod.xml");
string SameSizeCatalog(string productId, char padding) => $"""
    <KNX><ManufacturerData><Manufacturer RefId="M-SAME"><Hardware><Hardware Id="H-1"><Products>
    <Product Id="{productId}" Text="Same size" OrderNumber="S-1"/>
    </Products></Hardware></Hardware></Manufacturer></ManufacturerData><!--{new string(padding, 4096)}--></KNX>
    """;
File.WriteAllText(sameFile, SameSizeCatalog("P-AAA", 'a'));
var sameCold = new CatalogStore(sameLibrary, sameCache);
var sameFirst = sameCold.LoadAll();
Check("Identität: erster Lauf liest das XML", sameFirst.Single().Products.Single().Id == "P-AAA");
var sameStamp = File.GetLastWriteTimeUtc(sameFile);
var sameLength = new FileInfo(sameFile).Length;
File.WriteAllText(sameFile, SameSizeCatalog("P-BBB", 'b'));
File.SetLastWriteTimeUtc(sameFile, sameStamp);
Check("Identität: Testdatei hat wirklich dieselbe Größe", new FileInfo(sameFile).Length == sameLength);
var sameSecond = new CatalogStore(sameLibrary, sameCache).LoadAll();
Check("Identität: gleiche Größe und Änderungszeit, anderer Inhalt wird neu gelesen",
    sameSecond.Single().Products.Single().Id == "P-BBB",
    sameSecond.Single().Products.Single().Id);

// --- packed communication objects survive the cache, including the ones without display texts ---
var packedRoot = Path.Combine(indexRoot, "packed");
var packedLibrary = Path.Combine(packedRoot, "library");
var packedCache = Path.Combine(packedRoot, "cache");
Directory.CreateDirectory(packedLibrary);
var packedFile = Path.Combine(packedLibrary, "knxprod.xml");
File.WriteAllText(packedFile, """
    <KNX><ManufacturerData><Manufacturer RefId="M-PACK"><Hardware><Hardware Id="H-1"><Products>
    <Product Id="P-PACK" Text="Packed device" OrderNumber="PK-1"/>
    </Products><Hardware2Programs><Hardware2Program Id="HP-1"><ApplicationProgramRef RefId="A-PACK"/></Hardware2Program></Hardware2Programs>
    </Hardware></Hardware>
    <ApplicationPrograms><ApplicationProgram Id="A-PACK" Name="Packed application"><Static><ComObjectTable>
    <ComObject Id="CO-TEXT" Name="With text" Text="Switch" FunctionText="Switching" Number="1" ObjectSize="1 Bit" DatapointType="DPST-1-1"/>
    <ComObject Id="CO-PLAIN" Name="Without text" Number="2" ObjectSize="2 Byte"/>
    </ComObjectTable></Static></ApplicationProgram></ApplicationPrograms>
    </Manufacturer></ManufacturerData></KNX>
    """);
_ = new CatalogStore(packedLibrary, packedCache).LoadAll();
var packedWarm = new CatalogStore(packedLibrary, packedCache).LoadAll().Single();
var packedProgram = packedWarm.FindProgram("A-PACK")!;
var packedDevice = CatalogInstaller.CreateDevice(packedWarm.Products.Single(), packedProgram,
    new IndividualAddress(1, 2, 5), "Packed");
Check("Gepackte ComObjects: Anzeigetexte überleben den Cache",
    packedDevice.ComObjectRefs.Count == 2
    && packedDevice.ComObjectRefs[0] is { Text: "Switch", FunctionText: "Switching" }
    && packedDevice.ComObjectRefs[1].Text is null && packedDevice.ComObjectRefs[1].FunctionText is null,
    string.Join(" | ", packedDevice.ComObjectRefs.Select(r => $"{r.Id}:{r.Text}/{r.FunctionText}")));
Check("Gepackte ComObjects: Nummer, Größe und DPT überleben den Cache",
    packedProgram.ComObjects[0] is { Number: 1, SizeInBit: 1, DatapointType: "DPST-1-1" }
    && packedProgram.ComObjects[1] is { Number: 2, SizeInBit: 16, DatapointType: null },
    string.Join(" | ", packedProgram.ComObjects.Select(c => $"{c.Id}:{c.Number}/{c.SizeInBit}/{c.DatapointType}")));
Check("Gepackte ComObjects: Suchtext und DPT-Index bleiben vollständig",
    packedProgram.SearchText.Contains("switch", StringComparison.Ordinal)
    && packedProgram.SearchText.Contains("without text", StringComparison.Ordinal)
    && packedProgram.NormalizedDatapointTypes.Contains(";dpst-1-1;", StringComparison.Ordinal),
    packedProgram.SearchText);

// --- cancellation: a cold build stops without leaving half-written cache entries ---
var cancelRoot = Path.Combine(indexRoot, "cancel");
var cancelLibrary = Path.Combine(cancelRoot, "library");
var cancelCache = Path.Combine(cancelRoot, "cache");
Directory.CreateDirectory(cancelLibrary);
for (var index = 0; index < 3; index++)
    File.Copy(splitZipPath, Path.Combine(cancelLibrary, $"cancel-{index}.knxprod"));
using (var cancellation = new CancellationTokenSource())
{
    var canceled = false;
    try
    {
        new CatalogStore(cancelLibrary, cancelCache).LoadAll(
            new SyncProgress<CatalogLoadProgress>(_ => cancellation.Cancel()), null, cancellation.Token);
    }
    catch (OperationCanceledException)
    {
        canceled = true;
    }
    Check("Abbruch: der kalte Lauf bricht ab", canceled);
}
Check("Abbruch: keine halb geschriebenen Cache-Dateien",
    !Directory.EnumerateFiles(cancelCache, "*.tmp").Any(),
    string.Join(",", Directory.EnumerateFiles(cancelCache, "*.tmp").Select(Path.GetFileName)));
var cancelResumed = new CatalogStore(cancelLibrary, cancelCache);
var resumed = cancelResumed.LoadAll();
Check("Abbruch: der nächste Lauf vervollständigt die Bibliothek",
    resumed.Count == 3 && resumed.All(catalog => catalog.Programs.Count == 1)
    && cancelResumed.LoadErrors.Count == 0,
    $"catalogs={resumed.Count} programs={resumed.Sum(c => c.Programs.Count)} errors={cancelResumed.LoadErrors.Count}");

// --- progress: a split archive reports its parts while they are read ---
var progressRoot = Path.Combine(indexRoot, "progress");
var progressLibrary = Path.Combine(progressRoot, "library");
var progressCache = Path.Combine(progressRoot, "cache");
Directory.CreateDirectory(progressLibrary);
File.Copy(splitZipPath, Path.Combine(progressLibrary, "progress.knxprod"));
var progressReports = new List<CatalogLoadProgress>();
var progressStore = new CatalogStore(progressLibrary, progressCache);
var progressLoaded = progressStore.LoadAll(new SyncProgress<CatalogLoadProgress>(progressReports.Add));
Check("Fortschritt: Dateien und Teile werden gemeldet",
    progressReports.Any(report => report.Loaded == 1 && report.Total == 1)
    && progressReports.Any(report => report.Parts == 1 && report.Part == 1),
    string.Join(" | ", progressReports.Select(report => $"{report.Loaded}/{report.Total} {report.Part}/{report.Parts}")));
Check("Fortschritt: Programmteile sind abgezählt",
    progressLoaded.Single().ProgramPartCount == 1 && progressLoaded.Single().MetadataComplete);

// --- hostile XML: bounded section depth, bounded entry size (no real catalogue data) ---
var deepSections = new System.Text.StringBuilder();
deepSections.Append(
    "<KNX><ManufacturerData><Manufacturer RefId=\"M-DEEP\"><Catalog>");
for (var level = 0; level < 5000; level++)
    deepSections.Append($"<CatalogSection Id=\"CS-{level}\" Name=\"Level {level}\">");
for (var level = 0; level < 5000; level++) deepSections.Append("</CatalogSection>");
deepSections.Append("</Catalog></Manufacturer></ManufacturerData></KNX>");
var deepPath = Path.Combine(root, "deep-sections.knxprod");
using (var zip = ZipFile.Open(deepPath, ZipArchiveMode.Create))
using (var writer = new StreamWriter(zip.CreateEntry("knxprod.xml").Open()))
    writer.Write(deepSections.ToString());
try
{
    KnxProductCatalog.Open(deepPath);
    Check("Tiefe Katalogbereiche werden abgelehnt", false, "kein Fehler");
}
catch (InvalidDataException)
{
    Check("Tiefe Katalogbereiche werden abgelehnt", true);
}

var oversizedPath = Path.Combine(root, "oversized-entry.knxprod");
using (var zip = ZipFile.Open(oversizedPath, ZipArchiveMode.Create))
using (var writer = new StreamWriter(zip.CreateEntry("knxprod.xml").Open()))
{
    writer.Write("<KNX><ManufacturerData><Manufacturer RefId=\"M-BIG\"><Hardware/></Manufacturer></ManufacturerData><!--");
    var chunk = new string('a', 1 << 20);
    for (var megabyte = 0; megabyte < 260; megabyte++) writer.Write(chunk);
    writer.Write("--></KNX>");
}
try
{
    KnxProductCatalog.Open(oversizedPath);
    Check("Übergroße XML-Einträge werden abgelehnt", false, "kein Fehler");
}
catch (InvalidDataException)
{
    Check("Übergroße XML-Einträge werden abgelehnt", true);
}

// --- hardware facts: BusCurrent, power supply, coupler, repeater (shape of real product databases) ---
{
    var hardwareXml = System.Xml.Linq.XDocument.Parse("""
        <?xml version="1.0" encoding="utf-8"?>
        <KNX xmlns="http://knx.org/xml/project/20"><ManufacturerData><Manufacturer RefId="M-0083">
          <Hardware Id="H-PSU" Name="Busspannungsversorgung 640mA" BusCurrent="640"
                   IsPowerSupply="true" IsChoke="true">
            <Products><Product Id="P-PSU" Text="STV-0640.02"/></Products>
            <Hardware2Programs><Hardware2Program Id="HP-PSU" MediumTypes="MT-0">
              <ApplicationProgramRef RefId="A-PSU"/></Hardware2Program></Hardware2Programs>
          </Hardware>
          <Hardware Id="H-DIM" Name="Dimmaktor" BusCurrent="2.0000000e+001" IsCoupler="1">
            <Products><Product Id="P-DIM" Text="Dimmaktor"/></Products>
            <Hardware2Programs><Hardware2Program Id="HP-DIM" MediumTypes="MT-0 MT-2">
              <ApplicationProgramRef RefId="A-DIM"/></Hardware2Program></Hardware2Programs>
          </Hardware>
          <Hardware Id="H-PL-REPEATER" Name="PL-Repeater" BusCurrent="5" IsPowerLineRepeater="true">
            <Products><Product Id="P-PL" Text="PL-Repeater"/></Products>
          </Hardware>
          <Hardware Id="H-EMPTY" Name="Ohne Produkt" BusCurrent="1"/>
        </Manufacturer></ManufacturerData></KNX>
        """);
    var facts = new Dictionary<string, CatalogHardware>();
    CatalogHardwareFacts.Read(hardwareXml, facts);

    Check("Hardware-Fakten: Programm- und Produkt-Id aufgelöst",
        facts.ContainsKey("HP-PSU") && facts.ContainsKey("P-PSU")
        && facts.ContainsKey("HP-DIM") && facts.ContainsKey("P-DIM"));
    Check("Hardware-Fakten: Spannungsversorgung 640 mA",
        facts["HP-PSU"] is { BusCurrent: 640, IsPowerSupply: true, IsChoke: true });
    Check("Hardware-Fakten: wissenschaftliche Schreibweise = 20 mA",
        facts["HP-DIM"].BusCurrent == 20, facts["HP-DIM"].BusCurrent?.ToString());
    Check("Hardware-Fakten: Koppler und Mediumstypen",
        facts["P-DIM"].IsCoupler && facts["HP-DIM"].MediumTypeIds.SequenceEqual(["MT-0", "MT-2"]));
    Check("Hardware-Fakten: Powerline-Repeater", facts["P-PL"].IsPowerLineRepeater);
    Check("Hardware-Fakten: Hardware ohne Produkt/Programm wird ignoriert", !facts.ContainsKey("H-EMPTY"));
}

// --- real catalogues, when a corpus is available ---
// A corpus is never part of the repository: point KNX_CATALOG_SAMPLES at a folder of
// *.knxprod files (vendor downloads) to run the real-file regression.
var sampleRoot = Environment.GetEnvironmentVariable("KNX_CATALOG_SAMPLES");
if (!string.IsNullOrWhiteSpace(sampleRoot) && Directory.Exists(sampleRoot))
{
    var samples = Directory.EnumerateFiles(sampleRoot, "*.knxprod", SearchOption.AllDirectories)
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .ToList();
    var opened = 0;
    var products = 0;
    var resolved = 0;
    var loadedPrograms = 0;
    var extensionPrograms = 0;
    var dcaPrograms = 0;
    var failuresBefore = failures.Count;

    foreach (var file in samples)
    {
        try
        {
            var sampleCatalog = KnxProductCatalog.Open(file);
            var withProgram = sampleCatalog.Products.Count(product => sampleCatalog.FindProgramFor(product) is not null);
            opened++;
            products += sampleCatalog.Products.Count;
            resolved += withProgram;
            foreach (var program in sampleCatalog.Programs)
            {
                loadedPrograms++;
                if (program.Extensions.Count > 0) extensionPrograms++;
                if (program.RequiresDca) dcaPrograms++;
            }
            Check($"Sample lesbar: {Path.GetFileName(file)}", sampleCatalog.Products.Count > 0,
                $"products={sampleCatalog.Products.Count}");
            // knx_master.xml carries the display name; the catalogue parts only reference the id.
            if (Path.GetFileName(file).Contains("MDT_KP", StringComparison.OrdinalIgnoreCase))
                Check($"Herstellername aus knx_master: {Path.GetFileName(file)}",
                    sampleCatalog.Manufacturer is { } manufacturer
                    && manufacturer.Name.Contains("MDT", StringComparison.OrdinalIgnoreCase),
                    sampleCatalog.Manufacturer?.Name ?? "null");
            // DCA metadata from real files; one bounded application part per check.
            var sampleName = Path.GetFileName(file);
            if (sampleName.StartsWith("MDT_KP_BE_01_Push_Button_Smart_86", StringComparison.OrdinalIgnoreCase))
            {
                var mdtProgram = sampleCatalog.FindProgram("M-0083_A-008A-25-0499");
                Check("MDT: Extension gelesen, kein Ets*-Plug-in",
                    mdtProgram is { RequiresDca: false, IsStaticallyEditable: true },
                    mdtProgram is null ? "program not found" : string.Join(",", mdtProgram.Extensions));
                Check("MDT: kein RequiresExternalSoftware", mdtProgram?.RequiresExternalSoftware == false);
            }
            if (sampleName.StartsWith("ABB_i-bus_ETS4", StringComparison.OrdinalIgnoreCase))
            {
                var abbProgram = sampleCatalog.FindProgram("M-0002_A-A005-10-DC4D");
                Check("ABB: EtsUiPlugin aus echter Datei",
                    abbProgram is { RequiresDca: true } p && p.Extensions.Contains("EtsUiPlugin"),
                    abbProgram is null ? "program not found" : string.Join(",", abbProgram.Extensions));
                Check("ABB: DCA-Applikation nicht statisch editierbar", abbProgram?.IsStaticallyEditable == false);
            }
            if (sampleName.Contains("Berker_Alarmzentrale", StringComparison.OrdinalIgnoreCase))
            {
                var berkerProgram = sampleCatalog.FindProgram("M-0006_A-C004-03-133E-O000A");
                Check("Berker: RequiresExternalSoftware aus echter Datei", berkerProgram?.RequiresExternalSoftware == true);
                Check("Berker: EtsUiPlugin und EtsDataHandler",
                    berkerProgram is { RequiresDca: true } q && q.Extensions.Contains("EtsUiPlugin")
                    && q.Extensions.Contains("EtsDataHandler"),
                    berkerProgram is null ? "program not found" : string.Join(",", berkerProgram.Extensions));
            }
        }
        catch (IOException)
        {
            // A file that is still being written by a download must not fail the run.
        }
        catch (Exception ex)
        {
            Check($"Sample lesbar: {Path.GetFileName(file)}", false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    Console.WriteLine($"  PASS Korpus: {opened}/{samples.Count} Kataloge, {products} Produkte, " +
                      $"{resolved} mit Applikation, {loadedPrograms} geladene Applikationen, " +
                      $"{extensionPrograms} mit Extension, {dcaPrograms} mit ETS-Plug-in");
    Console.WriteLine("  INFO MDT-Korpus: EtsUiPlugin kommt in den MDT-Dateien nicht vor (nur Baggage); " +
                      "EtsUiPlugin/RequiresExternalSoftware werden an ABB/Berker belegt.");
    Check("Korpus: Applikationen geladen", loadedPrograms > 0, loadedPrograms.ToString());
    Check("Korpus: die meisten Dateien lesbar", samples.Count == 0 || opened >= samples.Count - 1,
        $"{opened}/{samples.Count}");
    Check("Korpus: Applikationen auflösbar", products == 0 || resolved * 100 / products >= 90,
        $"{resolved}/{products}");
    if (failures.Count == failuresBefore)
        Console.WriteLine("  PASS alle Korpus-Dateien ohne Fehler");
}
else
{
    var realPath = FindRealCatalogue();
    if (realPath is null)
    {
        Console.WriteLine("  SKIP echter .knxprod (kein Korpus gesetzt und keiner unter C:\\ProgramData\\KNX)");
    }
    else
    {
        var real = KnxProductCatalog.Open(realPath);
        Console.WriteLine($"  PASS echter Katalog {Path.GetFileName(realPath)}: {real.Products.Count} Produkte, " +
                          $"{real.CatalogSections.Count} Katalogbereiche");
        Check("Echter Katalog hat Produkte", real.Products.Count > 0, real.Products.Count.ToString());
    }
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALLE CHECKS OK");
    return 0;
}
Console.WriteLine($"{failures.Count} FEHLER:");
foreach (var failure in failures) Console.WriteLine("  - " + failure);
return 1;

// Looks for a real product database, bounded in depth so the profile scan stays fast.
static string? FindRealCatalogue(){
    var roots = new[] { @"C:\ProgramData\KNX", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
    foreach (var start in roots)
    {
        if (!Directory.Exists(start)) continue;
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((start, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Dequeue();
            string[] files;
            string[] subdirectories;
            try
            {
                files = Directory.GetFiles(directory, "*.knxprod");
                subdirectories = depth < 3 ? Directory.GetDirectories(directory) : [];
            }
            catch (Exception)
            {
                continue;
            }
            if (files.Length > 0) return files[0];
            foreach (var subdirectory in subdirectories) pending.Enqueue((subdirectory, depth + 1));
        }
    }
    return null;
}
/// <summary>Synchronous progress sink: Progress<T> would post to the thread pool.</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}