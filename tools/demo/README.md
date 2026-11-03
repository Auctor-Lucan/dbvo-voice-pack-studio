# Demo harness (Linux, headless)

Runs the real DBVO Studio backend (Backend*.cs + DbvoStudio.Core, built as `host/DemoHost`) behind the real web UI
(`src/DbvoStudio.App/wwwroot`) in Playwright's Chromium, with Windows-style paths mapped to `/srv/dbvo/<drive>/…`,
a fake ElevenLabs/AI server (`svc/fake_api.py`, port 11434; "proofread" prompts go to `svc/fake_ai.py`).
Used for UI regression scripts (`rec/t_*.mjs`), the bug-sweep scripts (`rec/sweep2`, `rec/sweep3`) and the guide's
screenshots (`rec/shots*.mjs`, `rec/shot_*.mjs`).

## Set up (once per machine/session)

```sh
mkdir -p /tmp/dbvo-work && cp -r <repo>/tools/demo /tmp/dbvo-work/demo   # scripts expect /tmp/dbvo-work/demo (or set DEMO=)
cd /tmp/dbvo-work/demo
REPO=<repo> sh sync.sh                       # copies the repo backend in (+ overrides/), builds host/
/opt/dotnet/dotnet host/bin/Debug/net10.0/DemoHost.dll --make-data   # writes the demo drives under /srv/dbvo (wipes it)
cd rec
OUT=/tmp/dbvo-work/f1 node v1_setup.mjs      # voice packs (stops at a watchdog after a few minutes; that's enough)
OUT=/tmp/dbvo-work/f2 node v2_phases.mjs     # the Skyrim mod (same)
node seed.mjs                                # a generated Skyrim DBVO Patch
```

Run every script from `rec/` (`cd rec && node sweep2/s1_buildmerge.mjs`): the demo host keeps its library in
`<current folder>/DBVO Studio`, since the container has no Documents folder. Some sweep scripts also expect a
`D:\fomodroot` folder (`mkdir -p /srv/dbvo/D/fomodroot`).

After changing the app: `sh sync.sh` again (the web UI is served straight from the repo's wwwroot; set WWW= to use
another folder). Run a test: `cd rec && node t_click.mjs` (prints PASS/FAIL lines and a total).

Good regression set: t_pages, t_click, t_addp, t_tag, t_proof, t_skip, t_queue, t_fx, sweep2/s1_buildmerge,
sweep2/s2_addrace, sweep2/s4_relink.

Never put real voice audio, the user's library or keys here; MakeData builds everything from scratch.
