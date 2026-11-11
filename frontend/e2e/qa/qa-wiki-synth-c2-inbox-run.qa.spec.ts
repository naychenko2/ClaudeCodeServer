// QA С2 · «Входящие», применение пакета, сценарий 6 и плашка шапки (задача b98fee32, эпик 66b5e075).
// Один спек на группу сценариев: ночь → «Входящие» → применение → правка шапки → интерфейс.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const ATT = path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2');
type Seed = { ownerId: string; projectId: string; dataDir: string; chats: { key: string; sessionId: string }[] };
const seed = JSON.parse(fs.readFileSync(path.join(ATT, 'seed.json'), 'utf8')) as Seed;
const inboxSeed = JSON.parse(fs.readFileSync(path.join(ATT, 'inbox-seed.json'), 'utf8')) as { chatId: string };
const SECTION = `projects/${seed.projectId}`;
const WIKI = path.join(seed.dataDir, 'wiki', seed.ownerId);

let request: APIRequestContext;
let client: Api;
let token = '';

test.setTimeout(420_000);

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

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });
});

test.afterAll(async () => {
  await request?.dispose();
});

test('ночь разбирает уменьшенное окно: пакет уходит во «Входящие» с причинами', async () => {
  const run = await client.post<{ started: boolean }>('/api/wiki/settings/nightly/run');
  expect(run.started, 'ночь запущена').toBe(true);
  const last = path.join(WIKI, 'keeper', 'nightly-last.json');
  const before = fs.existsSync(last) ? fs.readFileSync(last, 'utf8') : '';
  let text = before;
  const deadline = Date.now() + 5 * 60 * 1000;
  while (Date.now() < deadline) {
    await new Promise(r => setTimeout(r, 5000));
    if (!fs.existsSync(last)) continue;
    text = fs.readFileSync(last, 'utf8');
    if (text !== before) break;
  }
  expect(text, 'итог прохода записан').not.toBe(before);
  const result = JSON.parse(text) as { selected?: number; analyzed?: number; applied?: number; toInbox?: number; rejected?: number; firstRejectReason?: string | null };
  fs.writeFileSync(path.join(ATT, 'inbox-night-result.json'), JSON.stringify(result, null, 1), 'utf8');
  expect(result.analyzed ?? 0, 'чат разобран').toBeGreaterThan(0);
  expect(result.rejected ?? 0, 'отказов нет').toBe(0);
  expect(result.toInbox ?? 0, 'пакет во «Входящих»').toBeGreaterThan(0);

  const list = await client.get<{ packages: { id: string; status: string; operations: { decision: string; reason?: string; kind?: string; claim?: string; quotes?: string[] }[] }[] }>(
    '/api/wiki/packages?inbox=true');
  const pkg = (list.packages ?? [])[0];
  expect(pkg, 'пакет во «Входящих» есть').toBeTruthy();
  const reasons = pkg.operations.map(o => o.reason ?? '');
  fs.writeFileSync(path.join(ATT, 'inbox-reasons.json'), JSON.stringify(reasons, null, 1), 'utf8');
  expect(reasons.filter(r => r.includes('число не подтверждено цитатой')).length,
    'операция ушла во «Входящие» с причиной про число').toBeGreaterThan(0);
  // У каждой операции — сводка своими словами и 1–3 дословные цитаты.
  for (const op of pkg.operations) {
    expect(op.claim?.length ?? 0, 'у утверждения есть текст').toBeGreaterThan(0);
    expect(op.quotes?.length ?? 0, 'у утверждения есть цитаты').toBeGreaterThan(0);
    expect(op.quotes?.length ?? 0, 'цитат не больше трёх').toBeLessThanOrEqual(3);
  }
});

test('применение пакета: страницы видов decision/rule/state, у state есть as_of', async () => {
  const list = await client.get<{ packages: { id: string }[] }>('/api/wiki/packages?inbox=true');
  const pkg = (list.packages ?? [])[0];
  const res = await client.post<{ apply?: { applied?: boolean } }>(`/api/wiki/packages/${pkg.id}/accept-all`);
  expect(res, 'пакет принят').toBeTruthy();

  const cat = await client.get<{ pages?: { pageKey: string }[] }>(
    `/api/wiki/section?section=${encodeURIComponent(SECTION)}`);
  const keys = (cat.pages ?? []).map(p => p.pageKey);
  const types: Record<string, string> = {};
  const statePages: string[] = [];
  for (const k of keys) {
    const full = await client.get<{ content: string }>(
      `/api/wiki/page?section=${encodeURIComponent(SECTION)}&page=${encodeURIComponent(k)}`);
    const fm = frontmatter(full.content);
    types[k] = fm.type ?? '';
    if (fm.type === 'state') {
      expect(fm.as_of, `as_of у страницы вида state «${k}»`).toBeTruthy();
      statePages.push(k);
    }
  }
  fs.writeFileSync(path.join(ATT, 'applied-types.json'), JSON.stringify(types, null, 1), 'utf8');
  expect(Object.values(types).filter(t => t === 'decision').length, 'страницы вида decision').toBeGreaterThan(0);
  expect(Object.values(types).filter(t => t === 'rule').length, 'страницы вида rule').toBeGreaterThan(0);
  expect(statePages.length, 'страницы вида state').toBeGreaterThan(0);
});

test('правка сводки человеком не стирает as_of, правка as_of отклоняется', async () => {
  const cat = await client.get<{ pages?: { pageKey: string }[] }>(
    `/api/wiki/section?section=${encodeURIComponent(SECTION)}`);
  let target = ''; let fm: Record<string, string> = {}; let content = ''; let hash = '';
  for (const k of (cat.pages ?? []).map(p => p.pageKey)) {
    const full = await client.get<{ content: string; hash: string }>(
      `/api/wiki/page?section=${encodeURIComponent(SECTION)}&page=${encodeURIComponent(k)}`);
    const f = frontmatter(full.content);
    if (f.type === 'state' && f.as_of) { target = k; fm = f; content = full.content; hash = full.hash; break; }
  }
  expect(target, 'есть страница вида state с as_of').toBeTruthy();
  const marker = `правленная человеком (QA С2 #${Date.now()})`;
  const summaryLine = `summary: сводка, ${marker}`;
  const edited = /^summary:/m.test(content)
    ? content.replace(/^summary:.*$/m, summaryLine)
    : content.replace(/^---\n/, `---\n${summaryLine}\n`);
  const res = await client.post<{ status?: string }>('/api/wiki/pages/edit', {
    section: SECTION, page: target, baseHash: hash, content: edited,
  });
  expect(res.status, `правка сводки принята: ${JSON.stringify(res)}`).toBe('applied');
  const after = await client.get<{ content: string; hash: string }>(
    `/api/wiki/page?section=${encodeURIComponent(SECTION)}&page=${encodeURIComponent(target)}`);
  const fm2 = frontmatter(after.content);
  expect(fm2.as_of, 'as_of уцелел после правки сводки').toBe(fm.as_of);
  expect(fm2.summary, 'сводка обновилась').toContain(marker);

  const bad = after.content.replace(/^as_of:.*$/m, 'as_of: 2020-01-01');
  let refused = false; let message = '';
  try {
    await client.post('/api/wiki/pages/edit', {
      section: SECTION, page: target, baseHash: after.hash, content: bad,
    });
  } catch (e) { refused = true; message = String(e); }
  expect(refused, 'правка as_of отклонена').toBeTruthy();
  expect(message, 'причина отказа называет as_of').toContain('as_of');
});

test('интерфейс: страница открывается, плашка шапки по виду', async ({ page, context }) => {
  const cat = await client.get<{ pages?: { pageKey: string }[] }>(
    `/api/wiki/section?section=${encodeURIComponent(SECTION)}`);
  const kinds: { path: string; type: string; title: string }[] = [];
  for (const k of (cat.pages ?? []).map(p => p.pageKey)) {
    const full = await client.get<{ content: string }>(
      `/api/wiki/page?section=${encodeURIComponent(SECTION)}&page=${encodeURIComponent(k)}`);
    const fm = frontmatter(full.content);
    kinds.push({ path: k, type: fm.type ?? '', title: /^#\s+(.+)$/m.exec(full.content)?.[1]?.trim() ?? k });
  }
  const probe = async (t: { path: string; type: string; title: string }) => {
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
    await page.goto('/#/notes');
    await expect(page.locator('#root')).not.toBeEmpty();
    const row = page.locator('[data-testid="unified-row"]', { hasText: t.title.slice(0, 30) }).first();
    await expect(row, `строка «${t.title}» видна в дереве вики`).toBeVisible({ timeout: 30000 });
    await row.click();
    await expect(page.getByTestId('wiki-doc')).toBeVisible({ timeout: 30000 });
    return page.getByTitle('Шапка страницы не разобрана — показано имя файла').count();
  };
  const out: Record<string, unknown> = {};
  const decision = kinds.find(k => k.type === 'decision');
  const state = kinds.find(k => k.type === 'state');
  if (decision) {
    const n = await probe(decision);
    out[decision.path] = { type: decision.type, badgeCount: n };
    expect(n, `у страницы вида decision (${decision.path}) плашки нет`).toBe(0);
  }
  if (state) {
    const n = await probe(state);
    out[state.path] = { type: state.type, badgeCount: n };
  }
  fs.writeFileSync(path.join(ATT, 'ui-badge.json'), JSON.stringify(out, null, 1));
  expect(Object.keys(out).length, 'плашки зафиксированы в ui-badge.json').toBeGreaterThan(0);
});
