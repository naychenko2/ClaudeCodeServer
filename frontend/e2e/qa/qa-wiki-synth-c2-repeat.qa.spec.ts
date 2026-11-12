// QA С2 · повторяемость ночи на одном и том же окне (задача b98fee32, эпик 66b5e075).
// Чат уменьшенного окна s3 (12 реплик) разбирается ночью несколько раз подряд: курсор при
// отказе не двигается, поэтому окно берётся снова. Считаем, сколько прогонов дают пакет,
// а сколько — отказ кода.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const ATT = path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2');
type Seed = { ownerId: string; projectId: string; dataDir: string };
const seed = JSON.parse(fs.readFileSync(path.join(ATT, 'seed.json'), 'utf8')) as Seed;
const inboxSeed = JSON.parse(fs.readFileSync(path.join(ATT, 'inbox-seed.json'), 'utf8')) as { chatId: string };
const WIKI = path.join(seed.dataDir, 'wiki', seed.ownerId);
const RUNS = 2;

let request: APIRequestContext;
let client: Api;

test.setTimeout(420_000);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  client = api(request, token);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });
});

test.afterAll(async () => {
  await request?.dispose();
});

test('повторные проходы ночи на одном окне', async () => {
  const outcomes: { run: number; outcome?: string; reason?: string | null; result?: unknown }[] = [];
  for (let i = 1; i <= RUNS; i++) {
    const run = await client.post<{ started: boolean }>('/api/wiki/settings/nightly/run');
    expect(run.started, `проход ${i} запущен`).toBe(true);
    const last = path.join(WIKI, 'keeper', 'nightly-last.json');
    const before = fs.existsSync(last) ? fs.readFileSync(last, 'utf8') : '';
    let text = before;
    const deadline = Date.now() + 4 * 60 * 1000;
    while (Date.now() < deadline) {
      await new Promise(r => setTimeout(r, 4000));
      if (!fs.existsSync(last)) continue;
      text = fs.readFileSync(last, 'utf8');
      if (text !== before) break;
    }
    expect(text, `итог прохода ${i} записан`).not.toBe(before);
    const st = await client.get<{ last?: { outcome?: string; reason?: string | null } }>(
      `/api/wiki/chats/${inboxSeed.chatId}/analyze-status`);
    outcomes.push({ run: i, outcome: st.last?.outcome, reason: st.last?.reason, result: JSON.parse(text) });
  }
  fs.writeFileSync(path.join(ATT, 'repeat-outcomes.json'), JSON.stringify(outcomes, null, 1), 'utf8');
  expect(outcomes.length).toBe(RUNS);
});
