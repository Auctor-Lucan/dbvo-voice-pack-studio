import { createRequire } from 'module'; const require = createRequire(import.meta.url); const { chromium } = require('playwright');
import fs from 'fs';
const W = process.env.W || '/home/claude/dbvo-voice-pack-studio/src/DbvoStudio.App/wwwroot';
// pictures as JPEG at print size, so the PDF stays small
let body = fs.readFileSync(W + '/help/guide.html', 'utf8').replace(/ loading="lazy"/g, '').replace(/src="help\/img\/([^".]+)\.[a-z]+"/g, (m, n) => `src="file://${process.cwd()}/img/${n}.jpg"`);
// A short chapter follows the one before on the same page; a long one starts a new page.
{
  const parts = body.split(/(?=<h2 )/);
  body = parts.map((sec) => {
    const text = sec.replace(/<[^>]+>/g, '').length;
    const imgs = (sec.match(/<img /g) || []).length;
    return text + imgs * 900 < 3500 ? sec.replace(/^<h2 /, '<h2 class="nb" ') : sec;
  }).join('');
}
// table of contents from the h2/h3 headings
const toc = [...body.matchAll(/<h([23])(?: class="nb")? id="([^"]+)">(.*?)<\/h\1>/g)].map(([, l, id, t]) => `<li class="t${l}"><a href="#${id}">${t.replace(/<[^>]+>/g, '')}</a></li>`).join('');
const css = `
@page { size: A4; }
html { font-family: 'Segoe UI', 'Noto Sans', 'DejaVu Sans', Arial, sans-serif; font-size: 10.2pt; line-height: 1.45; color: #d5dbe3; background: #1f242c; }
body { margin: 0; background: #1f242c; }
a { color: #6cb6ff; text-decoration: none; }
strong, b { color: #eef2f6; }
h1, h2, h3, h4 { color: #ffffff; line-height: 1.25; break-after: avoid; page-break-after: avoid; }
h2 { font-size: 19pt; margin: 0 0 10pt; padding-bottom: 5pt; border-bottom: 2px solid #58a6ff; break-before: page; page-break-before: always; }
h2.nb { break-before: auto; page-break-before: auto; margin-top: 22pt; }
h3 { font-size: 13.5pt; margin: 16pt 0 6pt; color: #9ecbff; }
h4 { font-size: 11pt; margin: 12pt 0 4pt; color: #c3d6ee; }
h3 + *, h4 + *, h2 + * { break-before: avoid; page-break-before: avoid; }
p { margin: 0 0 6pt; orphans: 3; widows: 3; }
ul, ol { margin: 0 0 6pt 16pt; padding: 0; }
li { margin: 0 0 2pt; break-inside: avoid; }
figure { margin: 8pt 0 10pt; break-inside: avoid; page-break-inside: avoid; text-align: center; }
figure img { max-width: 100%; max-height: 125mm; object-fit: contain; border: 1px solid #3a4350; border-radius: 4px; }
figcaption { font-size: 8.6pt; color: #98a3b3; margin-top: 3pt; }
img { max-width: 100%; }
table { width: 100%; border-collapse: collapse; margin: 6pt 0 10pt; font-size: 9pt; break-inside: auto; }
thead { display: table-header-group; }
tr { break-inside: avoid; page-break-inside: avoid; }
th, td { border: 1px solid #3a4350; padding: 4pt 6pt; vertical-align: top; text-align: left; }
th { background: #2a313b; color: #ffffff; }
code, pre { font-family: Consolas, 'DejaVu Sans Mono', monospace; font-size: 8.8pt; background: #2b323c; color: #e6edf3; border-radius: 3px; }
code { padding: 0 3px; }
pre { padding: 6pt 8pt; white-space: pre-wrap; break-inside: avoid; }
blockquote { margin: 6pt 0; padding: 4pt 10pt; border-left: 3px solid #58a6ff; background: #252c36; break-inside: avoid; }
hr { border: 0; border-top: 1px solid #3a4350; }
.cover { height: 270mm; display: flex; flex-direction: column; justify-content: center; align-items: center; text-align: center; gap: 10pt; }
.cover img { width: 100%; border-radius: 6px; border: 1px solid #3a4350; }
.cover .sub { font-size: 16pt; color: #ffffff; font-weight: 700; margin-top: 8pt; }
.cover .ver { font-size: 10.5pt; color: #98a3b3; }
.toc { break-before: page; }
.toc h1 { font-size: 19pt; border-bottom: 2px solid #58a6ff; padding-bottom: 5pt; margin-top: 0; }
.toc ul { list-style: none; margin: 0; columns: 2; column-gap: 10mm; }
.toc li { margin: 0 0 3pt; }
.toc li.t2 { font-weight: 700; margin-top: 7pt; break-after: avoid; }
.toc li.t3 { padding-left: 10pt; font-size: 9.4pt; }
`;
const html = `<!doctype html><html><head><meta charset="utf-8"><base href="file://${W}/"><style>${css}</style></head><body>
<div class="cover"><img src="file://${process.cwd()}/img/splash.jpg"><div class="sub">User guide · version 11.0.8</div><div class="ver">The guide built into DBVO Studio (F1), as a PDF</div></div>
<div class="toc"><h1>Contents</h1><ul>${toc}</ul></div>
${body}</body></html>`;
fs.writeFileSync('guide-print.html', html);
const b = await chromium.launch();
const p = await b.newPage();
await p.goto('file://' + process.cwd() + '/guide-print.html', { waitUntil: 'networkidle' });
await p.pdf({ path: 'content.pdf', format: 'A4', printBackground: true, displayHeaderFooter: true,
  headerTemplate: '<div></div>',
  footerTemplate: '<div style="width:100%;font-size:7.5pt;color:#8b96a6;padding:0 7mm;display:flex;justify-content:space-between;font-family:Arial"><span>DBVO Studio 11.0.8 · User guide</span><span><span class="pageNumber"></span> / <span class="totalPages"></span></span></div>',
  margin: { top: '7mm', bottom: '10mm', left: '7mm', right: '7mm' } });
await b.close();
console.log('ok');
