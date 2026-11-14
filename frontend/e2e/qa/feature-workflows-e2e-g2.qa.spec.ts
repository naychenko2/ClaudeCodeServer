import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login, api, type Api } from './helpers';

// С8v2: «шаблон изменился» на идущей фиче — карточка reachable через «Сменить»,
// решение человека («Обновить» / «Оставить»), готовые шаги не переписываются.

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

test('С8v2: карточка обновления маршрута', async ({ context }) => {
  test.setTimeout(220_000);
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(5000);

  for (const t of ['feature-workflow-confirm-compact', 'feature-workflow-confirm-confirmed', 'feature-workflow-confirm-update']) {
    console.log('С8v2', t, await page.getByTestId(t).count());
  }
  const stepsBefore = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC);
  console.log('С8v2 шаги до решения:', JSON.stringify(stepsBefore.map(t => ({ role: t.role, v: t.stage?.templateVersion }))));

  await page.getByTestId('feature-workflow-confirm-change').first().click({ timeout: 15000 }).catch(e => console.log('С8v2 «Сменить»:', String(e).slice(0, 100)));
  await page.waitForTimeout(2000);
  const upd = page.getByTestId('feature-workflow-confirm-update');
  console.log('С8v2 карточка после «Сменить»:', await upd.count(), flat(await upd.innerText({ timeout: 3000 }).catch(() => '')).slice(0, 400));
  console.log('С8v2 кнопки:', await page.getByTestId('feature-workflow-confirm-update-apply').count(), await page.getByTestId('feature-workflow-confirm-update-keep').count());
  await page.screenshot({ path: `${SHOTS}/С8v2-update-card.png`, fullPage: true });
  expect.soft(await upd.count(), 'карточка «шаблон изменился» reachable').toBeGreaterThan(0);

  // «Оставить v1» — предупреждение снимается, маршрут не меняется
  await page.getByTestId('feature-workflow-confirm-update-keep').first().click({ timeout: 15000 }).catch(e => console.log('С8v2 keep:', String(e).slice(0, 100)));
  await page.waitForTimeout(3000);
  const wf1 = await client.get<any>(`/api/tasks/${EPIC}/workflow`);
  console.log('С8v2 после «Оставить»: v', wf1.confirmed?.confirmedVersion, 'dismissed', wf1.confirmed?.dismissedUpdateVersion, 'updateAvailable', wf1.updateAvailable);
  expect.soft(wf1.confirmed?.confirmedVersion, 'маршрут остался на своей версии').toBe(1);
  expect.soft(wf1.updateAvailable, 'предупреждение снято до следующей версии').toBeFalsy();

  const stepsAfter = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC);
  const view = stepsAfter.map(t => ({ role: t.role, v: t.stage?.templateVersion }));
  console.log('С8v2 шаги после «Оставить»:', JSON.stringify(view));
  expect.soft(view, 'созданные шаги не переписаны').toEqual(stepsBefore.map(t => ({ role: t.role, v: t.stage?.templateVersion })));
  console.log('С8v2 errors:', JSON.stringify([...new Set(errs)]));
  await page.close();
});
