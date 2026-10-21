# Clean-code review — core libraries

Base `55ff6e1` (branch `agents/clean-review/core`, worktree `agent-wt/clean-review-core`, clean), fixed point `12fc904`. Reviewed `git diff 12fc904...HEAD -- src/Knx.Core src/Knx.Bus` plus surrounding code; CONTRIBUTING.md. `tests/Knx.Workbench.Tests` passes.

## P1 — bug / data loss

None found; backup containment, hash-before-replace and atomic replace are sound.

## P2 — likely maintenance bug

### 1. Connector facts never reach the configuration signature

`src/Knx.Core/Workbench/WorkbenchProject.cs:231-235`

```csharp
return new { o.Id, o.ComObjectId, o.Number, m?.Dpt, m?.Bits, m?.Flags, m?.Active,
    Links = ... Sending = m?.SendingGroupId == id }).ToArray() };
```

`ConnectorMetadata` (`Acknowledge`, `Priority`, `Slot`) is not hashed, so `SetConnector` leaves `ConfigurationSignature` unchanged and `DeviceProgrammingState.NeedsProgramming` still reports the device loaded after a bus-relevant edit. `DeviceProgrammingReport.Check` defaults to this signature (`DeviceProgrammingState.cs:247`), while the app session uses another (`src/Knx.App/Workbench/Sessions/ConfigurationSnapshot.cs:17`), so the hashes are not comparable.

Fix: one signature in `Knx.Core`, including the effective connector per link; remove the default so callers pass the producing provider. Test missing: connector-only edit changes the signature; both producers agree.

### 2. Group-range bounds have no defined reading

`src/Knx.Core/ProjectCheck.cs:266-273`, `src/Knx.Core/AddressStrategy.cs:68-72`, `src/Knx.Core/Wizards.cs:145-153`

```csharp
_ => address.Sub,
return address.Raw >= range.RangeStart && address.Raw <= range.RangeEnd
    || relative >= range.RangeStart && relative <= range.RangeEnd;
```

`Wizards.EnsureRange` stores main/middle numbers as bounds (`RangeStart = address.Middle`); `WorkbenchJson` stores absolute raw values. For middle range `2..2` and address `1/2/5` both readings fail, producing a false `P033`; `TryNextGroup` reads the offset as raw and returns `0/0/3` for a main range `3..3`.

Fix: one representation (absolute raw), normalized on import/read, compared raw only; otherwise compare raw, main, middle and sub explicitly. Test missing: check and allocate over `ImportGroupAddresses` output.

### 3. Backup identity is the path, not the stored project GUID

`src/Knx.Core/ProjectBackup.cs:393-394`, `:97-98`

```csharp
return "path:" + (OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized);
...
.Where(m => identity is null || m.Identity == identity)
```

The manifest stores `ProjectGuid`, but `List(projectPath)` matches only the old path. Renaming or moving a project hides every backup of it in the UI although the files remain.

Fix: fall back to `ProjectGuid`/`ProjectId` when the path identity finds nothing, or key identity on the GUID. Test missing: backup, rename source, `List(newPath)` still finds it.

### 4. One catalogue status, two severities

`src/Knx.Core/ProjectCheck.cs:363-368` vs `src/Knx.Core/Catalog/CatalogModel.cs:97`

```csharp
if (program.DownloadInfoIncomplete)
    findings.Add(new(CheckSeverity.Warning, "P071", ...
public bool IsEditable => !Broken && !DownloadInfoIncomplete && IsStaticallyEditable;
```

`CatalogBrowser` blocks edit/download for incomplete download information, but the project check only warns. Two independent classifications already disagree about the same fact.

Fix: classify once (map `IsEditable`/block reason to severity) and use it in both places. Test missing: check severity matches the block reason for broken/incomplete programs.

## P3 — cleanup

### 5. Orphaned connector entries are never detected or removed

`src/Knx.Core/Workbench/Contracts.cs:84-102`, `src/Knx.Core/ProjectCheck.cs:328-341`, `src/Knx.Core/Workbench/WorkbenchProject.cs:175`

`Validate` checks connector keys are non-blank but not that the group is still linked; `P052` checks only `Objects` keys; `RemoveLink` returns before cleaning when the link is gone. A stale connector survives save/load forever and `Connector()` never surfaces it.

Fix: reject/prune connectors whose group is not linked; add that to `P052`; clean before the early return. Test missing: connector for an unlinked group.

### 6. `Serialize` writes files its own `Parse` rejects

`src/Knx.Core/Workbench/DeviceProgrammingState.cs:194-196` vs `:224-225`

```csharp
var ordered = states.OrderBy(s => s.DeviceId, StringComparer.Ordinal).ToArray();
```

Duplicate device ids are accepted on write, rejected on read. Fix: `TryAdd` and throw on duplicates. Test missing: serializing duplicates fails.

### 7. DPT main-type parsing is duplicated and divergent

`src/Knx.Bus/Dpt.cs:32-34`, `src/Knx.Core/Workbench/WorkbenchProject.cs:82-89`, `src/Knx.Core/Wizards.cs:172-178`

`TryBitSize` accepts `DPST-9-1` and `9.001` but rejects `DPT-9-1`, which `MainType` accepts; `DPST-1` (no minor) is accepted by `MainType` but rejected by `TryBitSize`. Three parsers will drift further. Fix: one main-type parser reused by bit size and link validation. Test missing: `TryBitSize("DPT-1-1")`.

### 8. Dead download gate

`src/Knx.Bus/KnxBus.cs:13,70`

```csharp
private static readonly bool SafeDownloadImplemented = false;
if (!SafeDownloadImplemented) throw new NotSupportedException(...);
```

The rest of `DownloadAsync` is unreachable and the flag cannot be flipped without editing source. Fix: throw directly and delete the unreachable transport body, keeping the reason comment. No test needed.

### 9. Diagnostic code `P012` changed meaning

`src/Knx.Core/ProjectCheck.cs:134,145`

Base `P012` warned about multi-segment lines; the diff reuses `P012` for duplicate segment addresses and moves the old finding to `P014`. Reports keyed by code silently change meaning. Fix: keep `P012` for multi-segment and allocate a fresh code for duplicates. Test missing: assertion that code meanings stay stable.
