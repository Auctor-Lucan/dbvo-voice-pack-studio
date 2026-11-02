# DBVO Studio — handover to a new chat

Read this whole file before doing anything. It replaces the earlier chat's context.

## The project

- **DBVO Studio**: a Windows desktop app (.NET 10, WPF + WebView2; C# backend, plain JS front end) for making DBVO
  player voice packs for Skyrim SE: Phase 1 Import Mods (dialogue lines from plugins), Phase 2 DBVO Patching
  (TTS generation, mainly ElevenLabs; .fuz building), Phase 3 FOMOD Creation. Other Tools: WAV to LIP, NPC Revoice,
  Voiced Books, Player Grunts (PCHT/SPVS).
- Repo: `Auctor-Lucan/dbvo-voice-pack-studio`, branch `main` (clone to `/home/claude/dbvo-voice-pack-studio`).
- Current version: **11.0.8** (`src/DbvoStudio.App/DbvoStudio.App.csproj` `<Version>`). Delivered to the user on
  2 Nov 2026 with: the flat zip, `docs/release-notes/DBVO Studio changelog 10.0.0-11.0.8.txt`,
  `docs/release-notes/DBVO Studio 11.0.8 - Nexus post.txt` and the 11.0.8 guide PDF.
- The user (Auctor Lucan, Sweden) makes the DBVO voice packs **DBVO Vampira** and **Vampire DBVO Remastered**
  (pack ID FearTcbVamp) with this app, and publishes the app on Nexus Mods.

## Standing rules (from the user; follow every time)

- Replies: concise, answer first, no preamble or recaps, no headers/bullets unless the content is a list, no
  unrequested follow-up offers, uncertainty flagged in a clause.
- Never use the word "clean" (or variants) in the app, guide or release texts.
- **Every build gets a new version number.** Never give the user the same number for a different build unless they
  ask. Bump `<Version>` in the csproj for each build you hand over. Next one: 11.0.9.
- Build zips are **flat**: the contents of the build folder at the zip root.
- Deliver files with SendUserFile.
- **Bug hunting must be thorough:** for any change, check everything that rewrites the same data (refresh, rebuild,
  relink, import, save, background jobs, the frontend and backend copies of a project), not just the code in front
  of you. Before a release, run a sweep (independent review agents work well) and then a review of the fixes.
- Commits end with:
  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: <this chat's session link>
  ```
  A stop hook requires everything to be committed and pushed.
- Security / data:
  - Never bundle FaceFXWrapper, FonixData.cdf or xWMAEncode.exe.
  - Don't read the user's `C:\DBVO Studio\settings.json` (it holds API keys); skip settings.json in backup zips.
  - Don't commit the user's audio, reference files or mod plugins.
  - Never try again to read the ElevenLabs Firebase token.
  - Dragonborn ReVoiced (DBReV): its app terms forbid decompiling. Raynor (Raynor1511), its author, gave permission
    for the DBReV export and OmniVoice API support. Tone toward Raynor: cordial only.

## Build, test, release

- Build: `cd src && PATH=/opt/dotnet:$PATH dotnet build DbvoStudio.App -p:EnableWindowsTargeting=true`
- Tests: `cd tests/DbvoStudio.Core.Tests && PATH=/opt/dotnet:$PATH dotnet test` (304 pass at 11.0.8).
- JS syntax check: `node --experimental-default-type=module --check src/DbvoStudio.App/wwwroot/<file>.js`
- UI tests and guide screenshots: `tools/demo/README.md` (headless harness with the real backend and UI).
- Release (CI on GitHub Actions builds the Windows app):
  1. bump `<Version>`, commit, push
  2. `id=$(gh run list -L 1 --json databaseId -q '.[0].databaseId'); gh run watch $id --exit-status`
     (if the "Bundle ffmpeg (LGPL build)" step fails with a 404, it's upstream: `gh run rerun $id --failed`)
  3. `gh run download $id -D <dir>`; artifact folder `DBVO-Studio-win-x64`
  4. check `strings -el "DBVO Studio.dll" | grep <version>` and that no FaceFX/Fonix/xWMA file is in it
  5. `cd <dir>/DBVO-Studio-win-x64 && zip -qr9 <out>/DBVO-Studio-<version>-win-x64.zip . && unzip -tq <zip>` (~146 MB)
- Release texts live in `docs/release-notes/`: per-version changelogs (first line `Version X.Y.Z`, then `- ` items
  written for users), a combined "everything since 10.0.0" file, and Nexus posts in BBCode (same style as the
  existing ones: cyan size-5 headings, [list] items). Update the combined changelog and write a new post when asked.
- Guide: `src/DbvoStudio.App/wwwroot/help/guide.html` (+ `help/img/*.webp`); update it with every user-visible
  change. PDF: `tools/pdf/README.md`.

## The user's computer

The desktop app can link this chat to the user's PC (remote-devices tools: device_bash etc.). **Link only the
folders a task needs.** Every linked folder that no longer exists is recreated (empty) whenever device_bash runs, and
folders can't be unlinked from a chat (the old chat had four empty `D:\MO2\mods\…` folders keep coming back:
MPCHT Toolkit, Multi Voice Packs support for PC Head Tracking, Rename Tool, Smart Player Voice System).

Useful folders: `C:\DBVO Studio` (the app library: `patches/<id>/patch.json` in PascalCase, `mods/`, `voicepacks/`,
`fomods/<id>/fomod.json`, `references/`, `backups/`), `C:\DBVO Vampira FOMOD`, `C:\DBVO Vamp Exp FOMOD`,
`C:\DBVOVampModDevelopment`, `C:\DBVOVampiraModDevelopment`, `C:\DBVO Modding Repository`, MO2 at `D:\MO2`.

Facts from the last session:
- FOMOD projects in the library: DVA (DBVO Vampira) and DVE (Vampire DBVO Remastered). DVA's own
  "Skyrim Base Game (Mandatory)" option installs `DragonbornVoiceOver\voice_packs\vampira_voice_pack.json`, so the app
  adds no JSON of its own (since 11.0.5).
- ElevenLabs v4 status (2 Nov): every patch is on v4 except Serana Dialogue Expansion in both voices (audio from
  2024–2025) and Vampire's AA AIO (3,507 lines from 2024); Interesting NPCs and OStim Quests are in progress.
- The user builds FOMODs with "Build without packing" and zips with 7-Zip themselves (the built-in .zip writer is
  single-threaded and slow; making .zip packing call 7-Zip is an open idea, not requested yet).

## Key mechanics worth knowing (where the recent bugs were)

- FOMOD (`Core/Fomod/FomodService.cs`, `FomodXml.cs`, `Backend.Extra.cs` fomod.*, `wwwroot/phase3.js`):
  - Options linked to a DBVO Patch: `FilesFor` (Sound folder + DBVO Fix plugins that exist) and `Relink` (only
    `Linked` files are replaced; `RemovedLinked` destinations stay out; your own files and settings stay; options from
    before the Linked flag are recognised by destination). Every build runs RefreshLinked; the build's result is merged
    back into the saved project with Relink, and the frontend's `onJobDone` merges the same way.
  - The unpacked installer goes to `<release folder>\<name version>` next to its archive, only replaced when it's a
    DBVO Studio build; missing sources are left out of the XML; conditionalFileInstalls sources are mapped and copied.
  - Frontend saves are queued (`saveNow`/`persist`/`settleSaves`); Add DBVO Patches and Refresh linked make the editor
    inert while the backend works (`editorWaits`).
  - Page/group order: `makeExplicit` turns A–Z/Z–A lists into "As listed" before a move.
- Proofread lines (`wwwroot/proofread.js`, `Core/Text/Proofreader.cs`): markup protected as ⟦n⟧, ScriptSafe applies
  the xEdit script rules, "unusable" suggestions are not auto-skipped.
- lib.js: `rpc()` shows the "Working…" chip after 350 ms (QUIET list for background calls); `withBusy` overlay;
  `afterPointer` defers re-renders during a click; spinTracker for ↻.
- BSA writer: names in Windows-1252 (`BsaWriter.CanStore`).
- Backend caches patches, revoice and grunt projects in memory (`LoadPatch`, `LoadRv`, `LoadGr`), so long jobs save
  the shared object; still check that results don't overwrite edits made meanwhile.
