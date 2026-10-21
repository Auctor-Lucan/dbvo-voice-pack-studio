# DBVO Voice Pack Studio

A Windows tool that takes Skyrim plugins all the way to a released Dragonborn Voice Over (DBVO) voice pack.

- **Phase 1 – Mods:** parse .esp/.esm/.esl files in isolation (no xEdit, no load order), apply the
  IdrinthDBVOTextExport rules, review and edit player lines, and write `DBVO Fix - <plugin>.esp`
  (ESL-flagged, override-only) plus topic files. Shared by every voice pack.
- **Phase 2 – DBVO Patches:** per voice pack, generate lines with ElevenLabs (v4 audio tags, pronunciation
  dictionaries, concurrency matched to your plan), trim the 15 ms tail artefact with a 10 ms fade, build LIP
  files with FaceFXWrapper, keep correctly named takes, and convert everything to .fuz.
- **Phase 3 – FOMOD:** build or import FOMOD installers (planned).

Naming: Legacy (DBVO 1.0, readable names) or DBVO 2.0 (xxHash64), matching DBVO Voice Pack Maker 2.7.0.

## Layout

| Path | What |
| --- | --- |
| `src/DbvoStudio.Core` | Plugin reader/writer, script rules, naming, audio, ElevenLabs client, project model |
| `src/DbvoStudio.App` | Windows app (WPF host + WebView2 UI) |
| `tests/DbvoStudio.Core.Tests` | Unit tests (synthetic plugins; no third-party mod files) |

## Build

Requires the .NET 10 SDK.

```
dotnet test tests/DbvoStudio.Core.Tests
```

## Credits

NPC voice types, quoted dialogue and book authors in `src/DbvoStudio.Core/Reference/uesp-skyrim.json.gz` come from
**[UESP — The Unofficial Elder Scrolls Pages](https://en.uesp.net/)**, collected by Auctor Lucan from 17,519 UESP Skyrim
pages. All credit for this information goes to the UESP editors and community. UESP text is licensed under
[CC BY-SA 2.5](https://creativecommons.org/licenses/by-sa/2.5/); the derived data file is shared under the same license.
See [`src/DbvoStudio.Core/Reference/README.md`](src/DbvoStudio.Core/Reference/README.md).
