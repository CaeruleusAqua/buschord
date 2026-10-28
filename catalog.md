# Product catalogues

BusChord imports local product catalogues, lists products and application programs,
and creates devices for the open project. Import a catalogue in the catalogue pane,
choose a product and program, then insert the device into the required line or segment.
Group-address links are added separately.

## Library

`CatalogStore` keeps imported files in `%APPDATA%\BusChord\catalogs` by default.
An explicit directory can be supplied to its constructor. Imports are copied into the
library; the metadata index is maintained separately and can be rebuilt from those files.

A catalogue's broken, incomplete and plug-in requirements remain visible. Unsupported
plug-ins are not executed. A catalogue signature's presence does not establish its validity.

## API

The catalogue API is in `Knx.Catalog`:

- `KnxProductCatalog.Open(path)` opens a supported catalogue file or directory.
  `Products`, `CatalogSections`, `Programs` and `Images` expose its contents.
- `FindProduct(id)`, `FindProgram(id)` and `FindProgramFor(product)` resolve catalogue
  identities. Application models are loaded when needed.
- `CatalogStore.Import(path)` copies a catalogue into the library and returns the parsed
  copy. `LastImportReport` describes the import and its application status.
- `CatalogStore.LoadAll(...)` reads the library. `LoadErrors` lists files that could not
  be loaded; one broken file does not stop the scan. `LastIndexStatistics` describes the
  cache and XML work performed.
- `CatalogStore.Remove(path)` removes an imported library file.
- `CatalogInstaller.CreateDevice(project, product, program, address, name)` creates a
  detached device with a project-wide free identity and the program's communication
  objects. The caller inserts it into the project and manages undo.

Only supported catalogue and application profiles are editable. Refer to
[product status](docs/status.md) for current limitations and
[the manual](docs/manual.md) for the catalogue workflow.
