# Katalog-Quellen: Theben, Zennio, Weinzierl, Elsner Elektronik

Echte ETS-Produktdatenbanken für die Härtung des Kataloglesers in
`src/Knx.Core/Catalog`. Alle Dateien sind öffentlich zum Import in die ETS
angeboten und wurden nur lokal ausgewertet (Ablage unter
`<project-path>\sample-catalogs\...`, nicht im Repository).

Stand der Erhebung: 2026-10-18. Branch `agents/run4/s9-catalog-theben`,
Basis `b1e954c`.

## Übersicht der Artefakte

| Hersteller | Produkt/Datenbank | URL | Größe | Layout | Namespace | Besonderheiten |
|---|---|---|---|---|---|---|
| Theben | KNX-Gesamtdatenbank ETS5 (KNXprod) | https://www.theben.de/ocsmedia/optimized/full/o27748v146_KNXprod_KNXGesamtdatenbank.zip | 22,1 MB | ZIP → 1 `.knxprod` + 1 PDF | project/20 | Split-Layout; **2 Hersteller** in einer Datei (`M-0048` Theben = 196 Einträge, `M-00E8` = 21 Einträge); 220 ZIP-Einträge; PDF `Theben AG 2506 ETS5.pdf` als Beifang |
| Theben | JU 1 KNX (Einzelprodukt) | https://www.theben.de/ocsmedia/optimized/full/o25141v53_JU_1_KNX_KNXDatenbank.zip (Seite: https://www.theben.de/en/ju-1-knx-4942550) | 322 kB | ZIP → 1 `.knxprod` | project/20 | Split; **genau ein Applikationsprogramm**; `RefId=M-0048`; `OrderNumber=4942550`; AppName „JU 1 secure“ |
| Theben | iONprime PB KNX (Einzelprodukt) | https://www.theben.de/ocsmedia/optimized/full/o59021v53_iONprime_PB_KNX_KNXDatenbank.zip (Seite: https://www.theben.de/en/ionprime-pb-knx-4969301) | 281 kB | ZIP → 1 `.knxprod` | project/20 | Split; AP-Dateiname mit `-O0228`-Suffix (`M-0048_A-E011-20-F75D-O0228.xml`) |
| Zennio | CX 1.2 (CX50) Applikationsprogramm | https://www.zennio.com/documents/application_program_cx/ | 2,0 MB | ZIP → 1 `.knxprod` + 1 PDF (ChangeLog) | project/20 | Split; HTTP-Response ist direkt der ZIP (Friendly-URL); `RefId=M-0071`; `OrderNumber=ZVICXxy` |
| Weinzierl | KNX USB Interface 312.1 (ETS5) | https://weinzierl.de/images/products/312.1/downloads/weinzierl-312-1-knx-usb-interface-5501-ets5.zip | 107 kB | ZIP → 1 `.knxprod` | project/20 | Split; 1 AP; `RefId=M-00C5`; `OrderNumber` = Gerätename (Text, nicht Artikelnummer) |
| Weinzierl | KNX USB Interface 312 (ETS4) | https://weinzierl.de/images/products/312/downloads/weinzierl-312-knx-usb-interface-5229-ets4.zip | 70 kB | ZIP → 1 `.knxprod` | **project/11** | Split; **älterer Namensraum**; gleiche Struktur wie project/20 |
| Weinzierl | KNX IO 520.1 secure (ETS5) | https://weinzierl.de/images/products/520.1/downloads/weinzierl-520-1-knx-io-secure-5515-ets5.zip | 338 kB | ZIP → 1 `.knxprod` | project/20 | Split; großes AP-XML (4,5 MB entpackt) |
| Weinzierl | KNX USB Stick 330 (ETS3) | https://weinzierl.de/images/products/330/downloads/weinzierl_330_knx_usb_stick_ets3.zip | 15 kB | ZIP → 1 `.vd5` | – | **Kein knxprod**: ETS3-Container, innen ZIP mit einzigem Member `Ets/ets.vd_` |
| Weinzierl | KNX ENO 630 (ETS3) | https://weinzierl.de/images/download/products/630/weinzierl-630-knx-eno-5129-ets3.zip | 1,15 MB | ZIP → 1 `.vd5` | – | Dito: `Ets/ets.vd_`; keine XML-Teile auf oberster Ebene |
| Elsner | Suntracer KNX pro | https://info.elsner-elektronik.de/hubfs/Produktdaten/70900_Suntracer_KNX_pro_1-4_d-en-es-fr-it.zip (Seite: https://www.elsner-elektronik.de/en/suntracer-knx-pro.html) | 1,6 MB | ZIP → 1 `.knxprod` | project/20 | Split; 1 AP; `RefId=M-00C9`; `OrderNumber=70900`; AP-XML entpackt **43 MB** |
| Elsner | KNX eTR 101 | https://info.elsner-elektronik.de/hubfs/Produktdaten/70650_KNX_eTR_101_2-0_d-en-fr-it-es.zip (Seite: https://www.elsner-elektronik.de/en/knx-etr-101.html) | 142 kB | ZIP → 1 `.knxprod` | project/20 | Split; 1 AP; `RefId=M-00C9` |

Hersteller-`RefId` sind mit Präfix gespeichert (`M-00C5`, `M-0048`, `M-00C9`,
`M-0071`) – also nicht als reine Hexzahl.

### Download-Strategie der Hersteller

- **Theben**: keine zentrale Produktdatenbank-Übersichtsseite. Die Download-URL
  steckt pro Produkt im JSON-LD (`subjectOf[...].url`) der Produktseite bzw.
  hinter dem Download-Center-Filter „KNX-Database (Single Product)“
  (https://www.theben.de/en/downloads/). Direkte URLs liegen unter
  `https://www.theben.de/ocsmedia/optimized/full/*.zip`.
- **Zennio**: Einzelgeräte-Apps liegen hinter Friendly-URLs
  `https://www.zennio.com/documents/application_program_<kuerzel>/`, die direkt
  den ZIP liefern. Ältere Versionen im „Application Program Repository“.
- **Weinzierl**: zentrale Downloadliste (https://weinzierl.de/en/download/) mit
  pro Gerät mehreren ETS-Generationen als `...-ets3.zip` / `-ets4.zip` /
  `-ets5.zip`.
- **Elsner**: Produktdatenbanken nur auf den Produktseiten, Dateien liegen unter
  `https://info.elsner-elektronik.de/hubfs/Produktdaten/`.

## Zentrale Struktur-Beobachtung: Split-Layout, zweistufig verschachtelt

Jede echte Lieferung ist **zweistufig** verschachtelt:

1. Äußerer ZIP (die Download-Datei) enthält genau eine `.knxprod`
   (optional plus PDF).
2. Die `.knxprod` ist **selbst ein ZIP** im **Split-Layout** – es gibt **kein**
   `knxprod.xml`:

```
knx_master.xml                      <- nur <MasterData> (DatapointTypes, Sprachen); KEIN <Manufacturer>
M-00C5.signature                    <- 175 Bytes, base64 (kein XML)
M-00C5/Baggages.xml
M-00C5/Catalog.xml                  <- <ManufacturerData>/<Manufacturer RefId="M-00C5">/Catalog
M-00C5/Hardware.xml                 <- .../Hardware/Products/Product + Hardware2Programs
M-00C5/M-00C5_A-030D-11-53D7.xml    <- ein XML pro Applikationsprogramm
M-00C5/Baggages/ …                  <- Bilder, icons.zip
```

Wurzel aller Teil-Dateien ist `<KNX>`; `knx_master.xml` mit
`xmlns="http://knx.org/xml/project/20"`, die `M-xxx`-Teile ebenso (mit
`CreatedBy`/`ToolVersion`). Ältere Weinzierl-Dateien (ETS4) nutzen
`http://knx.org/xml/project/11`. Der Namensraum `prod/20` (monolithisch, im
Code-Kommentar von `KnxProductCatalog` angenommen) kam **in keiner** der
erhobenen Dateien vor.

## Was der Parser können muss

Alle Punkte sind am echten Material beobachtet und brechen den aktuellen
`KnxProductCatalog`:

1. **Verschachtelung auflösen.** `Open()` öffnet nur eine ZIP-Ebene. Beim
   äußeren Hersteller-ZIP gibt es keine XML-Einträge → `PickMainEntry` liefert
   `null` → „contains no knxprod.xml“. Der Leser braucht einen zweiten Schritt:
   äußeren ZIP entpacken, darin die `.knxprod` finden und erneut als ZIP öffnen.

2. **Split-Layout zusammenführen statt eine Hauptdatei wählen.** Im `.knxprod`
   wählt `PickMainEntry` die größte XML auf oberster Ebene → das ist
   `knx_master.xml`. Die enthält nur `<MasterData>`, **kein** `<Manufacturer>`
   → `Parse()` wirft „not a KNX product catalogue: no <Manufacturer> element.“
   Nötig: alle Teile mergen – `knx_master.xml` **plus** jede
   `M-xxxx/Catalog.xml`, `M-xxxx/Hardware.xml`, `M-xxxx/Baggages.xml` **plus**
   jede `M-xxxx/M-xxxx_A-*.xml`.

3. **`Manufacturer` sitzt in jeder Teil-Datei, nicht in der Master-Datei.**
   `RefId="M-00C5"` (mit Präfix). `Catalog.xml`, `Hardware.xml`,
   `Baggages.xml` und die AP-Datei tragen jeweils
   `<ManufacturerData><Manufacturer RefId="…">`; `knx_master.xml` nicht.

4. **Applikationsprogramm-Dateien haben Wurzel `<KNX>`, nicht
   `<ApplicationProgram>`.** Die bestehende Erkennung
   `part.Root?.Name.LocalName == "ApplicationProgram"` greift daher nie – die
   Programme werden über `Descendants()` unter
   `ManufacturerData/Manufacturer/ApplicationPrograms/ApplicationProgram`
   gefunden. Dateiname: `M-xxxx/M-xxxx_A-nnnn-vv-hhhh[-Onnnn].xml`.

5. **Ein XML pro Applikationsprogramm.** Einzelgeräte-Dateien enthalten genau
   eine `_A-…`-Datei; die Theben-Gesamtdatenbank viele (196 `M-0048`-Einträge
   inkl. Unterordner). Alle Programme eines Geräts müssen aus den separaten
   Teilen zusammengesetzt werden (`AddPrograms` muss über alle Teile laufen).

6. **Mehrere Hersteller in einer `.knxprod`.** Theben-Gesamt: `M-0048` **und**
   `M-00E8` mit je eigener `*.signature`. Das aktuelle `Manufacturer ??= …`
   behält nur den ersten; Produkte/Programme/Filter müssen je Hersteller
   getrennt bleiben.

7. **Verknüpfung Product → ApplicationProgram über Referenzen.**
   `Product@Id` (z. B. `M-0048_H-4942550-2_P-4942550`) ist intern;
   `CatalogItem@ProductRefId` zeigt darauf, `CatalogItem@Hardware2ProgramRefId`
   → `Hardware/Hardware2Programs/Hardware2Program@Id`
   (z. B. `…_HP-2552-22-8CC5`) → `ApplicationProgramRef@RefId`
   (`M-0048_A-2552-22-8CC5`). Der Resolver über Entry-/Program-/Hardware-Refs
   muss also über Teil-Dateigrenzen hinweg auflösen.

8. **`OrderNumber` ist herstellerspezifisch belegt.** Theben `4942550`, Elsner
   `70900`, Zennio `ZVICXxy` (Artikelnummern); Weinzierl nutzt dort den
   Gerätenamen als Text („KNX USB Interface 312.1“). Nicht als Zahl erzwingen.

9. **`*.signature` je Hersteller** (`M-xxxx.signature`, 175 Bytes, base64) auf
   oberster Ebene des `.knxprod` – kein XML, muss beim Einlesen übersprungen
   werden (die bestehende Bild-/XML-Heuristik ist dafür unkritisch, aber nicht
   als XML laden).

10. **Bilder liegen unter `Baggages/`.** Referenzen stehen in `Baggages.xml`;
    die Dateien selbst sind tief verschachtelt
    (`M-00C5/Baggages/312-1/logo.png`, `…/icons.zip`). `Images` muss
    `Baggages.xml` und Unterordner berücksichtigen, nicht nur `*.png` neben dem
    Katalog.

11. **Äußerer ZIP mit Beifang.** Die Download-ZIPs enthalten neben der
    `.knxprod` z. B. `Theben AG 2506 ETS5.pdf` oder
    `00_ChangeLog - CX 1.2.pdf`. Die Auswahl der Katalogdatei darf sich davon
    nicht irritieren lassen.

12. **Namen/Versionen des Programms** stehen als Attribute an
    `ApplicationProgram` (`Name`, `ApplicationNumber`, `ApplicationVersion`,
    beobachtet 781/17, 9554/34, 4225/20, 23841/18). Der Code liest sie bereits –
    Voraussetzung ist nur, dass das Programm überhaupt gefunden wird (Punkt 4).

13. **Altes ETS3-Format `.vd5` ist kein knxprod.** ZIP mit einzigem Member
    `Ets/ets.vd_` (binär). Der Leser kann das nicht parsen und sollte es
    sauber als „nicht unterstützt“ melden statt an „no knxprod.xml“ zu
    scheitern. Ebenso relevant: `.vd4`/`.vd3`.

14. **Große Teil-Dateien.** Das Elsner-AP-XML ist entpackt ~43 MB, das
    Zennio-AP-XML ~15 MB. Streaming statt vollständiges `XDocument.Load` in den
    Speicher ist für Gesamtdatenbanken (Theben, 22 MB ZIP) relevant.

## Nicht erreichbar / nicht öffentlich

| Hersteller | Angebot | Grund |
|---|---|---|
| Zennio | Application-Program-Repository (https://www.zennio.com/application-program-repository) | Die 335 Einträge werden erst per JetEngine/AJAX gerendert; im HTML steht nur eine Autocomplete-Liste ohne Download-URL. Klick lädt ohne URL-Navigationsziel. Einzel-Apps daher nur über die Produktseiten bzw. `/documents/application_program_<kuerzel>/` erreichbar. |
| Weinzierl | KNX-IP-BAOS-, IO-, RF-Geräte u. a. | Einige per-Gerät-Downloads abbrechend (transienter Verbindungsabbruch, z. B. `weinzierl-673-knx-rf-tp-coupler-secure-5188-ets5.zip`, drei Versuche); Angebotsseite https://weinzierl.de/en/download/ |
| Weinzierl | „Product database ETS5 (all devices)“ | Die Downloadseite verweist auf eine Gesamtdatenbank, im HTML ist dafür kein Link vorhanden (möglicherweise Login/JS). |
| Theben | Zentrale „Produktdatenbanken“-Übersicht | Es gibt keine; nur Download-Center-Filter und JSON-LD je Produktseite (https://www.theben.de/en/downloads/). |
| Elsner | Zentraler ETS-Datenbank-Download | Kein Sammel-Download; nur pro Produktseite (https://www.elsner-elektronik.de/en/downloads führt nur Preislisten/Produktdaten). |

## Vergleich mit dem bestehenden Code

Der Code-Kommentar in `KnxProductCatalog.cs` beschreibt eine monolithische
`knxprod.xml` in `http://knx.org/xml/prod/20`. Die real erhobenen Dateien waren
**ausnahmslos** das mehrteilige `project/20`-Split-Layout (bzw. `project/11`
für Altbestand), ohne `knxprod.xml`. Damit bricht `Open()` bereits an Punkt 1
und 2; die Punkte 3–7 verhindern danach das korrekte Zusammensetzen von
Hersteller, Hardware, Katalog und Programmen. Namespace-Unabhängigkeit
(`LocalName`) und das Überspringen unbekannter Elemente sind dagegen schon
tragfähig.

## Heruntergeladene Dateien (lokal, nicht im Repo)

```
sample-catalogs/weinzierl/weinzierl-312-1-ets5.zip                     109.736 B
sample-catalogs/weinzierl/weinzierl-312-ets4.zip                        71.864 B
sample-catalogs/weinzierl/weinzierl-330-ets3.zip                        14.975 B
sample-catalogs/weinzierl/weinzierl-520-1-ets5.zip                     346.398 B
sample-catalogs/weinzierl/weinzierl-630-ets3.zip                     1.208.697 B
sample-catalogs/theben/theben_knx_gesamt.zip                        22.147.114 B
sample-catalogs/theben/theben_ju1_knx.zip                              322.173 B
sample-catalogs/theben/theben_ionprime_pb_knx.zip                      280.628 B
sample-catalogs/elsner/elsner_suntracer_knx_pro.zip                  1.675.251 B
sample-catalogs/elsner/elsner_etr_101.zip                              145.726 B
sample-catalogs/zennio/zennio_cx50_application_program.zip           2.113.072 B
```
