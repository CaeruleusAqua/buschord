# Plan — Kern, Archive und Hersteller

[Gesamtplan](00-gesamtplan.md) · [Genaue Profile und Grenzen](../docs/status.md)

## Steps

- [x] BCL-only-Projektmodell und Masterdaten implementieren.
- [x] Begrenzten Archivimport, atomare Speicherung und Undo absichern.
- [x] Produkt-/Applikationsauflösung, native Werte und KO-Metadaten korrigieren.
- [x] Validierte Entwürfe, dynamische Texte und begrenzte Strukturprofile ergänzen.
- [x] Native /20-/21-/22-/23-Speicherung einschließlich AES-Retention prüfen.
- [x] Geprüfte Parameter-/Objekt-/Kanalmigration mit Linkentscheidungen absichern.
- [x] Speicherdiagnose, archivierte Maskenprofile und explizite Byte-Teilmengen ergänzen.
- [x] Typisierte absolute/Root-Relative-Ressourcenpuffer mit atomaren Vorschauen prüfen.
- [x] Archivierten Ressourcenindex, Factory-Pufferleser und primären Ladeprozedur-Compiler prüfen.
- [x] Begrenzte M112-/SystemB-Tabellen mit begrenztem nativen Aktivitäts- und Inventarnachweis prüfen.
- [x] Separate Root-Bedingungen und relative Speichervergleiche mit aktueller Quellenbindung prüfen.
- [x] Begrenzte Root-Property-Teilmengen mit vollständigen Quellen, PDT-Rollen und unveränderten Count-Bytes prüfen.
- [x] Statische Root-Code-Beiträge unter expliziter Maskenrichtlinie mit Quellenbindung und unbekannten Bits komponieren.
- [x] Alle unterstützten festen Root-Referenzen und optionale Tabellen aus einer gemeinsamen Auswertung komponieren.
- [x] Begrenzte Root-Bedingungen mit vollständiger fester Referenzpopulation und passenden Tabellen aus derselben Auswertung auflösen.
- [x] Festes Root-Property-Profil aus tatsächlichen Allokationskontrollen initialisieren und mit vollständiger Referenzpopulation formatieren.
- [x] Gemeinsame logische Initialisierung und Root-Writer mit Optionsalternativen und aktuellen Tabellen-Seeds über API/MCP absichern.
- [x] Physische Single-/Zeitfelder und eine quellengebundene Policy-Ausgabe im gemeinsamen Lauf prüfen.
- [x] Mehrstufige überlappende Faltungen mit getrennten Quellenbesitzern und kumulierten Allokationsgrenzen prüfen.
- [ ] Allgemeine Property-/Speicher-Backends und vollständige Geräteimages validieren.
- [ ] Weitere Hersteller-, Plug-in-, Export- und Roundtrip-Profile schließen.

## Offene Profile

MDT, Jung und Gira bleiben verbindlich. Der Jung-SP8-KSP-Import ist auf die
ausgewiesene Applikation und Versionskombination beschränkt. Ein DCA-Manifest
lädt keine Herstellerassembly. VBScript, allgemeine Plug-ins, vollständige
Aktivierung und die Finalisierung von Ladeabläufen bleiben gesonderte Aufgaben.

Native /20-Archive sind mit direkter Linientopologie, Root-, ZIP- und
Projektordnerlayout einschließlich Bearbeiten, Undo und Save/Reopen geprüft.
/22 erhält Segmenttopologie, native Identitäten, Parameter-/Modulzustände und
neue undurchsichtige Attribute in denselben drei Layouts, einschließlich AES.
/21 und /23 bleiben erhalten. Breitere Exportvarianten bleiben offen.

Byte-Vorschauen prüfen explizite native Typen, eindeutige Speicherbesitzer,
bekannte Ausgangsdaten und aktuelle Quellen. Nicht unterstützte Union-, Alias-,
Ressourcen- oder Berechnungsfälle bleiben ohne Teilergebnis.

Die optionale Ressourcen-Vorschau verwendet vorhandene Data-/Mask-Puffer und
trennt absolute Speicherbereiche von logischen LSM-Bereichen. Deklarierte Größe
erzeugt keine Ausgangsbytes. Eine separate Property-Vorschau verwendet vollständige
archivierte Data-/Count-Puffer mit expliziten Masken und eindeutigem PDT für
begrenzte Root-Zahlen-/Auswahlfelder. Count-Bytes bleiben unverändert; Anwendungen
mit weiteren Memory-Besitzern werden wegen offener physischer Aliase verweigert.
Ein separater Property-Arbeitslauf initialisiert alle tatsächlichen Allokationen
seines festen Root-Profils und verarbeitet alle zugelassenen Referenzen aus einer
gemeinsamen Auswertung. Er trennt Initialdaten, Gültigkeit und Sollbytes und
beobachtet archivierte Count-Werte ohne Übernahme. Das erste Profil verweigert
zusätzliche Speicher-/Codebeiträge, relevante Pointer und nicht geschlossene
Quellen; seine positiven Integrationsfälle sind synthetisch.
Allgemeine Property-Initialisierung außerhalb dieses Profils, physische Abbildung,
vollständige Aktivierung und vollständige Ladeabläufe bleiben offene Anforderungen.
Der [gemeinsame logische Arbeitslauf](../docs/status.md#combined-logical-working-stage)
verbindet jetzt Speicher-/Property-Beiträge mit einer Auswertung und eigenen
Bindings. Überlappende Region-/Code- und Pointer-/Factory-Beiträge dürfen auch
in mehreren geordneten Schritten wachsen und Komponenten verbinden. Weitere
Writer, physisches Mapping und vollständige Images bleiben offen.
Die separate statische Code-Komposition erzeugt Arbeitsbytes mit expliziter
Bitmaske. Fehlende Quellbytes bleiben unbekannt; sie werden weder zu bekannten
Ausgangsbytes noch zu Geräte-Leseanweisungen. Parameter-/Tabellenwriter,
Property-/Maskenressourcen und Finalisierung sind dabei noch nicht komponiert.
Ein zusätzlicher Root-Arbeitslauf erfasst die vollständige Referenzpopulation
seines festen Speicherprofils einschließlich inaktiver, Always- und NoStorage-
Dispositionen. Er kombiniert Code, Parameter und passende Tabellen mit einer
gemeinsamen Auswertung; abweichende Tabellen-Ausgangsbytes verweigern den gesamten
Arbeitsstand. Module, weitere Writer und Imagefinalisierung bleiben offen.
Explizite M112-/SystemB-Tabellenfragmente besitzen eigene Quellenbindungen und
ersetzen kein vollständiges Image; genaue Grenzen stehen im Status.
Der separate Compiler unterstützt primäre Product-, Default-
und Merged-Prozeduren mit expliziten Teilefiltern und gebundenen Quelldaten.
Eine separate Prozedurvorschau unterstützt begrenzte Root-Bedingungen und relative
Speichervergleiche. Ein zusätzlicher expliziter Root-Arbeitslauf kombiniert diese
Auflösung mit der vollständigen unterstützten Referenzpopulation und passenden
Tabellen aus derselben Auswertung; alte Vorschauverträge bleiben erhalten.
AP2, weitere bedingte Profile, Modulprozeduren, globale Korrekturen und Finalisierung
sind nicht kompiliert.

## Erhaltungsregeln

Private Testdaten bleiben außerhalb des Repositories. Native Werte, Namen und
undurchsichtige Archivteile erhalten; abgeleitete Anzeigetexte nicht als feste
Overrides speichern. Core bleibt BCL-only; die Skriptlaufzeit ist getrennt.
