# ETS6 commissioning, bus, security and diagnostics — static reconstruction

Read-only REA analysis of the installed ETS6 6.4.8718.0 (`.NET Framework 4.8`, x86).
No assembly was executed, no bus/router/USB/cloud endpoint was contacted, no keyring,
certificate or password material was read, decrypted or copied. This document is the
only repository change; product sources are untouched.

## Scope, method and limits

Investigated: interface setup and connection types, download variants and flags, device
programming state, download queue and recovery, group/bus monitor and telegram recorder,
value read/write, line and device scan, project check, KNX Secure (keyring, certificates,
secure download, secure tunneling/routing), and failure/retry/audit semantics.

Method:

1. Read-only type inventory of the installed assemblies through `ReflectionOnly` metadata
   (names only, no execution, no dependency code) to locate candidates. This inventory is
   **partial** for large Dotfuscator-treated assemblies: 1 967 of 2 213 Falcon, 678 of 1 055
   HawkNET and 2 962 of 5 359 ViewModel type slots resolved, the rest raised loader
   exceptions. Negative findings are therefore bounded to the named searches.
2. Authoritative REA `inspect_managed_artifact` / `inspect_managed_members` calls against
   exact absolute paths, mostly with explicit `type_names`. Every cited type/member/signature
   below is from a retained REA record in the evidence register. Where a complete response
   exceeded the 10 MiB stdio budget, the retained parent was paged with
   `inspect_analysis_view` (`ev_6d54dd1b…` is such a page of `ev_68d2f092…`).
3. Manifest resource names from `inspect_managed_artifact` (complete detail).

Confirmed limits of this evidence:

- Dotfuscator renames fields/methods and encrypts user string literals in the analysed
  assemblies. **No UI label, timeout constant, retry count or error text was recovered**;
  names, signatures, enum values, call edges and resource names are readable.
- Static call edges show which API a method uses, not that a runtime branch is taken.
  Protocol statements below are structural (frame types, fields, enum values), not observed
  traffic. BusChord's own live captures remain the only wire evidence in this repository.
- REA type selection was complete for the selected types only. Whole-assembly member
  coverage is not claimed. Numeric protocol constraints (max APDU, timing) are exposed as
  members but their values were not read from IL operands.

## Artifact identity

| Tag | Assembly | SHA-256 | Version / TFM | REA artifact evidence |
|---|---|---|---|---|
| EV-HAWK-ART | `Knx.Ets.HawkNET.dll` | `98358825322e75667c8b77ff148ccdad0c78b9ad2f694f93ce02c323399f8aba` | 6.4.8718.0 / net48 | `ev_d498117c71f092455bad47413f3650da3a7658f2be7cac0ad9bc52b15fa80081` |
| EV-FAL-ART | `Knx.Falcon.dll` | `e91cdd8f8510ec76043a671d90cfabc9f89183c00699def98e9fb55544df650e` | 6.4.8718.0 / net48 | `ev_11c846d130b94896ec60485436721b01ea8376bfccc61c6579835b440cde5ddf` |
| EV-MON-ART | `Knx.Falcon.Monitor.dll` | `f9dab23e8a2fd1d8234d719dd86363064455d2a5d7acb96f9c32815433621bd6` | 6.4.8718.0 / net48 | `ev_97f0dcafe1893a9065b4c91dc87196fafdc2ada0aecf7db64b48f2fcc1218f0d` |
| EV-VM-ART | `Knx.Ets.ViewModel.dll` | `236048b0b3992f504488c2fb9890ab2fe73c3a7a7079c0f4d8671d30aefc5c75` | 6.4.8718.0 / net48 | `ev_8e5d5386a5e0468631e2d39bc82bb8b20fe9ee5d904b30515bb9336e94e4de7e` |
| EV-OM-ART | `Knx.Ets.ObjectModel.dll` | `73ffaaa97640a4207819c182101d23d3da73d4c983056f5bca5881c0f02eda8a` | 6.4.8718.0 / net48 | `ev_f2a1befdb5c100bc51a00161598f36789fe1e764b36034ec9a339d23f0cd180d` |
| EV-CRY-ART | `Knx.Bus.Cryptography.dll` | `7bdbc252e1061a23986bb8baaec6ee4d0d714d6a70739e726891dfe01797e533` | 6.4.8718.0 / net48 | `ev_48d348f670d677b4ffd5bdcc0c7fc3a2e6bddae049bd932b3ad0bdb322152d2d` |
| EV-DIAGLIB-ART | `Knx.Ets.DiagnosticLibrary.dll` | `125b07342c1e54616c7d4fa0ab13f416e40faa7ac258191cce84c90797854159` | 6.4.8718.0 / net48 | `ev_5054bc27955dd67ceb4b0211274142fac123e1a81e1c196a7afa778d0cfa4dee` |
| EV-TRV-ART | `Knx.Ets.TelegramRecorderView.dll` | `a8326a90ef15deb4acb0152a7acbe8c2a10888b611ad85e69cb521075c11a5c4` | 6.4.8718.0 / net48 | `ev_95b0a4072b82d13c21a02dfecfa1e83eb5393a5f55f49aabedef8e25de8b13fc` |

`Falcon.dll` (282 064 B) and `Hawk.dll` (425 936 B) are compatibility shells
(`Knx.Ets.Compatibility.Falcon.FalconManaged`, `Knx.Ets.Compatibility.Hawk.HawkManaged`),
not the bus stack. `Knx.Ets.DiagnosticLibrary.dll` is an ETS **support/log diagnostic**
library, not KNX bus diagnostics (see §7).

## Evidence register

| Tag | Content | Evidence |
|---|---|---|
| EV-HAWK-TASKS | `LoadDeviceTask`, `UnloadDeviceTask`, `LoadDeviceOptions`, `InternalLoadDeviceOptions`, `UnloadDeviceOptions`, `HawkTask`, `LegacyProcedure`, `ConnectionModes` — fields, methods, call edges | `ev_309b26b0a3818f256087ad4b72995e1c9f842f6933ce65db0903e70897a41bf2` |
| EV-HAWK-STATE | `DeviceProgrammingState`, `ApplicationProgramOptions`, `DownloadBehavior`, `LoadProcedureStyle`, `HawkConfiguration`, `HawkProgressIndication`, `Impl.LoadProcedure`, `LoadProcedureBuilder`, `LoadControlContext`, `DeviceImage`, `DifferentialImage`, `DownloadLogData`, `ImageAndProcedure` | `ev_02f4a975913604b68c95128ec6679bc1a2f3aef9ffffc06a2edcf176d2b54520` |
| EV-HAWK-NET | `Impl.NetworkFacade`(+`Device`), `FalconNetworkAccessProvider`, `AsyncNetworkAccessProviderExtensions`, exceptions | `ev_319d6de146c007cd76498a60503fce83b29e1f486c290045c250f7c5b760f927` |
| EV-FAL-CONN | Connector configuration types, `BusConnection`, `IRawBusConnector`, `ISharableBusConnector`, `ConnectorMode`, `BusInterfaceConfiguration/Features`, `CommunicationOptions/Path/Builder`, `Discovery.IpDeviceDiscovery`(+`Result`), `SessionType`, `BusConnectionState`, `RouterFeatures` | `ev_9c7ec3cb162bcd75342eba6befc90f6e40af1be8aa9503e4b5eb3f25be596fb6` |
| EV-FAL-SEC | `DataSecurity.*`, `SecureWrapperFrame`, `SecuredServiceFamily`, `SecureServiceType`, `SecureSyncRequestMessage` | `ev_bfec54643b943db4a0de83e74ccb7f75b6cf59da3e9c43f7b695f2afd5f957f4` |
| EV-CRY-KEYRING | `Knx.Bus.Cryptography.Keyring`, `SecureBuffer`, `SecureCryptoEngineEcdh`, `Curve25519` | `ev_5a0ce27bc5e614c8146532c04f3c86e66dd7659a5698247a650eaf597b196682` |
| EV-VM-DL | `DownloadManager` (parent record; 64 methods, 16 fields) | `ev_68d2f092871b565b3ba3d1324200ffec75ae89a61677b0f113a76ba2148512b7` |
| EV-VM-DL-VIEW | Paged methods/fields of EV-VM-DL | `ev_6d54dd1b2c765ee273c4d8c386a2cf7ff2757be9d9dac01237e390c8b04f76c7` |
| EV-VM-DL-SUM | `DownloadManager` summary: 64 methods, 16 fields, 394 call edges in selection | `ev_b6746354fcab337b62caa1ab89f953f7a1f9773f48dd6e519e142d33d5024f12` |
| EV-VM-DIAG | `LineScan`, `ProgModeScan`, `ProgModeScanScope`, monitors, `IndividualAddressDiagnosticViewModel`, `DiagnoseContentPanelViewModel`, `IDiagnoseService`, `DiagnosticWizardViewModel`, `DiagnosticOperationEnum`, `DiagnosticParameter` | `ev_651a2effefbf2378559fb5a2b9039648da0423311f030ddf3267106c02af9c12` |
| EV-VM-MON | `MonitorGridSettings`, `MonitorTrackingService`, `ISidebarMonitoring`, `BusMonitorRunner`, `GroupMonitorRunner`, `MonitorRunnerBase`, `MonitorConnectionManagerViewModel`, `RecordingContext`, `ReadWriteGroupValueControlViewModel`, `MonitorMode`, `MonitorColumn`, `ScanMode` | `ev_685da2f8af56f96ac24dc82346053d76e505226cb9307e2a5ad7af46dc9eb4b5` |
| EV-VM-CHECK | `ProjectCheckWizardViewModel`, `ProjectCheckResult`, `ProjectCheckSettings`, identifier/topology/GA/device/repeater/router/segment/app-update/battery-broker checks, `UriGenerator` | `ev_06655a76bb2421c0d3e87cdb7af7de6afbb07c1b934dabe926280c0cc51b66bf` |
| EV-OM-OPS | `DirtyContext`, `DeviceFlagService`, `DeviceCertificate`, `GroupAddressSecurityStateProvider`, `DownloadOperation`, keyring import/export, certificate operations, `ChangeDeviceSecurityOperation`, `CommunicationPathCalculator` | `ev_22d1c0b56e94ae965276f3d870ac20c13306e870eacd9f00617c23a23cfbbba3` |
| EV-FAL-IP | KNXnet/IP frames and DIBs: connect/CRI/CRD, tunneling CRI/CRD/ACK, routing lost/busy, remote diagnostic/basic-config/reset, device-info/tunneling-info/secured-families DIBs, `ServiceCode` | `ev_e7cb8a3966d9306b0293821cc9c49e5e2868007b301ec31ed8914b797c1d611f` |
| EV-MON-EV | `KnxMessageEvent`, `EventBase`, record start/stop/trigger, connection/comment events, `EventFilter`, `MetadataBlock`, `SecurityBlock`, XML/CSV writers, `Condition` | `ev_d4d902f6843a7a2c26c3a635d8030ffae721dd0cc33dcd81dbdc39f03d8a4fac` |
| EV-VM-RES | ViewModel manifest resources (8 entries) | `ev_7a5b8c5b9e17370dc302fa09672d4b65f049d112906785410e4fd5f02f22aaba` |
| EV-TRV-RES | TelegramRecorderView manifest resources (4 entries) | `ev_72f285d21e054355cd3079eac0dc2fe0469156979ab7cd21cb30ebc6032a163b` |

All 25 records above were re-resolved through `get_evidence_bundle` after the analysis: each
filter returned exactly one retained record (`matching_records: 1`, `complete-record`), with
the artifact SHA-256 matching the table.

## 1. Interface setup and connection types

### Visible workflow

Configuration is a connector-parameter model plus a connection dialog:

- `OnlineOperations.SelectConnectionDialogViewModel`, `ConnectionManager`,
  `CompactConnectionManagerViewModel`, `BusConnectionViewModel`,
  `EditableBusConnectionViewModel`, `ConfiguredIpTunnelingEditableBusConnectionViewModel`,
  `DiscoveredIpTunnelingEditableBusConnectionViewModel` (type names from the ViewModel
  inventory; their members are not part of the DownloadManager selection), plus
  `Application.Dashboard.Settings.Connections.ManageConfiguredConnectionsViewModel`.
- Persisted configurations: `Model.ConfiguredBusConnections` / `IConfiguredBusConnections`,
  `Model.BusConnectionConfiguration`, `Model.BusConnectionOptions`,
  `Model.BusConnectionUIOptions`, `Model.ActiveBusConnection`, `Model.BusConnectionService`.
- Discovery results feed editable connection rows: `IpDeviceDiscoveryResult.GetTunnelingConnections()`
  returns `IpTunnelingConnectorParameters` (EV-FAL-CONN).

### UI resources

`Knx.Ets.ViewModel.Properties.Resources.resources`,
`Knx.Ets.ViewModel.g.resources` (compiled BAML for the dialogs),
`Knx.Ets.ViewModel.Properties.LanguageNames.resources` (EV-VM-RES). Labels are resource-backed;
literal text is Dotfuscator-encrypted, so no label is quoted here.

### Types and methods

**Connector model** (EV-FAL-CONN):

| Type | Observed configuration surface |
|---|---|
| `ConnectorParameters` (base) | `Name`, `AutoReconnect`, `PromiscuousMode`, `SkipInitialization`, `Type`, `IsSecure`, `SupportsSecurity`, `ToConnectionString`/`FromConnectionString`, `LoadSecurityDataAsync`, `AddEncryptedDataToConnectionString` |
| `IpTunnelingConnectorParameters` | `IndividualAddress` (optional), `FallbackToAnyIndividualAddress`, `FromDiscovery` |
| `IpRoutingConnectorParameters` | `BackboneKey`, `MulticastAddress`, `IpPort`, `MulticastTtl`, `LatencyTolerance`, `SyncLatencyTolerance`, `TimerTrust`, `InitialGroupTimer`, `KnxAddress`, `LocalIPAddress`, `EnableIPSystemBroadcast`, `FromDiscovery` |
| `IpDeviceManagementConnectorParameters` | `IndividualAddress`, `FromDiscovery` (direct device management, no tunnel) |
| `IpUnicastConnectorParameters` | `HostAddress`, `SerialNumber`, `MacAddress`, `IpPort`, `ProtocolType`, `UseNat`, `RequiresSecurity`, `UserId`, `UserPasswordHash`, `DeviceAuthenticationCodeHash`, cloud endpoint/connector id/authorization |
| `IoTConnectorParameters` | `KnxAddress`, `InstallationId`, `IoTStackProvider` |

**Bus access and capability model** (EV-FAL-CONN):

- `ConnectorMode`: `LinkLayer`, `Busmonitor`, `Raw`, `Local`, `Info`.
- `BusConnection.GetAsync(ConnectorParameters, ConnectorMode, CancellationToken, progress)`; `Release`/`ReleaseAsync`.
- `IRawBusConnector`: `RawMessageReceived`, `WriteRawAsync`, `SuppressInitializeOnLocalReset`.
- `ISharableBusConnector.CanShare(params, mode)`; `BusConnectorFeatures.ConnectorType`.
- `BusInterfaceFeatures`: `DeviceDescriptor`, `SerialNumber`, `SupportedEmiTypes`,
  `SupportedLocalServices`, `MediumType`, `MaxApduLength`, `RfMultiPhysicalFeatures`,
  `SupportsLocalProgrammingMode`, `BusmonRelativeTimeBase`, `SupportedCommModes`,
  `SupportedFilteringModes`.
- `BusInterfaceConfiguration`: `IndividualAddress`, `DomainAddress`, `IsPlRepeater`,
  `IsRfMulti`, `RfMultiAckConfiguration`, `DefaultRfMode`, `RfMultiLinkLayerTimeouts`,
  `FilteringMode`.
- `SupportedCommModes`: `DataLinkLayer`, `BusMonitor`, `Raw`, `LocalTransportLayer`,
  `RfMultiRetryOption2`; `SupportedLocalServices`: `LocalMemory`, `MProp`,
  `LocalTransportLayer`; `SessionType.UnicastSession`.
- `CommunicationOptions`: `RfMode`, `DoNotChangeRfRxMode`,
  `EvaluateLinkLayerConfirmationOnConnect`, `ConnectFailFast`.

**Hawk facade over the selected interface** (EV-HAWK-NET):

`FalconNetworkAccessProvider` exposes `ConnectorType`, `ConnectorMode`, `ConnectionState`,
`InterfaceFeatures`, `InterfaceConfiguration`, `SetInterfaceConfigurationAsync`,
`ReadLocalProgModeAsync`, `ConnectIfNecessaryAsync`, `TriggerConnectionStateRequest`,
`DisableBackboneSecurityAsync`, `UseSecurityToolKey`, `SecureSyncAsync`,
`ReadAddressesInProgModeAsync`, `ReadSerialNumbersInProgModeAsync`,
`WriteIndividualAddressInProgModeAsync`, `WriteDomainAddressInProgModeAsync`,
`ReadIndividualAddressBySerialNumberAsync`, `DiscoverBySerialNumberAsync`.
`Impl.NetworkFacade` adds `IsTunnelingConnection`, `IsRouting`, `IsLocalUsbConnection`,
`IsDirectDeviceConnection`, `IsCloudConnection`, `SupportsLocalProgMode`, `MaxSupportedGatewayApduLength`,
`LocalAccessType`, `LocalIndividualAddress`, `LocalMediumType`, `LocalDomainAddress`,
`AddPath`, `FixConnection(Async)`.

### Wire/protocol behaviour (static)

- Service codes in `ServiceCode` (EV-FAL-IP) include discovery, connection, tunneling,
  routing, remote configuration and secure services: `SEARCH_REQUEST/RESPONSE(_EXTENDED)`,
  `DESCRIPTION_REQUEST/RESPONSE`, `CONNECT_REQUEST/RESPONSE`,
  `CONNECTIONSTATE_REQUEST/RESPONSE`, `DISCONNECT_REQUEST/RESPONSE`,
  `DEVICE_CONFIGURATION_REQUEST/ACK`, `TUNNELING_REQUEST/ACK`,
  `TUNNELING_FEATURE_GET/SET/RESPONSE/INFO`, `ROUTING_INDICATION/LOST_MESSAGE/BUSY/SYSTEM_BROADCAST`,
  `REMOTE_DIAGNOSTIC_REQUEST/RESPONSE`, `REMOTE_BASIC_CONFIGURATION_REQUEST`,
  `REMOTE_RESET_REQUEST`, `SECURE_WRAPPER`, `SESSION_REQUEST/RESPONSE/AUTHENTICATE/STATUS`,
  `TIMER_NOTIFY`.
- `ConnectRequestFrame` carries control endpoint, data endpoint and `CRI`;
  `ConnectResponseFrame` carries channel id, status, data endpoint and `CRD` (0x060012b9,
  0x060012c3). `TunnelingCRI` exposes `Layer` and optional `IndividualAddress` (0x0600133d,
  0x0600133e); `TunnelingCRD.IndividualAddress` (0x06001336).
- Device description exposes `TunnelingSlotInfo.Address/Status` with
  `Free|Authorized|Usable`, and `TunnelingInfoDIB.TunnelingSlotInfos` + `MaxApduLength`
  (0x06001519). `DeviceInfoDIB` exposes medium code, device status, individual address,
  project/installation id, serial, multicast address, MAC and friendly name
  (`get_DeviceStatus` 0x060014a9); `SecuredServiceFamiliesDIB.HasSecuredServiceFamilies`.
- Remote maintenance: `RemoteResetRequestFrame.ResetCommand`, selectors on
  `RemoteDiagnosticRequestFrame` / `RemoteBasicConfigurationRequestFrame`.
- Routing health: `RoutingBusyFrame.RoutingBusyWaitTime` (0x06001377),
  `RoutingLostMessageFrame.NumberOfLostMessages` (0x06001389),
  `RoutingLostMessageFrame.DeviceState`.

### Data effects

Connector parameters round-trip to and from a connection string and may carry encrypted
security data (`IProtectedDataProvider`). Selected interface, mode and discovery cache are
UI-session state; the connector configuration is the persisted unit. Interface
reconfiguration is an explicit write path (`SetInterfaceConfigurationAsync`) that changes
the interface's own addressing — see the safety boundary.

### Edge cases

- Tunnel address optional with explicit fallback to any individual address.
- Host-addressed (unicast) connectors and the multicast routing connector have separate
  parameter sets; NAT and cloud endpoints are modelled on the unicast side.
- Device-management connector bypasses tunneling for direct device access.
- Local (USB/EMI) access advertises separate features and local programming-mode support.
- Security-required transport is a first-class parameter (`RequiresSecurity`), not inferred.

### BusChord gap

BusChord models exactly one configured host/interface, UDP discovery, tunnel and routing
(`src/Knx.Bus/Discovery.cs`, `KnxTunnel.cs`, `KnxRouting.cs`, `KnxNetIp.cs`). It has **no**
USB HID/EMI connector, **no** device-management (RDAC) connector, **no** connector-parameter
object model/connection-string persistence, **no** local programming-mode query, **no**
auto-reconnect/connect-fail-fast policy, **no** multi-interface profile list, **no** IoT/cloud
path and **no** secure service-family negotiation. Discovery has no extended/V1 search switch
and no slot list.

### Confidence

High for the type/parameter surface and service codes (observed metadata, complete for the
selected types). Medium for which connector ETS prefers per workflow (no runtime evidence).
Unknown: `ConnectorType` enum values, default ports/timeouts, exact dialog order.

### Recommendation and safety boundary

Implement a typed `ConnectorParameters` model with connection-string round-trip, discovery
result → parameter mapping, mode capabilities and a connection-profile store **before**
touching writes. Keep USB and RDAC out until a matching transport exists. Interface
reconfiguration must be a separate, explicitly confirmed operation: it can change the
interface's individual address/domain and therefore affect every later download.

## 2. Download variants and flags

### Visible workflow

`DownloadManager` (EV-VM-DL/EV-VM-DL-VIEW) starts per-device or bulk operations:
`StartLoadDevicesAsync` (two overloads), `StartOverwritePhysicalAddressAsync`,
`StartUnloadDevicesAsync`, `StartResetDevicesAsync`, `StartCompareDevicesAsync`,
`StartReadCustomerAdjustableParametersAsync`, `StartReadDeviceInfoAsync`,
`StartExecuteParameterScriptOnDeviceAsync`, `StartFirmwareUpdateAsync`,
`StartReadGroupAddressValuesAsync`, `StartWriteGroupAddressValuesAsync`.
Each returns a `PendingOperation`; progress/cancel is tracked per operation.

The actual device work is `Knx.Ets.Hawk.LoadDeviceTask.ForDeviceInProject(...)`
(two overloads), and `UnloadDeviceTask.ForDeviceInProject` /
`ForDeviceWithAddress` / `ForDeviceInProgrammingMode`, plus the
`ResetDeviceTask`, `ReadDeviceTask`, `CompareDeviceTask`, `DeviceInfoTask` and
`FirmwareUpdateTask` counterparts (EV-HAWK-TASKS, EV-HAWK-STATE).

### UI resources

Download/monitor/connection labels live in `Knx.Ets.ViewModel.Properties.Resources.resources`
and the compiled BAML (`Knx.Ets.ViewModel.g.resources`, EV-VM-RES). No literal text is
recoverable; the wizard/dialog structure is the visible evidence.

### Types and flags

`LoadDeviceOptions` / `InternalLoadDeviceOptions` (EV-HAWK-TASKS) enumerate the actual
variants. `InternalLoadDeviceOptions` adds `QueryLoadAp2` and
`InternalSerialNumberAndIndividualAddressValid` to the public set below.

| Flag | Meaning (as named) |
|---|---|
| `LoadNetworkConfiguration` | load network-side configuration (address/router/IP/RF) |
| `LoadApplicationProgram` | load the application program |
| `PartialLoadMediumConfiguration` | partial: medium configuration |
| `PartialLoadParameters` | partial: parameters only |
| `PartialLoadGroupCommunication` | partial: group communication tables |
| `ExcludeCustomerAdjustableParameters` | keep device-side customer parameters |
| `LockDevice` | lock device during load |
| `ExpectDeviceInProgrammingMode` | require programming mode |
| `QueryPartialLoadIPConfig` | query whether IP config may be partial |
| `QueryLoadFullApplicationProgram` | query full-program requirement |
| `QueryLoadIndividualAddress` / `QueryLoadDomainAddress` | query address/domain load |
| `QueryLoadNetworkConfiguration` / `QueryLoadPlRepeaterConfiguration` | query network/PL repeater |
| `QueryLoadAp2` | query AP2 handling |
| `InternalUnload` | internal unload pass |

`UnloadDeviceOptions`: `UnloadNetworkConfiguration`, `UnloadApplicationProgram`,
`UnlockDevice`, `DeviceMustBeInProgrammingMode`.

`ApplicationProgramOptions` (EV-HAWK-STATE) contains the compatibility/partial switches:
`PreferPartialDownloadIfApplicationLoaded`, `LegacyNoPartialDownload`,
`LegacyAllowPartialDownloadIfAp2Mismatch`, `PartialDownloadOnlyVisibleParameters`,
`DownloadInvisibleParameters`, `LegacyNoMemoryVerifyMode`, `LegacyNoOptimisticWrite`,
`LegacyNoBackgroundDownload`, `MasterResetOnCRCMismatch`, `PromptBeforeFullDownload`,
`CustomerAdjustableParameters`, `NotLoadable`/`NotLoadableMessageRef`,
`SupportsExtendedMemoryServices`, `SupportsExtendedPropertyServices`, `StandardComTablesExpectable`.

`DownloadBehavior`: `None|Background|DefaultValue`; `LoadProcedureStyle`:
`DefaultProcedure|ProductProcedure|MergedProcedure`; `ConnectionModes`:
`RemoteConnectionOriented|RemoteConnectionless|Local`.

### Image/procedure pipeline

`DeviceImage.CreateImage(...)` builds an image plus a `LoadProcedure`; its static task list
(EV-HAWK-STATE call edges) is:

`VerifyCompatibleMaskVersionTask`, `VerifyCompatibleManufacturerTask`,
`VerifyAdditionalResourcesTask`, `BasicLoadProcedureTask`, `AddCodeSegmentsTask`,
`ApplyFixupsTask`, `ApplyParametersTask`, `ApplyGroupCommunicationTablesTask`,
`PatchApplicationProgramIdTask`, `PatchPeiTypeTask`, `AddIpConfigDataTask`,
`AddIpSecurityConfigDataTask`, `AddRfConfigDataTask`, `AddRouterConfigDataTask`,
`ApplyPlConfigTask`, `CopyLcSlaveMemoryDataTask`, `FinalizeLoadProcedureTask`.

Procedure execution uses `LoadProcedure.ExecuteAsync(image, resolveTextId, getMessage,
byteOrder, maskVersionData, deviceManagement, ct, progress)` and typed load controls
(`AbsSegment`, `RelSegment`, `WriteMem`, `WriteProp`, `CompareProp`, `CompareRangeSpec`,
`LoadImageProp`, `ReadFunctionProp`, `InvokeFunctionProp`, `ClearLCFilterTable`).
`LoadControlContext` carries `EnableSegmentWrite`, `EnableVerifyOnWriteDirect`,
`EnableOptimisticWrite`, `EnableMemoryAutoVerify`, `MemoryDataByteOrder`, plus error mapping.

`LegacyProcedure` holds an interpreter enum and raw `byte[]`; `ImageAndProcedure` bundles
`Image[]`, `LegacyProcedure`, procedure name.

### Partial/differential download

`DifferentialImage` (EV-HAWK-STATE) implements `SerializeImage`/`DeserializeImage`,
`SerializeStamp`/`DeserializeStamp`, `ExtractFromImage`, `VerifyStampAgainstDeviceAsync`,
`DifferenceImage`, `MaskEqualImageData`. `LoadDeviceTask` has `UseDifferentialAsync` and
`HandlePartialDownloadPreliminariesAsync` (call edges). The stamp is compared against device
properties/segments before a partial image is written (`VerifyStampAgainstDeviceAsync`);
the exact accept/reject decision table is obfuscated.

### Mapping to ETS letters (Adr/Prg/Par/Grp/Cfg)

The flag clusters map by name to address/network (Adr), application program (Prg),
parameters (Par), group communication (Grp) and medium/network configuration (Cfg). **The
letter mnemonics themselves were not found**: Dotfuscator encrypts literals and no enum with
those names exists. Treat the mapping as inferred from names, not as observed ETS UI
semantics.

### Data effects

A load takes the project device instance, its application program data, parameters,
commissioning flags and group links, builds a complete device image, and writes it through
device management. It updates the per-device programming state (individual address,
application program, parameters, communication part, medium config, checksums, loaded image,
keys, last download time). Partial loads update only the corresponding state flags.
Unload/reset clear the respective flags. `DownloadLogData` records time, image, procedure,
target mask version, individual address and load options and can be parsed/serialized as XML.

### Edge cases

`AddressAlreadyOccupied`, `SerialNumberAlreadyAssociated`, `NewSerialNumberEncountered`,
`ProceedWithSerialNumber`, `DeviceCertificateNotFound`, `UnexpectedKey`,
`PromptBeforeFullDownload`, `GetDeviceConnection`, `ProgrammingModeAlert`,
`ProvideAccessKey` are all user-interaction callbacks on the load/unload tasks
(EV-HAWK-TASKS). `ApplicationProgramOptions.NotLoadable` aborts with a message; its code
path still updates the programming-state flags (`set_MediumConfigLoaded`,
`set_IndividualAddressLoaded`, `set_CommunicationPartLoaded`, `set_ApplicationProgramLoaded`,
`set_ParametersLoaded` in `ocvf2rM7U7E` call edges). AP2 mismatch, CRC mismatch, table
overflow and unknown mask version have dedicated options/exceptions.

### BusChord gap

`src/Knx.Bus/DownloadSession.cs` is a single hard-coded path: read descriptor (type 0),
write `IApplicationProgram.Image` from `StartAddress` in `ChunkSize` chunks via
`A_MemoryWrite`, read back and compare, then `Restart(erase:false)`. It has:

- no programming-mode entry/test (writes are attempted without it),
- no descriptor-driven memory map, segment handling or load controls,
- no address/domain/network, medium, RF, router or IP configuration load,
- no parameters/group-communication separation, no partial/differential download,
- no access level/authorization, tool key, FDSK or secure sync,
- no unload/reset/compare/device-info/read-customer-parameters operations,
- no AP2/mask/manufacturer/resource verification,
- no download log.

The fixed 3 s response timeout and “write then read back” verify are the only recovery
semantics. This is not an ETS-compatible download and must not be presented as one.

### Confidence

High for flags, task list, differential API and state fields (observed members, complete
selection). Medium for which flag combination ETS chooses per action. Unknown: exact
decision table, procedure XML semantics, retry counts, timeout values.

### Recommendation and safety boundary

Do not extend the current writer. Build a load pipeline that (1) reads descriptor/mask and
refuses unknown masks, (2) enters/verifies programming mode and reports “press programming
button” as a first-class state, (3) builds a memory map from master data resources, (4)
separates address/parameters/group tables/application, (5) verifies with read-back only
where the mask supports it, and (6) logs every step. Partial download must require a valid
stamp; on any mismatch fall back to full load **with explicit user confirmation**. All write
paths stay behind an explicit arm/confirm gate and a dry-run that prints the computed image
and target addresses. No automatic download on project open or selection.

## 3. Device state and dirty logic

### Visible workflow

ETS distinguishes three different “dirty” notions, and they are separate types:

1. **Device programming state** — what was actually loaded into the device
   (`Knx.Ets.Hawk.DataProvider.DeviceProgrammingState`, EV-HAWK-STATE).
2. **Project dirty state** — unsaved project changes (`Knx.Ets.ObjectModel.DirtyContext`,
   EV-OM-OPS).
3. **Derived security state cache** — `GroupAddressSecurityStateProvider` with
   `InvalidateState`/`ResetState` (EV-OM-OPS).

The UI consumes (1) through the per-device flags and (2) through the dirty context; the
verified metadata does not contain a single combined “device dirty” type.

### UI resources

State is surfaced through the download/diagnostic views in `Knx.Ets.ViewModel.g.resources`
and their localized strings in `Properties.Resources.resources` (EV-VM-RES). The flags
themselves are model properties; no dedicated state dialog resource was identified.

### Types and fields

`DeviceProgrammingState` observed properties (getter tokens from EV-HAWK-STATE):

- load flags: `IndividualAddressLoaded` (0x06001e3c), `ApplicationProgramLoaded` (0x06001e3e),
  `ParametersLoaded` (0x06001e40), `CommunicationPartLoaded` (0x06001e42),
  `MediumConfigLoaded` (0x06001e44)
- `LastDownload` (0x06001e3a), `LoadedImage` (0x06001e46), `CheckSums` (0x06001e48)
- identity/transport: `MacAddress` (0x06001e4a), `AssignedIPAddress` (0x06001e4c),
  `RoutingMulticastAddress` (0x06001e4e), `SerialNumber` (0x06001e50),
  `MaxApduLength` (0x06001e52), `UsedApduLength` (0x06001e54),
  `MaxRoutingApduLength` (0x06001e56)
- security: `IpRoutingBackboneKey` (0x06001e58), `DeviceAuthenticationKey` (0x06001e5a),
  `ToolKey` (0x06001e5c), `FDSK` (0x06001e5e), `IoTPassword` (0x06001e60),
  `DeviceManagementPasswordHash` (0x06001e62), `LastValidSeqNr` (0x06001e64)

State is read/written through the project data provider: `IProjectData.GetProgrammingState`
and `SetProgrammingState` appear in load-task call edges (EV-HAWK-TASKS). It is therefore
part of the project, not transient UI state.

`DeviceFlagService` / `IDeviceFlagService` (EV-OM-OPS) resets flags after structural change:
`ResetSubSegmentCouplerCommunicationFlag`, `ResetSubSegmentsDevicesIndividualAddressLoadedFlag`,
`ResetsAllFlags(device)`. `DirtyContext` offers `Dirty`, `DirtyBefore`, `DirtyAfter`,
`DirtyCollection`, `Clean`, `CleanBefore`, `CleanAfter` for project objects.

### Data effects

- A successful full load sets all five load flags plus image/checksums/identity/keys and
  `LastDownload` (the full-load path's call edges set each flag).
- A partial load updates only affected flags; the rest keeps its previous value.
- Topology edits (move/copy/delete device, line, segment) reset communication-part or
  individual-address flags on affected devices via the flag service.
- Editing a project object marks it dirty via `DirtyContext`; the `Clean*` methods define the
  cleaned baseline (their call sites were not enumerated).
- Security edits invalidate the GA security-state cache.

### Edge cases

- A device can be partially loaded (IP config only) while application/parameters remain
  stale; the five flags express exactly that.
- `DeviceCertificateNotFound` clears/repairs `FDSK` and can disable backbone security for
  the session (see §8).
- Exchange/replace product and move-device operations are the cases that require a flag
  reset, otherwise the UI claims a load that never happened.

### BusChord gap

BusChord has a **demo-only** programming session (`src/Knx.App/Workbench/Sessions/ProgrammingSession.cs`):
`DownloadedDeviceState(DeviceId, Signature, At, Origin)`, `NeedsProgramming(deviceId)` comparing
the current `DeviceConfigurationSnapshot.Freeze` SHA-256 signature against the last recorded
signature, and a JSON export that deliberately stores signatures only. This is real value:
a device move/parameter/link change changes the signature and marks the device as needing
programming.

It is **not** ETS device state: `SessionOrigin.Demo`, no device read-back, no individual-address/
application/parameter/communication/medium flags, no image or checksum, no identity or security
keys, and the snapshot is “known native fields only, not a downloadable application image”.
State lives in the session plus a side file, not in the project model. Load-state semantics
(partial vs full) and security state are absent.

### Confidence

High for fields and persistence calls (observed). Medium for the exact reset matrix, because
the flag service methods are named but their callers were not enumerated exhaustively.

### Recommendation and safety boundary

Extend the existing `DownloadedDeviceState`/`NeedsProgramming` model rather than adding a
second one: keep the configuration signature as the drift detector and add the five ETS load
flags, image hash, identity and security-key **references** (not key bytes in plain project
files) plus `lastDownload`. Move it from the session side file into the project model (or a
clearly versioned companion), and reset the relevant flags on topology/product operations.
Keep project-dirty and device-load-state as separate UI signals. Never infer “loaded” from
the presence of an image or a signature alone.

## 4. Download queue, timeouts and recovery

### Visible workflow

`DownloadManager` (EV-VM-DL-VIEW, 64 methods) owns:

- queue: `PendingOperations`, `DevicesAwaitingStart`, `FinishedOperationsSinceLastSessionStart`,
  `SetUseSerialNumberForActiveAndWaitingOperations`, `ClearHistory`
- control: `RequestCancel`, `CancelAll()` and overloads taking a cancellation/time value,
  `get_DelayBetweenUnload` (token 0x060034de, row offset 7 117 848)
- operation kinds: `HawkPendingOperation`, `PendingGroupAddressOperation`,
  `PendingReadGroupAddressValuesOperation`, `PendingWriteGroupAddressValuesOperation`,
  `PendingScriptedOperation`, `FailedPendingOperation`
- conflict check: `OperationConflictCheck`; interface check: `InterfaceSettingsValidator`
- metrics: `Metrics.DownloadEventTypes`

Bulk starts (`StartLoadDevicesAsync`, `StartUnloadDevicesAsync`, …) accept a device set, a
mode, a “use serial number” flag, an overwrite flag, an address and a cancellation token, and
return an array of pending operations. `UnloadDeviceInProgrammingModeAsync` is a separate
entry point.

### Types and methods

`PendingOperation` is the queue item base; `FailedPendingOperation` keeps a failure for the
history; `HawkPendingOperation` wraps a Hawk task and its progress. `DownloadManagerOptions`
holds manager-level policy; `IDownloadUserInteractionManager` abstracts the user decisions
(enumerated as callbacks in §2).

### UI resources

The queue has no separate resource set; its rows and progress are rendered by the download
views whose BAML/strings are in EV-VM-RES. `HawkProgressIndication` (`Text`,
`PercentComplete`, `UserActionRequired`, `CanSkip`) is the model that supplies the text.

### Wire/protocol behaviour

The queue operates on Hawk tasks (`HawkPendingOperation`), so it inherits the selected
interface and its recovery primitives: `ConnectorParameters.AutoReconnect`,
`CommunicationOptions.ConnectFailFast`, `NetworkFacade.FixConnection(Async)`,
`TriggerConnectionStateRequest`, `ConnectionStateRequestFrame` and
`BusConnectionState.{Connected,Closed,Broken,MediumFailure}` (EV-FAL-CONN, EV-FAL-IP).
`LoadDeviceTask.WaitUntilKnxRestartCompleteAsync` and `RecheckAddressUniquenessAfter`
are the post-load recovery hooks (EV-HAWK-TASKS type list / properties).

### Data effects

Queue state is session state plus history. Each completed operation updates the device
programming state; failures are kept in `FailedPendingOperation` for retry/report. No queue
state is written into the project (static inference from types; no persistence member found).

### Edge cases

- Operations on the same device must not overlap (`OperationConflictCheck` exists); the exact
  conflict rule is not statically established.
- Unload between loads is delayed by `DelayBetweenUnload` to let the device settle.
- Address uniqueness is re-checked after a configurable interval.
- Cancel is cooperative (`RequestCancel`); bulk cancel has overloads taking a cancellation or
  time value.
- Failed operations stay in history (`FailedPendingOperation`); any retry is a new start call
  (no automatic-retry member observed).

### Timeouts and retry — what is actually known

Known **structure**: reconnect policy, connect-fail-fast, connection-state probing, restart
wait, uniqueness re-check, operation cancellation. **Not known**: numeric timeouts, retry
counts, backoff curve and “passes” semantics. These are constants inside obfuscated methods;
the only visible timeout is BusChord's own 3 s (`DownloadSession.cs:28`), not ETS's.

### BusChord gap

BusChord already has a queue in the workbench (`ProgrammingSession.cs`): `ProgrammingJob` with
`Queued|Running|Done|Failed`, an `Attempts` counter, `Retry` for failed jobs, `Cancel`, a
per-interface lease (`InterfaceCoordinator.TryAcquire(..., InterfaceActivity.Programming)`),
per-device busy protection, and an in-session downloaded-state record. Gaps:

- execution is **demo-only** (`SessionOrigin.Demo`, 150 ms simulated task) and is not wired to
  `DownloadSession` or a real transport,
- no deadline/per-operation timeout, no bulk API, no absolute queue order/priority,
- no `NeedsUser` state (programming button, key, certificate, address occupied),
- no attempt log/history with interface identity and failure cause (only the last error),
- no restart-completion wait, address re-check or reconnect sequencing,
- no progress model beyond job status.

### Confidence

High for queue surface and recovery hooks; low for numeric policy.

### Recommendation and safety boundary

Extend `ProgrammingSession` rather than adding a second queue: add explicit
waiting-for-user/succeeded/failed/cancelled states (it already has queued/running/done/failed),
a deadline per operation, a persisted attempt entry per run, and a bulk start API. Recovery
must reconnect and **re-read the device state** before retrying; never blindly replay a partial
write. Numeric timeouts stay configurable and are logged with each attempt so field data can
tune them.

## 5. Group monitor, telegram recorder, bus monitor, value read/write

### Visible workflow

Three monitor modes exist (`MonitorMode`): `Busmonitor`, `Groupmonitor`,
`BusActivityMonitor` (EV-VM-MON). Entry points are `IDiagnoseService.GetBusMonitor`,
`GetGroupMonitor`, `GetBusActivityMonitor` (EV-VM-DIAG), plus the command-line runners
`BusMonitorRunner` / `GroupMonitorRunner` (both derive from `MonitorRunnerBase`, which can
open a project, open a monitor panel and reuse a running monitor).

`MonitorConnectionManagerViewModel` owns the monitor connections: `Connections`,
`CurrentConnection`, `SetCurrentConnection`, `DisconnectCurrentConnection`,
`ShowFalconConnections`, `LocalSettings`, `IsAnyBusActionRunning`, `LogSessionKeys`
(flag as security-sensitive), `IsDropDownOpen`.

`RecordingContext` carries `AddressResolver` and `SecureKeyService`, i.e. monitor rows can
resolve names and decrypt secure telegrams.

`ReadWriteGroupValueControlViewModel` is the value read/write surface: `GroupAddress`,
`ProjectGroupAddress`, `GroupAddressString`, `SelectedDatapointType`, `AvailableDatapointTypes`,
`Value`, `ReadCommand`, `WriteCommand`, `StopReadCommand`, `StopWriteCommand`,
`CyclicSending`, `DelayOrCycleTime`, `IsReading`, `IsWriting`, `LastValueRead`,
`IsRecording`, `GroupMessageReceived`.

The telegram recorder add-in (`Knx.Ets.TelegramRecorderView`, EV-TRV-ART) provides
`LiveLogger`, `CsvExporter`, `StatisticReport`, `AddressResolver`, `SecureKeyService`,
`GroupMessageSender`, `BusActivityMonitorViewModel`, option pages for coloring and trigger
conditions, and long-time recording options.

### UI resources

`Knx.Ets.ViewModel.Properties.Resources.resources` (+ `g.resources`) and
`Knx.Ets.TelegramRecorderView.Properties.Resources.resources` (+ `g.resources`)
(EV-VM-RES, EV-TRV-RES).

### Types, columns and files

`MonitorColumn`: `Number`, `Timestamp`, `Service`, `Flags`, `Priority`, `Source`,
`SourceName`, `SourceDescription`, `Destination`, `DestinationName`,
`DestinationDescription`, `BuildingPart`, `BuildingFunction`, `Routes`, `Dpt`, `Type`,
`Info`, `Iack`, `MediumInfo`, `Security`, `Interface`, `AdditionalAddInColumn1..5`.
`MonitorGridSettings` stores each column plus width percentages.

Recorder options: `TelegramMonitorSettings`, `TelegramMonitorSettingsCollection`,
`TelegramMonitorTimeFormat`, `LongTimeRecordingRotationInterval`,
`LongTimeRecordingFilenamePostfix`, `LongTimeRecordingDeleteAfter`, `DetailLevel`,
`FrameToColorConversionContext`.

Falcon event model (EV-MON-EV): `KnxMessageEvent` exposes `RawData`, `MessageCode`,
`Flags`, `SourceAddress`, `DestinationAddress`, `DestinationName`, `IsDestinationGroup`,
`Priority`, `Tpci`, `TransportLayerSequenceNumber`, `HopCount`, `FrameFormat`,
`IsRepeated`, `IsInvalid`, `IsExtendedFrame`, `IsSystemBroadcast`, `GroupDataValue`,
`GroupDataTypedValue`, `GroupDataFormattedValue`, `IsDataSecured`, `DecryptionStatus`,
`LinkLayerAck`, `MediumInfo`, `RFInfo`, `IsTransportSecured`, `EnclosedTelegram`,
`DecryptedEnclosedTelegram`. `RecordStartEvent`/`RecordStopEvent`/`RecordTriggerEvent`/
`ConnectionEvent`/`CommentEvent` complete the journal.

### Wire/protocol behaviour

- Bus monitor is a connector mode (`ConnectorMode.Busmonitor`, `SupportedCommModes.BusMonitor`);
  frames arrive as `BusmonitorMessage` with `BusmonInfo` additional info and TP1 link-layer
  ACK (`TP1LinkLayerAckMessage`) (EV-FAL-ART type inventory).
- Group monitor receives group value read/write/response (`GroupValueDataMessage` etc.) and
  decodes `TPCI`, APCI and DPT.
- Serialization supports XML and CSV, and legacy EMI2/common-EMI conversion with checksum
  repair (`SerializationHelper.IsEmi2Possible`, `Emi2ToCemi`, `FixBusmonWithoutCheckum`,
  `FixBusmonTp1StandardFrameSerializedInExtendedFrameFormat`).
- Secure rows: `SecurityBlock.CreateDecryptionBlock(...)`, `DecryptionStatus`,
  `IsTransportSecured`, with `SecureKeyService` from the recording context.

### Data effects

Monitor output is a journal, not the project: live rows, filters, statistics, comments,
record start/stop/trigger markers and exported CSV/XML. `GroupMessageSender` can write group
values; cyclic sending is opt-in via `CyclicSending`. `LogSessionKeys` is an explicit
command whose name implies writing session keys to the log — a deliberate audit/leak
trade-off that BusChord should not copy.

### Edge cases

- Invalid/repeated frames and extended-format TP1 frames are explicitly modelled.
- Unknown source/destination, negative ACK and secure frames are first-class trigger
  conditions (`Condition` presets: `Repetition`, `Invalid`, `Acknowledged`,
  `NotAcknowledged`, `NegativelyAcknowledged`, `UnknownSource`, `UnknownDestination`,
  `Secure`).
- Long recordings rotate by interval, rename with a postfix, and delete old files.
- Stats windowing (`StatisticsBlock`, `StatisticsSnapshot`) can report per-source counts.

### BusChord gap

BusChord has `TelegramRecorder` (CSV/JSONL/XML, ETS CSV, replay) and monitor panes, and it
does have live group/bus monitor sessions. Missing: trigger/conditional recording,
retention/rotation, per-row security/decryption state, statistics report, medium/RF info,
record start/stop/comment markers, column configuration persistence, bus-activity summary and
a documented secure-key policy. `ReadWriteGroupValueControlViewModel`'s cyclic send and
DPT-typed value editing have no direct counterpart in `Knx.Bus` (only raw/typed DPT helpers).

### Confidence

High for types/events/columns; medium for the exact recording file schema (writers observed,
schema not read).

### Recommendation and safety boundary

Reuse `TelegramRecorder` as the storage layer, add a recorder session object (mode, interface,
start/stop, trigger condition, pre/post counts, rotation), and extend the event model with
security/decryption and link-layer ACK fields. Value write controls must be visibly armed,
rate-limited and never cyclic by default. Do not log session keys by default; if at all,
require an explicit diagnostic mode and redact them from exports.

## 6. Line/device scan and diagnostics

### Visible workflow

Two scan paths, both in `Knx.Ets.ViewModel.OnlineOperations.Diag` (EV-VM-DIAG):

- `LineScan.ScanAsync(bus, bool, IProgress<int>, CancellationToken)` — walk a line's address
  range and read device descriptors.
- `ProgModeScan.ScanAsync(target, bus, IProgress<int>, CancellationToken)` — find devices in
  programming mode. The scope is selected separately via `ProgModeScanScope`:
  `Direct`, `TargetDomain`, `Installation`.

`IndividualAddressDiagnosticViewModel` is the device workbench:
`ScanLineAsync`, `ReadAddressesByProgModeAsync`, `ReadDevicesInProgrammingModeCommand`,
`StopReadDevicesInProgrammingModeCommand`, `SetProgFlagsCommand`, `SetProgModeCommand`,
`CheckIndividualAddressCommand`, `StartDiagnosticsForLineScanItemCommand`,
`StartFlashLedCommand`/`StopFlashLedCommand`, `CheckWithProjectCommand`,
`PrintLineScanResultCommand`, `CurrentBusAction`, `CheckResult`, `ProgMode`,
`DevicesInProgrammingMode`, `AnyDomainAddressInProgrammingMode`,
`AnySerialNumberInProgrammingMode`, `ProgModeBroadcastDomain`, `ProgModeErrors`,
`SelectedLine`/`SelectedSegment`, `ScanTargetMediumType`.

`DiagnoseContentPanelViewModel` hosts the monitor and diagnostic tabs and the project-check
result; it can `StartDiagnosticWithIndividualAddress`, `StartDiagnosticWithLine`,
`NavigateToMonitorDetail`, and enforces close rules (`CanCloseDetailTab`,
`DisplayDiagnosticInUseInfo`).

The **Diagnostic Wizard** (`Application.Wizards.DiagnosticWizard`, EV-VM-DIAG) is different:
`DiagnosticOperationEnum.{CollectDiagnosticInformation,CleanupDiagnosticInformation}`,
`DiagnosticParameter.{ExportAsZip, PrintDiagnostic, IncludeSystemInfo, IncludeEventLogs,
IncludeLogfiles, IncludeActiveModules, IncludeLicenses}`. It produces a support bundle.

### UI resources

ViewModel BAML + `Properties.Resources.resources` (EV-VM-RES). `DiagnosticLibrary.Properties.Resources`
exists for the support bundle (EV-DIAGLIB-ART).

### Types and behaviour

- `LineScan` holds bus/progress/service dependencies, `ScanAsync`, line/segment helpers,
  a per-device result list and a “scan target medium” filter.
- `ProgModeScan` resolves its target bus, keeps a result collection and uses
  `ProgModeScanScope` (`Direct`/`TargetDomain`/`Installation`).
- Device checks combine descriptor read, individual-address read/write, programming-mode
  query/set, LED flash and a project comparison (`CheckWithProjectCommand`).
- `IDiagnoseService` exposes group/bus/bus-activity monitors and GA navigation.

### Wire/protocol behaviour (static)

Scans use the standard device-management services exposed by Falcon: device descriptor read
(`DeviceDescriptorReadMessage`/`DeviceDescriptorResponseMessage`), individual address
read/write (`IndividualAddressReadMessage`, `IndividualAddressWriteMessage`), memory read/write
and extended variants, plus programming-mode state from `DeviceInfoDIB.DeviceStatus` /
`DeviceDiagnosticExtensions.GetProgrammingModeAsync`/`SetProgrammingModeAsync`. Programming-mode
device enumeration uses `ReadAddressesInProgModeAsync`, `ReadSerialNumbersInProgModeAsync`,
`WaitForDevicesInProgrammingModeAsync` and `IsDeviceInProgModeAsync` (EV-HAWK-NET).

### Data effects

Scan results and diagnostics are session state; the only project effects are explicit address
or programming-flag writes initiated from the device workbench. `PrintLineScanResultCommand`
exports a report. The diagnostic wizard writes a ZIP/support bundle outside the project.

### Edge cases

- Devices answering in programming mode may be addressed by individual address, domain
  address or serial number; all three collections exist.
- Segment selection is conditional (`AllowSegmentSelection`) and slow devices are flagged.
- Partial reads produce `ErrorText`/`AnyReadError` per line.
- `DiagnosticWizard` is support/log collection, **not** bus diagnosis; mixing them in the UI
  would mislead users.

### BusChord gap

`Diagnostics.cs` already offers address-range scan, line scan, descriptor parsing, address
check and statistics. Missing: programming-mode scan (direct/domain/installation), device
check against the project, set/clear programming mode, flash LED, per-device diagnostics,
segment-aware scanning, slow-device handling, printable scan report and a diagnostic tab
model. There is also no support-bundle wizard; `Knx.Ets.DiagnosticLibrary` has no direct
counterpart.

### Confidence

High for the scan/diagnostic API surface; medium for exact scope semantics (enum names only);
unknown: timeout/progress granularity and which media allow segment selection.

### Recommendation and safety boundary

Implement programming-mode scan as a **read-only** discovery first (query + descriptor, never
set flags). Address/prog-mode writes belong to a separate, confirmed “device service” action
with per-device undo notes and a clear warning that programming mode affects the whole line.
Keep the support bundle separate from bus diagnostics.

## 7. Project check

### Visible workflow

`ProjectCheckWizardViewModel` (EV-VM-CHECK) runs a settings-driven check and returns
`ProjectCheckResult` (`Content`, `Project`). `ProjectCheckSettings` groups:
`DeviceCheck`, `GroupAddressCheck`, `TopologyCheck`, `ProductInformationCheck`,
`PreEts6StyleProjectCheck`. Each check has `Execute(project, content)` and
`UpdateSettingsFlag(bool)`, i.e. the chosen checks persist in user settings.

### Types and checks

- `IdentifiersCheck` — `Ets3MaxNameLength`, `Ets3MaxDescriptionLength`,
  `Ets3MaxNumberLength`, `Identifiers` list; device/GA variants derive from it.
- `BackboneLineIpCheck` — IP backbone addressing.
- `PowerConsumptionCheck` — per line, `T12mO4utBZu(line, content, settings)`.
- `RepeaterCheck` — repeater count per line (`RKFmO3qlJf6(line, ref int)`).
- `RouterTopologyCheck` — router topology (`rMQmOkkly0O(content, context, settings)`).
- `SegmentCouplerCheck` — segment coupler consistency.
- `ApplicationProgramUpdatesCheck` — collects a per-device map of update states (enum type
  not named) and exposes a string list per state.
- `BrokerForBatteryPoweredDevicesCheck` — finds devices needing a broker.
- `UriGenerator` — ETS4 URI/path conversion for report links.

Related object-model pieces: `Operations.Projects.ReferenceChecker`/`ReferenceCheckResult`
and consistency checkers (`ProjectConsistencyChecker`, `DeviceConsistencyChecker`, …) exist,
but the wizard checks above are the commissioning-facing set.

### UI resources

The check wizard and result tree are BAML views under `Knx.Ets.ViewModel.g.resources` with
strings in `Knx.Ets.ViewModel.Properties.Resources.resources` (EV-VM-RES). `UriGenerator`
turns result paths into ETS4-compatible URIs for reports.

### Data effects

Read-only analysis of the project; results are report content (and printable/URI-linked).
Only the check selection is persisted (settings flags). No bus access.

### Edge cases

- Checks are optional and independently toggled; an empty selection produces an empty result.
- ETS3 limits are explicit constants (name/description/number length).
- Battery-powered devices and routers/segment couplers are special topology cases.
- Application-program updates depend on catalog/product data being present.

### BusChord gap

BusChord exposes `project_check` via MCP (`src/Knx.Core/ProjectCheck.cs`) and checks address
ranges, duplicate device addresses, segment count, group addresses, dangling links and missing
datapoint types. Missing: power budget, repeater limits, router/segment-coupler topology,
identifier-length rules, application-program update status, battery-device broker, per-check
settings and a result model with per-check sections/URIs.

### Confidence

High for the check list and settings; medium for exact pass/fail rules (method bodies not
decompiled).

### Recommendation and safety boundary

Implement the checks as pure functions over the project model, each returning
`(severity, message, affected objects)`, and run them in a report-only wizard. No automatic
repair. Power/repeater limits need master-data constants and must state their source; do not
invent limits.

## 8. KNX Secure: keyring, certificates, secure download, tunneling/routing

### Visible workflow

- Project-level: `ExportKeyringCommandHandler` / `ImportKeyringCommandHandler`,
  `ExportKeyringWizard`, `Application.Dashboard.Commands.ExportKeyringCommandHandlerParameter`.
- Device-level: `ChangeDeviceSecurityOperation`, `TakeOverCertificateOperation`,
  `AddDeviceCertificateOperation`, `DeleteDeviceCertificateOperation`,
  `DeviceCertificateCollection`.
- Connection-level: `ChangeSecureTunnelingOperation`, `IpUnicastConnectorParameters.RequiresSecurity`,
  `IpRoutingConnectorParameters.BackboneKey`, `LoadSecurityDataAsync(...)` on all connector
  parameters.

### UI resources

Keyring export/import wizards and the certificate/security editors are BAML views under
`Knx.Ets.ViewModel.g.resources` with localized strings in
`Knx.Ets.ViewModel.Properties.Resources.resources` (EV-VM-RES). Key material itself is not a
resource.

### Types and methods

**Keyring file format** (`Knx.Bus.Cryptography.Keyring`, EV-CRY-KEYRING):

- `Schema`, `ValidateAgainstSchema`, `GetPasswordHash`,
  `GetCreatedHash(string)`, `CreateSignature`, `ValidateSignature`,
  `PreparePasswordStringForEncryption`, `ExtractPasswordString`, `Encrypt`, `Decrypt`;
  `GetPasswordHash` calls `SecureCryptoEngineEcdhCurve25519.GetPbkdf2HmacSha256PasswordHash`
  (0x0600001f → PBKDF2-HMAC-SHA256). Signatures use a SecureBuffer; passwords use strong
  random (`RandomNumbers.FillStrongRandom`). `Curve25519` is present for ECDH.
- `SecureBuffer` wraps key material in a disposable buffer (`Finalize`, `Dispose`,
  `Length`); `SecureCryptoEngineEcdh`/`Curve25519` provide the key agreement.

**ETS keyring operations** (EV-OM-OPS):

- `ExportKeyringOperation.CreateWholeProjectKeyringExport(...)` and
  `CreateInterfaceInformationExport(...)`; executes over devices/interfaces (the keyring
  crypto API supplies encryption and signature; the file layout itself is not read here).
- `ImportKeyringOperation`: `FoundCertificates`, `AddedCertificates`, `DuplicateCertificates`,
  transaction root paths, cancellation, undo/redo; `ImportKeyringValidator`.
- `DeviceCertificate`: `FDSK`, `Password`, `Key`, `RawData`, `SerialNumber`, `Device`,
  `ReadId`/`CreateId`, `InternalSynchronize`, `Anonymize`, `UpdateEncryption`;
  `AddDeviceCertificateOperation.ValidKeyLength`, `ValidIoTCertificateRegex`,
  `ValidIoTDmcCertificateRegex`, `DoubleCertificate`, `ValidateIoT`.
- `Validators.DeviceCertificateAlreadyDownloadedValidator`,
  `ImportKeyringValidator`, `MaxSecureGroupKeyTableEntryValidators.*`,
  `SecureTableLimitValidator`.

**Falcon data security** (EV-FAL-SEC):

- `SecureKey` (`Key`, `IsToolKey`, `ResponseKey`); `ISecureKeyService`
  (`GetAvailableUnicastKeys`, `GetAvailableMulticastKeys`).
- `GroupCommunicationSecurity` / `DeviceCommunicationSecurity`:
  `Load`/`LoadToolKeys`, `LoadDeviceSequenceCounters`, `SaveDeviceSequenceCounters`,
  `ReadDeviceSequenceNumbers`, `ReadGroupAddressKeys`, `ReadToolKeys`, `ReadP2pKeys`,
  `UpdateInitialSequenceNumbersTo`.
- `SecureApplicationLayer`: `Encrypt`/`Decrypt` overloads (0x060030c8–0x060030cf),
  `SecureKeyService`, `SecureSendingSeqNrService`, `SecureReceivingSeqNrService`,
  `GroupCommunicationSecurity`, `AcceptHighSecureSeqNr`, `SyncUsingOne`,
  `CreatePlainUnicastSyncRequest` (0x060030d8), `CreatePlainBroadcastSyncRequest`,
  `EvaluatePlainSyncResponse`; statistics counters for decrypt success/outdated seq/same seq/
  failure/no key.
- `SecureTransformation.Encrypt/Decrypt`; `DefaultSecureReceivingSeqNrService` maps
  individual address ↔ serial number and stores last valid seq numbers;
  `SequenceNumberWithIdentifier`.
- `SecureServiceType`: `Data`, `SyncReq`, `SyncRes`; `SecureSyncRequestMessage` exposes
  `SourceSeqNr`, `TargetSerialNumber`, `Challenge`, `A`, `P`, `B0`, `Ctr0`; the response
  evaluation uses those.
- `SecureWrapperFrame`: `SecureSessionIdentifier`, `SequenceNumber`, `SerialNumber`,
  `MessageTag`, `IsAuthentic`, `DecryptUsingSessionKey` (0x06001325);
  `SecuredServiceFamily` (`ServiceFamily`, `SecurityVersion`).

**Secure download** (EV-HAWK-NET/HawkNET tasks):

- `LoadDeviceTask.ReprogramDeviceSecurityAsync`, `CheckAccessLevelAsync`, `LockDeviceAsync`,
  `UseDifferentialAsync`, `WaitUntilKnxRestartCompleteAsync` (type list / call edges).
- Provider: `UseSecurityToolKey` (0x060003c2), `WriteToolKeyAsync` (0x060003c3),
  `SecureSyncAsync` (0x060003c4, 0x060003d1), `ChangeAuthorizationAsync`,
  `SetAccessKeyAsync`, `TryChangeAuthorizationAsync`, `UseToolKeyAsync`,
  `WriteSecurityModeAsync`, `DisableIpSecurityAsync`, `WriteToolKeyAsync` on DeviceManagement.
- State: `DeviceProgrammingState.ToolKey`, `FDSK`, `DeviceAuthenticationKey`,
  `IpRoutingBackboneKey`, `LastValidSeqNr`.

### Wire/protocol behaviour (static)

- Secure wrapper around KNXnet/IP frames (`SECURE_WRAPPER`) with session id, sequence number,
  serial number, message tag and session-key decryption/authentication.
- Secure session handshake frames `SESSION_REQUEST`, `SESSION_RESPONSE`, `SESSION_AUTHENTICATE`,
  `SESSION_STATUS`; `SecureServiceType.SyncReq/SyncRes` inside secure application data.
- Classic KNX Data Secure at application layer: secure APDU with sequence number and
  authentication tag; `SecureApplicationLayer` chooses group or device keys via the key
  services and tracks sending/receiving counters.
- Routing security: separate backbone key on `IpRoutingConnectorParameters`, router features
  `SupportsSecureSBC`; `CommunicationPathRouterAction.SecurityUnicastWhiteList`,
  `SecurityBroadcastControl`.
- Certificates carry the FDSK and device identity; keyring export/import moves them plus
  group/interface keys.

### Data effects

- Keyring import adds certificates/keys to the project and reports found/added/duplicate
  counts; export produces a keyring file through the encrypted/signed keyring path
  (inferred from the keyring crypto API plus the export operation).
- Certificate operations link a certificate to a device, may reject duplicates and are
  undoable.
- Secure download writes tool keys and security mode (plus related authentication material)
  into the device and updates the device programming state.
- Sequence counters are persisted through `SaveDeviceSequenceCounters` per device/group and
  updated on send/receive; restoring a project without matching counters is a plausible
  replay/decryption failure, consistent with the seq-counter services and statistics names.

### Edge cases

- Certificate not found on a secure device: `LoadDeviceTask.DeviceCertificateNotFound` asks
  the user; the code path can call `DisableBackboneSecurityAsync` and set `FDSK`
  (EV-HAWK-NET call edges). This weakens security and must be explicit, logged and one-shot.
- “Unexpected key in device”: `UnexpectedKey` asks the user to accept or reject the key.
- High sequence numbers raise `HighSecuritySequenceNumberWarningException` with
  `WarningMessage_HighSecuritySequenceNumber`.
- AP2 mismatch handling (`LegacyAllowPartialDownloadIfAp2Mismatch`) applies to the same
  partial/full decision, including secure devices.
- `LastValidSeqNr` is part of programming state, so an out-of-date project copy can cause
  avoidable decryption failures.

### BusChord gap

BusChord has `Security/Keyring.cs` (JSON/XML, tool keys, sequence numbers),
`SecureTelegram`, `SecureWrapper`/`SecureSession` and AES helpers. Missing:

- keyring schema validation, PBKDF2 password hashing, signature creation/validation and
  encrypted password strings,
- certificate collection/FDSK storage and import/export counters,
- secure *download*: access level, tool key write, secure sync, security mode, FDSK repair,
- sequence-counter persistence and “outdated/same sequence” statistics,
- secure tunneling session auth and secure service-family negotiation,
- routing backbone key wiring and secure broadcast control,
- any audit/redaction policy for keys and session keys.

### Confidence

High for the API surface, keyring crypto call edges, secure message fields and state
properties. Medium for protocol-level details (which key type is selected per frame, exact
AES mode/tag length) — those live in obfuscated method bodies. Unknown: keyring XML schema
version, certificate regex specifics, KDF iteration count.

### Recommendation and safety boundary

Implement keyring **read/validate only** first: parse, schema-check, verify signature, list
certificates/keys without exposing material. Certificate import must be all-or-nothing with
counts and undo. Secure download comes last and must: verify certificate/key availability
before touching the device, never silently disable backbone security, keep FDSK/tool keys out
of logs and exports, persist sequence counters atomically, and require an explicit confirmation
for any “write key/security mode” step. Do not implement key export before a reviewed
encryption/signature path exists.

## 9. Failure, retry and audit semantics

### Exception taxonomy (EV-HAWK-NET / EV-HAWK-TASKS)

`HawkException` base; `DeviceManagementException` (`DeviceReturnCode`, `GetErrorCause`
maps legacy error codes), `NetworkManagementException`, `LoadStateMachineException`,
`TableOverflowException`, `UnknownMaskVersionException`, `IncompatibleTargetDeviceException`,
`InconsistentProductDataException`, `ResourceNotFoundException`, `CreateImageException`,
`HighSecuritySequenceNumberWarningException`.

`Falcon.CommunicationException.Cause`, `ConnectorException.Cause`,
`ApplicationLayerException.DeviceReturnCode`, `ResultCode`/`ResultCodeInfo` and
`ExceptionExtensions.GetDiagEvents` give the transport-level cause chain.

### User-decision callbacks

`ProgrammingModeAlert`, `ProvideAccessKey`, `SerialNumberAlreadyAssociated`,
`NewSerialNumberEncountered`, `AddressAlreadyOccupied`, `ProceedWithSerialNumber`,
`DeviceCertificateNotFound`, `UnexpectedKey`, `PromptBeforeFullDownload`,
`GetDeviceConnection`, `SkipAction` on `HawkProgressIndication`. These are the points where
ETS pauses and asks; they are part of the workflow contract, not error handling afterthoughts.

### Audit / trace

- `HawkTask.Warnings` + `AddWarning` (aggregated exceptions kept as warnings).
- `HawkProgressIndication`: percent, text, user-action-required, can-skip, exception,
  run-finished.
- `DownloadLogData` (time, image, procedure, mask, address, load options) with `ToXml`/`Parse`
  — a persisted download log per attempt.
- `DownloadEventTypes` metrics for download telemetry.
- Monitor journal with record start/stop/trigger, comments and interface identity.
- `SecureApplicationLayer` statistics counters (success/outdated/same sequence/decryption
  failed/no key) — the audit surface for secure communication problems.
- `LogSessionKeys` on the monitor connection manager is an explicit key-logging switch.

### UI resources

No dedicated failure/audit resource set exists; progress and warning text comes from
`Knx.Ets.Hawk.Properties.Resources` and the ViewModel resources (encrypted as user strings,
resource-backed as localized entries), rendered through `HawkProgressIndication`.

### Retry semantics — known and unknown

Known: reconnect policy, connect-fail-fast, connection-state probe, `FixConnection`,
restart-completion wait, address-uniqueness re-check, per-operation cancel and restart handling.
Unknown: retry counts, backoff, which failures are retried automatically vs surfaced. No
claim is made beyond the named hooks.

### Confidence

High for the exception and audit surface; medium for the actual retry policy (obfuscated
constants, no runtime trace).

### BusChord gap

BusChord has a small `BusErrorKind` (`Timeout`, `SendFailed`, `Malformed`, `NegativeAck`,
`Other`) and bus statistics, but no download log, no warnings aggregation, no
user-decision model, no per-attempt history, no secure seq-counter audit and no redaction
policy.

### Recommendation and safety boundary

Adopt an explicit outcome model: `Succeeded | Failed(retryable) | Failed(final) |
Cancelled | NeedsUser(action) | PartialLoaded`. Log every attempt with interface identity,
target device, computed image hash, options, duration and failure cause. Redact keys and
session keys by default. “NeedsUser” must be a visible, blocking state — never a silent
timeout or an automatic fallback (especially for security downgrade).

## 10. BusChord gaps — ranked

Status refers to the whole chain, not the presence of a class. Severity: critical = unsafe
or data-destroying behaviour; high = misleading or incomplete workflow; medium = missing
convenience/coverage.

| # | Gap | Severity | Current BusChord evidence | Next step |
|---|---|---|---|---|
| 1 | Download writes raw memory without programming mode, descriptor map or authorization | **critical** | `DownloadSession.cs:39–87` (fixed `StartAddress`, 3 s timeout, write+readback+restart) | Gate the writer; implement descriptor/mask check and programming-mode state before any further download work |
| 2 | Device load state is a demo/signature model, not device-derived state | **high** | `ProgrammingSession.DownloadedDeviceState` + `NeedsProgramming`; no device read-back | Extend to the five load flags, image hash, identity and security refs; keep the signature as drift signal |
| 3 | Download queue exists but only executes demo jobs | **high** | `ProgrammingSession` queue/lease/retry, `SessionOrigin.Demo` | Wire the queue to a safe real download pipeline; add NeedsUser, timeouts, attempt log |
| 4 | No secure commissioning (certificates/FDSK/tool key/seq counters/secure tunneling) | **high** | keyring + secure frame primitives only | Read-only keyring validation first; secure download last |
| 5 | No programming-mode scan or device diagnostics (check, LED, set prog mode) | **medium-high** | `Diagnostics.ScanAsync/ScanBusAsync/ScanLineAsync`, `CheckAddresses` | Add read-only prog-mode discovery; writes only as confirmed device service |
| 6 | Monitor lacks triggers, rotation/retention, decryption state and statistics report | **medium** | `TelegramRecorder` CSV/JSONL/XML + replay | Session/trigger model + retention + security columns |
| 7 | Project check lacks ETS check set (power, repeater, router, identifiers, app updates, battery broker) | **medium** | `ProjectCheck.cs`: address ranges, duplicates, segments, links, DPT | Pure-function check suite + report model |
| 8 | Interface setup lacks USB/RDAC, connection profiles and reconnect policy | **medium** | one host, UDP discovery, tunnel/routing | Typed connector model + profiles; USB/RDAC later |
| 9 | No download/audit log, warnings aggregation or key redaction | **medium** | `BusStatistics` only | Attempt log with redaction and export |

## 11. Top gaps and recommended order

1. **Make download safe before making it complete.** The current writer can put a device into
   an undefined state. First add a hard gate: require descriptor read, supported mask,
   programming mode and explicit confirmation; refuse otherwise. This is a correctness and
   safety fix, not a feature.
2. **Extend the existing signature-based state** to real device state (five flags, image hash,
   identity/security refs) and keep the signature as the drift detector. This unlocks every
   later workflow (download prompt, project check, diagnostics).
3. **Wire the existing queue to a safe download**: one operation per device, deadline, attempt
   log, NeedsUser states.
4. **Diagnostics read-only**: programming-mode scan and device check without writes.
5. **Monitor/recorder completeness**: triggers, retention, security/decryption columns,
   statistics.
6. **Project check** as report-only pure functions.
7. **Secure commissioning** in the order: keyring validation → certificate store → sequence
   counters → secure download → secure tunneling/routing. Never invert this order.

## 12. Explicit safety boundary

- This analysis performed **no** bus, router, USB, cloud or device action; no download,
  address write, programming-mode change, reset, key write or keyring/certificate access.
- No secret material was read: the report names key *types* and *properties* only. Key bytes,
  passwords, FDSK values, session keys and keyring contents are not included.
- Protocol statements are derived from shipped metadata (frame/field/enum/service-code
  names), not from captured traffic. BusChord's own live capture remains
  `analysis/router-discovery.md`.
- Any implementation following these recommendations must keep write paths behind explicit
  arm/confirm gates, run read-only against real installations first, use a simulator or
  isolated line for download tests, and never auto-download, auto-repair security or
  auto-disable backbone security.
- The Dotfuscator-encrypted literals and obfuscated method bodies mean numeric timeouts,
  retry counts, UI strings and exact decision tables remain unknown; they must be measured,
  not guessed, before being encoded as constants.

## 13. Open questions (bounded)

1. Numeric timeouts/retry/backoff in `DownloadManager`, `LoadDeviceTask`, `NetworkFacade`.
2. Exact `ConnectorType` values and which connector ETS selects per workflow.
3. ETS letter mapping (Adr/Prg/Par/Grp/Cfg) to `InternalLoadDeviceOptions` flags.
4. Differential-download decision table and stamp format.
5. Keyring XML schema/signature algorithm details and KDF iteration count.
6. Secure wrapper AES mode/tag length and session-key derivation.
7. Recorder XML/CSV schema and trigger serialization.
8. Project-check pass/fail rules and master-data power/repeater limits.
9. Whether ETS persists download queue/history across sessions (no persistence member found).
10. Exact UI ordering/labels (encrypted strings/BAML).
