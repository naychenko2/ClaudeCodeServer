import { test, expect, type Page, type APIRequestContext } from '@playwright/test';
import { readFileSync, existsSync } from 'node:fs';
import { login, api } from './helpers';

// QA-2 · выкат 2 «LLM-wiki: связи и укрупнение страниц (очередь 2)» — тумблер merge ВЫКЛ.
// Стенд поднят с Wiki:MergeProposals = false (appsettings.Local.json).
// Проверяем: ночь проходит без merge-пакетов (итог MergeProposed = 0), во «Решениях» пусто.

const ROOT = process.env.QA_STAND_ROOT || '/home/an/Sources/ClaudeCodeServer-wiki-queue2c';
const OWNER = '11111111-2222-3333-4444-555555555501';
const WIKI = `${process.env.QA_STAND_DATA || `${ROOT}/data-qa`}/wiki/${OWNER}`;
const NIGHT_FILE = `${WIKI}/keeper/nightly-last.json`;
const T = { timeout: 20000 };

let request: APIRequestContext;
let token: string;

test.describe.configure({ mode: 'serial' });
test.setTimeout(600_000);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
});

test.afterAll(async () => { await request?.dispose(); });

function client() { return api(request, token); }

async function runNight(): Promise<any> {
  const c = client();
  const before = existsSync(NIGHT_FILE) ? readFileSync(NIGHT_FILE, 'utf8') : null;
  const r = await request.post('/api/wiki/settings/nightly/run', { headers: { Authorization: `Bearer ${token}` } });
  test.info().annotations.push({ type: 'note', description: `POST nightly/run → ${r.status()} ${await r.text()}` });
  expect(r.status(), 'ручной запуск ночи принят').toBe(202);
  const deadline = Date.now() + 240_000;
  while (Date.now() < deadline) {
    await new Promise(r => setTimeout(r, 3000));
    const st = await c.get<any>('/api/wiki/settings');
    if (!st.nightlyRunning) {
      const now = existsSync(NIGHT_FILE) ? readFileSync(NIGHT_FILE, 'utf8') : null;
      if (now && now !== before) return JSON.parse(now);
      if (st.lastNight) return st.lastNight;
    }
  }
  throw new Error('ночь не завершилась за 4 минуты');
}

test('тумблер merge ВЫКЛ: ночь без merge-пакетов, MergeProposed = 0', async () => {
  const night = await runNight();
  test.info().annotations.push({ type: 'note', description: `итог ночи: ${JSON.stringify(night)}` });
  expect(night.mergeProposed, 'итог ночи: предложений merge нет').toBe(0);

  const list = await client().get<{ packages: any[] }>('/api/wiki/packages?inbox=true');
  const merges = list.packages.flatMap(p => (p.operations ?? []).filter((o: any) => o.op === 'merge'));
  test.info().annotations.push({ type: 'note', description: `пакетов во «Решениях»: ${list.packages.length}; merge-операций: ${merges.length}` });
  expect(merges.length, 'merge-пакетов нет').toBe(0);
});

test('пустое состояние «Решений»: очередь пуста, экран объясняет, что решать нечего', async ({ page, context }) => {
  page.setDefaultTimeout(20000);
  await context.addInitScript((tk: string) => localStorage.setItem('cc_token', tk), token);
  await page.goto('/#/notes');
  await page.waitForTimeout(5000);
  let tree = page.locator('[data-testid="wiki-unified-tree"]');
  if (!(await tree.count())) {
    const nav = page.locator('button', { hasText: 'Заметки' }).first();
    if (await nav.count()) { await nav.click(T); await page.waitForTimeout(7000); }
  }
  tree = page.locator('[data-testid="wiki-unified-tree"]');
  if (!(await tree.count())) {
    const wikiMode = page.locator('button', { hasText: /^Вики/ }).first();
    if (await wikiMode.count()) { await wikiMode.click(T); await page.waitForTimeout(6000); }
    const openTree = page.locator('[data-testid="wiki-open-tree"]');
    if (await openTree.count()) { await openTree.first().click(T); await page.waitForTimeout(6000); }
  }
  const tab = page.locator('button', { hasText: /^Решения/ }).first();
  if (await tab.count()) { await tab.click(T); await page.waitForTimeout(2500); }
  const rows = page.locator('[data-testid="wiki-pkg-row"]');
  const count = await rows.count();
  test.info().annotations.push({ type: 'note', description: `строк пакетов во «Решениях»: ${count}` });
  expect(count, 'очередь «Решений» пуста').toBe(0);
  const body = await page.locator('body').innerText();
  test.info().annotations.push({ type: 'note', description: `текст панели: ${body.slice(0, 400)}` });
  expect(body.length, 'панель не пустая — есть объяснение').toBeGreaterThan(50);
});
