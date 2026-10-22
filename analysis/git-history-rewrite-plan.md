# Git history rewrite — plan and post-rewrite record

Status: **executed and force-pushed** on 2026-10-22 (see §4); this document is the
plan that was prepared for SEC-10 / release hygiene plus the verification record
after execution. One published ref (`refs/pull/1/head`) still exposes the removed
blobs; §6 lists the remaining actions.

- Task: remove third-party KNX master/manufacturer data and the GPL-2.0 test
  fixture from all history, replace the redacted identifiers that were still in
  history, and map the personal commit identity (`analysis/security-final.md`
  SEC-10, `analysis/release-hygiene.md`).
- Branch/worktree: `agents/release/history-plan` in
  `C:\projects\ai_work\agent-wt\history-plan`, based on `fd0729d` (verified).
- Mode: read-only investigation for the plan; the rewrite itself was executed by
  a separate release session (record in §4). No secret or credential **values**
  are written in this document; the replace-text and mailmap inputs must stay
  outside the repository. Any further history-affecting execution requires
  **explicit user approval** and a coordinated force-push window (§10).

## 1. Blobs and paths to remove or replace

| Path (history) | Blob | Size | Why | Introduced by | Gone from HEAD since |
|---|---|---|---|---|---|
| `tests/Knx.Core.Tests/knx_master.xml` | `ab9a3fb85f6cb883585ab2df0c3b0d566b51ab86` | 1,082,494 B | KNX Association master data (schema `/21`) redistributed in an Apache-2.0 repo, against the `.gitignore` policy | `644857a` (pre-public line), `2904460` (public root) | `171ebca` (development), `79f5d73` (pre-public line) |
| `tests/Knx.Encryption.Tests/fixtures/testprojekt-ets6.knxproj` | `6606a56b40f3f1d54fbe9782d4e8ed422e112c78` | 394,270 B | GPL-2.0 test fixture from `XKNX/xknxproject` (`test/resources/testprojekt-ets6.knxproj`, downloaded 2026-10-18, SHA-256 `DCB193…5FB4E`), incompatible with Apache-2.0 redistribution as shipped; contains manufacturer application programs and credentials | `7f2ec4e` / `1629165` (dev/pre-public), `2904460` (public root) | `171ebca` (development), `79f5d73` (pre-public line) |

Fixture contents (inspected read-only, values not printed):

- `knx_master.xml` (998,958 B uncompressed) and `.signature` files for
  `M-0008`, `M-0048`, `M-0083`, `P-04BF`.
- Manufacturer application programs: `M-0008` Gira Tastsensor 4 Komfort 4fach,
  `M-0048` B.E.G. FIX2 Series DM 4 T, `M-0083` IP Router Secure.
- Inner `P-04BF.zip` (WinZip AES-256, project password `test`, the published
  upstream one) with `0.xml` / `project.xml` (schema `/21`). Credential-bearing
  attributes present (names only): `@BCUKey`, `@IPRoutingBackboneKey`,
  `Security/@DeviceManagementPassword`, `@DeviceManagementPasswordHash`,
  `@ToolKey`, and five `BusInterface/@Password` + `@PasswordHash` pairs. The ZIP
  password is `Base64(PBKDF2("test"))`; that vector is already published in the
  test code (`Knx.Encryption.Tests/Program.cs`).

Replacement in HEAD (stays, was **not** touched by the rewrite):

- Synthetic master data at `tests/Knx.Core.Tests/fixtures/knx_master.xml`
  (blob `d0bfed91…`, 43 lines) and an in-memory `/21` project generator in
  `Knx.Encryption.Tests/Program.cs`; no binary fixture is tracked any more.

Text identifiers still present in history (replaced in HEAD by `072abb5` /
`b32860d`, and the hygiene report itself by `70e8c80`), redacted here:

- private LAN IPs (router, workstation), router MAC, router serial;
- absolute user paths (`C:\Users\<user>\…`) and workspace paths
  (`C:\projects\<ws>\…`), private project name;
- affected files: `analysis/router-discovery.md`,
  `analysis/parity-implementation.md`, `analysis/parity-dynamic.md`,
  `analysis/parity-parameters.md`, `analysis/parity-smartlinks.md`,
  `analysis/project-encryption.md`, the three `analysis/catalog-sources-*.md`,
  `docs/manual.md`, `mcp.md`, `tests/Knx.Bus.Tests/Program.cs`,
  `tests/Knx.Parameter.Tests/Program.cs`, `analysis/release-hygiene.md`.
- The exact old values are visible in the diffs of `072abb5`, `b32860d` and
  `70e8c80`; they must be extracted into a replace-text file outside the repo
  and must never be committed.

Commit metadata: the personal identity `<personal-name> <personal-mail>` was
author of 99 and committer of 106 commits; a mailmap entry maps it to the public
GitHub identity.

Scanned and clean (all refs, before and after the rewrite): no `.knxprod`,
`.vd4`, `.vd5`, `*.user`, no tokens (`ghp_`/`github_pat_`/`AKIA…` matches are
only the literal pattern names inside `analysis/release-hygiene.md`), no private
keys, no `.env`, no connection strings; the largest non-brand blobs were the two
fixtures above.

## 2. Blast radius (measured before the rewrite)

- Local repository: 170 refs, two product lineages (pre-public root `644857a`,
  public root `2904460`/`6562854`) plus the CLA branch (`3cc93a0`); ~659 commits
  in all refs; 429 commits had the old `knx_master.xml` in their tree, 310 the
  `knxproj`; 163 of 165 branches were on the old history; tag
  `pre-publish-backup` → `dfddb3f` (old history); one local clone plus 99
  worktree checkouts that still had the fixture on disk (plus copies under
  `bin\…\fixtures\`).
- GitHub (`CaeruleusAqua/buschord`, public since 2026-10-18, default branch
  `development`): refs `main`, `development`, `cla-signatures` and
  `refs/pull/1/head`; **both blobs were retrievable through the public API by
  SHA (HTTP 200)**; 0 forks, 0 stars, 0 watchers, 0 subscribers; clone traffic is
  owner-only (traffic API needs auth); Software Heritage has no origin for the
  repo; 60 Actions runs, 0 artifacts; PR #1 (CLA smoke test) closed with its head
  ref retained.
- Object store: 3,486 loose objects (~13.6 MiB) + one 7.04 MiB pack.
- Conclusion: anyone who cloned/fetched before the force-push has the removed
  blobs; the fixture data and the LAN identifiers must be treated as disclosed.

## 3. Reference execution plan (as prepared; executed 2026-10-22)

1. Freeze the repo (no pushes/commits during the rewrite) and announce the
   window; close/merge in-flight PRs.
2. Back up: `git clone --mirror` of the local repository and of GitHub
   (including `refs/pull/*` via `+refs/pull/*:refs/pull/*`), keep them outside
   the repo, verify with `git fsck`, and store hashes.
3. Extract the old identifier values from `072abb5` / `b32860d` / `70e8c80`
   into `<scratch>\replace-text.txt` and the identity into `<scratch>\mailmap.txt`
   (both outside any repository).
4. Rewrite on the mirror (git-filter-repo, Python 3; git 2.43 on this machine):

   ```powershell
   git clone --no-local --mirror C:\projects\ai_work\ets6-rebuild buschord-rewrite.git
   cd buschord-rewrite.git
   git filter-repo --force --replace-refs delete-no-add `
     --invert-paths `
     --path tests/Knx.Core.Tests/knx_master.xml `
     --path tests/Knx.Encryption.Tests/fixtures/testprojekt-ets6.knxproj `
     --replace-text <scratch>\replace-text.txt `
     --mailmap <scratch>\mailmap.txt
   ```

   `--invert-paths` deletes both paths from every commit; `--replace-text`
   rewrites the identifier values; `--mailmap` maps the personal identity.
   `--replace-refs delete-no-add` keeps the result free of replace refs so later
   scans are honest. Commits whose tree/parents are unchanged keep their SHAs.
   Trade-off of path deletion: the removed fixture is gone from old commits, so
   those commits can no longer run their encryption tests — acceptable, HEAD
   generates the fixture in memory.
5. Verify on the mirror before pushing (see §5) — a fresh clone of the mirror
   must contain none of the old blobs or paths.
6. Push only the three public branches, with a lease on the known remote tips;
   never `--mirror` (that would publish the 165 local agent branches and the old
   tag):

   ```powershell
   git remote add github ssh://git@github.com/CaeruleusAqua/buschord.git
   git push --force-with-lease=refs/heads/development:<old-dev-sha> github development
   git push --force-with-lease=refs/heads/main:<old-main-sha> github main
   git push github cla-signatures     # unchanged content, no-op
   ```

7. Tag/release handling: the remote had no tags or releases; the local tag
   `pre-publish-backup` (old history) must stay local or be deleted — pushing it
   would re-publish the blobs. Rewritten tags (had any existed) would need
   `--tag-rename` or a fresh tag after the window.
8. Rollback: the mirror backup of the state that was pushed (see §4) is the
   rollback point for a bad force-push; the old history exists only in the local
   clone and backups, and a rollback to it would re-expose the blobs.

## 4. What was executed

- 2026-10-22, 06:14–06:19 (+02:00), by `CaeruleusAqua` with git-filter-repo:
  both fixture paths removed from all 219 commits of the public lineage.
- Clean branches were pushed first (`development-rewritten` `caabd4b`,
  `main-rewritten` `49feddd`), then `development` and `main` were force-pushed
  (branch protection temporarily lifted and restored) and the clean branches
  deleted. Current remote refs (fresh `git ls-remote`):

  | Ref | SHA | Commits |
  |---|---|---|
  | `refs/heads/development` | `32d014d` (rewrite tip `7962a1a` + one doc commit) | 219 |
  | `refs/heads/main` | `49feddd` | 3 |
  | `refs/heads/cla-signatures` | `59a5c6c` | 2 |
  | `refs/pull/1/head` | `2741036` (old history, **not** rewritten) | 11 |

- Artifacts:
  - `C:\projects\ai_work\buschord-history-backup-20261022-061415.git` — mirror
    of the **rewritten** refs (219 commits, 4 refs, no old blobs); rollback
    point for the force-push, **not** a pre-rewrite archive.
  - `C:\Users\julia\AppData\Local\Temp\opencode\buschord-prehistory.bundle` —
    bundle from 2026-10-18 (pre-public lineage, tag `pre-publish-backup`); it
    still contains both fixtures. Move it out of `%TEMP%` if it is kept, and
    treat it as sensitive.
- Local repository after the rewrite: `development` = `7962a1a` (rewritten),
  but `main` is still the old root `2904460`; 163 branches, the old tag and all
  old objects are still present locally.

## 5. Verification (post-rewrite, measured 2026-10-22)

| Check | Result |
|---|---|
| `git rev-list --objects --all` for `ab9a3fb…` / `6606a56…` on `development`, `main`, `cla-signatures` | 0 hits |
| `git log --all -- tests/Knx.Core.Tests/knx_master.xml tests/Knx.Encryption.Tests/fixtures/testprojekt-ets6.knxproj` on the rewritten refs | empty |
| Identifier scan (old values from the sanitize diffs, checked with `git log -S` against the rewritten refs) | 0 hits for the real IPs, MACs and serial; remaining matches are values that already exist in HEAD (workspace root path, user profile paths, broadcast/placeholder MACs, event/CLI names) — no real identifier survived |
| Author/committer identities in the rewritten history | all `CaeruleusAqua <CaeruleusAqua@users.noreply.github.com>`, except `github-actions[bot]` |
| Fresh `git clone --mirror` from GitHub | 7.54 MiB, 232 commits incl. the PR ref; development 219 / main 3 / cla-signatures 2 |
| Fresh clone blob scan (`--all`, includes `refs/pull/1/head`) | **2 hits — both blobs still present via the PR ref** |
| GitHub API `GET /git/blobs/ab9a3fb…` and `…/6606a56…` | still HTTP 200 (unreachable-object cache not purged) |
| CI | `CI` and `CodeQL` runs for `7962a1a` completed successfully; runs for `32d014d` were in progress at the time of writing |
| Remote tags/releases | none |
| Forks/watchers | 0 |

So the three published branches are clean, but **GitHub still publishes the old
history through `refs/pull/1/head`** (`2741036` contains the whole old public
lineage `2904460 → c48e954`, including both fixtures) and still serves the blob
objects by SHA. Until that is purged, a fresh clone or a targeted
`git fetch origin +refs/pull/1/head:…` still yields the fixture.

## 6. Remaining actions

1. GitHub purge (needs the repo owner; no code execution):
   - open a GitHub Support request: delete the retained ref for the closed
     PR #1 (`refs/pull/1/head`) and garbage-collect unreachable objects /
     cached views for `CaeruleusAqua/buschord`;
   - optionally delete the 60 old Actions runs whose logs reference old SHAs;
   - re-run the acceptance checks (§7) until the blob API returns 404 and a
     fresh mirror clone has 0 hits.
2. Local cleanup (no network effect, but the local repo and the 99 worktrees
   still hold the data):
   - refresh/`git reset --hard` local `main` to `49feddd`; decide the fate of the
     163 old branches and the `pre-publish-backup` tag;
   - delete the fixture files from worktrees and `bin\…\fixtures\` outputs, or
     remove/refresh the worktrees;
   - expire reflogs and `git gc --prune=now` in the old clone, or re-clone from
     GitHub and keep the backups offline only.
3. Rotation checklist — see §8.
4. Contributor notice and rebase instructions — see §9.

## 7. Acceptance checks after the GitHub purge

```powershell
git ls-remote origin                                  # only development, main, cla-signatures
git clone --mirror https://github.com/CaeruleusAqua/buschord.git check.git
git -C check.git rev-list --objects --all | Select-String "ab9a3fb|6606a56"   # must be empty
git -C check.git log --all --oneline -- tests/Knx.Core.Tests/knx_master.xml tests/Knx.Encryption.Tests/fixtures/testprojekt-ets6.knxproj  # must be empty
curl.exe -s -o NUL -w "%{http_code}" https://api.github.com/repos/CaeruleusAqua/buschord/git/blobs/ab9a3fb85f6cb883585ab2df0c3b0d566b51ab86   # expect 404
```

Plus: CI green on `development`, fresh clone size back to ~7 MiB or below, and
the contributor notice sent.

## 8. Credential rotation checklist

| Value (from the removed fixture) | Rotate? |
|---|---|
| Project password `test`, `@BCUKey`, `@IPRoutingBackboneKey`, `@DeviceManagementPassword(+Hash)`, `@ToolKey`, 5× `@BusInterface/@Password(+Hash)` | Values come from the public GPL-2.0 upstream sample. Rotate/rekey only where one of them was ever reused on real hardware or a real project (change project password, rekey devices, regenerate tool keys). No evidence of reuse was found. |
| Private LAN identifiers (router/workstation IP, router MAC, serial) | Not rotatable as such; treat as disclosed. Change the router IP/reservation only if that matters to you. |
| GitHub token mentioned in chat during the release session | Rotate it. |
| Author e-mail `<personal-mail>` | Already public in the old history; the rewritten history maps it. Retire/forward only if desired. |
| Backups (`buschord-history-backup-…git`, `buschord-prehistory.bundle`, old local clone) | By design still contain the old data; keep offline and access-restricted, never push. |

## 9. Contributor notice (short)

> The public history of `buschord` was rewritten on 2026-10-22 to remove
> third-party KNX fixture data that could not be redistributed. All commit SHAs
> changed. If you have a clone, please re-clone or run
> `git fetch origin && git rebase --onto origin/development <your-old-base>`
> (or `git reset --hard origin/<branch>`) for each branch you keep. Old SHAs are
> no longer valid; do not push branches based on the old history.

German variant:

> Die öffentliche Historie von `buschord` wurde am 22.10.2026 neu geschrieben,
> um nicht weiterverteilbare KNX-Fixture-Daten zu entfernen. Alle Commit-SHAs
> haben sich geändert. Bitte neu klonen oder
> `git fetch origin && git rebase --onto origin/development <alter-base>`
> ausführen. Alte SHAs sind ungültig; nicht auf der alten Historie aufbauen.

## 10. Open decisions

- Request the GitHub Support purge (PR ref + unreachable objects), or accept the
  residual exposure of `refs/pull/1/head`?
- Keep or delete the local `pre-publish-backup` tag and the 163 old branches?
- Move the prehistory bundle out of `%TEMP%` (or delete it)?
- Residual `C:\projects\ai_work\…` and `C:\Users\julia\…` paths are still in
  HEAD in ~15–21 analysis files (pre-existing, not part of this rewrite) — clean
  up separately?
- Any further history change requires **explicit user approval** and a
  coordinated force-push window with a push freeze (the repo was actively being
  pushed to while this record was written).
