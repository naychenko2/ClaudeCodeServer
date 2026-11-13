import { test, expect, type BrowserContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login } from './helpers';

// Приёмка «Процессы фичи», третий спек: «Ждёт вас», чип возврата, редактор шаблонов, персона, 360.
const SHOTS = '.cc-attachments/qa-wf-final';
const PID = '32527f34-1907-4202-8c5c-9587b89d6366';
const A = '351d5c4e-c51f-4dce-b64e-2bc6be2e982d', D = 'fa5b0598-cbea-4d2f-94ef-8ecbacadabff';
const PERSONA = '20f7caaa-aa9a-4ccb-aeb2-841916d6fc9b';
const url = (f: string) => `/#/project/${PID}/feature/${f}`;
let token: string;
const errors: string[] = [];

test.beforeAll(async ({ playwright, baseURL }) => {
  const req = await playwright.request.newContext({ baseURL });
  token = await login(req);
  await req.dispose();
  mkdirSync(SHOTS, { recursive: true });
});

async function open(context: BrowserContext, path: string, theme: 'light' | 'dark' = 'light', w = 1280): Promise<Page> {
  const page = await context.newPage();
  await page.setViewportSize({ width: w, height: w < 500 ? 800 : 900 });
  page.on('console', m => { if (m.type() === 'error') errors.push(`[error] ${path} ${m.text()}`.slice(0, 300)); });
  page.on('pageerror', e => errors.push(`[pageerror] ${path} ${e}`.slice(0, 300)));
  await context.addInitScript(([tk, th]) => { localStorage.setItem('cc_token', tk as string); localStorage.setItem('theme-mode', th as string); }, [token, theme]);
  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(2500);
  return page;
}
const ov = (p: Page) => p.evaluate(() => ({ sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth }));
const flat = (s: string) => s.replace(/\n/g, ' | ');

test('С3: «Ждёт вас» тикает и совпадает с метриками', async ({ context }) => {
  const p = await open(context, url(A));
  await p.getByTestId('pgraph-node-step-back').click(); await p.waitForTimeout(1500);
  const kp = p.getByTestId('stage-screen-kpis');
  const k1 = flat(await kp.innerText());
  await p.getByRole('tab', { name: /Метрики/ }).click();
  const m1 = flat(await p.getByTestId('stage-metrics').innerText());
  await p.waitForTimeout(65000);
  const m2 = flat(await p.getByTestId('stage-metrics').innerText());
  const k2 = flat(await kp.innerText());
  console.log('KPI1:', k1, '\nМЕТР1:', m1, '\nKPI2:', k2, '\nМЕТР2:', m2);
  await p.close();
});

test('С9: чип возврата', async ({ context }) => {
  for (const [id, expected] of [[A, 'Процесс'], [D, 'Ход']] as const) {
    const p = await open(context, url(id));
    await p.getByRole('button', { name: /^Чат$/ }).first().click().catch(e => console.log('chat click fail', String(e).slice(0, 100)));
    await p.waitForTimeout(3000);
    console.log(expected, 'URL после клика:', p.url());
    const chips = await p.locator('[data-testid*="chip"], [data-testid*="feature-back"], [data-testid*="return"]').evaluateAll(els => els.map(e => `${e.getAttribute('data-testid')}: ${(e as HTMLElement).innerText}`));
    console.log(expected, 'чипы:', JSON.stringify(chips));
    const edge = p.getByTestId('chat-feature-edge');
    console.log(expected, 'chat-feature-edge:', await edge.count() ? flat(await edge.first().innerText()) : '—', '| label:', await p.getByTestId('chat-feature-label').count() ? flat(await p.getByTestId('chat-feature-label').first().innerText()) : '—');
    await p.screenshot({ path: `${SHOTS}/c9-chat-${expected}.png`, fullPage: false });
    await p.close();
  }
});

test('С7: редактор шаблонов', async ({ context }) => {
  const p = await open(context, url(A));
  await p.getByRole('button', { name: /Настройки проекта/ }).first().click();
  await p.waitForTimeout(1500);
  await p.getByText('Шаблоны процесса', { exact: true }).click();
  await p.waitForTimeout(2000);
  console.log('СПИСОК:', flat((await p.locator('body').innerText()).slice(-1800)));
  await p.screenshot({ path: `${SHOTS}/c7-list-light-1280.png`, fullPage: true });
  await p.getByText('Параллельный цикл', { exact: true }).last().click();
  await p.waitForTimeout(1500);
  const txt = await p.locator('body').innerText();
  console.log('ГЛОБАЛЬНЫЙ ПАРАЛЛЕЛЬНЫЙ:', flat(txt.slice(txt.indexOf('Шаблоны процесса'))).slice(0, 1800));
  await p.screenshot({ path: `${SHOTS}/c7-global-parallel.png`, fullPage: true });
  await p.getByRole('button', { name: 'Создать проектный' }).click().catch(e => console.log('нет Создать проектный', String(e).slice(0, 80)));
  await p.waitForTimeout(1500);
  const txt2 = await p.locator('body').innerText();
  console.log('ПОСЛЕ КОПИИ:', flat(txt2.slice(txt2.indexOf('Шаблоны процесса'))).slice(0, 2200));
  await p.screenshot({ path: `${SHOTS}/c7-copy-edit.png`, fullPage: true });
  console.log('ПОЛЯ после:', JSON.stringify(await p.locator('input, textarea, select').evaluateAll(els => els.slice(-20).map(e => `${e.tagName}[${(e as HTMLInputElement).placeholder || (e as HTMLElement).getAttribute('aria-label') || (e as HTMLElement).title || ''}]=${(e as HTMLInputElement).value?.slice(0, 30)}`))));
  await p.close();
});

test('С8: форма персоны, вкладка «Промпт»', async ({ context }) => {
  const p = await open(context, `/#/personas/${PERSONA}`);
  await p.getByRole('button', { name: 'Редактировать' }).first().click().catch(e => console.log('нет Редактировать', String(e).slice(0, 80)));
  await p.waitForTimeout(1500);
  await p.getByText('Промпт', { exact: true }).first().click().catch(e => console.log('нет Промпт', String(e).slice(0, 80)));
  await p.waitForTimeout(1500);
  const txt = await p.locator('body').innerText();
  console.log('ПРОМПТ:', flat(txt.slice(txt.indexOf('Промпт'))).slice(0, 2200));
  await p.screenshot({ path: `${SHOTS}/c8-persona-prompt-light.png`, fullPage: true });
  console.log('секции testid:', JSON.stringify(await p.locator('[data-testid^="prompt-section-"]').evaluateAll(els => els.map(e => e.getAttribute('data-testid')))));
  await p.close();
});

for (const [path, name] of [[url(A), 'граф'], [url(D), 'легаси'], [`/#/personas/${PERSONA}`, 'персона']] as const) {
  test(`С10: 360 — ${name}`, async ({ context }) => {
    for (const th of ['light', 'dark'] as const) {
      const p = await open(context, path, th, 360);
      const o = await ov(p);
      const small = await p.evaluate(() => [...document.querySelectorAll('button, [role=tab], select, a[href]')]
        .map(e => { const r = e.getBoundingClientRect(); return { t: ((e as HTMLElement).innerText || e.getAttribute('aria-label') || e.tagName).slice(0, 24), w: Math.round(r.width), h: Math.round(r.height) }; })
        .filter(x => x.w > 0 && (x.w < 40 || x.h < 40)).slice(0, 12));
      console.log(`360 ${name} ${th}:`, JSON.stringify(o), 'мелкие цели:', JSON.stringify(small));
      await p.screenshot({ path: `${SHOTS}/c10-${name}-${th}-360.png`, fullPage: true });
      await p.close();
    }
  });
}
test.afterAll(() => { console.log('CONSOLE ERRORS:', JSON.stringify([...new Set(errors)].slice(0, 12))); });
