import { test, expect, type APIRequestContext } from '@playwright/test';
import { login, api, type Api } from './helpers';

// Часть D: этап «План» идёт и сам останавливается с вопросом человеку —
// чтобы блок «вопросы к вам» и метрика ожидания человека проверялись живым состоянием.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const FEAT = 'qa-e2e-wf-feature';
const PLAN = 'qa-e2e-wf plan';

let client: Api;
let request: APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => { await request?.dispose(); });

test('С5: этап с вопросом человеку', async ({ playwright, baseURL }) => {
  test.setTimeout(420_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);

  const tasks = await client.get<any[]>(`/api/projects/${PID}/tasks`);
  const epic = tasks.find(t => t.title === FEAT && t.role === 'feature');
  expect(epic, 'эпик есть').toBeTruthy();

  let plan = tasks.find(t => t.title === PLAN);
  if (!plan) {
    plan = await client.post<any>(`/api/projects/${PID}/tasks`, {
      title: PLAN, role: 'plan', featureId: epic!.id, assignee: 'claude',
      description: 'План этапа: прочитай qa/cut.md и qa/review.md этого проекта и запиши в qa/plan.md короткий план проверки (5–8 строк). Затем ВЫЗОВИ tasks_update на этой задаче с awaitingHuman: {ask: «Какую линию проверки считать главной — экраны или API-контракты?»} и ОСТАВЬ задачу незавершённой: не вызывай tasks_complete.',
    });
  }
  console.log('С5 задача плана:', plan!.id, 'stage:', JSON.stringify(plan!.stage)?.slice(0, 120));

  const ex = await req.post(`/api/tasks/${plan!.id}/execute`, { headers: { Authorization: `Bearer ${token}` } });
  console.log('С5 execute:', ex.status());
  let asked = false;
  for (let i = 0; i < 50; i++) {
    await new Promise(r => setTimeout(r, 4000));
    const cur = await client.get<any>(`/api/tasks/${plan!.id}`);
    if (cur?.awaitingHumanSince) { asked = true; console.log('С5 вопрос поставлен за', (i + 1) * 4, 'с:', String(cur.awaitingHumanReason).slice(0, 120)); break; }
    if (cur?.status === 'done') { console.log('С5 задача закрылась без вопроса'); break; }
    if (i % 5 === 0) console.log('С5 статус:', cur?.status, 'stop:', cur?.executorStopReason);
  }
  expect.soft(asked, 'исполнитель сам поставил вопрос человеку').toBeTruthy();
  const cur = await client.get<any>(`/api/tasks/${plan!.id}`);
  console.log('С5 состояние:', cur?.status, 'awaiting:', cur?.awaitingHumanSince, 'linkedFiles:', JSON.stringify(cur?.linkedFiles));
  await req.dispose();
});
