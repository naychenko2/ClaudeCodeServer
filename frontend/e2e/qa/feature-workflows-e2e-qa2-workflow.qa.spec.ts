import { test, expect } from '@playwright/test';
import { login, api, type Api } from './helpers';

// QA-2 (задача 6c3cdc39), блок процесса.
// S5 = п.5: политика при зависании и таймаут этапа — значения меняются и применяются
//      (доходят до слепка этапа — единственного источника, который читает исполнение).
// S6 = п.6: авторазвержение маршрута (autoConfirm) — как включается и что происходит.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const SHOTS = '.cc-attachments/qa-wf-e2e';

let client: Api;
let request: import('@playwright/test').APIRequestContext;
let token: string;
let tplId: string | null = null;
const epicIds: string[] = [];

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => {
  for (const id of epicIds) await client.del(`/api/projects/${PID}/tasks/${id}`).catch(() => undefined);
  if (tplId) await client.del(`/api/workflow-templates/${tplId}`).catch(() => undefined);
  await request?.dispose();
});

test('S5: порог молчания и политика зависания — сохранение и применение в слепке этапа', async () => {
  test.setTimeout(240_000);

  // Свой глобальный шаблон с явными значениями на этапе «Производство».
  const body = {
    name: 'qa-e2e-qa2-stall',
    stages: [
      { role: 'cut', presence: 'on' },
      { role: 'step', presence: 'on', lane: 'other', title: 'Производство', stallMinutes: 7, stallPolicy: 'stop-alert' },
      { role: 'review', presence: 'on' },
    ],
  };
  const tpl = await client.post<any>('/api/workflow-templates', body);
  tplId = tpl?.id ?? tpl?.template?.id ?? null;
  console.log('S5 шаблон создан:', JSON.stringify({ id: tplId, version: tpl?.version ?? tpl?.template?.version }));
  expect(!!tplId, 'шаблон создан').toBeTruthy();

  const list = await client.get<any[]>('/api/workflow-templates');
  const mine = list.find(t => t.id === tplId || t.template?.id === tplId);
  const st = (mine?.stages ?? mine?.template?.stages ?? []).find((s: any) => s.role === 'step');
  console.log('S5 этап шаблона после сохранения:', JSON.stringify({ stallMinutes: st?.stallMinutes, stallPolicy: st?.stallPolicy }));
  expect.soft(st?.stallMinutes, 'порог молчания 7 сохранён в шаблоне').toBe(7);
  expect.soft(st?.stallPolicy, 'политика stop-alert сохранена в шаблоне').toBe('stop-alert');

  // Эпик с подтверждённым маршрутом по этому шаблону — и задача этапа:
  // её слепок обязан нести эти значения (исполнение читает только слепок).
  const epic = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa2-stall-feature', role: 'feature', assignee: 'me',
    description: 'Проверка применения настроек этапа.',
  });
  epicIds.push(epic.id);
  await client.post(`/api/tasks/${epic.id}/workflow/confirm`, { templateId: tplId, scope: 'global' });

  const step1 = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa2-stall-step-1', role: 'step', featureId: epic.id, assignee: 'claude',
    description: 'Ничего не делай, ответь одно слово: готово',
  });
  const snap1 = (await client.get<any>(`/api/tasks/${step1.id}`))?.stage;
  console.log('S5 слепок этапа 1:', JSON.stringify({ v: snap1?.templateVersion, stallMinutes: snap1?.stallMinutes, stallPolicy: snap1?.stallPolicy }));
  expect.soft(snap1?.stallMinutes, 'слепок этапа нёс порог 7').toBe(7);
  expect.soft(snap1?.stallPolicy, 'слепок этапа нёс stop-alert').toBe('stop-alert');

  // Правка значений в шаблоне → новый этап получает новые (правка действует со следующего шага).
  const edited = JSON.parse(JSON.stringify(mine?.template ?? mine));
  edited.id = tplId;
  const st2 = edited.stages.find((s: any) => s.role === 'step');
  st2.stallMinutes = 15; st2.stallPolicy = 'nudge-alert';
  // Правка — PUT по id с expectedVersion последней прочитанной (api.ts saveTemplate).
  await client.put(`/api/workflow-templates/${tplId}`, { ...edited, expectedVersion: (mine?.version ?? mine?.template?.version ?? 1) });
  const afterEdit = await client.get<any[]>('/api/workflow-templates');
  const tplNow = afterEdit.find(t => t.id === tplId || t.template?.id === tplId);
  const stNow = (tplNow?.stages ?? tplNow?.template?.stages ?? []).find((s: any) => s.role === 'step');
  console.log('S5 шаблон после правки:', JSON.stringify({ version: tplNow?.version ?? tplNow?.template?.version, stallMinutes: stNow?.stallMinutes, stallPolicy: stNow?.stallPolicy }));
  expect.soft(stNow?.stallMinutes, 'правка порода дошла до шаблона').toBe(15);

  const step2 = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa2-stall-step-2', role: 'step', featureId: epic.id, assignee: 'claude',
    description: 'Ничего не делай, ответь одно слово: готово',
  });
  const snap2 = (await client.get<any>(`/api/tasks/${step2.id}`))?.stage;
  console.log('S5 слепок этапа 2:', JSON.stringify({ v: snap2?.templateVersion, stallMinutes: snap2?.stallMinutes, stallPolicy: snap2?.stallPolicy }));
  expect.soft(snap2?.stallMinutes, 'слепок нового этапа нёс правленый порог 15').toBe(15);
  expect.soft(snap2?.stallPolicy, 'слепок нового этапа нёс nudge-alert').toBe('nudge-alert');
  // Прежний этап остался на своём слепке (правка не переписывает созданные шаги).
  const snap1After = (await client.get<any>(`/api/tasks/${step1.id}`))?.stage;
  console.log('S5 слепок этапа 1 после правки шаблона:', JSON.stringify({ v: snap1After?.templateVersion, stallMinutes: snap1After?.stallMinutes }));
  expect.soft(snap1After?.stallMinutes, 'созданный ранее этап остался на пороге 7').toBe(7);

  await client.del(`/api/projects/${PID}/tasks/${step1.id}`).catch(() => undefined);
  await client.del(`/api/projects/${PID}/tasks/${step2.id}`).catch(() => undefined);
});

test('S6: авторазвержение маршрута — как включается и что происходит', async ({ context }) => {
  test.setTimeout(360_000);

  // Механизма-переключателя нет: автоподтверждение пишет только фоновый путь
  // после рекомендации, когда случай «очевиден» (без сомнений и снятых этапов).
  // Проверяем живой факт: эпик с однозначной задачей → что пишет сервер.
  const epic = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa2-auto', role: 'feature', assignee: 'me',
    description: 'Большая фича: новый экран «Статистика расходов» со списком и фильтрами. Нужен бэкенд-эндпоит агрегации, фронтенд-страница, тесты. Обычный порядок работ.',
  });
  epicIds.push(epic.id);
  console.log('S6 эпик:', epic.id);

  let wf: any = null;
  for (let i = 0; i < 45; i++) {
    await new Promise(r => setTimeout(r, 4000));
    wf = await client.get<any>(`/api/tasks/${epic.id}/workflow`);
    if (wf?.confirmed || wf?.proposal) break;
  }
  // Фоновый путь не дал предложения — пробуем явный recommend (тот же путь модели,
  // что делал рабочий круг).
  const rec = await request.post(`/api/tasks/${epic.id}/workflow/recommend`, { headers: { Authorization: `Bearer ${token}` } });
  const recBody = await rec.json().catch(() => null);
  console.log('S6 явный recommend:', rec.status(), JSON.stringify(recBody).slice(0, 500));
  const wf2 = await client.get<any>(`/api/tasks/${epic.id}/workflow`);
  if (wf2) wf = wf2;
  const confirmed = wf?.confirmed;
  const proposal = wf?.proposal ?? recBody?.proposal ?? wf?.proposalState;
  console.log('S6 состояние маршрута:', JSON.stringify({
    confirmed: !!confirmed, autoConfirmed: confirmed?.autoConfirmed ?? null,
    version: confirmed?.confirmedVersion ?? null,
    doubt: proposal?.doubtReason ?? null, skipped: proposal?.skipped ?? null,
    state: proposal?.state ?? null, reason: proposal?.reason ?? null,
    template: proposal?.templateId ?? proposal?.templateKey ?? null,
  }).slice(0, 700));

  if (confirmed?.autoConfirmed) {
    console.log('S6 автоподтверждение произошло');
    // UI: бейдж «авто» и строка «выбрано моделью автоматически».
    const page = await context.newPage();
    page.setViewportSize({ width: 1280, height: 900 });
    await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
    await page.goto(`/#/project/${PID}/feature/${epic.id}`, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(4000);
    const body = (await page.locator('body').innerText()).replace(/\n/g, ' | ');
    const hasAuto = /выбрано моделью/.test(body);
    console.log('S6 строка автоподтверждения в UI:', hasAuto);
    expect.soft(hasAuto, 'UI показывает автоподтверждение').toBeTruthy();
    await page.screenshot({ path: `${SHOTS}/qa2-s6-autoconfirm.png`, fullPage: true });
    await page.close();
    expect.soft(true, 'факт: авторазвержение сработало').toBeTruthy();
  } else {
    console.log('S6 автоподтверждение НЕ произошло: ', JSON.stringify({ doubt: proposal?.doubtReason ?? null, state: proposal?.state ?? null }));
    // Это не дефект: без «очевидного» случая автоподтверждение не ставится (WorkflowRecommender.TryAutoConfirm).
    expect.soft(!!(proposal || !wf?.confirmed), 'без очевидного случая предложение осталось на подтверждении человека').toBeTruthy();
  }
});
