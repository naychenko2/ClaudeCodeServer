// QA-3 · перепроверка четырёх дефектов QA-2 (задача 597ed55a, эпик 66b5e075).
// Спек после первой ночи: итог ночи и «Входящие» по API. Хроника, дубли и шум в причинах —
// скриптом scripts/qa/wiki-synth-c2-qa3-analyze.py по диску (он же видит тексты чатов).
// Здесь — то, что видно только живому серверу: итог ночи не оборван хроникой (2d9df07a),
// зона awaiting_human со склоняемой причиной (регрессия), карточки «Входящих» с утверждением
// и цитатами (регрессия a9d8d6eb).

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const ATT = path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2-qa3');
type Seed = { ownerId: string; projectId: string; chats: { key: string; sessionId: string }[] };
const seed = JSON.parse(fs.readFileSync(path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2', 'seed.json'), 'utf8')) as Seed;

let request: APIRequestContext;
let client: Api;

test.setTimeout(120_000);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  client = api(request, token);
  fs.mkdirSync(ATT, { recursive: true });
});

test.afterAll(async () => {
  await request?.dispose();
});

test('итог ночи прочитан; ни один исход не оборван хроникой (2d9df07a)', async () => {
  const s = await client.get<{
    nightlyRunning: boolean;
    lastNight?: {
      date: string; selected: number; analyzed: number; applied: number; toInbox: number;
      inputTokens: number; rejected: number; firstRejectReason?: string | null;
      dropped: number; firstDropReason?: string | null; manual: boolean;
    };
  }>('/api/wiki/settings');
  expect(s.nightlyRunning, 'проход ночи закончился').toBeFalsy();
  expect(s.lastNight, 'итог ночи есть').toBeTruthy();
  fs.writeFileSync(path.join(ATT, 'night1.json'), JSON.stringify(s.lastNight, null, 1), 'utf8');
  // 2d9df07a: раньше итог ночи кончался строкой «…Хроники/…md: цитата N символов…» —
  // теперь ни одна причина отказа и отброшенной операции не может ссылаться на хронику.
  for (const r of [s.lastNight?.firstRejectReason, s.lastNight?.firstDropReason]) {
    if (r) expect(r, `причина ночи ссылается на хронику: ${r}`).not.toMatch(/[Рр]оника/);
  }
});

test('стор отбора: awaiting_human со склоняемой причиной (регрессия)', async () => {
  const list = await client.get<{ items: {
    sessionId: string; zone: string; reason: string | null; cursor: number; mark?: string | null;
  }[] }>('/api/wiki/chats');
  fs.writeFileSync(path.join(ATT, 'triage-api.json'), JSON.stringify(list.items, null, 1), 'utf8');
  const mine = list.items.filter(i => seed.chats.some(c => c.sessionId === i.sessionId));
  const awaiting = mine.filter(i => i.zone === 'awaiting_human');
  expect(awaiting.length, 'есть чат в зоне awaiting_human').toBeGreaterThan(0);
  for (const a of awaiting) {
    expect(a.reason, `awaiting_human ${a.sessionId.slice(0, 8)}`).toMatch(/^\d+ операци[ияи] на решении$/);
  }
});

test('«Входящие»: карточка операции несёт утверждение и цитаты (регрессия a9d8d6eb)', async () => {
  const inbox = await client.get<{ packages: {
    id: string; sources?: { ref?: string }[];
    operations: { op: string; claim?: string | null; quotes?: string[] | null; decision?: string }[];
  }[] }>('/api/wiki/packages?inbox=true');
  const pkgs = inbox.packages ?? [];
  expect(Array.isArray(inbox.packages), 'ответ — packages[]').toBeTruthy();
  fs.writeFileSync(path.join(ATT, 'inbox.json'), JSON.stringify(pkgs, null, 1), 'utf8');
  expect(pkgs.length, 'есть пакеты во «Входящих»').toBeGreaterThan(0);
  for (const p of pkgs) {
    for (const op of p.operations ?? []) {
      if (op.decision !== 'inbox') continue;
      expect(op.claim, `пакет ${p.id}: операция без утверждения`).toBeTruthy();
      expect((op.quotes ?? []).length, `пакет ${p.id}: операция без цитат`).toBeGreaterThan(0);
    }
  }
});
