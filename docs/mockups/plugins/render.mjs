// Снимает PNG-кадры макета plugins-v1.html: 4 экрана × (desktop dark, desktop light, mobile light, mobile dark).
// Запуск из корня репозитория: node docs/mockups/plugins/render.mjs
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(path.join(here, '../../../frontend/package.json'));
const { chromium } = require('playwright');

const screens = ['install', 'consent', 'sidebar', 'draft'];
const variants = [
  { layout: 'desktop', theme: 'dark', width: 1280 },
  { layout: 'desktop', theme: 'light', width: 1280 },
  { layout: 'mobile', theme: 'light', width: 360 },
  { layout: 'mobile', theme: 'dark', width: 360 },
];

const browser = await chromium.launch();
const base = pathToFileURL(path.join(here, 'plugins-v1.html')).href;
for (const s of screens) {
  for (const v of variants) {
    const page = await browser.newPage({ viewport: { width: v.width, height: 800 }, deviceScaleFactor: 2 });
    await page.goto(`${base}#screen=${s}&layout=${v.layout}&theme=${v.theme}`);
    const frame = page.locator('.frame.on');
    const out = path.join(here, `${s}-${v.layout}-${v.theme}.png`);
    await frame.screenshot({ path: out });
    console.log(out);
    await page.close();
  }
}
await browser.close();
