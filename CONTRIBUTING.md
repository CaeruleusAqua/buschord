# Contributing to BusChord

Thanks for helping. BusChord is an independent, from-scratch KNX engineering tool:
.NET 9 core libraries, an Avalonia desktop app, and a headless MCP server. Keep
changes focused, keep the diff readable, and treat the bus as production hardware.

## Building

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).
Nothing else: all NuGet packages restore with the SDK.

```powershell
dotnet build
dotnet run --project src/Knx.App          # desktop app
dotnet run --project src/Knx.Mcp          # MCP server on stdio
```

The solution file is `ets6.sln`. Windows is the primary development platform;
the core libraries are plain .NET and should stay portable.

## Tests

Tests are dependency-free console projects: they print their checks and exit
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

Other console suites: `Knx.Address.Tests`, `Knx.Buildings.Tests`,
`Knx.Catalog.Tests`, `Knx.CatalogUi.Tests`, `Knx.Diagnostics.Tests`,
`Knx.Encryption.Tests`, `Knx.Icon.Tests`, `Knx.Monitor.Tests`, `Knx.Owl.Tests`,
`Knx.ParameterUi.Tests`, `Knx.Search.Tests`, `Knx.SmartLink.Tests`, and
`src/Knx.App.Tests` for the UI shell. Smoke checks:

```powershell
dotnet run --project src/Knx.App -- --smoke
dotnet run --project src/Knx.Mcp -- --selftest
```

`tests/Knx.Catalog.Tests` needs real `.knxprod` files; point it at a local folder
with `KNX_CATALOG_SAMPLES=<folder>` or skip it.

Expectations:

- New behaviour gets a check in the matching console test project.
- Bug fixes get a check that fails before the fix.
- Tests never talk to a live bus, never write to a device, and never need
  manufacturer files checked into the repository.

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
