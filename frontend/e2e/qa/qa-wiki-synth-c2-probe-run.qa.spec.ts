// QA С2 · проба размера окна: ночь локальной моделью на уменьшенном окне (задача b98fee32).
// Перед прогоном: маршрут wiki-nightly возвращён в локальный, а все прочие чаты помечены
// «не разбирать» (skip побеждает балл) — иначе ночь снова взяла бы пять отказанных больших
// чатов (их курсор на нуле, они повторяются каждую ночь — это отдельная находка).

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const ATT = path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2');
type Seed = { ownerId: string; projectId: string; dataDir: string; chats: { key: string; sessionId: string }[] };
const seed = JSON.parse(fs.readFileSync(path.join(ATT, 'seed.json'), 'utf8')) as Seed;
const probe = JSON.parse(fs.readFileSync(path.join(ATT, 'probe-seed.json'), 'utf8')) as { seeded: Record<string, string> };
const WIKI = path.join(seed.dataDir, 'wiki', seed.ownerId);
const SMALL = Object.entries(probe.seeded).find(([n]) => n.includes('small'))?.[1] ?? '';

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

test('маршрут wiki-nightly возвращён в локальный, прочие чаты помечены «не разбирать»', async () => {
  await client.del('/api/admin/local-actions/wiki-nightly');
  for (const c of seed.chats) await client.put(`/api/wiki/chats/${c.sessionId}/mark`, { mark: 'skip' });
  for (const [name, id] of Object.entries(probe.seeded)) {
    if (id !== SMALL) await client.put(`/api/wiki/chats/${id}/mark`, { mark: 'skip' });
  }
  expect(SMALL, 'чат уменьшенного окна найден').not.toBe('');
});

test('ночь локальной моделью на уменьшенном окне', async () => {
  const run = await client.post<{ started: boolean }>('/api/wiki/settings/nightly/run');
  expect(run.started, 'ночь запущена').toBe(true);

  const last = path.join(WIKI, 'keeper', 'nightly-last.json');
  const before = fs.existsSync(last) ? fs.readFileSync(last, 'utf8') : '';
  let text = before;
  const deadline = Date.now() + 6 * 60 * 1000;
  while (Date.now() < deadline) {
    await new Promise(r => setTimeout(r, 5000));
    if (!fs.existsSync(last)) continue;
    text = fs.readFileSync(last, 'utf8');
    if (text !== before) break;
  }
  expect(text, 'итог прохода записан').not.toBe(before);
  const result = JSON.parse(text) as { selected?: number; analyzed?: number; applied?: number; toInbox?: number; rejected?: number; firstRejectReason?: string | null };
  fs.writeFileSync(path.join(ATT, 'probe-result.json'), JSON.stringify(result, null, 1), 'utf8');
  const st = await client.get<{ last?: { outcome?: string; reason?: string | null } }>(
    `/api/wiki/chats/${SMALL}/analyze-status`);
  fs.writeFileSync(path.join(ATT, 'probe-analyze-status.json'), JSON.stringify(st, null, 1), 'utf8');
  expect(result.analyzed ?? 0, 'чат разобран').toBeGreaterThan(0);
});
