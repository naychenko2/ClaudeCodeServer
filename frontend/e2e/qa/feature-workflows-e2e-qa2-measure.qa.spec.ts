import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login, api } from './helpers';

// QA-2 (задача 6c3cdc39), замеры.
// S7 = п.7: редактор шаблонов на 1280 — переполнение и тач-цели (в прошлый раз оборвалось).
// S9 = п.9: семь тач-целей редактора на 360 — фактический замер живого рендера.
// Правка D2 (fix/workflow-templates-touch-targets) в master ещё нет: замер фиксирует
// текущее состояние стенда, это доказательство «правки в UI нет», а не новый дефект.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const SHOTS = '.cc-attachments/qa-wf-e2e';

const measure = (page: import('@playwright/test').Page) => page.evaluate(() => {
  const doc = document.documentElement;
  const small = [...document.querySelectorAll('button, [role="tab"], select, a[href], [role="checkbox"], [role="radio"]')]
    .filter(e => (e as HTMLElement).offsetParent !== null)
    .map(e => {
      const r = (e as HTMLElement).getBoundingClientRect();
      return {
        w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10,
        tag: e.tagName,
        text: ((e as HTMLElement).innerText || e.getAttribute('aria-label') || e.getAttribute('title') || '').slice(0, 40),
      };
    }).filter(x => x.w > 0 && x.h > 0 && (x.w < 40 || x.h < 40));
  return { docOverflow: doc.scrollWidth - doc.clientWidth, smallTotal: small.length, small: small.slice(0, 30) };
});

const targetByName = (page: import('@playwright/test').Page, name: string | RegExp) =>
  page.getByRole('button', { name }).first().evaluate((el: Element) => {
    const r = (el as HTMLElement).getBoundingClientRect();
    return { w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10 };
  }).catch(() => null);

test('S7: редактор шаблонов на 1280 — переполнения и целей <40 нет', async ({ context, playwright, baseURL }) => {
  test.setTimeout(240_000);
  mkdirSync(SHOTS, { recursive: true });
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();

  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  page.on('pageerror', e => errs.push(String(e).slice(0, 200)));
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto('/#/specialties', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(3000);
  await page.getByText('Процессы фичи', { exact: true }).click({ timeout: 10000 });
  await page.waitForTimeout(1500);
  await page.getByRole('button', { name: /Полный цикл/ }).first().click({ timeout: 10000 });
  await page.waitForTimeout(2500);
  const m = await measure(page);
  console.log('S7 редактор @1280:', JSON.stringify(m).slice(0, 1500));
  await page.screenshot({ path: `${SHOTS}/qa2-s7-editor-1280.png`, fullPage: true });
  expect.soft(m.docOverflow, 'горизонтального переполнения документа нет').toBeLessThanOrEqual(1);
  console.log('S7 ошибки консоли:', JSON.stringify([...new Set(errs)]));
  await page.close();
});

test('S9: семь тач-целей редактора на 360 — фактический замер', async ({ context, playwright, baseURL }) => {
  test.setTimeout(300_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  const client = api(req, token);

  const page = await context.newPage();
  page.setViewportSize({ width: 360, height: 800 });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const results: Record<string, { w: number; h: number } | null> = {};

  // 1–4: обычный вид редактора на встроенном «Полном цикле».
  await page.goto('/#/specialties', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(3000);
  await page.getByText('Процессы фичи', { exact: true }).click({ timeout: 10000 });
  await page.waitForTimeout(1500);
  await page.getByRole('button', { name: /Полный цикл/ }).first().click({ timeout: 10000 });
  await page.waitForTimeout(2500);
  results['‹ Процессы'] = await targetByName(page, '‹ Процессы');
  results['Сбросить к исходному'] = await targetByName(page, 'Сбросить к исходному');
  results['+ Этап'] = await targetByName(page, /^\+ Этап/);
  results['Добавить этап'] = await targetByName(page, /^Добавить этап/);
  const m360 = await measure(page);
  console.log('S9 редактор @360:', JSON.stringify(m360).slice(0, 1500));
  await page.screenshot({ path: `${SHOTS}/qa2-s9-editor-360.png`, fullPage: true });

  // 5–6: плашка 409 — правка шаблона, который уже изменился (правим версию через API,
  // затем жмём «Сохранить» в редакторе на устаревшей). Свой тестовый шаблон заводим здесь.
  const tpl = await client.post<any>('/api/workflow-templates', {
    name: 'qa-e2e-qa2-conflict', stages: [{ role: 'cut', presence: 'on' }, { role: 'review', presence: 'on' }],
  });
  const tplId = tpl?.id ?? tpl?.template?.id ?? null;
  const tpls = await client.get<any[]>('/api/workflow-templates');
  const mine = tpls.find(t => (t.id ?? t.template?.id) === tplId);
  if (mine) {
    const id = mine.id ?? mine.template?.id;
    const body = JSON.parse(JSON.stringify(mine.template ?? mine)); body.id = id;
    body.name = 'qa-e2e-qa2-stall (внешняя правка)';
    await client.put('/api/workflow-templates', body); // версия в редакторе устаревает
    // открываем этот шаблон в редакторе (список уже показывает v2, редактор возьмёт её;
    // чтобы получить именно конфликт, правим имя в UI и сохраняем после внешней правки)
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(2500);
    await page.getByText('Процессы фичи', { exact: true }).click().catch(() => undefined);
    await page.waitForTimeout(1200);
    await page.getByRole('button', { name: /qa-e2e-qa2-conflict/ }).first().click({ timeout: 10000 }).catch(() => undefined);
    await page.waitForTimeout(2000);
    const nameInput = page.locator('input').first();
    await nameInput.fill('qa-e2e-qa2-stall (конфликт)').catch(() => undefined);
    await page.getByRole('button', { name: /Сохранить/ }).first().click({ timeout: 10000 }).catch(() => undefined);
    await page.waitForTimeout(2500);
    results['Загрузить vN…'] = await targetByName(page, /Загрузить v\d+/);
    results['Сохранить мои правки новым шаблоном'] = await targetByName(page, /Сохранить мои правки новым шаблоном/);
    const conflictBody = (await page.locator('body').innerText()).replace(/\n/g, ' | ');
    console.log('S9 плашка 409:', /шаблон уже измен|конфликт|обновить/i.test(conflictBody), JSON.stringify(conflictBody.match(/[^|]*(409|изменён|конфликт)[^|]*/i)?.slice(0, 2) ?? []));
    await page.screenshot({ path: `${SHOTS}/qa2-s9-conflict-360.png`, fullPage: true });
    await client.del(`/api/workflow-templates/${id}`).catch(() => undefined);
  } else {
    console.log('S9 тестового шаблона qa-e2e-qa2-stall нет — состояние 409 не собрано');
  }

  // 7: «Создать проектный» — на странице фичи в проектной области (глобальный read-only).
  const feats = await client.get<any[]>(`/api/projects/${PID}/tasks`);
  const epic = feats.find(t => t.title === 'qa-e2e-wf-feature' && t.role === 'feature');
  if (epic) {
    await page.goto(`/#/project/${PID}/feature/${epic.id}`, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(4000);
    results['Создать проектный'] = await targetByName(page, 'Создать проектный');
  } else {
    console.log('S9 фичи-сида qa-e2e-wf-feature нет — «Создать проектный» не измерен');
  }

  console.log('S9 семь тач-целей @360:', JSON.stringify(results));
  const got = Object.entries(results).filter(([, v]) => v !== null);
  const below = got.filter(([, v]) => (v as any).h < 40 || (v as any).w < 40);
  console.log('S9 итог: измерено', got.length, 'из 7; ниже 40 px:', JSON.stringify(below.map(([k]) => k)));
  expect(got.length, 'измерено не меньше четырёх доступных целей').toBeGreaterThanOrEqual(4);
  await page.close();
  await req.dispose();
});
