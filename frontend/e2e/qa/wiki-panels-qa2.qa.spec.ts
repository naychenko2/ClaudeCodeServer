// QA-2 · перепроверка двух дефектов фичи «Панели вики» (эпик dbaa0a17, ветка feat/wiki-panels):
// c86f08b8 — поле поиска «Отбора чатов» после перехода на TextField из кита (c265db608);
// 64bf0dbd — резерв под круглешок AI в полосе «Начать вики заново» на мобиле (1b3905104).
//
// Стенд — production-сборка этого дерева (npm run build → dist, бэкенд start_stand),
// как и требовал дефект c86f08b8: в прошлом круге интерфейс проверялся в dev-режиме именно
// потому, что production-сборка падала. Засев — тот же, что в wiki-panels.qa.spec.ts:
// 100 чатов API + страницы/стор отбора на диск стенда. Тексты панелей здесь не проверяются.

import { test, expect, type APIRequestContext, type Browser, type Page } from '@playwright/test';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { api, collectConsoleErrors, login, type Api } from './helpers';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5500';
const DATA = path.resolve(process.cwd(), '../../..', 'backend/ClaudeHomeServer/bin/Debug/net10.0/data');
const SHOTS = path.resolve(process.cwd(), '../..', '.cc-attachments', 'wiki-panels-qa');

const PREFIX = 'qa-wp2-';
const CHATS = 100;

const AI_CARD = 'ИИ-карточка QA2';
const HUMAN_CARD = 'Карточка человека QA2';
const HUMAN_PAGE = 'Страница человека QA2';
const AI_PAGES = Array.from({ length: 6 }, (_, i) => `Страница ИИ QA2 ${i + 1}`);

test.describe.configure({ mode: 'serial' });

let request: APIRequestContext;
let token: string;
let client: Api;
let root = '';
let projectId = '';
let sessions: Array<{ id: string; name: string }> = [];

const pageFile = (title: string) => `---
type: topic
aliases: []
owns: []
sources: []
status: active
---

# ${title}

## Сводка
Утверждение про локальную GPU и очередь. [^s1]
Второе утверждение для объёма текста. [^s1]

## Хронология

## Противоречия

## История

[^s1]: [[${AI_CARD}]] — «Утверждение про локальную GPU и очередь.»
`;

const humanPageFile = `---
type: topic
aliases: []
owns: []
sources: []
status: active
---

# ${HUMAN_PAGE}

## Сводка
Человеческое утверждение про очередь. [^s1]

## Хронология

## Противоречия

## История

[^s1]: [[${HUMAN_CARD}]] — «Человеческое утверждение про очередь.»
`;

const cardFile = (title: string, origin: 'session' | 'human') => `---
type: source
origin: ${origin}
ref: ${origin === 'session' ? 'session:qa-panels-qa2-source#m1-m5' : 'human:qa-panels-qa2'}
hash: ${title.replace(/\s+/g, '-').toLowerCase()}
captured: 2026-11-10T05:48:00.0000000+00:00
aliases: []
owns: []
sources: []
status: active
---

# ${title}
`;

async function wikiRoot(): Promise<string> {
  const me = await client.get<{ userId: string }>('/api/auth/me');
  mkdirSync(path.join(DATA, 'wiki', me.userId), { recursive: true });
  return path.join(DATA, 'wiki', me.userId);
}

function writeFile(file: string, text: string): string {
  mkdirSync(path.dirname(file), { recursive: true });
  writeFileSync(file, text, 'utf8');
  return createHash('sha256').update(text, 'utf8').digest('hex');
}

// Засев нужен ровно двум проверкам: страницы ИИ (предпросмотр чистки считает их)
// и стор отбора на 100 чатов (длинный список, как в жалобе). Журнал/пакеты/«Здоровье» — не здесь.
async function seedWiki(): Promise<void> {
  root = await wikiRoot();
  const pages = path.join(root, 'pages', 'Личное');
  rmSync(pages, { recursive: true, force: true });
  rmSync(path.join(root, 'keeper'), { recursive: true, force: true });

  const manifest: Record<string, string> = {};
  for (const title of AI_PAGES) manifest[`Личное/${title}.md`] = writeFile(path.join(pages, `${title}.md`), pageFile(title));
  manifest[`Личное/${HUMAN_PAGE}.md`] = writeFile(path.join(pages, `${HUMAN_PAGE}.md`), humanPageFile);
  manifest[`Личное/Источники/${AI_CARD}.md`] = writeFile(path.join(pages, 'Источники', `${AI_CARD}.md`), cardFile(AI_CARD, 'session'));
  manifest[`Личное/Источники/${HUMAN_CARD}.md`] = writeFile(path.join(pages, 'Источники', `${HUMAN_CARD}.md`), cardFile(HUMAN_CARD, 'human'));
  writeFile(path.join(root, 'keeper/manifest/personal.json'), JSON.stringify(manifest, null, 2));

  const now = new Date();
  const ago = (h: number) => new Date(now.getTime() - h * 3600_000).toISOString();
  writeFile(path.join(root, 'keeper/settings.json'), JSON.stringify({
    v: 1, autoApplyEnabled: false, classes: [],
    nightly: {
      enabled: true, windowStartHour: 3, windowEndHour: 6,
      maxPackagesPerNight: 20, maxPackagesPerSection: 10, maxChatsPerSection: 15,
      inputTokenBudget: 800_000,
    },
  }, null, 2));

  const entries = sessions.map((s, i) => ({
    sessionId: s.id, cursor: i % 7, cursorHash: null, zone: null, reason: null,
    nothingNewStreak: 0, triagedAt: ago(6), score: i % 10,
    signals: [], stage1Zone: 'skipped', stage1Reason: `ступень 1: низкий балл (${i % 10}) — знание не найдено`,
    rejectStreak: 0,
  }));
  writeFile(path.join(root, 'keeper/chat-triage.json'), JSON.stringify(entries, null, 2));
}

async function openNotes(p: Page): Promise<void> {
  await p.goto(BASE + '/#/notes');
  await p.waitForTimeout(2500);
  const closes = p.locator('button[title="Закрыть"]');
  for (let i = await closes.count(); i > 0; i--) {
    try { await closes.first().click({ timeout: 800 }); } catch { break; }
  }
}

// Раскладка зон — явная, иначе прогон наследует состояние прошлого (панель может открыться
// сама и с другой шириной зоны). left пуст: дерево здесь не предмет проверки.
const zones = (rightWidth: number) => JSON.stringify({
  right: { layout: [['wikiSettings']], mode: 'multi', width: rightWidth, colFlex: [], stash: [] },
  left: { layout: [], mode: 'multi', width: 340, colFlex: [], stash: [] },
  weights: {}, home: {}, tucked: [], railOrder: [],
});

// Открывает страницу «Заметки» и панель «Настройки» (или принимает её уже открытой)
async function openApp(p: Page): Promise<void> {
  await openNotes(p);
  const card = panelCard(p, PANEL_TITLES.wikiSettings).first();
  if (await card.isVisible().catch(() => false)) return;
  const rail = p.locator('[data-rail-item="wikiSettings"] button');
  try {
    await rail.waitFor({ state: 'visible', timeout: 20000 });
    await rail.click();
    await expect(card).toBeVisible({ timeout: 20000 });
  } catch {
    mkdirSync(SHOTS, { recursive: true });
    await p.screenshot({ path: path.join(SHOTS, 'qa2-openapp-fail.png') });
    writeFileSync(path.join(SHOTS, 'qa2-openapp-fail.txt'),
      `url: ${p.url()}\n\n` + (await p.locator('body').innerText()).slice(0, 5000), 'utf8');
    throw new Error('панель «Настройки» не открылась на стенде — см. qa2-openapp-fail.txt');
  }
}

const PANEL_TITLES: Record<string, string> = {
  wikiSettings: 'Настройки',
};

function panelCard(p: Page, title: string) {
  return p.locator(
    `xpath=//span[contains(@style,"font-weight: 600") and normalize-space()="${title}"]` +
    `/ancestor::div[contains(@style,"flex-direction: column")][1]`);
}

async function openPanel(p: Page, key: string) {
  const card = panelCard(p, PANEL_TITLES[key]).first();
  if (await card.count() === 0) {
    await p.locator(`[data-rail-item="${key}"] button`).click();
  }
  await expect(panelCard(p, PANEL_TITLES[key]).first()).toBeVisible({ timeout: 20000 });
  return panelCard(p, PANEL_TITLES[key]).first();
}

async function shot(p: Page, name: string): Promise<void> {
  mkdirSync(SHOTS, { recursive: true });
  await p.screenshot({ path: path.join(SHOTS, name), fullPage: false });
}

// Геометрия полосы и её кнопки + что стоит над кнопкой в каждой точке её ширины
function probeStrip(p: Page) {
  return p.evaluate(() => {
    const strip = document.querySelector('[data-testid="wiki-purge-strip"]') as HTMLElement | null;
    const btn = document.querySelector('[data-testid="wiki-purge-open"]') as HTMLElement | null;
    const fab = document.querySelector('[data-cc-fab]') as HTMLElement | null;
    if (!strip || !btn) return null;
    const cs = getComputedStyle(strip);
    const r = btn.getBoundingClientRect();
    const midY = r.top + r.height / 2;
    const hits = [10, 30, 50, 70, 85, 95].map(pct => {
      const x = r.left + (r.width * pct) / 100;
      const el = document.elementFromPoint(x, midY) as HTMLElement | null;
      const label = el === btn ? 'сама кнопка'
        : (el && btn.contains(el)) ? `внутри кнопки: ${el.tagName.toLowerCase()}`
        : `${el?.tagName.toLowerCase() ?? 'ничего'}${el?.getAttribute('data-testid') ? '[' + el.getAttribute('data-testid') + ']' : ''}${el?.getAttribute('class') ? '.' + String(el.getAttribute('class')).split(' ')[0] : ''}`;
      return { pct, label, covered: !(el === btn || btn.contains(el)) };
    });
    return {
      padTop: cs.paddingTop, padBottom: cs.paddingBottom, padLeft: cs.paddingLeft,
      inlinePadBottom: strip.style.paddingBottom || '',
      stripH: Math.round(r.height ? strip.getBoundingClientRect().height : 0),
      btn: { x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) },
      fab: fab ? { x: Math.round(fab.getBoundingClientRect().left), y: Math.round(fab.getBoundingClientRect().top), w: Math.round(fab.getBoundingClientRect().width) } : null,
      hits,
    };
  });
}

test.beforeAll(async ({ playwright }) => {
  request = await playwright.request.newContext({ baseURL: BASE });
  token = await login(request);
  client = api(request, token);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });
  await client.post('/api/wiki/initialize', {});

  const project = await client.post<{ id: string }>('/api/projects', {
    name: `${PREFIX}проект`, rootPath: `/tmp/${PREFIX}${Date.now()}`, createDirectory: true,
  });
  projectId = project.id;
  for (let i = 0; i < CHATS; i++) {
    const name = `${PREFIX}чат ${String(i + 1).padStart(3, '0')} — про очередь GPU и локальную модель`;
    const s = await client.post<{ id: string }>(`/api/projects/${project.id}/sessions`, { name });
    sessions.push({ id: s.id, name });
  }
  await seedWiki();
});

test.afterAll(async () => {
  try { if (projectId) await client.del(`/api/projects/${projectId}`); } catch { /* best-effort */ }
  await request?.dispose();
});

// ─── 1. c86f08b8: поле поиска «Отбора чатов» на production-стенде ───

test('1280 · «Отбор чатов»: поиск работает, «Сбросить поиск» работает, микрофона нет', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 1280, height: 900 });
  const errors = collectConsoleErrors(p);
  await openApp(p);
  const panel = await openPanel(p, 'wikiSettings');
  await expect(p.getByTestId('wiki-triage-list')).toBeVisible({ timeout: 20000 });
  const header = p.getByTestId('wiki-triage-header');
  await expect(header).toBeVisible();

  await p.getByRole('button', { name: /^Все · 100$/ }).first().click();
  await expect.poll(async () => (await header.innerText()).replace(/\s+/g, ' ')).toContain('Показано 100 из 100');

  // поиск в длинном списке — виден
  const search = panel.getByPlaceholder('Найти чат по названию');
  await expect(search, 'поле поиска в «Отборе чатов» видно при >10 чатах').toBeVisible();
  await search.fill('ничего-не-найдено');
  await expect(p.getByText('Ничего не нашлось по «ничего-не-найдено»'), 'поиск фильтрует список').toBeVisible();
  await shot(p, 'qa2-01-1280-triage-search-empty.png');
  await p.getByRole('button', { name: 'Сбросить поиск' }).click();
  await expect.poll(async () => (await header.innerText()).replace(/\s+/g, ' ')).toContain('Показано 100 из 100');

  // микрофона у этого поля нет и не нужно (путь Field.tsx → VoiceMicButton закрыт)
  const mics = await panel.evaluate(el => Array.from(el.querySelectorAll('[title]'))
    .filter(n => /Голосовой ввод/.test(n.getAttribute('title') || ''))
    .map(n => n.getAttribute('title')));
  expect(mics, 'в панели «Настройки» нет кнопки голосового ввода').toEqual([]);

  await ctx.close();
  expect(errors.filter(e => !e.includes('net::ERR_') && !e.includes('Failed to load resource')), 'ошибок консоли нет').toEqual([]);
});

// ─── 2. 64bf0dbd: 360 — круглешок AI не лежит на кнопке «Начать заново…» ───

test('360 · шторка «Настройки»: кнопка чистки целиком доступна, предпросмотр и чистка работают', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 360, height: 800 });
  const errors = collectConsoleErrors(p);
  await openNotes(p);
  const wikiToggle = p.getByRole('button', { name: 'Вики', exact: true }).first();
  if (await wikiToggle.count()) { await wikiToggle.click(); await p.waitForTimeout(1200); }
  await expect(p.getByTestId('wiki-open-panel')).toBeVisible({ timeout: 25000 });
  await p.getByTestId('wiki-open-panel').click();
  await expect(p.getByTestId('wiki-drawer-right')).toBeVisible({ timeout: 20000 });
  await p.getByRole('button', { name: 'Настройки', exact: true }).first().click();
  await expect(p.getByTestId('wiki-triage-list')).toBeVisible({ timeout: 20000 });

  // без круглешка проверка была бы пустой: он должен быть на экране
  await expect(p.locator('[data-cc-fab]'), 'AI-кнопка на телефоне есть — иначе проверке не о чем судить').toBeVisible();

  const probe = await probeStrip(p);
  expect(probe, 'полоса чистки на месте').not.toBeNull();
  console.log('[360] полоса:', JSON.stringify(probe));
  const covered = probe!.hits.filter(h => h.covered);
  expect(covered, `кнопка не накрыта ни в одной точке её ширины: ${JSON.stringify(covered)}`).toEqual([]);
  // резерв поставлен: нижний отступ полосы на телефоне больше базового
  expect(parseFloat(probe!.padBottom), `нижний отступ полосы (${probe!.padBottom}) больше базового (${probe!.padTop}) — резерв под FAB`).toBeGreaterThan(parseFloat(probe!.padTop));

  await shot(p, 'qa2-02-360-purge-button-not-covered.png');

  // предпросмотр и сама чистка
  const open = p.getByTestId('wiki-purge-open');
  await open.click();
  await expect(p.getByText('Начать вики заново?'), 'предпросмотр открылся').toBeVisible();
  const confirm = p.getByRole('button', { name: /^Удалить \d+ страниц/ }).first();
  const n = Number(((await confirm.innerText()).trim().match(/(\d+)/) ?? [])[1] ?? '0');
  expect(n, 'предпросмотр насчитал страницы ИИ').toBeGreaterThan(0);
  await shot(p, 'qa2-03-360-purge-preview.png');
  await confirm.click();
  await expect(p.getByText(/Удалено \d+ страниц/).first(), 'итог чистки в полосе').toBeVisible({ timeout: 30000 });

  await ctx.close();
  expect(errors.filter(e => !e.includes('net::ERR_') && !e.includes('Failed to load resource')), 'ошибок консоли нет').toEqual([]);
});

// ─── 3. 64bf0dbd (R-M2): на десктопе полоса не выросла ───

async function desktopStrip(browser: Browser, rightWidth: number) {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await ctx.addInitScript(w => localStorage.setItem('cc_notes_zones', w as string), zones(rightWidth));
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 1280, height: 900 });
  await openApp(p);
  const panel = await openPanel(p, 'wikiSettings');
  await expect(p.getByTestId('wiki-triage-list')).toBeVisible({ timeout: 20000 });
  const panelW = (await panel.boundingBox())!.width;
  const probe = await probeStrip(p);
  await ctx.close();
  return { panelW: Math.round(panelW), ...probe! };
}

test('десктоп (зона 340 и 560): полоса не резервирует место под FAB и не растёт в высоте', async ({ browser }) => {
  for (const want of [340, 560]) {
    const s = await desktopStrip(browser, want);
    const tag = `зона ${want}`;
    expect(Math.abs(s.panelW - want), `${tag}: раскладка задана явно, панель ${s.panelW} px`).toBeLessThan(30);
    console.log(`[desktop ${tag}]`, JSON.stringify({ panelW: s.panelW, padTop: s.padTop, padBottom: s.padBottom, padLeft: s.padLeft, inlinePadBottom: s.inlinePadBottom, stripH: s.stripH }));
    expect(s.padBottom, `${tag}: нижний отступ полосы = базовый (${s.padTop}), резерва нет`).toBe(s.padTop);
    // React пишет шейп-стиль padding, поэтому в inline видно разложенный longhand (8px);
    // резерв выдавал бы себя как max(..., var(--cc-fab-clear)) — его быть не должно
    expect(s.inlinePadBottom, `${tag}: inline-стиль полосы не несёт резерва под FAB`).not.toMatch(/cc-fab-clear|max\(/);
  }
});
