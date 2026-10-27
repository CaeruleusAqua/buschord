# BusChord

BusChord provides tools for KNX project editing, catalogues and building automation workflows. Available features depend on the selected branch and project.

## Build

Use the .NET SDK required by `global.json`, when present, and the target frameworks declared in the project files. Restore dependencies and build from this directory:

```powershell
dotnet restore
dotnet build -c Release
```

The desktop application and MCP server can be started independently:

```powershell
dotnet run --project src/Knx.App -c Release
dotnet run --project src/Knx.Mcp -c Release
```

Console check projects live under `tests/`; run the checks relevant to a change with `dotnet run --project <project-directory> -c Release`. Desktop checks require a graphical session.

## Documentation

- [Product catalogues](catalog.md)
- [MCP server](mcp.md)
- [Contributing](CONTRIBUTING.md)
- [Security](SECURITY.md)
- [Code of conduct](CODE_OF_CONDUCT.md)

## Limits

File formats, catalogues and device-specific behaviour have separate support limits. Preserve original project files and review changes before saving. An offline result does not establish device compatibility or authorize a bus write.

## Licence and notices

BusChord is an independent project. *KNX* and *ETS* are trademarks of the KNX Association;
they appear here only descriptively, to state which bus protocol and which file formats are
supported. No KNX Association code, schema file, product database or documentation is
redistributed with this repository. Product databases downloaded for testing stay outside
version control and are the property of their manufacturers.

Source code: [Apache License 2.0](LICENSE), copyright 2026 BusChord contributors.
*KNX* and *ETS* remain trademarks of the KNX Association; see the trademark note above.

Protected archives use the built-in protection profile and the project password.
A local `archive-protection.json` in the application data folder can override the
profile; `BUSCHORD_ARCHIVE_PROTECTION` selects an alternative local file.
