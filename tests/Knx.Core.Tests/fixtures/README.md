# Test fixtures

`knx_master.xml` is **synthetic** master data written for this repository. It
contains four datapoint types, eight subtypes for `1.xxx`, and three invented
manufacturers (`M-9001`–`M-9003`). It is not a copy of the ETS6
`knx_master.xml`, of KNX Association master data, or of any manufacturer
catalogue, so it carries no third-party licence.

Generation: none. Edit the file by hand and keep the counts in
`tests/Knx.Core.Tests/Program.cs` in sync. The tests assert the exact version,
type count, subtype count and manufacturer count, so a copied real catalogue
fails instead of silently restoring third-party data.

Policy: fixtures must be reproducible from this file alone. Do not add real
ETS6 exports, manufacturer `.knxprod`/`.vd4`/`.vd5` data, customer projects or
credentials. `.gitignore` keeps the corresponding local paths out of git.
