import { test, expect } from '@playwright/test';
import { login, api, type Api } from './helpers';

// С8v4: решение человека по карточке обновления — «Обновить маршрут до vN»;
// созданные ранее шаги остаются на своей версии шаблона.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';

let client: Api;
let token: string;
let request: import('@playwright/test').APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => { await request?.dispose(); });

test('С8v4: «Обновить маршрут до vN»', async ({ context }) => {
  test.setTimeout(200_000);
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const before = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC).map(t => ({ role: t.role, v: t.stage?.templateVersion }));
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(5000);
  await page.getByTestId('feature-workflow-confirm-update-apply').first().click({ timeout: 20000 }).catch(e => console.log('С8v4 apply:', String(e).slice(0, 120)));
  await page.waitForTimeout(4000);
  const wf = await client.get<any>(`/api/tasks/${EPIC}/workflow`);
  const stages = (wf.confirmed?.stages ?? []).map((s: any) => s.id ?? s.role);
  console.log('С8v4 после «Обновить»: v', wf.confirmed?.confirmedVersion, 'skipped:', JSON.stringify(wf.confirmed?.skipped), 'stages:', JSON.stringify(stages));
  expect.soft(wf.confirmed?.confirmedVersion, 'маршрут обновился до новой версии').toBeGreaterThanOrEqual(3);
  expect.soft(stages.includes('qa'), 'снятый в шаблоне этап не вошёл в маршрут').toBeFalsy();

  const after = (await client.get<any[]>(`/api/projects/${PID}/tasks`)).filter(t => t.featureId === EPIC).map(t => ({ role: t.role, v: t.stage?.templateVersion }));
  console.log('С8v4 шаги до:', JSON.stringify(before), 'после:', JSON.stringify(after));
  expect.soft(after, 'созданные ранее шаги не переписаны').toEqual(before);

  await page.waitForTimeout(2000);
  await page.screenshot({ path: '.cc-attachments/qa-wf-e2e/С8v4-after.png', fullPage: true });
  console.log('С8v4 errors:', JSON.stringify([...new Set(errs)]));
  await page.close();
});
