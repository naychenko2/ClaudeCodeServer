import { test, expect, type APIRequestContext, type BrowserContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { api, login, seed, type Api } from './helpers';

// QA-2 (задача fc2ba512): перепроверка дефекта 8f521cac — «Подобрать ещё раз».
// Сценарий: фича в режиме маршрута, модель не отвечает.
//   A) реальный POST /api/tasks/{id}/workflow/recommend → 200 {"proposal":null}:
//      блок остаётся «Предложения пока нет…», Select без предвыбора, «Подтвердить маршрут»
//      неактивна, кнопка «Подобрать ещё раз». Никаких «Предложение модели» и
//      «шаблон не найден в каталоге».
//   B) подмена ответа ручки (page.route) на {"proposal":{…}}: блок показывает этот шаблон
//      и обоснование, Select выбран, «Подтвердить маршрут» активна; подтверждение даёт
//      строку подтверждённого маршрута.
// Ширины: 1280 и 360. Модель на стенде недоступна → реальный подбор даёт proposal=null.

const SHOTS = '.cc-attachments/qa-wf-qa2';
const PREFIX = 'qa-wfqa2-';

let request: APIRequestContext;
let token: string;
let client: Api;
let seeded: ReturnType<typeof seed>;
let projectId: string;
let templateId: string;
let templateName: string;
const createdTasks: string[] = [];
const errors: string[] = [];

async function makeFeature(title: string): Promise<string> {
  // Проектные задачи — POST /api/projects/{projectId}/tasks (api/tasks — личные, без проекта).
  const f = await client.post<{ id: string }>(`/api/projects/${projectId}/tasks`, { title, role: 'feature' });
  createdTasks.push(f.id);
  return f.id;
}

async function open(context: BrowserContext, featureId: string, w: number, theme: 'light' | 'dark' = 'light'): Promise<Page> {
  const page = await context.newPage();
  await page.setViewportSize({ width: w, height: w < 500 ? 800 : 900 });
  page.on('console', m => { if (m.type() === 'error') errors.push(`[error] ${m.text()}`.slice(0, 200)); });
  page.on('pageerror', e => errors.push(`[pageerror] ${e}`.slice(0, 200)));
  await context.addInitScript(([tk, th]) => {
    localStorage.setItem('cc_token', tk as string);
    localStorage.setItem('theme-mode', th as string);
  }, [token, theme] as const);
  await page.goto(`/#/project/${projectId}/feature/${featureId}`, { waitUntil: 'domcontentloaded' });
  await page.getByTestId('feature-workflow-confirm').first().waitFor({ timeout: 20000 });
  await page.waitForTimeout(1500);
  return page;
}

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  seeded = seed(client);
  mkdirSync(SHOTS, { recursive: true });

  const stamp = Date.now();
  const project = await seeded.project(`${PREFIX}p-${stamp}`, `/tmp/${PREFIX}${stamp}`);
  projectId = project.id;

  const tpls = await client.get<Array<{ scope: string; template: { id: string; name: string } }>>(
    `/api/workflow-templates?projectId=${projectId}`);
  const tpl = tpls.find(t => t.scope === 'global');
  if (!tpl) throw new Error('в каталоге нет глобального шаблона');
  templateId = tpl.template.id;
  templateName = tpl.template.name;
});

test.afterAll(async () => {
  for (const id of createdTasks) await client.del(`/api/tasks/${id}`).catch(() => {});
  await seeded?.cleanup();
  await request?.dispose();
});

async function checkNoProposal(page: Page, shot: string): Promise<void> {
  const blk = page.getByTestId('feature-workflow-confirm').first();
  const bodies: string[] = [];
  const listener = async (r: import('@playwright/test').Response) => {
    if (r.url().includes('/workflow/recommend')) bodies.push(`${r.status()} ${await r.text()}`);
  };
  page.on('response', listener);
  await page.getByTestId('feature-workflow-confirm-recommend').click();
  await page.waitForTimeout(6000);
  page.off('response', listener);

  const text = (await blk.innerText()).replace(/\n/g, ' | ');
  const select = blk.locator('select').first();
  const submit = page.getByTestId('feature-workflow-confirm-submit');
  const disabled = await submit.isDisabled();
  const value = await select.inputValue();
  const recommendLabel = (await page.getByTestId('feature-workflow-confirm-recommend').innerText()).trim();
  console.log(`[${shot}] ответ ручки: ${JSON.stringify(bodies)}`);
  console.log(`[${shot}] блок: ${text}`);
  console.log(`[${shot}] select value=${JSON.stringify(value)} submit disabled=${disabled} кнопка=${JSON.stringify(recommendLabel)}`);
  await page.screenshot({ path: `${SHOTS}/${shot}.png`, fullPage: true });

  expect(bodies.length, 'ручка подбора не была вызвана').toBeGreaterThan(0);
  expect(bodies[0].startsWith('200'), `ручка подбора ответила не 200: ${bodies[0]}`).toBeTruthy();
  expect(bodies[0].includes('"proposal":null'), `ответ ручки не {"proposal":null}: ${bodies[0]}`).toBeTruthy();
  expect(text, 'блок должен остаться в состоянии «предложения нет»').toContain('Предложения пока нет');
  expect(text, 'ложного «Предложение модели» быть не должно').not.toContain('Предложение модели');
  expect(text, 'ложного «не найден в каталоге» быть не должно').not.toContain('не найден в каталоге');
  expect(value, 'Select без предвыбора').toBe('');
  expect(disabled, '«Подтвердить маршрут» неактивна').toBeTruthy();
  expect(recommendLabel, 'кнопка подбора').toBe('Подобрать ещё раз');
}

async function checkProposal(page: Page, shot: string, confirm: boolean): Promise<void> {
  const blk = page.getByTestId('feature-workflow-confirm').first();
  await page.route('**/api/tasks/*/workflow/recommend', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ proposal: { templateId, scope: 'global', skipped: [], rationale: 'QA-мотив подбора' } }),
  }));
  await page.getByTestId('feature-workflow-confirm-recommend').click();
  await page.waitForTimeout(2500);

  const text = (await blk.innerText()).replace(/\n/g, ' | ');
  const value = await blk.locator('select').first().inputValue();
  const disabled = await page.getByTestId('feature-workflow-confirm-submit').isDisabled();
  console.log(`[${shot}] блок: ${text}`);
  console.log(`[${shot}] select value=${JSON.stringify(value)} submit disabled=${disabled}`);
  await page.screenshot({ path: `${SHOTS}/${shot}.png`, fullPage: true });

  expect(text, 'обоснование подбора видно').toContain('QA-мотив подбора');
  expect(text, 'блок называет предложение модели').toContain('Предложение модели');
  expect(value, 'Select выбран на предложенном шаблоне').toBe(`global:${templateId}`);
  expect(disabled, '«Подтвердить маршрут» активна').toBeFalsy();

  if (confirm) {
    const confirms: string[] = [];
    const listener = async (r: import('@playwright/test').Response) => {
      if (r.url().includes('/workflow/confirm')) confirms.push(`${r.status()} ${await r.text()}`);
    };
    page.on('response', listener);
    await page.getByTestId('feature-workflow-confirm-submit').click();
    await page.waitForTimeout(6000);
    page.off('response', listener);
    console.log(`[${shot}] ответ confirm: ${JSON.stringify(confirms)}`);
    console.log(`[${shot}] блок после confirm: ${(await blk.innerText()).replace(/\n/g, ' | ')}`);
    const line = page.getByTestId('feature-workflow-confirm-line');
    const lineCount = await line.count();
    const lineText = lineCount ? (await line.first().innerText()).replace(/\n/g, ' | ') : '';
    console.log(`[${shot}] строка подтверждения: ${JSON.stringify(lineText)}`);
    await page.screenshot({ path: `${SHOTS}/${shot}-confirmed.png`, fullPage: true });
    expect(lineCount, 'строка подтверждённого маршрута не появилась').toBeGreaterThan(0);
    expect(lineText, 'строка называет шаблон').toContain(templateName);
  }
}

test('A: реальный подбор без ответа модели — блок остаётся «предложения нет» (1280)', async ({ context }) => {
  const id = await makeFeature(`${PREFIX}feature-null-1280`);
  const page = await open(context, id, 1280);
  await checkNoProposal(page, 'a-null-1280');
  await page.close();
});

test('B: подбор с предложением — шаблон и обоснование, подтверждение даёт строку (1280)', async ({ context }) => {
  const id = await makeFeature(`${PREFIX}feature-proposal-1280`);
  const page = await open(context, id, 1280);
  await checkProposal(page, 'b-proposal-1280', true);
  await page.close();
});

test('C: мобила 360 — оба состояния блока', async ({ context }) => {
  const idNull = await makeFeature(`${PREFIX}feature-null-360`);
  const p1 = await open(context, idNull, 360);
  await checkNoProposal(p1, 'c-null-360');
  await p1.close();

  const idProp = await makeFeature(`${PREFIX}feature-proposal-360`);
  const p2 = await open(context, idProp, 360);
  await checkProposal(p2, 'd-proposal-360', false);
  await p2.close();
});

test('E: ошибок консоли нет', () => {
  expect([...new Set(errors)], 'в консоли не должно быть ошибок').toEqual([]);
});
