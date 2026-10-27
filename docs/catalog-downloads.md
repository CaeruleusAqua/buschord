# Herstellerkataloge beziehen

Im Katalog öffnet **Herstellerkataloge beziehen** die Quellenverwaltung.
Das Öffnen des Dialogs startet keinen Abruf. Vorhandene Kataloge können weiterhin
über den lokalen Dateiimport eingelesen werden.

## Hersteller und Quellen

**Herstellerverzeichnis aktualisieren…** lädt nach ausdrücklicher Bestätigung das
[offizielle Verzeichnis](https://www.knx.org/community/manufacturers) und die
Herstellerkennungen in einen lokalen Cache. Die Liste enthält auch historische
Kennungen. Verzeichniseinträge und Kennungen können dieselbe Firma beschreiben;
ihre Summe ist keine Zahl unterschiedlicher aktiver Hersteller.

Eine Auswahl geprüfter Quellenprofile ist bereits ohne Verzeichnisabruf verfügbar.
Hersteller ohne Direktquelle behalten ihre offizielle Website als Ausgangspunkt.
Ein Quellenprofil kann nur eine Produktfamilie, einen älteren Datenstand oder
eine manuelle Downloadseite abdecken. Es ist keine Zusage eines vollständigen
und aktuellen Herstellerportfolios. Die Oberfläche nennt Quelle, Umfang,
Dateiformat, Prüfdatum und Bedingungen.

## Berechtigung je Quelle

Ein öffentlicher Downloadlink belegt zunächst die Erreichbarkeit. Er erteilt
keine zusätzlichen Nutzungs- oder Weitergaberechte. Prüfen Sie die verlinkten
Quellenbedingungen und den konkreten Produktumfang.

- Bei bestätigter Herstellerfreigabe wird die Zustimmung zu diesen Bedingungen
  lokal gespeichert.
- Bei ungeklärten Rechten kann eine eigene bereits vorhandene Genehmigung mit
  einem quellspezifischen Nachweisvermerk dokumentiert werden. Der Vermerk sollte
  Genehmigungsgeber, Datum und erfasste Produkte nennen. Er ist keine neue Lizenz
  und wird nicht als Herstellerfreigabe ausgewiesen.
- Ausdrücklich eingeschränkte Quellen bleiben für den automatischen Abruf
  gesperrt. Eine allgemeine Zustimmung hebt diese Einschränkung nicht auf.
- Login- und manuelle Quellen öffnen die offizielle Website. Die Anwendung
  übernimmt keine Browseranmeldung und akzeptiert keine Portalbedingungen.

Ändern sich Quelle, erfasster Umfang oder Bedingungen, wird eine erneute
Erklärung benötigt. **Widerrufen** sperrt zukünftige Abrufe; bereits importierte
Kataloge werden dadurch nicht gelöscht. Persönliche Vermerke bleiben lokal.

## Herunterladen und importieren

Nach Auswahl und Freigabe startet **Auswahl herunterladen und importieren** den
Vorgang. **Alle freigegebenen herunterladen** verarbeitet die lokal freigegebenen
Direktquellen. Fortschritt und Abbruch gelten für den laufenden Stapel.

Unterstützt werden direkte `.knxprod`-Archive und eine äußere ZIP-Ebene mit
mehreren solchen Dateien. Alte VD-Datenbanken, Installer, Firmware und Plug-ins
werden übersprungen beziehungsweise als nicht unterstützt gemeldet. Es wird
keine Herstellersoftware ausgeführt und kein Gerät am Bus verändert.

HTTPS-Ziele und Weiterleitungen sind auf die hinterlegten Quellhosts begrenzt.
Dateigröße, gesamter Netzwerkverkehr, Archivpfade, Expansion und XML werden vor
dem Import geprüft. Pro Host läuft höchstens eine Übertragung; insgesamt sind
bis zu drei gleichzeitig möglich. Eine vorhandene Signaturdatei ist keine
kryptografische Vertrauensbestätigung.

Byte-identische Kataloge werden übersprungen. Bei geänderten Daten derselben
Katalogidentität bleibt der installierte Katalog erhalten; der Bericht nennt
den Konflikt. Neue Applikationen ändern keine bereits projektierten Geräte.
Erfolgreiche Importe aktualisieren die Bibliotheksansicht automatisch.
Ein Abbruch erhält bereits abgeschlossene Importe und beendet die verbleibenden
Arbeiten. Das Ergebnis wird je Datei und Quelle angezeigt.

## Lokale Ablage

Registry, Erklärungen, Herkunft und Berichte liegen standardmäßig unter
`%LOCALAPPDATA%\BusChord\catalog-downloads`. Der Katalogbestand verwendet weiterhin
die konfigurierte Katalogbibliothek. Berichte enthalten Quelle, Zeitpunkt,
Datei-Hash, Ergebnis und die verwendete Erklärung. Temporäre Übertragungs- und
Prüfdateien werden nach dem Vorgang aufgeräumt.

Die Anwendung liefert keine kopierte vollständige Herstellerdatenbank oder
Herstellerarchive mit aus. Die lokalen Daten und Dateien werden nicht als
öffentlicher Katalogspiegel hochgeladen.
