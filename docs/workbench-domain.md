# Workbench domain contract

`Knx.Core`, namespace `Knx.Workbench`. Native `Project`, topology devices,
`ComObjectRefs`, `Links`, `Locations` and group ranges remain canonical.
No UI, bus transport or session state is owned here.

```csharp
Project project = WorkbenchJson.CreateDemo();
var scope = new WorkbenchScope(WorkbenchScopeKind.Room, "living", Related: true);
IReadOnlyList<ObjectRow> rows = WorkbenchProject.ScopeObjects(project, scope);
ObjectKey key = rows[0].Key; // DeviceId + ObjectRefId; ref IDs need not be project-global.
var undo = new UndoStack();
undo.Reset(project);
MutationResult result = WorkbenchProject.AddLinks(project, [key], "central-off", undo);
```

- `ObjectRow` references the actual `DeviceInstance` / `ComObjectInstanceRef`,
  plus optional `Metadata`, installed room, native line ID and `External` flag.
- Scope kinds: `Project`, `Room`, `Line`, `Device`, `GroupAddress`.
  `IncludeInactive` defaults false; room `Related` defaults true. External
  references include only relevant channels, never all channels of a device.
- `Project.Workbench`: supplemental dictionaries `Devices` (by device ID),
  `Objects` (by `ObjectKey.ToString()`), `Groups` (by GA ID), `FloorShortNames`.
  Native imports may have unknown DPT/size/flags/security; unknown is not false.
- `ValidateLink` returns null or a reason; `AddLinks` validates the entire batch
  before mutation, appends rather than replaces and skips duplicates.
- `SetSending` requires an existing active KO link, changes no flags.
  `RemoveLink` clears removed sending choice and returns a warning when other
  links remain. Neither operation silently picks a sending GA.
- `PreviewMode` is pure; `ApplyMode` recomputes the preview and applies one undo
  step. Only explicit imported DEMO switch/dim devices support this operation.
  Native manufacturer dynamics remain unsupported; inactive links are retained.
- Mutation APIs accept optional `UndoStack`; initialize it before editing.
  Successful changes push a post-mutation snapshot. Undo/redo returns a replacement
  native Project; rebuild row projections after replacement.
- `ConfigurationSignature(project, deviceId)` hashes ordered bus-relevant facts,
  including parameters, object state and actual linked addresses/security.
  It excludes labels, room/effect placement and revision counters.
  Opaque native manufacturer XML remains preserved, not evaluated as application
  dynamics or a complete programmable device image.
- `Associations` yields report DTOs including all inactive links and explicit
  sending choice. Escape fields at the actual HTML/CSV export boundary.
- `WorkbenchJson.Import/Export` reads/writes the bundle envelope
  `knx-studio-demo`, version 4 / schemaVersion 1. Import validates before returning
  a fresh Project. Export refuses native/unrepresentable states rather than
  discarding manufacturer data. `CreateDemo` uses the public fictional fixture.

## Persistence and limits

Supplemental facts serialize as a versioned `Workbench` element in
`urn:buschord:project:1`, with a JSON `Data` attribute. This is an explicitly
BusChord-owned extension, **not** a claim of ETS interoperability. Existing native
XML/archive preservation and undo snapshot mechanisms remain in use. Downloaded
signatures / successful device states belong outside project Undo; the fixture's
revision fields exist only for V4 wire-format compatibility.

Strict full-DPT/bit-size and explicit Secure capability checks implement the
reference's conservative linking rule, not complete KNX/manufacturer conformity.
Adding a link with unknown native capabilities is refused with an explanation.

Check: `dotnet run --project tests/Knx.Workbench.Tests`.
