import { test, expect, type BrowserContext, type Page } from '@playwright/test';
import { mkdirSync, readFileSync } from 'node:fs';
import { login } from './helpers';

// Приёмка фичи «Процессы фичи»: страница фичи, граф процесса, блок подтверждения
// маршрута, экран этапа, дорожки этапов. Данные — сид-мир на стенде (см. ids.json),
// он остаётся в data/ стенда и не требует уборки: стенд одноразовый.

const SHOTS = '.cc-attachments/qa-wf-accept';
const IDS = JSON.parse(readFileSync('/home/an/Sources/ClaudeCodeServer-feature-workflows/.cc-attachments/qa-wf-accept/ids.json', 'utf8')) as {
  project: string; epic: string; epicProposed: string; persona: string;
  epicNoRoute: string; personaNoSpecialty: string;
};
const url = (f: string) => `/#/project/${IDS.project}/feature/${f}`;

let token: string;
const errors: string[] = [];

test.beforeAll(async ({ playwright, baseURL }) => {
  const req = await playwright.request.newContext({ baseURL });
  token = await login(req);
  await req.dispose();
  mkdirSync(SHOTS, { recursive: true });
});

async function open(context: BrowserContext, path: string, w = 1280): Promise<Page> {
  const page = await context.newPage();
  await page.setViewportSize({ width: w, height: w < 500 ? 800 : 900 });
  page.on('console', m => { if (m.type() === 'error') errors.push(`[error] ${path} ${m.text()}`.slice(0, 300)); });
  page.on('pageerror', e => errors.push(`[pageerror] ${path} ${e}`.slice(0, 300)));
  await context.addInitScript((tk: string) => { localStorage.setItem('cc_token', tk); }, token);
  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await page.getByTestId('feature-page').waitFor({ state: 'visible', timeout: 30000 });
  await page.waitForTimeout(2000);
  return page;
}

const overflow = (page: Page) => page.evaluate(() => ({
  sw: document.documentElement.scrollWidth, cw: document.documentElement.clientWidth,
}));

test('Ф1: страница фичи — шапка, вкладки, граф, легенда, подтверждённый маршрут', async ({ context }) => {
  const page = await open(context, url(IDS.epic));
  await expect(page.getByTestId('feature-page')).toBeVisible({ timeout: 30000 });

  const title = await page.getByTestId('feature-page-hero-title').innerText().catch(() => '');
  const epicMeta = await page.getByTestId('feature-page-epic-meta').innerText().catch(() => '');
  const stagesMeta = await page.getByTestId('feature-page-stages-meta').innerText().catch(() => '');
  const progress = await page.getByTestId('feature-page-progress').innerText().catch(() => '');
  console.log('Ф1 шапка:', JSON.stringify({ title, epicMeta, stagesMeta, progress }));
  expect.soft(title.length, 'заголовок фичи не пуст').toBeGreaterThan(0);
  expect.soft(epicMeta.length, 'мета «эпик · дата» есть').toBeGreaterThan(0);
  expect.soft(stagesMeta.length, 'метастрока этапов есть').toBeGreaterThan(0);

  const tabs = await page.getByTestId('feature-page-tabs').getByRole('tab').allInnerTexts();
  console.log('Ф1 вкладки:', JSON.stringify(tabs));
  expect.soft(tabs.map(t => t.replace(/\d+/g, '').trim())).toEqual(['Процесс', 'Материалы', 'Разбор']);

  await expect(page.getByTestId('feature-process-graph')).toBeVisible();
  const nodes = page.locator('[data-testid^="pgraph-node-"]');
  const count = await nodes.count();
  console.log('Ф1 узлов графа:', count);
  expect.soft(count, 'узлов графа ≥ 8').toBeGreaterThanOrEqual(8);
  const legend = await page.getByTestId('pgraph-legend').innerText().catch(() => '');
  console.log('Ф1 легенда:', JSON.stringify(legend));
  expect.soft(legend.length, 'легенда не пуста').toBeGreaterThan(0);
  const bodyText = await page.getByTestId('feature-page-body').innerText();
  console.log('Ф1 «параллельно» в тексте:', /параллельно/i.test(bodyText));

  // предложенного этапа быть не должно: маршрут подтверждён
  console.log('Ф1 proposed-узлов:', await page.locator('[data-testid^="pgraph-proposed-"]').count());

  await expect(page.getByTestId('feature-workflow-confirm-confirmed')).toBeVisible();
  const confirmedLine = await page.getByTestId('feature-workflow-confirm-line').innerText();
  console.log('Ф1 подтверждение:', JSON.stringify(confirmedLine));

  const o = await overflow(page);
  console.log('Ф1 переполнение 1280:', JSON.stringify(o));
  expect.soft(o.sw, 'нет горизонтального переполнения на 1280').toBeLessThanOrEqual(o.cw + 1);
  await page.screenshot({ path: `${SHOTS}/f1-1280.png`, fullPage: true });
  await page.close();
});

test('Ф2: предложенный маршрут — строка предложения, блок подтверждения, граф предложения', async ({ context }) => {
  const page = await open(context, url(IDS.epicProposed));
  await expect(page.getByTestId('feature-page')).toBeVisible({ timeout: 30000 });
  const proposed = await page.locator('[data-testid^="pgraph-proposed-"]').count();
  console.log('Ф2 proposed-узлов:', proposed);
  expect.soft(proposed, 'узлы предложенного маршрута помечены «предложено»').toBeGreaterThan(0);
  const row = page.getByTestId('feature-page-route-proposal');
  console.log('Ф2 строка предложения:', await row.count());
  expect.soft(await row.count(), 'строка «Модель предлагает…» есть').toBeGreaterThan(0);
  if (await row.count() > 0) console.log('Ф2 текст строки:', JSON.stringify(await row.innerText()));
  const unconfirmed = await page.getByTestId('feature-page-unconfirmed').count();
  console.log('Ф2 плашка «не подтверждён»:', unconfirmed);
  // раскрыть блок подтверждения
  const action = page.getByTestId('feature-page-route-proposal-action');
  if (await action.count() > 0) {
    await action.click();
    await page.waitForTimeout(800);
  }
  const confirm = page.getByTestId('feature-workflow-confirm');
  console.log('Ф2 блок подтверждения:', await confirm.count());
  expect.soft(await confirm.count(), 'блок подтверждения открылся').toBeGreaterThan(0);
  if (await confirm.count() > 0) {
    console.log('Ф2 текст блока:', JSON.stringify((await confirm.innerText()).slice(0, 400)));
    console.log('Ф2 submit есть:', await page.getByTestId('feature-workflow-confirm-submit').count());
    console.log('Ф2 manual есть:', await page.getByTestId('feature-workflow-confirm-manual').count());
  }
  const emptyGraph = await page.getByTestId('feature-page-empty-graph').count();
  console.log('Ф2 пустой граф (нет маршрута):', emptyGraph);
  await page.screenshot({ path: `${SHOTS}/f2-proposed-1280.png`, fullPage: true });
  await page.close();
});

test('Ф3: мобильный экран 360 — строка «ждёт вас», шторка «Маршрут», без переполнения', async ({ context }) => {
  const page = await open(context, url(IDS.epicProposed), 360);
  await expect(page.getByTestId('feature-page')).toBeVisible({ timeout: 30000 });
  const o = await overflow(page);
  console.log('Ф3 переполнение 360:', JSON.stringify(o));
  expect.soft(o.sw, 'нет горизонтального переполнения на 360').toBeLessThanOrEqual(o.cw + 1);
  const mobileRow = await page.getByTestId('feature-workflow-confirm-mobile-row').count();
  console.log('Ф3 мобильная строка:', mobileRow);
  expect.soft(mobileRow, 'на мобиле есть строка блока маршрута').toBeGreaterThan(0);
  if (mobileRow > 0) {
    console.log('Ф3 текст строки:', JSON.stringify(await page.getByTestId('feature-workflow-confirm-mobile-row-text').innerText()));
    await page.getByTestId('feature-workflow-confirm-mobile-open').click();
    await page.waitForTimeout(1200);
    const visible = await page.locator('[data-testid]').evaluateAll(els =>
      els.filter(e => (e as HTMLElement).offsetParent !== null).map(e => e.getAttribute('data-testid')));
    console.log('Ф3 видимые testid после открытия шторки:', JSON.stringify(visible.filter(t => (t ?? '').includes('confirm') || (t ?? '').includes('sheet') || (t ?? '').includes('modal'))));
    const sheetText = await page.locator('body').innerText();
    console.log('Ф3 текст экрана (хвост):', JSON.stringify(sheetText.slice(-400)));
    const sheet = await page.getByTestId('workflow-route-sheet').innerText({ timeout: 5000 }).catch(() => '');
    console.log('Ф3 шторка «Маршрут»:', JSON.stringify(sheet.slice(0, 300)));
    expect.soft(sheet.length, 'шторка открылась').toBeGreaterThan(0);
    const o2 = await overflow(page);
    console.log('Ф3 переполнение в шторке:', JSON.stringify(o2));
    expect.soft(o2.sw, 'шторка без горизонтального переполнения').toBeLessThanOrEqual(o2.cw + 1);
    await page.screenshot({ path: `${SHOTS}/f3-sheet-360.png`, fullPage: true });
  }
  await page.screenshot({ path: `${SHOTS}/f3-360.png`, fullPage: true });
  await page.close();
});

test('Ф4: экран этапа — шапка, вкладки, блоки', async ({ context }) => {
  const page = await open(context, url(IDS.epic));
  await page.getByTestId('pgraph-node-step-back').click({ timeout: 30000 });
  await page.waitForTimeout(1500);
  const area = page.getByTestId('feature-stage-area');
  console.log('Ф4 область этапа:', await area.count());
  expect.soft(await area.count(), 'область этапа показана').toBeGreaterThan(0);
  if (await area.count() > 0) {
    console.log('Ф4 заголовок области:', JSON.stringify(await page.getByTestId('feature-stage-area-title').innerText()));
    await page.getByTestId('feature-stage-area-open').click();
    await page.waitForTimeout(1500);
  }
  const screen = page.getByTestId('stage-screen');
  expect.soft(await screen.count(), 'экран этапа открылся').toBeGreaterThan(0);
  if (await screen.count() === 0) { await page.close(); return; }
  console.log('Ф4 заголовок этапа:', JSON.stringify(await page.getByTestId('stage-screen-title').innerText()));
  console.log('Ф4 сводка:', JSON.stringify((await page.getByTestId('stage-screen-summary').innerText()).replace(/\n/g, ' | ')));
  console.log('Ф4 KPI:', await page.locator('[data-testid="stage-kpi"]').count());
  console.log('Ф4 KPI-текст:', JSON.stringify((await page.locator('[data-testid="stage-kpi"]').first().innerText({ timeout: 5000 }).catch(() => '')).replace(/\n/g, ' | ')));
  const tabs = await screen.getByRole('tab').allInnerTexts();
  console.log('Ф4 вкладки этапа:', JSON.stringify(tabs));
  for (let i = 0; i < tabs.length; i++) {
    const name = tabs[i]!;
    await screen.getByRole('tab').nth(i).click();
    await page.waitForTimeout(700);
    const blocks = await page.locator('[data-testid^="stage-"]').evaluateAll(els =>
      els.filter(e => (e as HTMLElement).offsetParent !== null).map(e => e.getAttribute('data-testid')));
    console.log(`Ф4 вкладка «${name}»: блоки`, JSON.stringify(blocks.filter(b => !/^(stage-screen|stage-head|stage-plate)/.test(b ?? ''))));
    const want: Record<string, string> = {
      'Ревью и вопросы': 'stage-review',
      'Артефакты': 'stage-artifacts',
      'Вход и транскрипт': 'stage-input',
      'Метрики': 'stage-metrics',
    };
    const key = want[name.trim()];
    if (key) expect.soft(blocks.includes(key), `на вкладке «${name}» есть блок ${key}`).toBeTruthy();
    expect.soft(await page.getByTestId('stage-screen-summary').isVisible(), `сводка видна на «${name}»`).toBeTruthy();
    await page.screenshot({ path: `${SHOTS}/f4-${i}.png`, fullPage: true });
  }
  const o = await overflow(page);
  console.log('Ф4 переполнение:', JSON.stringify(o));
  expect.soft(o.sw, 'нет переполнения на экране этапа').toBeLessThanOrEqual(o.cw + 1);
  await page.close();
});

test('Ф5: «Разбор» — дорожки этапов есть в «Время и срок» и отсутствуют в «Хронологии»', async ({ context }) => {
  const page = await open(context, url(IDS.epic));
  await page.getByRole('tab', { name: /Разбор/ }).click();
  await page.waitForTimeout(1500);
  const time = page.getByTestId('review-sec-time');
  const history = page.getByTestId('review-sec-history');
  expect.soft(await time.count(), 'секция «Время и срок» есть').toBeGreaterThan(0);
  expect.soft(await history.count(), 'секция «Хронология» есть').toBeGreaterThan(0);
  const lanesInTime = await time.locator('[data-testid^="review-stage-lanes"]').count();
  const lanesInHistory = await history.locator('[data-testid^="review-stage-lanes"]').count();
  console.log('Ф5 дорожки: в «Время и срок»', lanesInTime, '| в «Хронологии»', lanesInHistory);
  expect.soft(lanesInTime, 'дорожки есть в «Время и срок»').toBeGreaterThan(0);
  expect.soft(lanesInHistory, 'дорожек в «Хронологии» нет').toBe(0);
  await page.screenshot({ path: `${SHOTS}/f5-review.png`, fullPage: true });
  await page.close();
});

test('Ф6: экран этапа на 360 — вкладки, блоки, без переполнения', async ({ context }) => {
  const page = await open(context, url(IDS.epic), 360);
  await page.getByTestId('pgraph-node-step-back').click({ timeout: 30000 });
  await page.waitForTimeout(1500);
  const openBtn = page.getByTestId('feature-stage-area-open');
  if (await openBtn.count() > 0) { await openBtn.click(); await page.waitForTimeout(1500); }
  const screen = page.getByTestId('stage-screen');
  expect.soft(await screen.count(), 'экран этапа открылся на 360').toBeGreaterThan(0);
  if (await screen.count() > 0) {
    const tabs = await screen.getByRole('tab').allInnerTexts();
    console.log('Ф6 вкладки этапа на 360:', JSON.stringify(tabs));
    for (let i = 0; i < tabs.length; i++) {
      await screen.getByRole('tab').nth(i).click();
      await page.waitForTimeout(600);
      const blocks = await page.locator('[data-testid^="stage-"]').evaluateAll(els =>
        els.filter(e => (e as HTMLElement).offsetParent !== null).map(e => e.getAttribute('data-testid')));
      console.log(`Ф6 вкладка «${tabs[i]}»:`, JSON.stringify(blocks.filter(b => !/^(stage-screen|stage-head|stage-plate)/.test(b ?? ''))));
    }
  }
  const o = await overflow(page);
  console.log('Ф6 переполнение 360:', JSON.stringify(o));
  expect.soft(o.sw, 'нет горизонтального переполнения на 360').toBeLessThanOrEqual(o.cw + 1);
  await page.screenshot({ path: `${SHOTS}/f6-stage-360.png`, fullPage: true });
  await page.close();
});

test('Ф7: фича без маршрута — пустые дорожки в «Разборе», без падений', async ({ context }) => {
  const page = await open(context, url(IDS.epicNoRoute));
  await expect(page.getByTestId('feature-page')).toBeVisible({ timeout: 30000 });
  const emptyGraph = await page.getByTestId('feature-page-empty-graph').count();
  console.log('Ф7 пустой граф:', emptyGraph);
  await page.getByRole('tab', { name: /Разбор/ }).click();
  await page.waitForTimeout(1500);
  const time = page.getByTestId('review-sec-time');
  const history = page.getByTestId('review-sec-history');
  console.log('Ф7 секция «Время и срок»:', await time.count(), '| «Хронология»:', await history.count());
  if (await time.count() > 0) {
    console.log('Ф7 дорожки в «Время и срок»:', await time.locator('[data-testid^="review-stage-lanes"]').count());
    console.log('Ф7 текст «Время и срок»:', JSON.stringify((await time.innerText()).slice(0, 300)));
  }
  if (await history.count() > 0) {
    console.log('Ф7 дорожки в «Хронологии»:', await history.locator('[data-testid^="review-stage-lanes"]').count());
    console.log('Ф7 текст «Хронология»:', JSON.stringify((await history.innerText()).slice(0, 300)));
  }
  console.log('Ф7 ошибки консоли на этом экране:', JSON.stringify(errors.slice(-5)));
  await page.screenshot({ path: `${SHOTS}/f7-no-route.png`, fullPage: true });
  await page.close();
});

test.afterAll(() => {
  console.log('ФИНАЛ ошибки консоли:', JSON.stringify(errors.slice(0, 20)));
});
