# Contributing to BusChord

Thanks for helping. BusChord is an independent KNX engineering tool:
.NET 10 core libraries, an Avalonia desktop app, and a headless MCP server. Keep
changes focused, keep the diff readable, and treat the bus as production hardware.

## Building

You need a serviced [.NET 10 LTS SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
Nothing else: all NuGet packages restore with the SDK.
`global.json` selects SDK 10.0.403 or a newer stable .NET 10 feature band.
`Directory.Build.props` sets `net10.0` for every application, library and check.
CI and CodeQL install the latest serviced .NET 10 SDK. The migration and local
validation are recorded in [the product status](docs/status.md#validation).
With a side-by-side SDK outside `PATH`, use its full executable path, for example
`& "$env:USERPROFILE\.dotnet\dotnet.exe" build BusChord.sln -c Release`.

```powershell
dotnet build
dotnet run --project src/Knx.App          # desktop app
dotnet run --project src/Knx.Mcp          # MCP server on stdio
```

The solution file is `BusChord.sln`. Windows is the primary development platform;
the core libraries are plain .NET and should stay portable.

## Tests

Tests are console projects with their own checks: they print each check and exit
with code 0 on success, non-zero on failure. There is no test framework and no
`dotnet test` step. Run at least the projects your change touches, and the core
ones before opening a pull request:

```powershell
dotnet run --project tests/Knx.Core.Tests
dotnet run --project tests/Knx.Bus.Tests
dotnet run --project tests/Knx.Archive.Tests
dotnet run --project tests/Knx.Parameter.Tests
dotnet run --project tests/Knx.Linking.Tests
dotnet run --project tests/Knx.Mcp.Tests
```

These suites run on Linux and Windows in CI:

```powershell
dotnet run --project tests/Knx.Owl.Tests
dotnet run --project tests/Knx.Catalog.Tests
dotnet run --project tests/Knx.Address.Tests
dotnet run --project tests/Knx.Buildings.Tests
dotnet run --project tests/Knx.Diagnostics.Tests
dotnet run --project tests/Knx.Encryption.Tests
dotnet run --project tests/Knx.Icon.Tests        # needs a display; CI skips it on Linux
dotnet run --project tests/Knx.Monitor.Tests
dotnet run --project tests/Knx.Search.Tests
dotnet run --project tests/Knx.SmartLink.Tests
dotnet run --project tests/Knx.ParameterUi.Tests
dotnet run --project tests/Knx.CatalogUi.Tests
dotnet run --project tests/Knx.Workbench.Tests
dotnet run --project tests/Knx.WorkbenchPerformance.Tests
dotnet run --project tests/Knx.WorkbenchSessions.Tests
dotnet run --project tests/Knx.WorkbenchTheme.Tests
dotnet run --project tests/Knx.WorkbenchUiState.Tests
dotnet run --project tests/Knx.Localization.Tests
dotnet run --project src/Knx.App.Tests
dotnet run --project src/Knx.Mcp -- --selftest
```

`dotnet run --project tests/Knx.ProjectCompare.Tests` covers the compare and
revision engine locally and in the CI matrix. Native-window and app
checks require a graphical Windows desktop:

```powershell
dotnet run --project src/Knx.App -- --smoke
dotnet run --project src/Knx.App -- --workbench-selftest --theme=light --lang=de
dotnet run --project src/Knx.App -- --workbench-selftest --theme=dark --lang=en
dotnet run --project src/Knx.App -- --screenshot-selftest
dotnet run --project tests/Knx.ParameterUi.Tests -- --native
dotnet run --project tests/Knx.Docking.Tests
dotnet run --project tests/Knx.Shell.Tests

dotnet run --project tests/Knx.WorkbenchUiState.Tests -- --dialogs

dotnet run --project tests/Knx.WorkbenchUiState.Tests -- --dialogs --de

dotnet run --project tests/Knx.WorkbenchUiState.Tests -- --projection-refresh
```

`Knx.Docking.Tests` exercises floating and redocking tool windows,
`Knx.Shell.Tests` covers project lifecycle and shutdown, the `Knx.Workbench*`
suites cover domain contracts, sessions, theme, UI state and performance, and
`Knx.Localization.Tests` checks de/en key parity, placeholders and hardcoded
German boundaries. `src/Knx.App.Tests` covers the report/export pipeline
(including the acceptance protocol and handover package).

The opt-in native measurement mode is separate from CI timing assertions:
`dotnet run --project tests/Knx.WorkbenchUiState.Tests -c Release -- --performance`.
It records 12,000 synthetic communication objects, a running simulated monitor,
layout/render progress and p50/p95/maximum in an external JSON file. See the
[measurement method and limitations](docs/status.md#validation);
it does not establish a universal latency budget or manual accessibility acceptance.

Use `--performance-budget` on the same project for the separate exact-reference
budget and `budget.json`; [reference conditions and thresholds](docs/status.md#native-ui-and-accessibility)
must match. Exit 0 requires valid fresh evidence and every timing/memory boundary
to pass; a measurement-only pass is insufficient. `--performance-budget-checks`
runs the pure policy checks without opening a native window and is also included
in the normal suite. Native timing acceptance remains open after the first
reference run's five p95 violations; retain failing reports when investigating.

`tests/Knx.Catalog.Tests` runs its synthetic checks without manufacturer files.
Set `KNX_CATALOG_SAMPLES=<folder>` to add optional local `.knxprod` checks;
manufacturer files must stay outside version control.

### Continuous integration

`.github/workflows/ci.yml` restores and builds `BusChord.sln`, then runs the suites
above on Ubuntu (`test-linux`) and Windows (`test-windows`). Docking, Shell,
`ParameterUi --native`, `--smoke`, the Workbench self-checks and
`--screenshot-selftest` run only on Windows. `cla.yml` enforces the CLA and
`codeql.yml` runs the security scan. A pull request is not ready while any of
these checks is red.
The aggregate `ci` check also fails if a required job is skipped or cancelled.
The hosted `main` and `development` branches require `ci`, `CLA Assistant` and
`CodeQL`, with each check bound to its GitHub app. Single-maintainer pull requests
do not require an additional approval; checks and resolved conversations still apply.
CI and CodeQL run on pull requests and pushes to `development` or `main`.
Feature branches use PR checks or a manual dispatch, avoiding duplicate push/PR runs.
Both workflows cancel superseded runs on the same ref; action versions are pinned
to upstream commit SHAs and Dependabot maintains their update pull requests.

Expectations:

- New behaviour gets a check in the matching console test project.
- Bug fixes get a check that fails before the fix.
- Tests never talk to a live bus, never write to a device, and never need
  manufacturer files checked into the repository.

`Knx.ParameterUi.Tests` includes the full fixed-root working-set and shared-context
table regressions. Keep their independent byte literals, raw-input freshness,
atomic failures and the existing subset/table/source-only contracts intact;
`Knx.Mcp.Tests` checks the corresponding explicit options and output limits.

## Branches and pull requests

| Branch | Purpose |
|---|---|
| `main` | Releases only. Protected; updated from `development` by the maintainer. |
| `development` | Integration and default branch. All pull requests target this. |
| `feature/<topic>` | New behaviour, e.g. `feature/dca-import`. |
| `fix/<topic>` | Bug fixes, e.g. `fix/tunnel-timeout`. |

Workflow:

1. Branch from an up-to-date `development`.
2. Keep the branch single-purpose; split unrelated changes.
3. Rebase or merge `development` in rather than opening a giant PR.
4. Fill in the pull-request template, including the tests you ran.
5. Never force-push `development` or `main`.

Commit messages: a short imperative subject line (72 characters or fewer)
followed by an optional body explaining the *why*. Reference an issue when one
exists. One logical change per commit; no "wip" or "fixes" commits in a PR.

```
Archive: reject nested ZIPs beyond the depth limit

Deeply nested archives exhausted the stack on crafted input.
Adds a depth guard and a regression check.
```

## What must not go into a pull request

- Manufacturer product databases (`.knxprod`), catalogue binaries, or
  manufacturer XML extracts. They are copyrighted by their vendors.
- Customer `.knxproj` projects or any real installation data.
- Secrets, tokens, keyrings, device keys, or personal data.

To test catalogue code locally, put the files outside version control:

```
sample-catalogs/            # gitignored
  <vendor>/<product>.knxprod
```

Then point the tools at that folder, for example
`dotnet run --project tests/Knx.Catalog.Tests` with
`KNX_CATALOG_SAMPLES=sample-catalogs`, or `src/Knx.Mcp -- --scan-catalogs=sample-catalogs`.
Keep the folder out of commits and out of screenshots.

## Code style

- Follow the surrounding code: file-scoped namespaces, nullable enabled,
  `_camelCase` private fields, async methods suffixed `Async`.
- BCL first. No new NuGet dependency without discussing it in an issue first;
  state what the BCL alternative would cost.
- No reflection or dynamic code where a typed API works.
- Public API changes in `Knx.Core` or `Knx.Bus` deserve a comment in the PR
  explaining the compatibility impact.
- User-visible text goes through the existing `Strings`/`WorkbenchStrings` keys
  and the supported locale dictionaries. Keep all eight UI languages complete.
  RTL is not supported yet; do not add hardcoded German outside the checked sources.
- Comments in English, and only where the code is not obvious.

## Reporting security issues

Do not open a public issue for a vulnerability. Follow
[SECURITY.md](SECURITY.md) and report privately through GitHub Security
Advisories.

## Conduct

Everyone participating is expected to follow the
[Code of Conduct](CODE_OF_CONDUCT.md).

## Licence of contributions and CLA

BusChord is distributed under the [Apache License 2.0](LICENSE). To keep the
project maintainable — including the option to change the outbound licence in a
future release — every contributor must accept the
[Contributor License Agreement](CLA.md) before a pull request can be merged.

Signing is a single comment on your pull request:

> I have read the CLA Document and I hereby sign the CLA

The CLA bot records your GitHub account name and the pull request number in the
`cla-signatures` branch. You keep the copyright in your contribution and grant
the project a broad, irrevocable licence that includes relicensing. Signing once
covers future contributions until the CLA version changes. If you contribute on
behalf of an employer, make sure you are authorised to do so.

Pull requests without a recorded signature stay blocked by the `CLA Assistant`
check. The maintainer account and bots are allowlisted and do not need to sign.
