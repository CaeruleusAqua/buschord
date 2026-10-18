# Release hygiene — public GitHub publication

Scope: tracked tree at base `dfddb3f` (branch fast-forwarded to `23e729e` for a green
build) and the complete history (`git log -p --all`, 87 commits). The encrypted ETS6
test fixture `tests/Knx.Encryption.Tests/fixtures/testprojekt-ets6.knxproj` was excluded:
it is a public, GPL-2.0, password-protected sample project.

## Found and changed

| Finding | Where | Action |
|---|---|---|
| Private LAN addresses (router, workstation) | `analysis/router-discovery.md`, `analysis/parity-implementation.md`, `docs/manual.md`, `mcp.md`, `tests/Knx.Bus.Tests/Program.cs` | Replaced with documentation range `192.0.2.10` / `192.0.2.12` |
| Device MAC (router) | same files, incl. the captured DESCRIPTION_RESPONSE hex in the test | replaced, also in the frame bytes and the assertion |
| Device serial (router) | `analysis/router-discovery.md`, test frame + assertion | replaced |
| Absolute user paths | `analysis/project-encryption.md`, `analysis/parity-smartlinks.md` | `<project-path>` |
| Workspace paths | catalogue-source and parity analysis docs, `tests/Knx.Parameter.Tests/Program.cs` default corpus | `<project-path>`; test default is now the relative `sample-catalogs\mdt` |
| Private project name | `analysis/project-encryption.md` | `project.knxproj` |

Protocol facts, frame layouts, test logic and names were not changed; the redacted
capture keeps its DIB structure and still validates parsing. `Knx.Bus.Tests` passes.

`.gitignore` now blocks manufacturer and vendor data: `*.knxprod`, `*.knxprod.zip`,
`*.vd4`, `*.vd5`, `sample-catalogs/`, `catalogs/`, `catalogue-library/`,
`artifacts/`, `*.user`, plus the existing `bin/`, `obj/` rules.

## Checked and clean

- No API tokens (`ghp_`, `github_pat_`, `AKIA…`), private keys, `.env` files,
  credentials in URLs, bearer headers, or internal Gitea/Taiga URLs anywhere in the
  tree or history.
- No personal email addresses in tracked files.
- `git ls-files` lists no catalogue or vendor binary; the only match is the source file
  `src/Knx.Core/Catalog/KnxProductCatalog.cs`. No `artifacts/`, `*.knxprod`, `*.vd4`,
  `*.vd5` or sample catalogue is tracked.
- Live bus probe remains opt-in (`--live`); no network access during checks.

## Remains (not fixed here)

- **Commit author identity in history.** The project owner's personal name and e-mail are
  the author/committer of most commits. This is the only real PII left in the repository.
- **History still contains the pre-redaction values**: router/workstation IPs, the MAC,
  the serial, and the absolute user paths, because they were committed earlier.
- The SHA-256 of the private project file remains in `analysis/project-encryption.md`
  as analysis evidence; a hash discloses no content.

## Recommendation

Do not publish this history as-is. Either rewrite history before the public release
(`git filter-repo --mailmap` for the author identity plus replacement of the old
identifiers, or publish a squashed snapshot as the initial public commit), or publish
with the author email accepted as intentional. If history is rewritten, force-push to a
new public remote only — never to the internal one.
