import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login, api, type Api } from './helpers';

// С8v3: правка шаблона МЕНЯЕТ СОСТАВ маршрута (сняли этап «Проверка») —
// карточка «шаблон изменился», решение человека, готовые шаги не переписываются.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';
const SHOTS = '.cc-attachments/qa-wf-e2e';
const flat = (s: string) => s.replace(/\n/g, ' | ');

let client: Api;
let token: string;
let request: import('@playwright/test').APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  mkdirSync(SHOTS, { recursive: true });
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => { await request?.dispose(); });

test('С8v3: изменение состава маршрута', async ({ context }) => {
  test.setTimeout(280_000);
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const stepsBefore = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC);
  const viewBefore = stepsBefore.map(t => ({ role: t.role, v: t.stage?.templateVersion }));
  console.log('С8v3 шаги до правки:', JSON.stringify(viewBefore));

  // Правка: этап «Проверка» → «Нет» в маршруте
  await page.goto('/#/specialties', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(2500);
  await page.getByText('Процессы фичи', { exact: true }).click();
  await page.waitForTimeout(1500);
  await page.getByRole('button', { name: /Полный цикл/ }).first().click();
  await page.waitForTimeout(1200);
  await page.locator('[data-stage-index]').filter({ hasText: 'qa' }).first().click();
  await page.waitForTimeout(1000);
  await page.getByRole('button', { name: 'Нет', exact: true }).first().click({ timeout: 8000 }).catch(e => console.log('С8v3 presence:', String(e).slice(0, 100)));
  await page.waitForTimeout(800);
  await page.getByLabel('Закрыть настройку этапа').first().click({ timeout: 8000 }).catch(() => page.keyboard.press('Escape'));
  await page.waitForTimeout(500);
  await page.getByRole('button', { name: 'Сохранить', exact: true }).first().click({ timeout: 15000 }).catch(e => console.log('С8v3 сохранить:', String(e).slice(0, 100)));
  await page.waitForTimeout(2500);
  const tpls = await client.get<any[]>('/api/workflow-templates');
  const full = tpls.find(e => e.template.builtInKey === 'full-cycle');
  console.log('С8v3 версия шаблона:', full?.template.version, 'presence qa:', full?.template.stages.find((s: any) => s.role === 'qa')?.presence);

  // Страница фичи: «Сменить» → карточка обновления
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(5000);
  await page.getByTestId('feature-workflow-confirm-change').first().click({ timeout: 15000 }).catch(e => console.log('С8v3 «Сменить»:', String(e).slice(0, 100)));
  await page.waitForTimeout(2500);
  const upd = page.getByTestId('feature-workflow-confirm-update');
  const updText = await upd.innerText({ timeout: 3000 }).catch(() => '');
  console.log('С8v3 карточка:', await upd.count(), flat(updText).slice(0, 400));
  console.log('С8v3 кнопки apply/keep:', await page.getByTestId('feature-workflow-confirm-update-apply').count(), await page.getByTestId('feature-workflow-confirm-update-keep').count());
  await page.screenshot({ path: `${SHOTS}/С8v3-update-card.png`, fullPage: true });
  expect.soft(await upd.count(), 'карточка «шаблон изменился» показана').toBeGreaterThan(0);

  await page.getByTestId('feature-workflow-confirm-update-apply').first().click({ timeout: 15000 }).catch(e => console.log('С8v3 apply:', String(e).slice(0, 100)));
  await page.waitForTimeout(4000);
  const wf = await client.get<any>(`/api/tasks/${EPIC}/workflow`);
  const stages = (wf.confirmed?.stages ?? []).map((s: any) => s.id ?? s.role);
  console.log('С8v3 после «Обновить»: v', wf.confirmed?.confirmedVersion, 'stages:', JSON.stringify(stages));
  expect.soft(wf.confirmed?.confirmedVersion, 'маршрут обновился').toBeGreaterThan(1);
  expect.soft(stages.includes('qa'), 'снятый этап не вошёл в обновлённый маршрут').toBeFalsy();

  const stepsAfter = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC);
  const view = stepsAfter.map(t => ({ role: t.role, v: t.stage?.templateVersion }));
  console.log('С8v3 шаги после обновления:', JSON.stringify(view));
  expect.soft(view.every(s => s.v === 1), 'созданные ранее шаги остались на v1').toBeTruthy();

  await page.screenshot({ path: `${SHOTS}/С8v3-after.png`, fullPage: true });
  console.log('С8v3 errors:', JSON.stringify([...new Set(errs)]));
  await page.close();
});
