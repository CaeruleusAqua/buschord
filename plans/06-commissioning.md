# Plan — Gezielte Geräteabnahme

[Bus-Plan](02-bus.md) · [Produktgrenzen](../docs/status.md#network-and-commissioning)

- [x] Nutzerfreigabe für Programmierung und Schreiben an einem bestimmten Taster aufnehmen; Projektkopie außerhalb des Repositories sichern.
- [x] Ziel aus Produktreferenz und Gruppenverknüpfungen bestimmen; Maskenversion am Gerät lesen.
- [x] Adressierte Management-Verbindung mit Sequenzen, ACKs, Abbruch und strikter Antwortzuordnung ergänzen und prüfen; Bus-Suite mit 524 Checks bestanden.
- [x] Geräteidentität und Applikationsversion abgleichen; Programmierstatus und Device-Control lesen. Vollständiger Konfigurationsvergleich bleibt beim Image-Schritt.
- [x] Programmiermodus schreiben, rücklesen und über eine frische Verbindung wiederherstellen; anschließend Status und Applikation separat unverändert rücklesen sowie Temperaturantwort vom selben Gerät empfangen.
- [ ] Vollständiges passendes Geräteimage, Ladeprozedur und Wiederherstellung vor einem Applikationsdownload validieren.
- [ ] Download, Verify und Wiederanlauf am freigegebenen Ziel nachweisen; tatsächliche Grenzen dokumentieren.

Die Freigabe gilt für den vom Nutzer benannten Taster in der vorhandenen Installation.
Andere Geräte, globale Gruppenbefehle, Adressänderungen und Werksreset sind kein Testziel.
Private Projekte, Kennwörter, Netzwerkdetails und Rohmitschnitte bleiben außerhalb des Repositories.
Die bisherige allgemeine Hardware-Abnahme bleibt offen; ein Einzelgerätetest ersetzt sie nicht.

Der [Konsolenpfad und die tatsächlichen Grenzen](../docs/commissioning.md) sind dokumentiert.
Das ausgewählte Altprofil enthält 37 Speicher-Unions; der aktuelle Image-Builder
verweigert diese Quelle. Ein vollständiger Applikationsdownload wurde nicht ausgeführt.
