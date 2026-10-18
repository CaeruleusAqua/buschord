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
| Project archive read/write (`.knxproj`, schema `http://knx.org/xml/project/23`) | bounded root/nested ZIP support, atomic saves and opaque-data retention; real ETS round-trip unverified |
| Topology, group addresses, com object links, undo/redo, project check | working |
| Product databases (`.knxprod`, split and nested layouts) + local catalogue library | working, validated against 109 real vendor files |
| Device parameters (KSXML) with typed editors | static manufacturer types and validated edits; BusChord override persistence, no dynamic evaluation/native ETS parameter export |
| KNXnet/IP | discovery and tunnel handshake live-verified read-only (connect, one group read, clean disconnect); group writes, download and secure tunnelling unverified |
| KNX Data Security (keyring, secure telegrams, secure wrapper) | foundation, not yet wired into the tunnel |
| Semantic export (Turtle/JSON-LD) + OWL/KIM checks | working |
| Reports: CSV, labels (HTML/mail merge), project compare | working |
| UI: ribbon shell, dashboard, ETS-style workspace tabs (catalogue, parameters, communication objects), light/dark theme, German/English | working |
| MCP server for AI agents (stdio, 41 tools) | working; includes manual communication-object links |
| Hawk/IoT device images, building structure (`loc:`) in semantic export | open |

Latest bounded reference-tool analysis and implemented gap fixes:
[REA parity implementation report](analysis/parity-implementation.md).
Archive signatures are preserved but not recomputed; changed signed data may no
longer validate in ETS. Encrypted project archives remain unsupported.

## Quick start

```powershell
dotnet build
dotnet run --project src/Knx.App            # desktop app
dotnet run --project src/Knx.Mcp            # MCP server on stdio
dotnet run --project src/Knx.Mcp -- --list-tools
dotnet run --project src/Knx.Mcp -- --scan-catalogs=<folder>   # validate .knxprod files
```

Useful startup switches for the desktop app: `--open=<file.knxproj>`, `--catalog`,
`--theme=light|dark`, `--lang=de|en`, `--log`.

User manual (German): [docs/manual.md](docs/manual.md).

## Tests

Dependency-free console checks (exit code 0 = pass):

```powershell
dotnet run --project tests/Knx.Core.Tests
dotnet run --project tests/Knx.Bus.Tests
dotnet run --project tests/Knx.Owl.Tests
dotnet run --project tests/Knx.Catalog.Tests     # optional: KNX_CATALOG_SAMPLES=<folder with .knxprod>
dotnet run --project tests/Knx.Mcp.Tests
dotnet run --project tests/Knx.Archive.Tests
dotnet run --project tests/Knx.Parameter.Tests
dotnet run --project tests/Knx.ParameterUi.Tests
dotnet run --project tests/Knx.Linking.Tests
dotnet run --project tests/Knx.SmartLink.Tests
dotnet run --project tests/Knx.CatalogUi.Tests
dotnet run --project tests/Knx.Address.Tests
dotnet run --project src/Knx.Mcp -- --selftest
dotnet run --project src/Knx.App -- --smoke
dotnet run --project src/Knx.App.Tests
```

## Layout

```
src/Knx.Core            project model, knxproj, KSXML, catalogue reader/store, OWL, semantic export
src/Knx.Bus             KNXnet/IP framing, tunnel, routing, management, download, security, recorder
src/Knx.App             Avalonia desktop application
src/Knx.Mcp             MCP server (stdio) for agent control
src/Knx.Reports         CSV, labels
src/Knx.ProjectCompare  project diff
tests/                  dependency-free self checks
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
