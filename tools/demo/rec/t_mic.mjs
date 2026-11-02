import { chromium } from '/opt/node-tools/node_modules/playwright/index.mjs';
import fs from 'node:fs';
const b = await chromium.launch({ executablePath: '/opt/pw-browsers/chromium', args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream'] });
const p = await b.newPage(); await p.route('https://app.dbvo/**', (r) => r.fulfill({ status: 200, contentType: 'text/html', body: '<html></html>' })); await p.goto('https://app.dbvo/index.html');
const r = await p.evaluate(async () => {
  const s = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false } });
  const types = ['audio/webm;codecs=opus', 'audio/webm', 'audio/ogg;codecs=opus'].filter((t) => MediaRecorder.isTypeSupported(t));
  const rec = new MediaRecorder(s, { mimeType: types[0] }); const chunks = [];
  rec.ondataavailable = (e) => chunks.push(e.data); rec.start(250);
  // level meter while recording
  const ctx = new AudioContext(); const an = ctx.createAnalyser(); ctx.createMediaStreamSource(s).connect(an); const buf = new Float32Array(2048); let peak = 0;
  for (let i = 0; i < 20; i++) { await new Promise((r) => setTimeout(r, 100)); an.getFloatTimeDomainData(buf); peak = Math.max(peak, ...buf.map(Math.abs)); }
  await new Promise((res) => { rec.onstop = res; rec.stop(); });
  const blob = new Blob(chunks, { type: types[0] }); const ab = new Uint8Array(await blob.arrayBuffer());
  let bin = ''; for (const x of ab) bin += String.fromCharCode(x);
  return { type: types[0], bytes: ab.length, peak, b64: btoa(bin), rate: ctx.sampleRate };
});
fs.writeFileSync('/tmp/mic.webm', Buffer.from(r.b64, 'base64'));
console.log(`recorded ${r.bytes} bytes as ${r.type}, context ${r.rate} Hz, live level peak ${r.peak.toFixed(2)}`);
await b.close();
