# Plan — Installation und automatische Updates

[Gesamtplan](00-gesamtplan.md) · [Build, Betrieb und Grenzen](../docs/distribution.md)

- [x] Windows-Setup/Portable und Linux-AppImage/deb der vollständigen Anwendung bauen und lokal prüfen.
- [x] Signierte Feeds, Paketidentität, Rückstufungsschutz und Fehlerwiederholung integrieren und prüfen.
- [x] Sichere Start-/Schließgrenzen, globale Optionen und Schutz offener Projekte prüfen.
- [x] Automatische APT-Aktualisierung mit begrenztem Paketumfang, Prozessschutz und synthetischer Transaktionsprüfung ergänzen.
- [x] Installierte APT-3.2-Policyfälle isoliert prüfen; eigene Konfiguration und unbeaufsichtigte Conffile-Konflikte korrigieren und als Shell-Verhaltenstest absichern.
- [x] Vollständigen installierten Upgrade auf preview.42, laufende Anwendung, unveränderte Administrator-/Fremdpaketdaten, tatsächlichen Timeraufruf und native DE-/EN-Abläufe in Ubuntu 26.04/WSLg prüfen.
- [x] CI-Artefakte und getrennte, gesperrte Veröffentlichungswege prüfen.

Vollständige lokale Anwendungspakete und isolierte Updateprüfungen bestehen.
Die Windows-/Linux-Paketprüfung und sämtliche Integrationsprüfungen bestehen
im nach `development` übernommenen Stand.
Der installierte APT-Dienst besteht die abgegrenzten Policyprüfungen mit vollem
preview.41-Payload; die 41.x-Fixtures sind keine neuen Anwendungsversionen.
Der vollständige preview.42-Anwendungsupgrade mit korrigiertem Paket und die
installierten nativen Abläufe bestehen; die lokale Abnahme ist keine Veröffentlichung.
Weitere Plattform-/APT-Versionen, AppImage-Installation und Stromausfallabnahme
bleiben offen.
Private Signierschlüssel bleiben außerhalb des Repositories;
es erfolgt keine Hostinstallation oder Veröffentlichung ohne Freigabe.
