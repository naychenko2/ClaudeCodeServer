import { test, expect, type APIRequestContext } from '@playwright/test';
import { login, api, type Api } from './helpers';

// Часть F: этап в рабочем дереве с коммитом, помеченным задачей, — чтобы проверить
// «артефакты с версиями и диффом» на настоящих данных.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const FEAT = 'qa-e2e-wf-feature';
const STEP = 'qa-e2e-wf step';
const WT = '/tmp/cff-wt';

let client: Api;
let request: APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => { await request?.dispose(); });

test('С7: этап в worktree с коммитом CCS-Task', async ({ playwright, baseURL }) => {
  test.setTimeout(420_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);

  const tasks = await client.get<any[]>(`/api/projects/${PID}/tasks`);
  const epic = tasks.find(t => t.title === FEAT && t.role === 'feature');
  expect(epic, 'эпик есть').toBeTruthy();

  let step = tasks.find(t => t.title === STEP);
  if (!step) {
    step = await client.post<any>(`/api/projects/${PID}/tasks`, {
      title: STEP, role: 'step', featureId: epic!.id, assignee: 'claude', worktreePath: WT,
      description: 'Рабочее дерево этой задачи — ' + WT + '. Создай в нём файл qa/step.md с чек-листом из 5 пунктов проверки экранов фичи. Затем закоммить этот файл В СВОЁМ РАБОЧЕМ ДЕРЕВЕ и заверши задачу через tasks_complete.',
    });
    // id известен после создания — дописываем его в трейлер
    await client.put<any>(`/api/tasks/${step.id}`, { description: `Рабочее дерево этой задачи — ${WT}. Создай в нём файл qa/step.md с чек-листом из 5 пунктов проверки экранов фичи. Затем закоммить этот файл В СВОЁМ РАБОЧЕМ ДЕРЕВЕ: git add qa/step.md && git commit -m "feat(qa): чек-лист проверки экранов" — и добавь в конец сообщения коммита строку-трейлер «CCS-Task: ${step.id}». После этого заверши задачу через tasks_complete.` });
  }
  console.log('С7 задача:', step!.id, 'worktree:', step!.worktreePath ?? (await client.get<any>(`/api/tasks/${step!.id}`))?.worktreePath);

  if ((await client.get<any>(`/api/tasks/${step!.id}`)).status !== 'done') {
    const ex = await req.post(`/api/tasks/${step!.id}/execute`, { headers: { Authorization: `Bearer ${token}` } });
    console.log('С7 execute:', ex.status());
    for (let i = 0; i < 50; i++) {
      await new Promise(r => setTimeout(r, 4000));
      const cur = await client.get<any>(`/api/tasks/${step!.id}`);
      if (cur?.status === 'done') { console.log('С7 завершена за', (i + 1) * 4, 'с'); break; }
      if (cur?.status === 'todo' && i > 3) { console.log('С7 сорвалась:', cur?.executorStopReason); break; }
      if (i % 6 === 0) console.log('С7 статус:', cur?.status, 'stop:', cur?.executorStopReason);
    }
  }
  const st = await req.get(`/api/tasks/${step!.id}/stage`, { headers: { Authorization: `Bearer ${token}` } });
  const j = JSON.parse(await st.text() || '{}');
  console.log('С7 /stage artifacts:', JSON.stringify({ commits: j?.artifacts?.commits?.map((c: any) => ({ sha: c.sha, subject: c.subject, files: c.files })), available: j?.artifacts?.commitsAvailable, files: j?.artifacts?.files?.map((f: any) => ({ path: f.path, versions: f.versions?.length })) }));
  if (j?.artifacts?.files?.length) {
    const p = encodeURIComponent(j.artifacts.files[0].path);
    const d = await req.get(`/api/tasks/${step!.id}/stage/files/${p}/diff`, { headers: { Authorization: `Bearer ${token}` } });
    console.log('С7 diff статус:', d.status(), (await d.text()).slice(0, 300));
  }
  await req.dispose();
});
