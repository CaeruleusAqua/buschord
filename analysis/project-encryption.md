# G7: Verschlüsselte ETS6-Projekte (`*.knxproj`)

> **Nachtrag 2026-10-21:** Die Workbench fragt beim Öffnen nach dem ETS-Projektpasswort
> (nicht dem ZIP-Passwort), leitet daraus den WinZip-AES-Schlüssel ab und liest
> Schema `/21` sowie `/23`. `Knxproj.IsPasswordError` trennt fehlendes/falsches
> Passwort von Formatfehlern. Die Datei bleibt schreibgeschützt; ein Speichern
> verschlüsselter Archive ist weiterhin gesperrt.

Read-only-Analyse vom 2026-10-18. Keine Passwörter geraten, keine Daten entschlüsselt,
keine Datei verändert. Die Nutzerdatei
`<project-path>/project.knxproj` (SHA-256
`369de08cd274cd67d0c0331a01bd3107f5ccec49ca5b9ea6f7dc61ebb66ad05e`, 27.231.898 B)
wurde nur gelesen.

## Ergebnis in einem Absatz

Der äußere Container ist ein normales ZIP; das Projekt liegt in `P-0498.zip` und ist dortmit **WinZip AES-256 (AE-1)** verschlüsselt. Das ist kein ETS-eigenes Verfahren, sondern
der Standard aus PKWARE APPNOTE/WinZip. ETS6 verwendet als ZIP-Passwort nicht das
Nutzerpasswort selbst, sondern einen daraus abgeleiteten Schlüssel:
`Base64(PBKDF2-HMAC-SHA256(UTF-16LE(Passwort), "21.project.ets.knx.org", 65536, 32))`.
Mit dem Projektpasswort ist ein legitimer Lesepfad ohne Knacken möglich; ohne Passwort
gibt es keinen (kein Key-Escrow, keine Hintertür). Für `project.knxproj` kommt erschwerend
hinzu: die Datei nutzt Schema `http://knx.org/xml/project/21`; der Reader unterstützt
`/21` und `/23`, der Writer schreibt weiterhin `/23`. Entschlüsselung und `/21`-Lesen
sind damit möglich, das Archiv bleibt aber schreibgeschützt.

## Beobachteter Container (ohne Geheimnisse)

Äußeres ZIP, 155 Einträge. Projektbezug:

| Eintrag | Größe | Methode | verschlüsselt |
|---|---:|---|---|
| `knx_master.xml` | 1.106.027 | Deflate | nein |
| `P-0498.signature` | 172 | Deflate | nein |
| `P-0498.zip` | 18.182.011 | **Stored (0)** | nein |

`knx_master.xml`, Zeile 2: `<KNX xmlns="http://knx.org/xml/project/21">`.
Der Projektname ist nicht sichtbar (nur in `project.xml`, verschlüsselt).

Inneres `P-0498.zip` (SHA-256
`d608616610ffe52681f423f8d36adafd97be0450b8b1e075b52d2e9ca7160a31`,
16 Einträge): Verzeichniseinträge sind unverschlüsselt, alle Dateneinträge tragen
General-Purpose-Flag `0x0801` (Bit 0 = encrypted, Bit 11 = UTF-8) und Methode `99`
(AES). Betroffen sind `0.xml` (686.484 B), `project.xml` (668.016 B), acht
`AddinData/*.dat` und drei `UserFiles/*.etsapp`.

AES-Extrafield `0x9901` (7 Byte Nutzdaten) bei allen Dateneinträgen:

- Version `1` = **AE-1** (CRC im Header bleibt echt, hier z. B. `0x67e4e9bc`)
- Vendor `"AE"`
- Strength `3` = **AES-256**
- tatsächliche Kompressionsmethode `8` = Deflate

Header von `0.xml` (erste 48 Byte, lokal):

```
50 4b 03 04 14 00 01 08 63 00 7c 88 52 5d bc e9 e4 67
68 0e 01 00 94 79 0a 00 05 00 2f 00 30 2e 78 6d 6c
01 99 07 00 01 00 41 45 03 08 ...
```

Damit steht fest: `0.xml` ist nicht lesbar, bevor das AES-Passwort bekannt ist. Es gibt
keine unverschlüsselte Kopie im Container.

## Verifizierter Leselauf der Nutzerdatei

Mit dem vom Nutzer genannten ETS-Projektpasswort (nicht im Repository hinterlegt)
lädt `Knxproj.Load` die Datei vollständig: Name `Lostau`, ID `P-0498`,
1 Installation, 67 Geräte, 636 Gruppenadressen. Es wurde nur gelesen; keine
Projektdatei verändert oder entschlüsselt gespeichert.

## Format

### Ebene 1: ZIP / WinZip AES

- PBKDF2-HMAC-SHA1, 1.000 Iterationen, Salt 16 Byte (bei AES-256)
- abgeleitet werden 66 Byte = 32 Byte AES-Key + 32 Byte HMAC-Key + 2 Byte Password-Verifier
- AES-256 im CTR-Modus, HMAC-SHA1 über den Chiffretext, davon 10 Byte am Ende
- pro Eintrag: `Salt(16) || Verifier(2) || Chiffretext || HMAC(10)`
- AE-1 prüft zusätzlich den CRC-32; AE-2 setzt ihn auf 0. Beide sollten gelesen werden.

Das ist durch die Header der vorliegenden Datei belegt und deckt sich mit der
Referenzimplementierung `pyzipper` (`WZ_SALT_LENGTHS[3] = 16`, `count=1000`, `dkLen=66`,
`hmac_size=10`).

### Ebene 2: ETS6-Passwortableitung

Das ZIP-Passwort ist ein ASCII-String:

```
access_key = Base64( PBKDF2-HMAC-SHA256(
    password = UTF-16LE(Projektpasswort),
    salt     = "21.project.ets.knx.org",
    iterations = 65536,
    dklen    = 32 ) )
```

Beispielvektoren (öffentlich, lokal nachgerechnet und bestätigt):

| Passwort | abgeleitetes ZIP-Passwort |
|---|---|
| `a` | `+FAwP4iI7/Pu4WB3HdIHbbFmteLahPAVkjJShKeozAA=` |
| `test` | `2+IIP7ErCPPKxFjJXc59GFx2+w/1VTLHjJ2duc04CYQ=` |

ETS4/5 nutzen dagegen ZipCrypto (PKWARE) mit dem rohen UTF-8-Passwort; das betrifft
diese Datei nicht.

## ETS-Evidenz (REA, statisch)

Installation: `C:\Program Files (x86)\ETS6`, ETS/Assemblies Version **6.4.8718.0**.
Keine Assembly wurde geladen oder ausgeführt.

| Artefakt | SHA-256 | Evidence | Befund |
|---|---|---|---|
| `Knx.Ets.Common.dll` | `4d4fcd3b0aed672979aa45b4642a5af772e6dcf0b0388062a96d8ee992f86ecd` | `ev_53aa669fc99d8456b5388d8e04d34fec5c3c4cc174925f3736b2ad3f3e5518a2`, Parent `ev_7c25ec227a12b5a7644a99a8b64000c1c2f9524c31e702138598c716bc5f1b7f` | 389 Typen; `Knx.Ets.Common.Zip.*` und `Knx.Ets.Common.Security.*` |
| dito, Methoden/Konstanten | dito | `ev_e7f434e97001b78143856ed235a29f20236139223b5f296dadcee90456fe8ebc` | `ZipProtection` (Rfc2898, Salt 16, 1000, TripleDES), `Encryptor.GetPasswordKey` (65536, 32), `KeyProviderHelper` |
| dito, Felder/Aufrufe | dito | `ev_4411a3d3a604af74a11d82ffd042e8d93d8da4c17c208574f6a932c9accbd34d` | `ZipWrapper` → `Ionic.Zip.ZipFile`; `EncryptionAlgorithm` = `None`/`PkZip`/`WinZipAes256`; `SetEncryption`/`set_Password`; `EtsZipExtractorBase` setzt `IKeyProvider.get_Key` als ZIP-Passwort |
| dito, Key-Ableitung | dito | `ev_5644aa951b6e444a6785be661840631e9bcdf70150fcd45fcaf7aa412bf8eaf7` | `FromPasswordAccessKeyProvider` → `HashValidator.CalculateAccessKey` → `Encryptor.GetPasswordKey`; `LegacyPasswordKeyProvider` separat |
| dito, Passwortspeicher | dito | `ev_6d0c1e44a69e5ce8f0752549c999dd4e761bb946dec356b8837ef1a582355e5b` | `SecurePasswordReader/Writer` (AES-CCM, PBKDF2-SHA256) = ETS-interner Credential-Store, **nicht** das Archivformat |
| `Knx.Ets.ProjectArchive.dll` | `43d2a1bb74d78e12a93c260ee28ff6facffe9f6b48d79b6b87bf136fe84bc51f` | `ev_505eba14e99210cac2f1c4641a8f93491509745680c46285ad77bccfcf53e774`, Parent `ev_027b3de179d27e3b4f9ab967cfaa73d9d632bfb34904b8e7be72786c898e6c6d` | Projektarchiv-Dienste; keine Krypto-Implementierung |
| `Knx.Ets.Xml.ObjectModel.dll` | `db6f21679d046eaef4909121a553af52fa377fa582552faca6ec0fed5f7f51e1` | `ev_b8b92c3a2bfe2f2d76530f287995d9b8d58669abc71c62011f2c2a42c3886e76` | Member-Inspektion nicht verfügbar (Metadatenbudget); nur Identität |
| `Knx.Ets.ObjectModel.dll` | `73ffaaa97640a4207819c182101d23d3da73d4c983056f5bca5881c0f02eda8a` | Member-Inspektion nicht verfügbar | `ProjectPassword`/`ChangeProjectPassword` nur per String-Scan gesehen, nicht evidenzfähig |
| `Ionic.Zip.dll` (DotNetZip) | `3b3e541682e48f3fd2872f85a06278da2f3e7877ee956da89b90d732a1eaa0bd` | Version `1.9.1.8` | Schreibt/liest WinZip AES (AE-1) |

Wichtig: `ZipProtection` (Rfc2898/TripleDES) ist ein anderer Baustein als das
Archiv-AES. Es schützt gespeicherte Passwörter, nicht `0.xml`. Die Evidenz für das
Archivformat sind die Header der Datei plus `ZipWrapper`/`Ionic.Zip`/`WinZipAes256`.

## Bekannt / abgeleitet / unbekannt

**Beobachtet**

- Containeraufbau, Einträge, Flags, Methode 99, AE-1, AES-256, Deflate.
- ETS6 nutzt DotNetZip und setzt `ZipFile.Password` aus einem `IKeyProvider`.
- Die im Passwortpfad erzeugten Key-Provider (`DirectPasswordKeyProvider`,
  `FromPasswordAccessKeyProvider`, `LegacyPasswordKeyProvider`) werden ausschließlich
  aus dem eingegebenen Passwort konstruiert; kein Schlüsseldatei-, Zertifikats- oder
  Hardwarebezug in den Aufrufketten sichtbar.
- KNX-Dokumentation: Export wird verschlüsselt; ohne Passwort keine Wiederherstellung,
  "no hidden master keys".
- Öffentliche Implementierung `xknxproject` liest geschützte ETS4/5/6-Projekte mit
  genau dieser Ableitung; Testvektoren lokal reproduziert.

**Abgeleitet (hoch, aber nicht am Nutzerfile verifiziert)**

- Der Salt `21.project.ets.knx.org`, UTF-16LE und die 65.536 Iterationen sind die
  exakte ETS6-Ableitung. ETS-IL zeigt PBKDF2-SHA256/65536/32/Base64; der Salt ist
  Dotfuscator-verschleiert. Öffentlicher Code und Testvektoren stützen ihn.
- `0.xml` liegt im Schema `/21` (aus `knx_master.xml`; inneres XML nicht lesbar).

**Unbekannt**

- Ob das Projektpasswort dem Nutzer bekannt ist (nicht geprüft, nicht geraten).
- Ob ETS das Passwort für lokale Projekte im eigenen Store ablegt. Nicht untersucht;
  ein Auslesen wäre Key-Extraktion und ist für BusChord nicht nötig.
- Ob alle Einträge dasselbe Passwort nutzen (Header identisch; ohne Passwort nicht
  entschlüsselbar).
- Schreib-/Re-Encrypt-Parität (AE-1/AE-2, CRC-Behandlung, exakte ETS-Bytefolge).
- Verhalten neuerer ETS6-Schemata (`/23`) mit AES: gleicher Codepfad anzunehmen, nicht
  belegt.

## Legitimer Lesepfad?

**Ja, mit Passwort.** Der Nutzer gibt sein Projektpasswort ein; BusChord leitet daraus
das ZIP-Passwort ab, prüft Verifier und HMAC und liest `0.xml`. Das ist normale
Password-based Encryption, kein Knacken, keine Umgehung.

**Nein, ohne Passwort.** Es gibt keinen im Container gespeicherten Schlüssel. Ein
Wörterbuch-/Brute-Force-Angriff ist wegen ≤ 20 ASCII-Zeichen technisch attraktiv, aber
nicht Teil dieser Aufgabe und wird nicht durchgeführt.

## Empfehlung: (a) Passwortabfrage + Entschlüsselung

1. Beim Öffnen erkennen: inneres `P-*.zip` mit Flag-Bit 0 und Methode 99 / Extra `0x9901`.
2. Einmalig Passwort abfragen (pro Archiv, nicht pro Eintrag).
3. ZIP-Passwort ableiten (Parameter oben), AES-Einträge lesen, MAC prüfen.
4. Nur lesend öffnen. Ein verschlüsseltes Projekt darf **nicht** gespeichert werden,
   solange kein Re-Encrypt implementiert ist; sonst würde BusChord entweder Klartext
   schreiben oder das Passwort verlieren.
5. Falsches Passwort von beschädigter Datei unterscheiden: Verifier/MAC-Fehler =
   falsches Passwort, Deflate-/Strukturfehler = beschädigt.
6. Bibliothek: `System.IO.Compression` (net9) kann AES nicht. Kleinste Optionen:
   SharpZipLib oder DotNetZip als Paket (AE-1-Lesen testen) oder ein eigener Reader
   (Central Directory, AES-CTR, HMAC, Inflate; ca. 120 Zeilen). Wegen nur einer
   benötigten Richtung ist der eigene Reader vertretbar, ein gepflegtes Paket ist
   vorzuziehen.
7. **Zusätzlich Schema `/21` unterstützen.** Sonst schlägt `project.knxproj` nach dem
   Entschlüsseln weiter an `Knxproj.FromXml` fehl ("namespace /23").

Empfehlung (b) greift nur, wenn der Nutzer kein Passwort hat: dann sauber ablehnen,
nicht versuchen.

## Exakte Meldung, die BusChord heute zeigen sollte

Heute wirft `Knx.Core.ProjectArchive.ArchiveState.ValidateZip` (vollständig):

```
Encrypted project archives are not supported. Supported format: unencrypted ZIP with
root 0.xml or one unencrypted P-*.zip containing 0.xml; ZIP64 and multi-volume archives
are not supported.
```

Das ist korrekt, aber wenig hilfreich. Vorschlag (Exception-Typ bleibt
`InvalidDataException`, gleiche Stelle):

```
Encrypted ETS6 project (WinZip AES-256). Reading it requires the project password;
BusChord does not support password-protected projects yet. Export the project from
ETS6 without a password. Additionally, this file uses schema
http://knx.org/xml/project/21; the reader supports /21 and /23, but encrypted projects remain read-only.
```

Der Zusatz zum Schema ist wichtig, damit niemand nach erfolgreicher Entschlüsselung
einen zweiten, unerwarteten Fehler erhält. Sobald `/21` und AES unterstützt sind,
entfällt der jeweilige Satz.

## Benötigte Testdaten

1. **Öffentliches ETS6-Original**: `xknxproject`-Fixture `testprojekt-ets6.knxproj`
   (Passwort `test`, Schema 21) für Interop. Lizenz prüfen (Repo GPL-2.0); nicht
   ungeprüft vendorn, sondern als externer Test/Download.
2. **Eigene synthetische Fixtures**: äußeres ZIP + `P-XXXX.zip` mit `0.xml`/`project.xml`,
   AES-256/AE-1, abgeleitetes Passwort. Fälle: richtig, falsch, korrupter MAC, AE-2,
   mehrere Einträge. Diese Fixtures prüfen nur die eigene Implementierung, nicht
   ETS-Parität; deshalb zusätzlich (1).
3. **Passwort-Vektoren** aus diesem Report als Unit-Checks der Ableitung.
4. **Unverschlüsseltes `/21`-Projekt** vom Nutzer (ETS6-Export ohne Passwort) für die
   Schema-Erweiterung; `project.knxproj` selbst darf nicht eingecheckt werden.
5. **ETS4/5-ZipCrypto-Projekt** (öffentliches Fixture, Passwort `test`) nur, falls
   Legacy-Lesen mit abgedeckt werden soll.

## Grenzen dieser Analyse

- Die Nutzerdatei wurde nicht entschlüsselt; alle Aussagen zum Inneren stützen sich auf
  Header und Verzeichnisstruktur, nicht auf Klartext.
- Der Salt der ETS-Ableitung ist aus öffentlicher, funktionierender Implementierung
  übernommen und durch Testvektoren bestätigt, nicht aus ETS-IL gelesen.
- `Knx.Ets.ObjectModel.dll` und `Knx.Ets.Xml.ObjectModel.dll` waren für
  Member-Inspektion zu groß; Aussagen zu deren Passwortlogik sind deshalb nicht
  evidenzbasiert und werden hier nicht als Befund verwendet.
- Keine Aussage über ETS-Versionen außer 6.4.8718.0 und Schema `/21`.
