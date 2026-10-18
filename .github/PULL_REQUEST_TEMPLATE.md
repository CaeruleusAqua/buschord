## Summary

<!-- What changes, and why. One or two sentences. -->

## Type

- [ ] Bug fix
- [ ] New feature
- [ ] Refactor / internal cleanup
- [ ] Documentation
- [ ] Build / CI
- [ ] Other:

## Linked issue

<!-- Fixes #123, or a short note why there is none. -->

## Tests run

Paste the exit status or a short line per project. `dotnet build` is mandatory;
run the suites that cover your change.

- [ ] `dotnet build`
- [ ] `dotnet run --project tests/Knx.Core.Tests`
- [ ] `dotnet run --project tests/Knx.Bus.Tests`
- [ ] `dotnet run --project tests/Knx.Archive.Tests`
- [ ] `dotnet run --project tests/Knx.Parameter.Tests`
- [ ] `dotnet run --project tests/Knx.Linking.Tests`
- [ ] `dotnet run --project tests/Knx.Mcp.Tests`
- [ ] Other affected suite: <!-- e.g. Knx.Catalog.Tests, Knx.Search.Tests, src/Knx.App.Tests -->
- [ ] Not run (docs only):

## Risk and safety

- [ ] No test or sample writes to a live bus or downloads a device.
- [ ] No manufacturer catalogue binaries (`.knxprod`) or customer `.knxproj`
      files are added.
- [ ] No secrets, tokens, keyrings, or personal installation data are added.
- [ ] Behaviour changes that can write to a bus, download a device, or handle
      keys are described above, with the safety consequence spelled out.

## UI changes

<!-- Screenshots or a short screen recording, light and dark theme. Delete if not applicable. -->

## Notes for reviewers

<!-- Compatibility impact, follow-up work, deliberate simplifications. -->
