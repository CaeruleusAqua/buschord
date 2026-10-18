# Parity: building / location view (G2)

Goal: close the largest conceptual gap to ETS6 with a functional building view —
domain model, native persistence, undo-stable state and semantic export. The
panel/UI is explicitly out of scope (a later agent builds it).

Base: `77c78f9`. Branch: `agents/gap/g2-buildings`.

## 1. Evidence (REA, static inspection only — nothing executed)

| Item | Evidence ID | Observation |
| --- | --- | --- |
| `Knx.Ets.Xml.ObjectModel.dll` type probe | `ev_6a8de14cf147374a4efcff4d1769683f3b30c60ece26cbd741d6f9c6015ba017` | Matches `Space`, `SpaceType`, `SpaceUsage`, `Project`, `DeviceInstance`, `Installation`; no `Building`/`Floor`/`Room`/`Locations` *types* — those are space **values**. |
| `Space`, `SpaceUsage`, `SpaceType` fields | `ev_fc964bfd2996600c6c2ee3a64663bbffcf77e84d33d48f36471f481b6738db2c` | `SpaceType` enum: `Building, BuildingPart, Floor, Room, DistributionBoard, Stairway, Corridor, Area, Ground, Segment` (10 values, no "Staircase"). `Space` fields: id, name, type, usage, number, comment, description, completion status, default line, puid, context. |
| `Space` members | `ev_1ad17bce3f9ab05c8aed2c5f39133366024e0ffdc67a8fd8b807167223a96577` | `Id`, `Name`, `Type`, `Usage`, `Number`, `Comment`, `Description`, `CompletionStatus`, `DefaultLine`, `Puid`, `Context`, `ChildSpace`, `DeviceInstanceRef`, `Function`. |
| `Function`, `DeviceInstanceRef` members | `ev_6757083e26391549771ce1fe160a8b36eaebf145e462099a9a090c4ebab94a65` | `DeviceInstanceRef.RefId` only. `Function`: `Id, Name, Type, Implements, Number, Comment, Description, CompletionStatus, DefaultGroupRange, Puid, Context`, `GroupAddressRef` children. |
| Embedded schema resource | `ev_b20c97f2b974626ec692b81ee480e556a3ae9003938b3cad6c9169fec10b3aba` | `Knx.Ets.Xml.ObjectModel.knx_project.xsd`, 287,156 bytes, SHA-256 `a799e141b0c8dec36fb76a5a1013be79619f3192ecf83bab8814d23c0f3b69da`. |
| Namespace probe (no building types) | `ev_b8063f0930d72198ed36518412bc71a511d6d78fa39562d5222f8c200aa7114e` | `Knx.Ets.ObjectModel.*` has none of these types; the XML model lives in `Knx.Ets.Xml.ObjectModel`. |
| Empty-inventory control | `ev_4254657493ca1d9603bf1c889477b15ad2a6dcb1215d7fbe5ec6120a21e541e0` | Whole-assembly member inventory is not admitted; type-selected inspection is the working path. |

Artifact digests (SHA-256), ETS6 `6.4.8718`:

| Artifact | SHA-256 |
| --- | --- |
| `Knx.Ets.Xml.ObjectModel.dll` (4,967,888 B) | `db6f21679d046eaef4909121a553af52fa377fa582552faca6ec0fed5f7f51e1` |
| `Knx.Ets.Xml.ObjectModel.XmlSerializers.dll` | `e61bdcf9cea36cec15c93cdae32e9a4b55396e35cd8e80d8e4603574b18ad32b` |
| `Knx.Ets.ObjectModel.dll` | `73ffaaa97640a4207819c182101d23d3da73d4c983056f5bca5881c0f02eda8a` |
| `ontology.ttl` (3,200,364 B) | `5dbc4fd3b996ff995953ec020b857c12a74c0fea040a61756af072c03fb33ebd` |

The schema itself was read from the embedded `knx_project.xsd` resource bytes
(string extraction from the recorded artifact, not executed). It is a shipped
artifact, i.e. `authority: shipped-artifact`; the excerpt below is verbatim.

```xml
<xs:complexType name="Locations_t">
  <xs:sequence>
    <xs:element name="Space" type="knx:Space_t" minOccurs="0" maxOccurs="unbounded"/>
  </xs:sequence>
</xs:complexType>
<xs:complexType name="Space_t">
  <xs:sequence>
    <xs:element name="Space" type="knx:Space_t" minOccurs="0" maxOccurs="unbounded"/>
    <xs:element name="DeviceInstanceRef" type="knx:DeviceInstanceRef_t" minOccurs="0" maxOccurs="unbounded"/>
    <xs:element name="Function" type="knx:Function_t" minOccurs="0" maxOccurs="unbounded"/>
  </xs:sequence>
  <xs:attribute name="Id" type="xs:ID" use="required"/>
  <xs:attribute name="Name" type="knx:String255_t" use="required"/>
  <xs:attribute name="Type" type="knx:SpaceType_t" use="required"/>
  <xs:attribute name="Usage" type="knx:IDREF" use="optional"/>
  <xs:attribute name="Number" type="knx:String255_t" use="optional"/>
  <xs:attribute name="Comment" type="xs:string" use="optional"/>
  <xs:attribute name="Description" type="xs:string" use="optional"/>
  <xs:attribute name="CompletionStatus" type="knx:CompletionStatus_t" use="optional" default="Undefined"/>
  <xs:attribute name="DefaultLine" type="xs:string" use="optional"/>
  <xs:attribute name="Puid" type="xs:int" use="required"/>
  <xs:attribute name="Context" type="xs:string"/>
</xs:complexType>
<xs:complexType name="DeviceInstanceRef_t">
  <xs:attribute name="RefId" type="knx:IDREF" use="required"/>
</xs:complexType>
```

`Installation` sequence: `Topology`, **`Locations`**, `GroupAddresses`, `P2PLinks?`,
`Trades?`, `SplitInfos?`. `Function_t` carries `Id, Name, Type, Implements, Number,
Comment, Description, CompletionStatus, DefaultGroupRange, Puid, Context` and
`GroupAddressRef*` children.

Conclusion: the native element/attribute names are **certain**, so no BusChord
extension namespace is used. Persistence writes real `<Locations>`/`<Space>`/
`<DeviceInstanceRef>`/`<Function>` nodes in namespace
`http://knx.org/xml/project/23`.

Semantic export vocabulary (`ontology.ttl`, shipped with ETS6):

```
@prefix loc: <http://schema.knx.org/2023/en50090-6-2/loc#> .
loc:Building, loc:Floor, loc:Room, loc:Space, loc:RoomSegment, loc:Location
loc:hasSpace, loc:hasFloor, loc:hasRoom, loc:isSpaceOf, loc:containsEquipment, loc:isEquipmentOf
```

## 2. Domain model (`src/Knx.Core/Buildings.cs`)

```csharp
enum SpaceKind { Building, BuildingPart, Floor, Room, DistributionBoard,
                 Stairway, Staircase = Stairway, Corridor, Area, Ground, Segment,
                 Function, Unknown }

sealed class SpaceNode
{
    string Id; string Name; SpaceKind Kind; string? RawType;
    string? Number, Description, Comment, Usage; int Puid;
    SpaceNode? Parent; List<SpaceNode> Children; List<string> DeviceRefs;
    string Path;
    IEnumerable<SpaceNode> Descendants;
    void Add(SpaceNode child); bool Remove(SpaceNode child);
    bool AssignDevice(string deviceId); bool AssignDevice(DeviceInstance device);
    bool UnassignDevice(string deviceId); bool UnassignDevice(DeviceInstance device);
}

sealed class BuildingTree
{
    List<SpaceNode> Roots; bool IsEmpty;
    IEnumerable<SpaceNode> AllNodes;
    void AddRoot(SpaceNode node); bool Remove(SpaceNode node);
    SpaceNode Add(SpaceNode? parent, string name, SpaceKind kind, string? id = null, int puid = 0);
    SpaceNode? Find(string id); SpaceNode? Find(string parentId, string name);
    SpaceNode? FindDevice(string deviceId);
    IEnumerable<string> AllDeviceRefs();
    static IEnumerable<string> DevicesUnder(SpaceNode node);   // incl. descendants
    void Assign(SpaceNode node, string deviceId);              // moves the device
}

Installation.Locations : BuildingTree
Project.UnassignedDevices() : IEnumerable<DeviceInstance>
```

Notes:

- Nodes are keyed by their stable XML `Id`; save/load and undo keep it.
- `Function` is modeled as a node kind but serialized as native `<Function>` (not
  `<Space Type=...>`), matching the schema.
- Device assignment stores `DeviceInstance.Id` strings, so dangling references are
  data, not errors; `UnassignedDevices()` only considers topology devices.
- `SpaceKind.Stairway` matches the ETS enum; `Staircase` is an accepted alias on
  read and an enum alias in code (the task's wording).

## 3. Persistence (native, preservation-safe)

- `Knxproj.GenerateXml` writes `Locations` between `Topology` and `GroupAddresses`
  and per space the schema order `Space*`, `DeviceInstanceRef*`, `Function*`.
- `Knxproj.FromXml` reads it back (recursive spaces, device refs, functions).
- **Empty tree ⇒ no `Locations` element.** This keeps archives that carry no
  building data byte-compatible and is what lets the preservation merge persist a
  deletion of the last space.
- `Puid` is read and written, so a save never renumbers existing spaces. Attributes
  the model does not interpret (`CompletionStatus`, `DefaultLine`, `Context`,
  vendor attributes) are untouched by the existing `XmlPreservation` merge, as are
  unknown child elements (e.g. `<v:Payload>`).
- Unknown native `Type` values are kept in `SpaceNode.RawType` and written back
  verbatim instead of being coerced to a known kind.
- No BusChord extension namespace was needed; when an archive has no `Locations`
  container the preservation merge leaves it absent (unchanged behaviour).

## 4. Semantic export (`SemanticExport`)

- `loc:` prefix/context is appended **only** when at least one installation has a
  non-empty tree, so output for tree-less projects is unchanged (byte-identical
  prefix block; verified by test).
- Turtle: one statement per node with `a loc:<class>`, `rdfs:label`, and
  `loc:isSpaceOf <parent>` when nested; `loc:containsEquipment <device>` for every
  device reference that resolves to a topology device.
- JSON-LD: space nodes carry `@type` (`loc:Building|Floor|Room|RoomSegment|Space`),
  `isSpaceOf`, `containsEquipment`; context entries are added conditionally.
- Class mapping: Building→`loc:Building`, Floor→`loc:Floor`, Room→`loc:Room`,
  Segment→`loc:RoomSegment`; BuildingPart, Corridor, Stairway, DistributionBoard,
  Area, Ground and Function→generic `loc:Space` (the ontology has no classes for
  them). Documented in `SpaceClass` and here rather than inventing classes.

## 5. Checks

```
dotnet build ets6.sln                                   # 0 errors
dotnet run --project tests/Knx.Buildings.Tests          # ALLE BUILDING CHECKS OK (72 checks)
dotnet run --project tests/Knx.Core.Tests               # ALLE CHECKS OK
dotnet run --project tests/Knx.Archive.Tests            # ALL 106 ARCHIVE CHECKS PASSED
dotnet run --project tests/Knx.Owl.Tests                # ALLE CHECKS OK
```

`tests/Knx.Buildings.Tests` covers: tree add/remove/find/path/descendants/cycle
refusal, device assign/unassign/move/dangling refs, unassigned query, native
save/load round trip incl. XML order and `Puid`, undo/redo preservation, unknown
native XML retention (vendor attributes, vendor element, unknown `Type`, unknown
attribute) plus modeled deletion, and semantic export with/without building data
(Turtle + JSON-LD).

## 6. Limitations / follow-ups

- **No UI.** Domain + persistence + export only; the panel and tree editing UI are
  left to the panel agent.
- `Function` model covers identity/name/puid only. Its `GroupAddressRef` children
  and attributes `Type`, `Implements`, `DefaultGroupRange`, `CompletionStatus`,
  `Context` are preserved opaquely by the merge but are not editable.
- Deleting the *last* space removes the native `<Locations>` container (the XSD
  declares it required). Native ETS archives always contain one; re-adding a space
  recreates it. Not verified against ETS's loader.
- The preservation merge refuses ambiguous duplicate `Id`s; `SpaceNode.Id` is not
  enforced unique by the model itself (only on save).
- Semantic export links `core:Device` via `loc:containsEquipment`, whose declared
  range is `core:Equipment`. ETS' own ontology comment explicitly allows a device
  as equipment ("such as a device as part of a location"), but the ontology
  subclassing is not changed here.
- Only the `Locations` tree is modeled. `Trades` (native, sibling of `Locations`)
  is out of scope and remains opaque.
- Devices are matched to spaces by `DeviceInstance.Id`; files whose `RefId` points
  to an address-only legacy id keep the reference but it will not resolve.
