// QA С2 · сценарий 5: ночь без облака — ЗАПУСК (задача b98fee32, эпик 66b5e075).
// Маршрут «облако» для wiki-nightly и посев чатов — в qa-wiki-synth-c2-probe.qa.spec.ts
// (между ними — рестарт стенда: история посевных чатов видна серверу только после рестарта).

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const ATT = path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2');
type Seed = { ownerId: string; projectId: string; dataDir: string; chats: { key: string; sessionId: string }[] };
const seed = JSON.parse(fs.readFileSync(path.join(ATT, 'seed.json'), 'utf8')) as Seed;
const WIKI = path.join(seed.dataDir, 'wiki', seed.ownerId);

let request: APIRequestContext;
let client: Api;

test.setTimeout(300_000);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  client = api(request, token);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });
});

test.afterAll(async () => {
  await request?.dispose();
});

test('ночь с облачной моделью для wiki-nightly: разбор отказан, облако не вызвано', async () => {
  const run = await client.post<{ started: boolean }>('/api/wiki/settings/nightly/run');
  expect(run.started, 'ночь запущена').toBe(true);

  const last = path.join(WIKI, 'keeper', 'nightly-last.json');
  const before = fs.existsSync(last) ? fs.readFileSync(last, 'utf8') : '';
  let text = before;
  const deadline = Date.now() + 4 * 60 * 1000;
  while (Date.now() < deadline) {
    await new Promise(r => setTimeout(r, 3000));
    if (!fs.existsSync(last)) continue;
    text = fs.readFileSync(last, 'utf8');
    if (text !== before) break;
  }
  expect(text, 'итог прохода записан').not.toBe(before);
  const result = JSON.parse(text) as { rejected?: number; firstRejectReason?: string; manual?: boolean; analyzed?: number };
  fs.writeFileSync(path.join(ATT, 'nocloud-result.json'), JSON.stringify(result, null, 1), 'utf8');
  expect(result.manual, 'проход ручной').toBe(true);
  expect(result.rejected ?? 0, 'разбор отказан').toBeGreaterThan(0);
  expect(result.firstRejectReason ?? '', 'причина отказа: только локальная модель')
    .toContain('ночной разбор только локальной моделью');
});
