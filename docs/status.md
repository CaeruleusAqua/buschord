# Capabilities, validation and limits

Status: 2026-10-29. BusChord is a pre-release engineering workbench. A supported
partial profile is not a general compatibility or commissioning release.

## Project and archive support

The shared model covers topology, buildings/rooms, group addresses, objects,
native channel identities and links. Reviewed batch operations validate their
inputs before mutation and use atomic undo. Product migration requires explicit
compatible mappings and link decisions; ambiguous mappings are refused.

Archive support covers bounded /20, /21, /22 and /23 layouts, root/nested ZIPs, atomic
saves, opaque-data retention and existing AES encryption. Protected operations
require the project password and use the built-in profile or a matching local
override. The global salt can be configured in Options and persisted outside the
checkout; the same bounded profile serves the UI and MCP. An absent override
uses the built-in default; invalid or explicitly disabled overrides refuse
protected operations without affecting plain archives. The salt editor
masks its value and does not expose value/text accessibility providers.
Signatures are retained, not regenerated; changed signed
data may fail validation in another tool. Broader round-trip coverage remains open.

Native /20 supports root data, one project ZIP or an exact project-ID directory.
Its devices remain directly under lines, with line medium/domain, native numeric
addresses, channels and parameter values. Metadata, sidecar state, opaque entries
and the source layout survive edit/undo/save/reopen. Ambiguous layouts or identities
are refused. Segment operations are unavailable for /20; an unsupported segment
model cannot replace a destination file. This does not upgrade projects to /21.

Native /22 uses the checked segment topology shared with /21. All three source
layouts preserve assigned device addresses, native parameter/channel/object and
module state, metadata and opaque additions such as IPv6 installation IDs,
slow-resender flags and secondary address records without an assigned address.
Ambiguous identities are refused before mutation. Project-format support does
not widen application-namespace support for mask interpretation or byte previews.

## Catalogue acquisition

The catalogue offers explicit manufacturer-directory refresh and source-specific
download/import with local consent, provenance and batch reports. Directory entries
and registered codes remain distinct identities, including historical records;
no copied complete registry or vendor archives are shipped. Public source access
does not establish usage rights. Manufacturer grants, personal authorization
declarations, explicit restrictions and manual/login routes remain separate.
Restricted sources cannot be enabled by a generic acceptance checkbox.

Downloads use reviewed HTTPS hosts, bounded streaming and staging, cancellation,
ZIP/XML checks and manufacturer validation aligned with the import parser.
Byte-identical files skip; changed files sharing an installed catalogue identity
preserve the installed copy. Completed imports survive cancellation and report
storage failures. The full catalogue suite includes 22 acquisition scenarios;
native dialog checks cover consent, import/reload, cancellation and report failures.
See [catalogue acquisition](catalog-downloads.md) for the workflow and limits.

## Manufacturer and parameter profiles

MDT, Jung and Gira are required validation profiles. Typed drafts, dynamic labels
and visibility, native module records, bounded function calculations and conditional
module/Repeat structures are implemented. Allocator and BaseValue support is bounded;
unproven structures remain explicitly unsupported.

JavaScript requires the explicit separate `Knx.Scripting` worker capability.
Runtime limits are not an OS-container guarantee. Unsupported scripts and external
DCA/plug-in applications remain blocked. Manifest inspection loads no assembly.

Jung SP8 KSP import is limited to application `M-0004_A-5018-15-17D9`, API 1 and
database 3.0.0. Preview validates immutable input; Apply requires reviewed link
decisions and uses one undo. General DCA hosting, VBScript, complete load flows and
broader manufacturer activation remain open.

## Parameter previews

`download_plan` optionally previews 1–64 explicitly selected raw values with an
explicit numeric byte order. It preserves known initial Data, nullable Mask and
all unrelated bits. Sources, project state and undo remain unchanged. Unsupported
or ambiguous requests return no partial fields, ranges or resources.

| Codec | Bounded scope |
| --- | --- |
| Number | Signed/unsigned native 1–32-bit integers. |
| Value enum | Native unsigned Base=Value, 1–32 bits, 1–4096 unique canonical choices; exact membership. |
| IEEE-Single | Aligned finite 32-bit values, explicit .NET 10 XML Double-to-Single conversion, negative zero retained. |
| Ordinary Time | Signed 8–32-bit raw counts in Hours, Minutes, Seconds, HundredMilliseconds, TenMilliseconds and Milliseconds; complete declared interval must fit the signed width. No duration scaling. |
| Packed day-time | Exactly 24 bits/bit0, raw 0–691199 seconds, fixed second/minute/day-hour bytes; currently BigEndian request only. |

LittleEndian numeric fields require aligned whole-byte widths. Double, DPT9,
other packed Time layouts, wide fields and general transforms remain outside the
preview. Direct literal module Number, enum and ordinary Time have checked source
and branch bindings. Selected Repeat/nested/allocator/Union scopes, local resources
and module IEEE-Single/packed Time remain outside this preview, independently of editor support.

Access, calculation/structural dependencies, aliases, all-row bit ownership,
unknown spans and overlapping resources are conservative gates. Visibility alone
does not establish activation. Budgets are 16 resources, 1 MiB per payload,
4 MiB total and 320 touched output bytes. Missing data are not invented.

Source bindings detect stale project/device identity, revision, values and source
definitions. They are read-only facts, not commit or programming tokens.

| Selected subset | Codec profile | Module scope |
| --- | ---: | --- |
| No ordinary Time | 3 | null, 1 for module Number, 2 for module enum |
| Root ordinary Time | 4 | null |
| Root ordinary Time plus module Number/enum | 4 | 1/2 unchanged |
| Selected module ordinary Time | 4 | 3 |

`DenseTimeEncodingProfile` is `SignedRawOrdinaryTime8To32` only for selected
ordinary Time. Existing requests preserve their complete fingerprints. Packed
metadata remains separate. No preview is a complete application image or permits
a real download.

Set `resource_preview: true` with the explicit subset and byte order to request
the separate version-1 resource backend. It reuses the codecs above and returns
owned complete before/after buffers for each selected source resource through
Core; MCP exposes hashes and small changed ranges. Absolute memory and root
literal RelativeMemory are distinct logical arenas, including their LSM number.
Offsets include the resource base within that arena; relative offsets are not
physical addresses. Different LSMs may share offsets, but overlapping resources
in the same arena are refused. Mixed absolute/relative selections, module-relative
storage and property writes remain unavailable.

The backend retains all supplied Data and nullable Mask, distinguishes payload
length from declared size, and never pads missing bytes. Its independent binding
covers typed geometry, source buffers, selected fields and any admitted module
witness. Unresolved requirements include activation, generated tables, load
procedures, physical mapping, property initialization and Secure configuration.
`completeApplicationImage` and `downloadEligible` remain false. Omitting the flag
or supplying false retains the existing preview contract and fingerprints.

The separate `property_preview` option accepts bounded root Number/Value-enum
fields in one complete supplied property Data buffer, with an independently
supplied two-byte count and explicit all-FF masks. It requires a unique selected
PDT with equal known read/write widths and checks Data length against the encoded
source count. Property byte offsets and MSB bit offsets remain independent of
element width. Count bytes remain unchanged, including with LittleEndian parameter
encoding. Supplied count is neither current device state nor maximum capacity.

Its separate source index retains raw selectors, exact FirstApp rebasing, nullable
masks and distinct Data/Count components. Unknown PDT/count/capacity stay distinct;
the first patch profile refuses missing count or PDT and reports unknown capacity
as an unresolved requirement. No type-to-index mapping is guessed. The new
PDT-role fingerprint does not change the older property-width diagnostics or
memory bindings. MCP reports buffer hashes and bounded source lists with totals.

This first property patch profile refuses modules, Repeat, Union, calculations,
aliases, external state and every parameter Memory declaration, including hidden
or unselected owners. Physical Memory/LSM/property aliases are unresolved. It
does not update RawData/counts, evaluate scripts, finalize an image or authorize
execution. Positive complete-property fixtures are synthetic; inspected public
manufacturer inputs contain no factory-property before-data. General property
interoperability and complete device-image construction remain unverified.

Storage details expose effective coordinates, declared resource sizes, type footprints
and archived mask metadata. Native property indices remain unchanged; system/
application indices and PDT element widths are separate diagnostics. Unknown
capacities remain unknown. General property/image construction is incomplete.
Mask-derived facts require an explicitly supported application namespace
(/20, /21 or /23), even when another master format could otherwise supply a
matching mask. Raw native selectors remain available independently.

## Offline image inputs and load procedures

The archived resource index reads one exact mask/current-zero set for application
and master namespaces /20, /21 or /23. It retains separate Location and ImgLocation,
access/management selectors, memory regions and native/application/type property
coordinates. Remote resolution requires a unique matching declaration. Pointer,
None, Constant and ADC declarations do not become memory buffers or physical bases.
Declared resource length, supplied payload length, PDT width and property capacity
are separate facts.

`ArchivedImageSources` pairs the application ID with its actual archive entry and
native manufacturer container, then checks the exact linked Hardware2Program and
its single application reference. It reads immutable loaded parts, including after
an external file overwrite. Raw master bytes, serialized document and selected
resource facts have separate hashes. This source binding does not claim parameter
or communication-object activation.

The bounded factory decoder reads only supplied compressed buffers and explicitly
present Data/Mask. It distinguishes logical memory/property selectors and a
separate two-byte property-count component; it does not infer a current count,
capacity or count-update policy. Property-by-type counts retain their occurrence
and have no data offset. Unsupported index-count marker variants are refused.
Invalid, truncated, trailing, overlapping or
oversized input returns no partial segments. Limits are 1 MiB compressed, 4 MiB
decoded, 1 MiB per payload and 1,024 segments. Checksum blobs remain opaque and
unverified. Factory source is not current device state. Synthetic fixtures cover
positive decoding; the inspected public inputs provide no positive factory blob.

`OfflineImageComposition.Preview` provides a separate version-1
`RootStaticCodeWorkingSet` under an explicit `SourceDefinedBits` or
`NoStaticSourceBits` policy. Root Code Data and nullable Mask remain raw source
facts. Working allocation covers the declared size or supplied Data length;
missing Data and short tails have zero defined bits. Under `SourceDefinedBits`,
supplied Data uses its explicit mask, or all bits when no mask is supplied.
`NoStaticSourceBits` leaves all working bits unspecified. A procedure's Full flag
does not select either policy. No factory buffer is required or applied.

Contributions are sorted in distinct absolute or logical LSM arenas. Compatible
overlaps merge only their defined bits; conflicting defined bits refuse the
entire result. Chunk masks, unknown spans and bit counts preserve every missing
bit. Zero-filled working bytes are not known initial/current device bytes, and
unknown spans imply no concrete device-read action. Parameter/table writers,
property/mask resources, physical mapping, fixups, Secure and finalization remain
explicit requirements. All image, execution and download authority stays false.

Its separate source-only binding includes the explicit policy, source geometry,
payloads, composed chunks and requirements. It does not depend on saved parameter
values or claim activity. Limits are 1,024 sources, 1 MiB per source allocation,
4 MiB aggregate working allocation, 4 MiB raw payloads and 4,096 unknown spans.
The composer bounds its new retained byte payloads; this is not a bound on the
whole project or process heap. MCP exposes hashes, lengths and capped lists:
128 sources/chunks/source identities and 256 unknown spans/requirements, with
complete totals and explicit truncation.

`OfflineRootImageBuild.Preview` adds a separate version-1
`RootFixedMemoryWorkingSet`. It inventories every admitted original primary-root
reference, including inactive/Always records and proven NoStorage dispositions,
with no caller subset or byte order. One owned evaluation supplies ordinary
reference activity and current values. Current values follow Parameter, optional
reference override, then saved value; inactive defaults use the underlying
Parameter value. Removing a saved key cannot revive a stale retained XML value.
Raw saved strings and key presence must match the captured context exactly,
even when two spellings encode to the same numeric word.

Native source Options determine parameter byte order and inactive behavior.
Missing minimum versions, versions below 5.0 and absent Options use Background; modern Options
with an omitted inactive policy use DefaultValue. The working profile explicitly
includes customer-adjustable fields; actual device load options remain unverified.
Normal parameter records validate whole touched bytes; application prefix
`M-009E_A-1000-01` validates only field bits. Null Background records still validate whole
touched bytes. NoStorage is an original declaration fact, never an unknown address.

Raw Code, working data and desired validity stay separate. A source mask byte of
zero clears that working byte; a partial mask retains its raw bits. Missing tails
start with zero data and zero validity. `NoStaticSourceBits` resets validity without
erasing the working seed. Canonical desired bytes equal working data AND validity.
Optional M112/SystemB tables share the same evaluation. Every retained table's
complete encoder-input span must equal current working bytes; fresh spans must
actually be zero. Tables add their WriteMask to current validity. Seed mismatches
or parameter/table data-bit collisions discard the entire result.

This first route requires literal root Memory and unique Code allocations with
proved footprints, including latent/unreferenced owners and null records. It uses
the existing bounded Number/Value-enum/Single/Packed-Time/ordinary-Time codecs.
RawData, unsupported/unknown types, Double/DPT9 declarations, modules, Repeat,
Union, allocators, derived values, Property writers and mixed absolute/relative
mappings are refused. Different LSMs remain logical arenas. Parameter declarations
and native carrier namespaces/owners are validated before evaluation;
unknown retained parameter/module/binary carriers do not disappear silently.

Limits include 100,000 references/owners, 500,000 nodes at depth 128, 4,096 enum
choices per type and 65,536 aggregate choices before typed parsing. Code limits
are 1,024 sources, 1 MiB per payload and 4 MiB raw data/masks; combined working
allocation, including table-only intervals, is at most 4 MiB, with 4,096 unknown
spans. Retained root byte payloads total at most 16 MiB; temporary buffers, table
payloads, XML and the rest of the process have separate costs. The new binding
replays this same scope with source, carrier, raw input, population, policy,
geometry, requirements and table-input witnesses. All image/execution/download
authority remains false. General writers, mapping, device state, Secure and
finalization remain explicit requirements. MCP exposes bounded hashes and counts.

`OfflineRootImageBuild.PreviewResolved` adds a separate version-2
`RootFixedMemoryResolvedWorkingSet`. It combines the same complete admitted fixed
writer population with bounded root conditions, relative comparisons and optional
M112/SystemB tables. The prepared procedure supplies reachable controller seeds
before one application evaluation; a strict view of that same context proves
original controller declarations, placements, active owners and raw value closure.
No runtime or supplied context, controls, values or table fragments are accepted.
Unsupported assignments, aliases, derived state and source changes discard all
output. Unused procedure groups retain declaration checks without becoming active
controller seeds. Every existing writer, allocation and table-seed check still
applies.

This route has its own immutable request, controller proof and replay binding.
Graph, shared evaluation, placement inventory, actual procedure and optional table
emission/compatibility hashes participate in freshness. Tables provide detached
emission facts through this root route. The ordinary root and procedure-only
contracts retain their prior versions and fingerprints. A primitive procedure
keeps its version-1 form inside the version-2 root plan. MCP's explicit
`root_resolved_image_build_preview` object owns its procedure selection and optional
table format, excludes other preview routes and exposes bounded hashes/counts.
The same unresolved image requirements and false execution/download gates apply.

`OfflineRootPropertyBuild.Preview` adds a separate profile-1
`RootFixedPropertyWorkingSet`. One source capture and application evaluation
supply all admitted fixed-root Number/Value-enum references, strict procedure
controllers and the actual selected procedure. Actual nondefault WriteProp
controls determine every allocation, including unrelated selected properties;
their positive Start/Count extent uses a known symmetric PDT write width.
All physical owners, including latent and inactive records, need full coverage.
Source options determine byte order, inactive/default/Always behavior and mask
effects. NoStorage references retain their distinct disposition.

Exact raw factory data may initialize or grow an allocation; absent factory
declarations start zeroed working data. Initial validity is explicitly zero in
either case, independently of archived masks. Inline action bytes never seed
the allocation. Archived Count data remain separately observed, without being
adopted, prefixed, changed or interpreted as capacity/current device state.
The binding covers source closure, allocations, initial data, complete population,
shared procedure proof, desired bytes and all unresolved requirements.

This first profile excludes mixed Memory ownership, code contributions, selected
memory regions, contributing pointer sources, unknown/asymmetric PDTs, ambiguous
factory sources, PID27 and special allocation controls. Declared but unavailable
or empty factory containers fail atomically. Budgets are 128 properties, 1 MiB
per property, and 16 MiB for all newly owned property payload arrays, reserving
11 times the aggregate allocation length across initialization and formatting.
Existing XML, decoder and procedure limits remain separate. Positive integration
fixtures are synthetic; this adds no general MDT/Jung/Gira interoperability claim.
General mapping, device options/state, count policy, other writers, transfers,
checksums and finalization remain unresolved. Complete-image, execution and
download eligibility stay false. MCP exposes this stage only through its own
closed `root_property_image_build_preview` option.

### Combined logical working stage

`OfflineNativeImageBuild.Preview` and the closed MCP `native_image_build_preview`
option use one combined pipeline and one application evaluation for all declared
option assumptions. The stage combines selected nonoptional EEPROM regions,
native code normalization, contributing four-byte pointer buffers, actual
nondefault WriteProp property allocations and the entire admitted direct-root
Number/Value-enum population (1–32 bits), aligned IEEE-Single32 fields,
packed day/hour/minute/second Time24 in big endian and signed ordinary-Time8–32
fields. Ordinary Time stores raw unit counts; little endian requires whole bytes
at bit zero. Single preserves negative zero and refuses nonfinite conversion.
NoStorage Single/Time and Single/Time procedure controllers remain outside this
profile. NoStorage records remain distinct from
an unscheduled parameter stage, which emits no writer records. Default, Always,
inactive-null and customer-exclusion mask effects are
source-bound. Logical targets can mix standard memory, relative memory and
canonical properties; this does not establish physical disjointness.

The public policy reports source byte order, inactive-value behavior and codec
profiles with separate fingerprints. Codec counts cover every physical owner,
including latent fields, even when the parameter task is skipped. Single uses
the explicit .NET 10 XML Double-to-Single parser profile. Existing numeric-only
plan, binding and byte hashes remain unchanged.

Direct byte-aligned root Memory `TypeRawData` fields share this same pipeline,
including the M112 image/change builder. The reserved extent is `MaxSize + 4`;
the writer emits a 32-bit payload length in the program byte order and unchanged
opaque payload bytes. Only `4 + actual length` bytes acquire parameter write bits;
the tail retains the existing seed/validity policy. Strict Base64, capacity, aliases,
source binding and payload budgets remain enforced. Raw property, Union, module,
NoStorage and unaligned fields remain refused. Raw inputs retain the shared
65,536-character limit. Policy codec profile 4 reports raw field count/framing;
profiles without RawData retain their previous fingerprints. Forty-four core and
ten MCP checks cover this extension, including full M112/change-plan integration.

The same shared stage admits a bounded global-Memory module profile: unconditional
flat calls, literal nested Repeat and application-owned Memory-offset allocators,
with inherited 1–32-bit Number/Value-enum writers. Original call/definition/instance
origins include active, inactive and unreferenced latent owners. Complete allocated
spans, including padding, require initialized backing and cannot alias foreign owners.
First allocation alignment and inclusive pool ends are checked. Source-derived
instances are allowed when the native ModuleInstances carrier is absent or empty;
a nonempty saved carrier must contain every exact instance and argument.
The immutable public population reports origins, instances, allocations and
reservations. Limits are 256 occurrences, depth 8, 500k planned nodes, 100k
parameters/references, 500k premeasured repeat work and 5m combined validation
candidate checks. Empty repeat branches and large root-owner populations count
toward these budgets. Previous inspection and no-module profiles retain their
existing contracts. The M112 union/download route does not admit these modules.

A bounded direct-call case in the same stage supports up to two independently
proved root Value-enum choices, literal global Memory offsets and no Repeat or
allocator. It validates every potential instance and latent owner before selecting
the instantiated subset from one shared evaluation. Uninstantiated references emit
no write or native record; their saved values remain retained and source-bound for
later activation. Public/MCP facts distinguish potential and selected instances.
This case requires an absent or empty native ModuleInstances carrier and refuses
module LegacyPatchAlways. Eighteen independent full-buffer reference comparisons,
108 focused checks and nine nonzero-backing preservation cases cover the profile;
they do not establish manufacturer or complete-image compatibility.

Sources, initial Data/validity, working Data, desired validity and canonical bytes
remain separate. Factory masks and Count words are not adopted. Contained and
strictly overlapping region/code and pointer/factory contributions use an ordered
fold schedule, including repeated growth and bridging existing components.
Spatial order, including incoming-first equal starts, controls nullable-mask
preparation and raw exclusive tails. Source owners remain separate; overlapping
code owners refuse. Missing or short maskless code data creates a
valid supplied prefix and unknown padding. Non-QueryFull resets validity without
clearing Data. Applicable family invalidations precede parameter writers.
Physical/null records and conservative latent owners need covering initialization.
Order-sensitive validity clears/sets refuse, including disjoint fields in a
shared byte when whole-byte effects overlap.

One to sixteen distinct full uint32 words produce at most 32 conditional rows;
unknown consumed IP facts expand rather than become guessed facts. Actual
options/route remain unverified. A global source, union or aggregate-budget failure
returns no alternatives or binding. A local unsupported alternative remains an
explicit empty unavailable row. The replay binding covers the complete alternatives,
source closure, evaluation, masks, population, procedures, tables and requirements.

M112 and both SystemB table formats consume privately captured current working
seeds after parameters, with supported initialized backing and operation-specific
mapping. They preserve untouched header bytes and refuse data-write collisions.
Fresh table allocation without backing, full pointer/size adjustments, unused-tail
invalidation, further writers, configuration and finalization remain open.
Conditional calls outside that direct profile, parameter-driven repetition,
nested modules, local pools/code/types,
module property/Union/RawData storage, additional allocator domains, Union/aliases,
assignment/calculation dependencies, raw/count writers,
unsupported PDTs, unaccounted external/binary state and other native carriers
remain outside this first profile. Positive combined fixtures are synthetic;
there is no new general MDT/Jung/Gira compatibility or complete-image claim.

A single 16 MiB image-payload ledger covers new code capture, materialization,
staging, immutable/current-seed and final copies across every alternative, with
conservative reservation before materialization. Distinct procedure preparations
share a separate aggregate 4 MiB decoded-operand limit and conservative 16 MiB
copy reservation. Existing XML/decoder/metadata limits remain separate; this is
not a total-heap bound. Identity/value budgets precede source hashing. All complete
image, execution and download eligibility flags remain false.

The explicit `load_procedure_preview` option on `download_plan` selects a native
subtype, remote access and all three Full/Parameters/Groups flags. It compiles
primary Product, Default and Merged declarations into ordered typed controls,
merge decisions, source-resource references and three generated-table roles.
Missing merge groups are skipped; inserted auto conditions become all before
the explicit parts filter. Inline action bytes remain action arguments. They
are not property before-data.
This original profile keeps its version-1 procedure and witness shape.

The separate `OfflineApplicationBuildPlan.PreviewResolved` route and explicit
nested `resolve_conditions:true` option resolve bounded root conditions and
relative-memory comparisons. They produce a version-2 procedure with original
element paths, merge insertion occurrences and branch decisions. Conditions use
strict integer syntax, all matching branches and a sole fallback only when none
matches. An inactive controller skips its whole choice, including the fallback;
an active alias never substitutes for the requested reference's own placement.
Inline comparison bytes and masks remain action operands.

All candidate product groups and all branches of the selected master template
are parsed before values are selected, including unused groups. Declared
controllers must resolve to direct root Number/Value-enum types. Activity uses
the relevant normal reference owners and every enclosing selector, with an
independent bounded acyclic proof. Relevant assignments, module/Repeat activity,
calculation declarations and unaccounted DCA/plug-in or binary state refuse this
profile. A source-only primitive procedure makes no activity claim and retains
the complete original version-1 procedure. The new binding also covers the
prepared graph and controller context and rechecks this same route for freshness.
It does not execute scripts or use a GUI parameter snapshot.

Conditional resolution and `generated_tables` cannot be combined on this older
procedure-only route; the shared resolved-root route above supports their bounded
combination with the full admitted writer population. Nested product
merges remain outside the profile. Source depth is limited to 128, source controls
and choices to 4,096, and expanded nodes, decisions and controls to 8,192.

The build binding additionally covers the procedure witness and declared project
values, objects, connectors, group addresses and binary inputs. Source, model and
undo remain unchanged. AP2, module procedures, complete
activation, fixups, global policy and finalization remain uncompiled requirements.
Execution, complete-image and download eligibility are always false. Omitting the
option retains the existing preflight and legacy subset output.

MCP returns lengths/hashes rather than complete buffers. Source presentation caps
are 64 resource declarations, 32 regions and 32 factory segments; procedure caps
are 512 controls, 512 branch decisions, 256 merge decisions and 64 resource references. Full counts and
explicit truncation accompany these views; their bindings cover the complete
validated input, including an undisplayed tail. Core retains the complete results.

An explicit table request adds actual group-address, association and communication-
object Data/WriteMask fragments through `OfflineApplicationBuildPlan.Preview` and
MCP's nested `generated_tables` option. The admitted profiles are MV-0705 M112 with
fixed nominal placement and MV-07B0 SystemB with an explicit 2- or 4-byte association
format. This choice does not establish the connected device's association width.
All three payloads are produced together or refused together.

One fresh evaluation is checked by an independent acyclic ordinary-reference
activity proof. The bounded object-number inventory retains inactive definitions
and holes. Typed module origins and complete occurrences are checked against the
original application. Display activity does not activate an object. Native
flag/priority overrides, current link order, sending and
acknowledgement facts remain distinct from supplemental connector metadata.
Unresolved origins, conflicting overrides, duplicate active numbers, inactive saved
objects with links and unaccounted binary state refuse the preview. Assignments
or calculations affecting activity controllers remain unsupported, including with
an explicit runtime. Unrelated calculations do not automatically require a runtime.
Union-backed controllers remain outside this logical-activity profile; no union
storage or alias-write permission follows from these table previews.
Known DCA-dependent applications and explicit external plug-in declarations also
refuse until their configuration state is accounted for, even without a binary block.

Module admission covers flat literal calls, either unconditional or under up to
two root choices. Each controller must be an independently checked Value-enum
with exactly one unconditional root reference owner. Every possible call, argument and used
definition body is checked before evaluation, including unselected calls.
Repeat, nested module calls, allocators and deeper conditional calls remain unsupported.
Overlapping matching branches, activation cycles and differences from the current
snapshot are refused. A branch combining a true default with a test also remains
outside the checked profile.

M112 requires complete retained Code bytes for preserved headers, pointers and flag/
size bits; dynamic table management is outside this profile. SystemB generates fresh
logical LSM 1/2/3 buffers only when no retained owner conflicts. Declared Code size
does not synthesize initial data. Existing factory-image declarations, required
acknowledgements, secure links and other required medium-specific semantics remain
unsupported. WriteMask describes newly assigned whole-byte writes, separately from
source-mask validity. Unused M112 capacity-mask invalidation remains an explicit
requirement outside the used fragments.

The separate table binding covers the full native witness, source selection,
procedure, format, payloads and size adjustments. When an explicit runtime
participates, freshness checks require a freshly supplied runtime; no interpreter
is retained. Each
payload is limited to 1 MiB, with 4 MiB aggregate output; native definitions,
references and placements share a 100,000-item budget. Complete parameter activation,
physical mapping, applying procedure adjustments and image finalization remain open.

## Local history and recovery

The Workbench queries persistent telegram history with UTC/offset time bounds,
multi-value filters, stable pagination and labelled context. Loaded rows and capture
coverage are separate; unknown ends, gaps and retention boundaries stay visible.
Recording is opt-in and opening the viewer does not connect a bus. Export, backup
and explicit retention operate on the local store; see [workflows](workflows.md).

Optional versioned checkpoints retain committed model and undo/redo together with
an independent audit journal. Recovery requires explicit source selection, full
validation and a guarded adoption decision. Protected sources produce no plaintext
checkpoint/audit content; private parameter/DEMO drafts and layout are excluded.
Missing provenance remains visibly model-only. Existing file projects must be
reopened after enabling recording. Unsupported recovery never blocks ordinary
project opening or saving. See [scope, storage and limits](recovery.md).

## Validation

The current Windows build passes all 30 maintained suites, the MCP selftest and
854 native dialog checks in each of German and English, plus 33 recovery lifecycle
checks per language. Encryption passes 402 checks, including 25 recovery persistence,
source-binding and protection cases. Six import regressions cover unique XML identities, including IDs
allocated for different element kinds in the same operation. All 51 fixed
compatibility comparisons below pass without recapturing their envelopes.
App-owned modal windows share a theme-aware caption on Windows; native file
pickers and the explicit native-titlebar fallback keep system decorations.
This is not a completed manual Linux/macOS or acoustic screenreader acceptance.

The solution contains 40 projects. The maintained regression matrix covers
30 suites and the native modes listed in [test commands](../CONTRIBUTING.md).
The property extension passes both affected Core/parameter and MCP suites locally.
The Time slice
adds 106 Core and 70 MCP checks. Persistent projects reproduce four complete legacy
fingerprints exactly. An independent five-application comparison covers 14,819
parameter rows without differences; test inputs stay outside version control.

The /20 and /22 slices pass Core, 297 archive and 265 encryption checks plus MCP
and Workbench lifecycle integration across all three layouts. Independent public
archive roundtrips retain native identities and unrelated entries; the saved
native documents pass their respective /20 and /22 schemas. The public /22 case
contains no devices; synthetic fixtures cover device, parameter and module state.
Inputs remain outside version control.
AE-1 additionally checks the decompressed content CRC after ciphertext
authentication, including streamed entries with zero local CRC/size fields.
Tests distinguish valid authentication from an incorrect central CRC; AE-2 keeps
its format-specific authentication handling.

The resource backend adds 64 Core-facing checks plus MCP contract checks. A public
MDT root-relative case changes a selected two-byte value in a 3,972-byte resource
with either explicit byte order, preserving all 3,970 other bytes. An out-of-range
value is refused atomically. Sources, saved values and undo remain unchanged.

The image-source/property/procedure/table/composition/root-working layers pass 1,878 focused Core-facing checks and MCP
regressions for explicit arguments, bindings, errors and bounded presentation.
These include 33 property-source and 80 fixed-field property checks. Independent
literal buffers verify `AABBE3CC` to `AABBF7CC` while count `0002` and all unrelated
bits remain unchanged. Tests cover masks, source completeness, byte orders,
ownership, native containers and isolated PDT-role fingerprint changes. Four
complete legacy binding/byte-range goldens remain exact after the shared scalar
helper extraction; the reference envelopes are not regenerated.
Twelve complete version-1 root snapshots, from six fixed saved projects under
both mask policies, also retain identical full plans, bindings and bytes.
Twenty-four fixed version-2 root snapshots preserve both conditional and primitive
procedure paths under both mask policies: all 40 pre-existing complete comparisons
remain exact. Six additional property envelopes pass fresh-process full-plan,
binding, initial/working/validity/canonical-byte and Count replays.
Five fixed numeric-only combined plans also retain their complete diagnostics,
bindings and hashes, apart from the newly added policy view. The combined scalar
extension adds 74 Core-facing and 58 MCP checks with independent full buffers,
byte-order and mask cases, latent-field freshness and atomic refusal.
The shared root-writer helper adds 21 checks; property initialization adds 61 and
the complete property working stage 57. MCP adds 119 checks for full 520-byte
results, 128/129 allocations, 513 real controllers/decisions and 516 controls,
hidden-entry freshness, strict arguments before project/gateway access, observed
Count semantics, unchanged ordinary JSON, source/model/history retention and
atomic refusal. Controller-set hashes remain stable when only values change;
the complete content proofs and byte hashes change.
The combined logical stage adds 182 Core-facing checks: 36 inventory/population,
36 initialization/selection, 41 complete mixed working runs and 69 public-API
checks. MCP adds 85 checks for strict early parsing, full hidden-entry hashes,
shared diagnostic limits, all 32 alternatives and current working-seed tables.
Independent complete arrays verify 131 references, mixed memory/property data,
five pointer allocations, untouched unrelated properties, raw Count separation,
native byte order and all three table formats. All 46 older fixed full-plan,
binding and byte comparisons remain exactly equal without recapturing envelopes.
The union-growth extension adds 71 direct coalescer checks and 39 complete
integration checks. Independent full arrays cover equal-start ordering, both
extension directions, nullable masks, raw tails, Pointer4-to-Factory8/12 growth,
1 MiB/unsigned-end boundaries, shared-array rejection, exact allocation budgets
and exact source ownership. The ordered-fold extension adds 53 Core-facing and
43 MCP checks for repeated growth, equal starts, bridging, full arrays, shared
options and cumulative allocation limits. Failed schedules return no partial result.
The shared-context table route adds 39 checks; the complete fixed-root working
stage adds 122. Independent literal serializers cover 130 references/520 bytes,
native byte order, defaults/Always/NoStorage, seed masks, all three table formats,
raw capture changes, collisions, stale bindings and atomic refusal. MCP verifies
argument validation, bounded output, unchanged ordinary responses and closed device
gates. These root/table integration fixtures are synthetic; existing manufacturer
comparisons below establish their individually stated profiles.
The shared procedure adapter adds 40 checks and table materialization 48. The new
resolved table entry adds 51 and the complete resolved-root route 57, covering
all three formats, primitive fast paths, actual conditional controls and size
adjustments, strict source/context pairing, complete raw guard closure, unchanged
inputs and atomic failure. MCP additionally verifies the joint 130-reference/table
route, separate hashes, presentation caps and validation before gateway access.
The static-code working set adds 73 checks, including exact masks, missing tails,
atomic conflicts, replay, ownership and allocation limits. Independent original-XML
expectations match both policies on MDT `M-0083_A-0137-20-7EB2` and Jung
`M-0004_A-5018-15-17D9`: 3,972 supplied MDT bytes define 31,776 bits; Jung's
1,125-byte prefix defines 9,000 bits while 63,490 allocated tail bytes remain
unknown. Gira `M-0008_A-20E0-22-B419-O000A` retains 1,352 defined bits and
59,136 unknown bits across 7,561 bytes, including the exact 7,392-byte unknown
middle span. Source/chunk hashes, unknown spans and totals match; source archives,
own project envelopes, project summaries, undo and ordinary MCP output are unchanged.
These comparisons establish static source contributions, not complete images.
Public MDT/Jung/Gira pilots match independent XML evaluation of every emitted
control, ordering, filtering, merge decision and explicit argument. The admitted
cases emit 22/37/33 controls in each of two explicit parts selections, retaining
31/40 archived resource declarations and the original supplied resource bytes.
All source/envelope hashes, project summaries and undo remain unchanged; no
manufacturer scripts or device writes are used by these pilots.

The separate root-condition pilot uses the unchanged MDT JTA application
`M-0083_A-00A2-10-6169` and its archived master template. Three explicit root
input combinations emit 31/32/32 controls. An independent XML traversal compares
every selected control, declared operand, inline hash, error policy, merge,
branch decision and original-element/insertion provenance. It verifies the
inactive requested reference despite an active alias and preserves the split
writes `[0,835)` and `[904,1154)` without filling their gap. Source archives,
own envelopes, project summaries and undo remain unchanged; scripts stay disabled.

The public MDT0007 table case additionally compares complete Data/WriteMask and
mask hashes with an independent serializer using a fixed prior activity baseline.
An independent original-XML selection checks all 72 possible module calls:
24 selected and 48 skipped with the default values. SystemB covers 299 slots,
74 active references and both explicit association formats. The four-byte case
includes a linked module object numbered 257. Sources, own project envelopes and undo remain
unchanged. These are offline comparisons. The current table profile refuses
MDT0317's activity calculations, Jung5018's unaccounted DCA state and Gira20E0.
M112 table generation and retained-mask handling use synthetic validation fixtures.

CI runs portable suites on Linux and native suites on Windows; CodeQL and CLA
checks apply to pull requests. Read the check results for the exact commit being
reviewed. Tests generate synthetic fixtures; optional catalogue checks use local
inputs. See [CONTRIBUTING](../CONTRIBUTING.md).

## Native UI and accessibility

The native Workbench supports eight UI languages, Light/Dark, a project portfolio,
object-centred tabs, catalogue, inspector, reviewed linking and mail-merge TSV.
The native Bus diagram representation reads the current installation/area/line/
segment/device containment tree, including empty containers and area-level devices.
It offers native accessible node buttons, editor navigation, arrow-key traversal,
bounded zoom, background panning and a readable fit with scrolling for large trees.
Coupler badges require manufacturer hardware evidence; connectors describe project
containment, without claiming physical wiring or live status. Per-tab selection,
zoom and scrolling survive editor switches; edits and undo/redo rebuild the read
projection from the current model. A synthetic 4,102-device case realizes only the
viewport's node controls. The standalone Windows `--bus-diagram` regression mode
passes 27 checks in each of German and English, including actual arranged focus
bounds, queued refreshes, language changes and the compact inspector layout.
Light/Dark native renders have been visually inspected; this does not close the
remaining manual DPI, pointer-device or acoustic screenreader acceptance.
Local DEMO comparison state is stored separately from project/undo and does not
prove that a device was programmed.

Windows scaling 150/200 %, limited keyboard navigation and real contrast changes
have been observed on one screen. On 2026-10-27, actual Windows Night sky and Desert
palettes confirmed readable primary buttons, checkbox captions, command-palette
input/results and Settings dropdowns, including normal/hover/selection states.
Keyboard filtering and theme changes under high contrast were checked separately.
A keyboard-only pass covered project open/save, group creation and rename through
the inspector, undo/redo, DEMO parameter review/discard/apply, the command palette
and Settings navigation. Native dialog regressions check localized editor names,
both navigator context-menu gestures and focus return in German and English.
The command palette now activates the selected result when its actual ListBoxItem
has keyboard focus, preserving Enter from the query. Settings bounds its native
frame to the current monitor working area and keeps its scrolling body separate
from the fixed action row. An actual 200 % Windows recheck confirmed a new,
unmaximized Settings window with both action buttons visible, its last editor
reachable by scrolling and Tab, and correct Cancel/Save keyboard focus. Native
dialog suites pass 854 checks in each of German and English; the item-Enter
regression failed in both themes before the correction. The ordinary UI-state
suite passes 236 checks. Navigator expansion/collapse and selection retain the
current row's focus across queued refreshes without taking focus from another
editor. Eight initial native failures exposed the focus loss; two remaining
failures exposed a second refresh before focus restoration. Both themes now
pass in both languages, including immediate movement to the child row and
an intervening refresh. A real English retest confirmed Right/Left, repeated
arrows, Down to the child editor and Home to the project with row focus retained.
Initial search focus used the pointer. These are synthetic project workflows.
The New project wizard now names all five editors from their visible localized
labels, including the two focused inner NumericUpDown text editors. Ten outer-name
regressions failed before the fix; four further focused-editor regressions exposed
the missing name propagation. Both themes now pass in German and English. A real
English Tab pass confirms Name, Comment, address style, Areas, Lines per area,
Cancel and Create. Escape leaves the project unchanged. This is UIA/focus evidence,
not acoustic Narrator acceptance. A separate real project-creation pass completed
the native Save picker after one visible filename-field click; keyboard input
and the final path were verified visually. Picker UIA focus remains unreliable,
so this is not a wholly keyboard-only picker result. The same isolated project
passed line rename, Ctrl+Z, Ctrl+Y and Ctrl+S, with persisted XML checked separately.
An actual subsequent GUI reopen retained that saved line name; the file hash
remained unchanged throughout the navigator retest.
A later click-free Ctrl+N/Create/Save pass verified Alt+N and visible Ctrl+A
filename selection before entering the own output path. Keyboard-only Open
remains unverified; its empty filename field could not be confirmed visually,
so that separate attempt was cancelled before any path input.
A separate topology pass confirms context-menu line creation and undo through
observed line counts 2→3→2; initial navigator focus and focus before undo used the
pointer, so this is not a complete keyboard-only workflow.
The parameter-device selector has a localized UIA name and a friendly
address/name value matching its visible caption. A real English open/Escape/focus
pass confirms both, followed by unchanged Tab navigation through Function,
Status LED and Debounce. Native selection inspection preserves model and undo.
Separate manual flows confirm group-address creation/undo and DEMO parameter
review/discard; these do not establish a manufacturer editor's acceptance.
The DEMO parameter pass does not establish a native manufacturer editor's
keyboard or screenreader acceptance.
Editor refreshes and selection changes reuse a single local row projection for
device selection and inspector content. No persistent row cache crosses model
replacement. A separate native mode passes 26 checks for checkbox/table selection,
multiple/empty selection, filtering, collapse/expand, tab state and actual
edit/undo/redo or same-ID project replacement. It establishes behavior, not a
latency improvement.
Mixed DPI/monitor changes, full keyboard acceptance and manual Linux/macOS
acceptance remain open. Acoustic Narrator testing is deferred by user decision
as of 2026-10-28.
Actual Windows display settings offer only one internal display here, at 100 %
and 1920×1200/60 Hz; transfer between monitors with different DPI is unavailable
in this environment and has not been simulated or marked passed.

The opt-in `--performance-budget` mode enforces an engineering regression budget
for the exact Windows x64, .NET 10, Avalonia 12.1.4, Light/English reference case:
1440×900 DIP at scaling 1, 48 devices × 250 objects, eight group addresses,
three warmup cycles and 24 measured cycles of six actions with a running DEMO
monitor. Each action requires nearest-rank p95 ≤ 500 ms and maximum ≤ 1,500 ms;
process peak working set must be ≤ 512 MiB. Boundaries are inclusive. This is
not a universal hardware performance claim.

A separate `budget.json` binds the freshly saved measurement hash, ordered
fixture hash and before/after App/Core/harness assembly and environment evidence.
The policy recomputes statistics from all 144 typed raw samples. Missing native
progress, incomplete/invalid evidence and deadlines cannot pass. The pure policy
suite passes 123 checks and runs in ordinary CI; the native timing budget is opt-in.
The original `--performance` mode remains measurement-only.

On 2026-10-28, a fresh reference run after unlocked-desktop inspection and actual
100 %/normal-contrast restoration passed every limit. All 144 samples and the
before/after binary, environment and fixture evidence are complete. Per-action
p95 values range from 134.0 to 294.4 ms, the maximum is 309.6 ms and peak working
set is 366,166,016 bytes (349.2 MiB). An independent calculation from the saved
raw samples matches every median, nearest-rank p95 and maximum, and verifies the
measurement hash and unchanged before/after evidence.

Earlier runs had five and then two p95 exceedances; maxima and peak memory passed.
These runs are not a controlled causal comparison. Neither a later observed lock
screen nor the new successful run establishes the cause of the timing differences.
Rendered batches and dispatcher progress do not prove physical presentation,
manual input or acoustic screenreader acceptance.

## Network and commissioning

KNXnet/IP discovery, tunnel/routing and management foundations have synthetic and
loopback checks. The Workbench offers explicit read-only ordinary and IP Secure UDP tunnels,
real group monitor and addressed line scan alongside labelled demonstrations.
Connection options belong to one project lifetime; project replacement, cancellation
and window shutdown join network cleanup before an endpoint can be reused. Real
monitoring supports the existing recording, filters, CSV and optional local history;
it is not a complete hardware bus monitor. Scans require disconnecting the monitor
connection and report unknown/unprobed addresses without claiming absence.
The general Workbench programming queue remains a labelled simulation. General device writes and
application downloads remain disabled. An explicit console-only MDT M112 programming-mode test
has live identity, write, readback and restoration evidence for one released device;
see [targeted commissioning](commissioning.md). Complete images, complete load flows
and general management/Verify/commissioning remain incomplete.

A separate M112 profile builds bounded root unions and an absolute
full-load recipe for reloading an already matching configuration. Known persistent
bits, identity, required PEI and table pointers must match before unloading.
Durable recovery combines captured configuration bytes with the source recipe;
unobservable allocation metadata and volatile runtime state are outside that claim.
The [commissioning page](commissioning.md) records its exact validation and live status.

A reviewed M112 change plan separately binds baseline and desired projects with the
same application/layout. The change mode compiles changed parameters and
tables, validates actual baseline and active-object RAM geometry, and recovers the
captured original configuration after failure. This does not enable first provisioning,
other mask profiles or general Workbench programming. A separate read-only line scanner
supports explicitly selected device/coupler addresses, joined cancellation and honest
unknown/unprobed outcomes; it cannot issue configuration writes or restart.

The dedicated M112 Workbench dialog exposes offline review, explicit physical
identity/target/plan confirmation, guarded apply and inspected-file recovery.
Recovery-only mode works without a project or running device application.
Close/project replacement join active recovery; an unresolved recovery requires
explicit acknowledgement before leaving. The shared recovery inspector and executor
validate the same bounded snapshot and recheck its exact file hash before opening.
The extension has 38 state checks, 12 actual compiler/recovery fixtures and 25 native
checks each in German and English. These are separate from the real device evidence above.

The library's [authenticated Secure UDP session](secure-session.md) uses OS X25519,
verified gateway/user authentication, bounded framing, replay/counter checks and
joined cleanup. Twenty-eight focused Secure and 16 legacy-refusal checks pass on
Windows and Ubuntu 26.04; the complete bus suite passes on both. The previous
non-interoperable legacy Secure encoders/parsers are fail closed. Protected keyring
import verifies the full signature before decrypting owned secrets and requires exact
project-label and tunnel selection. The separate Data Secure group library supports
auth-only/auth-conf, explicit group/sender policy and durable send/receive counters.
Its optional tunnel processor persists before send/dispatch, deduplicates before
verification and binds own confirmations to prepared frames. A separate optional
monotonic anchor can detect full-file rollback; an ordinary backup cannot provide
that guarantee. Missing/corrupt state never creates fresh counters automatically.
See [keyring scope](protected-keyrings.md) and [counter/recovery limits](data-secure.md).
[Workbench/MCP connection controls](secure-connections.md) use one explicit profile,
one gateway reservation and one credential capability across fresh M112/recovery
connections. Exact assigned IA and source pins are enforced; known protected groups
cannot silently become plaintext. Native selection and password controls have
German/English checks; typed errors omit private source paths and credentials.
The separate [individual Tool/P2P library](individual-secure.md) adds explicitly
provisioned unicast data, authenticated client Sync, durable max merging and a
joined tunnel initializer. Twenty-one state/codec and five tunnel checks cover
synthetic vectors and actual UDP loopback. It retains missing-state and key-domain
gates; no real Secure device has been accepted. An explicit
[Tool-Access management operation](tool-access-management.md) joins the shared
factory, exact verified device key and existing trusted state across fresh opens.
Twenty-one focused checks and full Windows/Linux Bus regressions cover its
pre-network refusals, gateway authentication, Sync, management and recovery.
Its default state source requires an independent anchor; a separate explicitly
trusted unanchored source cannot detect full-file restoration. No production anchor
or App/MCP selector is supplied. Protected OS storage, provisioning integration
and secure first commissioning remain open.
Extended cEMI uses one NPDU length octet and the control-field frame selector;
81 checks cover published vectors, strict lengths and the shared raw-frame API.

The combined connection implementation has 29 focused state/lifetime checks and
19 native dialog checks per German/English run. Eleven additional tunnel checks
cover drained receive/send/connect operations, remote disconnect, heartbeat failure,
throwing observers, concurrent/idempotent close/dispose and actual UDP endpoint reuse.
Native dialogs use the shared Workbench chrome. These software checks do not replace
broader hardware, platform or accessibility acceptance.

General hardware acceptance remains open. Since 2026-10-29, the user has explicitly
released one device in the existing installation for targeted programming/write tests.
This does not authorize other targets or replace broader hardware acceptance. See the
[targeted test plan](../plans/06-commissioning.md).
