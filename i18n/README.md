# Translation catalogs

Source of truth for the language roadmap: the project documentation.

- `core/<lang>.json` - `Knx.Strings` keys (German/English reference stays in C#).
- `workbench/<lang>.json` - Workbench shell keys.
- `catalog/<lang>.json` - catalogue UI keys.
- `languages.json` - language codes, endonyms and status.

Each catalog is a flat JSON object. The German value in the C# map is the source;
English is the fallback reference. A missing overlay key falls back to English,
then German, then the key. Placeholders (`{0}`, `{1}`, ...) must match the German
source exactly.

## Status

`reference` (German source) -> `supported`; `planned` means no catalog yet.

`de`, `en`, `fr`, `it`, `es`, `nl`, `zh-CN` and `pl` are supported.
All supported catalogs are normal product content.

## Adding a language

1. Add the code and status to `languages.json`.
2. Add `<module>/<code>.json` for `core`, `workbench` and `catalog`.
3. Register the code in `Strings.Available` and the Settings selector.
4. Run `dotnet run --project tests/Knx.Localization.Tests`.
5. Run the Workbench selftest and screenshots in the new language.

No lookup code changes: `LanguageCatalog` already resolves any number of
overlays with the fallback chain.

## Commands

```powershell
dotnet run --project tests/Knx.Localization.Tests
dotnet run --project src/Knx.App -- --workbench-selftest --lang=fr --theme=light
dotnet run --project src/Knx.App -- --screenshot=out/fr.png --screen=main --lang=fr
```
