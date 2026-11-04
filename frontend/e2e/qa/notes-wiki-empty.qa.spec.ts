// QA-LI · LI-3 — «Вики пока пустая»: пустое состояние БЕЗ кнопки «Завести вики»,
// и LI-2 — тумблер ночного разбора заводит вику сам.
//
// Стэнд со свежим каталогом данных: вики не заведена. Спек наращивает состояние по ходу:
// сначала пустое состояние (ничего не создаёт), потом включение ночи — оно заводит вику.
//
// Сценарий 3 (перенос заметки, разбор чата, правка страницы) проверяется на уровне REST
// в соседнем спеке notes-wiki-auto-init.qa.spec.ts — там у каждого сценария свой владелец
// (вики заводится один раз на всю жизнь). Здесь — то, что видно человеку.
//
// Рецепт — docs/operations/qa-stand.md.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { existsSync, mkdirSync, readFileSync, rmSync } from 'node:fs';
import path from 'node:path';
import { api, collectConsoleErrors, login, type Api } from './helpers';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5500';
const NOTE_TITLE = 'Заметка для проверки LI-3';
// Порядок кнопок рельсы — реестр wikiTabs.tsx
const RAIL_IDS = ['wiki-rail-pages', 'wiki-rail-inbox', 'wiki-rail-journal', 'wiki-rail-health', 'wiki-rail-settings'];

test.describe.configure({ mode: 'serial' });

function uiErrors(sink: string[]): string[] {
  return sink.filter(t => !t.includes('net::ERR_') && !t.includes('Failed to load resource'));
}

let request: APIRequestContext;
let token: string;
let client: Api;
let noteId = '';

async function setFlags(unified: boolean, wiki = true): Promise<void> {
  await client.put('/api/feature-flags/llm-wiki', { enabled: wiki });
  await client.put('/api/feature-flags/notes-wiki-unified', { enabled: unified });
}

// Каталог данных стенда — из его appsettings.Local.json (DataPath — файл projects.json).
function dataDir(): string {
  const cfgPath = path.resolve(process.cwd(), '../../..', 'backend/ClaudeHomeServer/appsettings.Local.json');
  expect(existsSync(cfgPath), 'у стенда должен быть appsettings.Local.json с DataPath').toBeTruthy();
  const cfg = JSON.parse(readFileSync(cfgPath, 'utf8')) as { DataPath?: string };
  return path.dirname(path.resolve(cfg.DataPath!));
}

async function ownerId(): Promise<string> {
  const me = await client.get<{ userId: string }>('/api/auth/me');
  return me.userId;
}

const noteRow = (p: Page) => p.locator('[data-testid="unified-row"][data-kind="note"]', { hasText: NOTE_TITLE });

async function closeToasts(page: Page): Promise<void> {
  const closes = page.locator('button[title="Закрыть"]');
  for (let i = await closes.count(); i > 0; i--) {
    try { await closes.first().click({ timeout: 1000 }); } catch { break; }
  }
}

async function gotoNotes(page: Page): Promise<void> {
  await page.goto(BASE + '/#/notes');
  await page.reload();
  await page.waitForTimeout(1500);
  await closeToasts(page);
  const inSection = await page.getByRole('button', { name: 'Вики', exact: true }).count()
    + await page.getByTestId('unified-filter').count();
  if (inSection === 0) {
    const nav = page.getByRole('button', { name: 'Заметки', exact: true }).first();
    if (await nav.count()) { await nav.click(); await page.waitForTimeout(1500); }
  }
}

async function openNotesMode(page: Page): Promise<void> {
  await gotoNotes(page);
  await expect(page.getByTestId('wiki-unified-tree')).toBeVisible({ timeout: 30000 });
  await expect(noteRow(page).first()).toBeVisible({ timeout: 30000 });
}

const SHOTS = path.resolve(process.cwd(), '../../.cc-attachments/notes-wiki');
async function shot(p: Page, name: string): Promise<void> {
  mkdirSync(SHOTS, { recursive: true });
  await p.screenshot({ path: path.join(SHOTS, name) });
}

async function docOverflow(p: Page): Promise<number> {
  return p.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
}

test.beforeAll(async ({ playwright }) => {
  request = await playwright.request.newContext({ baseURL: BASE });
  token = await login(request);
  client = api(request, token);
  await setFlags(true);
  // Стенд обязан быть свежим: вики не заведена.
  const tree = await client.get<{ initialized: boolean }>('/api/wiki/tree');
  expect(tree.initialized, 'стенд должен быть со свежим каталогом данных: вики не заведена').toBeFalsy();
  // Своя заметка — ради режима заметок (перенос в вики проверяется в соседнем спеке).
  const note = await client.post<{ id?: string }>('/api/notes', {
    title: NOTE_TITLE, source: 'personal', content: 'Текст заметки для проверки пустого состояния вики.',
  });
  noteId = note?.id ?? '';
});

test.afterAll(async () => {
  try { if (noteId) await client.del(`/api/notes/${noteId}`); } catch { /* best-effort */ }
  await request?.dispose();
});

// ─── LI-3: пустое состояние без кнопки «Завести вики» (новая раскладка) ───

test('LI-3: пустая вики — «Вики пока пустая» с пояснением, кнопки «Завести вики» нет нигде', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await openNotesMode(page);

  // «Страницы» — центр вики: пустое состояние LI-3.
  // Сначала читаем настройки (панель «Настройки»): вторая строка пояснения появляется,
  // когда снимок настроек прочитан.
  await page.getByTestId('wiki-rail-settings').click();
  await expect(page.getByTestId('wiki-panel'), 'панель настроек открылась').toBeVisible();
  await page.getByTestId('wiki-rail-pages').click();
  await expect(page.getByTestId('wiki-empty'), 'пустое состояние вики показано').toBeVisible({ timeout: 30000 });
  await expect(page.getByText('Вики пока пустая')).toBeVisible();
  await expect(page.getByText('Она наполнится сама, когда вы разберёте чат или перенесёте заметку.'))
    .toBeVisible();
  await expect(page.getByText('Ночной разбор выключен — его можно включить в настройках вики.'))
    .toBeVisible({ timeout: 15000 });

  // Кнопки «Завести вики» нет нигде: ни по testid, ни текстом
  await expect(page.getByTestId('wiki-init-btn'), 'кнопки заведения вики быть не должно').toHaveCount(0);
  await expect(page.getByText('Завести вики'), 'слов «Завести вики» на экране быть не должно').toHaveCount(0);
  await shot(page, 'qali-01-empty-wiki-no-button-1280.png');

  // В режиме заметок кнопки тоже нет
  await page.getByRole('button', { name: 'Заметки', exact: true }).first().click();
  await page.waitForTimeout(800);
  await expect(page.getByTestId('wiki-init-btn')).toHaveCount(0);
  await expect(page.getByText('Завести вики')).toHaveCount(0);

  // Пустое состояние ничего не завело
  const tree = await client.get<{ initialized: boolean }>('/api/wiki/tree');
  expect(tree.initialized, 'пустое состояние не должно заводить вики').toBeFalsy();

  expect(await docOverflow(page), 'горизонтального переполнения нет').toBeLessThanOrEqual(1);
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── LI-3: пустое состояние без кнопки и в прежней раскладке ───

test('LI-3: прежняя раскладка (без «Заметки и Вики») — пустое состояние тоже без кнопки', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(false, true); // llm-wiki включён, notes-wiki-unified выключен
  await gotoNotes(page);

  // Прежняя раскладка: вкладки вики в центре
  await expect(page.getByRole('button', { name: 'Вики', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Вики', exact: true }).click();
  await page.waitForTimeout(1000);

  // Пустое состояние LI-3 — и здесь без кнопки
  await expect(page.getByTestId('wiki-empty'), 'пустое состояние вики показано').toBeVisible({ timeout: 30000 });
  await expect(page.getByText('Вики пока пустая')).toBeVisible();
  await expect(page.getByTestId('wiki-init-btn'), 'кнопки заведения вики быть не должно').toHaveCount(0);
  await expect(page.getByText('Завести вики')).toHaveCount(0);
  await shot(page, 'qali-02-old-layout-empty-no-button-1280.png');

  // Прежних вкладок вики на месте (рельсы и единого дерева нет — это прежняя раскладка)
  for (const t of ['Страницы', 'Входящие', 'Журнал', 'Здоровье', 'Настройки']) {
    await expect(page.getByRole('button', { name: t, exact: true })).toBeVisible();
  }
  await expect(page.getByTestId('wiki-rail')).toHaveCount(0);

  await setFlags(true);
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── Регресс: чтение и PUT только классов вику не заводят ───

test('LI регресс: GET дерева и настроек, PUT только классов — вики не заведена', async () => {
  const tree = await client.get<{ initialized: boolean }>('/api/wiki/tree');
  expect(tree.initialized).toBeFalsy();
  await client.get('/api/wiki/settings');

  await client.put('/api/wiki/settings', { autoApplyEnabled: true });
  await client.put('/api/wiki/settings', { class: 'note×create', enable: false });

  const tree2 = await client.get<{ initialized: boolean }>('/api/wiki/tree');
  expect(tree2.initialized, 'чтение и PUT только классов не должны заводить вики').toBeFalsy();
  const me = await ownerId();
  expect(existsSync(path.join(dataDir(), 'wiki', me, 'pages', '_schema.md')),
    'на диске шаблона вики быть не должно').toBeFalsy();
});

// ─── LI-2: тумблер ночного разбора заводит вику ───

test('LI-2: тумблер ночного разбора заводит вику — дерево разделов появляется', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await openNotesMode(page);

  // Панель «Настройки» → «Ночной разбор» → тумблер
  await page.getByTestId('wiki-rail-settings').click();
  const panel = page.getByTestId('wiki-panel');
  await expect(panel).toBeVisible();
  await expect(panel.getByTestId('wiki-nightly-switch')).toContainText('Разбирать по ночам');
  const toggle = panel.getByRole('switch', { name: 'Разбирать по ночам' });
  await expect(toggle).toHaveAttribute('aria-checked', 'false');
  await shot(page, 'qali-03-settings-nightly-before-1280.png');

  await toggle.click();

  // Вики заведена: это видно и в интерфейсе (дерево разделов), и по данным
  await expect
    .poll(async () => (await client.get<{ initialized: boolean }>('/api/wiki/tree')).initialized,
      { timeout: 30000, message: 'тумблер ночного разбора должен завести вики' })
    .toBe(true);
  const me = await ownerId();
  expect(existsSync(path.join(dataDir(), 'wiki', me, 'pages', '_schema.md')),
    'шаблон вики (pages/_schema.md) должен появиться на диске').toBeTruthy();

  // Дерево разделов появилось (постоянные разделы — «Личное» и «Общее»)
  await page.waitForTimeout(1500);
  await expect(page.locator('[data-testid="unified-group"]', { hasText: 'Личное' }).first()).toBeVisible();
  await expect(page.locator('[data-testid="unified-group"]', { hasText: 'Общее' }).first()).toBeVisible();
  await shot(page, 'qali-04-after-nightly-toggle-1280.png');

  // Кнопки «Завести вики» по-прежнему нет — вики завелась сама, заводить её не нужно
  await expect(page.getByTestId('wiki-init-btn')).toHaveCount(0);
  await expect(page.getByText('Завести вики')).toHaveCount(0);

  expect(await docOverflow(page), 'горизонтального переполнения нет').toBeLessThanOrEqual(1);
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── LI-2 (UI): «Перенести в вики» проходит без отказа «вики не заведена» ───

test('LI-2: «Перенести в вики» на заметке проходит — отказа «вики не заведена» на экране нет', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await openNotesMode(page);

  await noteRow(page).first().click();
  const transferBtn = page.getByRole('button', { name: 'Перенести в вики' });
  await expect(transferBtn, 'кнопка переноса на заметке есть').toBeVisible();
  await transferBtn.click();

  // Подтверждение переноса (если есть) — до него отказа быть не может
  const confirm = page.getByRole('button', { name: /перенести/i }).last();
  if (await confirm.count()) {
    await confirm.click().catch(() => undefined);
  }
  await page.waitForTimeout(2500);

  // Отказа «вики не заведена» на экране нет
  await expect(page.getByText(/вики.*не заведена/i)).toHaveCount(0);
  await expect(page.getByText(/не заведена/i)).toHaveCount(0);
  await shot(page, 'qali-05-transfer-note-1280.png');

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});
