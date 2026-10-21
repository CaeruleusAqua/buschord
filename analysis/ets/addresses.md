# ETS6 topology, buildings, devices and group-address links — REA analysis

Read-only, static managed-metadata analysis of the installed ETS6 (`C:\Program Files (x86)\ETS6`).
No target execution, no bus traffic, no product source changes.
Base `12fc904ee05089976eac93426141c6fdb348ffe1`, branch `agents/ets-rea/addresses`,
workspace `C:\projects\ai_work\agent-wt\ets-rea-addresses` (verified HEAD `12fc904`).

## 1. Target identity and method

| Artifact | SHA-256 | MVID |
| --- | --- | --- |
| `Knx.Ets.ObjectModel.dll` | `73ffaaa97640a4207819c182101d23d3da73d4c983056f5bca5881c0f02eda8a` | `484ff6a5-f1b3-4f33-ad19-2dd22dc9ae04` |
| `Knx.Ets.ViewModel.dll` | `236048b0b3992f504488c2fb9890ab2fe73c3a7a7079c0f4d8671d30aefc5c75` | `b557be5c-0e7f-4bc3-aa94-958fba394aba` |
| `Knx.Ets.Common.Types.dll` | from `inspect_managed_members` evidence `ev_fb639a4a65b462f9ca8d89a2f635e811623913946356c65c48fa67a5a44e9227` | — |
| `Knx.SmartLinking.dll` | `fed25099ec78b5367acc27e4e7731aa98f04deee7bc581d48e76fee0577c72b1` | `cf317729-615e-44cb-9334-99f0d42e5eb6` |

Method: `inspect_managed_members` on exact fully-qualified type names (REA managed metadata + CIL
anchors, no CLR load, no decompilation to C#), paged back through `inspect_analysis_view`
(`collection: methods|call_edges`) when the complete response exceeded the 10 MiB MCP transport
budget. Type-name discovery was bootstrapped from the metadata `#Strings` heap with a raw byte scan;
every claim below is anchored in a REA Evidence record, not in the scan. All tokens are build-local.

Confidence vocabulary: **observed** = type/method/token present in evidence; **derived** = structure
implied by member names/signatures; **inferred** = behaviour reconstructed from operation naming
conventions and ETS domain semantics, not executed.

## 2. Evidence ledger

| Evidence ID | Selection / view | Used for |
| --- | --- | --- |
| `ev_572d7855a5207b2ec54730132aa68e6789bc08c98dd8648b834a6584cada0212` | `Project.Area`, `Project.Line`, `Project.Segment` (148 methods) | topology model |
| `ev_78f677674ac2f9a2b0320fd1adcb15d638e3cc933c00168d1f8819d566657c06` | `Project.BuildingPart`, `Project.Function`, `Project.GroupAddress` (145 methods) | building/function/GA model |
| `ev_4fcace976042287f689f992365546e9c7d60cda49b9f0195121028de9f91c330` | `Devices.AddDeviceOperation`, `ChangeDeviceAddressOperation`, `DeleteDeviceOperation` | device insert/move/delete |
| `ev_d916ca66fe7e0fb2b2e2be1a3e2415af119a989aaab79c27b3b139e5f3e09430` | `Area.Add/ChangeAddress/DeleteAreaOperation` | area operations |
| `ev_4370af2f5aa9c16b5c1dfdd96437cf6eeb11355a70079743098b0a13a33d502d` | `Line.Add/ChangeAddress/MoveLineOperation` | line operations |
| `ev_ffcd9667040138ab255691ab58a95092096ffd83d62e3af6871c5f26ed62a192` | retained, 113 methods, 1066 call edges (`AddSegment/MoveSegment/AddArea/ChangeAreaAddress/AddLine/ChangeLineAddress/MoveLine`) | segment ops, `NewItemOperation`/`DefaultNames` call edges |
| `ev_1f62b0afc21bef124128cceab4484e5222f748fd5d4746dd4dc097df9887b9a3` | `OperationBase`, `MoveOperation`, `NewItemOperation`, `DeleteOperation` | operation architecture |
| `ev_e564902ca99c6108624377fe5aae0226acd0d0c8ef697d136538db1d1e76adb3` | `Coupler`, `CouplerCheck`, `MoveAddressStrategy`, `ResetSegmentCouplerFlagsSmart` | couplers/segments, address strategy |
| `ev_2fdd28c77b0d2ee95e023b85c189262bcb5d073d406eb30952c143f04f808b2e` | `GroupAddress.Add/Move/Copy/ChangeDatapointOperation` (74) | GA operations |
| `ev_67054805a7904dfbba27430dd15114da66170bfecc53f308d4efe69a45f6693b` | `LinkGroupAddressOperation`, `UnlinkGroupAddressRefOperation` (30) | link/unlink operations |
| `ev_5b441dee0a2315e940df0a0f0454a809b3def5713a83cc6a8fd1550ed262e26d` | `MoveGroupAddressValidator`, `MultiLinkGroupAddressOperation` | validators, bulk link |
| `ev_13f4967b1b7cb344058aff47b533fa074aa72d38e846811c75de5940d3312a35` | `Project.GroupRange`, `GroupRanges.FreeGroupRange` (54) | GA range model |
| `ev_a99dcba11a3d956d779d05ede739ceb37092fe3577fa43179527959ac3d74969` | retained, 81 methods `Import/ExportGroupAddressesOperation` | import/export |
| `ev_9315a3b44be8917441cf352672d4def34284923e26831e29552a9c63c678ee47` | validation/error enumerations | validation taxonomy |
| `ev_bb4bf72dca8fe2bb08bafc8f58cc9bced45e4f28351d701bf02fe17952bd3766` | retained, 130 methods `LinkGroupAddressValidator`, `Project.ComObjectInstance` | DPT/flags/object size |
| `ev_63435992426ba2209701e2b62796b21b3bae7616f78cab96d2a3ee555e7365a5` | `Project.Connector`, `ConnectorUndoRedoManager`, `UnlinkConnectorOperation` | link/sending-address model |
| `ev_fb639a4a65b462f9ca8d89a2f635e811623913946356c65c48fa67a5a44e9227` | `GroupAddressStyle`, `GroupAddressBehavior`, `BuildingPartType`, `GroupAddressValue`, `FreeGroupRangeFormattingStyle` | GA styles, address value |
| `ev_a10c1117a81df2f4524de4f9e607bc12e700a2d000911447c8da19af2404598d` | `Knx.SmartLinking.dll` summary (344/344 rows, metadata partial) | smart-linking split |
| `ev_90ffec80ed6392ec0699be56c44bf4bc39bff83b72ed8d275e3eab7c1adbdf8f` | retained, 150 methods `LinkWithViewModel`, `LinkedGroupObjectViewModel` | Link editor, GA counterview |
| `ev_88acaa845a4dd77779a75ba47bd91a50cf333748ca22c3c0b3ba16c886dba0e8` | retained, 33 methods `LinkWithCommand`, `PasteCommandHandler` | link command, paste/paste-special |
| `ev_7107db4d76b05f2871e9d7ad206f322dce045babef9e4857850b152b355af458` | retained, 44 methods `FindAndReplaceService`, `DummyFindAndReplaceService` | search/replace service |
| `ev_29597ad04f3014f8c7420e975a5c6c46a1c12812e3114bc497a85b9cd3ec7746` | retained, 54 methods `FindAndReplaceViewModel`, `SearchCriteria`, `IFindAndReplaceAble` | find/replace UI contract |
| `ev_7763419b277d32cb53a98a19bf56460543cf1686368845bc792a10d0326eedf0` | retained, 58 methods `SidebarGroupAddressesViewModel`, `SidebarGroupsTreeViewViewModel` | GA counterview |
| `ev_ea7235ffd7484dffa3c6f391010b005ebdbfa2ca95d178639e2d7b2dc186b927` | `Dashboard.Commands.ParkAddressCommandHandler` (10) | address park/free |
| `ev_3a9d9e503bbcbd271f92ea855d003d0ca04193de75b4513d589d802aa8c88a60` | retained, 20 methods `IDetailViewTab`, `AllDevicesDetailViewModel` | multi-selection counters |
| `ev_2131fa6a7f80ab5990ae4d65e8284811bd3662cbe799b8be015144907fbcf53a` | `GroupAddressCheckViewModel`, `TopologyCheckViewModel` (14) | project check UI |
| `ev_50ca270c46af99a2fccb519bf0d0c5211f4783f5b0ecb4dcfff573233119d84b` | `GroupAddressStyleDropDownViewModel`, `ChangeGroupAddressStyleViewModel` (18) | GA style UI |
| `ev_51cd7ef9e3af850ceb3ff6da69cafbcf3f9ff9271d27e18e6332804246820f44` | retained, 14 methods `GroupAddressDetailViewModel` | GA detail tab |

Supporting, non-REA: resource-key scan (UTF-16 `#US`/resource strings) of `Knx.Ets.ViewModel.dll`
and ASCII scan of `ETS6C.exe`. Used only for UI resource keys (`KnxGlyph*`, `PanelGroupAddresses`,
`IDS_CHECK_*`); these are discovery hints, not evidence of behaviour.

## 3. Topology: hierarchy and model

**Hierarchy (observed).** `Project.Installation` → `Project.Area` → `Project.Line` → `Project.Segment`
→ `Project.DeviceInstance`. Every node carries `Id`, `Name`, `Comment`, `Description`,
`CompletionStatus`, `SplitObjectType`, `CreateId`/`GenerateId`, `InternalSynchronize(2p)`,
`DiffersInAtLeastOneProperty`/`MergeProperties` (split/merge support), and `WriteTo(1p)` for XML
serialization. This is a DOM with per-node XML writers, not a database model.

- `Area` (`0x020001b0`): `Address` (`u1`, getter only; set through `IInternalArea.SetAddress`,
  `0x06001506`), `IsBackbone`, `MainLine`, `Lines`, aggregates `AllDevicesInArea`,
  `AllSegmentsInArea`, `OnMediumTypeChanged`.
- `Line` (`0x0200036c`): `Address` (`u1`), `AreaLineAddress` (`u1`, computed),
  `AddressString`, `IsMainLine`, `IsBackboneLine`, `MainSegment`, `Segments`, `Devices`,
  `IpDevices`, `IpRoutingSecurityState`, `GetNextSegmentNumber(2p)` (`0x060025a4`),
  `OnAreaAddressChanged` (143 IL bytes: renumbers children when the area address changes).
- `Segment` (`0x020003f5`): `Number` (`i4`, `IInternalSegment.SetNumber`), `MediumTypeId`
  (setter 86 IL bytes), `DomainAddress` (`u8`, setter 66 IL bytes), `BusAccess`,
  `CouplerDevice`, `HasCouplerDevice`, `GetDefaultName`.
- `DeviceInstance`: individual address, product/hardware program refs, `Channels`,
  `ComObjectRefs`, bus-interface and security sub-objects (`IInternalDevice` exposes
  `SetAddressWithoutUndo`, `SetSegment`, `SetBuildingPart`, `SetTrade`, `SetMacAddress`,
  `SetUnloadedOnTopologyAddressChange`, `SyncComObjects`, `UpdateChannelIndependentComObjects`,
  `UpdateRename`/`ClearRenames`).

**Key structural fact:** there is no `MoveDeviceOperation`. Device movement is expressed as
`ChangeDeviceAddressOperation` (address change, includes line/area change) plus
`LinkDeviceToSegmentOperation` / `UnlinkDeviceFromLineOperation`. Line and segment movement have real
move operations (`MoveLineOperation`, `MoveSegmentOperation`) built on `MoveOperation.Mover` /
`UndoMover` / `RedoMover` (`0x0600329f`–`0x060032a1`).

**Couplers/segments (observed).** `Operations.Coupler` exposes `MaskVersionIsCoupler`, `IsTp`, `IsIp`,
`NeededUpperLine`; `CouplerCheck.IsCoupler` (`0x06003135`) is the predicate used by validators.
`ResetSegmentCouplerFlagsSmart` (17 methods) has `AffectedSegmentCouplerAddresses`,
`CheckSegmentCouplerAssociation` and mutates coupler flags after topology changes. Coupler
capability flags: `CouplerCapability.{RfReady, RfMultiFast, RfMultiSlow, SecurityProxy, SegmentCoupler}`.

## 4. Topology operations (insert, delete, move, rename, addressing)

**Operation architecture (observed).** `OperationBase` (34 methods) provides `Target`/`TargetAs`,
`GetSourceObjects`, `GetValidators`, `GetProgressDescription`, `WithProgress`,
`ThrowIfCancellationRequested`, the six `Before/AfterSpecialized{Execute,Undo,Redo}` hooks and
localized single/multi item descriptions. Specializations: `NewItemOperation` (9 methods, incl.
`ExecuteReturnCreated`, `CheckSecureEnabledDevices`), `DeleteOperation` (28, incl. `FlagHandler` and
`GroupAddressService` properties), `MoveOperation` (14), `CopyOperation`, `AddOperation`,
`ProjectRelatedOperation`. Every mutating operation therefore has validate/execute/undo/redo and
undo-manager integration; nothing is a bare setter.

**Insert device.** `Devices.AddDeviceOperation` (`0x02000ac7`, 36 methods, 26 fields) —
13-parameter constructor, `SpecializedValidate` `0x06006bd0`, `SpecializedExecute` `0x06006bd7`,
`ExecuteReturnCreated` `0x06006be6` returning `Result<GroupAddress?>`-shaped created object,
`DetermineSecurityImpacts`, `BeforeOpenMarker`, undo/redo. Call edges from the topology batch show
add-operations route through `NewItemOperation..ctor` and `DefaultNames.FillUpNotSpecifiedNamesWithNull`
(`0x0600314b`), i.e. name defaults are filled before the object is created.
`AddDeviceWithProductAndHardwareToProgramOperation` and `CreateDeviceFromTemplateOperation` are the
catalog/template variants; `CopyDeviceToSegmentExtendedOperation` and
`CopyDeviceToBuildingPartExtendedOperation` are the cross-container copies.

**Delete device.** `Devices.DeleteDeviceOperation` (`0x02000b11`, 12 methods). `DeleteOperation` owns
`FlagHandler` and `GroupAddressService`, so deleting a device also rewrites dependent coupler/segment
flags and GA associations (derived). `ResetCouplersAffectedByLastOperation` /
`ResetAppropriateCouplerFlagsAfterOperation` are the post-operation repair entry points.

**Move device.** `ChangeDeviceAddressOperation` (`0x02000af6`, 16 methods): 4-parameter constructor
(target device + address), `SpecializedValidate` `0x06006d6c`, `SpecializedExecute` `0x06006d6d`,
undo/redo, `DetermineSecurityImpacts`. `MoveDeviceAddressStrategy` and the strategy family
(`AddressStrategy`, `IAddressStrategy`, `AddressStrategyInfo`, `DeviceAddressStrategy`,
`OffsetAddressStrategy`, `CreateDeviceStrategy`, `CreateLineAddressStrategy`,
`CreateAreaAddressStrategy`, `CreateDeviceOffsetStrategy`, `MoveAddressStrategy`,
`InternalKeepAddressStrategy`, `InternalIncrementalDeviceAddressStrategy`,
`ExtendedCopyAddressStrategy`, `CreateNewInThisRoomAddressStrategy`) implement address
suggestion/renumbering. `MoveAddressStrategy.AnyFreeAddresses` + `Get` is the free-address query used
by validation before a move.

**Line/segment/area operations.** `AddAreaOperation` (`0x02000bc1`), `ChangeAreaAddressOperation`,
`DeleteAreaOperation` (14), `AddLineOperation` (`0x0200076f`; `GetMediumType`, `GetDomainAddress`),
`ChangeLineAddressOperation`, `MoveLineOperation` (`0x0200078a`, 22 methods),
`AddSegmentOperation` (7-parameter constructor: parent line, address, names, medium type id, domain
address, strategy), `MoveSegmentOperation`, `ChangeMediumTypeOfSegmentOperation`,
`DeleteSubSegmentOperation`, `ChangeDomainAddressOperation`, `JoinFreeGroupRangeOperation`.
Main-line / medium-type constraints are enforced by dedicated validators:
`AddIpLineValidator`, `AddPlLineValidator`, `AddRfLineValidator`, `AddIoTLineValidator`,
`ChangeMediumTypeCouplerValidator`, `ChangeMediumTypeSegmentValidator`, `CouplerValidator`,
`ChangeSegmentMediumTypeValidator`, `RfMainLineMultiDeviceValidator`.

**Rename.** No rename operation type exists. Names are DOM properties (`set_Name`, `set_Text`,
`UpdateRename`) and there are `Add…WithSpecifiedNamesOperation` variants used by import/copy to
supply names up front (`AddAreaWithSpecifiedNamesOperation`,
`AddLineWithSpecifiedNamesOperation`, `AddGroupAddressWithSpecifiedNamesOperation`).
`IInternalDevice.Renames`/`UpdateRename`/`ClearRenames` propagate channel/com-object renamed texts.
*Derived:* UI rename edits the property and relies on the operation that wraps the parent edit; no
standalone "rename device" operation is present to hook.

**Address assignment / park.** `Operations.Devices.ParkAddressOperation` +
`Dashboard.Commands.ParkAddressCommandHandler` (single and multi selection overloads) implement
parking an address so it is not re-used; `IsAddressFreeCommand`, `CheckIndividualAddressCommand`,
`ButtonAddressFree` resource key and `GroupAddressAssignmentErrors.AddressLimitReached` /
`AlreadyAssigned` show the free/busy contract. `DeviceAddressNotUnique`, `DeviceAddressOutOfRange`,
`AreaAddressNotUnique`, `AreaAddressOutOfRange`, `AreaAddressAlreadyAllocatated`,
`AddressAlreadyAllocatated` are the corresponding error symbols.

## 5. Building / room / function model

`Project.BuildingPart` (`0x020001bb`, 37 methods) is the single node type for the whole location tree:

- `Type` (`BuildingPartType`), `LocationUsage`, `Name`, `Number?`, `Comment`, `Description`,
  `CompletionStatus`.
- Structure: `ParentBuildingPart`, `BuildingParts` (children), `Items`, `GetAllNestedBuildingParts`,
  `ParentPartWithCurrentLine`, `DefaultLine`/`DefaultLineOfBuildingStructure` (a room can own a
  default line for device insertion), `CanHoldDevices`.
- Content: `Functions`, `ContainsFunction`, `AllGroupAddresses`, `CreateDeviceCollection`,
  `CreateSubItemCollection`.

`BuildingPartType` (observed enum): `Building`, `BuildingPart`, `Floor`, `Stairway`, `Room`,
`Corridor`, `DistributionBoard`.
Additional UI/document node kinds come from the XML `SpaceType_t` mapping used by the product
(`Area`, `Ground`, `Segment`, `Function` — see `Buildings.cs`), while the object model enum stops at
the eight values above; the extra kinds are read from XML, not from this enum.

`Project.Function` (`0x02000275`, 31 methods): `Name`, `Type` (`MasterData.FunctionType`), `Number`
(string, not numeric), `Context`, `DefaultGroupRange`, `GroupAddressRefs`,
`ParentBuildingPart`, completion/comment.

Building operations: `BuildingParts.AddBuildingPartOperation`, `DeleteBuildingPartOperation`,
`MoveBuildingPartOperation`, `CopyBuildingPartOperation` (+`CopyBuildingPartExtendedOperation`),
`LinkDeviceToBuildingPartOperation` / `UnlinkDeviceFromBuildingPartOperation`,
`LinkDeviceToTradeOperation` / `UnlinkDeviceFromTradeOperation`,
`Operations.Functions.{Add,Delete,Move,Copy}FunctionOperation` and
`GroupAddressRefs.{AddGroupAddressRefToFunctionOperation, LinkGroupAddressRefToFunctionOperation}`.

Validation enums show the real constraints: `BuildingPartValidationReason.{NameNotUnique,
NameLengthError, DeviceNotCompatibleWithMediumTypeOfDefaultLine, CannotKeepConnectorsOnDifferentInstallations,
DeviceCountIsGreaterThanLicenceAllowed, SourceBuildingPartFromDifferentInstallations,
BuildingPartTypeCannotBeAdded, BuildingPartTypeDistributionBoardCannotBeAdded, TargetIsAChildBuildingPart,
TargetIsAlreadyAssigned, GroupAddressStrategyValidationErrors, HasDevicesBrokenForEditing,
HasBrokenDevices, MovingBuildingPartNotSupportedInSameProject}` plus the shared line/medium/split
reasons. `BuildingPartTypeDistributionBoardCannotBeAdded` is a genuine asymmetry: distribution boards
are not addable through this path.

## 6. Group addresses: model, styles, ranges

`Project.GroupAddress` (`0x02000284`, 77 methods, 21 fields):

- Address: `Address` (`u2` setter, side effects in `OnAddressChanged`), `AddressString`,
  `AddressValue`, `IsBit16Set`, `InvalidPowerlineGroupAddressValue`.
- Data: `Name`, `Comment`, `Description`, `Context`, `ObjectSize`, `ObjectSizeInBits`,
  `ObjectSizeViaConnectorsOrDtps`, `DatapointTypeId`, `DatapointType`, `DatapointTypeIds`,
  `DatapointTypesString`, `DatapointTypesCollection`, `MergeFlagsAndDatapointType`.
- Security: `Security`, `OrigSecurity`, `SecurityState`, `Key`/`IsKeySet`,
  `SharesSecurityDomainWith`, `InvalidateComObjectSecurityState`.
- Links: `Connectors`, `GetConnectorCollectionIfCreated`, `ConnectorsCount`, `BusInterfaces`,
  `BusInterfacesCount`.
- Filtering: `Unfiltered`, `UnfilteredSetInAnyParent`, `Central`, `Global`,
  `ReadySlowResenderNeeded`.
- `DiffersInAtLeastOneProperty`, `MergeProperties`, `InternalSynchronize`, `WriteTo`.

`GroupAddressStyle` (observed): `TwoLevel`, `ThreeLevel`, `Free`. `GroupAddressValue` implements the
format layer: `MainGroupAddress`, `MiddleGroupAddress`, `TwoLevelSubGroupAddress`,
`ThreeLevelSubGroupAddress`, `TwoLevelString`, `ThreeLevelString`, `NumericString`,
`LowerCaseHexString`, `UpperCaseHexString`, `TryParse`/`Parse`,
`IsTwoLevelGroupAddressString`/`IsThreeLevelGroupAddressString`, range comparison operators and a
configurable `DefaultFormatProvider`. Free style keeps a hex prefix and a free-format provider
(`FreeGroupRangeFormattingStyle.DefaultHexPrefix`/`DefaultFreeFormatProvider`).

Ranges: `Project.GroupRange` (`0x020002a2`, 47 methods) — `RangeStart`, `RangeEnd`, `Range`
(display), `GroupRanges`, `GroupAddresses`, `ParentRange`, `StructuralParents`,
`NumberOfFreeGroupAddresses`, `AllGroupAddressesInRange`, `IsInRange`, `Overlaps`,
`ApplyNewRange`, `MergeFlags`, security/unfiltered inheritance (`ParentalSecurity`,
`UnfilteredSetInAnyParent`). `GroupRanges.FreeGroupRange` (7 methods) is the free-style leaf;
`GroupRanges.OldStyleGroupRange` covers pre-ETS4 two-level. Range operations:
`GroupRanges.{AddGroupRange, AddFreeGroupRange, AddThreeLevelMainGroup, AddThreeLevelMiddleGroup,
AddTwoLevelMainGroup, ChangeGroupRangeAddress, Copy*, Move*, DeleteGroupRange, SplitFreeGroupRange,
JoinFreeGroupRange, ShiftFreeGroupRange, StretchFreeGroupRanges}Operation`.

Style switching: `Projects.ChangeGroupAddressStyleOperation` with
`IChangeGroupAddressStyleConverter`, validators `ChangeGroupAddressStyleValidator` and
`GroupAddressStyleValidator`, plus `ChangeGroupAddressStyleValidationResult`.
Style-specific rules surface as `GroupAddressValidationReason.{GroupAddressesOnlyAllowedInMiddleGroupsInThreeLevelStyle,
GroupAddressesOnlyAllowedInMainGroupsInTwoLevelStyle, DifferentGroupAddressStyle,
GroupAddressWith16BitOwnerExists, GroupAddressWith16BitsButHideFromLegacyPlugins,
GroupAddressGreater15BitWouldCreated}` and the `CsvExportNotAllowedForFreeGroupAddressStructure`
resource key (no CSV export in free style).

## 7. Group-address operations

All inherit the operation contract and are undoable individually:

| Operation | Token | Notable members |
| --- | --- | --- |
| `GroupAddress.AddGroupAddressOperation` | `0x0200094c` | `SpecializedExecute`, `ExecuteReturnCreated`, `GetName` |
| `GroupAddress.ChangeGroupAddressOperation` | `0x02000952` | 48 methods; `SpecializedValidate` `0x06005fbf`, `SpecializedExecute` `0x06005fc0`, undo `0x06005fc3`, redo `0x06005fc4` |
| `GroupAddress.MoveGroupAddressOperation` | `0x02000970` | `Mover`/`UndoMover`/`RedoMover` (`0x06005ebd`–`0x06005ebf`), `DetermineSecurityImpacts` |
| `GroupAddress.CopyGroupAddressOperation` | `0x02000960` | `SourceGroupAddressMap`, `CreateSourceGroupAddressMap`, `ExecuteReturnCreated` |
| `GroupAddress.DeleteGroupAddressOperation` | — | delete base + `GroupAddressService` cascade |
| `GroupAddress.ChangeGroupAddressDatapointOperation` | `0x0200094f` | DPT change with validate/execute/undo/redo |
| `GroupAddress.MultiLinkGroupAddressOperation` | — | `SpecializedValidate/Execute/Undo/Redo`, `DetermineSecurityImpacts` |
| `GroupAddress.LinkGroupAddressOperation` | `0x0200096c` | `BuildComObjectInstanceRefList` `0x06005e87`, `BuildGaListText` `0x06005e88`, validate `0x06005e89` |
| `GroupAddress.UnlinkConnectorOperation` | — | connector-level unlink with undo |
| `GroupAddressRefs.LinkGroupAddressRefToComObjectInstanceOperation` | — | validate `0x06005fbf`, execute `0x06005fc0`, undo `0x06005fc3`, redo `0x06005fc4`; constructs `LinkGroupAddressOperation` |
| `GroupAddressRefs.UnlinkGroupAddressRefOperation` | — | validate `0x06005ffb` … redo `0x06005ffe` |
| `GroupAddressRefs.MultiLinkGroupAddressOperation` | — | bulk link path |
| `GroupAddress.*` import/export | see §11 | — |

Guards: `AddressOutOfRange`, `GroupAddressAlreadyExistsInTargetRange`, `NoMoreGroupAddressesAllowed`,
`GroupAddressNotInParentRange`, `MoveGroupAddressFailedNoParentRange`,
`CollisionWithChildRanges`, `FreeGroupAddressStructureNotEmpty`,
`GroupAddressCannotBeCopiedNoMoreFreeAddresses`, `GroupAddressWith16BitOwnerExists`,
`SourceGroupAddressFromAnotherProject`, `CannotKeepGroupAddressesInDifferentInstallations`.
Validators: `ChangeGroupAddressValidator`, `MoveGroupAddressValidator`, `GroupAddressStrategyValidator`,
`GroupAddressStyleValidator`, `Check16BitGroupAddressValidator`, `Copy16BitGroupAddressValidator`,
`GroupRangeAddressStyleValidator`, `DeleteGroupAddressSecurityValidator`,
`LinkGroupAddressSecurityValidator`, `LinkGroupAddressSecurityModeValidator`.

**Multi-selection GA paths (observed).** `CopyGroupAddressOperation`, `MoveGroupAddressOperation`
and `DeleteGroupAddressOperation` each expose `GetMultipleItemLocalizedDescription` /
`GetMultipleItemDescription` alongside the single-item pair. The metadata string table additionally
holds `get_CopyMultipleGroupAddresses`, `get_MoveMultipleGroupAddresses` and
`get_DeleteMultipleGroupAddressesDescription` property getters (their declaring type was not part of
a selected type set, so it is not pinned here). Multi-item execution is therefore the same operation
object over a selection, with one undo entry and one progress description.

## 8. Links: connector model, flags, DPT compatibility, sending address

**The link is a `Connector`, not an edge in a list** (`ev_63435992`). `Project.Connector`
(30 methods): `Send`, `Acknowledge`, `Slot`, `OnRfFastAckSlotChanged`, `OnGroupAddressChanged`,
`GroupAddress`/`GroupAddressId`, `ParentComObjectInstance`, `ComObjectInstanceRefId`, `Functions`,
`ParentDevice`, `PopulateConnectorAtGroupAddress`, `WriteTo`, `GenerateId`/`CreateId`/`ReadId`.
`ConnectorUndoRedoManager` (11 methods) owns `Add`, `Remove`,
`RemoveWithoutCheckSending`, `CheckSendingConnector` — the **single sending connector invariant**.
A `GroupAddress` collects `Connectors` (`ConnectorsCount`), and `BusInterface` links use
`BusInterfaceConnector`/`LinkGroupAddressToBusInterfaceOperation`.

**KO flags and DPT on the object side** (`Project.ComObjectInstance`, `ev_bb4bf72d`):
`ReadFlag`/`WriteFlag`/`CommunicationFlag`/`TransmitFlag`/`UpdateFlag`/`ReadOnInitFlag`/`MayReadFlag`
with `Raw*` counterparts for persistence, `IsActive` (+`SetActivityToFalseIfNotSetYet`,
`SetActivityDirect`, `HasDefaultValues`, `SetDefaultFlags`), `Priority`, `ObjectSize`,
`DatapointTypeIds`/`DatapointTypes`/`DatapointTypesCollection`/`DatapointTypesString` with
`JoinDatapointTypes`, `AddDatapointTypes`, `RemoveDatapointTypes`, `IsPotentialSender`,
`IsPotentialReceiver`, `SendingConnector`, `PopulateConnectorsAtGroupAddresses`, `Direction`.
Flag edits are separate undoable operations in `Operations.Flags`:
`ChangeCommunicationFlagOperation`, `ChangeReadFlagOperation`, `ChangeReadOnInitFlagOperation`,
`ChangeTransmitFlagOperation`, `ChangeUpdateFlagOperation`, `ChangeWriteFlagOperation`, each with a
matching `KnxSecure.*GrpFlagHandler` (security side effects).

**Compatibility contract (observed taxonomy).** `GroupAddressAssignmentErrors` enumerates exactly what
a link attempt can fail on: `DifferentObjectSize`, `DifferentDatapointTypes`, `DifferentLongMainTypes`,
`AlreadyAssigned`, `AssociationLimitReached`, `AddressLimitReached`, `CommunicationObjectNotActive`,
`DeniedByPlugin`, `DeviceLockedForEditing`, `DifferentInstallation`, `DeviceIsBroken`,
`GroupAddressGreater15BitWouldCreated`, `CompatibilityModeRequired`, `FastAckSlotLimitReached`,
`ComObjectSecureAndPlainComObjects`, `SecurityModeOnAndOff`, plus six security-limit reasons.
`LinkGroupAddressValidator.Validate` is the validation entry for a link attempt
(`GroupAddress.ObjectSizeViaConnectorsOrDtps` is the derived size used when no DPT is set).

**Link editor UI (observed types, `ev_90ffec80`).**

- `ViewModel.ViewModel.LinkWithViewModel` (`0x060004da` ctor, 9 parameters): `SearchBox`,
  `SearchText`, `SearchTexts` (`Dictionary<string,string>`), `ObjectsToLink` (collection) and
  `ObjectToLink` (single), `OkCommand`/`OnOkCanExecute`/`OnOkExecuted` (`0x060004ff`),
  `GroupAddressTotalCount`/`GroupAddressCurrentCount` (`0x060004f3`/`0x060004f5`) — the GA
  counter in the dialog header, `GroupAddressesView`, `GroupAddresses`, `FreeGroupRanges`,
  `SelectedFreeGroupRange`, `IsFreeDisplayStyle` (`0x06000518`), `NewGroupAddress` (`0x06000507`),
  `NewGroupAddressName`, `NewGroupAddressNameMaxLength`, `SecureVisibility`, `IsSuggestedName`,
  `SetUpFooterCommands`.
- `ViewModel.ViewModel.LinkedGroupObjectViewModel`: per-object row with `Direction`, `Sending`
  (`0x0600047b`), `SendingConnector` (`0x06000494`), `Dpt` (`0x0600047f`), `Length`, `Priority`,
  all five flag getter/setter pairs (`CommunicationFlag`, `AckFlag`, `ReadFlag`, `WriteFlag`,
  `TransmitFlag`, `UpdateFlag`, `ReadOnInitFlag`), `MayReadFlag`, `GroupAddresses`,
  `GroupAddressesReadOnlyString`, `IsSecure`, `IsLockedForEditing`, `Room`, `Trade`,
  `Product`, `Program`, `Number`.
- `ContextMenu.Commands.LinkWithCommand` (`CanExecute`, `Execute`, `GetFlattenedList`) and
  `ContextMenu.Commands.ILinkWithCommand`; the flattened list is what feeds `ObjectsToLink`.
- `Dialogs.LinkWithHeaderViewModel`.

**GA counterview (observed, `ev_7763419b`).** `Sidebar.GroupAddresses.SidebarGroupAddressesViewModel`:
`SearchBox`, `SidebarGroupAddressViewType`, `ChangeGroupAddressPresentationCommand`, `DeleteCommand`,
`GroupsTreeView`, `Setup`. `SidebarGroupsTreeViewViewModel`: `Items`, `CurrentType`, `SelectedItems`,
`Tokenizer`, `DragDropHandler`, `ActivateGroupAddressPresentation`,
`ActivateBuildingsPresentation`, `ActivateRecentlyUsedGroupAddressPresentation`,
`FilterForSearchString`, plus a generated `CalculateFilteredItems`/`AddParentBuildings` helper —
filtering keeps ancestor building nodes. The three `Activate*Presentation` methods (derived: the
counterview modes group addresses / buildings / recently used) switch the panel presentation.
`GroupAddressDetailViewModel` (`ev_51cd7ef9`) exposes `GroupAddressStyle` (get/set),
`GetGroupAddress`, `SubscriptionFilter`, `UpdateCollection`, `ComputeGroupsAndContext` — the detail
grid is grouped by the same style object.

## 9. Multi-selection

- Infrastructure: `ISingleAndMultiSelectionCommandHandler<T>`,
  `ISingleAndMultiSelectionCommandHandlerAsync<T>`,
  `SingleAndMultiSelectionCommandHandler{Async}Provider<T>`, `ProjectSelectionCommandHandlerBase`,
  `Collections.MultiSelectionObject`, `Service.MultiSelectionKeyService`.
- Counters: `IDetailViewTab` exposes `ItemsCount` and `SelectedItemsCount` (`ComputePreferredTabId`,
  `TabName`, `IconKey`, `Type`, `IsVisible`, `IsSelected`, `CanPrint`, `PrintCommand`).
- Multi-item undo/progress: `OperationBase.GetMultipleItem{Localized}Description` and the
  `GetMultipleItem*` overrides on move/delete/copy operations produce a single aggregate undo entry.
- Concrete arrays that behave as multi-select surfaces: `AllDevicesDetailViewModel`,
  `AllBuildingsDetailViewModel`, `AllAreasDetailViewModel`, `AreaLinesDetailViewModel`,
  `LineDevicesDetailViewModel`, `SegmentDevicesDetailViewModel`,
  `BuildingPartDevicesDetailViewModel`, `FunctionDetailViewModel`, `GroupAddressDetailViewModel`,
  `DeviceActiveComObjectsDetailViewModel`, `ChannelActiveComObjectsDetailViewModel`,
  `ConnectorGroupAddressDetailViewModel`, `BusInterfaceAssociationsDetailViewModel`.
- `LinkedGroupObjectViewModel.GetFlattenedList` (in `LinkWithCommand`) flattens a mixed selection
  (devices → their KOs) before the link dialog.

## 10. Search / replace

`Workspace.FindAndReplaceService` (`ev_7107db4d`, 44 methods): `Matches`, `CurrentItem`,
`CurrentSearchCriteria`, `Results`, `RelatedPanel`, `IsReplaceAllowed`, `CanExecuteSearch`,
`FindNext`, `ReplaceNext`, `ReplaceAll`, `UpdateTextWithReplacement`, `Reset`.
`DummyFindAndReplaceService` is the no-op implementation for panels that do not support it, selected
through `Workspace.DetailView.IFindAndReplaceSupporting`.

Contract per panel: `Interfaces.IFindAndReplaceAble` = `FindResults`, `NextResult`,
`ReplaceCurrentResult`, `ReplaceAllResults(1p)` — the panel implements the search, the service drives
it. `Workspace.SearchCriteria` (5-parameter constructor) = `TextToFind`, `ReplacementText`,
`IsCaseSensitive`, `MatchWholeWord`, `IgnoreColumnSelection`.
`ViewModel.Sidebar.FindAndReplaceViewModel` (sidebar panel) exposes `FindString`, `ReplaceString`,
`FindNext`, `ReplaceNext`, `ReplaceAll`, `IsCaseSensitive`, `MatchWholeWord`,
`IgnoreColumnSelection`, `Columns`, `Service`, `CurrentPanelContent`.

Consequence (derived): replace in group addresses is **column-scoped replace inside the active detail
grid**, not a project-wide GA renumbering. It rewrites cell text (names/comments) through the same
edit path; address values are a separate concern handled by `Change/MoveGroupAddressOperation`.
There is no "renumber all addresses" search/replace operation in the metadata.

## 11. Import / export

`Operations.GroupAddressImportExport`:

- `ImportGroupAddressesOperation` (`ev_a99dcba1`, methods `0x0600613d`–`0x0600617a`): `Results`,
  `ImportedGroupRanges`, `ImportedGroupAddresses`, `GeneratedGroupRanges`,
  `ExistingGroupAddresses` (`HashSet<u2>`), `SkippedGroupRanges`, `SkippedGroupAddresses`,
  `SkippedDatapointTypes`, `IsUndoable`, `IsCancelable`, `GetValidators`,
  `SpecializedValidate/Execute/Undo/Redo`.
- `ExportGroupAddressesOperation` (`0x0600600a`–`0x0600601c`): three constructors (7, 7, 5 params
  over `GroupRange` / `GroupAddress` collections), `IsUndoable`, `SpecializedValidate/Execute`.
- `CsvFileOperation` plus `CsvFileFormats`: `Header`, `ICsvFileRecord`, `ColumnBuilder`,
  `GroupAddressCsvFileRecordBase`, `GroupAddressCsvRecordUtils`,
  `Converters.{BooleanValueConverter, UshortValueConverter}` and eight record mappers:
  `{One,Two,Three}ColumnsName{One,Two,Three}Columns{Two,Three}LevelAddressRecordMapper`,
  i.e. documented CSV column layouts for both GA styles, with 1–3 address columns and 1–3 name
  columns.
- `OpcExportOperation` (`Knx.Ets.ObjectModel.Operations.GroupAddressImportExport.OpcExportOperation`,
  generic-typed export), `IGroupAddressImportReader` / `IGroupAddressExportWriter` /
  `IGroupAddressImportCallback` extension points for plugins (`Import.Audit` namespace also present).
- Import-generated names come from `GroupAddressImportGeneratedMainGroupName` /
  `GroupAddressImportGeneratedMiddleGroupName`; skipped data is reported through
  `GroupAddressImportSkipInformation` / `GroupAddressImportDatapointTypeSkipInformation`, and the
  UI shows it in `Dialogs.ImportGroupAddressResultViewModel` / `SkippedImportGroupAddressElement`.
- `CsvExportNotAllowedForFreeGroupAddressStructure` blocks CSV export in free style.

## 12. Drag-drop, bulk linking, Alt+V

- Drag-drop stack (`Knx.Ets.Ui.UserControls.Behaviors`): `DragDropBehaviorBase`, `DragDropBehavior`,
  `DragDropMultiSelectBehavior`, `EditChannelDragDropBehavior`, `ProjectTabPageDragDropBehavior`,
  `TabDropBehavior`, `DragDropPeerFactory` with peers `KnxTreeViewDragDropPeer`,
  `RadTreeViewDragDropPeer`, `CollectionDragDropPeer<T1,T2>`, `DataGridDragDropPeer`,
  `GroupedDataGridDragDropPeer`, `ListBoxDragDropPeer`, `RadTabControlDragDropPeer`;
  `AttachedProperties.DragDropState`, `Enums.DragDropContent`, `Adorners.DragAdorner`,
  `Services.DragAdornerDisplayTextService`. Plugin-side drag data contract:
  `Knx.Ets.Sdk.DragDrop.IDragDataBase`. The GA counterview exposes `DragDropHandler`.
- Bulk link: `GroupAddressRefs.MultiLinkGroupAddressOperation` (validate/execute/undo/redo,
  security impacts) and `GroupAddress.LinkGroupAddressOperation.BuildComObjectInstanceRefList`
  + `BuildGaListText` — one operation builds the reference list for many objects and one GA list
  text for the undo description. This is the engine behind linking a selection to one address.
- Paste paths: `ContextMenu.PasteCommandHandler` exposes `Paste`, `PasteExtended`, `PasteSpecial`
  (with `Operations.PasteSpecial.*`, `PasteSpecial.ConnectorCreator`, `ExtendedCopy` namespace).
- **Alt+V: not corroborated.** No `Alt+V`/`InputGesture` string or command gesture was found in
  managed metadata, `ETS6C.exe` string table, or the ViewModel resource strings. The observed
  equivalent is the multi-object `LinkWithCommand` → `LinkWithViewModel` dialog. Treat "Alt+V" as
  unverified UI folklore (or an XAML-level `InputBinding` in BAML we did not decompress) —
  confidence **low**, and do not implement a shortcut on the assumption it exists in ETS.

## 13. Validation and errors

Two layers:

1. **Operation validators** (`Operations.Validators.*`, ~60 types): per-operation `Validate`
   returning `ValidationResult`/`ValidationFailedOperation`, aggregated by `OperationValidator`
   and surfaced before execution. Notable: `LinkGroupAddressValidator`,
   `GroupAddressStrategyValidator`, `MoveGroupAddressValidator`, `ChangeGroupAddressValidator`,
   `CouplerValidator`, `DomainAddressValidator`, `AreaAddressValidator`, `DeviceIsLockedForEditingValidator`,
   `BrokenDeviceValidator`, `BrokenProjectValidator`, `MoveAcrossProjectValidator`,
   `SplitAndMergeGroupAddressValidator`, `MaxSecureGroupKeyTableEntryValidators.*`.
2. **Project check wizard** (`Application.Wizards.ProjectCheck`): `GroupAddressCheckViewModel`,
   `TopologyCheckViewModel`, `DeviceCheckViewModel` and per-check classes —
   GroupAddresses: `GroupAddressesAssociatedCheck`, `GroupAddressIdentifiersCheck`,
   `GroupRangesValid`, `Associated16BitGroupAddressesCheck`, `BrokerForBatteryPoweredDevicesCheck`;
   Topology: `CouplerAddressCheck`, `SegmentCouplerCheck`, `RepeaterCheck`, `ChokeCountCheck`,
   `PowerConsumptionCheck`, `RouterTopologyCheck`, `BackboneLineIpCheck`,
   `TopologyIdentifiersCheck`.
   Resource keys observed for these checks (supporting scan): `IDS_CHECK_ADDRESS_16_BIT_SET`,
   `IDS_CHECK_GROUPADDRESS_0_IS_INVALID`, `IDS_CHECK_GROUPADDRESS_TOO_MANY_GROUPACKNOWLEDGER`,
   `IDS_CHECK_LINE_BUS_CURRENT*`, `IDS_CHECK_LINE_NO_POWER_SUPPLY*`,
   `IDS_CHECK_LINE_TOO_MANY_POWER_SUPPLIES*`, `IDS_CHECK_LINE_TOO_MANY_REPEATERS`,
   `IDS_CHECK_LINE_CHOKE_PS_MISMATCH*`, `IDS_CHECK_NO_SEGMENT_COUPLER_IN_SUBSEGMENT_PRESENT`,
   `IDS_CHECK_AREA_NO_MEDIACOUPLER_IN_MAINLINE_PRESENT`, `IDS_CHECK_MIXED_IP_AND_NONIP_COUPLERS`,
   `IDS_CHECK_MISSING_IP_BACKBONE`, `IDS_CHECK_DEVICE_MUST_NOT_HAVE_ADDRESS`.

Error surfaces are typed enums, not strings: `GroupAddressAssignmentErrors` (36 values),
`GroupAddressValidationReason`, `AreaValidationReasons` (19), `LineValidationReason`,
`SegmentValidationReason`, `BuildingPartValidationReason` (19), `DeviceAddErrors`,
`CouplerCapability`. Media/limit rules embedded there: address range, uniqueness, 15-bit vs 16-bit
ownership, association limit, fast-ack slot limit, secure address limits (local/remote/bus-interface),
broken/for-editing-locked devices, split/merge restrictions, medium-type mixture.

## 14. Full function inventory (operation layer)

Verified type names from the ObjectModel metadata; all inherit the
validate/execute/undo/redo operation contract unless noted.

**Topology/area/line/segment:** `AddAreaOperation`, `AddAreaWithSpecifiedNamesOperation`,
`ChangeAreaAddressOperation`, `DeleteAreaOperation`, `CopyAreaOperation`, `CopyAreaExtendedOperation`,
`MoveAreaAcrossProjectOperation`, `AddLineOperation`, `AddLineWithSpecifiedNamesOperation`,
`AddLineWithSpecifiedMediumTypeOperation`, `AddLineToOperation`, `ChangeLineAddressOperation`,
`DeleteLineOperation`, `MoveLineOperation`, `CopyLineOperation`, `CopyLineExtendedOperation`,
`MoveLineAcrossProjectOperation`, `AddSegmentOperation`, `MoveSegmentOperation`,
`CopySegmentOperation`, `CopySegmentExtendedOperation`, `DeleteSubSegmentOperation`,
`ChangeMediumTypeOfSegmentOperation`, `ChangeDomainAddressOperation`,
`MoveSegmentAcrossProjectOperation`, `SplitProjectOperation`, `ShrinkProjectOperation`,
`MergeProjectsOperation`, `MergeDifferentProjectsOperation`, `MergeMasterWithSplitProjectsOperation`,
`RestoreProjectSyncOperation`, `CreateProjectSyncOperation`.

**Devices:** `AddDeviceOperation`, `AddDeviceWithProductAndHardwareToProgramOperation`,
`CreateDeviceFromTemplateOperation`, `DeleteDeviceOperation`, `CopyDeviceOperation`,
`CopyDeviceToSegmentExtendedOperation`, `CopyDeviceToBuildingPartExtendedOperation`,
`CopyDeviceToAllDevicesCollectionExtendedOperation`, `MoveDeviceAcrossProjectOperation`,
`ChangeDeviceAddressOperation`, `ChangeApplicationProgramOperation`,
`UpdateApplicationProgramOperation`, `ReplaceProductOperation`, `TransferParametersAndFlagsOperation`,
`TransferDeviceIdentityOperation`, `ResetParameterValuesToDefaultOperation`, `SetSerialNumberOperation`,
`SetSlowResenderOperation`, `ParkAddressOperation`, `ChangeAssignedIpAddressOperation`,
`ChangeAdditionalAddressesOperation`, `AddAddressToAdditionalAddressesCollectionOperation`,
`AddDeviceBinaryDataOperation`, `DeleteDeviceBinaryDataOperation`, `AddDeviceCertificateOperation`,
`DeleteDeviceCertificateOperation`, `TakeOverCertificateOperation`,
`InternalizeProductDataOperation`, `InternalizeProductDataWithoutCatalogOperation`,
`UpdateDeviceTemplateStoreOperation`, `DownloadOperation`, `DoWhenTargetOperation`.

**Building/function/link-to-location:** `AddBuildingPartOperation`, `DeleteBuildingPartOperation`,
`MoveBuildingPartOperation`, `CopyBuildingPartOperation`, `CopyBuildingPartExtendedOperation`,
`AddFunctionOperation`, `DeleteFunctionOperation`, `MoveFunctionOperation`, `CopyFunctionOperation`,
`LinkDeviceToBuildingPartOperation`, `UnlinkDeviceFromBuildingPartOperation`,
`LinkDeviceToSegmentOperation`, `UnlinkDeviceFromLineOperation`, `UnlinkDeviceOperation`,
`LinkDeviceToTradeOperation`, `UnlinkDeviceFromTradeOperation`,
`AddGroupAddressRefToFunctionOperation`, `LinkGroupAddressRefToFunctionOperation`,
`LinkComObjectInstanceToUsageOperation`, `UnlinkComObjectInstanceFromUsageOperation`,
`UnlinkConnectorOperation`, `RenameChannelInstanceOperation`.

**Group addresses/ranges:** `AddGroupAddressOperation`, `AddGroupAddressWithSpecifiedNamesOperation`,
`AddAdditionalGroupAddressesOperation`, `DeleteAdditionalGroupAddressesOperation`,
`ChangeGroupAddressOperation`, `ChangeGroupAddressDatapointOperation`,
`ChangeGroupAddressStyleOperation`, `MoveGroupAddressOperation`, `CopyGroupAddressOperation`,
`CopyGroupAddressExtendedOperation`, `DeleteGroupAddressOperation`,
`SetSecurityModeForGroupAddressOperation`, `LinkGroupAddressOperation`,
`MultiLinkGroupAddressOperation`, `UnlinkGroupAddressOperation`,
`LinkGroupAddressRefToComObjectInstanceOperation`, `UnlinkGroupAddressRefOperation`,
`LinkGroupAddressToBusInterfaceOperation`, `UnlinkGroupAddressFromBusInterfaceOperation`,
`AddGroupRangeOperation`, `AddFreeGroupRangeOperation`, `AddThreeLevelMainGroupOperation`,
`AddThreeLevelMiddleGroupOperation`, `AddTwoLevelMainGroupOperation`,
`ChangeGroupRangeAddressOperation`, `CopyFreeGroupRangeOperation`,
`CopyGroupRangeToFreeGroupRangeExtendedOperation`, `CopyGroupRangeToRootGroupRangeExtendedOperation`,
`CopyGroupRangeToThreeLevelMiddleGroupExtendedOperation`, `CopyThreeLevelMainGroupOperation`,
`CopyThreeLevelMiddleGroupOperation`, `CopyTwoLevelMainGroupOperation`, `DeleteGroupRangeOperation`,
`MoveFreeGroupRangeOperation`, `MoveGroupRangeOperation`, `SplitFreeGroupRangeOperation`,
`JoinFreeGroupRangeOperation`, `ShiftFreeGroupRangeOperation`, `StretchFreeGroupRangesOperation`,
`ExportGroupAddressesOperation`, `ImportGroupAddressesOperation`, `CsvFileOperation`,
`OpcExportOperation`.

**Flags/flags-security:** `Flags.ChangeCommunicationFlagOperation`, `ChangeReadFlagOperation`,
`ChangeReadOnInitFlagOperation`, `ChangeTransmitFlagOperation`, `ChangeUpdateFlagOperation`,
`ChangeWriteFlagOperation` (+ `KnxSecure.*GrpFlagHandler` per flag change,
`ResetSegmentCouplerFlagsSmart`, `ResetCouplerFlagsSmart`, `ResetAppropriateCouplerFlagsAfterOperation`,
`ResetCouplersAffectedByLastOperation`).

**Infrastructure:** `OperationBase`, `Operation`, `ProjectRelatedOperation`, `NewItemOperation`,
`AddOperation`, `DeleteOperation`, `MoveOperation`, `CopyOperation`, `CreateFromTemplateOperation`,
`SplitAndMerge`, `ExtendedCopy` (+ `ExtendedCopyAddressStrategy`/`ExtendedCopyFreeGroupAddressStrategy`),
`PasteSpecial.ConnectorCreator`, `Coupler`, `CouplerCheck`, `DeviceFlagService`/`IFlagHandler`,
`GroupAddressService` (in `Operations.KnxSecure`), `DefaultNames`, `ValidationResult`,
`ValidationFailedOperation`, `OperationValidator`, `MessageBus`.

## 15. BusChord gap (base `12fc904`, product repo)

What exists today:

- `src/Knx.Core/Model.cs` (126 lines): `Installation → Area → Line → Segment → DeviceInstance`,
  `GroupRange` with `RangeStart`/`RangeEnd`, `GroupAddressEntry` with a single `DatapointType`
  string and `Puid`, `Installation.GroupAddressStyle` (default `ThreeLevel`),
  `ChannelInstance`, `ComObjectInstanceRef.Links` (list of GA ids), per-device
  `ParameterValues` dictionary for the BusChord XML extension.
- `Knxproj.cs` (514 lines) reads/writes Area/Line/Segment/Device, `GroupRange`/`GroupAddress`
  (Address, Name, DPT, Puid), `ComObjectInstanceRef` `Links` attribute.
- `GroupLinks.cs` (35 lines): `Link`/`Unlink` by reference-id with project-membership and
  uniqueness validation; returns bool; callers push undo.
- `Workbench/WorkbenchProject.cs`: `ValidateLink` (inactive KO, missing DPT/bits, bit-length
  mismatch, full-DPT mismatch, secure-capability), `AddLinks` (bulk, atomic, single undo),
  `SetSending` (only on an existing link of an active object), `RemoveLink` (clears sending with a
  warning instead of auto-repairing), `Associations`, `ConfigurationSignature`.
- `Buildings.cs` (273 lines): `SpaceNode`/`SpaceKind` (ETS `SpaceType_t` incl. `Function`, `Area`,
  `Ground`, `Segment`), device refs, `Usage`, `Puid` preservation.
- `ProjectSearch.cs` (451 lines): `SearchQuery`/`ReplaceQuery` with `SearchScope`
  (All/Devices/GroupAddresses/Topology/CommunicationObjects/Buildings), `Preview`, `Apply`,
  `ReplaceChange`/`ReplacePreview`/`ReplaceResult`.
- `ProjectCheck.cs` (145 lines): P001–P042 — installation present, area/line address bounds,
  multi-segment warning, device address 0, missing product ref, duplicate device address,
  GA without DPT/name, duplicate GA, GA outside range bounds, dangling link id, unlinked GA.
- UI: `WorkbenchWindow.Commands.cs` `ShowBatchLinkDialog` (multi-select KOs → one GA, per-object
  validation preview, atomic apply), `Views/GroupLinksPane.axaml.cs` (per-device KO/GA lists with
  object and address search filters), `WorkbenchWindow.Commands.BuildMenus`.

Concrete gaps against ETS6:

1. **No connector model.** `ComObjectInstanceRef.Links` is a string list; ETS6 has `Connector` with
   `Send`, `Acknowledge`, `Slot`, per-link undo manager and `CheckSendingConnector`. Our sending
   address lives in `WorkbenchMetadata.SendingGroupId` — an out-of-model overlay that survives only
   because the workbench JSON is separate. Links cannot express per-link ack/send or RF slot.
2. **No coupler/segment topology.** No `CouplerDevice`, `HasCouplerDevice`, `DomainAddress`,
   `MediumTypeId` per segment, no area `IsBackbone`/`MainLine`, no coupler capability flags, no
   `ResetSegmentCouplerFlagsSmart` equivalent. `Segment.Address` is a byte, whereas ETS6 segment
   number is an `i4` independent of the line address. Consequence: no segment-coupler validation,
   no bus-current/repeater/PSU checks, no IP/TP/PL/RF medium rules.
3. **No address strategy layer.** No free-address generator, no next-address suggestion, no
   park-address operation, no move-across-project. `ProjectCheck` only reports duplicates; it never
   allocates.
4. **Building tree is structural only.** `SpaceNode` has `Usage`, `Puid`, device refs — but no
   `DefaultLine` per node, no `LocationUsage`/`Type` semantics beyond `Kind`, no `Function`
   group-address refs, no `ContainsFunction`, no "device not compatible with default line medium"
   rule, and no `BuildingPartTypeDistributionBoardCannotBeAdded` asymmetry.
5. **GA model is style-blind.** Every range is a `GroupRange` with sub-range numbering
   (`RangeStart`/`RangeEnd` interpreted as the low byte in `ProjectCheck`); there is no
   `FreeGroupRange`/`OldStyleGroupRange`, no `AddressValue` formatter (two-level/three-level/hex),
   no 16-bit address support, no per-range inheritance of security/unfiltered, and no style
   conversion (`ChangeGroupAddressStyleOperation`) beyond storing the enum and rewriting it globally.
6. **DPT compatibility is a full-string compare.** ETS6 compares object size in bits, full DPT set
   (`DatapointTypeIds` join), "long main types", then security/plain mix and association limits.
   Our `ValidateLink` returns a string on the first mismatch and cannot report multiple reasons;
   the ETS error enum has 36 distinct values we collapse into five messages.
7. **Flags are absent.** No communication/read/read-on-init/write/transmit/update flags, no
   `Priority`, no `MayReadFlag`, no flag-change operations or undo entries, no per-flag secure side
   effects.
8. **Multi-selection is partial.** Bulk link exists (`AddLinks` + dialog); there is no aggregated
   undo description, no `ItemsCount`/`SelectedItemsCount` per tab, no multi-item move/delete/copy
   for devices, GAs or building nodes.
9. **Replace is text-only and preview-based.** ETS6 drives replace through
   `IFindAndReplaceAble` on the active panel with column scoping and `ReplaceNext`/`ReplaceAll`
   semantics; ours is a project-graph walk with a preview object. Both are defensible, but ours has
   no column scoping, no current-item navigation and no "replace in this view only".
10. **Import/export is CSV-only-ish and style-blind.** ETS6 has eight named CSV layouts, an OPC
    exporter, reader/writer/callback plugin interfaces, and skip reporting (ranges, addresses, DPTs).
    Our import path is not in `Knx.Core` at all; `WorkbenchWindow.Commands` only exports
    associations CSV/demo JSON.
11. **Validation is thin.** 16 finding codes vs. ETS6's typed validators + project-check wizard
    matrix (topology power/PSU/repeater/choke/coupler, GA association/16-bit/identifier checks).
12. **Drag-drop absent.** No peer/behavior abstraction, so tree/list reorganisation cannot reuse a
    single multi-select drag pipeline.

## 16. Implementation recommendations (lazy order)

1. **Connector as the link primitive.** Add `Connector { GroupAddressId, Send, Acknowledge, Slot }`
   to `ComObjectInstanceRef` and keep `Links` as a derived view for the existing serializer. Migrate
   `WorkbenchMetadata.SendingGroupId` into `Connector.Send` with a single-sending invariant
   (`CheckSendingConnector` equivalent). One core check: at most one `Send == true` per object, and
   unlink must not silently promote another link.
2. **Typed link errors, multi-reason.** Replace `ValidateLink`'s `string?` with a
   `GroupAddressAssignmentError` flags enum carrying the ETS6 cases that actually occur
   (`DifferentObjectSize`, `DifferentDatapointTypes`, `DifferentLongMainTypes`,
   `CommunicationObjectNotActive`, `AlreadyAssigned`, `AssociationLimitReached`,
   `SecureModeMismatch`). Keep the existing `ValidateLink` signature as a thin adapter so the
   current dialog and tests keep working.
3. **Segment/coupler fields, no behaviour yet.** Add `MediumTypeId`, `DomainAddress`,
   `CouplerDeviceId`, `Number` to `Segment` and `MainLine`/`IsBackbone` to `Area`, serialize them,
   and leave validation to `ProjectCheck`. Add the coupler checks only when a coupler product data
   model exists — otherwise the fields are inert, which is fine.
4. **Address strategies as one small service.** `NextFreeAddress(installation, area?, line?)` plus
   `ParkAddress`. ETS6 needs 13 strategy classes; a single method with a documented preference order
   (line, then area, then installation, skipping parked addresses) covers the observed UI flows.
5. **Free/old-style ranges only if a real project needs them.** `GroupAddressStyle.Free` already
   exists in the enum; adding `FreeGroupRange` + the `GroupAddressValue` parser is justified only
   together with free-format import/export. Otherwise keep rejecting free style loudly.
6. **Flags.** Add the six KO flags + `Priority` to `ComObjectInstanceRef`, persist them, and expose
   one `ChangeFlags` mutation that snapshots undo — not six operations. Group the secure
   side effects behind a single hook.
7. **Column-scoped replace.** Add `column` to `ReplaceQuery` and an "active view only" filter to
   reuse the existing preview/apply pipeline; skip building an ETS-style `IFindAndReplaceAble`
   contract until more than one pane needs it.
8. **Do not build Alt+V.** It is unverified; the multi-object link dialog already exists and is the
   observed mechanism.

## 17. Unknowns and limits

- Static metadata only: no CIL was executed, no CLR load, no decompilation to C#; behaviour is
  derived from operation/method structure and ETS domain semantics, not observed at runtime.
- Internal helpers are name-obfuscated (`yHNt0CDd09u`, `XxEgGCOq3uf`, 5-character patterns); their
  role is unreadable, so exact algorithms (address suggestion order, GA cascade on delete) remain
  inferred.
- `Knx.Ets.ObjectModel.dll` metadata is complete (4024 TypeDefs / 38472 MethodDefs), but two
  selections exceeded the 10 MiB transport budget and were read through paged views; pages are
  complete for the selected types (`coverage.exhausted: true`), not for the assembly.
- UI labels, XAML views and key bindings live in BAML/resources that were not decompressed;
  only resource keys and `#US` strings are available. `Alt+V` therefore stays unverified.
- Old-style/two-level GA semantics, RF multi fast-ack slot allocation and secure group-key limits
  are named but not reconstructed.
- `Knx.Ets.ObjectModel.Operations.GroupAddressRefs.LinkGroupAddressRefToComObjectInstanceOperation`
  tokens in the ledger are the ones reported by the earlier focused inspection and match this build's
  MVID; tokens are build-local and invalid for any other ETS6 build.
- No claim of full ETS behavioural parity. No proprietary code was copied.
