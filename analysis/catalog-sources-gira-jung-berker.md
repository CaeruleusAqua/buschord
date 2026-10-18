# Echte ETS-Produktdatenbanken: Gira, Jung, Berker/Hager

Erhebung für die Härtung von `src/Knx.Core/Catalog/KnxProductCatalog.cs`.
Alle Downloads liegen **außerhalb** des Repos unter `<project-path>\sample-catalogs\{gira,jung,berker}` und werden nicht committet.

- Basis-Commit: `b1e954c`
- Branch: `agents/run4/s7-catalog-gira`
- Erhebungsdatum: 2026-10-18
- Heruntergeladen: **12 Dateien, ~223 MB** (Gira 5, Jung 2, Berker/Hager 5)

## Übersicht der Artefakte

| Hersteller | Produkt/Datenbank | URL | Größe | Layout | Namespace | Besonderheiten |
|---|---|---|---|---|---|---|
| Gira | Produktdatenbank ETS3.0f (Gesamt, 01/2018) | https://download.gira.de/data3/pdb2_ets30f.zip | 147,4 MB | ZIP → `.vd5` (kein XML) | – | ZIP enthält genau eine `pdb2_ets30f.vd5`; diese ist wieder ein ZIP mit Installer-Pfaden (`Program Files (x86)/Ets3/Database/ets.vd_` 480 MB, `.../EIBA sc/eteC/MASK/*.BIN`). **Kein KNX-XML**, Parser kann das nicht lesen. |
| Gira | Raumklimasensoren V2.1 | https://download.gira.de/data3/20049100.zip | 6,2 MB | ZIP → `.knxprod` (gesplittet) | `http://knx.org/xml/project/20` | ZIP→knxprod (Zip-in-Zip). RefId `M-0008` (Gira). `knx_master.xml` + `M-0008.signature` + `M-0008/{Catalog,Hardware,Baggages}.xml` + 4× `M-0008_A-502x-*.xml` (~30 MB entpackt) + `M-0008/Baggages/` mit 26 PNG sowie **3 weiteren ZIPs** (`Help_5020xx_DE.zip`, `Help_5020xx_EN.zip`, `Icons_5020xx.zip`). Bestellnummern `2007 00`, `2006 00`, `2005 00`, `2004 00`. |
| Gira | Einzeldatenbank (vd4) | https://download.gira.de/data3/20219190.vd4 | 160 KB | ZIP → `ets.vd_` (kein XML) | – | `.vd4` = ZIP mit `Ets/Database/ets.vd_` + `MASK0705.BIN`. ETS-internes Binärformat. |
| Gira | Einzeldatenbank (vd4) | https://download.gira.de/data3/10379290.vd4 | 384 KB | ZIP → `ets.vd_` (kein XML) | – | wie oben. |
| Gira | Präsenzmelder Standard 2105 (Sprachvarianten) | https://download.gira.de/data3/21059490.zip | 268 KB | ZIP → 2× `.vd4` | – | Ein ZIP mit **zwei Artefakten** (EN/DE), beide `.vd4` (kein XML). |
| Jung | DALI-Gateway 2098 REG HE, „Datenbank ETS4 - ETS5“ | https://application.jung.de/downloads/download/61285/en → https://downloads.jung.de//Downloads/Produktdownloads/Datenbank%20ETS4%20-%20ETS5/KNX/en_2098REGHE_20180302.vd5 | 10,6 MB | ZIP → `Ets/ets.vd_` (kein XML) | – | `.vd5` trotz Beschriftung „ETS4 - ETS5“ = ZIP mit `Ets/ets.vd_` (24 MB), ETS-Binärblob, **kein XML**. |
| Jung | KNX Multistation 23066 REGHE | https://application.jung.de/downloads/download/61786/en → https://downloads.jung.de//Downloads/Produktdownloads/Datenbank%20ETS4%20-%20ETS5/KNX/en_23066REGHE_V2_20180301.knxprod | 2,1 MB | ZIP → `.knxprod` (gesplittet) | `http://knx.org/xml/project/11` | RefId `M-0004` (Albrecht Jung). `knx_master.xml` + `M-0004.signature` + `M-0004/{Catalog,Hardware}.xml` + **ein** `M-0004_A-8028-12-541F-O000A.xml` (58,8 MB entpackt). Bestellnummer `23066REGHE`, CatalogItems `Multistation`. |
| Berker | RTR mit Tasterschnittstelle 754411xx (V1.1) | https://www.siblik.com/fileadmin/user_upload/03_Support/Downloads/03_KNX_Datenbanken/Berker/7544/754411xx_Berker_RTR_mit_Tsterschnittstelle_V1.1.knxprod | 125 KB | ZIP → `.knxprod` (gesplittet) | `http://knx.org/xml/project/11` | RefId `M-0006`. Enthält `Baggages.xml` + `Baggages/ApplUpdateE102006.txt`. Bestellnummer `754411xx`. |
| Berker | Helligkeits-/Temperatursensor 75492001 (V2.0) | https://www.siblik.com/fileadmin/user_upload/03_Support/Downloads/03_KNX_Datenbanken/Berker/7549/75492001_Helligkeits-_und_Temperatursensor_V2.0.knxprod | 58 KB | ZIP → `.knxprod` (gesplittet) | `http://knx.org/xml/project/11` | RefId `M-0006`. **Kein** `Baggages.xml` (Variante ohne Baggages). |
| Berker | Spannungsversorgung 320 mA 75010009 | https://www.siblik.com/fileadmin/user_upload/03_Support/Downloads/03_KNX_Datenbanken/Berker/7501/75010009_Spannungsversorgung_320mA_REG.knxprod.zip | 36 KB | ZIP → `.knxprod` (Zip-in-Zip) | `http://knx.org/xml/project/11` | Äußerer ZIP enthält genau eine `.knxprod` (Dateiname mit Leerzeichen). |
| Berker | Alarmzentrale 75730010 (V0.3, ETS4) | https://www.siblik.com/fileadmin/user_upload/03_Support/Downloads/03_KNX_Datenbanken/Berker/7573/75730010_Berker_Alarmzentrale_V0.3_ETS4.knxprod | 508 KB | ZIP → `.knxprod` (gesplittet) | `http://knx.org/xml/project/11` | RefId `M-0006`. `Baggages/econEts3.dll` (Plug-In, 641 KB). |
| Berker/Hager | Hager Produktdatenbank gesamt 2021 | https://assets.sc.hager.com/de/-/media/project/hagerdeep/deutschland/hager/b2b-de/software/knx-gebaeudesteuerung/knx-gesamt-produktdatenbanken/hager_produktdatenbank-gesamt-2021.zip | 62,5 MB | ZIP → `.knxprod` (gesplittet) | `http://knx.org/xml/project/20` | RefId `M-0009` (Hager Electro). 444 ZIP-Einträge: 146 XML, 246 PDF, 35 JPG, 6 CHM, 4 DLL, 1 MSI, 3 PNG, 2 CSV, 1 `.signature`. Bestellnummern `TX022`, `TX023`, `TXA111` usw. |

## Was der Parser können muss

Alles Folgende ist an den **tatsächlich geladenen Dateien** beobachtet, nicht theoretisch.

### 1. Gesplittetes Layout statt monolithischer `knxprod.xml` — der Regelfall
**Keine** der 6 echten `.knxprod`-Dateien enthält eine monolithische `knxprod.xml`. Alle nutzen das gesplittete Layout:

```
knx_master.xml                     (Root <KNX>, ns project/11 bzw. project/20)
<M-XXXX>.signature                 (175 Bytes, pro Hersteller)
<M-XXXX>/Catalog.xml
<M-XXXX>/Hardware.xml
<M-XXXX>/Baggages.xml              (optional)
<M-XXXX>/Baggages/...              (beliebige Binärdateien)
<M-XXXX>/<M-XXXX>_A-....xml        (je ein Applikationsprogramm)
```

`PickMainEntry` zieht bei fehlender `knxprod.xml` die größte XML im Wurzelverzeichnis → `knx_master.xml`. Damit bricht die gesamte Kataloglesung, siehe Punkt 2.

### 2. `knx_master.xml` ist **nicht** der Katalog
`knx_master.xml` (Root `<KNX>`) enthält nur die **globale Herstellerliste** `<Manufacturers>` (779 `<Manufacturer>`-Einträge: Siemens `M-0001`, ABB `M-0002`, Jung `M-0004`, Berker `M-0006`, Gira `M-0008`, Hager `M-0009`, …). Es gibt dort **kein** `<CatalogSection>`, **kein** `<Product>`.

Folge für den aktuellen Parser: `Parse()` findet `knx_master.xml`, setzt `Manufacturer` auf den **ersten** Eintrag (also Siemens `M-0001`!) und liefert 0 Produkte, 0 Sections, 0 Programme. Der Parser muss stattdessen:
- die `M-XXXX`-Ordner im ZIP erkennen,
- `<M-XXXX>/Catalog.xml` und `<M-XXXX>/Hardware.xml` als Katalog lesen,
- über `RefId` mit dem `<Manufacturers>`-Eintrag aus `knx_master.xml` (Name, `KnxManufacturerId`) verknüpfen.

### 3. Applikationsprogramm-Datei hat Root `<KNX>`, nicht `<ApplicationProgram>`
Die `_A-*.xml`-Dateien haben Root `<KNX>` und darunter `…/Manufacturer/ApplicationPrograms/ApplicationProgram`. Der aktuelle `attachedPrograms`-Filter prüft `part.Root.Name.LocalName == "ApplicationProgram"` → trifft **nie** zu, Programme werden nie geladen. Ebenso müssen `Product`, `CatalogItem`, `Hardware2Program`/`ApplicationProgramRef` aus **drei getrennten** Dateien über deren Ids gejoint werden.

### 4. Namespace ist `project/…`, nicht `prod/…`
Beobachtet: `http://knx.org/xml/project/11` (ETS4: Jung, Berker) und `http://knx.org/xml/project/20` (ETS5/6: Gira, Hager). Root-Element jeweils `KNX` (ungesplittet wäre `KNX/ManufacturerData`). Der Doku-Kommentar mit `prod/20` passt zu den Realdaten der drei Hersteller **nicht**.

### 5. Zip-in-Zip ist normal
Mehrere offizielle Downloads sind ein ZIP, das genau eine `.knxprod`/`.vd5` enthält (Gira `20049100.zip`, Berker `…knxprod.zip`, Hager Gesamt-ZIP). Der Reader muss eine Ebene absteigen (oder anhand Signatur `PK\x03\x04` das innere Archiv erkennen). Die innere Datei kann Leerzeichen im Namen haben.

### 6. Signaturdateien und 0-Byte-Ordner-Einträge
`<M-XXXX>.signature` (175 B) liegt auf ZIP-Wurzelebene und muss ignoriert werden. Zusätzlich existieren **Verzeichnis-Einträge mit Länge 0 und leerem Namen** (`M-0008/`, `M-0008/Baggages/`) — beim Aufzählen der Entries tolerieren.

### 7. Baggages: Bilder, aber auch Plug-Ins, Hilfe, CSV, verschachtelte ZIPs
`<M-XXXX>/Baggages.xml` (Root `<KNX>`, `<Manufacturer RefId=…><Baggages><Baggage Name= TargetPath=><FileInfo/></Baggage>`) listet beliebige Nutzdaten:
- Bilder: PNG (Gira), JPG (Hager `Baggages/…/img/*.jpg`, `Images/*.png`)
- **verschachtelte ZIPs** (Gira `Help_5020xx_DE.zip`, `Icons_5020xx.zip`)
- **Plug-Ins/Windows-Binaries**: DLL (Hager `KnxDaliGatewayR3.dll`, `TR131_mc/RFDevice.dll`), MSI, CHM-Hilfe, CSV (`RFDevice.csv`)
Die aktuelle Bilder-Heuristik (`IsImage` über alle Entries) findet Bilder, aber `TargetPath` verwendet **Windows-Backslashes** (`TR131_mc\img`), während ZIP-Einträge `/` nutzen — Pfadnormalisierung nötig. Bilder liegen teils auch außerhalb von `Baggages` (Hager entpackt PDFs/JPGs in `M-0009/…`).

### 8. Übersetzungen liegen inline, nicht in Extra-Dateien
Keine separaten Übersetzungsdateien beobachtet. `<Languages><Language Identifier="de-DE"><TranslationUnit RefId=…><TranslationElement RefId=…><Translation AttributeName=… Text=…/>` steckt **in** `Catalog.xml`/`Hardware.xml` (Gira `Catalog.xml`: `TranslationUnit=True`, `Translation=True`). Sprache steht zusätzlich in `knx_master.xml`. `DefaultLanguage` je Produkt variiert (`de-DE`, `en-US`, `fr-FR`).

### 9. Legacy-Formate `.vd1/.vd2/.vd3/.vd4/.vd5` sind **kein** XML
Gira und Jung liefern einen Großteil der Einzeldatenbanken als `.vd4`/`.vd5`. Beobachtet: das sind ZIPs mit genau einem ETS-internen Binärblob `Ets/ets.vd_` (Gira ETS3-Gesamt zusätzlich mit `Program Files (x86)/Ets3/Database/ets.vd_`, 480 MB) — **kein** `knxprod.xml`/`knx_master.xml`. Der aktuelle Reader wirft dort `InvalidDataException("contains no knxprod.xml")`. Sinnvoll: als „nicht unterstütztes Legacy-Format“ melden statt hart zu scheitern, und solche Dateien beim Katalog-Scan überspringen.

### 10. Ein ZIP kann mehrere Artefakte / Sprachvarianten enthalten
Gira `21059490.zip` enthält zwei `.vd4` (EN/DE). Der Reader darf nicht annehmen, dass ein Archiv genau ein Produkt beschreibt.

### 11. IDs/Bestellnummern-Vielfalt
Beobachtet: `RefId` mit Präfix `M-XXXX`, Produkt-Ids wie `M-0008_H-8.20.2F.2F.202007.2000-1-O000A_P-2007.2000`, Hardware2Program-Ids `…_HP-8028-12-541F-O000A`, CatalogItem-Ids `…_CI-23066REGHE-2`. Bestellnummern: Gira `2007 00` (mit Leerzeichen), Berker `754411xx`, Jung `23066REGHE`, Hager `TX022`. Nicht jedes Produkt hat `OrderNumber`/`VisibleDescription`.

## Nicht erreichbar / eingeschränkt

- **Jung, „KNX Produktdatenbank komplett“**: https://www.jung-group.com/en-ASIA/downloads/product-downloads/knx-produktdatenbank-komplett → Kennzeichnung **„NO LONGER AVAILABLE“**. Die Komplettdatenbank ist nicht mehr öffentlich. Einzeldatenbanken sind weiterhin über die jeweilige Produktseite unter `https://application.jung.de/downloads/download/<id>/<lang>` erreichbar (Redirect auf `downloads.jung.de/.../Datenbank ETS4 - ETS5/KNX/…`), aber nur als `.vd5`/`.knxprod` pro Gerät.
- **Gira, ETS4-Gesamtdatenbank** (https://download.gira.de/en?id=2545): Die Seite führt nur die ETS3-Plug-in-Datenbank (`ETS3_Installationsmanager_0312v1.zip`) und den Econ-Patch — keine öffentliche ETS4/5/6-Gesamt-`.knxprod`. Gesamtdatenbank nur als ETS3 (`pdb2_ets30f.zip`).
- **Gira Einzel-Datenbanken** (https://download.gira.de/en?id=3456): 170 ZIPs, 320 `.vd1`, 114 `.vd4`, 56 `.vd2`, 16 `.vd5`, 4 `.vd3` — **kein einziger `.knxprod`-Direktlink**. Moderne Gira-`.knxprod` liegen nur verpackt in `.zip` (z. B. `20049100.zip`).
- Kein Login/Captcha/EULA-Klick war für die beschafften Dateien nötig. Hager nennt auf der Seite Lizenzvereinbarungen, der Download selbst ist aber frei. Jung-Produktseiten liefern die Datenbank-Links erst per JS; direkte Datei-URLs sind über `downloads.jung.de` erreichbar.

## Konsequenzen für den Testkorpus
- Der synthetische Fixture deckte nur den (monolithischen) Idealfall ab. **Reale Herstellerdateien sind gesplittet** (`knx_master.xml` + `M-XXXX/…`), teils mit Zip-in-Zip und Win-Pfaden.
- Mindestens drei Regressionstests nötig: (a) gesplittet `project/11` (Jung/Berker), (b) gesplittet `project/20` (Gira/Hager), (c) ZIP→`.knxprod` bzw. ZIP→`.vd5` (Legacy = sauberer Fehler statt Absturz).
