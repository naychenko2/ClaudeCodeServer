// QA С2 · посев для пробы «Входящих» (задача b98fee32, эпик 66b5e075).
// Новый чат с уменьшенным окном s3 (12 реплик, ~15 КБ) + «не разбирать» всем прочим чатам,
// чтобы ночь взяла только его. История лежит файлом — видна серверу после рестарта стенда.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const ATT = path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2');
type Seed = { ownerId: string; projectId: string; dataDir: string; chats: { key: string; sessionId: string }[] };
const seed = JSON.parse(fs.readFileSync(path.join(ATT, 'seed.json'), 'utf8')) as Seed;
const stamp = Date.now();

let request: APIRequestContext;
let client: Api;
let chatId = '';

test.setTimeout(300_000);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  client = api(request, token);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });

  const s = await client.post<{ id: string }>(`/api/projects/${seed.projectId}/sessions`,
    { name: `qa-c2-inbox-s3-${stamp}` });
  chatId = s.id;
  const dir = path.join(seed.dataDir, 'sessions', chatId);
  fs.mkdirSync(dir, { recursive: true });
  fs.copyFileSync(path.join(ATT, 'history-s3-small.json'), path.join(dir, 'history.json'));
  await client.put(`/api/wiki/chats/${chatId}/mark`, { mark: 'important' });
  // Прочие чаты — «не разбирать»: skip побеждает балл, ночь идёт по чату.
  for (const c of seed.chats) await client.put(`/api/wiki/chats/${c.sessionId}/mark`, { mark: 'skip' });
  fs.writeFileSync(path.join(ATT, 'inbox-seed.json'), JSON.stringify({ chatId, stamp }, null, 1), 'utf8');
});

test.afterAll(async () => {
  await request?.dispose();
});

test('чат уменьшенного окна заведён и помечен важным', async () => {
  expect(chatId).not.toBe('');
  const st = await client.get<{ running: boolean }>(`/api/wiki/chats/${chatId}/analyze-status`);
  expect(st.running).toBe(false);
});
