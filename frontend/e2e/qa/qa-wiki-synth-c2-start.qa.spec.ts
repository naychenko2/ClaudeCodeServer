// QA С2 · ночной синтез (задача b98fee32, эпик 66b5e075). Спек 2/3: после рестарта стенда
// (история чатов лежит файлом — сервер видит её только когда у чата нет живого аккумулятора)
// проверяем, что посев доехал, и запускаем ночь вручную.
//
// Уборка — в спеке 3 (после разбора результатов).

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const ATT = path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2');

type SeedChat = { unit: string; key: string; label: string; title: string; sessionId: string; user: number; assist: number };
type Seed = { ownerId: string; projectId: string; dataDir: string; chats: SeedChat[] };
const seed = JSON.parse(fs.readFileSync(path.join(ATT, 'seed.json'), 'utf8')) as Seed;

let request: APIRequestContext;
let client: Api;

test.setTimeout(300_000);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  client = api(request, token);
});

test.afterAll(async () => {
  await request?.dispose();
});

test('история посевных чатов видна серверу', async () => {
  for (const c of seed.chats) {
    const hist = await client.get<{ kind: string; text?: string }[]>(
      `/api/projects/${seed.projectId}/sessions/${c.sessionId}/history`);
    expect(Array.isArray(hist), `чат ${c.key}: ответ истории`).toBeTruthy();
    expect(hist.length, `чат ${c.key}: реплик в истории`).toBeGreaterThan(20);
    const humans = hist.filter(m => m.kind === 'user_message').length;
    expect(humans, `чат ${c.key}: реплик человека`).toBeGreaterThanOrEqual(3);
    expect(hist.some(m => (m.text ?? '').includes('USER:')), `чат ${c.key}: без служебных префиксов`).toBeFalsy();
  }
});

test('ручной запуск ночи принят', async () => {
  const run = await client.post<{ started: boolean }>('/api/wiki/settings/nightly/run');
  expect(run.started).toBe(true);
  // Сторож ночи живёт, пока идёт проход: файл итога появится в keeper/nightly-last.json.
  const s = await client.get<{ nightlyRunning: boolean }>('/api/wiki/settings');
  expect(s.nightlyRunning, 'проход ночи идёт').toBe(true);
});
