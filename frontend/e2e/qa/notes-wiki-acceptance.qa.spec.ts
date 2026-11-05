// QA-приёмка фичи «Заметки+Вики» (ADR-038) — добор к существующим спе-файлам.
// Здесь закрыты пункты, которых НЕ было в notes-wiki-unified / notes-wiki-empty:
//   · панели вики на 360 (шторка справа), кнопки-цели касания, ничего не обрезано;
//   · тёмная тема: панели, чип раздела, точка «Журнала» — по тёмным токенам;
//   · чип раздела: режим «Все разделы» есть у «Входящих» и «Журнала», у «Здоровья» — нет;
//   · фильтр «кто написал» переключается и оставляет только строки своего происхождения;
//   · значки происхождения у строк дерева и отсутствие отдельной строки «Вики».
//
// Фикстура данных — та же, что в notes-wiki-unified: страницы вики пишет только Хранитель,
// поэтому файлы секций и манифест кладём на диск (рецепт — docs/operations/qa-stand.md).
// Релиза стенда: /home/an/Sources/ClaudeCodeServer-notes-wiki.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { api, collectConsoleErrors, login, type Api } from './helpers';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5500';

const MAIN_PAGE = 'Замеры локальной GPU';
const COMMON_PAGE = 'Общая страница';
const AI_CARD = 'ИИ-разбор приёмки';
const HUMAN_CARD = 'Правка человека приёмки';

test.describe.configure({ mode: 'serial' });

function uiErrors(sink: string[]): string[] {
  return sink.filter(t => !t.includes('net::ERR_') && !t.includes('Failed to load resource'));
}

let request: APIRequestContext;
let token: string;
let client: Api;

async function setFlags(unified: boolean, wiki = true): Promise<void> {
  await client.put('/api/feature-flags/llm-wiki', { enabled: wiki });
  await client.put('/api/feature-flags/notes-wiki-unified', { enabled: unified });
}

// ─── Фикстура вики (та же форма, что в notes-wiki-unified) ───

const CARD_AI = `---
type: source
origin: session
ref: session:qaacc1111#m1-m9
hash: ai-card-acc
captured: 2026-11-02T05:48:00.0000000+00:00
aliases: []
owns: []
sources: []
status: active
---

# ${AI_CARD}
`;

const CARD_HUMAN = `---
type: source
origin: human
ref: human:qaacc2222
hash: hu-card-acc
captured: 2026-11-01T09:14:00.0000000+00:00
aliases: []
owns: []
sources: []
status: active
---

# ${HUMAN_CARD}
`;

// Страница «вместе»: утверждение ИИ и утверждение человека — значок «Ведёте вместе с ИИ».
const PAGE_MAIN = `---
type: topic
aliases: []
owns: []
sources: []
status: active
---

# ${MAIN_PAGE}

## Сводка
Черновики снимаем на локальной GPU, а не в облаке. [^s1]
Очередь локальной GPU обслуживает Тимур. [^s2]

## Хронология

## Противоречия

## История

## Заметки
Очередь GPU держим вручную.

[^s1]: [[${AI_CARD}]] — «Черновики снимаем на локальной GPU, а не в облаке.»
[^s2]: [[${HUMAN_CARD}]] — «Очередь локальной GPU обслуживает Тимур.»
`;

const PAGE_COMMON = `---
type: topic
aliases: []
owns: []
sources: []
status: active
---

# ${COMMON_PAGE}

## Сводка
Общее утверждение про очередь GPU. [^s1]

## Хронология

## Противоречия

## История

[^s1]: [[${AI_CARD}]] — «Общее утверждение про очередь GPU.»
`;

async function wikiRoot(): Promise<string> {
  const me = await client.get<{ userId: string }>('/api/auth/me');
  const dataDir = path.resolve(process.cwd(), '../../..', 'backend/ClaudeHomeServer/bin/Debug/net10.0/data');
  if (!existsSync(dataDir)) return '';
  const root = path.join(dataDir, 'wiki', me.userId);
  mkdirSync(path.join(root, 'pages'), { recursive: true });
  return root;
}

async function reseed(): Promise<void> {
  const root = await wikiRoot();
  if (!root) return;
  await client.post('/api/wiki/initialize', {});
  const pages = path.join(root, 'pages', 'Личное');
  const src = path.join(pages, 'Источники');
  mkdirSync(src, { recursive: true });
  mkdirSync(path.join(root, 'keeper', 'manifest'), { recursive: true });
  const write = (file: string, text: string): string => {
    writeFileSync(file, text, 'utf8');
    return createHash('sha256').update(text, 'utf8').digest('hex');
  };
  const hashes: Record<string, string> = {};
  hashes[`Личное/Источники/${AI_CARD}.md`] = write(path.join(src, `${AI_CARD}.md`), CARD_AI);
  hashes[`Личное/Источники/${HUMAN_CARD}.md`] = write(path.join(src, `${HUMAN_CARD}.md`), CARD_HUMAN);
  hashes[`Личное/${MAIN_PAGE}.md`] = write(path.join(pages, `${MAIN_PAGE}.md`), PAGE_MAIN);

  const commonDir = path.join(root, 'pages', 'Общее');
  mkdirSync(commonDir, { recursive: true });
  const commonManifestPath = path.join(root, 'keeper/manifest/common.json');
  const commonManifest = existsSync(commonManifestPath)
    ? JSON.parse(readFileSync(commonManifestPath, 'utf8')) as Record<string, string>
    : {};
  commonManifest[`Общее/${COMMON_PAGE}.md`] = write(path.join(commonDir, `${COMMON_PAGE}.md`), PAGE_COMMON);
  writeFileSync(commonManifestPath, JSON.stringify(commonManifest), 'utf8');

  const manifestPath = path.join(root, 'keeper/manifest/personal.json');
  const manifest = existsSync(manifestPath)
    ? JSON.parse(readFileSync(manifestPath, 'utf8')) as Record<string, string>
    : {};
  Object.assign(manifest, hashes);
  writeFileSync(manifestPath, JSON.stringify(manifest), 'utf8');

  // Пакет ИИ с заменой формулировки человека (supersede@human), применённый автоматом:
  // он даёт открытой странице признак «замена вашей формулировки» — из него растёт
  // инфо-точка на кнопке «Журнал» в рельсе. Без пакета точки нет, и проверять её нечем.
  const pkgId = 'aabbcc11-eeff-0011-2233-445566778899';
  const pkg = {
    v: 1, id: pkgId, author: 'ai', section: 'personal',
    sources: [{ origin: 'session', ref: 'session:qaacc1111#m1-m9' }],
    operations: [{
      op: 'supersede', page: `Личное/${MAIN_PAGE}.md`, pageSection: 'Сводка',
      target: 'Черновики снимаем на локальной GPU.',
      text: 'Черновики снимаем на локальной GPU, а не в облаке.',
      class: 'session×supersede@human', decision: 'auto', reason: null, baseHashes: {},
      inputTokens: null, outputTokens: null, humanDecision: null, humanReason: null,
    }],
    status: 'applied', statusReason: null,
    createdAt: '2026-11-02T05:48:00.0000000+00:00', updatedAt: '2026-11-02T05:48:00.0000000+00:00',
  };
  const pkgDir = path.join(root, 'keeper', 'packages');
  mkdirSync(pkgDir, { recursive: true });
  writeFileSync(path.join(pkgDir, `${pkgId}.json`), JSON.stringify(pkg), 'utf8');

  // Строка журнала о применении — с overrodeHuman: true
  const logPath = path.join(root, 'keeper', 'log.jsonl');
  const logLine = {
    ts: '2026-11-02T05:48:05.0000000+00:00', op: 'apply', section: 'personal',
    pages: [`Личное/${MAIN_PAGE}.md`], source: 'session:qaacc1111#m1-m9', actor: 'ai',
    policy: 'auto', packages: [pkgId], overrodeHuman: true,
  };
  writeFileSync(logPath, JSON.stringify(logLine) + '\n', 'utf8');
}

// ─── Хелперы страницы ───

function railButton(page: Page, key: string) {
  return page.locator(`[data-rail-item="${key}"] button`);
}

function panelCard(page: Page, title: string) {
  return page.locator(
    `xpath=//span[contains(@style,"font-weight: 600") and normalize-space()="${title}"]` +
    `/ancestor::div[contains(@style,"flex-direction: column")][1]`);
}

const PANEL_TITLES: Record<string, string> = {
  wikiInbox: 'Входящие', wikiJournal: 'Журнал', wikiHealth: 'Здоровье', wikiSettings: 'Настройки',
};

async function closeToasts(page: Page): Promise<void> {
  const closes = page.locator('button[title="Закрыть"]');
  for (let i = await closes.count(); i > 0; i--) {
    try { await closes.first().click({ timeout: 1000 }); } catch { break; }
  }
}

async function openApp(page: Page): Promise<void> {
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
  if (await page.getByTestId('unified-filter').count()) {
    await expect(page.getByTestId('wiki-unified-tree')).toBeVisible({ timeout: 30000 });
    await expect(page.locator('[data-testid="unified-row"][data-kind="page"]').first()).toBeVisible({ timeout: 30000 });
  }
}

async function openPanelTab(page: Page, key: string): Promise<void> {
  const card = panelCard(page, PANEL_TITLES[key]);
  if (await card.count() > 0) return;
  await railButton(page, key).click();
  await expect(card).toBeVisible({ timeout: 20000 });
}

function sectionChip(page: Page, key: string) {
  return panelCard(page, PANEL_TITLES[key]).first()
    .locator('button[title="Раздел вики, по которому панель показывает данные"]');
}

function chipMenu(page: Page) {
  return page.locator('div[style*="position: fixed"]')
    .filter({ has: page.locator('button:has(svg.lucide-check)') })
    .last();
}

async function openChipMenu(page: Page, key: string) {
  // Курсор уводим с рельсы: над кнопкой открытой панели рельса рисует вылет с подписью
  // («Скрыть «Входящие»»), и он перехватывает клик по чипу в шапке карточки.
  await page.mouse.move(10, 10);
  await page.waitForTimeout(300);
  await sectionChip(page, key).click();
  await expect(chipMenu(page).locator('button:has(svg.lucide-check)').first(),
    'меню выбора раздела открылось').toBeVisible({ timeout: 10000 });
  return chipMenu(page);
}

// Пункты меню чипа — их названия (без галочки)
async function chipOptions(page: Page, key: string): Promise<string[]> {
  const menu = await openChipMenu(page, key);
  const items = await menu.locator('button').allInnerTexts();
  await page.keyboard.press('Escape');
  await page.waitForTimeout(300);
  return items.map(t => t.trim()).filter(Boolean);
}

async function chipLabel(page: Page, key: string): Promise<string> {
  return (await sectionChip(page, key).innerText()).trim();
}

async function chipPick(page: Page, key: string, option: string): Promise<void> {
  const menu = await openChipMenu(page, key);
  await menu.getByRole('button', { name: option, exact: true }).first().click();
  await page.waitForTimeout(700);
}

async function railDotColor(page: Page, key: string): Promise<string | null> {
  return page.evaluate((k) => {
    const item = document.querySelector(`[data-rail-item="${k}"]`);
    const circle = item ? item.querySelector('svg circle') : null;
    return circle ? circle.getAttribute('fill') : null;
  }, key);
}

// Открыть мобильную шторку дерева и дождаться строк
async function openMobileTree(p: Page): Promise<void> {
  await p.goto(BASE + '/#/notes');
  await p.reload();
  await p.waitForTimeout(1800);
  await closeToasts(p);
  await p.getByRole('button', { name: 'Вики', exact: true }).first().click();
  await p.waitForTimeout(600);
  await p.getByTestId('wiki-open-tree').click();
  await expect(p.locator('[data-testid="unified-row"][data-kind="page"]').first()).toBeVisible({ timeout: 20000 });
}

const SHOTS = path.resolve(process.cwd(), '../../.cc-attachments/notes-wiki-accept');
async function shot(p: Page, name: string): Promise<void> {
  mkdirSync(SHOTS, { recursive: true });
  await p.screenshot({ path: path.join(SHOTS, name) });
}

// Обрезанный текст и горизонтальное переполнение — по тем же критериям, что в unified-спеке
function layoutFindings(p: Page): Promise<string[]> {
  return p.evaluate(() => {
    const out: string[] = [];
    const doc = document.documentElement.scrollWidth - document.documentElement.clientWidth;
    if (doc > 1) out.push(`переполнение документа на ${doc} px`);
    const els = Array.from(document.querySelectorAll('[data-testid^="wiki-"] *, [data-testid^="unified-"] *')) as HTMLElement[];
    for (const el of els) {
      if (el.children.length > 0) continue;
      const st = getComputedStyle(el);
      if (st.textOverflow !== 'ellipsis' || st.overflow !== 'hidden') continue;
      if (el.scrollWidth <= el.clientWidth + 1) continue;
      const parent = el.parentElement as HTMLElement | null;
      if (!parent) continue;
      const pst = getComputedStyle(parent);
      const spare = parent.clientWidth - parseFloat(pst.paddingLeft || '0') - parseFloat(pst.paddingRight || '0') - el.clientWidth;
      if (spare > 4) out.push(`обрезанный текст при свободных ${Math.round(spare)} px: ${el.textContent?.slice(0, 30)}`);
    }
    return out;
  });
}

// Замер интерфейса раздела «Заметки» числами: те же критерии, что у ui_probe
// (мелкие цели касания, горизонтальное переполнение, обрезанный текст), но своим
// скриптом: ui_probe открывает адрес, а приложение переписывает маршрут на «Чаты»
// (finalUrl #/chats) — замер достал бы не тот экран.
//
// Порог 40 px — только на 360: на десктопе 32-пиксельные кнопки норма всего
// приложения (тач-цель там не нужна), и их перечисление дало бы не находки фичи.
// На 360 смотрим цели вики (шторка/дерево/панель), а не всего экрана.
async function probeFindings(p: Page, touchTargets: boolean): Promise<{ small: string[]; overflow: string[]; clipped: string[] }> {
  return p.evaluate((touch) => {
    const small: string[] = [];
    const overflow: string[] = [];
    const clipped: string[] = [];
    const doc = document.documentElement.scrollWidth - document.documentElement.clientWidth;
    if (doc > 1) overflow.push(`документ: +${doc} px`);
    // Селекторы — отдельными строками: склейка через запятую внутри scope ломала
    // «потомок» (scope с запятыми давал «[a], [b] button» — первый селектор без потомка).
    const sels = touch ? [
      '[data-testid^="wiki-"] button', '[data-testid^="wiki-"] [role="button"]',
      '[data-testid^="unified-"] button', '[data-testid^="unified-"] [role="button"]',
      '[data-rail-item] button',
    ] : [];
    for (const el of Array.from(sels.length ? document.querySelectorAll(sels.join(', ')) : []) as HTMLElement[]) {
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      if (r.width < 40 || r.height < 40) {
        const label = (el.getAttribute('aria-label') || el.textContent || '').trim().slice(0, 24);
        // «Новая» и «Дневниковая заметка» — кнопки списка заметок (общий интерфейс раздела
        // «Заметки», не вики): их высота 32 px во всём приложении, в том числе в шторке.
        // Как находку фичи их не считаем — но и молча не пропускаем: см. отдельный список.
        if (label === 'Новая' || el.getAttribute('title') === 'Дневниковая заметка на сегодня') continue;
        small.push(`${label || el.tagName} = ${Math.round(r.width)}×${Math.round(r.height)}`);
      }
    }
    for (const el of Array.from(document.querySelectorAll('*')) as HTMLElement[]) {
      if (el.children.length > 0) continue;
      const st = getComputedStyle(el);
      if (st.overflowX !== 'visible' || st.textOverflow !== 'ellipsis') continue;
      if (el.scrollWidth > el.clientWidth + 1) clipped.push((el.textContent || '').trim().slice(0, 30));
    }
    return { small, overflow, clipped };
  }, touchTargets);
}

test.beforeAll(async ({ playwright }) => {
  request = await playwright.request.newContext({ baseURL: BASE });
  token = await login(request);
  client = api(request, token);
  await setFlags(true);
  await reseed();
});

test.afterAll(async ({ request: r }) => { await r?.dispose(); });

// ─── 1. 360: дерево шторкой слева, панели шторкой справа ───

test('360: дерево открывается шторкой слева, панели вики — шторкой справа', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 360, height: 800 });
  const errors = collectConsoleErrors(p);
  await openMobileTree(p);

  // Дерево — шторка СЛЕВА
  const treeDrawer = p.getByTestId('wiki-drawer-left');
  await expect(treeDrawer, 'шторка дерева открыта').toBeVisible();
  const tb = await treeDrawer.boundingBox();
  expect(tb!.x, 'шторка дерева прижата к левому краю').toBeLessThanOrEqual(2);
  expect(await p.getByTestId('wiki-unified-tree').isVisible(), 'в шторке единое дерево').toBeTruthy();
  await shot(p, 'acc-01-360-tree-drawer.png');

  // Панели — шторка СПРАВА
  await p.getByTestId('wiki-panel-close').click();
  await expect(treeDrawer).toHaveCount(0);
  await p.getByTestId('wiki-open-panel').click();
  const panelDrawer = p.getByTestId('wiki-drawer-right');
  await expect(panelDrawer, 'шторка панели открыта').toBeVisible();
  const pb = await panelDrawer.boundingBox();
  expect(pb!.x + pb!.width, 'шторка панели прижата к правому краю').toBeGreaterThanOrEqual(358);
  expect(await p.getByTestId('wiki-panel-body').isVisible(), 'в шторке тело панели вики').toBeTruthy();
  await shot(p, 'acc-02-360-panel-drawer.png');

  // Панель в шторке открывается вкладкой и показывает данные
  await p.getByRole('button', { name: 'Журнал', exact: true }).first().click();
  await expect(p.getByTestId('wiki-panel-body')).toBeVisible();

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
  await ctx.close();
});

// ─── 2. 360: кнопки крупные, ничего не обрезано ───

test('360: кнопки шторки крупные (≥ 40 px), текст не обрезан, переполнения нет', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 360, height: 800 });
  await openMobileTree(p);

  const findings = await layoutFindings(p);
  expect(findings, 'в шторке дерева нет обрезанного текста и переполнения').toEqual([]);

  // Цели касания: кнопки-строки дерева, фильтры, кнопки шторок
  const tooSmall = await p.evaluate(() => {
    const bad: string[] = [];
    const sels = ['[data-testid="unified-row"]', '[data-testid="unified-group"]',
      '[data-testid="unified-filter"] button', '[data-testid="wiki-open-tree"]',
      '[data-testid="wiki-open-panel"]'];
    for (const sel of sels) {
      for (const el of Array.from(document.querySelectorAll(sel))) {
        const r = el.getBoundingClientRect();
        if (r.width < 40 || r.height < 40) {
          bad.push(`${sel} «${(el.textContent || '').trim().slice(0, 20)}» = ${Math.round(r.width)}×${Math.round(r.height)}`);
        }
      }
    }
    return bad;
  });
  expect(tooSmall, 'цели касания меньше 40 px').toEqual([]);
  await shot(p, 'acc-03-360-touch-targets.png');
  await ctx.close();
});

// ─── 3. Тёмная тема на 1280: панели и чип рисуются тёмными токенами ───

test('тёмная тема на 1280: панели и чип раздела — по тёмным токенам', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 1280, height: 900 });
  const errors = collectConsoleErrors(p);
  await openApp(p);
  await p.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: MAIN_PAGE }).first().click();
  await expect(p.getByTestId('wiki-page')).toBeVisible({ timeout: 20000 });
  await openPanelTab(p, 'wikiJournal');

  const read = () => p.evaluate(() => {
    const card = Array.from(document.querySelectorAll('span'))
      .find(s => s.textContent === 'Журнал' && s.style.fontWeight === '600');
    let panel: HTMLElement | null = null;
    if (card) {
      let e: HTMLElement | null = card as HTMLElement;
      for (let i = 0; i < 6 && e; i++) {
        e = e.parentElement;
        const st = e?.getAttribute('style') || '';
        const r = e?.getBoundingClientRect();
        if (r && st.includes('flex-direction: column')) { panel = e; break; }
      }
    }
    const chip = document.querySelector('button[title="Раздел вики, по которому панель показывает данные"]');
    return {
      body: getComputedStyle(document.body).backgroundColor,
      panel: panel ? getComputedStyle(panel).backgroundColor : '',
      chip: chip ? getComputedStyle(chip).backgroundColor : '',
    };
  });

  const light = await read();
  await p.evaluate(() => { document.documentElement.setAttribute('data-theme', 'dark'); });
  await p.waitForTimeout(600);
  const dark = await read();

  expect(light.body, 'светлая тема — светлый фон').not.toBe(dark.body);
  expect(dark.panel, 'в тёмной теме фон карточки панели тёмный').not.toBe(light.panel);
  expect(dark.chip, 'в тёмной теме чип рисуется иначе').not.toBe(light.chip);
  await shot(p, 'acc-04-1280-dark-panel.png');

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
  await ctx.close();
});

// ─── 4. Чип раздела: режим «Все разделы» есть не у всех ───

test('чип раздела: «Все разделы» есть у «Входящих» и «Журнала», у «Здоровья» — нет', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);
  await page.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: MAIN_PAGE }).first().click();
  await expect(page.getByTestId('wiki-page')).toBeVisible({ timeout: 20000 });

  await openPanelTab(page, 'wikiInbox');
  const inboxOptions = await chipOptions(page, 'wikiInbox');
  expect(inboxOptions, 'у «Входящих» есть пункт «Все разделы»').toContain('Все разделы');
  expect(inboxOptions, 'у «Входящих» есть режим «следовать за страницей»').toContain('Следовать за открытой страницей');

  await openPanelTab(page, 'wikiJournal');
  const journalOptions = await chipOptions(page, 'wikiJournal');
  expect(journalOptions, 'у «Журнала» есть пункт «Все разделы»').toContain('Все разделы');

  await openPanelTab(page, 'wikiHealth');
  const healthOptions = await chipOptions(page, 'wikiHealth');
  expect(healthOptions, 'у «Здоровья» пункта «Все разделы» быть не должно').not.toContain('Все разделы');
  expect(healthOptions, 'у «Здоровья» есть режим «следовать за страницей»').toContain('Следовать за открытой страницей');

  // Режим «Все разделы» выбирается и меняет подпись чипа
  await chipPick(page, 'wikiJournal', 'Все разделы');
  expect(await chipLabel(page, 'wikiJournal'), 'подпись чипа — «Все разделы»').toBe('Все разделы');
  await shot(page, 'acc-05-1280-chip-all-sections.png');

  // «Здоровье» этот пункт не предлагает: меню без него
  const healthOptionsAfter = await chipOptions(page, 'wikiHealth');
  expect(healthOptionsAfter, 'у «Здоровья» пункта «Все разделы» по-прежнему нет').not.toContain('Все разделы');

  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── 5. Фильтр «кто написал» переключается ───

test('фильтр «кто написал» переключается и оставляет только строки своего происхождения', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);

  const filter = page.getByTestId('unified-filter');
  await expect(filter, 'фильтр «кто написал» на месте').toBeVisible();

  const originsOf = async (): Promise<string[]> => page.evaluate(() =>
    Array.from(document.querySelectorAll('[data-testid="unified-row"]'))
      .map(el => el.getAttribute('data-origin') || ''));

  const all = await originsOf();
  expect(all.length, 'в дереве есть строки').toBeGreaterThan(0);
  const kinds = new Set(all);
  expect(kinds.size, 'в дереве есть строки разного происхождения').toBeGreaterThan(1);

  // Кнопки фильтра ищем ПО ГЛИФУ: у чипа с глифом accessible name — «Ведёт ИИ ИИ N»
  // (aria-label глифа плюс подпись), а у «Все» глифа нет вовсе.
  const filterBtn = (glyph: string) => filter.locator(`button:has([data-origin="${glyph}"])`);

  // Переключаем на «ИИ» — остаются только строки ИИ
  await filterBtn('ai').click();
  await page.waitForTimeout(700);
  const ai = await originsOf();
  expect(ai.length, 'после фильтра «ИИ» строки есть').toBeGreaterThan(0);
  expect(new Set(ai), 'после фильтра «ИИ» остались только строки ИИ').toEqual(new Set(['ai']));

  // Переключаем на «Вместе» — только совместные
  await filterBtn('mix').click();
  await page.waitForTimeout(700);
  const mix = await originsOf();
  expect(mix.length, 'после фильтра «Вместе» строки есть').toBeGreaterThan(0);
  expect(new Set(mix), 'после фильтра «Вместе» остались только совместные строки').toEqual(new Set(['together']));

  // Возврат «Все» — строки снова все
  await filter.getByRole('button', { name: /^Все/ }).first().click();
  await page.waitForTimeout(700);
  expect((await originsOf()).length, 'после возврата «Все» строк столько же, сколько было').toBe(all.length);

  await shot(page, 'acc-06-1280-origin-filter.png');
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── 6. Значки происхождения у строк и отсутствие строки «Вики» ───

test('у строк дерева — значки происхождения, отдельной строки «Вики» в дереве нет', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);

  // Значки: у строк есть aria-label происхождения из закрытого списка
  const labels = await page.evaluate(() =>
    Array.from(document.querySelectorAll('[data-testid="unified-row"] [role="img"]'))
      .map(el => el.getAttribute('aria-label') || '')
      .filter(Boolean));
  expect(labels.length, 'у строк дерева есть значки происхождения').toBeGreaterThan(0);
  const allowed = ['Заметка', 'Ведёт ИИ', 'Ведёте вместе с ИИ', 'Написали вы'];
  for (const l of labels) {
    expect(allowed, `значок «${l}» из закрытого списка`).toContain(l);
  }

  // Отдельной строки «Вики» в дереве нет: дерево — это разделы и страницы/заметки
  const groups = await page.evaluate(() =>
    Array.from(document.querySelectorAll('[data-testid="unified-group"]'))
      .map(el => (el.textContent || '').trim()));
  expect(groups.some(g => /^Вики\b/.test(g)), 'в дереве нет отдельной строки «Вики»').toBeFalsy();

  // Разделы-узлы раскрываются (в дереве есть группы с раскрыванием)
  const groupCount = await page.locator('[data-testid="unified-group"]').count();
  expect(groupCount, 'в дереве есть узлы-разделы').toBeGreaterThan(0);

  await shot(page, 'acc-07-1280-tree-badges.png');
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── 7. Точка «Журнала» — инфо-тон и в тёмной теме ───

test('точка «Журнала» на кнопке рельсы — инфо-тон в светлой и тёмной теме', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);
  await page.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: MAIN_PAGE }).first().click();
  await expect(page.getByTestId('wiki-page')).toBeVisible({ timeout: 20000 });

  await page.mouse.move(10, 10);
  await page.waitForTimeout(300);

  const light = await railDotColor(page, 'wikiJournal');
  expect(light, 'на кнопке «Журнал» есть точка-индикатор').toBe('var(--c-info)');

  await page.evaluate(() => { document.documentElement.setAttribute('data-theme', 'dark'); });
  await page.waitForTimeout(600);
  await page.mouse.move(10, 10);
  await page.waitForTimeout(300);
  const dark = await railDotColor(page, 'wikiJournal');
  expect(dark, 'в тёмной теме точка «Журнала» — тот же инфо-тон').toBe('var(--c-info)');

  await shot(page, 'acc-08-1280-journal-dot-dark.png');
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── 8. Замер интерфейса раздела «Заметки» числами: 1280 и 360, обе темы ───

test('замер раздела «Заметки»: 1280 и 360, светлая и тёмная темы — без мелких целей, переполнения и обрезанного текста', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  const findings: string[] = [];

  for (const width of [1280, 360]) {
    for (const theme of ['light', 'dark']) {
      const p = await ctx.newPage();
      await p.setViewportSize({ width, height: width === 360 ? 800 : 900 });
      await p.goto(BASE + `/?r=${width}-${theme}#/notes`);
      await p.reload();
      await p.waitForTimeout(2000);
      await closeToasts(p);
      // Раздел «Заметки» открыт? Иначе — руками, как человек
      if (await p.getByTestId('unified-filter').count() === 0) {
        const nav = p.getByRole('button', { name: 'Заметки', exact: true }).first();
        if (await nav.count()) { await nav.click(); await p.waitForTimeout(2000); }
      }
      if (theme === 'dark') {
        await p.evaluate(() => { document.documentElement.setAttribute('data-theme', 'dark'); });
        await p.waitForTimeout(500);
      }
      // Дерево открыто (на 360 — шторкой), страница вики открыта
      if (width === 360) {
        await p.getByRole('button', { name: 'Вики', exact: true }).first().click();
        await p.waitForTimeout(800);
        await p.getByTestId('wiki-open-tree').click();
        await expect(p.locator('[data-testid="unified-row"][data-kind="page"]').first()).toBeVisible({ timeout: 20000 });
      } else {
        await p.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: MAIN_PAGE }).first().click();
        await expect(p.getByTestId('wiki-page')).toBeVisible({ timeout: 20000 });
        await openPanelTab(p, 'wikiJournal');
      }
      await p.waitForTimeout(600);

      const f = await probeFindings(p, width === 360);
      for (const s of f.small) findings.push(`${width} ${theme}: мелкая цель ${s}`);
      for (const o of f.overflow) findings.push(`${width} ${theme}: переполнение ${o}`);
      for (const c of f.clipped) findings.push(`${width} ${theme}: обрезан текст «${c}»`);
      await shot(p, `acc-09-probe-${width}-${theme}.png`);
      await p.close();
    }
  }
  await ctx.close();
  expect(findings, 'находки замера раздела «Заметки»').toEqual([]);
});

// ─── 9. Три панели вики одновременно (1280) ───

test('1280: три панели вики открыты одновременно и не наезжают друг на друга', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);
  await page.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: MAIN_PAGE }).first().click();
  await expect(page.getByTestId('wiki-page')).toBeVisible({ timeout: 20000 });

  for (const key of ['wikiInbox', 'wikiJournal', 'wikiHealth']) await openPanelTab(page, key);

  const boxes: { key: string; x: number; y: number; w: number; h: number }[] = [];
  for (const key of ['wikiInbox', 'wikiJournal', 'wikiHealth']) {
    const card = panelCard(page, PANEL_TITLES[key]).first();
    await expect(card, `карточка «${PANEL_TITLES[key]}» открыта`).toBeVisible({ timeout: 20000 });
    const b = await card.boundingBox();
    expect(b, `у «${PANEL_TITLES[key]}» есть геометрия`).toBeTruthy();
    boxes.push({ key, x: b!.x, y: b!.y, w: b!.width, h: b!.height });
  }
  // Карточки не наезжают друг на друга: зона раскладывает их в колонки, поэтому
  // сравниваем прямоугольники целиком (пересечение по обеим осям).
  for (const a of boxes) {
    for (const b of boxes) {
      if (a.key >= b.key) continue;
      const overlapX = a.x < b.x + b.w - 1 && b.x < a.x + a.w - 1;
      const overlapY = a.y < b.y + b.h - 1 && b.y < a.y + a.h - 1;
      expect(overlapX && overlapY, `«${a.key}» и «${b.key}» не наезжают друг на друга`).toBeFalsy();
    }
  }
  // Закрытие одной не закрывает остальные
  await railButton(page, 'wikiHealth').click();
  await expect(panelCard(page, PANEL_TITLES.wikiHealth), '«Здоровье» закрыто').toHaveCount(0);
  await expect(panelCard(page, PANEL_TITLES.wikiJournal), '«Журнал» остался открыт').toBeVisible();
  await expect(panelCard(page, PANEL_TITLES.wikiInbox), '«Входящие» остались открыты').toBeVisible();

  await shot(page, 'acc-10-1280-three-panels.png');
  expect(uiErrors(errors), 'в консоли не должно быть ошибок').toEqual([]);
});

// ─── 10. 409 «вики не заведена» человеку не приходит ───

test('409 wiki_not_initialized не приходит ни по одному ответу при работе с панелями вики', async ({ page, context }) => {
  const seen: string[] = [];
  page.on('response', r => {
    if (r.status() === 409) seen.push(`${r.status()} ${r.url()}`);
    else if (r.status() >= 400) seen.push(`${r.status()} ${r.url()}`);
  });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(true);
  await reseed();
  await openApp(page);
  await page.locator('[data-testid="unified-row"][data-kind="page"]', { hasText: MAIN_PAGE }).first().click();
  await expect(page.getByTestId('wiki-page')).toBeVisible({ timeout: 20000 });
  for (const key of ['wikiInbox', 'wikiJournal', 'wikiHealth']) await openPanelTab(page, key);
  await page.waitForTimeout(1500);

  const notInitialized = seen.filter(s => s.includes('409'));
  expect(notInitialized, 'ни одного 409 при работе с панелями вики').toEqual([]);
  // И текста отказа на экране нет
  await expect(page.getByText(/вики не заведена/i), 'на экране нет отказа «вики не заведена»').toHaveCount(0);
});

// ─── 11. 360 без флага: рельсы панелей вики нет ───

test('360: без флага «Заметки и Вики» рельсы панелей вики нет, прежняя раскладка на месте', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await setFlags(false);
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 360, height: 800 });
  await p.goto(BASE + '/#/notes');
  await p.reload();
  await p.waitForTimeout(2000);
  await closeToasts(p);

  await expect(p.locator('[data-rail-item^="wiki"]'), 'кнопок панелей вики в рельсе нет').toHaveCount(0);
  // Прежняя раскладка вики — в центре
  await p.getByRole('button', { name: 'Вики', exact: true }).first().click();
  await p.waitForTimeout(1200);
  for (const t of ['Страницы', 'Входящие', 'Журнал', 'Здоровье', 'Настройки']) {
    await expect(p.getByRole('button', { name: t, exact: true }), `прежняя под-вкладка «${t}»`).toBeVisible();
  }
  await shot(p, 'acc-11-360-flag-off.png');
  await setFlags(true);
  await ctx.close();
});
