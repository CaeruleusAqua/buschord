# buschord-mcp – MCP server for BusChord

Headless [Model Context Protocol](https://modelcontextprotocol.io) server that exposes the
`Knx.Core`, `Knx.Bus`, `Knx.Reports` and `Knx.ProjectCompare` functionality as 57 MCP tools.
It is a .NET 10 console app; JSON-RPC 2.0 runs over stdio,
one JSON message per line. stdout carries protocol messages only, all logging goes to stderr.

## Run

```bash
dotnet build
dotnet run --project src/Knx.Mcp --no-build              # stdio server (for MCP clients)
dotnet run --project src/Knx.Mcp -- --selftest           # end-to-end check, exit 0/1
dotnet run --project src/Knx.Mcp -- --list-tools         # 57 tool names only
dotnet run --project src/Knx.Mcp -- --scan-catalogs=<folder>   # parse .knxprod files, read-only
dotnet run --project src/Knx.Mcp -- --import-catalogs=<folder> # copy them into the local library
```

Use `--no-build` for clients: a `dotnet run` that builds writes MSBuild output to stdout and
corrupts the protocol stream. After one build the compiled entry point can also be used directly:
`src/Knx.Mcp/bin/Debug/net10.0/buschord-mcp.dll`.

A separate `--management-probe=<gateway-ip>` console mode provides explicit single-device
diagnostics and a bounded MDT M112 programming-mode cycle. This is outside the stdio
tool set; normal MCP write/download restrictions remain in place. See
[targeted commissioning](docs/commissioning.md) for required arguments and limits.
The MCP client's `command` must resolve to a .NET 10 host. For a side-by-side
installation outside `PATH`, configure the full path to that installation's
`dotnet` executable.

## Connect an agent

Register the server as a stdio MCP server, for example in an OpenCode/Claude-style config:

```json
{
  "mcpServers": {
    "buschord": {
      "command": "dotnet",
      "args": ["<repo>/src/Knx.Mcp/bin/Debug/net10.0/buschord-mcp.dll"]
    }
  }
}
```

The agent then calls `initialize`, reads `tools/list` and invokes tools with `tools/call`.
Server name `buschord`, version `1.0.0`, protocol version `2025-06-18`
(`2025-03-26` is accepted for older clients).

Bus tools support explicit authenticated `ip_secure_udp` profiles with a protected
keyring, exact host/user/tunnel selection and a password environment-variable name.
[Fields, ownership and limits](docs/secure-connections.md). Raw connection passwords
and keys are refused; Secure failures cannot fall back to plain discovery.

Typical session: `project_open` or `project_new` → inspect with `project_info` /
`project_dashboard` → edit with `group_address_*`, `group_link_*`, `device_*`, `smart_link_apply`,
`product_replace` → verify with `project_check` → `project_save`.

`project_open` and `project_save` accept an optional `password` for that call.
Protected projects require it again when saving; the server retains no password.
Protected operations use the built-in profile unless a local override exists in
`%APPDATA%\BusChord\archive-protection.json`. Options → Archive protection edits
its salt; `BUSCHORD_ARCHIVE_PROTECTION` can select another local profile file.
An invalid override refuses protected operations; an explicitly empty salt
disables them. Plain archives remain usable independently of this configuration.
The remembered save path changes after a successful write. Manufacturer
JavaScript calculations require explicit `execute_scripts: true` on each relevant
open, save or migration call and run through the bounded scripting worker.

For `product_replace`, first pass `dry_run: true` and inspect the returned exact
parameter/object migration rows. Missing definitions return `validated: false`
without a review token. Different declaration IDs require complete-id
`parameter_map`, `object_map` or `channel_map` objects; names and suffixes never
establish identity. Apply with the returned `review_token` and the identical
inputs, including the script opt-in. Tokens expire after ten minutes, are single
use, and each new preview replaces the previous one. A changed project requires
a fresh preview. Batch application documents and manufacturer parts have a
256 MiB input-copy budget. The whole migration records
one undo step and retains required manufacturer data for later export.

## State model

- One open project per server process. `project_open` replaces it and resets history.
- Mutating tools change only the in-memory project and push an undo snapshot (100 steps).
  `undo`/`redo` move through that history across tool calls.
- The `.knxproj` on disk changes only when `project_save` is called.
- Results come back as a text block (compact JSON) plus `structuredContent`; expected failures
  are `isError: true` results with a readable message, never JSON-RPC errors.
- A project may carry a durable revision log in its BusChord extension; no MCP tool creates a
  revision. Existing revisions are read by `report_export` and listed by `handover_package_export`.

## Tools

The server exposes 57 tools in the groups below.

### Manufacturer metadata and Jung SP8 import

`dca_manifest_info(path, application_program_id?)` reads a bounded ZIP DCA package
and reports its manifest identities, declared assembly/hash and application match.
It does not load the assembly or authenticate the publisher. Known externally
registered DCA requirements also protect ordinary parameter writes and migration.

`jung_ksp_info(path)` validates bounded KSP framing, ZIP/zlib/JSON and returns
metadata and exact block hashes. File validation alone does not validate an import.

After `project_open`, call
`jung_ksp_import_preview(device_id, path, master_data_path, language?)` and inspect
its parameter/object/link differences and `reviewToken`. The project snapshot must
contain the exact `M-0004_A-5018-15-17D9` definition; this initial profile requires
DCA API 1 / database 3.0.0. Master XML supplies the DPT sizes/subtypes; its version
and SHA-256 are reported. Preview changes no project or undo history.

`jung_ksp_import_apply(review_token, unlink_decisions?)` uses the frozen preview
bytes and rechecks the current project/application. Every `RequiredUnlink` needs
an explicit `true`, for example `{"12":true}`; optional unlink defaults to false.
The token expires after ten minutes and applies once, with one undo step. Saving
still uses `project_save`, including a fresh password for a protected source.
No manufacturer assembly, script or bus operation runs in this import.
Exact scope, byte limits and unverified cases:
[manufacturer adapters](docs/status.md#parameter-previews).

**Project** — `project_open(path, password?, execute_scripts?)`, `project_new(name, style, areas, lines, save_path?)`,
`project_save(path?, password?, execute_scripts?)`, `project_info`, `project_dashboard`

**Search** — `project_search(query, scope?, regex?, case_sensitive?, limit?)`,
`project_replace_preview(query, replacement, scope?, regex?, case_sensitive?)`,
`project_replace_apply(query, replacement, scope?, regex?, case_sensitive?)`.
`scope` is one of `all`, `devices`, `group_addresses`, `topology`, `communication_objects` or `buildings`.
Hits carry `kind`, `id`, display `path`, matched `field`, `value` and a `snippet`; `total` counts all matches
while `count` respects `limit`, and ordering is deterministic (kind, path, field, id).
Replacement only touches names, comments and communication-object text/function text — never addresses,
datapoint types or references. `project_replace_preview` reports every before/after field without changing
the project; `project_replace_apply` recomputes and validates that preview before the first mutation, pushes
one undo step, and returns `changedIds`. A request without matches changes nothing and adds no undo entry.
Building nodes are searched only when the optional building model provides `Project.Buildings`.

**Group addresses** — `group_addresses_list(filter?)`, `group_address_add(address, name, datapoint_type?)`,
`group_address_update(address, name?, datapoint_type?, description?, comment?)`,
`group_address_delete(address)`, `group_address_import_csv(content | path)`

**Devices** — `devices_list(line?)`, `device_update(address, name?, description?, comment?)`,
`device_add(line_area, line_address, name, product_ref?)`, `device_delete(address)`,
`device_move(address, target_area, target_line)`

**Manual links** — `communication_objects_list(device_address)`,
`group_link_add(device_address, object_id, group_address)`,
`group_link_remove(device_address, object_id, group_address)`.
Use the reference `id` from `communication_objects_list`, not its `comObjectId`.
List results include object text/number and links with GA id/address/name; dangling links retain their id.
Add/remove require an existing device, reference and GA; duplicate/absent links return `changed: false`
without adding undo history. Errors never mutate the project. These tools work offline and send no telegrams.
No DPT compatibility is inferred when reference metadata is unavailable.

**Analysis** — `project_check`, `smart_link_suggest`, `smart_link_apply`,
`mass_link_review(review_token?, device_address?, targets?)`,
`mass_link_apply(review_token, row_ids?)`,
`product_replace(old_ref, new_ref, new_program?, dry_run, review_token?, parameter_map?, object_map?, channel_map?, target_secure_capable?, execute_scripts?)`

`mass_link_review` returns all unlinked rows, candidate targets and explicit ready,
ambiguous, incompatible, unmatched or unverified states without changing the
project. Pass existing row/group IDs in `targets` to revise the review. The bound
token expires after ten minutes. `mass_link_apply` revalidates the source and
compatibility before applying selected ready rows as one undo step; stale or
incompatible requests leave the project unchanged. Unverified rows cannot be
applied through these tools.

**Export** — `export_csv(kind, path)`, `export_semantic(format, path)`,
`export_labels(kind, format, path)`, `report_export(path, title?, include_*)`,
`acceptance_protocol_export(path, format?, language?, title?, states_path?, technician?, building_owner?, defect_lines?)`,
`handover_package_export(path?, output, language?, title?, technician?, building_owner?, generated_at?, states_path?)`,
`project_compare(left_path, right_path)`

`report_export` writes one self-contained printable HTML project report (title page with counts,
topology and devices, group address table with datapoint types and links, communication objects,
link matrix summary, project check findings, and the revision table when the project has one).
Optional `include_title_page`, `include_topology`,
`include_group_addresses`, `include_com_objects`, `include_link_matrix` and `include_findings`
booleans default to `true`. There is no PDF output: a dependency-free PDF writer would have to
embed fonts, so the report is printed from the browser (page breaks, print header/footer and page
numbers come from the inline CSS).

`acceptance_protocol_export` writes the KNX acceptance/test protocol (P13): HTML is the full
document (project facts with document version, SHA-256 document hash and master-data signature
status, topology and device list, group addresses with datapoint types, function matrix, per-device
test rows, open items with a blank defect list and a de/en sign-off block); `format=csv` writes the
per-device test sheet alone (same rows, spreadsheet-ready, UTF-8 with BOM). It is read-only: no bus
access, no project change, no download. Visual/function/bus/parameter checks always start as
`not tested`, measured value and remark stay blank fill-in columns, and the download column reports
only recorded device programming states (`states_path`, a JSON file in the `DeviceProgrammingStates`
format produced by `ProgrammingSession.ExportDownloadedJson`; the Workbench has no menu export for
it yet): without a state every device reads `no download recorded`, demo states read
`simulation (demo)`, and only a successful state with origin `real` reads as `loaded for real` or
`partially loaded (real)` depending on its load flags. The result
carries `documentHash`, `devices`, `openItems` and `commissioning` (`real`, `simulation` or
`not_recorded`; failed attempts never count as a load). The export uses the current time, so its
document hash changes between runs; a caller that passes a fixed generation timestamp through
the Core API gets a byte-identical document.

`handover_package_export` writes one deterministic handover ZIP for the open project or for an
optional `path` (`.knxproj`, read only). It bundles the printable report, group address, device
and topology CSV, the function matrix (`05_zuordnungen.csv`) and link summary, the room book from
the native Locations tree (including devices without a room), group address and device labels,
the semantic Turtle export, the acceptance protocol (`10_pruefprotokoll.html`), per-device
configuration signatures and recorded load states, template pages for operating instructions,
maintenance contacts and data protection, the revision list, a README and `00_MANIFEST.sha256`
with a SHA-256 entry for every other file. `language` (`de`/`en`) and `generated_at` (ISO 8601) make
regeneration byte-identical; without `generated_at` the current time is used. `states_path`
follows the protocol rules and a state file from another project is rejected. The tool is
read-only: it loads `path` without replacing the open project, never writes to the `.knxproj`,
adds no undo history and only writes the ZIP (`output`; it must not be the project file). The
package contains no keyring, catalogue or manufacturer files and no absolute source paths;
missing revisions, locations or states and demo loads are named explicitly.

`project_compare(left_path, right_path)` reads both files without changing the open project and
returns an ordinal-ordered comparison of project facts, installations, devices, parameters, group
addresses, links (including send/ack/priority/slot facts), workbench metadata and the building
tree. `added`, `removed`, `changed` and `unchanged` are counts; the first 500 items are returned
with `truncated: true` when more exist. Signature changes are labelled `preserved, not
recomputed or verified`; the tool never verifies a signature.

**Editing** – `undo`, `redo`

**Catalog** – `catalog_library`, `catalog_import(path)`, `catalog_products(catalog?, filter?)`,
`catalog_import_device(product_id, line_area, line_address, catalog?, name?, address?)`.
Catalogues are read from the local library (`%APPDATA%\BusChord\catalogs`, override with the
`KNX_CATALOG_DIR` environment variable); `.knxprod` files are imported with `catalog_import`.
The metadata index lives in `%LOCALAPPDATA%\BusChord\cache\catalog-index`, is revalidated by
SHA-256 and rebuilt when a catalogue is new or changed; warm starts and searches read the index
and only load an application model when a tool needs it.

**Bus** – `bus_discover(timeout_ms?, host?)`, `bus_connect(mode?, device?, own_address?, interface?, timeout_ms?)`,
`bus_disconnect`, `bus_read(group_address)`, `bus_write(group_address, value, datapoint_type?)`,
`bus_status`, `recorder_start`, `recorder_stop`, `recorder_save(path, format)`

`bus_write` is listed for protocol compatibility but always returns `isError`: this build is
read-only and refuses group value writes by policy. `bus_read` is the only group-value tool; its
wire behaviour is not verified against real hardware (see "Bus behaviour" below).

**Bus diagnostics (read-only)** – `bus_scan(target?, gateway?, timeout_ms?, response_timeout_ms?)`,
`bus_device_info(address, gateway?, timeout_ms?, response_timeout_ms?)`,
`bus_statistics(window_ms?)`.
`bus_scan` broadcasts `IndividualAddressRead`, collects the answering addresses, reads each device
descriptor (`DeviceDescriptorRead`, mask version) and compares the result with the open project's
devices: duplicate, missing and unexpected addresses. `target` restricts the scan to one line
(`1.1`). `bus_device_info` reads the descriptor of one address. Both tools open their own
short-lived tunnelling connection (own address 1.1.249) because `KnxBus` exposes no
management-frame send; they send no write/programming frames and never change a device.
`bus_statistics` counts the telegrams of the current bus session over a trailing window:
telegrams, acks, errors (by kind), rate per second/minute and telegrams per source. It works
offline and returns zeros until a connection delivers telegrams.

`download_plan(device_address, gateway?, timeout_ms?, response_timeout_ms?, parameter_subset?, byte_order?, resource_preview?, property_preview?, image_composition_preview?, root_image_build_preview?, root_resolved_image_build_preview?, root_property_image_build_preview?, native_image_build_preview?, load_procedure_preview?)` is the read-only
preflight and dry run for a future device download: it reports the gate checks (descriptor/mask,
programming mode, confirmed address/line, program match, parameter/link completeness,
confirmations), the computed image chunks, the printable dry run and the attempt-log status. It
sends no programming frame. An ordinary preflight with `gateway` reads the descriptor
like `bus_device_info`. Any explicit archive subset, procedure or image preview stays
offline even with `gateway`; no descriptor discovery or connection is attempted.
The tool cannot query programming mode or obtain on-site confirmations, so a plan always reports
those gates as open and is never ready. `writesEnabled` stays `false`, `realDownloadAvailable` is
`false`, and a real download remains disabled.

The additive `archiveMaskProfile` field reports the exact archived mask reference,
current (legacy version 0) selection, FirstAppObject index, declared interface/PDT
counts and original master-file hash when available. Missing or ambiguous metadata
stays diagnostic. This field evaluates no parameter scripts and supplies no image,
property capacity or authorization; see the [profile scope and evidence](docs/status.md#parameter-previews).

`image_composition_preview: { "mask_policy": "source_defined_bits" }` explicitly
requests a separate static root Code working set. The only other policy is
`no_static_source_bits`. The closed object requires exactly this one string;
null, boolean, unknown, duplicate or extra fields are tool errors before gateway
access. It needs neither a parameter subset nor byte order and does not infer
policy from a procedure's Full flag. Omitting it preserves ordinary output.

The additive `offlineImageComposition` reports raw source Data/nullable Mask
hashes separately from composed working Data/Mask, logical arena coordinates,
unknown spans, bit totals, source binding and unresolved writer/finalization
requirements. Missing source bytes remain unspecified, even when working storage
is allocated. Compatible defined bits merge; conflicts return no partial chunks
or binding. No full buffers, current device bytes or concrete read actions are
exposed. Lists report totals/shown/truncated: 128 sources/chunks/source identities,
256 unknown spans/requirements. `completeApplicationImage`, `executionEligible`
and `downloadEligible` remain false. See the
[exact composition profile](docs/status.md#offline-image-inputs-and-load-procedures).

`root_image_build_preview: { "mask_policy": "source_defined_bits" }` explicitly
combines static code, every admitted primary fixed-root parameter reference and
optional communication tables in one captured evaluation. Its other policy is
`no_static_source_bits`. The closed object requires one policy string and an
explicit ordinary `load_procedure_preview` selecting Full or Parameters. It
excludes `parameter_subset`, caller `byte_order`, `image_composition_preview` and
`resolve_conditions:true`; malformed/duplicate options fail before gateway access.
Optional `generated_tables` inside the procedure retains its Full/Groups gate.

`offlineRootImageBuild` reports scope `RootFixedMemoryWorkingSet`, profile 1,
source-derived byte order/inactive policy, complete admitted reference counts,
record dispositions, working/validity/canonical hashes, geometry and unresolved
requirements. It uses one shared result in place of the separate
`offlineApplicationBuild` field. Source mask-zero bytes are cleared before parameter
writing; partial-mask bytes retain their raw working bits. A validity reset does
not erase working bytes. Tables require identical retained encoder inputs or a
zero fresh span; input mismatches and overlapping data writers return no partial
population, claims, chunks or binding.

Presentation caps are 128 sources/records/claims/chunks/source identities,
256 unknown spans/requirements and 512 controls, with totals/shown/truncated.
No raw values, encoded words or complete buffers are serialized. The separate
binding includes raw current inputs, population, policy, geometry, requirements,
working data, validity and table compatibility. `completeFixedRootPopulation`
applies only inside this bounded profile; `completeApplicationImage`,
`executionEligible` and `downloadEligible` stay false. Omitting the option preserves
ordinary output. See the [source and codec limits](docs/status.md#offline-image-inputs-and-load-procedures).

`root_resolved_image_build_preview` selects a separate shared resolved-root route:

```json
{
  "mask_policy": "source_defined_bits",
  "procedure": {
    "subtype": "all", "access": "remote",
    "full": true, "parameters": false, "groups": false
  },
  "generated_tables": { "format": "m112" }
}
```

The closed object requires an explicit policy and all five procedure fields;
`generated_tables` is optional. Policies, subtype names and table formats retain
their existing spellings. Full or Parameters is required; tables also require Full
or Groups. Resolution is inherent to this route: no `resolve_conditions` flag,
caller values, byte order, subset, context or fragments are accepted. It excludes
the other four preview options; malformed or duplicate fields fail before gateway
access. The older procedure-only option retains its table exclusion.

`offlineRootResolvedImageBuild` reports profile 2 and scope
`RootFixedMemoryResolvedWorkingSet`. One application evaluation supplies complete
admitted fixed-root writers, strict controller provenance, the resolved procedure
and optional tables. Its own binding covers the prepared graph, shared controller
proof, actual procedure, working bytes and table emission/compatibility. Tables
expose emission facts through the owning root route. A primitive procedure may
retain profile 1 while its root plan stays profile 2. Caps match the ordinary root
route, with 512 controller facts and branch decisions, complete totals and hashes.
Raw values and full buffers are omitted. All image/execution/download gates remain
false; omitted options preserve ordinary output and existing bindings.

`root_property_image_build_preview` selects the separate fixed-root property stage:

```json
{
  "procedure": {
    "subtype": "all", "access": "remote",
    "full": true, "parameters": false, "groups": false
  }
}
```

Both objects are closed. All five procedure fields are required; Full or
Parameters must be true. Bounded condition resolution is inherent. Caller values,
byte order, mask policy, generated tables, context and runtime options are not
accepted. The option excludes other procedure/image previews, parameter subsets
and byte-order options. Legacy `resource_preview`/`property_preview` flags may be
false; true conflicts, and duplicate or nonboolean flags fail. Malformed new
options and these conflicts are checked before project or gateway access.
Omitting the option preserves ordinary output and existing preview contracts.

`offlineRootPropertyBuild` reports scope `RootFixedPropertyWorkingSet`, profile 1,
source-owned allocations, full population totals, shared procedure proof and its
own replay binding. Initial Data/validity, working Data, desired validity and
canonical desired bytes have separate complete hashes. Inline actions are only
hashed action operands. `observedCounts` reports archived Count hashes, status
and `ObservedNotAdopted`; these are neither current-device counts nor capacity.

Presentation caps are 128 records/claims/properties/observed counts, 256 unknown
spans/requirements, and 512 controllers, controls, branch decisions and allocation
control ordinals. Each capped collection reports total, shown and truncated;
hashes cover the complete result. Raw parameter/controller values, encoded words
and full buffers are omitted. A primitive procedure keeps its own profile 1;
conditional procedures use profile 2 without changing the property profile.
Source, model, revision and undo remain unchanged. The supported synthetic first
profile and refusal/budget boundaries are in the [status document](docs/status.md#parameter-previews).
All complete-image/execution/download gates remain false.

`native_image_build_preview` selects the combined logical working pipeline:

```json
{
  "device_address": "1.1.1",
  "native_image_build_preview": { "option_words": [1, 129] }
}
```

The closed object requires 1–16 distinct uint32 option words. These are explicit
assumptions, never observed device options. Masks `0x1` and `0x2` schedule
parameters and tables; `0x80` retains initial masks and `0x400` excludes adjustable
customer parameters. Other bits remain in selection and binding. An unresolved
consumed IP condition yields both conditional alternatives, up to 32 rows.
There is no caller-provided context, geometry, values or IP fact.

Optional `"tables":{"format":"m112"}` applies only to table-scheduled
alternatives; for example use `"option_words":[131]`. Formats `system_b_2` and
`system_b_4` require the corresponding supported relative backing. Tables read
the current working seed after parameters. Retained headers therefore preserve
parameter edits; `beforeWorkingDataHash` does not assert archived or device data.
Malformed/duplicate fields, invalid words and conflicting preview/subset/byte-order
options fail before project or gateway access. Backend flags may only be false
booleans. This option replaces the separate legacy mask diagnostic with its own
captured `maskVersionId`; omission preserves the existing response.

`offlineNativeImageBuild` reports scope `NativeLogicalWorkingSet`, profile 1,
all alternatives including unavailable ones, separate source/initial/working/
validity/canonical hashes, observed Count data and its own complete replay binding.
Each detail role shares a 128-item budget across all variants; requirements share
256 items. Complete totals, truncation flags and full role hashes remain present.
All 32 possible rows stay visible. Parameter words, raw values and full buffers
are omitted. Core exposes immutable full buffers through
`OfflineNativeImageBuild.Preview`. The `policy` view reports source byte order,
inactive-value behavior, codec profile names, physical field counts and separate
source-policy/codec fingerprints. It includes latent fields and remains present
when parameter tasks are skipped. This synthetic direct-root profile supports
Number/Value-enum, aligned IEEE-Single32, packed Time24 in big endian and signed
ordinary-Time8–32 fields. Ordinary Time retains raw unit counts; little endian
requires whole-byte fields at bit zero. Single/Time NoStorage and procedure
controllers remain unsupported. The profile
retains explicit refusals and unresolved image requirements; see the
[exact combined scope](docs/status.md#combined-logical-working-stage).

Its bounded global-Memory module profile adds `moduleOrigins`, `moduleInstances`,
`moduleAllocations` and `moduleReservations` to each population, plus complete role
hashes, totals, shown counts and truncation flags. Each role shares its own 128-item
budget across all alternatives. Origins retain original declarations and concrete
fields, including latent owners; allocations expose alignment and inclusive pool
bounds. Instance arguments expose only count and hash. These are synthetic logical
image facts; actual options, complete-image, execution and download flags remain false.

The bounded conditional direct-module case additionally reports
`potentialModuleInstancesTotal`, its complete hash and `moduleNotInstantiatedCount`.
Uninstantiated references have no native record or writes; retained saved values
remain part of the source binding. See the same profile limits in the status document.

The same native logical pipeline also accepts direct byte-aligned root Memory
RawData: strict Base64, declared `MaxSize + 4` capacity, a program-byte-order
32-bit length prefix and opaque payload. The writer touches only the actual frame;
the tail retains the source seed/validity policy. RawData in properties, unions,
modules or NoStorage remains refused. `policy.rawDataFieldCount` and
`rawDataEncodingProfile` describe this extension; codec profile 4 applies when a
RawData owner is present. Existing profiles retain their previous fingerprints.

For an explicit numeric byte preview, supply both `parameter_subset` (1–64 unique
`{ "refId": "...", "rawValue": "10" }` items) and `byte_order` (`big_endian` or
`little_endian`). The additive `offlineParameterPatch` contains small original,
resulting and field-mask hex ranges; original absent masks stay null. Omitting
both options returns null without calculating a patch. Invalid option pairs are
tool errors; unsupported semantics return issues without partial ranges. This
preview cannot enable downloads. [Exact supported profile and limits](docs/status.md#parameter-previews).
Declared unsigned `TypeRestriction Base="Value"` choices use the same arguments;
`rawValue` must be an exact declared decimal value, independently of labels or
list order. BinaryValue forms are excluded. [Enumeration profile](docs/status.md#parameter-previews).
Aligned native IEEE-Single fields use .NET 10 XML Double-to-Single conversion;
the binding reports `codecProfileVersion: 3` without ordinary Time selection,
plus `numericParseProfile`. Integer-only
subsets report `ExactInteger`; subsets containing Single report
`ExactIntegerAndDotNet10XmlDoubleToSingle`. Nonfinite/overflow inputs, display
transforms, IEEE Double and DPT9 are refused. [Float scope and compatibility evidence](docs/status.md#parameter-previews).
The narrow Time profile accepts `PackedDaysHoursMinutesAndSeconds`, exactly 24 bits,
BitOffset zero and BigEndian only. Raw seconds 90061 produce fixed bytes `010121`,
not numeric `015FCD`. Selected Time adds the fingerprint-bound
`packedTimeEncodingProfile: "PackedDaysHoursMinutesAndSeconds24BigEndianOnly"`;
it is null otherwise. This packed profile refuses LittleEndian, other packed
layouts and wrapping values.
[Packed-time bounds and exact public result](docs/status.md#parameter-previews).
Selected direct module Number fields additionally require internally preserved
original parameter/ref identity, literal arguments and checked current branches.
The binding reports `moduleScopeProfileVersion: 1` and `moduleScopeWitnessHash`;
both are null for root-only subsets. Without ordinary Time selection, codec
version remains 3. There is no new
request option; IDs and caller metadata cannot establish source identity. Repeat,
allocator and nested selected scopes are refused. [Module scope and evidence](docs/status.md#parameter-previews).
The [module Value-enum extension](docs/status.md#parameter-previews)
uses the existing 1–32-bit declared-choice codec after bounded raw source preflight.
Selected module enums without module DenseTime use `moduleScopeProfileVersion: 2`. Module Number-only
subsets, including a root enum, retain profile 1 and their prior witness; root-only
subsets retain null module fields. Without DenseTime, codec profile 3 is unchanged;
request arguments stay the same for every profile.
The [ordinary Time profile](docs/status.md#parameter-previews) admits signed
8–32-bit raw counts in Hours, Minutes, Seconds, HundredMilliseconds, TenMilliseconds
and Milliseconds, with the complete declared range fitting that signed width.
There is no duration scaling. Only actual DenseTime selection uses codec profile 4
and `denseTimeEncodingProfile: "SignedRawOrdinaryTime8To32"`; the new property is
null/omitted for legacy subsets, whose codec 3 and fingerprints remain unchanged.
Only a selected module DenseTime raises module scope to 3. A root DenseTime mixed
with module Number/enum keeps module scope 1/2. PackedTime24 keeps its fixed bytes
and metadata; other packed units and module PackedTime remain excluded.

`resource_preview: true` requires both `parameter_subset` and `byte_order` and
selects a separate `offlineParameterPatch` resource result with `backendVersion: 1`
and scope `ExplicitResourceSubsetPreview`. It reuses the same bounded codecs for
absolute memory or root literal relative memory. Each field and range identifies
its `arena` (`absolute_memory` or `relative_memory`, with `loadStateMachine` when
relative), `byteOffsetInArena` and `byteOffsetInResource`. Arena offsets include
the declared resource base; an LSM offset is never a physical address. Mixed
absolute/relative selections and module-relative or property writes are refused.

Core owns the complete supplied before/after resource buffers. MCP returns their
hashes, payload length, declared size and coverage, plus compact changed ranges;
it does not serialize complete buffers. Missing data are not padded from a size
declaration. The version-1 binding includes typed resource and field hashes and
any admitted module witness. `unresolvedRequirements` names remaining activation,
table, procedure, mapping, property and Secure work. `completeApplicationImage`
and `downloadEligible` stay false. Omitting `resource_preview` or supplying false
keeps the existing result and fingerprints; false alone calculates no patch.

Resource mode also includes an independently bound `imageSources` inventory.
It pairs exact application/archive and hardware identities, lists separate native
resource/image locations and reports optional decoded factory segments through
hashes and lengths. Pointer resolution, property capacity/current counts and
checksum verification remain unavailable. Missing factory data stay missing.
The source inventory has its own profile/binding and does not change the existing
subset binding. [Source profiles, budgets and limits](docs/status.md#offline-image-inputs-and-load-procedures).

`property_preview: true` requires `parameter_subset` and `byte_order`, and cannot
be combined with `resource_preview: true`. It selects a separate property result
in `offlineParameterPatch`. The initial profile accepts root Number and declared
unsigned Value-enum fields with literal property coordinates. It requires one
complete archived Data buffer at byte offset zero, a separate two-byte count,
explicit all-FF validity masks for both, and a uniquely declared PDT with equal,
known nonzero read/write element widths. The encoded count times that width must
equal the supplied Data length. Count bytes always retain their original BigEndian
encoding, independently of the requested parameter byte order.

Fields and small changed ranges report `byteOffsetInPropertyData`, MSB bit offsets
and typed selectors. Property images report complete-buffer hashes and lengths;
their separate count bytes and mask are preserved unchanged. Supplied source count
does not establish current device state or maximum property capacity. Capacity
remains unresolved. Missing data, masks, count or PDT never become default bytes.
The property source index has separate source and selected PDT-role fingerprints;
its MCP projection is bounded and reports omitted-entry counts.

Module/Repeat addressing, Union, calculations, aliases, external plug-ins, RawData,
count updates and asymmetric PDT layouts remain outside this profile. Any parameter
Memory declaration, including an unselected one, is refused until physical aliases
can be resolved. Failure returns no binding, fields, ranges or property images.
The separate binding rechecks saved values, source buffers, declarations, identity
and revision. `completeApplicationImage`, `downloadEligible` and
`executionEligible` stay false. Omitting `property_preview` or supplying false
preserves the complete existing response; false alone calculates no patch.

For an offline primary-program procedure graph, supply `load_procedure_preview`
with all five fields explicitly. It can be used without a parameter subset:

```json
{
  "device_address": "1.1.1",
  "load_procedure_preview": {
    "subtype": "all",
    "access": "remote",
    "full": true,
    "parameters": false,
    "groups": false
  }
}
```

`subtype` is one of `all`, `par`, `grp`, `par,grp`, `ap1` or `cfg`; `access` is
exactly `remote`. Full is a declared procedure filter, not a complete-image claim.
Unknown/duplicate fields, missing flags and nonbooleans are tool errors. Omitting
the whole option leaves the previous output unchanged.

The additive `offlineApplicationBuild` returns ordered typed controls, effective
conditions, merge decisions, source-resource hashes, three table responsibilities
and unresolved requirements. Inline Data/Mask are represented by length/hash only.
Its independent version-1 binding covers exact sources, the full procedure witness
and declared project values/object/link/connector/group/binary inputs. This binding
detects stale input and cannot authorize execution. AP2, module procedures, complete
activation, fixups and finalization remain unresolved. The preflight's
Program/chunks/readiness and all write gates remain unchanged. Presentation caps
retain complete counts, explicit truncation and full input witnesses; see
[the exact scope](docs/status.md#offline-image-inputs-and-load-procedures).

For a separate procedure-only preview, add `"resolve_conditions":true` inside
`load_procedure_preview`. This selects bounded root Number/Value-enum conditions
without scripts and retains relative-memory comparison operands as length/hash.
The returned procedure has profile version 2, original element provenance and
`conditionalSelection` decisions with reference activity, an active value (omitted when inactive),
selected branch ordinals and fallback use. Inactive references skip all branches,
including fallback. This is distinct from alias activity and GUI visibility.
Its binding additionally includes `procedureGraphHash` and `evaluationContextHash`.
Controls and decisions are capped at 512 for presentation, with complete counts,
truncation flags and complete witnesses. A primitive procedure keeps its original
version-1 shape. Omitted/false `resolve_conditions` preserves the original route
and its complete JSON. A true value cannot be combined with `generated_tables`;
invalid combinations and duplicate/nonboolean options fail before a gateway read.
Module/Repeat activity, calculation declarations, relevant assignment dependencies,
unaccounted external state and nested product merges remain unsupported. The
original table route and all readiness, image, execution and write gates retain
their existing contracts.

Inside that explicit option, `"generated_tables":{"format":"m112"}` additionally
requests communication-table fragments. The other formats are `system_b_2` and
`system_b_4`; their association-entry width is a requested offline format, never a
discovered device capability. `full` or `groups` must be true. Selecting tables does
not enable scripts. Unknown, duplicate or malformed table options are tool errors
before any optional gateway read.

`offlineApplicationBuild.generatedTables` has its own binding over independently
checked bounded native object activity, module origins, saved native flags, current links, sender/acknowledge
facts, explicit format and produced bytes. Its payloads expose lengths and hashes;
Core retains immutable Data/WriteMask spans. A refused request returns no table
payloads or overall build binding. The original build binding still covers only
its source/procedure/declared-input stage; table freshness requires the separate
generated-table binding. Omitting `generated_tables` preserves the previous output.
Procedure size adjustments are reported separately, with a 512-item presentation
cap; up to four table payload summaries retain complete counts and truncation flags.
See [supported table profiles and remaining requirements](docs/status.md#offline-image-inputs-and-load-procedures).

Mask-derived metadata requires an application namespace explicitly supported for
that interpretation (/20, /21 or /23). Opening a /22 project does not implicitly
enable /22 application-byte or mask-profile interpretation.

The diagnostic logic itself is in `src/Knx.Bus/Diagnostics.cs` and takes an
`IBusDiagnosticSession`, so it is usable and tested without hardware
(`tests/Knx.Diagnostics.Tests`). The wire behaviour of scan and descriptor frames is **unverified
live**: the frame layout follows the KNX spec but has not been validated against a real device.

## Bus behaviour

`bus_discover(host="192.0.2.10")` uses a targeted SEARCH/DESCRIPTION lookup.
Without `host`, discovery sends requests on each usable local IPv4 interface.
The standalone read-only diagnostic is `dotnet run --project src/Knx.Mcp -- --bus-probe=192.0.2.10`.
See [network and commissioning limits](docs/status.md#network-and-commissioning) for the live validation
and remaining tunnel wire-format limitations; successful discovery is not a
commissioning validation.

`bus_connect` opens the interface: `mode=tunnel` (default) searches the gateway and connects the first
match for `device` (name fragment or individual address), `mode=routing` joins the multicast group
without a device. `bus_disconnect` releases the socket; both are idempotent.
`bus_read` reports `isError` with "not connected" instead of blocking when no connection exists;
`bus_write` is refused even when connected, and `bus_connect` reports `isError` when no gateway
answers the search.
