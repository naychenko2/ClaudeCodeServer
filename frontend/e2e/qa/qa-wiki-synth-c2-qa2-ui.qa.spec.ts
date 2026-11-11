// QA-2 · UI-проверки после доработки (задача ef37589d, эпик 66b5e075).
// 1. d7ea8ba0: на страницах знаний (decision/rule/term/state) нет плашки «шапка не разобрана».
// 2. a9d8d6eb: карточка операции в «Входящих» несёт утверждение и цитаты; в описании операции
//    утверждение один раз (превью «предполагаемого изменения» — диф страницы, не повтор).
// 3. новое: бейдж «ждёт решения во «Входящих»» в списке разбора чатов, причина с числом,
//    такой чат первый; после решения всех спорных операций бейдж исчезает.
//    Пометка «важный» перекрывает зону (rowMetaOf, ADR-035 §12а) — для проверки бейджа
//    пометку снимаем; отсутствие бейджа у помеченного чата — находка, фиксируется отдельно.
// Порядок запуска — после analyze-qa2.py: тест про решение меняет состояние «Входящих».

import { test, expect, type Page, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const ATT = path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2-qa2');
type Seed = { ownerId: string; projectId: string; chats: { key: string; sessionId: string }[] };
const seed = JSON.parse(fs.readFileSync(path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2', 'seed.json'), 'utf8')) as Seed;
const SECTION = `projects/${seed.projectId}`;
const KNOWLEDGE = new Set(['decision', 'rule', 'term', 'state']);

let request: APIRequestContext;
let client: Api;
let token = '';

test.setTimeout(300_000);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });
});

test.afterAll(async () => {
  await request?.dispose();
});

const railButton = (page: Page, key: string) => page.locator(`[data-rail-item="${key}"] button`);
const panelCard = (page: Page, title: string) => page.locator(
  `xpath=//span[contains(@style,"font-weight: 600") and normalize-space()="${title}"]` +
  `/ancestor::div[contains(@style,"flex-direction: column")][1]`);

async function openPanel(page: Page, key: string, title: string) {
  const card = panelCard(page, title);
  if (await card.count() > 0) return;
  await railButton(page, key).click();
  await expect(card).toBeVisible({ timeout: 20000 });
}

async function openNotes(page: Page) {
  await page.context().addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto('/#/notes');
  await expect(page.locator('#root')).not.toBeEmpty();
}

// ── 1. плашка шапки на страницах знаний ────────────────────────────────────
test('d7ea8ba0: ни на одной странице знаний нет плашки «шапка не разобрана»', async ({ page }) => {
  const cat = await client.get<{ pages?: { pageKey: string }[] }>(
    `/api/wiki/section?section=${encodeURIComponent(SECTION)}`);
  const keys = (cat.pages ?? []).map(p => p.pageKey);
  expect(keys.length, 'в разделе есть страницы').toBeGreaterThan(0);
  const kinds: { path: string; type: string; title: string }[] = [];
  for (const k of keys) {
    const full = await client.get<{ content: string }>(
      `/api/wiki/page?section=${encodeURIComponent(SECTION)}&page=${encodeURIComponent(k)}`);
    const type = /^type:\s*(.+)$/m.exec(full.content)?.[1]?.trim() ?? '';
    if (!KNOWLEDGE.has(type)) continue; // служебные (source/chronicle/_*) в дереве не строками
    kinds.push({ path: k, type, title: /^#\s+(.+)$/m.exec(full.content)?.[1]?.trim() ?? k });
  }
  expect(kinds.length, 'есть страницы видов decision/rule/term/state').toBeGreaterThan(0);
  const seen: Record<string, number> = {};
  for (const k of kinds) seen[k.type] = (seen[k.type] ?? 0) + 1;
  fs.writeFileSync(path.join(ATT, 'page-kinds.json'), JSON.stringify(seen, null, 1), 'utf8');

  await openNotes(page);
  for (const k of kinds) {
    const row = page.locator('[data-testid="unified-row"]', { hasText: k.title.slice(0, 30) }).first();
    await expect(row, `строка «${k.title}» видна в дереве`).toBeVisible({ timeout: 30000 });
    await row.click();
    await expect(page.getByTestId('wiki-doc')).toBeVisible({ timeout: 30000 });
    const badge = await page.getByText('Шапка страницы не разобрана').count();
    expect(badge, `плашка на странице вида ${k.type} (${k.path})`).toBe(0);
  }
});

// ── 2. карточка операции: утверждение и цитаты, один раз в описании ────────
test('a9d8d6eb: карточка «Входящих» несёт утверждение и цитаты, утверждение один раз', async ({ page }) => {
  await openNotes(page);
  await openPanel(page, 'wikiInbox', 'Входящие');
  const rows = page.getByTestId('wiki-pkg-row');
  expect(await rows.count(), 'пакеты в «Входящих» видны').toBeGreaterThan(0);
  await rows.first().click();
  const opRows = page.getByTestId('wiki-op-row');
  const n = await opRows.count();
  expect(n, 'операции пакета видны').toBeGreaterThan(0);
  const out: unknown[] = [];
  for (let i = 0; i < n; i++) {
    const op = opRows.nth(i);
    const claim = op.getByTestId('wiki-op-claim');
    const quotes = op.getByTestId('wiki-op-quote');
    const claimCount = await claim.count();
    const quoteCount = await quotes.count();
    // Повтор утверждения мерим в описании операции (wiki-op-evidence): «предполагаемое
    // изменение» — диф будущей страницы, он содержит утверждение по смыслу, это не повтор.
    const evidence = op.getByTestId('wiki-op-evidence');
    const evidenceText = (await evidence.innerText()).replace(/\s+/g, ' ');
    const claimText = claimCount > 0
      ? (await claim.first().innerText()).replace(/^Утверждение:\s*/, '').trim() : '';
    const occurrences = claimText ? (evidenceText.split(claimText).length - 1) : 0;
    out.push({ i, claimCount, quoteCount, occurrences });
    expect(claimCount, `операция ${i}: утверждение не показано`).toBe(1);
    expect(quoteCount, `операция ${i}: цитат нет`).toBeGreaterThan(0);
    expect(occurrences, `операция ${i}: утверждение повторено в описании ${occurrences} раза`).toBe(1);
  }
  fs.writeFileSync(path.join(ATT, 'inbox-card.json'), JSON.stringify(out, null, 1), 'utf8');
});

// ── 3. бейдж awaiting_human: текст, причина, первая позиция, исчезание ─────
test('новое: бейдж «ждёт решения» — первый в списке; после решения исчезает', async ({ page }) => {
  // Бейдж зоны перекрывается пометкой (rowMetaOf): для проверки снимаем пометку с одного
  // awaiting-чата; отсутствие бейджа у помеченного «важный» — находка, её видим по API-зоне.
  const list = await client.get<{ items: { sessionId: string; zone: string; mark?: string | null }[] }>(
    '/api/wiki/chats');
  const awaiting = list.items.filter(i =>
    seed.chats.some(c => c.sessionId === i.sessionId) && i.zone === 'awaiting_human');
  expect(awaiting.length, 'есть чат в зоне awaiting_human').toBeGreaterThan(0);
  const target = awaiting[0];
  const unmark = await client.put<{ mark: string | null }>(`/api/wiki/chats/${target.sessionId}/mark`, { mark: null });
  expect(unmark.mark, 'пометка снята').toBeFalsy();

  await openNotes(page);
  await openPanel(page, 'wikiSettings', 'Настройки');
  const rows = page.getByTestId('wiki-triage-row');
  const count = await rows.count();
  expect(count, 'список разбора чатов виден').toBeGreaterThan(0);

  const seededTitles = seed.chats.map(c => `qa-c2-${c.key}`);
  const rowsInfo: { title: string; hasBadge: boolean; reason: string }[] = [];
  for (let i = 0; i < count; i++) {
    const t = (await rows.nth(i).innerText()).replace(/\s+/g, ' ');
    if (!seededTitles.some(k => t.includes(k))) continue;
    rowsInfo.push({
      title: t.slice(0, 120),
      hasBadge: t.includes('ждёт решения во «Входящих»'),
      reason: (/Почему: (\d+ операци[ияи] на решении)/.exec(t)?.[1] ?? ''),
    });
  }
  expect(rowsInfo.length, 'строки seeded-чатов найдены').toBeGreaterThanOrEqual(1);
  fs.writeFileSync(path.join(ATT, 'triage-ui.json'), JSON.stringify(rowsInfo, null, 1), 'utf8');
  expect(rowsInfo.some(r => r.hasBadge), 'бейдж awaiting_human есть (у снятого с пометки чата)').toBeTruthy();
  const firstOther = rowsInfo.findIndex(r => !r.hasBadge);
  const lastBadge = rowsInfo.findLastIndex(r => r.hasBadge);
  expect(lastBadge < firstOther || firstOther === -1, 'чаты с бейджем — первые в списке').toBeTruthy();
  for (const r of rowsInfo.filter(x => x.hasBadge)) {
    expect(r.reason, `строка «${r.title}»: причина с числом и склонением`).toMatch(/^\d+ операци[ияи] на решении$/);
  }

  // Решаем все спорные операции этих чатов — бейдж обязан исчезнуть.
  const inbox = await client.get<{ packages: {
    id: string; sources?: { ref?: string }[];
  }[] }>('/api/wiki/packages?inbox=true');
  const applyOutcomes: unknown[] = [];
  const chatIds = new Set<string>();
  for (const p of inbox.packages ?? []) {
    for (const s of p.sources ?? []) {
      const m = /^session:([0-9a-f-]+)/.exec(s.ref ?? ''); // ref несёт окно: session:{id}#m1-76
      if (m && list.items.find(i => i.sessionId === m[1] && i.zone === 'awaiting_human')) chatIds.add(m[1]);
    }
  }
  console.log('DEBUG inbox=', (inbox.packages ?? []).length, 'chatIds=', [...chatIds],
    'refs=', (inbox.packages ?? []).map(p => (p.sources ?? []).map(s => s.ref)),
    'zones=', list.items.map(i => [i.sessionId.slice(0, 8), i.zone]));
  for (const id of chatIds) {
    const pkgs = (inbox.packages ?? []).filter(p => (p.sources ?? []).some(s => (s.ref ?? '').startsWith(`session:${id}`)));
    for (const p of pkgs) {
      const r = await client.post<{ package?: { status: string }; apply?: { applied: boolean; reason?: string | null } }>(
        `/api/wiki/packages/${p.id}/accept-all`);
      // Решение человека принято (200) независимо от исхода применения; сам исход — артефакт.
      expect(r.package?.status, `accept-all пакета ${p.id} не принят`).toBeTruthy();
      applyOutcomes.push({ id: p.id, chat: id, status: r.package?.status, apply: r.apply });
    }
  }
  fs.writeFileSync(path.join(ATT, 'resolve-outcomes.json'), JSON.stringify(applyOutcomes, null, 1), 'utf8');

  // Перечитать панель: закрыть и открыть (открытая панель свой список не перезагружает).
  await railButton(page, 'wikiSettings').click();
  await expect(panelCard(page, 'Настройки')).toHaveCount(0);
  await railButton(page, 'wikiSettings').click();
  await expect(panelCard(page, 'Настройки')).toBeVisible({ timeout: 20000 });
  const after: string[] = [];
  const afterCount = await page.getByTestId('wiki-triage-row').count();
  for (let i = 0; i < afterCount; i++) {
    const t = (await page.getByTestId('wiki-triage-row').nth(i).innerText()).replace(/\s+/g, ' ');
    if (seededTitles.some(k => t.includes(k)) && t.includes('ждёт решения во «Входящих»')) after.push(t.slice(0, 120));
  }
  expect(after, 'после решения всех спорных операций бейдж остался').toEqual([]);
});
