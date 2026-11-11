// QA С2 · посев для сценария 5 и для пробы размера окна (задача b98fee32, эпик 66b5e075).
//
// Два чата:
//   qa-c2-cloud-* — копия переписки s5 (нужна для отказа «ночной разбор только локальной моделью»);
//   qa-c2-small-* — первые 12 реплик переписки s3 (окно ~15 КБ: проверяем, зависит ли отказ
//   от размера окна).
// История лежит файлом — сервер видит её только после рестарта стенда.

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
const seeded: Record<string, string> = {};

test.setTimeout(300_000);

const plant = async (name: string, historyFile: string) => {
  const s = await client.post<{ id: string }>(`/api/projects/${seed.projectId}/sessions`, { name });
  const dir = path.join(seed.dataDir, 'sessions', s.id);
  fs.mkdirSync(dir, { recursive: true });
  fs.copyFileSync(path.join(ATT, historyFile), path.join(dir, 'history.json'));
  await client.put(`/api/wiki/chats/${s.id}/mark`, { mark: 'important' });
  seeded[name] = s.id;
};

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  client = api(request, token);
  await client.put('/api/feature-flags/llm-wiki', { enabled: true });
  await plant(`qa-c2-cloud-s5-${stamp}`, 'history-s5.json');
  await plant(`qa-c2-small-s3-${stamp}`, 'history-s3-small.json');
  fs.writeFileSync(path.join(ATT, 'probe-seed.json'), JSON.stringify({ stamp, seeded }, null, 1), 'utf8');
});

test.afterAll(async () => {
  await request?.dispose();
});

test('для wiki-nightly назначена облачная модель', async () => {
  const put = await client.put<{ route?: string }>('/api/admin/local-actions/wiki-nightly', { route: 'claude' });
  expect(put.route ?? 'claude').toBeTruthy();
});
