// QA С2 · уборка посева (задача b98fee32, эпик 66b5e075). Убирает все проекты с префиксом
// qa-c2-, вики владельца и каталог данных стенда; флаг llm-wiki выключает.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const ATT = path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2');

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

test('посев убран: проекты, вики и каталог данных стенда', async () => {
  await client.put('/api/feature-flags/llm-wiki', { enabled: false });

  const projects = await client.get<{ id: string; name: string }[]>('/api/projects');
  const mine = (projects ?? []).filter(p => p.name.startsWith('qa-c2-'));
  for (const p of mine) await client.del(`/api/projects/${p.id}`);
  expect(mine.length, 'посевные проекты были и удалены').toBeGreaterThan(0);

  const seed = JSON.parse(fs.readFileSync(path.join(ATT, 'seed.json'), 'utf8')) as { ownerId: string; dataDir: string };
  const wiki = path.join(seed.dataDir, 'wiki');
  if (fs.existsSync(wiki)) fs.rmSync(wiki, { recursive: true, force: true });
  expect(fs.existsSync(wiki), 'вики владельца удалена').toBeFalsy();
});
