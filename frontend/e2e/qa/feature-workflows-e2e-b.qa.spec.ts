import { test, expect, type APIRequestContext } from '@playwright/test';
import { login, api, type Api } from './helpers';

// Часть B: живой шаг первого этапа подтверждённого маршрута (С4 groundwork).
// Задача этапа заводится так, как её заводит оркестратор: role этапа + featureId.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const FEAT = 'qa-e2e-wf-feature';
const CUT = 'qa-e2e-wf cut';

let client: Api;

let request: APIRequestContext;
test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => { await request?.dispose(); });

test('С4а: запуск этапа и его завершение', async ({ playwright, baseURL }) => {
  test.setTimeout(480_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);

  const tasks = await client.get<any[]>(`/api/projects/${PID}/tasks`);
  const epic = tasks.find(t => t.title === FEAT && t.role === 'feature');
  expect(epic, 'эпик есть').toBeTruthy();
  const epicId = epic!.id;

  let cut = tasks.find(t => t.title === CUT);
  if (!cut) {
    cut = await client.post<any>(`/api/projects/${PID}/tasks`, {
      title: CUT, role: 'cut', featureId: epicId, assignee: 'claude',
      description: 'Разрез фичи: перечисли, что именно проверяет фича, и запиши список экранов и сценариев в файл qa/cut.md этого проекта (создай папку qa).',
    });
  }
  console.log('С4а задача этапа:', cut!.id, 'stage:', JSON.stringify(cut!.stage)?.slice(0, 300));
  expect.soft(!!cut!.stage, 'задача получила слепок этапа из маршрута').toBeTruthy();

  if (cut!.status !== 'done') {
    const ex = await req.post(`/api/tasks/${cut!.id}/execute`, { headers: { Authorization: `Bearer ${token}` } });
    console.log('С4а execute:', ex.status(), (await ex.text()).slice(0, 120));
    let done = false;
    for (let i = 0; i < 60; i++) {
      await new Promise(r => setTimeout(r, 5000));
      const cur = await client.get<any>(`/api/tasks/${cut!.id}`);
      if (cur?.status === 'done') { done = true; console.log('С4а этап завершён за', (i + 1) * 5, 'с'); break; }
      if (i % 6 === 0) console.log('С4а статус:', cur?.status, 'stop:', cur?.executorStopReason);
    }
    expect.soft(done, 'живой ход этапа завершился').toBeTruthy();
  }
  const after = await client.get<any>(`/api/tasks/${cut!.id}`);
  console.log('С4а итог этапа:', String(after?.resultMarkdown ?? '').slice(0, 400));
  console.log('С4а linkedFiles:', JSON.stringify(after?.linkedFiles));
  console.log('С4а personaId:', after?.personaId);
  await req.dispose();
});
