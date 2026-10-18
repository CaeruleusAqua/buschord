# P3: manufacturer parameters and catalogue storage

## Evidence (static; no ETS execution)

- XML assembly: `Knx.Ets.Xml.ObjectModel.dll` 6.4.8718.0, SHA-256 `db6f21679d046eaef4909121a553af52fa377fa582552faca6ec0fed5f7f51e1`.
  Artifact record: `ev_b20c97f2b974626ec692b81ee480e556a3ae9003938b3cad6c9169fec10b3aba`.
- Focused member record: `ev_0af143c66a6bf3d17d5d2bb4c73d0cf0056ba9f5b350d57c7bfa4f8eedc031f4`.
  All eight selected names matched: `ParameterTypeNumber`, `ParameterTypeRestriction`,
  `ParameterTypeRestrictionEnumeration`, `ParameterTypeFloat`, `ParameterTypeText`,
  `Parameter`, `ParameterRef`, `ParameterInstanceRef` (namespace `Knx.Ets.Xml.ObjectModel`).
  Observed getters: Number has SizeInBit, Type, minInclusive/maxInclusive;
  Float has Encoding and double bounds; Text has SizeInBit and Pattern;
  Restriction has Enumeration/Base/SizeInBit; each Enumeration has **uint Value and string Text**.
  ParameterRef has Id/RefId/Text/Value/InitialValue/Access; ParameterInstanceRef has Id/RefId/Value/CustomizedText.
  The initial unfiltered inspection exceeded REA's metadata representation budget;
  focused selection succeeded. Tokens and signatures are build-local, not source-level behavior proof.
- UI assembly SHA-256 `54e28d224543e3c14d04dfd7359fc5552d7472959e695bb89aed1defbff440b0`.
  Inventory: `ev_22b1899d0bd61f82ed03f292a8434039e93383c335a0911577d35fcdf7d4902a`.
  Focused UI record: `ev_63ce5e565f1c84c0f414272d834e631ae540f27ecb603a7ec81d97818f917db2`.
  `EnumerationParameterItemViewModel` independently exposes uint Value and string Text;
  `ParameterViewModel` exposes ParameterInstance, DefaultValue, Value and VerifyAndPersistValueCommand.
  `TextParameterViewModel` exposes MaximumChars, IsZeroTerminated and Encoding.
  This establishes a richer text model than our conservative static validator, not its encoding rules.

## Implemented behavior

- One KSXML parser now serves both catalogue programs and direct XML callers. Synthetic attributes remain supported.
- Real child TypeNumber, TypeRestriction/Enumeration, TypeFloat, TypeText declarations retain sizes,
  encodings and numeric bounds. Unknown shapes are explicit, disabled rows rather than invented editors.
- ParameterRef rows use the reference Id, retain the source Parameter Id, resolve the type, and apply
  reference text/value/access overrides. Parameters without references remain available by their own Id.
  `Value` supplies the declared catalogue default; no value is manufactured when absent.
- Labels and storage values remain separate; catalogue translations are applied to enum/parameter/ref texts.
- ObjectSize `Bit`/`Byte`/`Bytes` becomes SizeInBit (also honors synthetic SizeInBit).
- Content SHA-256 filenames replace manufacturer/program-name filenames. Split archives retain lazy
  loading after import even when the source disappears. Same bytes from a renamed source reimport idempotently.
- Store Remove rejects outside-store paths and symlink files. Nested archives materialize lazy parts before
  cleaning their temporary archive, retain the outer source identity, and reject more than four wrappers.

## Shared API / integration contract

Existing Ksxml.Parse tuple shape and existing public members remain compatible. Additions:

- `ParameterRestriction`: `string? EnumerationText`, `Id`, `MinimumValue`, `MaximumValue`.
  Existing `EnumerationValue` is always the **persisted value**, never its label.
  Existing `Min`/`Max` remain double bounds; original bound strings permit exact 64-bit integer validation.
- `ParameterType`: `string? NumberType`, `UnsupportedReason`.
- `ParameterInstance`: `string? ParameterId`, `Unit`, `UnsupportedReason`.
  Persist edits keyed by **instance.Id**; for referenced manufacturer parameters this is the ParameterRef Id.
  `ParameterId` is the underlying static declaration, not a second independently editable value.
- `ParameterReference`: `string? Value`, `InitialValue` (ParameterInstanceId stores the static RefId).
- `CatalogApplicationProgram`: `List<ParameterReference> ParameterReferences`, `List<string> UnsupportedFeatures`.
- `Ksxml.ParseParameterReferences(XElement)`, `Ksxml.UnsupportedFeatures(XElement)`, `Ksxml.ParseObjectSize(string?)`.
- `ParameterValidation.TryValidate(ParameterInstance instance, string? value, out string? error)` and
  `TryValidate(ParameterType? type, string? value, out string? error)`: false gives an explanatory error.
  Instance overload respects read-only/unsupported state. Type overload validates storage values, not labels.

### Required App integration (outside P3 ownership)

- Keep enum choices as value/label pairs: display EnumerationText, save EnumerationValue.
- Prefer instance.Unit over type.Unit. Call ParameterValidation before accepting edits.
- Persist overrides in DeviceInstance.ParameterValues by instance.Id; apply to fresh parsed instances on open.
- Remove the App's InitialValue fallback (including DateTime.Today); opening must not generate defaults.
- Show a static-editor warning when program.UnsupportedFeatures is nonempty. These declarations do not
  describe the currently active parameter set or active communication objects.

## Limits

- Choose/When/Assign, module instances/arguments and calculations are reported, **not evaluated**.
  ModuleDef parameter rows are excluded rather than pretending to instantiate module-local storage.
  Other static rows, including union declarations, are not a complete download image or dynamic state model.
- No UI files, Model.cs, Knxproj.cs, Undo.cs or persistence wiring changed here.
- Validation covers integer/invariant finite float ranges, integer bit width, enum storage membership and
  a conservative UTF-8 byte-size bound for text. Text XML-schema patterns explicitly refuse edits rather
  than silently applying .NET regex semantics. Encoding/zero-termination/display scaling and DPT float
  representability are not fully reconstructed. No values are truncated or converted on import.
- Nested archives are loaded eagerly to release temporary storage. No new dependencies or real catalogue files committed.
- A store directory import is not supported (file/archive imports are); changing bytes changes identity,
  so old catalogue versions coexist rather than being silently replaced.

## Verification

Commands from the P3 worktree:

```powershell
dotnet build tests/Knx.Parameter.Tests
dotnet run --project tests/Knx.Parameter.Tests
dotnet run --project tests/Knx.Core.Tests
dotnet run --project tests/Knx.Catalog.Tests
```

- Build succeeds (existing Catalog nullable warning remains).
- New dependency-free console: **1,038 checks pass**, exercising child types, synthetic compatibility, defaults, reference
  identity/overrides, translated enum labels versus values, range/bit/invariant validation, exact large
  integer bounds, lazy store relocation, coexistence, idempotence, nested archives and deletion boundary.
  Real-file checks also confirm editable numeric, enum and text rows pass validation with their declared values.
- Read-only MDT corpus at `<project-path>\sample-catalogs\mdt`: **99 files, 273 programs,
  401,197 parameter/reference rows**; all applicable products resolve and all returned parameter types
  and communication object sizes resolve. Counts: 3,022 numeric, 18,008 enumeration, 513 text types.
  USB-interface hardware without an application is not treated as a missing program.
  Override sample location with KNX_CATALOG_SAMPLES; when absent the test explicitly skips the corpus.
- Core.Tests: **all checks pass**.
- Catalog.Tests: **one obsolete filename assertion fails** (`M-00FA_A-008A-25.knxprod` expected;
  new name is manufacturer plus content SHA-256). All other checks pass. Updating that test is outside
  the assigned write scope; integration must update the assertion to test content identity instead.
  With KNX_CATALOG_SAMPLES set: 99/99 catalogues, 351 products, 347 with applications; corpus checks pass.
