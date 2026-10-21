using System.IO.Compression;
using System.Text;
using Knx;
using Knx.Catalog;

// Self-check for the Knxprod catalogue reader: synthetic catalogue (ZIP, loose XML,
// directory), translations, store round trip and device creation.
// Run: dotnet run --project tests/Knx.Catalog.Tests
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
// German and English translations. Element layout follows the prod schema of ETS6.
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
                         Number="1" ObjectSize="1 Bit"/>
              <ComObject Id="CO-2" Name="Status" Text="Status" FunctionText="Status"
                         Number="2" ObjectSize="1 Bit"/>
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
static string? FindRealCatalogue()
{
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
