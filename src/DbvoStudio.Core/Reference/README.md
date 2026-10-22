# UESP reference data

`uesp-skyrim.json.gz` is reference data extracted from **[UESP — The Unofficial Elder Scrolls Pages](https://en.uesp.net/)**.
All credit for this information goes to the UESP editors and community.

The data was collected by Auctor Lucan in a corpus of 17,519 UESP Skyrim pages (page revisions from
2011-09-30 to 2026-09-21) and reduced by `extract_uesp.py` to what DBVO Studio uses.

## License

UESP text is licensed under the [Creative Commons Attribution-ShareAlike 2.5 License](https://creativecommons.org/licenses/by-sa/2.5/)
([UESP copyright policy](https://en.uesp.net/wiki/UESPWiki:Copyrights)). This derived data file is shared under the same
license (CC BY-SA 2.5). It has been adapted: wiki markup was removed and only the fields below were kept. Every
NPC and book entry keeps the URL of the UESP article it came from; the page history there lists its authors.

This license covers the data file only, not the source code of DBVO Studio.

## What it contains

| Section | Count | From | Used for |
|---|---|---|---|
| `npcs` | 1,426 | `{{NPC Summary}}` of NPC pages | name, plugin, base/reference ID, voice type, race, gender |
| `quotes` | 22,854 | quoted dialogue (`''"…"''`) on NPC pages | who says a line; a `'''Name''':` prefix names the speaker when a page quotes a conversation |
| `books` | 1,245 | `{{Game Book}}` pages | book form ID, plugin, author (with the author's page), where it is found, quest |

Plugins are taken from the page's mod header: Dawnguard.esm, Dragonborn.esm or HearthFires.esm, otherwise Skyrim.esm.
Creation Club pages have no plugin.

## How the app uses it

* **NPC Revoice:** when a base-game line can only be narrowed to a voice type (several NPCs share it and the
  conditions don't say which one speaks), a UESP quote of that exact line by a single NPC with that voice type picks the
  speaker. Such speakers are marked *Probable* with "UESP quote" as the reason; the plugin data still decides the file
  name, folder and voice type.
* **Checks:** tests compare the scanner's speakers with UESP's quotes for the base game (about 99% agree where both name
  one NPC of the same voice type).
* **Voiced books:** book authors are suggestions for attributing books to the NPC who wrote them; the NPC's voice type
  still comes from the plugins.

## Rebuilding

```
python3 extract_uesp.py uesp_skyrim_articles.jsonl uesp-skyrim.json
gzip -9 uesp-skyrim.json
```

The input is a JSONL file with one UESP page per line (`title`, `source_url`, `revision_timestamp`, `content_wikitext`).

# Voice type templates

`voicetype-templates.txt` lists, for every vanilla voice type (Skyrim, Dawnguard, Dragonborn), the leveled actor
templates (`Lvl…`) and character templates (`LChar…`) whose NPCs can have it. It was compiled by Auctor Lucan for
Immersively Voiced Books of Skyrim. The Voiced Books Tool uses it when a book's author is an NPC that gets its voice
type from a template: the template's voice types are shown as candidates (a template can stand for several).
