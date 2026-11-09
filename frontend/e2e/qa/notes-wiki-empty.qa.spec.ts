// QA-LI · LI-3 — «Вики пока пустая»: пустое состояние БЕЗ кнопки «Завести вики»,
// и LI-2 — вики заводится сама (ночной разбор, перенос заметки).
//
// Сценарии разведены по владельцам: вики заводится один раз на всю жизнь, поэтому
// «незаведённая вики» у одного и того же владельца получить нельзя. Учётки создаёт
// админ через POST /api/users, свой пароль у каждой (≥8 символов). Мусор убирается
// в afterAll: учётки удаляются, каталоги вики сносятся с диска.
//
// Пустое состояние проверяется по центру (WikiEmpty: «Вики пока пустая», без кнопки
// «Завести вики») и по рельсе зон: кнопки панелей вики есть, а счётчиков на них нет.
// Прежних самодельных testid вики больше нет — смотрим на штатную рельсу зон
// (data-rail-item) и карточки панелей.
//
// Рецепт — docs/operations/qa-stand.md.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { existsSync, mkdirSync, readFileSync, rmSync } from 'node:fs';
import path from 'node:path';
import { api, collectConsoleErrors, login, type Api } from './helpers';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5500';
const USER_PASS = 'qa-lI-empty-2026';
const NOTE_TITLE = 'Заметка для проверки LI-3';

test.describe.configure({ mode: 'serial' });

function uiErrors(sink: string[]): string[] {
  return sink.filter(t => !t.includes('net::ERR_') && !t.includes('Failed to load resource'));
}

// ─── Владельцы ───

interface Owner {
  name: string;
  id: string;
  token: string;
  client: Api;
}

let request: APIRequestContext;
let admin: Api;
const owners: Owner[] = [];

function repoRoot(): string {
  return path.resolve(process.cwd(), '../../..');
}

// Каталог данных стенда — из его appsettings.Local.json (DataPath — файл projects.json).
function dataDir(): string {
  const cfgPath = path.join(repoRoot(), 'backend/ClaudeHomeServer/appsettings.Local.json');
  expect(existsSync(cfgPath), 'у стенда должен быть appsettings.Local.json с DataPath').toBeTruthy();
  const cfg = JSON.parse(readFileSync(cfgPath, 'utf8')) as { DataPath?: string };
  expect(cfg.DataPath, 'в appsettings.Local.json стенда должен быть DataPath').toBeTruthy();
  return path.dirname(path.resolve(cfg.DataPath!));
}

function wikiRoot(ownerId: string): string {
  return path.join(dataDir(), 'wiki', ownerId);
}

function wikiInitialized(ownerId: string): boolean {
  return existsSync(path.join(wikiRoot(ownerId), 'pages', '_schema.md'));
}

async function loginAs(name: string): Promise<string> {
  const r = await request.post('/api/auth/login', { data: { username: name, password: USER_PASS } });
  expect(r.ok(), `логин ${name} должен пройти (${r.status()})`).toBeTruthy();
  const body = await r.json() as { token: string };
  return body.token;
}

// Новый владелец: учётка, вход, флаги вики.
async function makeOwner(baseName: string, unified = true): Promise<Owner> {
  const name = `${baseName}_${Date.now().toString(36)}`.slice(0, 32);
  const created = await admin.post<{ id: string; username: string }>('/api/users', {
    username: name, password: USER_PASS, role: 'user',
  });
  expect(created?.id, `POST /api/users должен был создать ${name}`).toBeTruthy();
  const token = await loginAs(name);
  const client = api(request, token);
  const owner: Owner = { name, id: created.id, token, client };
  owners.push(owner);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });
  await client.put('/api/feature-flags/notes-wiki-unified', { enabled: unified });
  return owner;
}

// ─── Хелперы страницы ───

// Кнопка панели вики в рельсе зон (ключ из panelCatalog).
const railButton = (p: Page, key: string) => p.locator(`[data-rail-item="${key}"] button`);

// Карточка панели: заголовок в шапке островка ищется по стилю заголовка.
function panelCard(p: Page, title: string) {
  return p.locator(
    `xpath=//span[contains(@style,"font-weight: 600") and normalize-space()="${title}"]` +
    `/ancestor::div[contains(@style,"flex-direction: column")][1]`);
}

// Числа на кнопках панелей вики в рельсе (счётчики) — у пустой вики их нет.
async function wikiRailNumbers(p: Page): Promise<string[]> {
  return p.evaluate(() => Array.from(document.querySelectorAll('[data-rail-item^="wiki"]'))
    .flatMap(item => Array.from(item.querySelectorAll('span'))
      .map(s => (s.textContent || '').trim())
      .filter(t => /^\d+$/.test(t))));
}

async function closeToasts(page: Page): Promise<void> {
  const closes = page.locator('button[title="Закрыть"]');
  for (let i = await closes.count(); i > 0; i--) {
    try { await closes.first().click({ timeout: 1000 }); } catch { break; }
  }
}

async function gotoNotes(page: Page): Promise<void> {
  await page.goto(BASE + '/#/notes');
  await page.reload();
  await page.waitForTimeout(2000);
  await closeToasts(page);
  // Раздел «Заметки» мог не открыться (перезагрузка сбрасывает маршрут) — открываем руками.
  const inSection = await page.getByTestId('unified-filter').count()
    + await page.getByRole('button', { name: 'Вики', exact: true }).count();
  if (inSection === 0) {
    const nav = page.getByRole('button', { name: 'Заметки', exact: true }).first();
    if (await nav.count()) { await nav.click(); await page.waitForTimeout(2000); await closeToasts(page); }
  }
}

const SHOTS = path.resolve(process.cwd(), '../../.cc-attachments/notes-wiki');
async function shot(p: Page, name: string): Promise<void> {
  mkdirSync(SHOTS, { recursive: true });
  await p.screenshot({ path: path.join(SHOTS, name) });
}

async function docOverflow(p: Page): Promise<number> {
  return p.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
}

// ─── Жизненный цикл ───

test.beforeAll(async ({ playwright }) => {
  request = await playwright.request.newContext({ baseURL: BASE });
  const adminToken = await login(request);
  admin = api(request, adminToken);
});

test.afterAll(async () => {
  for (const o of owners) {
    try { await admin.del(`/api/users/${o.id}`); } catch { /* best-effort */ }
    rmSync(wikiRoot(o.id), { recursive: true, force: true });
  }
  await request?.dispose();
});

// ─── LI-3: пустое состояние без кнопки «Завести вики» (новая раскладка) ───

test('LI-3: пустая вики — «Вики пока пустая» с пояснением, кнопки «Завести вики» нет нигде', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  const o = await makeOwner('qa_li_empty');
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), o.token);
  await gotoNotes(page);

  // Центр вики: пустое состояние LI-3.
  await expect(page.getByTestId('wiki-empty'), 'пустое состояние вики показано').toBeVisible({ timeout: 30000 });
  await expect(page.getByText('Вики пока пустая')).toBeVisible();
  await expect(page.getByText('Она наполнится сама, когда вы разберёте чат или перенесёте заметку.'))
    .toBeVisible();

  // Вторая строка пояснения — по снимку настроек, а он читается открытием панели
  // «Настройки»: сначала открываем её, потом смотрим пустое состояние в центре.
  await railButton(page, 'wikiSettings').click();
  await expect(panelCard(page, 'Настройки'), 'панель настроек открылась').toBeVisible({ timeout: 20000 });
  await expect(page.getByText('Ночной разбор выключен — его можно включить в настройках вики.'))
    .toBeVisible({ timeout: 15000 });
  // В пустом состоянии есть только кнопка «Открыть настройки» — завести вики нечем
  await expect(page.getByTestId('wiki-empty-settings'), 'кнопка «Открыть настройки» есть').toBeVisible();

  // Кнопки «Завести вики» нет нигде: ни по testid, ни текстом
  await expect(page.getByTestId('wiki-init-btn'), 'кнопки заведения вики быть не должно').toHaveCount(0);
  await expect(page.getByText('Завести вики'), 'слов «Завести вики» на экране быть не должно').toHaveCount(0);
  await shot(page, 'qali-01-empty-wiki-no-button-1280.png');

  // Рельса зон: кнопки панелей вики есть, а счётчиков на них нет — вики пуста
  for (const key of ['wikiInbox', 'wikiJournal', 'wikiHealth', 'wikiSettings']) {
    await expect(railButton(page, key), `кнопка панели «${key}» в рельсе`).toBeVisible();
  }
  expect(await wikiRailNumbers(page), 'у пустой вики на рельсе нет ни одного числа').toEqual([]);

  // Пустое состояние ничего не завело
  const tree = await o.client.get<{ initialized: boolean }>('/api/wiki/tree');
  expect(tree.initialized, 'пустое состояние не должно заводить вики').toBeFalsy();
  expect(wikiInitialized(o.id), 'на диске шаблона вики быть не должно').toBeFalsy();

  expect(await docOverflow(page), 'горизонтального переполнения нет').toBeLessThanOrEqual(1);
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── LI-3: пустое состояние без кнопки и в прежней раскладке ───

test('LI-3: прежняя раскладка (без «Заметки и Вики») — пустое состояние тоже без кнопки', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  const o = await makeOwner('qa_li_old', false);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), o.token);
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

  // Прежних вкладок вики на месте, а рельсы панелей вики нет (флажная раскладка)
  for (const t of ['Страницы', 'Входящие', 'Журнал', 'Здоровье', 'Настройки']) {
    await expect(page.getByRole('button', { name: t, exact: true })).toBeVisible();
  }
  await expect(page.locator('[data-rail-item^="wiki"]'), 'правой рельсы панелей вики быть не должно').toHaveCount(0);

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── Регресс: чтение и PUT только классов вику не заводят ───

test('LI регресс: GET дерева и настроек, PUT только классов — вики не заведена', async () => {
  const o = await makeOwner('qa_li_read');

  const tree = await o.client.get<{ initialized: boolean }>('/api/wiki/tree');
  expect(tree.initialized).toBeFalsy();
  await o.client.get('/api/wiki/settings');

  await o.client.put('/api/wiki/settings', { autoApplyEnabled: true });
  await o.client.put('/api/wiki/settings', { class: 'note×create', enable: false });

  const tree2 = await o.client.get<{ initialized: boolean }>('/api/wiki/tree');
  expect(tree2.initialized, 'чтение и PUT только классов не должны заводить вики').toBeFalsy();
  expect(wikiInitialized(o.id), 'на диске шаблона вики быть не должно').toBeFalsy();
});

// ─── LI-2: тумблер ночного разбора заводит вику ───

test('LI-2: тумблер ночного разбора заводит вику — дерево разделов появляется', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  const o = await makeOwner('qa_li_night');
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), o.token);
  await gotoNotes(page);

  // Панель «Настройки» → «Ночной разбор» → тумблер
  await railButton(page, 'wikiSettings').click();
  const card = panelCard(page, 'Настройки');
  await expect(card, 'панель настроек открылась').toBeVisible({ timeout: 20000 });
  await expect(card.getByTestId('wiki-nightly-switch')).toContainText('Разбирать по ночам');
  const toggle = card.getByRole('switch', { name: 'Разбирать по ночам' });
  await expect(toggle).toHaveAttribute('aria-checked', 'false');
  await shot(page, 'qali-03-settings-nightly-before-1280.png');

  await toggle.click();

  // Вики заведена: это видно и в интерфейсе (дерево разделов), и по данным
  await expect
    .poll(async () => (await o.client.get<{ initialized: boolean }>('/api/wiki/tree')).initialized,
      { timeout: 30000, message: 'тумблер ночного разбора должен завести вики' })
    .toBe(true);
  expect(wikiInitialized(o.id), 'шаблон вики (pages/_schema.md) должен появиться на диске').toBeTruthy();

  // Вики заведена — и в интерфейсе пустого состояния вики больше нет.
  // Каталог разделов читается при открытии раздела, поэтому раздел переоткрываем.
  await gotoNotes(page);
  await expect(page.getByTestId('wiki-empty'), 'пустое состояние вики больше не показывается')
    .toHaveCount(0);
  await expect(page.getByText('Вики пока пустая'), 'слов «Вики пока пустая» на экране быть не должно')
    .toHaveCount(0);
  await shot(page, 'qali-04-after-nightly-toggle-1280.png');

  // Кнопки «Завести вики» по-прежнему нет — вики завелась сама, заводить её не нужно
  await expect(page.getByTestId('wiki-init-btn')).toHaveCount(0);
  await expect(page.getByText('Завести вики')).toHaveCount(0);

  expect(await docOverflow(page), 'горизонтального переполнения нет').toBeLessThanOrEqual(1);
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── LI-2 (UI): перенос заметки в вику заводит вику сам ───

test('LI-2: перенос заметки в вику заводит вику — отказа «вики не заведена» на экране нет', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  const o = await makeOwner('qa_li_transfer');
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), o.token);

  const note = await o.client.post<{ id: string }>('/api/notes', {
    title: NOTE_TITLE, source: 'personal', content: 'Текст заметки для проверки переноса в вику.',
  });
  expect(note?.id).toBeTruthy();

  await gotoNotes(page);
  const row = page.locator('[data-testid="unified-row"][data-kind="note"]', { hasText: NOTE_TITLE }).first();
  await expect(row, 'заметка видна в дереве заметок').toBeVisible({ timeout: 30000 });
  await row.click();
  await page.waitForTimeout(1500);
  await closeToasts(page);

  const transferBtn = page.getByRole('button', { name: 'Перенести в вики' });
  await expect(transferBtn, 'кнопка переноса на заметке есть').toBeVisible();
  await transferBtn.click();

  // Подтверждение переноса
  const confirm = page.getByRole('button', { name: 'Перенести', exact: true });
  await expect(confirm, 'диалог подтверждения открылся').toBeVisible({ timeout: 10000 });
  await confirm.click();

  // Вики завелась сама — и отказа «вики не заведена» на экране нет
  await expect
    .poll(async () => (await o.client.get<{ initialized: boolean }>('/api/wiki/tree')).initialized,
      { timeout: 30000, message: 'перенос заметки должен завести вику' })
    .toBe(true);
  await expect(page.getByText(/не заведена/i)).toHaveCount(0);
  await expect(page.getByText('Завести вики')).toHaveCount(0);
  await shot(page, 'qali-05-transfer-note-1280.png');

  await o.client.del(`/api/notes/${note.id}`);
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});
