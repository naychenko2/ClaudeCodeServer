import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login, api, type Api } from './helpers';

// Часть G: правка шаблона у идущей фичи (эффект со следующего шага, готовые шаги не
// переписываются) и кастомизация на уровне проекта поверх админского пресета.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';
const SHOTS = '.cc-attachments/qa-wf-e2e';
const flat = (s: string) => s.replace(/\n/g, ' | ');

let client: Api;
let request: import('@playwright/test').APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  mkdirSync(SHOTS, { recursive: true });
  request = await playwright.request.newContext({ baseURL });
  const token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => { await request?.dispose(); });

async function editFullCycle(page: Page, title: string) {
  await page.goto('/#/specialties', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(2500);
  await page.getByText('Процессы фичи', { exact: true }).click();
  await page.waitForTimeout(1500);
  await page.getByRole('button', { name: /Полный цикл/ }).first().click();
  await page.waitForTimeout(1200);
  await page.locator('[data-stage-index]').filter({ hasText: 'design' }).first().click();
  await page.waitForTimeout(1000);
  const nameInput = page.locator('[data-stage-settings]').locator('input').first();
  await nameInput.fill(title);
  await page.getByLabel('Закрыть настройку этапа').first().click({ timeout: 8000 }).catch(() => page.keyboard.press('Escape'));
  await page.waitForTimeout(500);
  await page.getByRole('button', { name: 'Сохранить', exact: true }).first().click({ timeout: 15000 });
  await page.waitForTimeout(2500);
}

test('С8: правка шаблона на идущей фиче', async ({ context, playwright, baseURL }) => {
  test.setTimeout(280_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const before = await client.get<any>(`/api/tasks/${EPIC}/workflow`);
  const stepsBefore = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC);
  console.log('С8 до правки: confirmedVersion', before.confirmed?.confirmedVersion, 'шаги:', JSON.stringify(stepsBefore.map(t => ({ role: t.role, v: t.stage?.templateVersion }))));

  await editFullCycle(page, 'Дизайн · проверка');
  const tpls = await client.get<any[]>('/api/workflow-templates');
  const full = tpls.find(e => e.template.builtInKey === 'full-cycle');
  console.log('С8 версия шаблона после правки:', full?.template.version, 'title design:', full?.template.stages.find((s: any) => s.role === 'design')?.title);

  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  const upd = page.getByTestId('feature-workflow-confirm-update');
  console.log('С8 предупреждение об обновлении:', await upd.count(), flat(await upd.innerText({ timeout: 3000 }).catch(() => '')).slice(0, 300));
  expect.soft(await upd.count(), 'страница сообщает, что шаблон изменился').toBeGreaterThan(0);
  await page.screenshot({ path: `${SHOTS}/С8-update.png`, fullPage: true });

  const stepsAfter = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC);
  console.log('С8 после правки: confirmedVersion', (await client.get<any>(`/api/tasks/${EPIC}/workflow`)).confirmed?.confirmedVersion, 'шаги:', JSON.stringify(stepsAfter.map(t => ({ role: t.role, v: t.stage?.templateVersion, title: t.stage?.prompt ? 'has prompt' : null }))));
  expect.soft(stepsAfter.map(t => t.stage?.templateVersion), 'созданные шаги остались на своей версии шаблона').toEqual(stepsBefore.map(t => t.stage?.templateVersion));

  // Обновить маршрут до новой версии
  await page.getByTestId('feature-workflow-confirm-update-apply').first().click({ timeout: 15000 }).catch(e => console.log('С8 apply:', String(e).slice(0, 100)));
  await page.waitForTimeout(3000);
  const after = await client.get<any>(`/api/tasks/${EPIC}/workflow`);
  console.log('С8 после «Обновить»: confirmedVersion', after.confirmed?.confirmedVersion, 'stages:', JSON.stringify(after.confirmed?.stages?.map((s: any) => s.id ?? s.role)));
  expect.soft(after.confirmed?.confirmedVersion, 'маршрут обновился до новой версии').toBeGreaterThan(before.confirmed?.confirmedVersion ?? 1);
  const stepsAfter2 = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC);
  console.log('С8 шаги после обновления:', JSON.stringify(stepsAfter2.map(t => ({ role: t.role, v: t.stage?.templateVersion }))));
  await page.screenshot({ path: `${SHOTS}/С8-after-apply.png`, fullPage: true });
  console.log('С8 errors:', JSON.stringify([...new Set(errs)]));
  await page.close();
});

test('С9: проектная копия глобального пресета', async ({ context, playwright, baseURL }) => {
  test.setTimeout(240_000);
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  await page.getByRole('button', { name: /Настройки проекта/ }).first().click({ timeout: 15000 }).catch(e => console.log('С9 настройки проекта:', String(e).slice(0, 100)));
  await page.waitForTimeout(2000);
  await page.getByText('Процессы', { exact: true }).first().click({ timeout: 10000 }).catch(e => console.log('С9 секция Процессы:', String(e).slice(0, 100)));
  await page.waitForTimeout(2000);
  await page.getByRole('button', { name: /Полный цикл/ }).first().click().catch(e => console.log('С9 открыть глобальный:', String(e).slice(0, 100)));
  await page.waitForTimeout(1500);
  const body = await page.locator('body').innerText();
  console.log('С9 виден ли глобальный на чтение:', /правит администратор/i.test(flat(body).slice(-2000)) ? 'да' : 'нет', '| кнопка «Создать проектный»:', await page.getByRole('button', { name: 'Создать проектный' }).count());
  await page.getByRole('button', { name: 'Создать проектный' }).first().click({ timeout: 10000 }).catch(() => undefined);
  await page.waitForTimeout(1500);
  await page.getByRole('button', { name: 'Сохранить', exact: true }).first().click({ timeout: 15000 }).catch(e => console.log('С9 сохранить:', String(e).slice(0, 100)));
  await page.waitForTimeout(2500);
  const tpls = await client.get<any[]>(`/api/workflow-templates?projectId=${PID}`);
  const proj = tpls.filter(e => e.scope === 'project');
  console.log('С9 проектных шаблонов:', JSON.stringify(proj.map(e => ({ id: e.template.id, name: e.template.name, basedOn: e.basedOn?.name, diff: e.diffFromPreset }))));
  expect.soft(proj.length, 'проектный шаблон появился').toBeGreaterThan(0);
  await page.screenshot({ path: `${SHOTS}/С9-project-copy.png`, fullPage: true });
  await page.close();
});
