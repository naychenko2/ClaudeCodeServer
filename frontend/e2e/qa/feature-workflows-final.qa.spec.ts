import { test, expect, type BrowserContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login, api } from './helpers';

// Приёмка «Процессы фичи» целиком (задача f55676aa). Сид сделан скриптом до прогона (стенд из worktree).
const SHOTS = '.cc-attachments/qa-wf-final';
const PID = '32527f34-1907-4202-8c5c-9587b89d6366';
const F = { A: '351d5c4e-c51f-4dce-b64e-2bc6be2e982d', B: '8ffcf962-2700-4e53-b3fe-1bfec0452b64',
  D: 'fa5b0598-cbea-4d2f-94ef-8ecbacadabff', E: '29c70b75-0df5-4ae5-b426-d2f52eab2e3c',
  G: '77703c53-b830-4b07-b3b7-951466b8ba0e', H: '59d29226-2434-4a8d-b1e6-fe17b89c8968',
  I: 'd340ca9b-e5ee-4518-b92d-5ac56c50e82a' };
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
  await expect(page.getByTestId('feature-page')).toBeVisible({ timeout: 30000 });
  await page.waitForTimeout(1500);
  return page;
}

const tabLabels = (page: Page) => page.getByTestId('feature-page-tabs').getByRole('tab').allInnerTexts();
async function overflow(page: Page) {
  return page.evaluate(() => ({ sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth }));
}

test('С1: вкладки фичи с маршрутом и легаси, выбранный этап не теряется', async ({ context }) => {
  const page = await open(context, url(F.A));
  const tabs = await tabLabels(page);
  console.log('A tabs:', JSON.stringify(tabs));
  expect.soft(tabs.map(t => t.replace(/\d+/g, '').trim())).toEqual(['Процесс', 'Материалы', 'Разбор']);
  await expect.soft(page.getByTestId('feature-process-graph'), 'граф на вкладке Процесс по умолчанию').toBeVisible();
  const sel = await page.getByRole('tab', { name: /Процесс/ }).getAttribute('aria-selected');
  expect.soft(sel).toBe('true');
  await page.getByTestId('pgraph-node-step-back').click();
  await page.waitForTimeout(800);
  await expect.soft(page.getByTestId('feature-stage-area-title')).toContainText('бэк');
  console.log('area title:', await page.getByTestId('feature-stage-area-title').innerText());
  await page.screenshot({ path: `${SHOTS}/c1-process-light-1280.png`, fullPage: true });

  await page.getByRole('tab', { name: /Материалы/ }).click();
  await page.waitForTimeout(800);
  await expect.soft(page.getByTestId('feature-process-graph'), 'на Материалах графа нет').toHaveCount(0);
  await page.screenshot({ path: `${SHOTS}/c1-materials.png`, fullPage: true });
  await page.getByRole('tab', { name: /Разбор/ }).click();
  await page.waitForTimeout(800);
  await expect.soft(page.getByTestId('feature-process-graph'), 'на Разборе графа нет').toHaveCount(0);
  await page.screenshot({ path: `${SHOTS}/c1-analysis.png`, fullPage: true });
  await page.getByRole('tab', { name: /Процесс/ }).click();
  await page.waitForTimeout(800);
  const pressed = await page.getByTestId('pgraph-node-step-back').getAttribute('aria-pressed');
  console.log('step-back pressed after return:', pressed);
  expect.soft(pressed, 'выбранный этап сохранился после возврата').toBe('true');
  await page.close();

  const leg = await open(context, url(F.D));
  const lt = await tabLabels(leg);
  console.log('legacy tabs:', JSON.stringify(lt));
  expect.soft(lt.map(t => t.replace(/\d+/g, '').trim())).toEqual(['Ход', 'Материалы', 'Разбор']);
  await expect.soft(leg.getByTestId('feature-process-graph')).toHaveCount(0);
  await expect.soft(leg.getByTestId('feature-workflow-confirm')).toHaveCount(0);
  await leg.screenshot({ path: `${SHOTS}/c1-legacy.png`, fullPage: true });
  await leg.close();
});

for (const [theme, w] of [['light', 1280], ['dark', 1280], ['light', 360], ['dark', 360]] as const) {
  test(`С2: граф параллельных веток — ${theme} ${w}`, async ({ context }) => {
    const page = await open(context, url(F.A), theme, w);
    await expect(page.getByTestId('feature-process-graph')).toBeVisible();
    const nodes = page.locator('[data-testid^="pgraph-node-"]');
    const labels = await nodes.evaluateAll(els => els.map(e => `${e.getAttribute('data-testid')}=${e.getAttribute('aria-label')}`));
    console.log(`A nodes ${theme} ${w}:`, JSON.stringify(labels));
    expect.soft(await nodes.count()).toBeGreaterThanOrEqual(8);
    if (w > 500) {
      const b = await page.getByTestId('pgraph-node-step-back').boundingBox();
      const f = await page.getByTestId('pgraph-node-step-front').boundingBox();
      const qp = await page.getByTestId('pgraph-node-qa-plan').boundingBox();
      const pl = await page.getByTestId('pgraph-node-plan').boundingBox();
      console.log('boxes', JSON.stringify({ b, f, qp, pl }));
      expect.soft(Math.round(b!.x), 'бэк и фронт — одна колонка').toBe(Math.round(f!.x));
      expect.soft(Math.round(qp!.x), 'план и проверка плана — одна колонка').toBe(Math.round(pl!.x));
      expect.soft(Math.abs(qp!.y - pl!.y)).toBeGreaterThan(10);
    } else {
      const o = await overflow(page);
      console.log('overflow 360', JSON.stringify(o));
      expect.soft(o.sw).toBeLessThanOrEqual(o.cw + 1);
    }
    await page.screenshot({ path: `${SHOTS}/c2-graph-${theme}-${w}.png`, fullPage: true });
    // длинное имя — фича G
    const g = await open(context, url(F.G), theme, w);
    await expect(g.getByTestId('feature-process-graph')).toBeVisible();
    const long = g.getByTestId('pgraph-node-sback');
    const m = await long.evaluate(el => { const r = el.getBoundingClientRect(); return { w: r.width, h: r.height, sw: el.scrollWidth, sh: el.scrollHeight, text: (el as HTMLElement).innerText }; });
    console.log(`G long ${theme} ${w}`, JSON.stringify(m));
    expect.soft(m.sw, 'длинное имя не вылезает по ширине').toBeLessThanOrEqual(m.w + 1);
    expect.soft(m.sh, 'длинное имя не вылезает по высоте').toBeLessThanOrEqual(m.h + 1);
    const o2 = await overflow(g);
    expect.soft(o2.sw, 'страница G без горизонтального переполнения').toBeLessThanOrEqual(o2.cw + 1);
    await g.screenshot({ path: `${SHOTS}/c2-long-${theme}-${w}.png`, fullPage: true });
    await g.close(); await page.close();
  });
}

test('С3: экран этапа — вкладки, сводка, «Ждёт вас»', async ({ context }) => {
  const page = await open(context, url(F.A));
  await page.getByTestId('pgraph-node-step-back').click();
  await page.waitForTimeout(1500);
  const screen = page.getByTestId('stage-screen');
  console.log('stage screen present:', await screen.count());
  const tabs = await screen.getByRole('tab').allInnerTexts();
  console.log('stage tabs:', JSON.stringify(tabs));
  await page.screenshot({ path: `${SHOTS}/c3-stage-step-back.png`, fullPage: true });
  const summary = page.getByTestId('stage-screen-summary');
  console.log('summary:', (await summary.innerText()).replace(/\n/g, ' | '));
  for (const name of ['Артефакты', 'Вход', 'Ревью', 'Метрики']) {
    const t = screen.getByRole('tab', { name: new RegExp(name) });
    if (await t.count() === 0) { console.log('нет вкладки', name); continue; }
    await t.click(); await page.waitForTimeout(400);
    const visible = await page.locator('[data-testid^="stage-"][data-testid$="s"], [data-testid=stage-artifacts], [data-testid=stage-input], [data-testid=stage-review], [data-testid=stage-metrics]').evaluateAll(els => els.filter(e => (e as HTMLElement).offsetParent !== null).map(e => e.getAttribute('data-testid')));
    console.log(`вкладка ${name}: видимые блоки`, JSON.stringify(visible), 'сводка видна:', await summary.isVisible());
    expect.soft(await summary.isVisible(), `сводка видна на «${name}»`).toBeTruthy();
    await page.screenshot({ path: `${SHOTS}/c3-tab-${name}.png`, fullPage: true });
  }
  // ждёт вас: тикает ли
  const aw = page.getByTestId('stage-awaiting-human');
  console.log('awaiting count', await aw.count());
  if (await aw.count()) {
    const a1 = await aw.first().innerText(); await page.waitForTimeout(65000); const a2 = await aw.first().innerText();
    console.log('awaiting t1:', a1.replace(/\n/g, ' '), ' t2:', a2.replace(/\n/g, ' '));
    expect.soft(a2, '«Ждёт вас» тикает').not.toBe(a1);
  }
  // смена задачи этапа — первая вкладка; этап без метрик
  await page.getByTestId('pgraph-node-cut').click(); await page.waitForTimeout(1200);
  const cutTabs = await page.getByTestId('stage-screen').getByRole('tab').allInnerTexts();
  console.log('cut tabs:', JSON.stringify(cutTabs));
  expect.soft(cutTabs.join('|').includes('Метрики'), 'у этапа без метрик вкладки нет').toBeFalsy();
  await page.screenshot({ path: `${SHOTS}/c3-stage-cut.png`, fullPage: true });
  await page.close();
});

test('С4: блок подтверждения маршрута — состояния а/б/в/г', async ({ context }) => {
  // б: нет предложения (B)
  let page = await open(context, url(F.B));
  let block = page.getByTestId('feature-workflow-confirm');
  await expect.soft(block).toBeVisible();
  console.log('B block:', (await block.innerText()).replace(/\n/g, ' | '));
  const sel = block.locator('select').first();
  console.log('B select value:', JSON.stringify(await sel.inputValue()), 'options:', JSON.stringify(await sel.locator('option').allInnerTexts()));
  console.log('B submit disabled:', await page.getByTestId('feature-workflow-confirm-submit').isDisabled());
  await page.screenshot({ path: `${SHOTS}/c4b-no-proposal.png`, fullPage: true });
  await page.getByTestId('feature-workflow-confirm-recommend').click().catch(e => console.log('recommend click', String(e).slice(0, 100)));
  await page.waitForTimeout(2500);
  console.log('B after recommend:', (await block.innerText()).replace(/\n/g, ' | '));
  await page.close();
  // а: предложение (E)
  page = await open(context, url(F.E));
  block = page.getByTestId('feature-workflow-confirm');
  console.log('E block:', (await block.innerText()).replace(/\n/g, ' | '));
  console.log('E checkboxes:', await block.getByRole('checkbox').count());
  await page.screenshot({ path: `${SHOTS}/c4a-proposal-light-1280.png`, fullPage: true });
  await page.close();
  // в/г: подтверждённые
  for (const [k, id] of [['H-auto', F.H], ['A-human', F.A], ['G-changed', F.G], ['I-changed', F.I]] as const) {
    const p = await open(context, url(id));
    const line = p.getByTestId('feature-workflow-confirm-line');
    console.log(`${k} line:`, await line.count() ? (await line.innerText()).replace(/\n/g, ' | ') : 'НЕТ СТРОКИ', ' confirm block:', await p.getByTestId('feature-workflow-confirm').count());
    await p.screenshot({ path: `${SHOTS}/c4-${k}.png`, fullPage: true });
    await p.close();
  }
  // темная и 360
  for (const [th, w] of [['dark', 1280], ['light', 360], ['dark', 360]] as const) {
    const p = await open(context, url(F.E), th, w);
    const o = await overflow(p);
    console.log(`E ${th} ${w} overflow`, JSON.stringify(o));
    expect.soft(o.sw).toBeLessThanOrEqual(o.cw + 1);
    const small = await p.evaluate(() => [...document.querySelectorAll('[data-testid=feature-workflow-confirm] button, [data-testid=feature-workflow-confirm] select, [data-testid=feature-workflow-confirm] input')].map(e => { const r = e.getBoundingClientRect(); return `${e.tagName}:${Math.round(r.width)}x${Math.round(r.height)}`; }));
    console.log(`E ${th} ${w} controls`, JSON.stringify(small));
    await p.screenshot({ path: `${SHOTS}/c4a-proposal-${th}-${w}.png`, fullPage: true });
    await p.close();
  }
});

test('С5: гейт запуска — отказ без подтверждения, запуск после', async ({ playwright, baseURL }) => {
  const req = await playwright.request.newContext({ baseURL });
  const c = api(req, token);
  const mk = async (title: string, extra: object) => c.post<{ id: string }>(`/api/projects/${PID}/tasks`, { title, ...extra });
  const step = await mk('qa-wf gate step', { featureId: F.B, role: 'step', assignee: 'claude' }).catch(e => { console.log('create gate step failed', String(e).slice(0, 200)); return null; });
  console.log('gate step', step?.id);
  if (step) {
    const r = await req.post(`/api/tasks/${step.id}/execute`, { headers: { Authorization: `Bearer ${token}` } });
    const txt = await r.text();
    console.log('execute (без маршрута):', r.status(), txt.slice(0, 300));
    expect.soft(txt).toContain('маршрут фичи не подтверждён');
    // подтверждаем и повторяем
    const cf = await req.post(`/api/tasks/${F.B}/workflow/confirm`, { headers: { Authorization: `Bearer ${token}` }, data: { templateId: 'builtin-fast-track', scope: 'global', skipped: [] } });
    console.log('confirm B:', cf.status());
    const r2 = await req.post(`/api/tasks/${step.id}/execute`, { headers: { Authorization: `Bearer ${token}` } });
    console.log('execute (после):', r2.status(), (await r2.text()).slice(0, 300));
  }
  await req.dispose();
});

test('С9: чип возврата с вкладки «Процесс» и у легаси', async ({ context }) => {
  for (const [id, expected] of [[F.A, 'Процесс'], [F.D, 'Ход']] as const) {
    const page = await open(context, url(id));
    // уходим в чат: ищем кнопку чата/задачи в странице
    const chatBtns = await page.getByRole('button').allInnerTexts();
    console.log(`${expected}: кнопки страницы`, JSON.stringify(chatBtns.filter(t => /чат|открыть|задач/i.test(t)).slice(0, 8)));
    await page.screenshot({ path: `${SHOTS}/c9-${expected}.png`, fullPage: true });
    await page.close();
  }
});

test('С7/С8: редактор шаблонов, форма персоны — разведка текстов', async ({ context }) => {
  const page = await open(context, url(F.A));
  await page.getByRole('button', { name: 'Настройки проекта' }).first().click().catch(() => {});
  await page.waitForTimeout(1500);
  await page.getByText('Шаблоны процесса', { exact: true }).click().catch(e => console.log('нет пункта шаблонов', String(e).slice(0, 100)));
  await page.waitForTimeout(2000);
  await page.screenshot({ path: `${SHOTS}/c7-templates-list.png`, fullPage: true });
  console.log('TEMPLATES BODY:', (await page.locator('body').innerText()).slice(0, 1500).replace(/\n/g, ' | '));
  await page.close();
});

test.afterAll(() => { console.log('CONSOLE ERRORS:', JSON.stringify([...new Set(errors)].slice(0, 12))); });
