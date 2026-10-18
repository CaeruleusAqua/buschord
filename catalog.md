# Product catalogues (`.knxprod`)

Reader for original KNX product databases so the clone can list manufacturers, products,
catalogue trees and application programs and create devices from them.

## Format

A `.knxprod` is a ZIP whose main part is `knxprod.xml` (sometimes named differently; the
reader takes the ZIP entry called `knxprod.xml`, else the largest top-level XML). Older
archives additionally carry device XMLs as separate entries (`M-0083/M-0083_A-….xml`); those
whose root is `ApplicationProgram` are merged into the program list.

```text
KNX
└─ ManufacturerData
   └─ Manufacturer @RefId
      ├─ Catalog / CatalogSection (@Id, @Name, @Number)
      │  ├─ CatalogSection …            nested sections
      │  └─ CatalogItem @Id @Name @VisibleDescription @ProductRefId @Hardware2ProgramRefId
      ├─ Hardware / Hardware @Id @Name
      │  ├─ Products / Product @Id @Text @OrderNumber @VisibleDescription
      │  └─ Hardware2Programs / Hardware2Program @Id / ApplicationProgramRef @RefId
      ├─ ApplicationPrograms / ApplicationProgram @Id @Name @ApplicationNumber @ApplicationVersion
      │  └─ Static / ParameterTypes, Parameters, ComObjectTable / ComObject …
      └─ Languages / Language @Identifier
         └─ TranslationUnit @RefId / TranslationElement @RefId
            └─ Translation @AttributeName @Text
```

The namespace is `http://knx.org/xml/prod/20` for current files; 14/17/19 occur. The reader
matches on local names only, so namespaces, unknown elements and missing optional parts
(`Catalog`, `Languages`, `Hardware`, `ApplicationPrograms`) are tolerated. The only hard
failure is "not a catalogue": no `<Manufacturer>` element at all (`InvalidDataException`).

Texts: attributes such as `Text`, `Name`, `VisibleDescription`, `FunctionText` carry literal
text in the element's `DefaultLanguage`; translations live in the per-language translation
units keyed by element id and attribute name. `CatalogText.Pick` prefers `de-DE`, then `de`,
then `en-US`, then `en`, then the literal attribute value.

Images are reported as entry names only (`Images`); they are not extracted.

## Verified vs inferred

Verified against the KNX schema embedded in `C:\Program Files (x86)\ETS6\Knx.Ets.Xml.ObjectModel.dll`
(namespace `http://knx.org/xml/project/23`, same model as the product database) and the type
`Knx.Ets.Xml.ObjectModel.CatalogSection`:

- `KNX` → `ManufacturerData` → `Manufacturer`, `Manufacturer@RefId`
- `Manufacturer` children `Catalog`, `ApplicationPrograms`, `Hardware`, `Languages`, `Baggages`
- `CatalogSection` (`@Id`, `@Name`, `@Number`, nested `CatalogSection`, `CatalogItem`)
- `CatalogItem` (`@Id`, `@Name`, `@Number`, `@VisibleDescription`, `@ProductRefId`, `@Hardware2ProgramRefId`)
- `Hardware@Id/@Name`, `Products/Product` (`@Id`, `@Text`, `@OrderNumber`, `@VisibleDescription`)
- `Hardware2Program@Id`, `ApplicationProgramRef@RefId`
- `ApplicationProgram` (`@Id`, `@Name`, `@ApplicationNumber`, `@ApplicationVersion`) and its
  `Static` children `ParameterTypes/ParameterType`, `Parameters/Parameter`, `ComObjectTable/ComObject`
- `ComObject` (`@Id`, `@Name`, `@Text`, `@Number`, `@FunctionText`, `@DatapointType`)
- `Parameter` (`@Id`, `@Name`, `@ParameterType`, `@Text`, `@Value`, `@InitialValue`)
- `Languages/Language@Identifier`, `TranslationUnit@RefId`, `TranslationElement@RefId`,
  `Translation@AttributeName/@Text`

Inferred (not observable on this machine, no real `.knxprod` available):

- The namespace URI `http://knx.org/xml/prod/20` for the product database itself; the ETS6
  assembly only embeds the schema under the `project/20…23` namespaces.
- `<Manufacturer Name="…">` (not in the schema; the name comes from the translation unit, the
  attribute is read as a fallback).
- Flat translations with `Translation@Language` instead of a `Language@Identifier` wrapper.
- `ParameterBlock` and `ParameterInstance` elements: ETS6 product programs use `Parameter`;
  both local names are accepted because the knxproj device format uses the latter.
- Program resolution order in `FindProgramFor`: catalogue item `Hardware2ProgramRefId`, then
  the hardware's `ApplicationProgramRef`s, then the product id itself.
- `CatalogProduct.CatalogPaths` is the section path (`"Beleuchtung/Schaltaktoren"`), not the
  item name.

## Store

`CatalogStore` keeps a local library in `%APPDATA%\BusChord\catalogs` (constructor argument
overrides it, `DefaultDirectory` is the default).

- `Import(path)` parses the file, copies it as `<manufacturer>_A-<applicationNumber>-<version>.knxprod`
  (overwriting the previous import of that manufacturer+application) and returns the parsed copy
  whose `Path` points into the library.
- `LoadAll()` parses every `.knxprod` and `knxprod.xml` in the library. A broken file never
  aborts the scan: it is recorded in `LoadErrors` (`CatalogLoadError(File, Message)`).
- `Remove(catalogPath)` deletes one library file.

`C:\ProgramData\KNX\ETS6\ProductStore` is deliberately not parsed (obfuscated ETS cache).

## API

`Knx.Catalog` (`src/Knx.Core/Catalog/`):

- `KnxProductCatalog.Open(path)` — `.knxprod` ZIP, loose `knxprod.xml`, or a directory
  containing either. Properties `Path`, `Name` (file stem), `Manufacturer`
  (`CatalogManufacturer(Id, Name)`), `Products`, `CatalogSections`, `Programs`, `Images`.
- `FindProduct(id)`, `FindProgram(programId | hardware2ProgramId)`, `FindProgramFor(product)`.
- `CatalogProduct` (Id, Name, OrderNumber, HardwareRefId, Description, ProgramRefIds, CatalogPaths),
  `CatalogSection` (Id, Name, Number, Sections, Items),
  `CatalogItem` (Id, Name, ProductRefId, Description, Hardware2ProgramRefId),
  `CatalogApplicationProgram` (Id, Name, ApplicationNumber, ApplicationVersion, ComObjects,
  ParameterTypes, ParameterBlocks, ParameterInstances).
- `CatalogStore` — see above.
- `CatalogInstaller.CreateDevice(product, program, address, name)` — `DeviceInstance` with
  `ProductRefId`, `Hardware2ProgramRefId` and one `ComObjectInstanceRef` per program com
  object (Id/Number/Text/FunctionText, no links).

Known gap in the existing model: `Knx.ComObject` has no `Text`/`FunctionText`, so the catalogue
reader keeps those display texts in an internal per-program table used by `CatalogInstaller`.
`Knx.ComObject` was left unchanged.

Check: `dotnet run --project tests/Knx.Catalog.Tests` builds a synthetic catalogue (ZIP, loose
XML, directory), exercises translations, store round trip and device creation, and parses a
real `.knxprod` when one is found under `C:\ProgramData\KNX` or the user profile.

## Real-world layouts (validated 2026-10)

The first shipped files we obtained (109 `.knxprod` from MDT, Gira, Jung, Berker/Hager,
Siemens, ABB, Schneider/Merten, Theben, Elsner, Weinzierl, Zennio) show that the monolithic
`knxprod.xml` of the synthetic fixture **does not occur in practice**. What occurs:

1. **Split archive** – `knx_master.xml` (global manufacturer table, no device data),
   `<M-XXXX>.signature`, `M-XXXX/Catalog.xml`, `M-XXXX/Hardware.xml`,
   `M-XXXX/Baggages.xml`, and one `M-XXXX/M-XXXX_A-….xml` per application program.
2. **ZIP around the archive** – vendor downloads are often `something.zip` (or
   `….knxprod.zip`) containing the actual `.knxprod`.
3. **Root element is always `<KNX>`**, namespace `http://knx.org/xml/project/11…20`
   (`prod/20` never appeared), so application-program parts are not rooted at
   `<ApplicationProgram>`.
4. Multiple manufacturers per file, and non-image "baggages" (`.dll`, `.msi`, `.html`,
   `.chm`, `.gui`, `.dat`, `.loc`, `.abb`).
5. Legacy ETS3/4 containers (`.vd4`, `.vd5`, `Ets/ets.vd_`) are binary blobs, not XML;
   the reader reports them as "not a catalogue".

`KnxProductCatalog.Open` handles 1–3: it picks `knxprod.xml` only when it really contains
catalogue/hardware content, otherwise it reads the split parts, unwraps one nested archive,
and loads application programs lazily by program id (keeps memory flat for files with 100+
programs). `CatalogStore.LoadAll` accepts `.knxprod`, `.knxprod.zip` and `.zip`.

Corpus result with `--scan-catalogs`:

```
109 Kataloge, 0 Fehler
1147 Produkte, 1102 davon mit auflösbarer Applikation (96 %)
```

Reproduce with a local corpus (never committed):

```powershell
$env:KNX_CATALOG_SAMPLES = "C:\path\to\knxprod-samples"
dotnet run --project src/Knx.Mcp -- --scan-catalogs=$env:KNX_CATALOG_SAMPLES
dotnet run --project tests/Knx.Catalog.Tests
```

Not supported: `.vd4`/`.vd5` (ETS3/4), encrypted `.knxproj` inner archives, and the
obfuscated `C:\ProgramData\KNX\ETS6\ProductStore` cache (deliberately not parsed).

