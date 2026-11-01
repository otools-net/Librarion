// Draws every icon of the client's sprite (src/Librarion.Web/src/app/shared/icon.ts) on a contact sheet, light and dark,
// at 20 px (as used) and 48 px, and saves it as a PNG: a look at new or changed icons without running the app.
//   node agents/tools/icon-sheet/icon-sheet.mjs [out.png]     (default D:/Temp/librarion-icons.png)
// Uses the Playwright of tests/e2e (npm ci there first) with the system's Edge, as the e2e tests do.
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const require = createRequire(path.join(repo, 'tests/e2e/package.json'));
const { chromium } = require('@playwright/test');

const out = process.argv[2] ?? 'D:/Temp/librarion-icons.png';
const source = fs.readFileSync(path.join(repo, 'src/Librarion.Web/src/app/shared/icon.ts'), 'utf8');
const symbols = [...source.matchAll(/<symbol id="icon-([a-z-]+)"[^>]*>([\s\S]*?)<\/symbol>/g)].map(m => ({ name: m[1], body: m[2] }));

const cell = ({ name, body }) => `
    <figure>
        <svg viewBox="0 0 24 24" class="small">${body}</svg>
        <svg viewBox="0 0 24 24" class="large">${body}</svg>
        <figcaption>${name}</figcaption>
    </figure>`;
const sheet = (theme, colours) => `
    <section style="background:${colours.bg};color:${colours.text}">
        <h2>${theme}</h2>
        <div class="grid">${symbols.map(cell).join('')}</div>
    </section>`;
const html = `<!doctype html><html><head><style>
    body { margin: 0; font: 12px system-ui, sans-serif; }
    section { padding: 16px; }
    h2 { margin: 0 0 12px; font-size: 14px; }
    .grid { display: grid; grid-template-columns: repeat(8, 110px); gap: 10px; }
    figure { margin: 0; display: flex; flex-direction: column; align-items: center; gap: 6px; }
    svg { fill: none; stroke: currentColor; stroke-width: 1.8; stroke-linecap: round; stroke-linejoin: round; }
    .small { width: 20px; height: 20px; }
    .large { width: 48px; height: 48px; }
    figcaption { opacity: .75; }
</style></head><body>
${sheet('light', { bg: '#fffdf9', text: '#1f2328' })}
${sheet('dark', { bg: '#191e24', text: '#e7e9ec' })}
</body></html>`;

const browser = await chromium.launch({ channel: 'msedge' });
const page = await browser.newPage({ viewport: { width: 980, height: 600 } });
await page.setContent(html);
await page.screenshot({ path: out, fullPage: true });
await browser.close();
console.log(`${symbols.length} icons → ${out}`);
