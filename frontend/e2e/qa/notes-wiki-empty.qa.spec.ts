// QA-5: повторная проверка дефекта 9817046a (TL-8) — рельса и панель вики доступны из режима
// заметок и при пустой вики (фикс 5a12368dd, ветка feat/notes-wiki-unified).
//
// Стенд — со СВЕЖИМ каталогом данных: вики не заведена, чужих фикстур нет (reseed прежнего
// спека здесь не вызывается намеренно). Спек сам наращивает состояние по ходу: сначала пустая
// вики, затем «Завести вики», затем своя страница для пробы переключения заметка ↔ вики.
// Свой мусор убирает afterAll. Рецепт — docs/operations/qa-stand.md.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { api, collectConsoleErrors, login, type Api } from './helpers';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5500';
const NOTE_TITLE = 'Заметка для проверки QA5';
const WIKI_PAGE = 'Страница для проверки QA5';
// Порядок кнопок рельсы — реестр wikiTabs.tsx; «Страницы» добавлена фиксом TL-8
const RAIL_IDS = ['wiki-rail-pages', 'wiki-rail-inbox', 'wiki-rail-journal', 'wiki-rail-health', 'wiki-rail-settings'];

test.describe.configure({ mode: 'serial' });

// Ошибки консоли без сетевого шума стенда — то же правило, что в прежнем спеке:
// обрыв соединения и 4xx-ответы («Failed to load resource») экранами не проверяются.
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

// Каталог данных этого билета — тот же путь, что читает прежний спек. Стенд с другим
// DataPath своих файлов не трогает.
async function dataRoot(): Promise<string> {
  const me = await client.get<{ userId: string }>('/api/auth/me');
  const dataDir = path.resolve(process.cwd(), '../../..', 'backend/ClaudeHomeServer/bin/Debug/net10.0/data');
  if (!existsSync(dataDir)) return '';
  return path.join(dataDir, 'wiki', me.userId);
}

const noteRow = (p: Page) => p.locator('[data-testid="unified-row"][data-kind="note"]', { hasText: NOTE_TITLE });
const wikiRow = (p: Page) => p.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: WIKI_PAGE });

// Тосты перекрывают кнопки на узких экранах — перед кликами закрываем их штатной кнопкой.
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
  // Раздел мог не открыться после reload — идём по навигации, как человек
  const inSection = await page.getByRole('button', { name: 'Вики', exact: true }).count()
    + await page.getByTestId('unified-filter').count();
  if (inSection === 0) {
    const nav = page.getByRole('button', { name: 'Заметки', exact: true }).first();
    if (await nav.count()) { await nav.click(); await page.waitForTimeout(1500); }
  }
}

// Открыть «Заметки» в режиме заметок. При пустой вики строк страниц вики в дереве нет —
// ждём свою строку-заметку: она и есть рабочее состояние этого спека.
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

// Своя страница для пробы переключения: обычная topic-страница без сносок
// (авторство по умолчанию «ai» — на проверку не влияет).
const PAGE_TEXT = `---
type: topic
aliases: []
owns: []
sources: []
status: active
---

# ${WIKI_PAGE}

## Сводка
Утверждение про проверку переключения режимов.

## Хронология

## Противоречия

## История
`;

async function writeMyPage(): Promise<void> {
  const root = await dataRoot();
  if (!root) throw new Error('каталог данных стенда недоступен — сценарий не выполним');
  const pages = path.join(root, 'pages', 'Личное');
  mkdirSync(pages, { recursive: true });
  writeFileSync(path.join(pages, `${WIKI_PAGE}.md`), PAGE_TEXT, 'utf8');
  const manifestPath = path.join(root, 'keeper/manifest/personal.json');
  const manifest = existsSync(manifestPath)
    ? JSON.parse(readFileSync(manifestPath, 'utf8')) as Record<string, string>
    : {};
  manifest[`Личное/${WIKI_PAGE}.md`] = createHash('sha256').update(PAGE_TEXT, 'utf8').digest('hex');
  writeFileSync(manifestPath, JSON.stringify(manifest), 'utf8');
}

test.beforeAll(async ({ playwright }) => {
  request = await playwright.request.newContext({ baseURL: BASE });
  token = await login(request);
  client = api(request, token);
  await setFlags(true);
  // Стенд обязан быть свежим: вики не заведена. Иначе «пустая вики» проверялась бы
  // на чужом состоянии, а не на том, что описывает дефект.
  const tree = await client.get<{ initialized: boolean }>('/api/wiki/tree');
  expect(tree.initialized, 'стенд должен быть со свежим каталогом данных: вики не заведена').toBeFalsy();
  // Своя заметка — ради «режима заметок» и переключения. Фикстур вики не заводим вовсе.
  const note = await client.post<{ id?: string }>('/api/notes', {
    title: NOTE_TITLE, source: 'personal', content: 'Текст заметки для проверки рельсы при пустой вики.',
  });
  noteId = note?.id ?? '';
});

test.afterAll(async () => {
  // Убираю за собой: заметку (API; и файл на диске, если id не вернулся),
  // свою страницу вместе с её записью из манифеста — иначе файл и манифест разойдутся.
  try { if (noteId) await client.del(`/api/notes/${noteId}`); } catch { /* best-effort */ }
  const root = await dataRoot();
  if (root) {
    rmSync(path.join(root, 'pages', 'Личное', `${WIKI_PAGE}.md`), { force: true });
    const manifestPath = path.join(root, 'keeper/manifest/personal.json');
    if (existsSync(manifestPath)) {
      const manifest = JSON.parse(readFileSync(manifestPath, 'utf8')) as Record<string, string>;
      if (delete manifest[`Личное/${WIKI_PAGE}.md`]) writeFileSync(manifestPath, JSON.stringify(manifest), 'utf8');
    }
    if (!noteId) {
      // id не вернулся — сносим файл заметки по заголовку из личного vaulta
      const userId = path.basename(root);
      const notesDir = path.join(path.dirname(path.dirname(root)), 'notes', userId);
      if (existsSync(notesDir)) {
        for (const f of readdirSync(notesDir)) {
          if (f === `${NOTE_TITLE}.md`) rmSync(path.join(notesDir, f), { force: true });
        }
      }
    }
  }
  await request?.dispose();
});

// ─── П.5: без флага совместной правки — прежняя раскладка (проверяем первой, пока вики пуста) ───

test('QA5 п.5: без notes-wiki-unified — прежние вкладки в центре, рельсы нет; без llm-wiki — нет входа в «Вики»', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(false, true); // llm-wiki включён, notes-wiki-unified выключен
  await gotoNotes(page);

  // Прежний переключатель с входом в «Вики» — на месте
  await expect(page.getByRole('button', { name: 'Вики', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Вики', exact: true }).click();
  await page.waitForTimeout(800);

  // Прежняя раскладка: вкладки в центре вики, единого дерева и рельсы нет
  for (const t of ['Страницы', 'Входящие', 'Журнал', 'Здоровье', 'Настройки']) {
    await expect(page.getByRole('button', { name: t, exact: true })).toBeVisible();
  }
  await expect(page.getByTestId('wiki-rail')).toHaveCount(0);
  await expect(page.getByTestId('wiki-unified-tree')).toHaveCount(0);

  // Пустая вики достижима и в прежней раскладке: «Вики не заведена» с кнопкой «Завести вики»
  await expect(page.getByText('Вики не заведена')).toBeVisible();
  await expect(page.getByTestId('wiki-init-btn')).toBeVisible();
  await shot(page, 'qa5-01-old-layout-empty-wiki-1280.png');

  // Без llm-wiki — входа в «Вики» нет вовсе
  await setFlags(false, false);
  await gotoNotes(page);
  await expect(page.getByRole('button', { name: 'Вики', exact: true })).toHaveCount(0);

  await setFlags(true);
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── П.1: пустая вики, оба флага, десктоп — рельса в режиме заметок ───

test('QA5 п.1: пустая вики, оба флага, 1280 — рельса видна в режиме заметок, ровно одна, пять кнопок', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await openNotesMode(page);

  // Режим заметок: центр — не вики, «Завести вики» здесь нет
  await expect(page.getByTestId('wiki-init-btn')).toHaveCount(0);
  await expect(page.getByTestId('wiki-doc')).toHaveCount(0);

  // Рельса видна и ровно одна; кнопки — все пять по порядку реестра
  await expect(page.getByTestId('wiki-rail')).toHaveCount(1);
  for (const id of RAIL_IDS) await expect(page.getByTestId(id)).toBeVisible();
  await expect(page.getByTestId('wiki-rail-pages')).toHaveAttribute('aria-pressed', 'false');

  // Постоянные разделы бэк отдаёт всегда (ADR-036, решение 1): в дереве они есть и до
  // заведённой вики — фикс не «добавляет разделы», он даёт вход в центр вики
  await expect(page.locator('[data-testid="unified-group"]', { hasText: 'Личное' }).first()).toBeVisible();
  await expect(page.locator('[data-testid="unified-group"]', { hasText: 'Общее' }).first()).toBeVisible();

  // Открытая заметка — рельса остаётся (ровно этого не было в дефекте 9817046a)
  await noteRow(page).first().click();
  await expect(page.getByRole('button', { name: 'Перенести в вики' })).toBeVisible();
  await expect(page.getByTestId('wiki-rail')).toHaveCount(1);
  await expect(page.getByTestId('wiki-rail-pages')).toHaveAttribute('aria-pressed', 'false');
  await shot(page, 'qa5-02-rail-in-notes-empty-wiki-1280.png');

  expect(await docOverflow(page), 'горизонтального переполнения нет').toBeLessThanOrEqual(1);
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── П.2: панели из режима заметок при пустой вики ───

test('QA5 п.2: из режима заметок панели открываются и закрываются без ошибки (пустая вики)', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await openNotesMode(page);

  // «Из режима заметок (открыта заметка)» — открываем свою заметку
  await noteRow(page).first().click();
  await expect(page.getByRole('button', { name: 'Перенести в вики' })).toBeVisible();

  // «Настройки»: блок «Ночной разбор» — тумблер, окно, лимиты
  await page.getByTestId('wiki-rail-settings').click();
  const settings = page.getByTestId('wiki-panel');
  await expect(settings).toBeVisible();
  await expect(settings).toContainText('Ночной разбор');
  await expect(settings.getByTestId('wiki-nightly-switch')).toContainText('Разбирать по ночам');
  await expect(settings.getByTestId('wiki-nightly-window')).toBeVisible();
  await expect(settings.getByTestId('wiki-nightly-input-inputTokenBudget')).toBeVisible();
  await expect(settings.getByTestId('wiki-nightly-input-maxPackagesPerNight')).toBeVisible();
  await expect(settings.getByTestId('wiki-nightly-input-maxPackagesPerSection')).toBeVisible();
  // Пустая вики — «Прошлая ночь» пуста: пустое состояние допустимо
  await expect(settings.getByTestId('wiki-nightly-none')).toBeVisible();
  await shot(page, 'qa5-03-settings-nightly-empty-wiki-1280.png');
  // Повторный клик закрывает
  await page.getByTestId('wiki-rail-settings').click();
  await expect(page.getByTestId('wiki-panel')).toHaveCount(0);

  // «Входящие», «Журнал», «Здоровье» — открываются без ошибки и без краша; пустые состояния допустимы
  for (const [id, label] of [['wiki-rail-inbox', 'Входящие'], ['wiki-rail-journal', 'Журнал'], ['wiki-rail-health', 'Здоровье']] as const) {
    await page.getByTestId(id).click();
    const panel = page.getByTestId('wiki-panel');
    await expect(panel, `панель «${label}» открылась`).toBeVisible();
    await expect(panel).toContainText(label);
    await expect(panel.getByTestId('wiki-panel-body')).toBeVisible();
    // Отказа на панели нет: пустая вики — пустое состояние, не ошибка
    await expect(panel).not.toContainText('Не удалось');
    await page.getByTestId(id).click();
    await expect(panel, `повторный клик закрыл «${label}»`).toHaveCount(0);
  }
  await shot(page, 'qa5-04-panels-closed-empty-wiki-1280.png');

  expect(await docOverflow(page), 'горизонтального переполнения нет').toBeLessThanOrEqual(1);
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── П.1 (вторая половина): «Страницы» → «Завести вики» ───

test('QA5 п.1: «Страницы» ведут к «Вики не заведена»; кнопка заводит вики', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await openNotesMode(page);

  // Клик «Страницы» — центр показывает пустое состояние вики с кнопкой
  await page.getByTestId('wiki-rail-pages').click();
  await expect(page.getByTestId('wiki-doc')).toBeVisible();
  await expect(page.getByText('Вики не заведена')).toBeVisible();
  const initBtn = page.getByTestId('wiki-init-btn');
  await expect(initBtn).toBeVisible();
  await expect(page.getByTestId('wiki-rail-pages')).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('wiki-rail')).toHaveCount(1);
  await shot(page, 'qa5-05-empty-wiki-center-1280.png');

  // Кнопка заводит вики: EmptyWiki уходит, бэк подтверждает заведённость
  await initBtn.click();
  await expect(initBtn).toHaveCount(0, { timeout: 15000 });
  await expect(page.getByText('Вики не заведена')).toHaveCount(0);
  const tree = await client.get<{ initialized: boolean; sections: { key: string }[] }>('/api/wiki/tree');
  expect(tree.initialized, 'POST /api/wiki/initialize завёл вики').toBeTruthy();
  expect(tree.sections.map(s => s.key), 'постоянные разделы на месте').toEqual(expect.arrayContaining(['personal', 'common']));
  // Дерево показывает разделы
  await expect(page.locator('[data-testid="unified-group"]', { hasText: 'Личное' }).first()).toBeVisible();
  await expect(page.locator('[data-testid="unified-group"]', { hasText: 'Общее' }).first()).toBeVisible();
  await shot(page, 'qa5-06-after-init-1280.png');

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── П.3: переключение заметка ↔ вики — панель не сбрасывается, рельса одна ───

test('QA5 п.3: переключение заметка ↔ вики — открытая панель не сбрасывается, рельса без дубля', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);

  // Своя страница на диске + запись в манифесте — чтобы в дереве было на что кликнуть
  await writeMyPage();
  await openNotesMode(page);
  await expect(wikiRow(page).first(), 'своя страница появилась в дереве').toBeVisible({ timeout: 30000 });

  // Открытая панель («Журнал») переживает переход в вики и обратно
  await page.getByTestId('wiki-rail-journal').click();
  await expect(page.getByTestId('wiki-panel')).toBeVisible();

  await wikiRow(page).first().click();
  await expect(page.getByTestId('wiki-page')).toBeVisible({ timeout: 30000 });
  await expect(page.getByTestId('wiki-page')).toContainText(WIKI_PAGE);
  await expect(page.getByTestId('wiki-panel'), 'панель не сбросилась при переходе в вики').toBeVisible();
  await expect(page.getByTestId('wiki-rail'), 'рельса ровно одна в режиме «Вики»').toHaveCount(1);
  await expect(page.getByTestId('wiki-rail-journal')).toHaveAttribute('aria-pressed', 'true');
  await shot(page, 'qa5-07-wiki-mode-panel-kept-1280.png');

  await noteRow(page).first().click();
  await expect(page.getByTestId('wiki-page')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Перенести в вики' })).toBeVisible();
  await expect(page.getByTestId('wiki-panel'), 'панель не сбросилась при возврате к заметке').toBeVisible();
  await expect(page.getByTestId('wiki-rail'), 'рельса ровно одна в режиме заметок').toHaveCount(1);
  await shot(page, 'qa5-08-note-mode-panel-kept-1280.png');

  // Ещё раз в вики — дубля рельсы нет
  await wikiRow(page).first().click();
  await expect(page.getByTestId('wiki-page')).toBeVisible({ timeout: 30000 });
  await expect(page.getByTestId('wiki-rail')).toHaveCount(1);

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── П.4: мобильный 360 — переключатель на месте, шторки как раньше, рельсы у края нет ───

test('QA5 п.4: 360 — переключатель «Заметки | Вики | Граф» на месте, шторки работают, рельсы нет', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const p = await ctx.newPage();
  const errors = collectConsoleErrors(p);
  await setFlags(true);

  await p.setViewportSize({ width: 360, height: 800 });
  await p.goto(BASE + '/#/notes');
  await p.reload();
  await p.waitForTimeout(1800);
  await closeToasts(p);

  // Переключатель режимов на месте
  for (const t of ['Заметки', 'Вики', 'Граф']) {
    await expect(p.getByRole('button', { name: t, exact: true }).first(), `переключатель «${t}»`).toBeVisible();
  }
  // Рельсы у края нет
  await expect(p.getByTestId('wiki-rail')).toHaveCount(0);

  // Шторка дерева: «Вики» → дерево → выбор страницы открывает её и закрывает шторку
  await p.getByRole('button', { name: 'Вики', exact: true }).first().click();
  await p.waitForTimeout(600);
  await expect(p.getByTestId('wiki-rail'), 'в режиме «Вики» рельсы всё равно нет').toHaveCount(0);
  await p.getByTestId('wiki-open-tree').click();
  await expect(p.getByTestId('wiki-unified-tree')).toBeVisible({ timeout: 20000 });
  await expect(p.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: WIKI_PAGE }).first()).toBeVisible({ timeout: 20000 });
  await p.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: WIKI_PAGE }).first().click();
  await expect(p.getByTestId('wiki-page'), 'страница открылась').toBeVisible({ timeout: 20000 });
  await expect(p.getByTestId('wiki-drawer-overlay'), 'шторка закрылась').toHaveCount(0);
  await shot(p, 'qa5-09-mobile-page-360.png');

  // Шторка панели: «Входящие» открывается и без заведённых данных (пустое состояние допустимо)
  await p.getByTestId('wiki-open-panel').click();
  await p.waitForTimeout(400);
  await p.getByRole('button', { name: 'Журнал', exact: true }).first().click();
  await expect(p.getByTestId('wiki-panel-body'), 'панель в шторке открылась').toBeVisible({ timeout: 20000 });
  await expect(p.getByTestId('wiki-rail'), 'рельсы у края по-прежнему нет').toHaveCount(0);
  await shot(p, 'qa5-10-mobile-panel-360.png');

  expect(await docOverflow(p), 'горизонтального переполнения на 360 нет').toBeLessThanOrEqual(1);
  await ctx.close();
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});
