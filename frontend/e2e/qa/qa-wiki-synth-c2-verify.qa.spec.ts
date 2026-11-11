// QA С2 · ночной синтез (задача b98fee32, эпик 66b5e075). Спек 3/3: проверка результатов ночи.
//
// Сценарии: 1 (сводка своими словами + цитаты-доказательства), 2 (виды страниц и as_of),
// 4 (одна цитата — одна страница), 6 (правка шапки человеком: сводка не стирает as_of,
// правка as_of отклоняется). Сценарий 5 (ночь без облака) — отдельный спек.
//
// Утверждения и цитаты в REST-контракте пакета не отдаются (DTO операции несёт только
// текст/решение), поэтому claim/quotes/kind/as_of берём из файлов пакета на диске.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const ATT = path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2');

type SeedChat = { unit: string; key: string; label: string; title: string; sessionId: string };
type Seed = { ownerId: string; projectId: string; dataDir: string; chats: SeedChat[] };
const seed = JSON.parse(fs.readFileSync(path.join(ATT, 'seed.json'), 'utf8')) as Seed;
const SECTION = `projects/${seed.projectId}`;
const WIKI = path.join(seed.dataDir, 'wiki', seed.ownerId);

type Op = {
  op: string; page: string; kind?: string; asOf?: string; claim?: string; text?: string;
  quotes?: string[] | null; decision: string; reason?: string;
};
type Pkg = {
  id: string; status: string; section: string; sources: { origin: string; ref: string }[];
  operations: Op[];
};

let request: APIRequestContext;
let client: Api;
let token = '';
let packages: Pkg[] = [];
let pages: { path: string; content: string; hash: string }[] = [];

test.setTimeout(300_000);

const chatText = (sessionId: string): string => {
  const p = path.join(seed.dataDir, 'sessions', sessionId, 'history.json');
  if (!fs.existsSync(p)) return '';
  try {
    const msgs = JSON.parse(fs.readFileSync(p, 'utf8')) as { text?: string }[];
    return msgs.map(m => m.text ?? '').join('\n');
  } catch { return ''; }
};


// Канонизация «как рендер» — та же, что у QuoteMatcher.NormalizeForMatch: маркеры
// эмфазиса/кода выброшены, whitespace схлопнут, двойные кавычки свёрнуты. Дословность
// цитаты по контракту продукта — «с точностью до пробелов, кавычек и разметки».
const canon = (s: string): string =>
  (s ?? '').replace(/[*`~\\]/g, '').replace(/"/g, "'").replace(/\s+/g, ' ').trim();

const frontmatter = (content: string): Record<string, string> => {
  const m = /^---\n([\s\S]*?)\n---\n/.exec(content);
  const fm: Record<string, string> = {};
  if (!m) return fm;
  for (const line of m[1].split('\n')) {
    const i = line.indexOf(':');
    if (i > 0) fm[line.slice(0, i).trim()] = line.slice(i + 1).trim().replace(/^"|"$/g, '');
  }
  return fm;
};

const titleOf = (content: string, path: string): string =>
  /^#\s+(.+)$/m.exec(content)?.[1]?.trim() ?? path.basename(path, '.md');

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });

  // Файлы пакетов: в них claim/quotes/kind/asOf (REST их не отдаёт).
  const pkgDir = path.join(WIKI, 'keeper', 'packages');
  packages = fs.existsSync(pkgDir)
    ? fs.readdirSync(pkgDir).filter(f => f.endsWith('.json'))
      .map(f => JSON.parse(fs.readFileSync(path.join(pkgDir, f), 'utf8')) as Pkg)
    : [];

  const cat = await client.get<{ pages?: { pageKey: string }[] }>(
    `/api/wiki/section?section=${encodeURIComponent(SECTION)}`);
  const pagePaths = (cat.pages ?? []).map(p => p.pageKey).filter(p => !p.split('/').pop()!.startsWith('_'));
  for (const p of pagePaths) {
    const full = await client.get<{ content: string; hash: string }>(
      `/api/wiki/page?section=${encodeURIComponent(SECTION)}&page=${encodeURIComponent(p)}`);
    pages.push({ path: p, content: full.content, hash: full.hash });
  }
});

test.afterAll(async () => {
  await request?.dispose();
});

test('ночь дала страницы по видам знания', async () => {
  expect(packages.length, 'пакеты ночи').toBeGreaterThan(0);
  const types = pages.map(p => frontmatter(p.content).type).filter(Boolean);
  expect(types.length, 'страниц с разобранной шапкой').toBeGreaterThan(0);
  const allowed = ['decision', 'rule', 'term', 'state', 'source', 'chronicle', 'overview', 'topic', 'entity', 'concept'];
  for (const t of types) expect(allowed, `вид страницы «${t}»`).toContain(t);
  for (const p of pages) {
    const fm = frontmatter(p.content);
    if (fm.type === 'state') expect(fm.as_of, `as_of у страницы «${p.path}»`).toBeTruthy();
  }
});

test('цитаты дословны и одна цитата — одна страница', async () => {
  const owner = new Map<string, string>();
  for (const pkg of packages) {
    const sid = (pkg.sources ?? []).map(s => s.ref).find(r => r.startsWith('session:'))?.slice(8).split('#')[0] ?? '';
    const text = chatText(sid);
    for (const op of pkg.operations) {
      if (op.op !== 'create') continue;
      for (const q of op.quotes ?? []) {
        const qq = q.trim();
        expect(qq.length, `длина цитаты «${qq.slice(0, 40)}»`).toBeGreaterThanOrEqual(8);
        expect(qq.length, `длина цитаты «${qq.slice(0, 40)}»`).toBeLessThanOrEqual(300);
        expect(canon(text).includes(canon(qq)), `цитата дословно есть в чате ${sid}: «${qq.slice(0, 60)}»`).toBeTruthy();
        const prev = owner.get(qq);
        expect(prev, `цитата «${qq.slice(0, 40)}» принадлежит двум страницам (${prev} и ${op.page})`).toBeFalsy();
        owner.set(qq, op.page);
      }
    }
  }
});

test('утверждение — сводка своими словами, а не копия цитаты', async () => {
  let checked = 0;
  for (const pkg of packages) {
    for (const op of pkg.operations) {
      if (!op.claim) continue;
      for (const q of op.quotes ?? []) {
        const a = op.claim.trim().toLowerCase();
        const b = q.trim().toLowerCase();
        const copy = a === b || (a.includes(b) && b.length > 40);
        expect(copy, `утверждение не копия цитаты (страница ${op.page})`).toBeFalsy();
        checked++;
      }
    }
  }
  expect(checked, 'проверенных пар «утверждение — цитата»').toBeGreaterThan(0);
});

test('числа утверждения есть в цитатах', async () => {
  const num = (s: string) => (s.match(/\d+(?:[.,]\d+)?/g) ?? []).map(n => n.replace(',', '.'));
  for (const pkg of packages) {
    for (const op of pkg.operations) {
      if (!op.claim) continue;
      const inQuotes = new Set((op.quotes ?? []).flatMap(q => num(q)));
      const extra = num(op.claim).filter(n => !inQuotes.has(n));
      expect(extra, `числа вне цитат (страница ${op.page}): ${extra.join(', ')}`).toEqual([]);
    }
  }
});

test('правка сводки человеком не стирает as_of, правка as_of отклоняется', async () => {
  // Страница вида state с as_of: в ночи такие не родились (см. отчёт), поэтому заводим её
  // руками человека (POST /api/wiki/pages/edit без baseHash — создание страницы).
  const pagePath = `Проекты/${seed.projectId}/qa-c2-проверка-состояния.md`;
  const created = `---
type: state
as_of: 2026-11-11
summary: состояние, заведённое человеком (QA С2)
aliases: []
owns: []
sources: []
status: active
---

# Проверка состояния (QA С2)

## Сводка
Состояние на 11.11.2026 — заведено человеком для проверки правки шапки.

## Хронология

## Противоречия

## История
`;
  const c = await client.post<{ status?: string; hash?: string }>('/api/wiki/pages/edit', {
    section: SECTION, page: pagePath, content: created,
  });
  expect(['applied', 'unchanged'], `страница состояния заведена: ${JSON.stringify(c)}`).toContain(c.status);
  const fresh = await client.get<{ content: string; hash: string }>(
    `/api/wiki/page?section=${encodeURIComponent(SECTION)}&page=${encodeURIComponent(pagePath)}`);
  const fm = frontmatter(fresh.content);
  expect(fm.type).toBe('state');
  expect(fm.as_of, 'as_of заведён').toBe('2026-11-11');

  // Правка сводки человеком — as_of не стирается.
  const edited = fresh.content.replace(/^summary:.*$/m, 'summary: сводка, правленная человеком (QA С2)');
  const res = await client.post<{ status?: string; hash?: string }>('/api/wiki/pages/edit', {
    section: SECTION, page: pagePath, baseHash: fresh.hash, content: edited,
  });
  expect(['applied', 'unchanged'], `правка сводки принята: ${JSON.stringify(res)}`).toContain(res.status);
  const after = await client.get<{ content: string; hash: string }>(
    `/api/wiki/page?section=${encodeURIComponent(SECTION)}&page=${encodeURIComponent(pagePath)}`);
  const fm2 = frontmatter(after.content);
  expect(fm2.as_of, 'as_of уцелел после правки сводки').toBe('2026-11-11');
  expect(fm2.summary, 'сводка обновилась').toContain('правленная человеком');

  // Правка as_of человеком — отказ.
  const bad = after.content.replace(/^as_of:.*$/m, 'as_of: 2020-01-01');
  let refused = false;
  let message = '';
  try {
    await client.post('/api/wiki/pages/edit', {
      section: SECTION, page: pagePath, baseHash: after.hash, content: bad,
    });
  } catch (e) {
    refused = true;
    message = String(e);
  }
  expect(refused, 'правка as_of отклонена').toBeTruthy();
  expect(message, 'причина отказа называет as_of').toContain('as_of');
});

test('страница открывается в интерфейсе (плашка шапки по виду страницы)', async ({ page, context }) => {
  // Каталог — свежим: страницу вида state завёл предыдущий тест.
  const cat = await client.get<{ pages?: { pageKey: string }[] }>(
    `/api/wiki/section?section=${encodeURIComponent(SECTION)}`);
  const keys = (cat.pages ?? []).map(p => p.pageKey).filter(p => !p.split('/').pop()!.startsWith('_'));
  const withType: { path: string; type: string; title: string }[] = [];
  for (const k of keys) {
    const full = await client.get<{ content: string }>(
      `/api/wiki/page?section=${encodeURIComponent(SECTION)}&page=${encodeURIComponent(k)}`);
    const fm = frontmatter(full.content);
    withType.push({ path: k, type: fm.type ?? '', title: titleOf(full.content, k) });
  }
  const probe = async (target: { path: string; type: string; title: string }) => {
    await context.addInitScript(t => localStorage.setItem('cc_token', t as string), token);
    await page.goto('/#/notes');
    await expect(page.locator('#root')).not.toBeEmpty();
    const row = page.locator('[data-testid="unified-row"]', { hasText: target.title.slice(0, 30) }).first();
    await expect(row, `строка «${target.title}» видна в дереве вики`).toBeVisible({ timeout: 30000 });
    await row.click();
    await expect(page.getByTestId('wiki-doc')).toBeVisible({ timeout: 30000 });
    const badge = page.getByTitle('Шапка страницы не разобрана — показано имя файла');
    return badge.count();
  };

  const decision = withType.find(p => p.type === 'decision');
  const state = withType.find(p => p.type === 'state');
  const out: Record<string, unknown> = {};
  if (decision) {
    const n = await probe(decision);
    out[decision.path] = { type: decision.type, badgeCount: n };
    // Вид, который фронт знает, — плашки быть не должно.
    expect(n, `у страницы вида decision (${decision.path}) плашки нет`).toBe(0);
  }
  if (state) {
    const n = await probe(state);
    out[state.path] = { type: state.type, badgeCount: n };
  }
  fs.writeFileSync(path.join(ATT, 'ui-badge.json'), JSON.stringify(out, null, 1));
  expect(Object.keys(out).length, 'плашки зафиксированы в ui-badge.json').toBeGreaterThan(0);
});
