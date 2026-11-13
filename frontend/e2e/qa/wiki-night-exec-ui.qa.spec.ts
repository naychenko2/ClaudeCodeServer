// QA · фича «Ночь вики на нескольких локальных моделях»: интерфейс.
// A. Редактор админа «Исполнители ночи» в строке места wiki-nightly (вкладка «Применение»):
//    добавить/убрать модель, потолок, сохранение с перечиткой, двойной клик — одна запись.
// B. Блок владельца «Вики → Настройки»: список и разбивка только для чтения, перенос длинного
//    списка на 360 px, нет горизонтального переполнения, обе темы.
// Снимки — .cc-attachments/qa-night-exec/shots/. Стенд поднят заранее (см. отчёт).

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';
import { ATT, readSeed } from './wiki-night-exec-helpers';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5560';
const SHOTS = path.join(ATT, 'shots');
fs.mkdirSync(SHOTS, { recursive: true });

test.setTimeout(240_000);

let request: APIRequestContext;

// Вход на стенде под rate-limit: один токен на пару «логин/пароль» за прогон.
// Новый контекст браузера требует только свою подстановку токена, а не новый вход.
const tokenCache = new Map<string, string>();
async function tokenFor(username: string, password: string): Promise<string> {
  const key = `${username}:${password}`;
  const hit = tokenCache.get(key);
  if (hit) return hit;
  const r = await request.post('/api/auth/login', { data: { username, password } });
  expect(r.ok(), `вход ${username} (${r.status()})`).toBeTruthy();
  const token = (await r.json()).token as string;
  tokenCache.set(key, token);
  return token;
}

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
});

test.afterAll(async () => {
  await request?.dispose();
});

// Вход через токен (роль — из /api/auth/me): форма входа проверена другими спеками, здесь
// важна проверяемая поверхность, а не логин.
async function uiLogin(page: Page, username: string, password: string) {
  const token = await tokenFor(username, password);
  await page.goto(`${BASE}/#/home`);
  await page.evaluate(async (t) => {
    localStorage.setItem('cc_token', t);
    const me = await fetch('/api/auth/me', { headers: { Authorization: `Bearer ${t}` } });
    const m = await me.json() as { userId?: string; role?: string };
    localStorage.setItem('cc_user_id', m.userId || '');
    if (m.role) localStorage.setItem('cc_role', m.role);
  }, token);
  await page.reload();
  await expect(page.locator('#root')).not.toBeEmpty();
  await page.waitForTimeout(1200);
}

const scrollMetrics = async (page: Page, selector: string) => page.evaluate((sel) => {
  const el = document.querySelector(sel) as HTMLElement | null;
  const doc = document.documentElement;
  const r = el?.getBoundingClientRect() ?? null;
  return {
    docOverflow: doc.scrollWidth - doc.clientWidth,
    box: r ? { x: Math.round(r.x), width: Math.round(r.width), right: Math.round(r.right) } : null,
    viewport: doc.clientWidth,
  };
}, selector);

test('A. редактор админа: выбор движков, потолок, сохранение и двойной клик', async ({ page }) => {
  await uiLogin(page, 'admin', 'qa-dev-12345');
  // Исходное состояние списка — две модели (прошлые проверки могли оставить одну).
  const adminToken0 = await tokenFor('admin', 'qa-dev-12345');
  const reset = await request.put('/api/admin/local-actions/wiki-nightly/executors', {
    headers: { Authorization: `Bearer ${adminToken0}` },
    data: { maxTotal: 2, executors: [{ engine: 'local', parallel: 1 }, { engine: 'night2', parallel: 1 }] },
  });
  expect(reset.status(), 'исходный список записан').toBe(200);
  await page.goto(`${BASE}/#/models`);
  await page.getByRole('button', { name: 'Применение', exact: true }).click({ timeout: 20_000 });

  const row = page.locator('[data-testid="action-row-wiki-nightly"]');
  await row.scrollIntoViewIfNeeded();
  await expect(row, 'строка места wiki-nightly есть в таблице').toBeVisible();

  const block = row.locator('[data-testid="night-executors"]');
  await expect(block, 'блок «Исполнители ночи» у места с флагом').toBeVisible();
  await expect(row.locator('[data-testid="night-exec-row-local"]')).toBeVisible();
  await expect(row.locator('[data-testid="night-exec-row-night2"]')).toBeVisible();

  // Добавить/убрать модель: у неотмеченного движка нет своего степпера, у отмеченного — есть.
  const localRow = row.locator('[data-testid="night-exec-row-local"]');
  const localCheck = localRow.getByRole('checkbox');
  expect(await localCheck.isChecked(), 'local отмечен в сохранённом списке').toBe(true);
  expect(await localRow.getByRole('group', { name: /Параллельность/ }).count(), 'степпер отмеченного движка').toBe(1);
  await localCheck.uncheck();
  expect(await localRow.getByRole('group', { name: /Параллельность/ }).count(), 'у снятого движка степпера нет').toBe(0);
  await localCheck.check();

  // Потолок 1..4 и упор: «+» на 4 гаснет. Причина упора — maxHint у Stepper (хвост R-W7b п.2):
  // по постановке упор обязан показывать причину, поэтому факт проверяется отдельно.
  const total = row.locator('[data-testid="night-exec-total"]');
  const plus = total.getByRole('button', { name: /Больше/ });
  const minus = total.getByRole('button', { name: 'Меньше' });
  const value = total.locator('span[aria-live="polite"]');
  for (let i = 0; i < 5; i++) {
    if (await plus.isEnabled()) await plus.click();
  }
  await expect(value, 'потолок упирается в 4').toHaveText('4');
  expect(await plus.isDisabled(), 'на 4 «+» погашена').toBe(true);
  const hint = await plus.getAttribute('aria-label');
  const hintTitle = await plus.evaluate(el => el.parentElement?.getAttribute('title') ?? null);
  fs.writeFileSync(path.join(SHOTS, 'stepper-cap.json'), JSON.stringify({ hint, hintTitle, disabled: true }, null, 2));
  for (let i = 0; i < 5; i++) {
    if (await minus.isEnabled()) await minus.click();
  }
  await expect(value, 'низ упирается в 1').toHaveText('1');

  // Сохранение с перечиткой: одна запись на двойной клик, состояние из /usage совпадает.
  const state = { maxTotal: 2, executors: [{ engine: 'local', parallel: 1 }, { engine: 'night2', parallel: 1 }] };
  await plus.click();
  await expect(value, 'потолок 2 перед сохранением').toHaveText('2');

  let puts = 0;
  await page.route('**/api/admin/local-actions/wiki-nightly/executors', async route => {
    if (route.request().method() === 'PUT') puts++;
    await route.continue();
  });
  const save = row.locator('[data-testid="night-executors-save"]');
  // Двойной клик: второй клик приходит в тот же миг, когда кнопка уже сохраняет.
  await save.dblclick();
  await expect.poll(() => puts, { timeout: 15_000, message: 'список записан' }).toBeGreaterThanOrEqual(1);
  await page.waitForTimeout(1500);
  expect(puts, 'двойной клик дал ровно одну запись').toBe(1);

  // Перечитка: UI показывает то, что лежит в снимке usage (ответ PUT для рендера не берётся).
  const usage = await api(request, await tokenFor('admin', 'qa-dev-12345')).get<{ ollama: { actions: Array<{ key: string; executors: unknown }> } }>('/api/usage');
  expect(usage.ollama.actions.find(a => a.key === 'wiki-nightly')!.executors).toEqual({
    maxTotal: 2, items: state.executors,
  });
  await expect(localCheck, 'после сохранения local отмечен').toBeChecked();
  const night2Check = row.locator('[data-testid="night-exec-row-night2"]').getByRole('checkbox');
  await expect(night2Check, 'после сохранения night2 отмечен').toBeChecked();
  await row.screenshot({ path: path.join(SHOTS, 'admin-editor-1280.png') });

  // Убрать движок и сохранить: список места — только local.
  await night2Check.uncheck();
  await save.click();
  await expect.poll(async () => {
    const u = await api(request, await tokenFor('admin', 'qa-dev-12345')).get<{ ollama: { actions: Array<{ key: string; executors: unknown }> } }>('/api/usage');
    return JSON.stringify(u.ollama.actions.find(a => a.key === 'wiki-nightly')!.executors);
  }, { timeout: 15_000, message: 'список без night2 записан' })
    .toBe(JSON.stringify({ maxTotal: 2, items: [{ engine: 'local', parallel: 1 }] }));

  // Возврат к двум движкам — для витрины владельца ниже.
  await night2Check.check();
  await save.click();
  await expect.poll(async () => {
    const u = await api(request, await tokenFor('admin', 'qa-dev-12345')).get<{ ollama: { actions: Array<{ key: string; executors: unknown }> } }>('/api/usage');
    return JSON.stringify(u.ollama.actions.find(a => a.key === 'wiki-nightly')!.executors);
  }, { timeout: 15_000, message: 'список снова из двух движков' })
    .toBe(JSON.stringify({ maxTotal: 2, items: state.executors }));
});

test('B. блок владельца: чтение, перенос, обе темы, 360 и 1280', async ({ browser }) => {
  const seed = readSeed();

  // Каждый замер — в своём контексте браузера: открытые панели и снимок навигации прошлого
  // прохода иначе переживают перезагрузку и меняют рельсу вики (проверено разведкой).
  const openSettings = async (page: Page, mobile: boolean) => {
    // Пользовательский путь: раздел «Заметки» из левой навигации, дальше — вики.
    if (mobile) {
      // На 360 левой навигации нет: разделы живут под кнопкой «Ещё».
      await page.getByRole('button', { name: 'Ещё', exact: true }).click({ timeout: 20_000 });
      await page.waitForTimeout(600);
      // Меню «Ещё» — ToolbarOverflowMenu: пункты это role=menuitem.
      await page.getByRole('menuitem', { name: 'Заметки', exact: true }).click({ timeout: 20_000 });
    } else {
      const notesNav = page.getByRole('button', { name: 'Заметки', exact: true }).first();
      await expect(notesNav).toBeVisible({ timeout: 30_000 });
      await notesNav.click({ timeout: 20_000 });
    }
    if (mobile) {
      await page.getByRole('button', { name: 'Вики', exact: true }).click({ timeout: 20_000 });
      await page.getByRole('button', { name: /Решения, журнал, здоровье, настройки/ }).click({ timeout: 20_000 });
    }
    // Рельса панелей вики (десктоп) строится после загрузки дерева разделов — ждём саму кнопку.
    const settingsRail = page.getByRole('button', { name: 'Настройки', exact: true }).first();
    await expect(settingsRail).toBeVisible({ timeout: 30_000 });
    await settingsRail.click({ timeout: 20_000 });
    await expect(page.locator('[data-testid="wiki-nightly-executors-note"]')).toBeVisible({ timeout: 20_000 });
  };

  const login = async (page: Page) => {
    const token = await tokenFor(seed.owner, seed.password);
    await page.goto(`${BASE}/#/home`);
    await page.evaluate(async (t) => {
      localStorage.setItem('cc_token', t);
      const me = await fetch('/api/auth/me', { headers: { Authorization: `Bearer ${t}` } });
      const m = await me.json() as { userId?: string; role?: string };
      localStorage.setItem('cc_user_id', m.userId || '');
      if (m.role) localStorage.setItem('cc_role', m.role);
    }, token);
    await page.reload();
    await expect(page.locator('#root')).not.toBeEmpty();
    await page.waitForTimeout(1200);
  };

  const measure = async (page: Page, width: number, theme: 'light' | 'dark') => {
    const applied = await page.evaluate(() => document.documentElement.dataset.theme);
    expect(applied, `тема ${theme} применена`).toBe(theme);

    const note = page.locator('[data-testid="wiki-nightly-executors-note"]');
    await expect(note, 'строка исполнителей у владельца').toContainText('Исполнители:');
    await expect(note, 'источник назначения назван').toContainText('назначает администратор');
    // Только чтение: чекбоксов и кнопки сохранения редактора здесь нет.
    await expect(page.locator('[data-testid="night-executors"]'), 'редактора у владельца нет').toHaveCount(0);

    // Разбивка последней ночи по движкам — числа попыток, без текста знаний.
    const breakdown = page.locator('[data-testid="wiki-nightly-executors"]');
    await expect(breakdown, 'разбивка по исполнителям есть').toBeVisible();
    const text = (await breakdown.innerText()).replace(/\s+/g, ' ');
    expect(text, `разбивка по движкам: ${text}`).toMatch(/Qwen 27B: \d+ попыт/);
    expect(text).toMatch(/qwen3\.8-27b: \d+ попыт/);

    const m = await scrollMetrics(page, '[data-testid="wiki-nightly-executors-note"]');
    const b = await scrollMetrics(page, '[data-testid="wiki-nightly-executors"]');
    expect(m.docOverflow, `нет горизонтального переполнения документа (${width}/${theme})`).toBeLessThanOrEqual(0);
    expect(m.box && m.box.right <= m.viewport, 'строка исполнителей в пределах экрана').toBeTruthy();
    expect(b.box && b.box.right <= b.viewport, 'разбивка в пределах экрана').toBeTruthy();
    fs.writeFileSync(path.join(SHOTS, `owner-${width}-${theme}.json`),
      JSON.stringify({ note: m, breakdown: b, text }, null, 2));
    await page.screenshot({ path: path.join(SHOTS, `owner-${width}-${theme}.png`) });

    // Длинные строки разбивки переносятся, а не тянут экран (проверка по каждой строке).
    const rows = await page.locator('[data-testid="wiki-nightly-executors"] > div').evaluateAll(
      els => els.map(e => {
        const r = (e as HTMLElement).getBoundingClientRect();
        return { text: (e as HTMLElement).innerText, height: Math.round(r.height), right: Math.round(r.right) };
      }));
    expect(rows.length, 'в разбивке две строки').toBe(2);
    for (const row of rows) {
      expect(row.right, `строка «${row.text}» не выходит за экран ${width}`).toBeLessThanOrEqual(width);
      expect(row.height, `строка «${row.text}» имеет высоту (не схлопнута)`).toBeGreaterThan(10);
    }
    return { text, rows };
  };

  for (const width of [1280, 360]) {
    for (const theme of ['light', 'dark'] as const) {
      console.log(`[ui] замер ${width}/${theme}`);
      const ctx = await browser.newContext({ viewport: { width, height: 900 } });
      try {
        const page = await ctx.newPage();
        await page.addInitScript((t) => localStorage.setItem('theme-mode', t), theme);
        await login(page);
        await openSettings(page, width <= 600);
        await measure(page, width, theme);
      } finally {
        await ctx.close();
      }
    }
  }
});

test('C. редактор админа на 360: перенос строк и отсутствие переполнения', async ({ browser }) => {
  const ctx = await browser.newContext({ viewport: { width: 360, height: 900 } });
  try {
    const page = await ctx.newPage();
    const seed = readSeed();
    void seed;
    // Роль админа нужна и в браузере: модалка «Модели и расход» — админская поверхность.
    const token = await tokenFor('admin', 'qa-dev-12345');
    await page.goto(`${BASE}/#/home`);
    await page.evaluate(async (t) => {
      localStorage.setItem('cc_token', t);
      const me = await fetch('/api/auth/me', { headers: { Authorization: `Bearer ${t}` } });
      const m = await me.json() as { userId?: string; role?: string };
      localStorage.setItem('cc_user_id', m.userId || '');
      if (m.role) localStorage.setItem('cc_role', m.role);
    }, token);
    await page.reload();
    await expect(page.locator('#root')).not.toBeEmpty();
    await page.waitForTimeout(1200);

    await page.goto(`${BASE}/#/models`);
    await page.getByRole('button', { name: 'Применение', exact: true }).click({ timeout: 20_000 });
    const row = page.locator('[data-testid="action-row-wiki-nightly"]');
    await row.scrollIntoViewIfNeeded();
    await expect(row.locator('[data-testid="night-executors"]')).toBeVisible();

    const m = await scrollMetrics(page, '[data-testid="action-row-wiki-nightly"]');
    const block = await scrollMetrics(page, '[data-testid="night-executors"]');
    expect(m.docOverflow, 'нет горизонтального переполнения документа на 360').toBeLessThanOrEqual(0);
    expect(m.box && m.box.right <= m.viewport, 'строка места в пределах 360').toBeTruthy();
    expect(block.box && block.box.right <= block.viewport, 'блок редактора в пределах 360').toBeTruthy();
    fs.writeFileSync(path.join(SHOTS, 'admin-editor-360.json'), JSON.stringify({ row: m, block }, null, 2));
    await row.screenshot({ path: path.join(SHOTS, 'admin-editor-360.png') });
  } finally {
    await ctx.close();
  }
});
