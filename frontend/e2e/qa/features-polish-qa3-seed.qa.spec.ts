import { test, expect, type APIRequestContext } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { api, login } from './helpers';

// Засев для QA-3 «шапка чата с фичей и закреплёнными материалами» (7a3754fd).
// Один прогон = один мир с префиксом qa-fp3-: проект, открытая и закрытая фичи со шагами,
// четыре чата (A — открытая фича + материалы, A2 — открытая фича без материалов,
// B — вне фичи, C — закрытая фича + материалы), материалы контекста для A и C.
// TaskId чатам ставит внешний шаг (правка data/sessions.json + рестарт стенда):
// executor-ось членства чата в фиче — только Session.TaskId, REST его не задаёт.
// Итог — .cc-attachments/qa-fp3/seed.json для мейн-спека.

const PREFIX = 'qa-fp3-';
const OUT = '/home/an/Sources/ClaudeCodeServer-features-polish/.cc-attachments/qa-fp3/';
const STAMP = '1112b'; // метка среза: одна семья данных на прогон (перезасев — руками, с новым STAMP)

test.describe.configure({ mode: 'serial' });

let request: APIRequestContext;
let token: string;
let client: ReturnType<typeof api>;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});

test.afterAll(async () => { await request?.dispose(); });

test('засев мира qa-fp3', async () => {
  const rootPath = `/tmp/${PREFIX}${STAMP}`;
  const project = await client.post<{ id: string; name: string }>('/api/projects', {
    name: `${PREFIX}p-${STAMP}`, rootPath, createDirectory: true,
  });

  // файл-материал контекста (реальный, чтобы запись не была missing)
  mkdirSync(rootPath, { recursive: true });
  writeFileSync(`${rootPath}/qa-fp3-doc.md`, '# материал контекста для QA-3\n');

  const task = (title: string, extra: Record<string, unknown> = {}) =>
    client.post<{ id: string; title: string }>(`/api/projects/${project.id}/tasks`, { title, ...extra });

  const fOpen = await task(`${PREFIX}фича-открытая-${STAMP}`, { role: 'feature' });
  const s1 = await task(`${PREFIX}шаг-1-${STAMP}`, { role: 'step', featureId: fOpen.id, stepRef: 'S1' });
  const s1b = await task(`${PREFIX}шаг-1b-${STAMP}`, { role: 'step', featureId: fOpen.id, stepRef: 'S1b' });
  const s1c = await task(`${PREFIX}шаг-1c-${STAMP}`, { role: 'step', featureId: fOpen.id, stepRef: 'S1c' });
  const fClosed = await task(`${PREFIX}фича-закрытая-${STAMP}`, { role: 'feature' });
  const s2 = await task(`${PREFIX}шаг-2-${STAMP}`, { role: 'step', featureId: fClosed.id, stepRef: 'S2' });
  // закрыть закрытую: эпик Done и не merged → state=closed (TerminalState)
  await client.put(`/api/tasks/${fClosed.id}`, { status: 'done' });

  const session = (name: string) =>
    client.post<{ id: string }>(`/api/projects/${project.id}/sessions`, { name });
  const chatA = await session(`${PREFIX}chat-a-${STAMP}`);   // открытая фича + материалы
  const chatA2 = await session(`${PREFIX}chat-a2-${STAMP}`); // открытая фича без материалов
  const chatB = await session(`${PREFIX}chat-b-${STAMP}`);   // вне фичи
  const chatC = await session(`${PREFIX}chat-c-${STAMP}`);   // закрытая фича + материалы

  // материалы контекста: файл + ссылка + задача (для A), файл + ссылка (для C)
  const ctxA = await client.put<unknown[]>(`/api/projects/${project.id}/sessions/${chatA.id}/context`, [
    { type: 'file', id: 'qa-fp3-doc.md', title: 'qa-fp3-doc.md' },
    { type: 'url', id: 'https://example.com/qa-fp3', title: 'example.com' },
    { type: 'task', id: s1.id, title: s1.title },
  ]);
  expect(Array.isArray(ctxA) && ctxA.length === 3, 'контекст A записан').toBeTruthy();
  const ctxC = await client.put<unknown[]>(`/api/projects/${project.id}/sessions/${chatC.id}/context`, [
    { type: 'file', id: 'qa-fp3-doc.md', title: 'qa-fp3-doc.md' },
    { type: 'url', id: 'https://example.org/qa-fp3-c', title: 'example.org' },
  ]);
  expect(Array.isArray(ctxC) && ctxC.length === 2, 'контекст C записан').toBeTruthy();

  mkdirSync(OUT, { recursive: true });
  writeFileSync(`${OUT}/seed.json`, JSON.stringify({
    stamp: STAMP, rootPath,
    project: project.id,
    fOpen: fOpen.id, s1: s1.id, s1b: s1b.id, s1c: s1c.id,
    fClosed: fClosed.id, s2: s2.id,
    chatA: chatA.id, chatA2: chatA2.id, chatB: chatB.id, chatC: chatC.id,
  }, null, 1));

  // самопроверка засев: индекс фич видит обе фичи проекта (открытая и закрытая)
  const idx = await client.get<{ features: Array<{ featureId: string; state: string }> }>(
    `/api/tasks/features/index?projectId=${project.id}`);
  const open = idx.features.find(f => f.featureId === fOpen.id);
  const closed = idx.features.find(f => f.featureId === fClosed.id);
  expect(open && open.state !== 'merged' && open.state !== 'closed', 'открытая фича в индексе открыта').toBeTruthy();
  expect(closed && (closed.state === 'closed' || closed.state === 'merged'), 'закрытая фича в индексе закрыта').toBeTruthy();
});
