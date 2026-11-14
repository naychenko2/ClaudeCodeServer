import { test, expect, type APIRequestContext } from '@playwright/test';
import { login, api, type Api } from './helpers';

// Часть C1: ревью этапа по-настоящему (role=review, reviewOf=[задача этапа]) —
// чтобы на экране этапа были отчёт ревью и метрики по трём ролям.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const FEAT = 'qa-e2e-wf-feature';
const CUT = 'qa-e2e-wf cut';
const REV = 'qa-e2e-wf review';

let client: Api;
let request: APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => { await request?.dispose(); });

test('С4б: живой ревью этапа', async ({ playwright, baseURL }) => {
  test.setTimeout(480_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);

  const tasks = await client.get<any[]>(`/api/projects/${PID}/tasks`);
  const epic = tasks.find(t => t.title === FEAT && t.role === 'feature');
  const cut = tasks.find(t => t.title === CUT);
  expect(epic && cut, 'эпик и задача этапа есть').toBeTruthy();

  let rev = tasks.find(t => t.title === REV);
  if (!rev) {
    rev = await client.post<any>(`/api/projects/${PID}/tasks`, {
      title: REV, role: 'review', featureId: epic!.id, reviewOf: [cut!.id], assignee: 'claude',
      description: 'Отревьюй разрез из qa/cut.md этого проекта. Найди, что в списке пропущено или сформулировано двусмысленно. Запиши находки в qa/review.md и закоммить их в git этого проекта (git add qa/review.md && git commit). Завершая задачу, поставь вердикт accepted_with_remarks с перечнем замечаний.',
    });
  }
  console.log('С4б задача ревью:', rev!.id, 'reviewOf:', JSON.stringify(rev!.reviewOf), 'stage:', JSON.stringify(rev!.stage)?.slice(0, 120));

  if (rev!.status !== 'done') {
    const ex = await req.post(`/api/tasks/${rev!.id}/execute`, { headers: { Authorization: `Bearer ${token}` } });
    console.log('С4б execute:', ex.status());
    let done = false;
    for (let i = 0; i < 60; i++) {
      await new Promise(r => setTimeout(r, 5000));
      const cur = await client.get<any>(`/api/tasks/${rev!.id}`);
      if (cur?.status === 'done') { done = true; console.log('С4б ревью завершён за', (i + 1) * 5, 'с'); break; }
      if (i % 6 === 0) console.log('С4б статус:', cur?.status, 'stop:', cur?.executorStopReason);
    }
    expect.soft(done, 'живой ревью завершился').toBeTruthy();
  }
  const after = await client.get<any>(`/api/tasks/${rev!.id}`);
  console.log('С4б вердикт:', JSON.stringify(after?.verification), 'linkedFiles:', JSON.stringify(after?.linkedFiles));
  console.log('С4б результат:', String(after?.resultMarkdown ?? '').slice(0, 400));

  // Вопрос человеку на этапе — тем же API, которым пользуется исполнитель
  const q = await client.put<any>(`/api/tasks/${rev!.id}`, { awaitingHuman: { ask: 'Уточнить объём: проверять ли экран наследования в той же фиче?' } });
  console.log('С4б awaitingHuman поставлен:', JSON.stringify(q?.awaitingHumanSince ?? q?.awaitingHuman)?.slice(0, 120));
  await req.dispose();
});
