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
  `CheckLink` returns the typed `LinkValidation` (`LinkValidationReason`), and a
  rejected `LinkError` carries the same code. Unknown capability, inactive
  object, bit size, full DPT, long main type and Secure capability stay distinct.
- `SetSending` requires an existing active KO link, changes no flags.
  `RemoveLink` clears removed sending choice and returns a warning when other
  links remain. Neither operation silently picks a sending GA.
- Per-link connector facts (`ConnectorMetadata`: `Send`, `Acknowledge`, optional
  `Priority`/`Slot`) live in `ObjectMetadata.Connectors`, keyed by GA id.
  `Connector` reads the effective facts (legacy `SendingGroupId` counts as Send);
  `SetConnector` replaces one link's facts and enforces at most one sending link
  per object. `SendingGroupId` stays the compatible adapter: older archives
  without connector flags read it as Send, and the first connector edit replaces it.
- `AddressStrategy` is pure: `TryNextIndividual` finds the first free device
  number in a line (area/line/segment devices and caller-supplied parked
  addresses excluded), `TryNextGroup` the lowest free raw address inside the
  installed group ranges, skipping the style's zero sub-address. Both return
  false on a full range and never mutate the project.
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
- `ProjectRevisions` (`ProjectRevisions.cs`) owns the durable revision log in
  `Workbench.Revisions`: monotonic `REV-0001` ids, canonical SHA-256
  `ContentHash` (revisions excluded), optional source path/hash, description and
  `RevisionSignatureStatus.Absent|Unverified`. `Capture` appends in memory;
  `Latest`/`All` read it; `Validate` rejects duplicate ids/numbers and inconsistent
  signature data. Signature text is preserved and hashed, never verified. The log
  serializes with the Workbench extension and survives save/load; no Workbench
  command or MCP tool captures a revision, so only callers of `Capture` add one.
- `ProjectComparer.Compare` compares project/installation facts, devices
  (including parameters), group addresses, links with connector facts, Workbench
  metadata and the building tree in ordinal order. `CompareRevisions` optionally
  attaches each side's latest revision; the Workbench and MCP compare commands use
  the plain comparison, so revision metadata is not rendered there.
- `DeviceProgrammingState` (`DeviceProgrammingState.cs`) records what is known to be
  loaded into one device, separate from project dirty/undo state: five load flags
  (`IndividualAddress`, `ApplicationProgram`, `Parameters`, `GroupAddresses`,
  `Configuration`), an image/program hash, last attempt/success timestamps, attempts,
  last error, interface id and explicit `ProgrammingOrigin.Demo|Real`. A successful
  attempt ORs the loaded flags and replaces the hash; a failed attempt only updates
  attempt/error/interface and never the success fields. `NeedsProgramming(signature)`
  is conservative: an incomplete flag set or a different hash needs programming.
  `ProgrammingOrigin.Real` is reserved; only demo execution exists.
- `DeviceChangeKind` maps a classified change to the flags it invalidates:
  `IndividualAddress`/`Topology` → address, `Product` → application program +
  parameters + group addresses, `Parameters` → parameters, `GroupAddressLink` →
  group addresses, `Configuration` → configuration. `Name`/`Room` invalidate nothing.
  `DeviceConfigurationSnapshot.ClassifyChange(before, after)` deterministically derives
  the class from two frozen payloads; `ProgrammingSession.ResetForChange(deviceId, kind)`
  applies it and is not an undo step.
- `ProgrammingSession.DeviceState(deviceId)` / `DeviceStates` expose the rich state;
  `ProgrammingSession.Downloaded` remains the successful-load projection for existing
  callers. `DeviceProgrammingReport.Check(project, states, signatureProvider)` is a
  pure diagnostics/project-check helper reporting `NeedsProgramming`, missing flags,
  signature drift, origin and last success per device. Pass the same signature provider
  that produced the states.
- `Associations` yields report DTOs including all inactive links and explicit
  sending choice. Escape fields at the actual HTML/CSV export boundary.
- `WorkbenchJson.Import/Export` reads/writes the bundle envelope
  `knx-studio-demo`, version 4 / schemaVersion 1. Import validates before returning
  a fresh Project. Export refuses native/unrepresentable states rather than
  discarding manufacturer data. `CreateDemo` uses the public fictional fixture.

## Persistence and limits

Supplemental facts serialize as a versioned `Workbench` element in
`urn:buschord:project:1`, with a JSON `Data` attribute. The revision log rides in
the same element, so it survives save/load without changing other project elements.
This extension is owned by BusChord; compatibility with other applications is not
guaranteed. Existing native XML/archive preservation and undo snapshot mechanisms
remain in use. `ProjectReport` renders the latest revision and the
revision table; `HandoverPackage` writes the list as `15_projektverlauf.csv`.

Device programming state uses its own versioned JSON side file
(`DeviceProgrammingStates`, version 2, project-id bound, sorted by device id),
never the project or its undo stack. Import parses and validates the complete file
before swapping state; malformed entries, unknown flags, duplicate device ids,
inconsistent attempt/success data and foreign project ids are rejected atomically.
The file stores signatures, counters, timestamps, interface id and error text only —
no frozen configuration payloads, parameter values, keys or other secrets. The old
version-1 comparison file is not silently reinterpreted because it cannot prove the
five flags.

Strict full-DPT/bit-size and explicit Secure capability checks apply a
conservative linking rule, not complete KNX/manufacturer conformity.
Adding a link with unknown native capabilities is refused with an explanation.

## Not implemented

- No real bus write or download is wired to `ProgrammingSession`. Every job remains
  explicit `Demo`; a `Real` programming origin exists only as a reserved model value.
- Device read-back, image building, partial/differential download and secure
  commissioning (certificates, tool keys, FDSK, sequence counters) are unsupported.
- The reset hook is caller-driven (`ClassifyChange` + `ResetForChange`), not a global
  interception of every project mutation command.
- Coupler/segment topology is partially modeled: segment `Number` and `MediumTypeRefId`
  are preserved, `DomainAddress` is kept as opaque text, and hardware facts (`BusCurrent`,
  `IsPowerSupply`, `IsCoupler`, `IsPowerLineRepeater`) come from the project's own
  `M-*/Hardware.xml`. A coupler is detected through its hardware flag.
  `Area.IsBackbone`/`MainLine` and `Segment.CouplerDevice` are not modeled.
  Repeater/choke limits are not checked.
- The numeric RF fast-ack slot limit is not validated. `Slot` is stored and
  rejected only when negative; no limit is invented.
- Address parking is caller-supplied (`parked` sets). This layer provides no parking
  command or persisted park list.

Check: `dotnet run --project tests/Knx.Workbench.Tests`.
