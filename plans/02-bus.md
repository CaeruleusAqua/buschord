# Plan — KNXnet/IP, Download und Secure

[Gesamtplan](00-gesamtplan.md) · [Aktueller Funktionsumfang](../docs/status.md#network-and-commissioning)

## Steps

- [x] KNXnet/IP-Framecodec, Discovery, Tunnel und Routing implementieren.
- [x] cEMI-, Management- und DPT-Grundlagen mit Byte-Vektoren und Loopback prüfen.
- [x] Abbruch, Ressourcenfreigabe und erneuten Tunnelaufbau absichern.
- [x] Monitor-, Scan- und Programmier-Demos klar kennzeichnen.
- [ ] Positiven Linien-Scan und reale Transport-Interoperabilität prüfen.
- [ ] Herstellerimage, Management, Download, Verify und Neustart durchgängig abnehmen.
- [ ] Secure-Tunnel, Data Security und sichere Inbetriebnahme vollständig integrieren.

## Hardwareentscheidung

Reale Hardwaretests sind seit 2026-10-26 auf Nutzerwunsch zurückgestellt, da kein
isolierter Testaufbau vorhanden ist. Diese Prüfungen gehören derzeit nicht zum
auszuführenden Testumfang. Simulation und Loopback ersetzen die spätere Abnahme nicht.

## Spätere Abnahme

Protokollfelder, Framegrößen und APCI-Zuordnung mit einem freigegebenen Gerätetest
prüfen. Timeout, Sequenzfehler, Abbruch, Wiederverbindung und Verify einschließen.
Discovery oder ein erfolgreicher Verbindungsaufbau beweisen keinen Download.

Keine echten Bus-Schreibzugriffe, Downloads, Programmierung, Neustarts oder
Secure-Inbetriebnahme ohne ausdrückliche Freigabe eines isolierten Testumfangs.
