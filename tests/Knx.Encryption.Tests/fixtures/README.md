# Test fixtures

No binary fixtures are tracked here. `Knx.Encryption.Tests` builds all
fixtures in memory in `Program.cs`:

- `synthetic-ets6.knxproj` — a synthetic ETS6-shaped project: outer plain ZIP
  with one `P-0001.zip`, which is WinZip AES-256 (AE-1), password `test`. Its
  `0.xml`/`project.xml` use schema `http://knx.org/xml/project/21` with
  fictional devices, spaces, group addresses and no security data. This keeps
  the `/21` reading, password, tamper and save-refusal checks while containing
  no ETS, manufacturer, customer or credential data.
- `synthetic-aes1.knxproj` / `synthetic-aes2.knxproj` — minimal AE-1/AE-2
  archives for the encryption round-trip checks.

Generation: `AesZip`/`PlainZip` in `Knx.Encryption.Tests/Program.cs` write the
archives at test time, so there is nothing to regenerate and no fixture that
can drift from its documented provenance.

Provenance: everything here is written by this repository's tests. Do not add
real ETS6 exports, manufacturer `.knxprod`/`.vd4`/`.vd5` data, customer projects
or credentials; `.gitignore` keeps the corresponding local paths out of git.
