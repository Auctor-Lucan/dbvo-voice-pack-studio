# DBVO Studio

A Windows tool that takes Skyrim plugins all the way to a released Dragonborn Voice Over (DBVO) voice pack.

Phase 1: Import Mods
Drop in a mod's plugins. Every player line is pulled out and checked against DBVO's rules, and the DBVO Fix plugin is written for you. No xEdit, no collecting topic files by hand.

Phase 2: DBVO Patching
Voice the lines back to back with ElevenLabs, a local engine or the Voice Changer. Audio tags, several takes per line, review with the keyboard, take checks with Whisper, and .lip and .fuz files made for you. No Yakitori needed.

Phase 3: FOMOD Creation
Gather your patches into a FOMOD installer, with the DBVO JSON, that's easy to keep working on. You can export at any stage, or skip the FOMOD altogether.

Other Tools

NPC Revoice
Point it at any mod, or the base game. It finds every line a character speaks, works out who says what, lets you give each character the voice you want, and writes the files where the game looks for them. Revoice a whole follower, or give an NPC a voice type of their own.

Voiced Books
Narrate books, notes and journals for Voiced Books of Skyrim. Letters written by NPCs are read in their own voice type. Ships with the base game and DLC book texts ready to voice.

WAV to LIP
Lip sync from the audio alone. Whisper listens to each WAV and writes the transcript, then FaceFXWrapper makes the .lip. Drop in whole folders.

Naming: Legacy (DBVO 1.0.1, readable names) or DBVO 2.0 (xxHash64), matching DBVO Voice Pack Maker 2.7.0.

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
## Network Connections and Privacy

DBVO Studio is a Windows application for Skyrim Special Edition that helps authors create Dragonborn Voice Over (DBVO) voice packs and revoice NPCs.

### Internet Connections

DBVO Studio has no automatic updater, telemetry, or developer-operated account system. Network connections are made for features the user chooses to use.

Depending on the features enabled and services configured, the application may connect to:

- **ElevenLabs API:** Generates voice lines using the user's own ElevenLabs API key. This is a core feature of the application.
- **Hugging Face:** Downloads a Whisper speech-recognition model when the WAV-to-LIP workflow is used for the first time.
- **Optional AI providers:** Suggests dialogue delivery tags. Users can configure a local provider, such as Ollama or LM Studio, or an online provider, such as OpenAI or Anthropic, using their own credentials.
- **Optional local voice servers:** Connects to VoxCPM or OmniVoice on localhost when the user runs and selects those services.

Online services process requests under their own terms and privacy policies. Depending on the feature used, dialogue text, audio, or other request data may be sent to the selected provider.

### API Key Storage

API keys are stored encrypted for the current Windows user account and are sent to the service they belong to when the corresponding feature is used.

Users should protect their API keys and review the privacy and data-retention policies of any online provider they configure.

### Third-Party Components

The application distribution includes FFmpeg and 7-Zip, unmodified and accompanied by their applicable licence information and source links.

FaceFXWrapper, FonixData.cdf, and xWMAEncode are not bundled. Users must obtain these separately and comply with their applicable terms.

### AI-Generated Content

DBVO Studio can generate voice audio and assist with dialogue tagging through AI services. Users are responsible for ensuring that their generated content complies with the applicable service terms and platform policies, including any required AI-content disclosures.

### Nexus Mods Submission

Because DBVO Studio uses internet-connected features, its distribution is subject to Nexus Mods' applicable submission policies. The developer has contacted Nexus Mods staff to request guidance before publishing the application.

DBVO Studio is an independent community project and is not affiliated with or endorsed by Bethesda Softworks, Nexus Mods, ElevenLabs, Hugging Face, OpenAI, or Anthropic.

## Credits

Phase 1 is built on **[Idrinth](https://www.nexusmods.com/profile/Idrinth?gameId=1704)**'s DBVO text export script
([Nexus Mods](https://www.nexusmods.com/skyrimspecialedition/mods/182009)). Heartfelt thanks.

NPC voice types, quoted dialogue and book authors in `src/DbvoStudio.Core/Reference/uesp-skyrim.json.gz` come from
**[UESP — The Unofficial Elder Scrolls Pages](https://en.uesp.net/)**, collected by Auctor Lucan from 17,519 UESP Skyrim
pages. All credit for this information goes to the UESP editors and community. UESP text is licensed under
[CC BY-SA 2.5](https://creativecommons.org/licenses/by-sa/2.5/); the derived data file is shared under the same license.
See [`src/DbvoStudio.Core/Reference/README.md`](src/DbvoStudio.Core/Reference/README.md).
