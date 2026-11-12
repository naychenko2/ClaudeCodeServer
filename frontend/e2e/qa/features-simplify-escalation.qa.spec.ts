import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login, type Api } from './helpers';

// FS-QA-2: дополнения к сценарию 8 (эскалация) фичи «Упрощение раздела «Фичи»».
// Фикстура — .cc-attachments/qa/fs_fixture.py. Запускаться ДО features-simplify.qa.spec.ts
// (файл первым в files=[…]): 8.1 проверяет, что служебная подача ожидание НЕ сняла,
// 8.2 снимает ожидание второго шага (W2), основной сценарий 8 потом снимает W1.

const PROJECT_NAME = 'qa-fs';
const WAIT = 'qa-fs · ждёт тебя';

test.describe.configure({ mode: 'serial' });

let request: APIRequestContext;
let token: string;
let client: Api;
let projectId: string;

interface TaskRow {
  id: string;
  title: string;
  role: string | null;
  stepRef: string | null;
  awaitingHumanSince: string | null;
  linkedSessionId: string | null;
}

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  const projects = await client.get<Array<{ id: string; name: string }>>('/api/projects');
  const p = projects.find(x => x.name === PROJECT_NAME);
  expect(p, 'проект фикстуры qa-fs должен быть на стенде').toBeTruthy();
  projectId = p!.id;
});

test.afterAll(async () => {
  await request?.dispose();
});

async function tasks(): Promise<TaskRow[]> {
  return await client.get<TaskRow[]>(`/api/projects/${projectId}/tasks`);
}

async function attention(featureId: string): Promise<string | null> {
  const r = await client.get<{ run: { attention: { kind: string } | null } }>(`/api/tasks/features/${featureId}`);
  return r.run.attention?.kind ?? null;
}

test('сценарий 8.1: служебная подача «продолжить в новом чате» ожидание НЕ снимает', async () => {
  const idx = await client.get<{ features: Array<{ featureId: string; title: string }> }>('/api/tasks/features/index');
  const wait = idx.features.find(f => f.title === WAIT)!;
  expect(await attention(wait.featureId), 'фича ждёт человека').toBe('wait');

  const w1 = (await tasks()).find(t => t.stepRef === 'W1')!;
  expect(w1.awaitingHumanSince, 'у W1 стоит ожидание').toBeTruthy();
  const chatId = w1.linkedSessionId!;
  expect(chatId, 'у W1 есть чат').toBeTruthy();

  // Служебная подача — ход передачи в чате задачи. Не ждём его конца: на дев-стенде без
  // CLI он закончится отказом; важен сам факт подачи и её отсутствие на шине.
  const cont = request.post(`/api/chats/${chatId}/continue`, {
    headers: { Authorization: `Bearer ${token}` },
    data: { note: 'FS-QA-2: проверочная приписка' },
  });

  // 10 с после начала подачи: ожидание обязано стоять (без правки FS-E3b-2 здесь оно бы
  // погасло — служебная подача прошла бы как ответ человека).
  await expect
    .poll(async () => (await tasks()).find(t => t.stepRef === 'W1')?.awaitingHumanSince ?? null,
      { timeout: 12000, intervals: [1000, 2000] })
    .not.toBeNull();
  expect(await attention(wait.featureId), 'фича всё ещё «ждёт тебя»').toBe('wait');

  const res = await cont;
  const body = await res.text();
  // На этом стенде ход передачи не может состояться (CLI без OAuth) — продолжение
  // отвергается. Отказ ДО подачи («передавать нечего») значит, что фикстура не сработала.
  expect(body, 'подача дошла до хода передачи: ' + body.slice(0, 200)).not.toContain('передавать нечего');
  expect(res.status(), 'продолжение отвергнуто (409)').toBe(409);

  // И после отказа — ожидание по-прежнему стоит, фича ждёт.
  expect((await tasks()).find(t => t.stepRef === 'W1')?.awaitingHumanSince, 'W1 всё ещё ждёт').toBeTruthy();
  expect(await attention(wait.featureId), 'фича всё ещё «ждёт тебя»').toBe('wait');
});

test('сценарий 8.2: ответ человека в свободный чат снимает ожидание (прямая подача)', async () => {
  const w2 = (await tasks()).find(t => t.stepRef === 'W2')!;
  expect(w2.awaitingHumanSince, 'у W2 стоит ожидание').toBeTruthy();
  const chatId = w2.linkedSessionId!;
  expect(chatId, 'у W2 есть чат').toBeTruthy();

  await client.post(`/api/sessions/${chatId}/messages`, { text: 'Беру срок: неделя.', wait: 'none' });

  await expect
    .poll(async () => (await tasks()).find(t => t.stepRef === 'W2')?.awaitingHumanSince ?? null,
      { timeout: 20000, intervals: [500, 1000, 2000] })
    .toBeNull();

  // Чужое ожидание (W1) прямая подача в чат W2 не трогает: фича всё ещё ждёт — это
  // готовит основной сценарий 8.
  const idx = await client.get<{ features: Array<{ featureId: string; title: string }> }>('/api/tasks/features/index');
  const wait = idx.features.find(f => f.title === WAIT)!;
  expect(await attention(wait.featureId), 'фича всё ещё ждёт по W1').toBe('wait');
});
