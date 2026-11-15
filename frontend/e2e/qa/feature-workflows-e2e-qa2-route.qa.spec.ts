import { test, expect } from '@playwright/test';
import { login, api, type Api } from './helpers';

// QA-2 (задача 6c3cdc39), блок маршрута.
// S10 = п.10: «Обновить маршрут до vN» применяет версию сразу — ответ POST confirm несёт
//      confirmed.confirmedVersion = 3 (bfefbd50c); карточка исчезает, следующий этап берёт v3.
// S11 = п.11: «изменился = состав» (33894b62f): переименование этапа — предупреждения нет;
//      добавленный этап — карточка с «Обновить»/«Оставить».

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const SHOTS = '.cc-attachments/qa-wf-e2e';

let client: Api;
let request: import('@playwright/test').APIRequestContext;
let token: string;
const epicIds: string[] = [];
const tplIds: string[] = [];

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => {
  for (const id of epicIds) await client.del(`/api/projects/${PID}/tasks/${id}`).catch(() => undefined);
  for (const id of tplIds) await client.del(`/api/workflow-templates/${id}`).catch(() => undefined);
  await request?.dispose();
});

async function makeTemplate(name: string, stages: any[]): Promise<{ id: string; version: number }> {
  const r = await client.post<any>('/api/workflow-templates', { name, stages });
  const id = r?.id ?? r?.template?.id;
  tplIds.push(id);
  return { id, version: r?.version ?? r?.template?.version ?? 1 };
}

async function editTemplate(id: string, mutate: (t: any) => void): Promise<number> {
  const list = await client.get<any[]>('/api/workflow-templates');
  const entry = list.find(t => (t.template?.id ?? t.id) === id);
  const full = JSON.parse(JSON.stringify(entry?.template ?? entry));
  mutate(full);
  // Ответ PUT — сохранённый шаблон с новой version (контракт WorkflowTemplatesController):
  // сузим тип ответа до него; фолбэк full.version + 1 остаётся на случай пустого тела.
  const res = await client.put<{ version?: number }>(`/api/workflow-templates/${id}`, { ...full, expectedVersion: full.version });
  return res?.version ?? full.version + 1;
}

async function confirmRoute(epicId: string, templateId: string) {
  return await client.post<any>(`/api/tasks/${epicId}/workflow/confirm`, { templateId, scope: 'global' });
}

async function openFeature(context: import('@playwright/test').BrowserContext, epicId: string) {
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  page.on('pageerror', e => errs.push(String(e).slice(0, 200)));
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PID}/feature/${epicId}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(5000);
  return { page, errs };
}

test('S10: «Обновить маршрут до v3» — ответ confirm несёт confirmedVersion=3, карточка гаснет, следующий этап на v3', async ({ context }) => {
  test.setTimeout(300_000);

  const tpl = await makeTemplate('qa-e2e-qa2-route-v3', [
    { role: 'cut', presence: 'on' },
    { role: 'step', presence: 'on', lane: 'other', title: 'Производство' },
    { role: 'review', presence: 'on' },
  ]);
  const epic = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa2-route-feature', role: 'feature', assignee: 'me',
    description: 'Проверка обновления маршрута до v3.',
  });
  epicIds.push(epic.id);
  const c1 = await confirmRoute(epic.id, tpl.id);
  console.log('S10 подтверждение v1:', c1?.confirmed?.confirmedVersion);
  expect.soft(c1?.confirmed?.confirmedVersion, 'маршрут подтверждён на v1').toBe(1);

  // v2 — добавленный этап (состав меняется), v3 — ещё одна правка.
  const v2 = await editTemplate(tpl.id, t => t.stages.push({ role: 'qa', presence: 'on', title: 'Проверка' }));
  const v3 = await editTemplate(tpl.id, t => {
    const q = t.stages.find((s: any) => s.role === 'qa'); if (q) q.title = 'Проверка качества';
  });
  console.log('S10 версии шаблона:', v2, v3);

  const { page, errs } = await openFeature(context, epic.id);
  const card = page.getByTestId('feature-workflow-confirm-update');
  const cardOn = await card.count();
  const cardText = cardOn ? await card.innerText() : '';
  console.log('S10 карточка до обновления:', cardOn, JSON.stringify(cardText.slice(0, 200)));
  expect.soft(cardOn, 'карточка «шаблон изменился» есть').toBeGreaterThan(0);

  // Клик «Обновить маршрут до v3» с ответом сервера — именно его снимаем.
  const [resp] = await Promise.all([
    page.waitForResponse(r => r.url().includes('/workflow/confirm') && r.request().method() === 'POST', { timeout: 30000 }),
    page.getByTestId('feature-workflow-confirm-update-apply').click({ timeout: 15000 }),
  ]);
  const body = await resp.json().catch(() => null);
  console.log('S10 ответ POST confirm:', resp.status(), JSON.stringify({
    confirmedVersion: body?.confirmed?.confirmedVersion,
    updateAvailable: body?.updateAvailable,
  }));
  expect.soft(body?.confirmed?.confirmedVersion, 'ответ confirm несёт confirmedVersion=3').toBe(3);

  // Карточка гаснет сразу (Ф-2.15) и не возвращается после перечитывания.
  await page.waitForTimeout(3000);
  const cardAfter = await card.count();
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(5000);
  const cardAfterReload = await page.getByTestId('feature-workflow-confirm-update').count();
  console.log('S10 карточка после обновления:', cardAfter, 'после перезагрузки:', cardAfterReload);
  expect.soft(cardAfter, 'карточка исчезла сразу').toBe(0);
  expect.soft(cardAfterReload, 'карточка не вернулась после перечитывания').toBe(0);

  // Следующий этап берёт шаблон v3.
  const step = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa2-route-qa', role: 'qa', featureId: epic.id, assignee: 'claude',
    description: 'Ничего не делай, ответь одно слово: готово',
  });
  const snap = (await client.get<any>(`/api/tasks/${step.id}`))?.stage;
  console.log('S10 слепок нового этапа:', JSON.stringify({ v: snap?.templateVersion }));
  expect.soft(snap?.templateVersion, 'новый этап взял шаблон v3').toBe(3);

  await page.screenshot({ path: `${SHOTS}/qa2-s10-route-v3.png`, fullPage: true });
  console.log('S10 ошибки консоли:', JSON.stringify([...new Set(errs)]));
  await page.close();
  await client.del(`/api/projects/${PID}/tasks/${step.id}`).catch(() => undefined);
});

test('S11: «изменился = состав» — переименование этапа молчит, добавленный этап даёт карточку', async ({ context }) => {
  test.setTimeout(300_000);

  const tpl = await makeTemplate('qa-e2e-qa2-compose', [
    { role: 'cut', presence: 'on' },
    { role: 'review', presence: 'on' },
  ]);
  const epic = await client.post<any>(`/api/projects/${PID}/tasks`, {
    title: 'qa-e2e-qa2-compose-feature', role: 'feature', assignee: 'me',
    description: 'Проверка правила «изменился = состав».',
  });
  epicIds.push(epic.id);
  await confirmRoute(epic.id, tpl.id);

  // Правка состава не меняется: переименование этапа (тот же id) → v2 → предупреждения нет.
  const v2 = await editTemplate(tpl.id, t => {
    const r = t.stages.find((s: any) => s.role === 'review'); if (r) r.title = 'Ревью (переименовано)';
  });
  const wf1 = await client.get<any>(`/api/tasks/${epic.id}/workflow`);
  console.log('S11 после переименования (v2):', JSON.stringify({
    v: v2, updateAvailable: wf1?.updateAvailable, templateUpdate: wf1?.templateUpdate ?? null,
  }));
  expect.soft(v2, 'переименование подняло версию шаблона').toBe(2);
  expect.soft(wf1?.updateAvailable, 'переименование — не перемена состава: updateAvailable=false').toBeFalsy();
  expect.soft(wf1?.templateUpdate ?? null, 'templateUpdate нет').toBeNull();

  const { page, errs } = await openFeature(context, epic.id);
  const cardAfterRename = await page.getByTestId('feature-workflow-confirm-update').count();
  console.log('S11 карточка после переименования:', cardAfterRename);
  expect.soft(cardAfterRename, 'карточки «шаблон изменился» после переименования нет').toBe(0);
  await page.close();

  // Перемена состава: добавленный этап → v3 → карточка с «Обновить»/«Оставить».
  const v3 = await editTemplate(tpl.id, t => t.stages.push({ role: 'qa', presence: 'on', title: 'Проверка' }));
  const wf2 = await client.get<any>(`/api/tasks/${epic.id}/workflow`);
  console.log('S11 после добавления этапа (v3):', JSON.stringify({
    v: v3, updateAvailable: wf2?.updateAvailable, templateUpdate: wf2?.templateUpdate ?? null,
  }));
  expect.soft(wf2?.updateAvailable, 'добавленный этап: updateAvailable=true').toBeTruthy();

  const { page: p2, errs: e2 } = await openFeature(context, epic.id);
  const card = p2.getByTestId('feature-workflow-confirm-update');
  const cardOn = await card.count();
  const hasApply = await p2.getByTestId('feature-workflow-confirm-update-apply').count();
  const hasKeep = await p2.getByTestId('feature-workflow-confirm-update-keep').count();
  console.log('S11 карточка после добавления этапа:', cardOn, '| «Обновить»:', hasApply, '| «Оставить»:', hasKeep);
  expect.soft(cardOn, 'карточка есть').toBeGreaterThan(0);
  expect.soft(hasApply, 'кнопка «Обновить маршрут» есть').toBeGreaterThan(0);
  expect.soft(hasKeep, 'кнопка «Оставить» есть').toBeGreaterThan(0);
  await p2.screenshot({ path: `${SHOTS}/qa2-s11-card.png`, fullPage: true });
  console.log('S11 ошибки консоли:', JSON.stringify([...new Set([...errs, ...e2])]));
  await p2.close();
});
