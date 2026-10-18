# P2 — safe project archives

## Focused static REA evidence (2026-10-18)

- `C:\Program Files (x86)\ETS6\Knx.Ets.ProjectArchive.dll`, SHA-256
  `43d2a1bb74d78e12a93c260ee28ff6facffe9f6b48d79b6b87bf136fe84bc51f`,
  MVID `a1537d0c-51f9-43ad-aa0f-82a3aa5c45dd`.
  REA member inspection: `ev_717c8478c2662ce04059319288f523c1c341c21337c1d1654815ed164b56ec83`.
  Member-reference view `ev_e7d653becddd7772e1a90718378ec6132f5a164bbf1c172563e3c1d4e6b79fbb`
  observes `Knx.Ets.Common.Zip.ZipWrapper` constructors (path/stream), `IZipWrapper.get_Entries`,
  `IZipEntry.get_FileName`, `IZipEntry.AsStream(string)`, `IZipEntry.get_UsesEncryption`,
  `EtsZipExtractor.Extract`, and `InvalidPasswordError`.
  This establishes archive/encryption abstractions, **not** their full implementation or nested filename rules.
  Coverage is partial (five invalid CustomAttribute parent rows); references cover 936 returned rows.
- `Knx.Ets.Xml.ObjectModel.dll`, SHA-256
  `db6f21679d046eaef4909121a553af52fa377fa582552faca6ec0fed5f7f51e1`:
  `ev_b8b92c3a2bfe2f2d76530f287995d9b8d58669abc71c62011f2c2a42c3886e76`
  / summary `ev_4254657493ca1d9603bf1c889477b15ad2a6dcb1215d7fbe5ec6120a21e541e0`.
  Managed member inspection unavailable: metadata exceeded REA's 64 MiB representation budget.
  **Device ParameterInstanceRefs/RefId/Value mapping was not verified.** No assemblies were executed,
  no passwords guessed, and no downloads or runtime instrumentation used.

## Contract and implementation choices

- `DeviceInstance.ParameterValues` is `Dictionary<string,string>` (public getter).
  Persistence uses explicit `urn:buschord:project:1` elements
  `ParameterInstanceRefs/ParameterInstanceRef` with unqualified `RefId` / `Value` attributes.
  This is a BusChord contract/inference, **not a claim of ETS parameter interoperability**.
  Native project-namespace parameter nodes are retained verbatim as opaque XML; not imported into overrides.
  The same extension stores communication-object model-only `ComObjectId` / `Number` attributes.
- Existing project/23 mappings stay compatible. Missing ID, comment, signature, installation type,
  and channel model state are now round-tripped. `Channels/ChannelInstance` is a synthetic/model mapping;
  its exact ETS layout remains unverified. Unknown native alternatives remain opaque and retained.
- App `LoadProject` / `SaveProject` / `SaveProjectAs`, MCP project open/save, and `UndoStack`
  all route through the same public Core signatures; no App/MCP changes are needed.
- Loaded archive bytes live on `Project`, independent of the source path. Undo snapshots share these
  immutable bytes and carry the full merged XML. SaveAs and restored snapshots retain arbitrary parts.
- Root `0.xml` and one unencrypted root `P-*.zip` containing `0.xml` are supported.
  The nested naming/layout support is a requested compatibility choice tested with independent synthetic fixtures,
  not binary-confirmed ETS export parity. Ambiguous root+nested/multiple projects are rejected.
- Save writes an exclusive temporary sibling, flushes it, reload-validates it, then uses `File.Replace`
  for an existing destination or `File.Move` for a new one. There is no delete-original fallback.
  Failure before replacement leaves the destination unchanged; temporary cleanup runs in `finally`.
- XML uses a three-way merge (original, modeled baseline, current model). Unknown elements, attributes,
  text, comments, and collection-container payloads survive ordinary edits and undo/redo. Stable IDs
  preserve payloads when objects relocate. Ambiguous identities/content are refused, not flattened.
  Deliberately deleting a modeled object deletes its owned opaque subtree too. XML lexical formatting,
  ZIP compressed representation, and signatures/checksums are not guaranteed/recomputed.

## Bounds and limits

- No extraction: ZIP names, including traversal-like names, are only opaque dictionary/archive keys.
- 128 MiB archive file; 4096 entries; 64 MiB expanded per entry; 256 MiB expanded per ZIP level;
  16 MiB project/application XML; XML depth 128; DTDs prohibited and resolver disabled.
- Central/local encrypted flags and AES entries are rejected with a supported-format error.
  ZIP64, split volumes, duplicate names, unsupported project namespaces, and multiple projects are refused.
- Supported XML schema remains `/23`; no encrypted projects, password handling, or real-export acceptance
  claim. The original phase-1 real ETS export acceptance item remains open.

## Checks

- `dotnet build tests/Knx.Archive.Tests`: successful (existing unrelated Catalog CS8620 warning on clean build).
- `dotnet run --project tests/Knx.Archive.Tests`: independent synthetic root/nested archives, arbitrary parts,
  source-independent SaveAs, in-place replacement, serialization/locked-destination failures, unsupported inputs,
  encryption, XML bounds/DTD/depth, opaque XML, parameters, IDs/comments/channels, moves, reordering, undo/redo.
  Result: `ALL 102 ARCHIVE CHECKS PASSED` (exit 0).
- `dotnet run --project tests/Knx.Core.Tests`: `ALLE CHECKS OK` (existing suite unchanged).
