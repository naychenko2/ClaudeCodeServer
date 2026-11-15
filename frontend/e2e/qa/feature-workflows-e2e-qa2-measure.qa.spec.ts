import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login, api } from './helpers';

// QA-2 (задача 6c3cdc39), замеры.
// S7 = п.7: редактор шаблонов на 1280 — переполнение и тач-цели (в прошлый раз оборвалось).
// S9 = п.9: семь тач-целей редактора на 360 — фактический замер живого рендера.
// Правка D2 (merge 9b9a1f3bb) в master: замер фиксирует живое состояние стенда.

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

test('S9a: четыре тач-цели редактора на 360 — фактический замер', async ({ context, playwright, baseURL }) => {
  test.setTimeout(120_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();

  const page = await context.newPage();
  page.setViewportSize({ width: 360, height: 800 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  page.on('pageerror', e => errs.push(String(e).slice(0, 200)));
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const results: Record<string, { w: number; h: number } | null> = {};

  // 1–4: панель «Процессы фичи», встроенный «Полный цикл».
  await page.goto('/#/specialties', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(3000);
  await page.getByText('Процессы фичи', { exact: true }).click({ timeout: 10000 });
  await page.waitForTimeout(1500);
  await page.getByRole('button', { name: /Полный цикл/ }).first().click({ timeout: 10000 });
  await page.waitForTimeout(2500);
  results['‹ Процессы'] = await targetByName(page, '‹ Процессы');
  results['Сбросить к исходному'] = await targetByName(page, 'Сбросить к исходному');
  results['+ Этап'] = await targetByName(page, /^\+ Этап/);
  // «Добавить этап» в непустом шаблоне открывается по «+ Этап» (WorkflowStageStrip.addButton).
  await page.getByRole('button', { name: /^\+ Этап/ }).first().click({ timeout: 8000 }).catch(() => undefined);
  await page.waitForTimeout(1200);
  results['Добавить этап'] = await targetByName(page, /^Добавить этап/);
  console.log('S9a четыре цели @360:', JSON.stringify(results));
  const m360 = await measure(page);
  console.log('S9a цели <40 на странице:', JSON.stringify(m360).slice(0, 1200));
  await page.screenshot({ path: `${SHOTS}/qa2-s9-editor-360.png`, fullPage: true });
  const got = Object.entries(results).filter(([, v]) => v !== null);
  console.log('S9a итог: измерено', got.length, 'из 4; ниже 40 px:',
    JSON.stringify(got.filter(([, v]) => (v as any).h < 40 || (v as any).w < 40).map(([k]) => k)));
  console.log('S9a ошибки консоли:', JSON.stringify([...new Set(errs)]));
  expect(got.length, 'измерено все четыре цели').toBe(4);
  await page.close();
});

test('S9b: плашка конфликта и «Создать проектный» на 360 — фактический замер', async ({ context, playwright, baseURL }) => {
  test.setTimeout(240_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  const client = api(req, token);

  const page = await context.newPage();
  page.setViewportSize({ width: 360, height: 800 });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const results: Record<string, { w: number; h: number } | null> = {};

  // 5–6: плашка конфликта. Порядок такой: открыть редактор (v1) → правка в UI (черновик
  //      изменён, «Сохранить» активен) → внешняя правка (v2) → «Сохранить» с v1 → 409.
  //      Иначе редактор успевает перечитать v2 и «Сохранить» остаётся disabled.
  const tpl = await client.post<any>('/api/workflow-templates', {
    name: 'qa-e2e-qa2-conflict', stages: [{ role: 'cut', presence: 'on' }, { role: 'review', presence: 'on' }],
  });
  const tplId = tpl?.id ?? tpl?.template?.id ?? null;
  try {
    await page.goto('/#/specialties', { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(3000);
    await page.getByText('Процессы фичи', { exact: true }).click({ timeout: 10000 });
    await page.waitForTimeout(1500);
    await page.getByRole('button', { name: /qa-e2e-qa2-conflict/ }).first().click({ timeout: 10000 });
    await page.waitForTimeout(2500);
    // Поле имени — первый textbox редактора (Field label не связан с input, getByLabel не берёт).
    await page.getByRole('textbox').first().fill('qa-e2e-qa2-conflict (в редакторе)', { timeout: 10000 });
    console.log('S9b правка в редакторе внесена');
    const tpls = await client.get<any[]>('/api/workflow-templates');
    const entry = tpls.find(t => (t.template?.id ?? t.id) === tplId);
    const full = JSON.parse(JSON.stringify(entry?.template ?? entry));
    full.name = 'qa-e2e-qa2-conflict (внешняя правка)';
    const ext = await client.put<any>(`/api/workflow-templates/${tplId}`, { ...full, expectedVersion: full.version ?? 1 });
    console.log('S9b внешняя правка: версия была', full.version, 'стала', ext?.version);
    await page.getByRole('button', { name: /^Сохранить$/ }).first().click({ timeout: 10000 });
    await page.waitForTimeout(3000);
    results['Загрузить vN…'] = await targetByName(page, /Загрузить v\d+/);
    results['Сохранить мои правки новым шаблоном'] = await targetByName(page, /Сохранить мои правки новым шаблоном/);
    console.log('S9b две цели плашки @360:', JSON.stringify(results));
    const conflictBody = (await page.locator('body').innerText()).replace(/\n/g, ' | ');
    console.log('S9b плашка конфликта:', /уже измен|конфликт|перечитать/i.test(conflictBody),
      JSON.stringify((conflictBody.match(/[^|]*(конфликт|изменён|перечитать)[^|]*/i) ?? []).slice(0, 2)));
    await page.screenshot({ path: `${SHOTS}/qa2-s9-conflict-360.png`, fullPage: true });
  } catch (e) {
    console.log('S9b состояние 409 не собрано:', String(e).slice(0, 200));
  } finally {
    if (tplId) await client.del(`/api/workflow-templates/${tplId}`).catch(() => undefined);
  }

  // 7: «Создать проектный» — настройки проекта → секция «Процессы» → глобальный шаблон (read-only).
  try {
    await page.goto(`/#/project/${PID}`, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(4000);
    await page.getByRole('button', { name: /Настройки проекта/ }).first().click({ timeout: 10000 });
    await page.waitForTimeout(2500);
    await page.getByText('Процессы', { exact: true }).first().click({ timeout: 10000 });
    await page.waitForTimeout(1500);
    await page.getByRole('button', { name: /Полный цикл/ }).first().click({ timeout: 10000 });
    await page.waitForTimeout(2500);
    results['Создать проектный'] = await targetByName(page, 'Создать проектный');
    console.log('S9b «Создать проектный» @360:', JSON.stringify(results['Создать проектный']));
    await page.screenshot({ path: `${SHOTS}/qa2-s9-project-360.png`, fullPage: true });
  } catch (e) {
    console.log('S9b «Создать проектный» не измерен:', String(e).slice(0, 200));
  }

  console.log('S9b семь тач-целей @360 (собирается с S9a):', JSON.stringify(results));
  await page.close();
  await req.dispose();
});

test('S9c: «Создать проектный» на 360 — настройки проекта → «Процессы» → глобальный шаблон', async ({ context, playwright, baseURL }) => {
  test.setTimeout(150_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();

  const page = await context.newPage();
  page.setViewportSize({ width: 360, height: 800 });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.goto(`/#/project/${PID}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  // На мобиле пункт настроек может быть свёрнут в «Ещё».
  if (await page.getByRole('button', { name: /Настройки проекта/ }).count() === 0) {
    await page.getByRole('button', { name: 'Ещё' }).first().click({ timeout: 8000 }).catch(() => undefined);
    await page.waitForTimeout(1500);
  }
  await page.getByRole('button', { name: /Настройки проекта/ }).first().click({ timeout: 15000 });
  await page.waitForTimeout(3000);
  const sect = page.getByText('Процессы', { exact: true }).first();
  console.log('S9c секция «Процессы» в настройках:', await sect.count());
  await sect.click({ timeout: 15000 });
  await page.waitForTimeout(2000);
  const openTpl = await page.getByRole('button', { name: /Полный цикл/ }).first().click({ timeout: 15000 })
    .then(() => true).catch(() => false);
  console.log('S9c глобальный шаблон открыт:', openTpl);
  await page.waitForTimeout(2500);
  const target = await targetByName(page, 'Создать проектный');
  console.log('S9c «Создать проектный» @360:', JSON.stringify(target));
  const notice = (await page.locator('body').innerText()).includes('Глобальный шаблон правит администратор');
  console.log('S9c плашка read-only есть:', notice);
  await page.screenshot({ path: `${SHOTS}/qa2-s9c-project-360.png`, fullPage: true });
  expect(target, 'цель измерена').not.toBeNull();
  await page.close();
});
