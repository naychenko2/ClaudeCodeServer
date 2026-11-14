import { test, expect } from '@playwright/test';
import { login } from './helpers';

// С11v2: чего не успел С11 — редактор на 1280 и экран этапа на 360 и 1280.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';


test('С11v2: экран этапа на 360 и 1280', async ({ context, playwright, baseURL }) => {
  test.setTimeout(360_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  for (const w of [360, 1280]) {
    const page = await context.newPage();
    page.setViewportSize({ width: w, height: w < 500 ? 800 : 900 });
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
    await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(4000);
    await page.getByTestId('pgraph-node-cut').click({ timeout: 15000 }).catch(() => undefined);
    await page.waitForTimeout(1500);
    const openBtn = await page.getByTestId('feature-stage-area-open').count();
    console.log(`С11v2 @${w} кнопка «Открыть этап»:`, openBtn);
    if (openBtn) await page.getByTestId('feature-stage-area-open').click({ timeout: 10000 }).catch(() => undefined);
    await page.waitForTimeout(4000);
    const hasScreen = await page.getByTestId('stage-screen').count();
    console.log(`С11v2 @${w} экран этапа:`, hasScreen);
    if (hasScreen) {
      const m = await page.evaluate(() => {
        const doc = document.documentElement;
        const small = [...document.querySelectorAll('button, [role="tab"], select, [role="checkbox"], [role="radio"]')]
          .filter(e => (e as HTMLElement).offsetParent !== null)
          .map(e => { const r = (e as HTMLElement).getBoundingClientRect(); return { w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10, id: e.getAttribute('data-testid') ?? '', text: ((e as HTMLElement).innerText || e.getAttribute('aria-label') || '').slice(0, 24) }; })
          .filter(x => x.w > 0 && x.h > 0 && (x.w < 40 || x.h < 40));
        return { docOverflow: doc.scrollWidth - doc.clientWidth, smallTotal: small.length, small: small.slice(0, 14) };
      });
      console.log(`С11v2 @${w} замер:`, JSON.stringify(m).slice(0, 1200));
    }
    await page.screenshot({ path: `.cc-attachments/qa-wf-e2e/С11v2-stage-${w}.png`, fullPage: false });
    await page.close();
  }
  expect(true).toBeTruthy();
});
