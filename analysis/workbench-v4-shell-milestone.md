# V4 native shell — first coherent milestone

Base: `d902089f299742fe292fbc66e91e0a7686d9ddbd`. This is **not full V4 acceptance**.
Reference: `C:\Users\julia\Downloads\knx-workbench-v4-bundle` (all Markdown docs,
`src/shell.html`, `src/app.js`, rendered objects/parameters/monitor reference images).

## Delivered

- Product startup defaults to `WorkbenchWindow`; no legacy product mode or CLI switch.
- Fixed native Grid zones: 49px header, 47px rail, 272px navigator, object tabs,
  optional 350px inspector, optional 242px lower tool, 27px footer. GridSplitters,
  focus/reset layout, independent tab view/filter/selection/expansion/scroll state.
- Three native-project navigator projections, including segment and unassigned-device
  nodes. GA counter-navigation resolves the same `DeviceInstance` objects.
- Virtualized ListBox hierarchical projection with common Grid columns: device,
  recursive manufacturer channel, explicitly unassigned communication-object group,
  KO. Channels are **not falsely associated with objects** when the base lacks that metadata.
- Single-object link editing reuses `GroupLinksPane` and existing validated service;
  parameters reuse the existing typed `ParameterPane`, explicit Apply and undo.
- Basic device/KO/multiple-selection/GA inspector, native name edits, GA navigation,
  project new/open/save/save-as/recent projects, real catalog browser, project check,
  existing CSV exports and printable HTML report.
- Existing recorder state survives hide/show and fresh independent monitor tool
  windows. No live Control is moved to another parent or stored in workspace state.
- MainWindow and its view model are unmodified. Old native harnesses explicitly inject
  MainWindow through `App.TestWindowFactory`; normal product startup cannot select it.

## Integration API

`WorkbenchWindow(MainWindowViewModel, WorkbenchWorkspace, string? layoutPath = null,
CatalogLibrary? catalogLibrary = null)` supports isolated test services and long-lived
workspace state. Public entry points:

- `ViewModel`, `Workspace`, `Navigate("building"|"topology"|"groups")`
- `OpenTarget(WorkbenchTarget, string? view = null)`; only existing targets and `tool/catalog`
- `SetRepresentation("objects"|"params"|"relations"|"overview")`
- `RefreshWorkspace()`, `ShowCatalog()`, `ShowTool("monitor"|"problems")`
- `SaveLayout(path)`, `RequestShutdown()`

`WorkbenchProjection` is read-only and independent of controls. Targets use native
IDs (`project`, `room` for any native space, `line`, `device`, `ga`). KO selection keys
are JSON `[deviceId, objectId]`, not bare IDs. Every projection resolves after undo.
Supplemental domain metadata should replace/enrich `Rows` and `Devices`, not clone
the native project. UI mutation still goes through the existing service/domain commands.

Scoped `Shell*` resources in WorkbenchWindow are temporary light-V4 fallbacks.
No App.axaml or WorkbenchTheme/Sessions/Workspace/ParameterPane files were changed.

## Explicit incomplete acceptance matrix

| Criteria | Milestone evidence | Still pending |
|---|---|---|
| A01 / US01 | Native routed navigator selection and common device reference in all perspectives | Catalog insertion into selected room; richer navigation edits |
| A02 | Independent work-item tabs, filter, multi-selection, expansion; layout roundtrip | Full keyboard/DPI/small-window acceptance; pin/open-beside workflow |
| A03 / US02 | Native installation location preserved | Supplemental external room/channel scope metadata |
| A04 | Hierarchical common-column virtualized table; 10,000+ KO check | Real channel-object associations, explicit KO metadata, full perf budget/p95 acceptance |
| A05 / US03–06 | Existing validated single-object add/remove editor and typed parameter Apply | Atomic cross-device batch links, sending-GA, effect preview, model-owned draft session |
| A06 / US08–09 | Existing recorder survives fresh tool views and hide/show | Workbench monitor/queue session integration, fixed/follow filters, retries and interface lease |
| A07 | Native Avalonia 12.1.4; no new packages or commercial controls | Shared dark/light resource contract; full visual/keyboard acceptance |
| US07 | No fabricated device/download status displayed | Supplemental configuration signatures and successful device-state tracking |
| US10 | Existing native project archive, GA/device/topology CSV, current HTML report | Supplemental JSON and assignment CSV integration |

Also pending: full original command parity (compare/replace/smart linking/semantic and
label exports), richer GA creation/deletion UI, line/device move and drag/drop,
Secure overview, parameter styling/nav and all remaining bundle commands.
The parameter control is intentionally one fixed host at this base: its existing
draft cache survives representations/tabs but is **not yet an independent shared session**.
It is not floated or recreated. Session-owner integration must remove that limitation.
The lower monitor currently shows the existing offline recorder, not the bundle's
running/following simulation. It never opens a transport or fabricates traffic.

## Runnable verification

```powershell
dotnet build ets6.sln -c Release
dotnet src/Knx.App/bin/Release/net9.0/BusChord.dll --workbench-selftest --theme=light --log
dotnet src/Knx.App/bin/Release/net9.0/BusChord.dll --smoke
dotnet src/Knx.App/bin/Release/net9.0/BusChord.dll --screenshot-selftest
```

The native self-check uses synthetic archive/application XML and an empty isolated
catalog. It checks routed selection/Apply/inspector/link-host commands, each navigation
perspective, tab state, undo rebinding, saved layout, three fresh tool-window lifetimes,
and model-wide filtering with 10,020 additional KOs. A measured run realized 17 of
10,027 rows (232 ms including a deliberate 150 ms render wait). This is **not** a p95
performance promise or a native mouse Float/Redock acceptance result.

1440×900 native snapshots: `%LOCALAPPDATA%\Temp\opencode\workbench-v4-shell\`
`main.png`, `parameters.png`, `monitor.png`, `inspector.png`, `links.png`, `catalog.png`.
They were read back for inspection. Catalog snapshot deliberately contains no manufacturer data.
No OS mouse/keyboard automation, bus discovery, connection, writes or downloads were used.

Release solution build, all 22 existing console/native test projects, legacy smoke,
screenshot self-test and the new native Workbench self-test passed. The two legacy
native harnesses use explicit factory injection rather than assuming the product's
default window type. Existing deprecation warnings remain in the legacy UI code.
