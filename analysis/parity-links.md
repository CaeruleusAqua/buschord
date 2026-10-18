# Manual communication-object / group-address links

## Static ETS evidence

Target files under `C:\Program Files (x86)\ETS6`; REA managed metadata/CIL inspection only, no execution or bus traffic.

- **ObjectModel** SHA-256 `73ffaaa97640a4207819c182101d23d3da73d4c983056f5bca5881c0f02eda8a`, MVID `484ff6a5-f1b3-4f33-ad19-2dd22dc9ae04`.
  Focused `inspect_managed_members` retained Evidence `ev_2af81bb21e90085413dc0c392dc3e4137577092cb65e9daf585124a28414f3bc` selected the two operation types below, complete within selection (2 types / 27 methods).
  Methods view: `ev_f058bf38eec1268e2696f5689bfe6fb103d0a5e62629d5dfa0b01e1e9ae77332`.
  - `Knx.Ets.ObjectModel.Operations.GroupAddressRefs.LinkGroupAddressRefToComObjectInstanceOperation` has `SpecializedValidate` (`0x06005fbf`), `SpecializedExecute` (`0x06005fc0`), `SpecializedUndo` (`0x06005fc3`), `SpecializedRedo` (`0x06005fc4`), `get_UndoManager`, and `BuildComObjectInstanceRefList`.
  - `Knx.Ets.ObjectModel.Operations.GroupAddressRefs.UnlinkGroupAddressRefOperation` has validation (`0x06005ffb`), execution (`0x06005ffc`), undo (`0x06005ffd`), redo (`0x06005ffe`), and undo-manager access.
  - CIL call-edge view `ev_a12f8317cb782f5474e35a3926702e9b13a99c550d38c7cbda43cacfb08ee2bd` shows the link operation constructs `Operations.GroupAddress.LinkGroupAddressOperation`, and routes through operation validation/execution. It also references SmartLinking suggestions and security-impact handling. This is observed static structure, not an execution trace or a full reconstruction of ETS validation semantics.
- **ViewModel** SHA-256 `236048b0b3992f504488c2fb9890ab2fe73c3a7a7079c0f4d8671d30aefc5c75`, MVID `b557be5c-0e7f-4bc3-aa94-958fba394aba`.
  Retained Evidence `ev_bd1f57114254f1001dd0ebdbe3c6ef785cf11ae3fa6bb8d1fb5815bebb7d8f10` includes assembly-wide member references even with an unmatched declaring-type selection. Paged references confirm callers reference the ObjectModel link/unlink constructors:
  - Link-to-com-object constructor `0x0a003712`: view `ev_506f457a216b4154198bce1a0cdd53fd1ef47dad8ab0ecad723b44441438b97b` (offset 14000, limit 1000).
  - Unlink-reference constructor `0x0a0026e8`: view `ev_bf3a0006154b5bf9b30ec8a7cb9b6c8508587d77fad9fdbe974ca9e30e501c13` (offset 9000, limit 1000).
  - Link-group-address and link-reference-to-function constructors: view `ev_fb98253e4f52b54d713c04cfc4243cf362fe520e86c042cef12da3f718720468`.

**Limits:** `Knx.Ets.ObjectModel.Project.ComObjectInstanceRef`, `Knx.Ets.ViewModel.ComObjectInstanceRef`, and `Knx.Ets.ViewModel.GroupAddressRefs` exact guesses were unmatched, not evidence that similarly named/interface/XML types are absent. Initial unrestricted ObjectModel inspection exceeded REA's 64 MiB metadata representation budget (`ev_ecd6da0b4e2bfdb70eb9d3b069e8766d5b5b642280365554129ae4205818d284`); focused selection succeeded. One oversized reference view exceeded transport budget, then was replaced by bounded pages. Tokens are build-local; obfuscation and static coverage prevent claiming full ETS behavioral parity. No proprietary code was copied.

## BusChord gap and implementation

At base `98bb80a8ed26fafda71f97c9c38e11b9e7c60a8d`, `Model.cs` / `Parameters.cs` already expose `DeviceInstance.ComObjectRefs` and `ComObjectInstanceRef.Links`; `Knxproj.cs` reads/writes link IDs; `Undo.cs` snapshots that XML. `SmartLinking.Apply` only adds scored suggestions. `McpServer.Tools.cs` has no manual link or communication-object listing tools. Existing lists and serializer are retained unchanged.

- `GroupLinks.Link(Project, DeviceInstance, string comObjectRefId, string groupAddressId)` / `Unlink(...)` return whether a link changed. Validate null/blank inputs, reference-identity project membership (including area/segment devices), unique reference id on that device and unique GA id in the project before mutation. Add is idempotent; unlink removes all matching legacy duplicates. Unrelated links retain order. Stale pre-undo device instances are rejected.
- MCP `communication_objects_list(device_address)` exposes reference id, source object id, text, function, number and link GA id/address/name. Dangling link IDs remain visible. `group_link_add` / `group_link_remove` accept `device_address`, reference `object_id` and `group_address` text. They validate string arguments and unique device/GA addresses; only real changes push the existing undo stack. No-op/errors preserve redo history. All operate offline, with no bus calls.
- No DPT checks are invented: instance references currently have no DPT metadata. No new dependencies, GUI changes, or model/serializer changes.

## Verification and integration limits

- `dotnet build tests/Knx.Linking.Tests`
- `dotnet run --project tests/Knx.Linking.Tests`
- `dotnet run --project tests/Knx.Mcp.Tests`
- `dotnet run --project src/Knx.Mcp -- --selftest`

All four commands passed on 2026-10-18. Existing unrelated warnings: Core catalog `CS8620` and Bus routing `CS1998`. `git diff --check` passed (only Windows line-ending notices).

Core regression checks cover idempotence, multiple links/order, unchanged list instances, validation/no mutation, foreign/stale devices, ambiguous ids, all topology containers, existing save/load and undo/redo link preservation, and legacy duplicate removal. MCP checks invoke all three tools against a synthetic catalogue, validate errors/no mutation, no-op history, undo/redo of add/remove, and disconnected bus state. All checks are offline; existing MCP bus_connect checks only reject malformed input/unknown modes before network access.

Existing serializer limitation (owned separately): `Knxproj` does **not** persist reference `Number` or separate `ComObjectId` at this base; load resets number to zero and reconstructs `ComObjectId` from `Id`. Links, reference ID, Text, FunctionText and Device survive. Tests assert currently supported metadata only. GUI integration should resolve the current device after undo, call Core with reference `Id` / GA `Id`, then push its existing undo snapshot only when the returned bool is true.
