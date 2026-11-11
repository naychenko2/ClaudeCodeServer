// QA-2 · повтор ночного синтеза после доработки (задача ef37589d, эпик 66b5e075).
// Спек после первой ночи: собирает артефакты итога ночи, стора отбора и «Входящих»
// и проверяет то, что видно по API: поле Dropped/FirstDropReason в итоге (критерий 4),
// зона awaiting_human со склоняемой причиной и первой позицией (новое), карточки
// «Входящих» с утверждением и цитатами (a9d8d6eb на контракте).
// Таблица по чатам и доли — скриптом analyze-qa2.py по файлам, не здесь.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';
import fs from 'node:fs';
import path from 'node:path';

const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const ATT = path.join(ROOT, '.cc-attachments', 'qa-wiki-synth-c2-qa2');
type Seed = { ownerId: string; projectId: string; dataDir: string; chats: { key: string; sessionId: string }[] };
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

test('итог ночи прочитан; отброшенные операции видны числом и первой причиной', async () => {
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
  // Критерий 4: отброшенные поштучным приёмом операции — число и первая причина в итоге.
  if ((s.lastNight?.dropped ?? 0) > 0) {
    expect(s.lastNight?.firstDropReason, 'при первой отброшенной операции есть причина')
      .toBeTruthy();
  }
});

test('стор отбора: awaiting_human со склоняемой причиной — первой в списке', async () => {
  const list = await client.get<{ items: {
    sessionId: string; name: string | null; zone: string; reason: string | null;
    cursor: number; nothingNewStreak: number;
  }[] }>('/api/wiki/chats');
  expect(Array.isArray(list.items), 'ответ списка — items[]').toBeTruthy();
  fs.writeFileSync(path.join(ATT, 'triage-api.json'), JSON.stringify(list.items, null, 1), 'utf8');
  const byId = new Map(seed.chats.map(c => [c.sessionId, c.key]));
  const mine = list.items.filter(i => byId.has(i.sessionId));
  const awaiting = mine.filter(i => i.zone === 'awaiting_human');
  expect(awaiting.length, 'есть чат в зоне awaiting_human').toBeGreaterThan(0);
  for (const a of awaiting) {
    // Причина — точное число и склонение («1 операция», «2 операции», «5 операций»).
    expect(a.reason, `awaiting_human ${byId.get(a.sessionId)}: причина с числом и словом`)
      .toMatch(/^\d+ операци[ияи] на решении$/);
  }
  // Порядок бэка: awaiting_human — первая зона; все такие чаты раньше прочих своих чатов.
  const firstOther = mine.findIndex(i => i.zone !== 'awaiting_human');
  const lastAwaiting = mine.findLastIndex(i => i.zone === 'awaiting_human');
  expect(lastAwaiting < firstOther || firstOther === -1,
    'чаты awaiting_human идут раньше прочих в списке').toBeTruthy();
});

test('«Входящие»: карточка операции несёт утверждение и цитаты', async () => {
  const inbox = await client.get<{ packages: {
    id: string; section: string; createdAt: string;
    operations: { op: string; kind?: string; claim?: string | null; quotes?: string[] | null; decision?: string }[];
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
