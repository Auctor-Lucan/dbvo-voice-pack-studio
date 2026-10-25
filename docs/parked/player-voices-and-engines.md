# Player voice packs (built 2026-10-25) and new voice engines (still parked)

The player voice part is built as the Player Grunts Tool (Other Tools, 8.1): see the user guide. The notes below were the plan; the new engines are still parked.

## Player Voice Tool (Other Tools)
One tool exporting from the same takes to:
- **PC Head Tracking** (nexusmods.com/skyrimspecialedition/mods/11993): `Data\Sound\FX\PC Head Tracking\<Category>\<Event>\01..05` (Greeting Goodbye/Hello/Random: 10 files). WAV/XWM/FUZ (xwm preferred, mono, <5 s). Every file slot filled (duplicates fine) or the event plays silence. Shouts: `Sound\Voice\PC Head Tracking - MCM.esp\_FemaleBYO|_MaleBYO` (84) and `PC Head Tracking - Patch.esp` (28), renamed vanilla shout files.
- **Smart Player Voice System** (mods/183133, needs Audio Util; also plays PCHT packs):
  - Script-based: `sound\SPVS_AdditionalVoice\CustomVoiceType\FemaleCustom01-05|MaleCustom01-05\_Vanilla\<Attack|AttackFatigued|Bash|BashFatigued|Block|BlockFatigued|Death|EnterEagleEye|ExitEagleEye|Hit|LeaveWater|OutofBreath|PowerAttack|PowerAttackFatigued|Sprint>`; any names/counts; .wav/.fuz keep lip sync (.xwm doesn't).
  - Additional voices: same root, one folder per event (Book, Craft, Sneak, Magic\…, Health\…); names unique across subfolders.
  - Discovered: `Discovered\General`, `Discovered\Specific\Voice` + `Specific\Config\*.json` mapping location name → file name.
  - Subtitles: `<file>.toml` next to the audio, lines like `en = "Hmm..."`.
  - Vanilla-based: `sound\voice\SmartPlayerVoiceSystem.esp\SPVS_ReplaceFemaleCustom01-05|SPVS_ReplaceMaleCustom01-05`, fixed names/counts (e.g. `Combat_____Attack__00000811_1`..`816_1`); only method for shouts; no subtitles.
- Idea: events grid (event, lines, required count, takes/tags/voice as Phase 2); fill missing slots with duplicates; .lip/.fuz/.toml/JSON written; non-verbal sounds best via Record + Voice Changer.

## Engines asked for on Nexus
- Generic OpenAI-compatible TTS (`/v1/audio/speech`): address, model, voice.
- Google Gemini TTS (own API).
- Fish Audio / FishDrama3 (own API; check per server).
