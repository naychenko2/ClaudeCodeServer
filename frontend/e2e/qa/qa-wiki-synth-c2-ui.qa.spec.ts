// QA С2 · интерфейс страницы знания: плашка «Шапка страницы не разобрана» по виду страницы
// (задача b98fee32, эпик 66b5e075). Заголовок плашки рисуется ТЕКСТОМ (Notice), искать по
// getByTitle — мимо.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const ATT = path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2');
type Seed = { ownerId: string; projectId: string; dataDir: string };
const seed = JSON.parse(fs.readFileSync(path.join(ATT, 'seed.json'), 'utf8')) as Seed;
const SECTION = `projects/${seed.projectId}`;

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

test('плашка шапки: вида decision нет, вида state — есть (фронт не знает новых видов)', async ({ page, context }) => {
  const cat = await client.get<{ pages?: { pageKey: string }[] }>(
    `/api/wiki/section?section=${encodeURIComponent(SECTION)}`);
  const kinds: { path: string; type: string; title: string }[] = [];
  for (const k of (cat.pages ?? []).map(p => p.pageKey)) {
    const full = await client.get<{ content: string }>(
      `/api/wiki/page?section=${encodeURIComponent(SECTION)}&page=${encodeURIComponent(k)}`);
    const type = /^type:\s*(.+)$/m.exec(full.content)?.[1]?.trim() ?? '';
    kinds.push({ path: k, type, title: /^#\s+(.+)$/m.exec(full.content)?.[1]?.trim() ?? k });
  }
  const probe = async (t: { path: string; type: string; title: string }) => {
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
    await page.goto('/#/notes');
    await expect(page.locator('#root')).not.toBeEmpty();
    const row = page.locator('[data-testid="unified-row"]', { hasText: t.title.slice(0, 30) }).first();
    await expect(row, `строка «${t.title}» видна в дереве вики`).toBeVisible({ timeout: 30000 });
    await row.click();
    await expect(page.getByTestId('wiki-doc')).toBeVisible({ timeout: 30000 });
    const badge = page.getByText('Шапка страницы не разобрана');
    return { badge: await badge.count(), shown: await badge.first().isVisible().catch(() => false) };
  };

  const out: Record<string, unknown> = {};
  const decision = kinds.find(k => k.type === 'decision');
  const state = kinds.find(k => k.type === 'state');
  const rule = kinds.find(k => k.type === 'rule');
  if (decision) {
    const r = await probe(decision);
    out[decision.path] = { type: decision.type, ...r };
    expect(r.badge, `у страницы вида decision (${decision.path}) плашки нет`).toBe(0);
  }
  if (state) {
    const r = await probe(state);
    out[state.path] = { type: state.type, ...r };
  }
  if (rule) {
    const r = await probe(rule);
    out[rule.path] = { type: rule.type, ...r };
  }
  fs.writeFileSync(path.join(ATT, 'ui-badge.json'), JSON.stringify(out, null, 1));
  expect(decision && state, 'есть страницы видов decision и state').toBeTruthy();
});
