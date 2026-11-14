import { test, type BrowserContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login } from './helpers';

// Приёмка «Процессы фичи», второй спек: добор по итогам первого прогона (задача f55676aa).
const SHOTS = '.cc-attachments/qa-wf-final';
const PID = '32527f34-1907-4202-8c5c-9587b89d6366';
const F = { A: '351d5c4e-c51f-4dce-b64e-2bc6be2e982d', D: 'fa5b0598-cbea-4d2f-94ef-8ecbacadabff',
  G: '77703c53-b830-4b07-b3b7-951466b8ba0e', I: 'd340ca9b-e5ee-4518-b92d-5ac56c50e82a', J: 'dce0a8b7-7c8c-43bf-90b0-534dface4ec0' };
const url = (f: string) => `/#/project/${PID}/feature/${f}`;
let token: string;
const errors: string[] = [];

test.beforeAll(async ({ playwright, baseURL }) => {
  const req = await playwright.request.newContext({ baseURL });
  token = await login(req);
  await req.dispose();
  mkdirSync(SHOTS, { recursive: true });
});

async function open(context: BrowserContext, path: string, theme: 'light' | 'dark' = 'light', w = 1280): Promise<Page> {
  const page = await context.newPage();
  await page.setViewportSize({ width: w, height: w < 500 ? 800 : 900 });
  page.on('console', m => { if (m.type() === 'error') errors.push(`[error] ${path} ${m.text()}`.slice(0, 300)); });
  page.on('pageerror', e => errors.push(`[pageerror] ${path} ${e}`.slice(0, 300)));
  await context.addInitScript(([tk, th]) => { localStorage.setItem('cc_token', tk as string); localStorage.setItem('theme-mode', th as string); }, [token, theme]);
  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(2500);
  return page;
}

test('4г: «шаблон изменился» после правки состава шаблона', async ({ context }) => {
  for (const id of [F.G, F.I]) {
    const p = await open(context, url(id));
    const blk = p.getByTestId('feature-workflow-confirm');
    console.log(`${id.slice(0, 4)} block count`, await blk.count(), (await blk.count()) ? (await blk.innerText()).replace(/\n/g, ' | ') : '');
    await p.screenshot({ path: `${SHOTS}/c4g-changed-${id.slice(0, 4)}.png`, fullPage: true });
    if (id === F.I && await blk.count()) {
      // обновить маршрут: подтвердить заново
      const sub = p.getByTestId('feature-workflow-confirm-submit');
      console.log('I submit text', await sub.innerText());
      await sub.click(); await p.waitForTimeout(2500);
      console.log('I after resubmit line', await p.getByTestId('feature-workflow-confirm-line').count() ? (await p.getByTestId('feature-workflow-confirm-line').innerText()) : 'нет строки', 'block', await blk.count());
      await p.screenshot({ path: `${SHOTS}/c4g-after-resubmit.png`, fullPage: true });
    }
    await p.close();
  }
  // слепок шага не переписан
  const req = await (await import('@playwright/test')).request.newContext({ baseURL: 'http://127.0.0.1:5500' });
  const tasks = await (await req.get(`/api/projects/${PID}/tasks`, { headers: { Authorization: `Bearer ${token}` } })).json() as Array<{ title: string; stage?: { templateVersion: number }; workflow?: { stages: Array<{ id: string }>; confirmedVersion: number } }>;
  const step = tasks.find(t => t.title === 'qa-wf G step back');
  const g = tasks.find(t => t.title.startsWith('qa-wf G'));
  console.log('СЛЕПОК шага G: версия', step?.stage?.templateVersion, '| маршрут G:', g?.workflow?.stages.map(s => s.id).join(','), 'ver', g?.workflow?.confirmedVersion);
  await req.dispose();
});

test('J: «Подобрать ещё раз» при пустом ответе модели', async ({ context }) => {
  const p = await open(context, url(F.J));
  const blk = p.getByTestId('feature-workflow-confirm');
  console.log('J до:', (await blk.innerText()).replace(/\n/g, ' | '));
  const calls: string[] = [];
  p.on('response', async r => { if (r.url().includes('/workflow/recommend')) calls.push(`${r.status()} ${(await r.text()).slice(0, 200)}`); });
  await p.getByTestId('feature-workflow-confirm-recommend').click();
  await p.waitForTimeout(5000);
  console.log('J recommend ответы:', JSON.stringify(calls));
  console.log('J после:', (await blk.innerText()).replace(/\n/g, ' | '));
  await p.screenshot({ path: `${SHOTS}/cJ-after-recommend.png`, fullPage: true });
  await p.close();
});

test('С3: «Ждёт вас» тикает; этап без задач — вкладки', async ({ context }) => {
  const p = await open(context, url(F.A));
  await p.getByTestId('pgraph-node-step-back').click(); await p.waitForTimeout(1500);
  const sum = p.getByTestId('stage-screen-summary');
  const t1 = (await sum.innerText()).replace(/\n/g, ' | ');
  const kp1 = (await p.getByTestId('stage-screen-kpis').innerText()).replace(/\n/g, ' | ');
  await p.getByRole('tab', { name: /Метрики/ }).click();
  const m1 = (await p.getByTestId('stage-metrics').innerText()).replace(/\n/g, ' | ');
  await p.waitForTimeout(62000);
  const t2 = (await sum.innerText()).replace(/\n/g, ' | ');
  const m2 = (await p.getByTestId('stage-metrics').innerText()).replace(/\n/g, ' | ');
  console.log('ЖДЁТ t1:', t1, '\nKPIS:', kp1, '\nМЕТР t1:', m1, '\nЖДЁТ t2:', t2, '\nМЕТР t2:', m2);
  // этап без задач
  await p.getByTestId('pgraph-node-review').click(); await p.waitForTimeout(1200);
  const area = p.getByTestId('feature-stage-area');
  console.log('review area text:', (await area.innerText()).replace(/\n/g, ' | ').slice(0, 400), 'stage-screen:', await p.getByTestId('stage-screen').count());
  await p.screenshot({ path: `${SHOTS}/c3-stage-empty.png`, fullPage: true });
  // смена задачи этапа -> первая вкладка: у step-back одна задача; проверим picker у step (две)
  await p.close();
});

test('С9: возврат из чата на «Процесс» и у легаси «Ход»', async ({ context }) => {
  for (const [id, expected] of [[F.A, 'Процесс'], [F.D, 'Ход']] as const) {
    const p = await open(context, url(id));
    const chat = p.getByTestId('feature-page-chat').or(p.getByRole('button', { name: /^Чат$/ })).first();
    console.log(expected, 'кнопки чата:', await chat.count());
    const ids = await p.locator('[data-testid]').evaluateAll(els => [...new Set(els.map(e => e.getAttribute('data-testid')!))].filter(x => /chat|open|session|lead/i.test(x)));
    console.log(expected, 'testid про чат:', JSON.stringify(ids));
    await p.close();
  }
});

test('С7: редактор шаблонов', async ({ context }) => {
  const p = await open(context, url(F.A));
  const gear = p.getByRole('button', { name: /Настройки проекта/ });
  console.log('gear count', await gear.count());
  await gear.first().click({ timeout: 5000 }).catch(e => console.log('gear click fail', String(e).slice(0, 120)));
  await p.waitForTimeout(1500);
  const names = await p.getByRole('button').allInnerTexts();
  console.log('кнопки после настроек:', JSON.stringify(names.filter(Boolean).slice(0, 40)));
  await p.screenshot({ path: `${SHOTS}/c7-settings.png`, fullPage: true });
  await p.close();
});
test.afterAll(() => { console.log('CONSOLE ERRORS:', JSON.stringify([...new Set(errors)].slice(0, 12))); });
