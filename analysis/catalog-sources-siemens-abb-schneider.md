# Echte ETS-Produktdatenbanken: Siemens, ABB/Busch-Jaeger, Schneider/Merten

Untersuchung für den Katalog-Reader (`src/Knx.Core/Catalog`, Klasse `KnxProductCatalog`) des
ETS6-Rebuild. Ziel war, echte `.knxprod`-Dateien der drei Herstellergruppen zu beschaffen und
deren tatsächlichen Aufbau festzuhalten, damit der Parser gegen reale Strukturen gehärtet werden
kann statt nur gegen das synthetische Testarchiv.

- Datum: 18.10.2026
- Branch: `agents/run4/s8-catalog-siemens`
- Ablage der Proben (nicht im Repository, nicht eingecheckt):
  `<project-path>\sample-catalogs\{siemens,abb,schneider}`
- Nur öffentlich angebotene Herstellerdownloads, kleine Stichproben. Keine Binärdateien im Repo.

## Übersicht der beschafften Dateien

| Hersteller | Produkt/Datenbank | URL (Angebotsseite → direkter Download) | Größe | Layout | Namespace | Besonderheiten |
|---|---|---|---|---|---|---|
| Siemens | GAMMA instabus + HVAC KNX, Complete PDB 10/2023 (ETS5/6) | https://knx-td.sihub.siemens.com/produktdb_en_2023.htm → https://knx-td.sihub.siemens.com/de/pdb/files/2023_Oct/Siemens_HVAC_All_PDB_Oct_2023_ETS5_ETS6.knxprod | 74.534.693 B | Split (`knx_master.xml` + `M-0001/`, `M-00FD/`) | `http://knx.org/xml/project/20` | 241 Einträge; 2 Hersteller in einer Datei; Master listet ~600 KNX-Hersteller; Baggages mit MSI/DLL/ZIP/PNG |
| Siemens | GAMMA instabus „Now and then" 1998–2015 (ETS4/5) | https://knx-td.sihub.siemens.com/produktdb_en_2023.htm → https://knx-td.sihub.siemens.com/de/pdb/files/2015/Now_and_then_June2015.knxprod | 75.629.076 B | Split (`knx_master.xml` + `M-0001/`) | `http://knx.org/xml/project/11` | 626 Einträge; Alt- und Neuapplikationen gemischt; Baggages mit ETS3-Plug-in-DLLs/MSIs |
| ABB / Busch-Jaeger | ETS5/ETS6 Applikationen Busch-Installationsbus KNX, Rev K (12/2024) | https://library.abb.com/share?cid=9AAC189142&dk=Software → ABB Library „ID 9AKK107046A7479, TEIL:BJE" | 92.762.432 B | Split (`knx_master.xml` + `M-0007/`) | `http://knx.org/xml/project/20` | 385 Einträge; Hersteller `M-0007` = „Busch-Jaeger Elektro"; `.abb`/`.png`/`.dll`/`.msi`/`.loc`/`.dat`/`.pdf`; Programmtypen `ApplicationProgram` **und** `PeiProgram` |
| ABB | ETS4 Applikationen ABB i-bus KNX, Rev D (2018) | https://library.abb.com/share?cid=9AAC189142&dk=Software → ABB Library „ID 9AKK107046A2718" | 50.574.524 B | Split (`knx_master.xml` + `M-0002/`) | `http://knx.org/xml/project/11` | 447 Einträge; Hersteller `M-0002` = „ABB", `DefaultLanguage="en"`; `Hardware@IsRailMounted="1"`, `WidthInMillimeter="-1"`; Beigaben `*.html` (88), `*.chm`, `*.gui`, `*.resources` |
| ABB | Sidus KNX Bewegungsmelder flat (ETS4/5) | ABB Library „ID 9AKK107680A5409" | 161.652 B | Split (`knx_master.xml` + `M-0002/`) | `http://knx.org/xml/project/13` | Hersteller `M-0002` = „ABB", DefaultLanguage `de-DE`; ein Programm |
| ABB | Sidus KNX Tastsensor 1/2fach (ETS4/5) | ABB Library „ID 9AKK107046A4905" | 403.776 B | Split (`knx_master.xml` + `M-0002/`) | `http://knx.org/xml/project/11` | Alter Namespace-Teilstand (project/11) |
| Schneider / Merten | SpaceLogic KNX Secure Switch/Blind Master, 5810/1.2 (ETS5/6), 2026 | https://www.se.com/de/de/download/document/MTN6705-0008S_ETS_5810_1_2/ | ZIP 1.422.211 B (inneres `.knxprod` 1.509.057 B) | **ZIP in ZIP**: äußeres `.zip` → 1 `.knxprod` (Split, `M-0064/`) | `http://knx.org/xml/project/20` | Hersteller `M-0064` = „Schneider Electric Industries SAS"; OrderNumber `MTN6705-0008S`; Secure |
| Schneider / Merten | KNX-Gerät MTN6306xx-00, ETS4 (2011) | https://www.se.com/de/de/download/document/MTN6306xx-00_ETS4_2011_11_ALL_8/ | ZIP 769.524 B (inneres `.knxprod` 780.949 B) | **ZIP in ZIP** (Split, `M-0064/`) | `http://knx.org/xml/project/10` | Ältester beobachteter Split-Namespace (project/10) |
| Schneider / Merten | MTN6215-0410 (ETS5) | https://www.se.com/de/de/download/document/MTN6215-0410_ETS5/ | ZIP 1.538.528 B (inneres `.knxprod` 1.873.457 B) | **ZIP in ZIP** (Split, `M-0064/`) | `http://knx.org/xml/project/20` | 54 Einträge im inneren Archiv |
| Schneider / Merten | SpaceLogic KNX 8-Kanal 1–10 V Dimmer, MTN6720-0008 (ETS5) | https://www.se.com/de/de/download/document/MTN6720-0008_ETS5/ | ZIP 306.679 B (inneres `.knxprod` 311.645 B) | **ZIP in ZIP** (Split, `M-0064/`) | `http://knx.org/xml/project/20` | Dateiname des inneren Archivs mit Leerzeichen/Sonderzeichen: `Spacelogic KNX 8 channel·1-10V dimmer with manual switch.knxprod` |

Summe der Proben: 298,10 MB (unter dem Größenziel von 300 MB). Die ABB-Dateien laufen über
`https://search.abb.com/library/Download.aspx?DocumentID=<ID>&LanguageCode=en&Action=Launch`, das
auf `https://library.e.abb.com/public/<guid>/<datei>.knxprod?x-sign=…` weiterleitet. Bei ABB muss
`LanguageCode=en` gesetzt sein (`de` liefert 404), und `DocumentPartId` nur bei mehrteiligen
Dokumenten (z. B. BJE).

## Aufbau der echten Dateien

### Gemeinsames Muster: Split-Archiv (alle realen `.knxprod`)

Alle zehn beschafften Dateien – und auch die von anderen Agenten beigelegten Hersteller
(Berker, MDT, Theben) – sind **Split-Archive**, kein monolithisches `knxprod.xml`:

```text
knx_master.xml                       # Wurzel <KNX>; <MasterData> + komplette KNX-Herstellerliste
<M-XXXX>.signature                   # 175 B Signaturdatei je Herstellerverzeichnis
M-XXXX/                              # ein Verzeichnis je Hersteller (RefId)
  Catalog.xml                        # <KNX><ManufacturerData><Manufacturer RefId="M-XXXX"><Catalog>…
  Hardware.xml                       # …<Hardware><Hardware Id=…><Products><Product …>
  Baggages.xml                       # optional
  M-XXXX_A-<AppNr>-<Ver>-<Hash>[-O<MaskHex>].xml   # ein XML je Applikationsprogramm
  Baggages/<PluginId>/…              # MSI, DLL, PNG, ZIP, PDF, TXT, INI, DAT, LOC, ABB …
```

Beispiele für Programmdatei-Namen: `M-0001_A-7002-01-6822.xml`, `M-0001_A-140B-11-E4CB-O0048.xml`,
`M-0064_A-5810-12-2E9D.xml`, `M-0007_A-6136-61-CA73.xml`. Der `-O<hex>`-Suffix trägt die
Mask-/Objektvariante.

**Wichtig:** Die Wurzel jeder XML (auch der Programmdateien) ist `<KNX>`, nicht
`<ApplicationProgram>`. Die Nutzdaten stecken unter `<ManufacturerData>`:

```xml
<KNX xmlns="http://knx.org/xml/project/20">
  <ManufacturerData>
    <Manufacturer RefId="M-0001">
      <ApplicationPrograms>
        <ApplicationProgram Id="M-0001_A-7002-01-6822" ApplicationNumber="28674"
            ApplicationVersion="1" ProgramType="ApplicationProgram" MaskVersion="MV-0300-…"
            Name="300 CO USB-Interface 700201" LoadProcedureStyle="ProductProcedure"
            PeiType="0" DefaultLanguage="en-US" PreEts4Style="true" ConvertedFromPreEts4Data="true" />
```

### `knx_master.xml`

- Wurzel `<KNX xmlns="http://knx.org/xml/project/NN">`, direkt `<MasterData Id="MD-1" Version="…" Signature="…">`.
- Enthält `<DatapointTypes>` (DPT/DPST) und die **vollständige KNX-Herstellertabelle**
  `<Manufacturers>` – beobachtet: Siemens ~600, ABB 400–707, Schneider 778 Einträge `M-0001 … M-02BE … M-ABB2/M-ABB7`.
- Ein Eintrag z. B.:
  `<Manufacturer Id="M-0007" KnxManufacturerId="7" Name="Busch-Jaeger Elektro" CompatibilityGroup="2" DefaultLanguage="en-US" ImportRestriction="Group" ImportGroup="Group-ABB" MemberStatus="Active">`.
- **Keine** `ManufacturerData`, **kein** `Hardware`/`Catalog`/`ApplicationPrograms` – die liegen in `M-XXXX/*.xml`.

### Je Hersteller: `RefId` vs. `Id`

- `knx_master.xml`: `<Manufacturer Id="M-XXXX" …>` (Herstellertabelle).
- `M-XXXX/Catalog.xml` und `Hardware.xml`: `<Manufacturer RefId="M-XXXX">` und darin
  `<Catalog>/<CatalogSection>/<CatalogItem ProductRefId=… Hardware2ProgramRefId=…>` bzw.
  `<Hardware><Hardware Id=…><Products><Product Id=… OrderNumber=…>`.
- `Hardware2Program Id=… MediumTypes="MT-0" Hash=…` mit `<ApplicationProgramRef RefId=…>`.

Beispielprodukt (Siemens): `OrderNumber="5WG1205-2AB_2"`, Text `UP 205/x2 Touch Control TC5`.
Beispiel (Merten/Schneider): `OrderNumber="MTN6705-0008S"`.
Beispiel (ABB): `OrderNumber="2CCA 388 975 R0001"`.
Sonderzeichen in `OrderNumber` sind durch `_` ersetzt (`5WG1205-2AB_2`), IDs kodieren Sonderzeichen
hex-artig als `.2D` (z. B. `…_CI-MTN6705.2D0008S-1`).

### Übersetzungen

Jede Teil-Datei (`knx_master.xml`, `Catalog.xml`, `Hardware.xml`, jede Programmdatei) hat ihre
**eigene** `<Languages>`-Tabelle und eigene `<TranslationUnit RefId="…">`-Blöcke. Die Übersetzungen
sind also über die Archivteile verteilt, nicht zentral.

### Mehrere Hersteller pro Datei

Siemens 2023 enthält `M-0001` (GAMMA instabus, ~174 Einträge) **und** `M-00FD` (HVAC, ~64 Einträge)
in einer Datei. Der Reader darf nicht beim ersten Hersteller stehenbleiben.

### Nicht-XML-Nutzdaten

- `<M-XXXX>.signature` (175 B) je Hersteller.
- `M-XXXX/Baggages.xml` + `M-XXXX/Baggages/<PluginId>/…`: ETS3-Plug-in-Setups (`*.msi`), `*.dll`,
  `*.png`, `*.zip` (Icon-Pakete), `*.pdf`, `*.txt`, `*.ini`, `*.dat`, `*.loc`, `*.abb`.
- Diese dürfen den Parser nicht stören und müssen beim Import nicht mitgeschleppt werden.

### Doppelte Verpackung

Schneider/Merten (und Theben) liefern **ZIP → knxprod**: das geladene `.zip` enthält genau eine
`.knxprod`. Innere Dateinamen enthalten Leerzeichen und Sonderzeichen.

## Was der Parser können muss

Beobachtete Varianten, die der aktuelle Reader (`PickMainEntry`, `Parse`, `AddPrograms`,
`attachedPrograms`) noch nicht bzw. nur zufällig abdeckt:

1. **Split-Archiv zusammensetzen.** Statt *einer* Hauptdatei müssen `knx_master.xml` **und** alle
   `M-XXXX/*.xml` geladen und über `RefId`/`Id` verbunden werden: `Catalog.xml` (Katalogbaum),
   `Hardware.xml` (Hardware/Products/Hardware2Program), je Programmdatei die `ApplicationPrograms`.
2. **Master-Tabelle von Herstellerdaten trennen.** Die ~400–780 `<Manufacturer Id="M-XXXX">`-Einträge
   in `knx_master.xml` sind reine Stammdaten. Nur Hersteller mit tatsächlichem `M-XXXX/`-Datenverzeichnis
   liefern Produkte.
3. **Programmdateien mit Wurzel `<KNX>` erkennen.** Der aktuelle Zweig prüft
   `Root.Name.LocalName == "ApplicationProgram"` – Programmdateien haben Wurzel `<KNX>`; Typisierung
   muss über `<ManufacturerData><Manufacturer><ApplicationPrograms><ApplicationProgram>` erfolgen.
4. **Namespace-agnostisch bleiben.** Beobachtete Namespaces: `project/10`, `project/11`, `project/13`,
   `project/20`. Die Code-Doku nennt `prod/20` – in echten Dateien **nicht** vorgekommen. Ein
   monolithisches `knxprod.xml` (`prod/NN`) trat in keiner realen Probe auf, nur im synthetischen Test.
5. **Übersetzungen je Teil-Datei einsammeln** und RefId-basiert zusammenführen; sonst bleiben Namen
   auf `DefaultLanguage` (meist `en-US`, bei ABB/Sidus `de-DE`).
6. **Mehrere Hersteller pro Datei** verarbeiten (nicht `Manufacturer ??=`).
7. **Doppelt gepackte ZIPs auspacken** (`.zip` → `.knxprod`, eine Ebene).
8. **Mehrere Programmtypen** zulassen: `ProgramType="ApplicationProgram"` und `"PeiProgram"`.
9. **Namens-/Pfad-Robustheit:** `-O<MaskHex>`-Suffixe, hex-artige `RefId`s mit `.`-Ersatz,
   Dateinamen mit Leerzeichen/Sonderzeichen, Groß-/Kleinschreibung.
10. **Bibliotheksname ohne Kollision.** Da bei Split-Archiven aktuell `Programs` leer bleibt, hieße
    jede Siemens-Datei `M-0001.knxprod` (Kollision/Überschreiben). Name aus Hersteller-Id **plus**
    Datei-/Programmkennung bilden.
11. **Beigaben ignorieren**, nur Bilder durchreichen (`.abb`, `.dll`, `.msi`, `.dat`, `.loc`, `.html`,
    `.chm`, `.gui`, `.resources` sind keine Bilder; `.png`/`.jpg` sind Produktbilder).

## Folgen für den vorhandenen Code (statisch geprüft)

- `PickMainEntry` wählt `knx_master.xml` (einzige XML auf oberster Archivebene) → `Parse` findet
  `<Manufacturer>` (Tabelle) und wirft **nicht**, aber `ReadHardware`/`ReadCatalog` finden nichts
  (Daten liegen in separaten Dateien) → **0 Produkte, 0 Katalogknoten, 0 Programme**.
- Der `attachedPrograms`-Zweig (`_A-`-Filter) lädt die Programmdateien, verwirft sie aber wieder,
  weil deren Wurzel `<KNX>` und nicht `<ApplicationProgram>` ist.
- `CatalogStore.LoadAll` erfasst nur `*.knxprod`/`knxprod.xml`; **doppelt gepackte `.zip`-Downloads
  werden gar nicht importiert**, `Open` würde bei direktem Aufruf `InvalidDataException` werfen.
- Namespaces werden nur deshalb toleriert, weil auf `LocalName` verglichen wird; die Code-Dokumentation
  (`prod/20`) passt nicht zu den realen Dateien (`project/NN`).

## Nicht erreichbar (Login/Konto, nicht umgangen)

| Angebot | URL | Status |
|---|---|---|
| KNX ETS App Store (ETS-Apps von Siemens/ABB/BJE) | https://my.knx.org/en/shop/ | Nur mit KNX-Account; „Download nicht öffentlich". |
| Schneider Electric, Software-/ETS-Downloads | z. B. https://www.se.com/de/de/download/document/MTN6710-0102S_ETS5/ | Download-Schaltfläche im HTML `disabled`; UI verlangt Geschäftskonto („Um diese Software herunterzuladen bitte ein Konto erstellen", „Log in or create an account to download the software."). Direkte `https://download.se.com/files?p_Doc_Ref=…`-URLs waren für die gelisteten Beispiele ohne Login abrufbar, `MTN6710-0102S_ETS5` antwortete mit HTTP 403. |
| ABB Library (einzelne Dokumente) | https://library.abb.com/share?cid=9AAC189142&dk=Software | Meldung „… only available for ABB internal users" bei unvollständigen `DocumentID`-Aufrufen (kein Login vonnöten für die genutzten Public-Dokumente). |
| Merten Legacy (ETS < v3, `.vd`/`.vd_`) | https://community.se.com/t5/SpaceLogic-KNX-Forum/Legacy-Merten-ETS-files/td-p/497535 | Altformat, teils nur über Repositories; kein `.knxprod`. |
| ABB „Reconstruction" `.vd`-Gesamtdatenbank (546 MB) | ABB Library „ID 9AKK107046A5150" | Öffentlich, aber Altformat (`.vd`) und über dem Größenziel. |

Nicht im Scope: `.vd2/.vd4/.vd5/.vd_` (ETS2/3) und Plug-in-Projekte (`.pr3/.pr5`) – anderer
Dateityp, für den `.knxprod`-Parser nicht relevant.

## Ablage der Proben

Zehn reale Dateien, 298,10 MB, außerhalb des Repositories in `<project-path>\sample-catalogs\`:

```text
siemens\Siemens_HVAC_All_PDB_Oct_2023_ETS5_ETS6.knxprod                 74.534.693 B
siemens\Now_and_then_June2015.knxprod                                   75.629.076 B
abb\IBUS_ETS5_BJE_XX_V24-12-20_9AKK107046A7479-Rev_K.knxprod            92.762.432 B
abb\ABB_i-bus_ETS4_Applications_XX_9AKK107046A2718_RevD.knxprod        50.574.524 B
abb\Sidus_KNX_Bewegungsmelder_flat_ETS4-5_9AKK107680A5409.knxprod         161.652 B
abb\Sidus_KNX_Tastsensor_1_2fach_ETS4-5_9AKK107046A4905.knxprod           403.776 B
schneider\MTN6705-0008S-5810_12-ETS5-2026-02_ALL9.zip                   1.422.211 B
schneider\MTN6306xx-00_ETS4_2011_11_ALL_8.zip                             769.524 B
schneider\MTN6215-0410_ETS5.zip                                         1.538.528 B
schneider\MTN6720-0008_ETS5.zip                                           306.679 B
```

Keine dieser Binärdateien ist Bestandteil des Repositories.
