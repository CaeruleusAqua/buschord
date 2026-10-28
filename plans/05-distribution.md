# Plan — Installation und automatische Updates

[Gesamtplan](00-gesamtplan.md) · [Build, Betrieb und Grenzen](../docs/distribution.md)

- [x] Windows-Setup/Portable und Linux-AppImage/deb der vollständigen Anwendung bauen und lokal prüfen.
- [x] Signierte Feeds, Paketidentität, Rückstufungsschutz und Fehlerwiederholung integrieren und prüfen.
- [x] Sichere Start-/Schließgrenzen, globale Optionen und Schutz offener Projekte prüfen.
- [x] Automatische APT-Aktualisierung mit begrenztem Paketumfang, Prozessschutz und synthetischer Transaktionsprüfung ergänzen.
- [ ] CI-Artefakte und getrennte, gesperrte Veröffentlichungswege prüfen.

Vollständige lokale Anwendungspakete und isolierte Updateprüfungen bestehen.
Installierte APT-Dienst-, Desktopplattform- und Stromausfallabnahme bleiben offen.
Private Signierschlüssel bleiben außerhalb des Repositories;
es erfolgt keine Hostinstallation oder Veröffentlichung ohne Freigabe.
