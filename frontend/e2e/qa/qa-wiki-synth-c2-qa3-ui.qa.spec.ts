// QA-3 · UI-проверки после ночи 1 (задача 597ed55a, эпик 66b5e075).
// 1. 82567033: чат с пакетом во «Входящих» И пометкой «важный» — строка списка показывает
//    бейдж действия «ждёт решения во „Входящих“» (пометка — вторым бейджем), а не одну пометку.
// 2. 2d9df07a (решение человека): «принять всё» по пакету — применение не отвергается хроникой.
//    После решения бейдж исчезает.
// 3. Подготовка целевого сценария 03a50f53: частичное решение по другому пакету (одна операция
//    отклонена) — пакет остаётся открытым с человеческим решением; его проверяет ночь 2.
// Порядок запуска — после qa3-verify: тесты меняют состояние «Входящих».

import { test, expect, type Page, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const ATT = path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2-qa3');
type Seed = { projectId: string; chats: { key: string; sessionId: string }[] };
const seed = JSON.parse(fs.readFileSync(path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2', 'seed.json'), 'utf8')) as Seed;

const BADGE = 'ждёт решения во «Входящих»';
// s3 — чат для «принять всё» и проверки исчезания бейджа; s4 — для частичного решения.
const ACCEPT_KEY = 's3';
const PARTIAL_KEY = 's4';

let request: APIRequestContext;
let client: Api;
let token = '';

test.setTimeout(300_000);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });
  fs.mkdirSync(ATT, { recursive: true });
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

async function triageRows(page: Page) {
  const out: { title: string; hasBadge: boolean; hasMark: boolean }[] = [];
  const rows = page.getByTestId('wiki-triage-row');
  const count = await rows.count();
  for (let i = 0; i < count; i++) {
    const t = (await rows.nth(i).innerText()).replace(/\s+/g, ' ');
    if (!seed.chats.some(c => t.includes(`qa-c2-${c.key}`))) continue;
    out.push({ title: t.slice(0, 140), hasBadge: t.includes(BADGE), hasMark: t.includes('важный') });
  }
  return out;
}

test('82567033: бейдж действия поверх пометки «важный»', async ({ page }) => {
  const s3 = seed.chats.find(c => c.key === ACCEPT_KEY)!;
  // Чат seeded-корпуса помечен «важный» (посев ставит пометку всем) — именно этот случай
  // в QA-2 терял бейдж действия.
  const list = await client.get<{ items: { sessionId: string; zone: string; mark?: string | null }[] }>(
    '/api/wiki/chats');
  const s3row = list.items.find(i => i.sessionId === s3.sessionId)!;
  expect(s3row.mark, 'чат помечен «важный»').toBe('important');
  expect(s3row.zone, 'чат в зоне awaiting_human').toBe('awaiting_human');

  await openNotes(page);
  await openPanel(page, 'wikiSettings', 'Настройки');
  const rows = await triageRows(page);
  fs.writeFileSync(path.join(ATT, 'triage-ui-before.json'), JSON.stringify(rows, null, 1), 'utf8');
  const target = rows.find(r => r.title.includes(`qa-c2-${ACCEPT_KEY}`));
  expect(target, 'строка s3 найдена').toBeTruthy();
  expect(target!.hasBadge, `бейдж действия для помеченного чата не показан: ${target!.title}`).toBeTruthy();
  expect(target!.hasMark, 'пометка «важный» осталась вторым бейджем').toBeTruthy();
  // Порядок бэка: awaiting-чаты раньше прочих своих чатов (регрессия).
  const firstOther = rows.findIndex(r => !r.hasBadge);
  const lastBadge = rows.findLastIndex(r => r.hasBadge);
  expect(lastBadge < firstOther || firstOther === -1, 'чаты с бейджем — первые в списке').toBeTruthy();
});

test('2d9df07a: «принять всё» применяется; бейдж после решения исчезает', async ({ page }) => {
  const s3 = seed.chats.find(c => c.key === ACCEPT_KEY)!;
  const inbox = await client.get<{ packages: {
    id: string; sources?: { ref?: string }[];
    operations: { op: string; decision?: string }[];
  }[] }>('/api/wiki/packages?inbox=true');
  const pkgs = (inbox.packages ?? []).filter(p =>
    (p.sources ?? []).some(s => (s.ref ?? '').startsWith(`session:${s3.sessionId}`)) &&
    (p.operations ?? []).some(o => o.decision === 'inbox'));
  expect(pkgs.length, 'у s3 есть пакет со спорными операциями').toBeGreaterThan(0);

  const outcomes: unknown[] = [];
  for (const p of pkgs) {
    const r = await client.post<{ package?: { status: string }; apply?: { applied: boolean; reason?: string | null } }>(
      `/api/wiki/packages/${p.id}/accept-all`);
    expect(r.package?.status, `accept-all пакета ${p.id} не принят`).toBeTruthy();
    // 2d9df07a: раньше решение человека кончалось apply_failed с причиной про хронику.
    if (r.apply && !r.apply.applied) {
      expect(r.apply.reason ?? '', `применение решения отвергнуто: ${r.apply.reason}`).not.toMatch(/[Рр]оника/);
    }
    outcomes.push({ id: p.id, status: r.package?.status, apply: r.apply });
  }
  fs.writeFileSync(path.join(ATT, 'resolve-outcomes.json'), JSON.stringify(outcomes, null, 1), 'utf8');
  expect(outcomes.every((o: any) => o.apply?.applied), 'accept-all не применился').toBeTruthy();

  // Панель открывается заново (новая страница теста) — список читается свежим.
  await openNotes(page);
  await openPanel(page, 'wikiSettings', 'Настройки');
  const after = (await triageRows(page)).filter(r => r.title.includes(`qa-c2-${ACCEPT_KEY}`) && r.hasBadge);
  fs.writeFileSync(path.join(ATT, 'triage-ui-after.json'), JSON.stringify(after, null, 1), 'utf8');
  expect(after, 'после решения всех спорных бейдж остался').toEqual([]);
});

test('03a50f53 (подготовка): частичное решение пакета s4 — он остаётся открытым с решением', async () => {
  const s4 = seed.chats.find(c => c.key === PARTIAL_KEY)!;
  const inbox = await client.get<{ packages: {
    id: string; sources?: { ref?: string }[];
    operations: { op: string; decision?: string }[];
  }[] }>('/api/wiki/packages?inbox=true');
  const pkg = (inbox.packages ?? []).find(p =>
    (p.sources ?? []).some(s => (s.ref ?? '').startsWith(`session:${s4.sessionId}`)) &&
    (p.operations ?? []).some(o => o.decision === 'inbox'));
  expect(pkg, 'у s4 есть открытый пакет').toBeTruthy();
  const idx = pkg!.operations.findIndex(o => o.decision === 'inbox');
  const r = await client.post<{ package?: { status: string } }>(
    `/api/wiki/packages/${pkg!.id}/decide`, { index: idx, accept: false, reason: 'QA-3: число в утверждении не подтверждено цитатой' });
  expect(r.package?.status, 'решение по операции отклонено').toBeTruthy();
  fs.writeFileSync(path.join(ATT, 'partial-decision.json'), JSON.stringify(
    { pkgId: pkg!.id, window: pkg!.sources?.[0]?.ref, status: r.package?.status }, null, 1), 'utf8');
  // Пакет с человеческим решением остаётся открытым (есть нерешённые спорные) — его гасит
  // или не гасит повторный разбор, это меряет спек после ночи 2.
  expect(['proposed', 'partially_accepted'], `статус после частичного решения: ${r.package?.status}`)
    .toContain(r.package?.status);
});
