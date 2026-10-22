# BusChord

[![CI](https://github.com/CaeruleusAqua/buschord/actions/workflows/ci.yml/badge.svg?branch=development)](https://github.com/CaeruleusAqua/buschord/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9.0-512bd4.svg)](https://dotnet.microsoft.com/download/dotnet/9.0)

**Open-source building automation engineering.** An independent configuration and
commissioning tool for KNX installations.

BusChord opens, edits, checks and exports building automation projects, browses manufacturer
product databases, and speaks KNXnet/IP on the bus. It is written from scratch in .NET 9
(Core + Avalonia UI + a headless MCP server) and is not affiliated with, endorsed by or
derived from the KNX Association or any tool vendor.

## What it does

| Area | Status |
|---|---|
| Project archive read/write (`.knxproj`, schemas `/21` and `/23`) | bounded root/nested ZIP support, atomic saves and opaque-data retention; `/21` is read-only, `/23` is written; encrypted archives open with the ETS project password but saving is refused; real ETS round-trip unverified |
| Topology, group addresses, com object links, undo/redo, project check | working; project check includes offline bus power and segment/coupler planning (P080–P082) from the project's own hardware data |
| Product databases (`.knxprod`, split and nested layouts) + local catalogue library | working, validated against real vendor files kept outside the repository; persistent SHA-256-revalidated metadata index, lazy application models and warm start |
| Device parameters (KSXML) with typed editors | static manufacturer types and validated edits; BusChord override persistence; no dynamic evaluation or native ETS parameter export; DCA/plug-in applications stay read-only, also when only the project archive knows the application |
| KNXnet/IP | discovery, tunnel and routing handshake live-verified read-only (connect, one group read, clean disconnect); group writes are refused by policy and real downloads are not implemented; secure tunnelling unverified |
| KNX Data Security (keyring, secure telegrams, secure wrapper) | foundation, not yet wired into the tunnel |
| Semantic export (Turtle/JSON-LD) + OWL/KIM checks | working in Core and tests; the checks are not surfaced in the workbench |
| Reports: CSV, labels (HTML in the workbench; CSV or combined HTML through MCP), project report and compare | working; the report carries the latest revision and revision table when present; MCP also writes the KNX acceptance protocol and one deterministic handover package (ZIP with room book, acceptance protocol and SHA-256 manifest) |
| UI: Avalonia 12.1.4, native Workbench (no third-party docking package), object-centred tabs, project portfolio dialog, inspector, monitor/programming/scan tools, light/dark theme, German/English/French/Italian/Spanish/Dutch | working V4 shell; monitor, scan and programming are labelled simulations; report-specific tables remain German/English |
| MCP server for AI agents (stdio, 51 tools) | working; includes manual communication-object links, the read-only download preflight (`download_plan`), the acceptance-protocol export and the read-only handover package |
| Hawk/IoT device images | open |

Latest bounded reference-tool analysis and implemented gap fixes:
[REA parity implementation report](analysis/parity-implementation.md).
Archive signatures are preserved but not recomputed; changed signed data may no
longer validate in ETS.

**Workbench:** the product UI is the native `WorkbenchWindow` with object-centred
tabs, a project portfolio and per-project layout. `tests/Knx.Docking.Tests`
covers floating and redocking tool windows, `tests/Knx.Shell.Tests` covers
project lifecycle and shutdown, and the `Knx.Workbench*` suites cover domain,
sessions, theme, UI state and performance.
The [ETS6 UI comparison](analysis/ux/ets6-panel-recheck.md) distinguishes project
tabs, work panels, device sub-tabs and the properties sidebar; the workbench keeps
those roles distinct instead of presenting one flat tab strip.

## Quick start

```powershell
dotnet build
dotnet run --project src/Knx.App            # desktop app
dotnet run --project src/Knx.Mcp            # MCP server on stdio
dotnet run --project src/Knx.Mcp -- --list-tools
dotnet run --project src/Knx.Mcp -- --scan-catalogs=<folder>   # validate .knxprod files
```

Useful startup switches for the desktop app: `--open=<file.knxproj>`, `--catalog`,
`--theme=light|dark|system`, `--lang=de|en`, `--log`.

To render the workbench without a screen session:

```powershell
dotnet run --project src/Knx.App -- --screenshot=out\main.png --screen=main
dotnet run --project src/Knx.App -- --screenshot=out\demo.png --screen=demoinspector --theme=light
```

`--screen` accepts `main`, `catalog`, `parameters`, `links`, `settings`,
`new-project`, `splash`, `monitor`, `inspector`, `portfolio`, the `demo*` variants and the
`-small`/`-tiny` layouts listed in `src/Knx.App/Screenshot.cs`. The screenshot path
uses a synthetic fixture with isolated settings, never the user's project.

## Documentation

- [User manual (German)](docs/manual.md)
- [Workbench domain contract](docs/workbench-domain.md)
- [Local project backups](docs/project-backup.md)
- [Concept icons](docs/ICONS.md)
- [MCP server](mcp.md)

The user interface and the manuals exist in German and English. The UI also
supports French, Italian, Spanish and Dutch; report-specific tables remain
German/English. There is no RTL support yet.

## Tests

Console checks (no test framework, exit code 0 = pass). These are the projects in
the CI matrix; all run on Linux and Windows except where noted:

```powershell
dotnet run --project tests/Knx.Core.Tests
dotnet run --project tests/Knx.Bus.Tests
dotnet run --project tests/Knx.Owl.Tests
dotnet run --project tests/Knx.Catalog.Tests     # optional: KNX_CATALOG_SAMPLES=<folder with .knxprod>
dotnet run --project tests/Knx.Mcp.Tests
dotnet run --project tests/Knx.Archive.Tests
dotnet run --project tests/Knx.Address.Tests
dotnet run --project tests/Knx.Buildings.Tests
dotnet run --project tests/Knx.Diagnostics.Tests
dotnet run --project tests/Knx.Encryption.Tests
dotnet run --project tests/Knx.Icon.Tests        # needs a display; CI skips it on Linux
dotnet run --project tests/Knx.Linking.Tests
dotnet run --project tests/Knx.Monitor.Tests
dotnet run --project tests/Knx.Search.Tests
dotnet run --project tests/Knx.SmartLink.Tests
dotnet run --project tests/Knx.Parameter.Tests
dotnet run --project tests/Knx.ParameterUi.Tests
dotnet run --project tests/Knx.CatalogUi.Tests
dotnet run --project tests/Knx.Workbench.Tests
dotnet run --project tests/Knx.WorkbenchPerformance.Tests
dotnet run --project tests/Knx.WorkbenchSessions.Tests
dotnet run --project tests/Knx.WorkbenchTheme.Tests
dotnet run --project tests/Knx.WorkbenchUiState.Tests
dotnet run --project tests/Knx.Localization.Tests
dotnet run --project src/Knx.App.Tests
dotnet run --project src/Knx.Mcp -- --selftest
```

`dotnet run --project tests/Knx.ProjectCompare.Tests` covers the compare and
revision engine locally but is not part of the CI matrix. Native-window and app
regressions require a graphical Windows desktop:

```powershell
dotnet run --project src/Knx.App -- --smoke
dotnet run --project src/Knx.App -- --workbench-selftest --theme=light --lang=de
dotnet run --project src/Knx.App -- --workbench-selftest --theme=dark --lang=en
dotnet run --project src/Knx.App -- --screenshot-selftest
dotnet run --project tests/Knx.ParameterUi.Tests -- --native
dotnet run --project tests/Knx.Docking.Tests
dotnet run --project tests/Knx.Shell.Tests
```

`Knx.Docking.Tests` covers floating/redocking tool windows, `Knx.Shell.Tests`
covers project lifecycle and shutdown, the `Knx.Workbench*` suites cover the
domain, sessions, theme, UI state and performance, and `Knx.Localization.Tests`
checks key parity, placeholders and German/English/French/Italian/Spanish/Dutch boundaries. CI skips Docking,
Shell, `ParameterUi --native` and the app self-checks outside Windows.

## Layout

```
src/Knx.Core            project model, knxproj, KSXML, catalogue reader/store/index, portfolio, revisions, OWL, semantic export
src/Knx.Bus             KNXnet/IP framing, tunnel, routing, management, download preflight, security, recorder
src/Knx.App             Avalonia desktop application (WorkbenchWindow, portfolio, panes, dialogs, sessions)
src/Knx.Mcp             MCP server (stdio) for agent control
src/Knx.Reports         CSV, labels, project report, acceptance protocol, handover package
src/Knx.ProjectCompare  deterministic project diff with revision metadata
tests/                  console self checks (Knx.Docking/Shell/Workbench suites included)
analysis/               reverse-engineering notes about the reference tools and catalogue sources
```

## Names and trademarks

BusChord is an independent project. *KNX* and *ETS* are trademarks of the KNX Association;
they appear here only descriptively, to state which bus protocol and which file formats are
supported. No KNX Association code, schema file, product database or documentation is
redistributed with this repository. Product databases downloaded for testing stay outside
version control and are the property of their manufacturers.

## Contributing / Licence

Build steps, branch model, test commands and PR rules: [CONTRIBUTING.md](CONTRIBUTING.md).
Everyone participating follows the [Code of Conduct](CODE_OF_CONDUCT.md); report
vulnerabilities privately as described in [SECURITY.md](SECURITY.md).
Pull requests require accepting the [Contributor License Agreement](CLA.md)
(a one-line comment, checked automatically).

Source code: [Apache License 2.0](LICENSE), copyright 2026 BusChord contributors.
The CLA grants the project the right to relicense future distributed versions;
published releases stay under the licence they shipped with.
*KNX* and *ETS* remain trademarks of the KNX Association; see the trademark note above.
