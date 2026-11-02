import fs from 'node:fs'; import path from 'node:path';
import { boot, sleep, waitJob } from './shots_common.mjs';
const LIB = '/srv/dbvo/D/DBVO Studio Library';
const s = await boot(); const { page } = s;
page.on('console', (m) => console.log('CONSOLE', m.text()));
await page.waitForSelector('.home-cards');
let id; for (const d of fs.readdirSync(path.join(LIB, 'patches'))) { const j = JSON.parse(fs.readFileSync(path.join(LIB, 'patches', d, 'patch.json'), 'utf8')); if (j.Name === 'Skyrim DLCs') id = d; }
const r = await page.evaluate(async (id) => { const { rpc } = await import('/lib.js'); const sc = await rpc('patch.legacyScan', { id, folder: 'D:\\Old pack' }); let started; try { started = await rpc('patch.importLegacy', { id, folder: 'D:\\Old pack' }); } catch (e) { started = 'ERR ' + e.message; } return JSON.stringify({ sc, started }); }, id);
console.log(r);
await sleep(5000);
const j = JSON.parse(fs.readFileSync(path.join(LIB, 'patches', id, 'patch.json'), 'utf8'));
console.log(j.Lines.filter((l) => l.Takes.length && l.Takes.some((t) => t.OriginalFuz)).length, 'lines with original fuz;', j.Lines.filter((l) => !l.Takes.length).length, 'without takes;', j.Lines.filter((l) => l.LastError).map((l) => l.LastError).join(' | '));
process.exit(0);
