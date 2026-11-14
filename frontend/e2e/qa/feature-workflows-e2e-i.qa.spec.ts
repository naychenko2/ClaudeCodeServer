import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login } from './helpers';

// Часть I: адаптивность там, куда ui_probe по URL не доходит — редактор шаблонов
// и экран этапа; и кто именно на странице фичи даёт цели меньше 40 px на 360.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';
const SHOTS = '.cc-attachments/qa-wf-e2e';

const measure = (page: import('@playwright/test').Page) => page.evaluate(() => {
  const doc = document.documentElement;
  const small = [...document.querySelectorAll('button, [role="tab"], select, a[href], [role="checkbox"], [role="radio"]')]
    .filter(e => (e as HTMLElement).offsetParent !== null)
    .map(e => {
      const r = (e as HTMLElement).getBoundingClientRect();
      return {
        w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10,
        id: e.getAttribute('data-testid') ?? '', tag: e.tagName,
        text: ((e as HTMLElement).innerText || e.getAttribute('aria-label') || e.getAttribute('title') || '').slice(0, 30),
      };
    }).filter(x => x.w > 0 && x.h > 0 && (x.w < 40 || x.h < 40));
  const overflow = [...document.querySelectorAll('*')]
    .filter(e => (e as HTMLElement).scrollWidth > (e as HTMLElement).clientWidth + 1 && (e as HTMLElement).clientWidth > 0)
    .slice(0, 8).map(e => ({ sel: (e as HTMLElement).className ? '.' + String((e as HTMLElement).className).split(' ')[0] : e.tagName, sw: (e as HTMLElement).scrollWidth, cw: (e as HTMLElement).clientWidth }));
  return {
    docOverflow: doc.scrollWidth - doc.clientWidth,
    small: small.slice(0, 25), smallTotal: small.length, overflow,
  };
});

test('С11: редактор шаблонов и экран этапа на 360 и 1280', async ({ context, playwright, baseURL }) => {
  test.setTimeout(400_000);
  mkdirSync(SHOTS, { recursive: true });
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();

  for (const w of [360, 1280]) {
    let page = await context.newPage();
    page.setViewportSize({ width: w, height: w < 500 ? 800 : 900 });
    page.on('console', m => { if (m.type() === 'error') console.log(`[console ${m.type()}]`, m.text().slice(0, 200)); });
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

    // редактор шаблонов
    await page.goto('/#/specialties', { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(2500);
    await page.getByText('Процессы фичи', { exact: true }).click().catch(() => undefined);
    await page.waitForTimeout(1500);
    await page.getByRole('button', { name: /Полный цикл/ }).first().click().catch(() => undefined);
    await page.waitForTimeout(1500);
    console.log(`С11 редактор @${w}:`, JSON.stringify(await measure(page)).slice(0, 1200));
    await page.screenshot({ path: `.cc-attachments/qa-wf-e2e/С11-editor-${w}.png`, fullPage: true });

    // экран этапа — в свежей вкладке: хэш-переход на фичу из открытого /specialties в той же вкладке
    // теряет сегмент /feature/ (дефект приложения, заведён отдельной карточкой), а не спека
    await page.close();
    page = await context.newPage();
    page.setViewportSize({ width: w, height: w < 500 ? 800 : 900 });
    await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
    await page.getByTestId('pgraph-node-cut').click({ timeout: 20000 });
    await page.getByTestId('feature-stage-area-open').click({ timeout: 20000 });
    await expect(page.getByTestId('stage-screen'), `экран этапа открылся @${w}`).toBeVisible({ timeout: 20000 });
    await expect(page.getByTestId('stage-screen-loading')).toHaveCount(0, { timeout: 20000 });
    console.log(`С11 экран этапа @${w}:`, JSON.stringify(await measure(page)).slice(0, 1500));
    await page.screenshot({ path: `.cc-attachments/qa-wf-e2e/С11-stage-${w}.png`, fullPage: true });
    await page.close();
  }
});
