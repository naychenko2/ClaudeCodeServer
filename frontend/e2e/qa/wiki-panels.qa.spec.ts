// QA-приёмка фичи «Панели вики» (эпик dbaa0a17, ветка feat/wiki-panels):
// проверка на 360 и 1280 — панели в правой зоне 340 (узко) и 560 (широко), шторка мобилы.
//
// Данные: вики заводится через API, страницы/манифест/журнал/настройки/пакеты/стор отбора
// кладутся на диск стенда (рецепт — docs/operations/qa-stand.md); 100 чатов создаются API,
// чтобы «Отбор чатов» был длинным, как в жалобе владельца.
//
// Стенд: dev-серверы (host :5173, remote «Заметки» :5174) + бэкенд :5000 — production-сборка
// ветки падает на стороже графа remote «Заметки» (см. отчёт), поэтому принято решение
// проверять интерфейс в dev-режиме.

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { api, collectConsoleErrors, login, type Api } from './helpers';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5173';
const DATA = path.resolve(process.cwd(), '../../..', 'backend/ClaudeHomeServer/bin/Debug/net10.0/data');
const SHOTS = path.resolve(process.cwd(), '../..', '.cc-attachments', 'wiki-panels-qa');

const PREFIX = 'qa-wp-';
const CHATS = 100;

const AI_CARD = 'ИИ-карточка панелей';
const HUMAN_CARD = 'Карточка человека панелей';
const HUMAN_PAGE = 'Страница человека';
const AI_PAGES = Array.from({ length: 6 }, (_, i) => `Страница ИИ ${i + 1}`);
const LONG_TITLE = `${PREFIX}очень длинное название чата, которое не должно рвать карточку, а должно обрезаться многоточием в одну строку`;
const LONG_REASON =
  'серая зона: в переписке есть решения про локальную очередь GPU, но часть утверждений без источника — ' +
  'нужно посмотреть вручную, потому что автоматика не смогла подтвердить цитаты и не стала писать в вики';

// id пакетов — только GUID (WikiPackage.ShapeErrors: «id пакета не GUID»)
const PKG_INBOX1 = '11111111-2222-3333-4444-555555555555';
const PKG_INBOX2 = '11111111-2222-3333-4444-666666666666';
const PKG_APPLIED = '11111111-2222-3333-4444-777777777777';

test.describe.configure({ mode: 'serial' });

let request: APIRequestContext;
let token: string;
let client: Api;
let root = '';
let sectionKey = 'personal';
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
ref: ${origin === 'session' ? 'session:qa-panels-source#m1-m5' : 'human:qa-panels-human'}
hash: ${title.replace(/\s+/g, '-').toLowerCase()}
captured: 2026-11-10T05:48:00.0000000+00:00
aliases: []
owns: []
sources: []
status: active
---

# ${title}
`;

const HEALTH = `# Здоровье раздела «personal»

Проверено: 2026-11-13T06:30:00.0000000+00:00. Код: 29.

## Находки (46)
${Array.from({ length: 40 }, (_, i) => {
  const longRel = `Личное/Проекты/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/${PREFIX}Очень длинная страница вики номер ${i + 1} с длинным названием`;
  return i === 0
    ? `- призрак · страница \`Личное/${PREFIX}${AI_PAGES[0]}.md\` · ссылка ведёт в пустоту: страницы с таким ключом нет · связанные: \`${longRel}\`, \`Личное/Проекты/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/Вторая страница с длинным названием\` · счёт: 3`
    : `- призрак · страница \`Личное/${PREFIX}страница-${i}.md\` · ссылка ведёт в пустоту: страницы с таким ключом нет · счёт: 2`;
}).join('\n')}
- нет источников · страница \`Личное/${PREFIX}${AI_PAGES[1]}.md\` · утверждения без сноски · счёт: 4
- нет источников · страница \`Личное/${PREFIX}${AI_PAGES[2]}.md\` · утверждения без сноски · счёт: 2
- нет источников · страница \`Личное/${PREFIX}${AI_PAGES[3]}.md\` · утверждения без сноски · счёт: 1
- сирота · страница \`Личное/${PREFIX}${AI_PAGES[4]}.md\` · на страницу никто не ссылается · счёт: 1
- без изменений · страница \`Личное/${PREFIX}${AI_PAGES[5]}.md\` · страница не менялась · счёт: 120
- почти дубль · страница \`Личное/${PREFIX}${AI_PAGES[0]}.md\` · две страницы об одном · счёт: 9
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

// ─── Засев ───

async function seedWiki(): Promise<void> {
  root = await wikiRoot();
  const pages = path.join(root, 'pages', 'Личное');
  rmSync(pages, { recursive: true, force: true });
  rmSync(path.join(root, 'keeper'), { recursive: true, force: true });

  // Страницы ИИ (6) + карточка ИИ (сирота) — их сотрёт чистка; страница и карточка человека
  // (карточка нужна сноской живой страницы) останутся.
  const manifest: Record<string, string> = {};
  for (const title of AI_PAGES) manifest[`Личное/${title}.md`] = writeFile(path.join(pages, `${title}.md`), pageFile(title));
  manifest[`Личное/${HUMAN_PAGE}.md`] = writeFile(path.join(pages, `${HUMAN_PAGE}.md`), humanPageFile);
  manifest[`Личное/Источники/${AI_CARD}.md`] = writeFile(path.join(pages, 'Источники', `${AI_CARD}.md`), cardFile(AI_CARD, 'session'));
  manifest[`Личное/Источники/${HUMAN_CARD}.md`] = writeFile(path.join(pages, 'Источники', `${HUMAN_CARD}.md`), cardFile(HUMAN_CARD, 'human'));
  writeFile(path.join(root, 'keeper/manifest/personal.json'), JSON.stringify(manifest, null, 2));

  // Отчёт «Здоровья» — проекция раздела (в манифест не входит)
  writeFile(path.join(pages, '_health.md'), HEALTH);

  // Журнал: две даты, разные op и actor, одна строка «ИИ поменял моё» (кнопка «Вернуть мою»)
  const day = (n: number) => `2026-11-${String(n).padStart(2, '0')}T1${n % 10}:0${n % 6}:00.0000000+00:00`;
  const log = [
    { ts: day(13), op: 'apply', section: sectionKey, pages: [`Личное/${AI_PAGES[0]}.md`], source: `session:${sessions[0]?.id ?? 'x'}#m1-m5`, actor: 'ai', policy: 'auto', packages: [PKG_APPLIED], overrodeHuman: true },
    { ts: day(13), op: 'edit', section: sectionKey, pages: [`Личное/${HUMAN_PAGE}.md`], source: 'human:qa-panels-human', actor: 'user', policy: null, packages: [] },
    { ts: day(12), op: 'apply', section: sectionKey, pages: [`Личное/${AI_PAGES[1]}.md`], source: `session:${sessions[1]?.id ?? 'x'}#m1-m3`, actor: 'ai', policy: 'inbox', packages: [PKG_APPLIED] },
    { ts: day(12), op: 'revert', section: sectionKey, pages: [`Личное/${AI_PAGES[2]}.md`], source: `session:${sessions[2]?.id ?? 'x'}#m1-m2`, actor: 'user', policy: null, packages: [PKG_APPLIED] },
    { ts: day(11), op: 'adopt', section: sectionKey, pages: [`Личное/${HUMAN_PAGE}.md`], source: null, actor: 'user', policy: null, packages: [] },
    { ts: day(11), op: 'link', section: sectionKey, pages: [`Личное/${AI_PAGES[3]}.md`], source: `session:${sessions[3]?.id ?? 'x'}#m1-m1`, actor: 'ai', policy: 'auto', packages: [] },
    { ts: day(10), op: 'apply', section: sectionKey, pages: [`Личное/${AI_PAGES[4]}.md`], source: `session:${sessions[4]?.id ?? 'x'}#m1-m4`, actor: 'ai', policy: 'auto', packages: [PKG_APPLIED] },
    { ts: day(10), op: 'edit', section: sectionKey, pages: [`Личное/${HUMAN_PAGE}.md`], source: 'human:qa-panels-human', actor: 'user', policy: null, packages: [] },
  ];
  writeFile(path.join(root, 'keeper/log.jsonl'), log.map(l => JSON.stringify(l)).join('\n') + '\n');

  // Пакеты: два во «Входящих» (один — источник зоны awaiting_human у чата) и один применённый
  const now = new Date();
  const ago = (h: number) => new Date(now.getTime() - h * 3600_000).toISOString();
  const pkg = (id: string, status: string, decision: string, source: string, hours: number, section = sectionKey) => ({
    v: 1, id, author: 'ai', section,
    sources: [{ origin: 'session', ref: source }],
    operations: [{
      op: 'append', page: `Личное/${AI_PAGES[0]}.md`, pageSection: 'Сводка',
      target: null, text: 'Дополнение из чата для проверки панелей.',
      class: 'session×append', decision, reason: decision === 'inbox' ? 'спорно: утверждение без источника' : null,
      baseHashes: {}, inputTokens: null, outputTokens: null,
      humanDecision: null, humanReason: null, judgeDecision: null, judgeReason: null,
      judgeAt: null, judgeCostUsd: null, judgeModel: null, quotes: null, reasonCode: null,
    }],
    status, statusReason: null, createdAt: ago(hours), updatedAt: ago(hours),
  });
  const pkgDir = path.join(root, 'keeper', 'packages');
  const chatForInbox = sessions[8]?.id ?? 'x';
  writeFile(path.join(pkgDir, `${PKG_INBOX1}.json`), JSON.stringify(pkg(PKG_INBOX1, 'proposed', 'inbox', `session:${chatForInbox}#m1-m6`, 5), null, 2));
  writeFile(path.join(pkgDir, `${PKG_INBOX2}.json`), JSON.stringify(pkg(PKG_INBOX2, 'proposed', 'inbox', `session:${sessions[9]?.id ?? 'x'}#m1-m2`, 30), null, 2));
  writeFile(path.join(pkgDir, `${PKG_APPLIED}.json`), JSON.stringify(pkg(PKG_APPLIED, 'applied', 'auto', `session:${sessions[0]?.id ?? 'x'}#m1-m5`, 50), null, 2));

  // Настройки: ручная очередь во «Входящих», 20 классов (3 выключены), ночной блок и итог ночи
  const classes = [
    'note×append', 'file×append', 'url×append', 'task×append', 'session×append', 'dossier×append',
    'memory×append', 'note×create', 'session×create', 'task×create', 'url×create', 'file×create',
    'note×supersede', 'session×supersede', 'file×supersede', 'task×supersede', 'url×supersede',
    'session×conflict', 'note×conflict', 'session×supersede@human',
  ].map((cls, i) => ({
    class: cls,
    disabled: i === 2 || i === 5 || i === 18,
    reason: i === 2 || i === 5 || i === 18 ? 'доля откатов выше порога за 7 дней' : null,
    disabledAt: i === 2 || i === 5 || i === 18 ? ago(48 + i) : null,
    resumedAt: null,
  }));
  writeFile(path.join(root, 'keeper/settings.json'), JSON.stringify({
    v: 1, autoApplyEnabled: false, classes,
    nightly: {
      enabled: true, windowStartHour: 3, windowEndHour: 6,
      maxPackagesPerNight: 20, maxPackagesPerSection: 10, maxChatsPerSection: 15,
      inputTokenBudget: 800_000,
    },
  }, null, 2));
  writeFile(path.join(root, 'keeper/nightly-last.json'), JSON.stringify({
    date: '2026-11-13', selected: 12, analyzed: 9, applied: 5, toInbox: 3, estTokens: 240_000,
    outcome: 'completed', rejected: 1, manual: false,
    judgeCalls: 2, judgeCostUsd: 0.04, judgeApplied: 1, judgeRejected: 1,
  }, null, 2));

  // Стор отбора: 100 чатов — 8 «пропущено после 3 отказов», 1 «ждёт решения», 12 серых,
  // 12 важных (пометка ставится ниже), остальные — «пропущено» со своими причинами
  const entries = sessions.map((s, i) => {
    const base = {
      sessionId: s.id, cursor: i % 7, cursorHash: null, zone: null, reason: null,
      nothingNewStreak: 0, triagedAt: ago(6), score: i % 10,
      signals: [], stage1Zone: 'skipped', stage1Reason: `ступень 1: низкий балл (${i % 10}) — знание не найдено`,
      rejectStreak: 0,
    };
    if (i < 8) return { ...base, zone: 'rejected_skipped', reason: 'пропущено после 3 отказов разбора подряд — вернёмся позже', score: 0 };
    if (i === 8) return { ...base, zone: 'awaiting_human', reason: 'разобранный исход — пакет во «Входящих», ждёт вашего решения', score: 7 };
    if (i < 21) return { ...base, stage1Zone: 'gray', stage1Reason: LONG_REASON, score: 6 + (i % 3) };
    return base;
  });
  writeFile(path.join(root, 'keeper/chat-triage.json'), JSON.stringify(entries, null, 2));
}

async function markImportant(n: number): Promise<void> {
  for (const s of sessions.slice(21, 21 + n)) {
    await client.put(`/api/wiki/chats/${s.id}/mark`, { mark: 'important' });
  }
}

async function openNotes(p: Page): Promise<void> {
  await p.goto(BASE + '/#/notes');
  await p.waitForTimeout(2500);
  const closes = p.locator('button[title="Закрыть"]');
  for (let i = await closes.count(); i > 0; i--) {
    try { await closes.first().click({ timeout: 800 }); } catch { break; }
  }
}

async function openApp(p: Page): Promise<void> {
  await p.goto(BASE + '/#/notes');
  await p.waitForTimeout(2500);
  const closes = p.locator('button[title="Закрыть"]');
  for (let i = await closes.count(); i > 0; i--) {
    try { await closes.first().click({ timeout: 800 }); } catch { break; }
  }
  const tree = p.getByTestId('unified-tree').or(p.getByTestId('wiki-unified-tree')).first();
  // Свежий контекст открывает «Заметки» в режиме вики сам; если нет — ведём кнопкой слева
  // (её accessible name включает счётчик, поэтому имя — по началу) и переключателем «Вики».
  // дерево может грузиться дольше 2,5 с — сначала ждём его, и только потом вмешиваемся кликами
  await tree.waitFor({ state: 'visible', timeout: 12000 }).catch(() => undefined);
  if (!(await tree.isVisible().catch(() => false))) {
    const nav = p.getByRole('button', { name: /^Заметки/ }).first();
    if (await nav.count()) { await nav.click(); await p.waitForTimeout(2500); }
    const wiki = p.getByRole('button', { name: 'Вики', exact: true }).first();
    if (await wiki.count()) { await wiki.click(); await p.waitForTimeout(1500); }
  }
  try {
    await expect(tree).toBeVisible({ timeout: 25000 });
  } catch {
    // Диагностика: что на экране, если дерево не появилось
    mkdirSync(SHOTS, { recursive: true });
    await p.screenshot({ path: path.join(SHOTS, 'openapp-fail.png') });
    writeFileSync(path.join(SHOTS, 'openapp-fail.txt'),
      `url: ${p.url()}\n\n` + (await p.locator('body').innerText()).slice(0, 5000), 'utf8');
    throw new Error('дерево заметок/вики не появилось — см. openapp-fail.txt');
  }
  // Панели «Решения»/«Журнал»/«Здоровье» по умолчанию следят за ОТКРЫТОЙ страницей —
  // открываем первую страницу раздела «Личное», иначе область панели пуста
  const pageRow = p.locator('[data-testid="unified-row"][data-kind="page"]').first();
  if (await pageRow.count()) {
    await pageRow.click();
    await p.waitForTimeout(1500);
  }
}

const PANEL_TITLES: Record<string, string> = {
  wikiInbox: 'Решения', wikiJournal: 'Журнал', wikiHealth: 'Здоровье', wikiSettings: 'Настройки',
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

// Прокручиваемый контейнер панели, в котором живёт элемент
function scrollBoxOf(p: Page, selector: string) {
  return p.evaluate((sel) => {
    const el = document.querySelector(sel) as HTMLElement | null;
    if (!el) return null;
    let e: HTMLElement | null = el.parentElement;
    while (e) {
      const s = getComputedStyle(e);
      if (/(auto|scroll)/.test(s.overflowY) && e.scrollHeight > e.clientHeight + 4) {
        const r = e.getBoundingClientRect();
        return { top: r.top, bottom: r.bottom, scrollTop: e.scrollTop, scrollHeight: e.scrollHeight, clientHeight: e.clientHeight };
      }
      e = e.parentElement;
    }
    return null;
  }, selector);
}

// Сквозной замер: переполнение, обрезанный текст, мелкие цели
function probe(p: Page, smallTargets: boolean) {
  return p.evaluate((small) => {
    const out = { docOverflow: 0, clipped: [] as string[], small: [] as string[] };
    out.docOverflow = document.documentElement.scrollWidth - document.documentElement.clientWidth;
    for (const el of Array.from(document.querySelectorAll('[data-testid^="wiki-"] *')) as HTMLElement[]) {
      if (el.children.length > 0) continue;
      const st = getComputedStyle(el);
      if (st.textOverflow !== 'ellipsis' || st.overflow !== 'hidden') continue;
      if (el.scrollWidth > el.clientWidth + 1) {
        // Многоточие по замыслу (название, путь) — не находка; находка — когда у родителя
        // есть свободное место, а текст всё равно обрезан
        const parent = el.parentElement as HTMLElement | null;
        if (!parent) continue;
        if (parent.children.length > 1) continue; // рядом другие элементы (балл, иконка) — место занято ими
        const ps = getComputedStyle(parent);
        const spare = parent.clientWidth - parseFloat(ps.paddingLeft || '0') - parseFloat(ps.paddingRight || '0') - el.clientWidth;
        if (spare > 4) out.clipped.push(`${(el.textContent || '').trim().slice(0, 40)} [запас ${Math.round(spare)}]`);
      }
    }
    if (small) {
      for (const el of Array.from(document.querySelectorAll('[data-testid^="wiki-"] button, [data-testid^="wiki-"] [role="button"]')) as HTMLElement[]) {
        const r = el.getBoundingClientRect();
        if (!r.width || !r.height) continue;
        if (r.width < 40 || r.height < 40) {
          out.small.push(`${(el.getAttribute('aria-label') || el.textContent || '').trim().slice(0, 24)} = ${Math.round(r.width)}×${Math.round(r.height)}`);
        }
      }
    }
    return out;
  }, smallTargets);
}

test.beforeAll(async ({ playwright }) => {
  request = await playwright.request.newContext({ baseURL: BASE });
  token = await login(request);
  client = api(request, token);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });
  await client.post('/api/wiki/initialize', {});

  // 100 чатов одним проектом (имена — как у владельца, одно длинное)
  const project = await client.post<{ id: string }>('/api/projects', {
    name: `${PREFIX}проект`, rootPath: `/tmp/${PREFIX}${Date.now()}`, createDirectory: true,
  });
  projectId = project.id;
  for (let i = 0; i < CHATS; i++) {
    const name = i === 5 ? LONG_TITLE : `${PREFIX}чат ${String(i + 1).padStart(3, '0')} — про очередь GPU и локальную модель`;
    const s = await client.post<{ id: string }>(`/api/projects/${project.id}/sessions`, { name });
    sessions.push({ id: s.id, name });
  }
  await seedWiki();
  await markImportant(12);
});

test.afterAll(async () => {
  // Уборка: проект с чатами сносит каскад (стор отбора ссылается на исчезнувшие чаты —
  // список их сам отфильтрует); каталог вики стенда остаётся (стенд наш, под нами)
  try { if (projectId) await client.del(`/api/projects/${projectId}`); } catch { /* best-effort */ }
  await request?.dispose();
});

// ─── 1. 1280: «Настройки» в правой зоне 340 (узкая раскладка) ───

test('1280 · «Настройки»: рамка 420, закреплённая шапка, карточки, фильтры, поиск, полоса чистки', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 1280, height: 900 });
  const errors = collectConsoleErrors(p);
  await openApp(p);
  const panel = await openPanel(p, 'wikiSettings');
  await expect(p.getByTestId('wiki-triage-list')).toBeVisible({ timeout: 20000 });

  const pb = await panel.boundingBox();
  expect(pb!.width, 'правая зона при 1280 — узкая (340±20)').toBeGreaterThan(320);
  expect(pb!.width).toBeLessThan(430);

  // (1) рамка списка: предел 420 и свой скролл
  const frame = p.getByTestId('wiki-triage-list');
  const fs = await frame.evaluate(el => {
    const s = getComputedStyle(el);
    return { maxHeight: s.maxHeight, overflowY: s.overflowY, scrollH: el.scrollHeight, clientH: el.clientHeight };
  });
  expect(fs.maxHeight, 'предел высоты — токен LIST_MAX_H').toBe('420px');
  expect(fs.overflowY, 'свой скролл у рамки').toBe('auto');
  expect(fs.scrollH, 'список длиннее рамки').toBeGreaterThan(fs.clientH + 20);

  // Закреплённая шапка: счёт «Показано N из M» остаётся на месте при прокрутке
  const header = p.getByTestId('wiki-triage-header');
  await expect(header).toBeVisible();
  const headText = (await header.innerText()).replace(/\s+/g, ' ').trim();
  expect(headText, 'шапка несёт счёт').toMatch(/^Показано \d+ из 100$/);
  expect(await header.evaluate(el => getComputedStyle(el).position), 'шапка закреплена').toBe('sticky');
  await frame.evaluate(el => { el.scrollTop = 260; });
  await p.waitForTimeout(200);
  const hb = await header.boundingBox();
  const fb = await frame.boundingBox();
  expect(Math.abs(hb!.y - fb!.y), 'после прокрутки шапка у верхней кромки рамки').toBeLessThan(3);

  // (2) карточки вместо таблицы: таблиц нет, название не рвётся, «Почему» до 2 строк
  expect(await panel.locator('table').count(), 'таблиц в панели нет').toBe(0);
  const rowInfo = await p.getByTestId('wiki-triage-row').first().evaluate(el => {
    const title = Array.from(el.querySelectorAll('span')).find(s => (s as HTMLElement).style.fontWeight === '600') as HTMLElement | undefined;
    const t = title ? getComputedStyle(title) : null;
    const why = Array.from(el.querySelectorAll('span')).find(s => (s.textContent || '').startsWith('Почему:')) as HTMLElement | undefined;
    const w = why ? getComputedStyle(why) : null;
    return {
      nowrap: t?.whiteSpace ?? '', ellipsis: t?.textOverflow ?? '', clamped: w?.webkitLineClamp ?? '',
      titleOverflow: title ? title.scrollWidth - title.clientWidth : -1,
      rowOverflow: el.scrollWidth - el.clientWidth,
    };
  });
  expect(rowInfo.nowrap, 'название — в одну строку').toBe('nowrap');
  expect(rowInfo.ellipsis, 'название обрезается многоточием').toBe('ellipsis');
  expect(rowInfo.clamped, '«Почему» — не больше 2 строк').toBe('2');
  expect(rowInfo.rowOverflow, 'карточка не шире рамки').toBeLessThanOrEqual(1);

  // длинное название действительно обрезается, а не растягивает карточку
  const longRow = p.getByTestId('wiki-triage-row').filter({ hasText: PREFIX + 'очень длинное' }).first();
  if (await longRow.count()) {
    const over = await longRow.evaluate(el => {
      const title = Array.from(el.querySelectorAll('span')).find(s => (s as HTMLElement).style.fontWeight === '600') as HTMLElement;
      return title.scrollWidth - title.clientWidth;
    });
    expect(over, 'длинное название обрезано многоточием').toBeGreaterThan(0);
  }

  // (3) фильтры и пустое состояние под фильтром
  const seg = (label: RegExp) => panel.getByRole('button', { name: label }).first();
  await expect(seg(/^Ждут вас · 9$/)).toBeVisible();
  await expect(seg(/^Важные · 12$/)).toBeVisible();
  await expect(seg(/^Все · 100$/)).toBeVisible();
  expect((await header.innerText()).replace(/\s+/g, ' '), 'по умолчанию — «Ждут вас»').toContain('Показано 9 из 100');

  await seg(/^Все · 100$/).click();
  await expect.poll(async () => (await header.innerText()).replace(/\s+/g, ' ')).toContain('Показано 100 из 100');

  // поиск — только при > 10 чатах и вместе с сегментом
  const search = panel.getByPlaceholder('Найти чат по названию');
  await expect(search, 'поиск есть в длинном списке').toBeVisible();
  await search.fill('ничего-не-найдено');
  await expect(p.getByText('Ничего не нашлось по «ничего-не-найдено»')).toBeVisible();
  await p.getByRole('button', { name: 'Сбросить поиск' }).click();
  await expect.poll(async () => (await header.innerText()).replace(/\s+/g, ' ')).toContain('Показано 100 из 100');

  // (4) полоса «Начать вики заново»: видна без прокрутки списка и закреплена внизу
  const open = p.getByTestId('wiki-purge-open');
  await expect(open, 'кнопка чистки видна сразу').toBeVisible();
  // ставим тело панели в начало — так человек и видит «Настройки» при открытии
  await p.evaluate(() => {
    const stripEl = document.querySelector('[data-testid="wiki-purge-open"]') as HTMLElement;
    let e: HTMLElement | null = stripEl?.parentElement ?? null;
    while (e) { if (/(auto|scroll)/.test(getComputedStyle(e).overflowY) && e.scrollHeight > e.clientHeight + 4) { e.scrollTop = 0; break; } e = e.parentElement; }
  });
  await p.waitForTimeout(300);
  const strip = await scrollBoxOf(p, '[data-testid="wiki-purge-open"]');
  const ob = await open.boundingBox();
  const cardBox = await panel.boundingBox();
  expect(ob!.y + ob!.height, 'кнопка внутри видимой части панели').toBeLessThanOrEqual(cardBox!.y + cardBox!.height + 1);
  expect(strip, 'у панели есть свой скролл (список ушёл в рамку)').not.toBeNull();
  expect(strip!.scrollTop, 'сверху списка полоса уже видна — до неё не надо прокручивать').toBeLessThan(5);
  await shot(p, '01-1280-settings-wide-panel.png');

  // «Применение»: рамка классов с пределом, выключенные наверху
  const classes = p.getByTestId('wiki-apply-classes');
  expect((await classes.evaluate(el => getComputedStyle(el).maxHeight)), 'рамка классов тоже 420').toBe('420px');
  expect((await p.getByTestId('wiki-apply-classes-header').innerText()).replace(/\s+/g, ' ')).toContain('Классов: 20 · выключено: 3');
  const firstClass = (await p.getByTestId('wiki-apply-row').first().innerText()).replace(/\s+/g, ' ');
  expect(firstClass, 'выключенный класс — первым').toContain('выключен');

  // «Ночной разбор»: узкие сетки — поля в одну колонку, итог ночи 2×N
  const engineCols = await p.getByTestId('wiki-nightly-engine').evaluate(el => getComputedStyle(el).gridTemplateColumns.split(' ').length);
  expect(engineCols, 'поля ночного разбора — одна колонка').toBe(1);
  const nightCols = await p.getByTestId('wiki-nightly-card').evaluate(el => {
    const grid = el.firstElementChild as HTMLElement;
    return getComputedStyle(grid).gridTemplateColumns.split(' ').length;
  });
  expect(nightCols, 'итог ночи — две колонки').toBe(2);

  const pr = await probe(p, false);
  expect(pr.docOverflow, 'нет горизонтального переполнения документа').toBeLessThanOrEqual(1);
  expect(pr.clipped, 'обрезанного без многоточия текста нет').toEqual([]);
  await ctx.close();
  expect(errors.filter(e => !e.includes('net::ERR_') && !e.includes('Failed to load resource')), 'ошибок консоли нет').toEqual([]);
});

// ─── 2. 1280 · широкая зона 560: узость по контейнеру, а не по окну ───

// Раскладка панели решается по ширине КОНТЕЙНЕРА (П1: узко = < 600), а боковая зона
// ограничена COL_MAX = 560 — значит в боковой зоне панель всегда узкая. Проверяем именно
// это: на растянутой до предела зоне поведение не «переключается» на широкую раскладку.
test('1280 · зона растянута до 560: панель остаётся узкой (П1: порог 600), список тот же', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await ctx.addInitScript(() => {
    localStorage.setItem('cc_notes_zones', JSON.stringify({
      right: { layout: [['wikiSettings']], mode: 'multi', width: 560, colFlex: [], stash: [] },
      left: { layout: [], mode: 'multi', width: 340, colFlex: [], stash: [] },
      weights: {}, home: {}, tucked: [], railOrder: [],
    }));
  });
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 1280, height: 900 });
  await openNotes(p);
  const panel = await openPanel(p, 'wikiSettings');
  await expect(p.getByTestId('wiki-triage-list')).toBeVisible({ timeout: 20000 });
  const w = (await panel.boundingBox())!.width;
  expect(w, 'зона растянута до 560').toBeGreaterThan(500);

  await p.getByRole('button', { name: /^Все · 100$/ }).first().click();
  await p.waitForTimeout(400);
  const children = await p.getByTestId('wiki-triage-row').first().evaluate(el => el.children.length);
  expect(children, '560 всё ещё уже порога 600 — пометки под текстом').toBe(1);
  const engineCols = await p.getByTestId('wiki-nightly-engine').evaluate(el => getComputedStyle(el).gridTemplateColumns.split(' ').length);
  expect(engineCols, 'поля ночного разбора — одна колонка').toBe(1);
  console.log('[wide] зона 560: широкая раскладка недостижима в боковой зоне (COL_MAX=560 < MOBILE_MAX=600)');
  await shot(p, '02-1280-settings-zone560.png');
  await ctx.close();
});

// ─── 3. 1280: «Журнал», «Решения», «Здоровье» в узкой зоне ───

test('1280 · «Журнал»: время в мете, закреплённые фильтры, короткое окно', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 1280, height: 900 });
  const errors = collectConsoleErrors(p);
  await openApp(p);
  await openPanel(p, 'wikiJournal');
  await expect(p.getByTestId('wiki-journal-row').first()).toBeVisible({ timeout: 20000 });

  const row = p.getByTestId('wiki-journal-row').first();
  const info = await row.evaluate(el => {
    const meta = el.querySelector('span[style*="mono"]') as HTMLElement | null;
    return { children: el.children.length, meta: (meta?.textContent || '') };
  });
  expect(info.meta, 'время — частью строки меты').toMatch(/·\s*\d{2}:\d{2}/);
  expect(info.children, 'колонки времени справа нет').toBe(2);

  const bar = p.getByTestId('wiki-journal-bar');
  expect(await bar.evaluate(el => getComputedStyle(el).position), 'фильтры закреплены').toBe('sticky');
  const box = await scrollBoxOf(p, '[data-testid="wiki-journal-bar"]');
  expect(box, 'у журнала есть свой скролл').not.toBeNull();
  await p.evaluate(() => {
    const bar2 = document.querySelector('[data-testid="wiki-journal-bar"]') as HTMLElement;
    let e: HTMLElement | null = bar2?.parentElement ?? null;
    while (e) { if (/(auto|scroll)/.test(getComputedStyle(e).overflowY) && e.scrollHeight > e.clientHeight + 4) { e.scrollTop = 400; break; } e = e.parentElement; }
  });
  await p.waitForTimeout(300);
  const bb = await bar.boundingBox();
  const bodyTop = (await panelCard(p, 'Журнал').first().boundingBox())!.y;
  expect(bb!.y - bodyTop, 'при прокрутке ряд фильтров остаётся у верха панели').toBeLessThan(160);
  await shot(p, '03-1280-journal.png');
  await ctx.close();
  expect(errors.filter(e => !e.includes('net::ERR_') && !e.includes('Failed to load resource'))).toEqual([]);
});

test('1280 · «Решения»: возраст в мете, плашка сворачивается и помнит выбор', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 1280, height: 900 });
  await openApp(p);
  await openPanel(p, 'wikiInbox');
  await expect(p.getByTestId('wiki-pkg-row').first()).toBeVisible({ timeout: 20000 });

  const row = p.getByTestId('wiki-pkg-row').first();
  const info = await row.evaluate(el => (el.textContent || '').replace(/\s+/g, ' '));
  expect(info, 'возраст пакета — в строке меты').toMatch(/·\s*\d+\s*(ч|дн|мин)/);
  expect(await p.getByTestId('wiki-pkg-row').count(), 'во «Входящих» два пакета').toBe(2);

  const notice = p.getByTestId('wiki-inbox-notice');
  await expect(notice, 'плашка есть').toBeVisible();
  await p.getByTestId('wiki-inbox-notice-collapse').click();
  await expect(p.getByTestId('wiki-inbox-notice-expand'), 'после «Понятно» — одна строка с «Подробнее»').toBeVisible();
  const shortHeight = (await notice.boundingBox())!.height;

  // перезагрузка: свёрнутость помнится
  await p.reload();
  await openApp(p); // после перезагрузки приложение стартует на «Чатах» — раздел открываем заново
  await openPanel(p, 'wikiInbox');
  await expect(p.getByTestId('wiki-inbox-notice-expand'), 'свёртка держится после перезагрузки').toBeVisible({ timeout: 20000 });
  expect((await p.getByTestId('wiki-inbox-notice').boundingBox())!.height, 'плашка осталась короткой').toBeLessThanOrEqual(shortHeight + 4);
  const stored = await p.evaluate(() => localStorage.getItem('wiki.inbox.noticeCollapsed'));
  expect(stored, 'выбор записан в localStorage').toBe('1');
  await shot(p, '04-1280-inbox.png');
  await ctx.close();
});

test('1280 · «Здоровье»: 2 счётчика, карточка находки, «Показать ещё N», длинный путь не рвёт панель', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 1280, height: 900 });
  await openApp(p);
  const panel = await openPanel(p, 'wikiHealth');
  await expect(p.getByTestId('wiki-health-row').first()).toBeVisible({ timeout: 20000 });

  const cols = await p.getByTestId('wiki-health-counters').evaluate(el => getComputedStyle(el).gridTemplateColumns.split(' ').length);
  expect(cols, 'узко — два счётчика в ряд').toBe(2);

  const pathStyle = await p.getByTestId('wiki-health-row-path').first().evaluate(el => {
    const s = getComputedStyle(el);
    return { mono: s.fontFamily, nowrap: s.whiteSpace, ellipsis: s.textOverflow };
  });
  expect(pathStyle.nowrap, 'путь — одна строка').toBe('nowrap');
  expect(pathStyle.ellipsis, 'путь обрезается многоточием').toBe('ellipsis');

  await expect(p.getByTestId('wiki-health-row-meta').first(), 'мета находки на месте').toBeVisible();
  expect(await p.getByTestId('wiki-health-group-dangling').getByTestId('wiki-health-row').count(), 'в группе «призрак» показаны 5').toBe(5);
  const more = p.getByTestId('wiki-health-expand-dangling');
  await expect(more, 'кнопка «Показать ещё 35»').toHaveText(/Показать ещё 35/);
  await more.click();
  await p.waitForTimeout(300);
  expect(await p.getByTestId('wiki-health-group-dangling').getByTestId('wiki-health-row').count(), 'после раскрытия — 40 находок группы').toBe(40);

  // риск (а): длинный «связанные: …» в мете — не вылезает ли за край панели
  const risk = await panel.evaluate(el => {
    const meta = el.querySelector('[data-testid="wiki-health-row-meta"]') as HTMLElement | null;
    return {
      panelOverflow: el.scrollWidth - el.clientWidth,
      metaOverflow: meta ? meta.scrollWidth - meta.clientWidth : 0,
      metaText: meta ? (meta.textContent || '').slice(0, 60) : '',
    };
  });
  const pr = await probe(p, false);
  await shot(p, '05-1280-health.png');
  expect(pr.docOverflow, 'документ без горизонтального переполнения').toBeLessThanOrEqual(1);
  expect(risk.panelOverflow, 'мета не вылезает за панель (wordBreak сохранён)').toBeLessThanOrEqual(1);
  expect([risk.metaOverflow, risk.metaText], 'длинные «связанные» переносятся внутри меты').toEqual([expect.any(Number), expect.any(String)]);
  console.log('[health-risk]', JSON.stringify(risk));
  await ctx.close();
});

// ─── 4. тёмная тема на 1280 ───

test('1280 · тёмная тема: «Настройки» и «Здоровье» по тёмным токенам', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await ctx.addInitScript(() => localStorage.setItem('cc_theme', 'dark'));
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 1280, height: 900 });
  await openApp(p);
  await p.evaluate(() => document.documentElement.setAttribute('data-theme', 'dark'));
  await openPanel(p, 'wikiSettings');
  await expect(p.getByTestId('wiki-triage-list')).toBeVisible({ timeout: 20000 });
  const bg = await p.evaluate(() => getComputedStyle(document.body).backgroundColor);
  const lum = (bg.match(/\d+/g) ?? ['255', '255', '255']).slice(0, 3).map(Number).reduce((a, b) => a + b, 0) / 3;
  expect(lum, `фон страницы в тёмной теме тёмный (получено ${bg})`).toBeLessThan(80);
  await shot(p, '06-1280-settings-dark.png');
  await p.locator('[data-rail-item="wikiSettings"] button').click();
  await openPanel(p, 'wikiHealth');
  await expect(p.getByTestId('wiki-health-row').first()).toBeVisible({ timeout: 20000 });
  await shot(p, '07-1280-health-dark.png');
  await ctx.close();
});

// ─── 4б. Снимки списка «Отбор чатов» (карточки глазами): 340 в зоне, закреплённая шапка, 360 ───

test('снимки списка «Отбор чатов»: карточки на 340 и 360, шапка закреплена при прокрутке рамки', async ({ browser }) => {
  const scrollToTriage = (p: Page) => p.evaluate(() => {
    const el = document.querySelector('[data-testid="wiki-triage-list"]') as HTMLElement;
    let e: HTMLElement | null = el.parentElement;
    while (e) {
      if (/(auto|scroll)/.test(getComputedStyle(e).overflowY) && e.scrollHeight > e.clientHeight + 4) {
        e.scrollTop = el.getBoundingClientRect().top - e.getBoundingClientRect().top + e.scrollTop - 90; break;
      }
      e = e.parentElement;
    }
  });
  {
    const ctx = await browser.newContext({ baseURL: BASE });
    await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
    const p = await ctx.newPage();
    await p.setViewportSize({ width: 1280, height: 900 });
    await openApp(p);
    await openPanel(p, 'wikiSettings');
    await expect(p.getByTestId('wiki-triage-list')).toBeVisible({ timeout: 20000 });
    await p.mouse.move(600, 450);
    await scrollToTriage(p);
    await p.waitForTimeout(400);
    await p.screenshot({ path: path.join(SHOTS, '14-1280-triage-card-list.png'), clip: { x: 880, y: 50, width: 370, height: 850 } });
    await p.getByRole('button', { name: /^Все · 100$/ }).first().click();
    await p.waitForTimeout(400);
    await p.evaluate(() => { (document.querySelector('[data-testid="wiki-triage-list"]') as HTMLElement).scrollTop = 600; });
    await p.waitForTimeout(300);
    await p.screenshot({ path: path.join(SHOTS, '15-1280-triage-scrolled-sticky.png'), clip: { x: 880, y: 50, width: 370, height: 850 } });
    await ctx.close();
  }
  {
    const ctx = await browser.newContext({ baseURL: BASE });
    await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
    const p = await ctx.newPage();
    await p.setViewportSize({ width: 360, height: 800 });
    await openNotes(p);
    const wikiToggle = p.getByRole('button', { name: 'Вики', exact: true }).first();
    if (await wikiToggle.count()) { await wikiToggle.click(); await p.waitForTimeout(1200); }
    await p.getByTestId('wiki-open-panel').click();
    await p.getByRole('button', { name: 'Настройки', exact: true }).first().click();
    await expect(p.getByTestId('wiki-triage-list')).toBeVisible({ timeout: 20000 });
    await scrollToTriage(p);
    await p.waitForTimeout(400);
    await p.screenshot({ path: path.join(SHOTS, '16-360-triage-card-list.png') });
    // пометки на телефоне — тач-цель не меньше 40
    const marks = await p.locator('[data-testid^="wiki-triage-mark-"]').evaluateAll(els =>
      els.slice(0, 6).map(e => { const r = e.getBoundingClientRect(); return [Math.round(r.width), Math.round(r.height)]; }));
    expect(marks.length, 'в списке есть кнопки пометок').toBeGreaterThan(0);
    for (const [w, h] of marks) expect(h, `высота кнопки пометки ${w}×${h} ≥ 40`).toBeGreaterThanOrEqual(39);
    await ctx.close();
  }
});

// ─── 5. 360: шторка мобилы, полоса чистки, «Скрыть» ≥ 40, чистка до конца ───

test('360 · шторка: полоса чистки видна без прокрутки, «Скрыть» ≥ 40 px, чистка и предпросмотр работают', async ({ browser }) => {
  const ctx = await browser.newContext({ baseURL: BASE });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  const p = await ctx.newPage();
  await p.setViewportSize({ width: 360, height: 800 });
  const errors = collectConsoleErrors(p);
  await p.goto(BASE + '/#/notes');
  await p.waitForTimeout(2500);
  const wikiToggle = p.getByRole('button', { name: 'Вики', exact: true }).first();
  if (await wikiToggle.count()) { await wikiToggle.click(); await p.waitForTimeout(1200); }
  await expect(p.getByTestId('wiki-open-panel')).toBeVisible({ timeout: 25000 });
  await p.getByTestId('wiki-open-panel').click();
  await expect(p.getByTestId('wiki-drawer-right')).toBeVisible({ timeout: 20000 });
  await p.getByRole('button', { name: 'Настройки', exact: true }).first().click();
  await expect(p.getByTestId('wiki-triage-list')).toBeVisible({ timeout: 20000 });

  // полоса видна сразу, текст не обрезан
  const open = p.getByTestId('wiki-purge-open');
  await expect(open, 'кнопка чистки видна без прокрутки').toBeVisible();
  const text = p.getByText('Удалить страницы ИИ и начать заново', { exact: true });
  const tt = await text.evaluate(el => {
    const s = getComputedStyle(el as HTMLElement);
    return { h: (el as HTMLElement).clientHeight, scrollH: (el as HTMLElement).scrollHeight, clamp: s.webkitLineClamp };
  });
  expect(tt.scrollH, 'подпись полосы не обрезана').toBeLessThanOrEqual(tt.h + 1);
  await shot(p, '08-360-settings-drawer.png');

  // предпросмотр и чистка
  await open.click();
  await expect(p.getByText('Начать вики заново?')).toBeVisible();
  const confirm = p.getByRole('button', { name: /^Удалить \d+ страниц/ }).first();
  const confirmText = (await confirm.innerText()).trim();
  const n = Number(confirmText.match(/(\d+)/)?.[1] ?? '0');
  expect(n, 'предпросмотр насчитал страницы ИИ').toBeGreaterThan(0);
  await shot(p, '09-360-purge-preview.png');
  await confirm.click();
  await expect(p.getByText(/Удалено \d+ страниц/).first(), 'итог чистки в полосе').toBeVisible({ timeout: 30000 });
  await p.getByRole('button', { name: 'Закрыть', exact: true }).last().click();
  await expect(p.getByTestId('wiki-purge-status'), 'итог остался строкой полосы').toBeVisible();

  const hide = p.locator('[data-testid="wiki-purge-status"] button[aria-label="Скрыть"]');
  const hb = await hide.boundingBox();
  expect(hb!.width, '× на телефоне — не меньше 40').toBeGreaterThanOrEqual(39);
  expect(hb!.height).toBeGreaterThanOrEqual(39);
  await shot(p, '10-360-purge-result.png');
  await hide.click();
  await expect(p.getByTestId('wiki-purge-status')).toHaveCount(0);

  // остальные панели в шторке: снимки и замер
  for (const [tab, file] of [['Решения', '11-360-inbox-drawer.png'], ['Журнал', '12-360-journal-drawer.png'], ['Здоровье', '13-360-health-drawer.png']] as const) {
    await p.getByRole('button', { name: tab, exact: true }).first().click();
    await p.waitForTimeout(900);
    await shot(p, file);
  }
  const target = await p.getByTestId('wiki-open-panel').boundingBox();
  expect(target!.width, 'кнопка шторки панели — тач-цель').toBeGreaterThanOrEqual(39);
  const pr = await probe(p, true);
  expect(pr.small, 'мелких целей касания в панелях нет').toEqual([]);
  await ctx.close();
  expect(errors.filter(e => !e.includes('net::ERR_') && !e.includes('Failed to load resource'))).toEqual([]);
});
