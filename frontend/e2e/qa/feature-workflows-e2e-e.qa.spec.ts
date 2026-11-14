import { test, expect, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login } from './helpers';

// Часть E: экран этапа по всем четырём блокам на живых данных:
// «Разрез» (исполнитель + его ревью), «Ревью» (коммит и дифф), «План» (вопрос человеку).

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';
const SHOTS = '.cc-attachments/qa-wf-e2e';
const errors: string[] = [];
const flat = (s: string) => s.replace(/\n/g, ' | ');

async function openStage(page: Page, node: string) {
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  await page.getByTestId(`pgraph-node-${node}`).click();
  await page.waitForTimeout(1500);
  await page.getByTestId('feature-stage-area-open').click({ timeout: 15000 }).catch(e => console.log('open stage', node, String(e).slice(0, 80)));
  await page.waitForTimeout(4000);
  await page.getByTestId('stage-screen-tabs').waitFor({ timeout: 20000 }).catch(() => console.log('таблиц нет у', node));
  await page.waitForTimeout(1000);
}

async function dumpTabs(page: Page, tag: string) {
  const screen = page.getByTestId('stage-screen');
  const kpis = await page.getByTestId('stage-plates').innerText({ timeout: 3000 }).catch(() => '');
  console.log(`${tag} kpis:`, flat(kpis).slice(0, 300));
  const tabs = await screen.getByRole('tab').allInnerTexts();
  console.log(`${tag} вкладки:`, JSON.stringify(tabs));
  for (const name of tabs) {
    const key = name.replace(/[0-9\s·]/g, '');
    await screen.getByRole('tab', { name }).click().catch(() => undefined);
    await page.waitForTimeout(1200);
    const body = await page.locator('[data-testid="stage-screen-body"]').innerText({ timeout: 3000 }).catch(() => '');
    const blocks = await page.locator('[data-testid^="stage-"]').evaluateAll(els => [...new Set(els
      .filter(e => (e as HTMLElement).offsetParent !== null).map(e => e.getAttribute('data-testid')!))]);
    console.log(`${tag} [${name}] блоки:`, JSON.stringify(blocks.slice(0, 20)));
    console.log(`${tag} [${name}] текст:`, flat(body).slice(0, 1400));
    await page.screenshot({ path: `${SHOTS}/${tag}-${key}.png`, fullPage: true });
  }
}

test('С4: экран этапа «Разрез» — ревью, артефакты, транскрипт, метрики', async ({ context, playwright, baseURL }) => {
  test.setTimeout(280_000);
  mkdirSync(SHOTS, { recursive: true });
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  page.on('console', m => { if (m.type() === 'error') errors.push(`[cut] ${m.text()}`.slice(0, 250)); });
  page.on('pageerror', e => errors.push(`[cut pageerror] ${e}`.slice(0, 250)));
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await openStage(page, 'cut');
  expect.soft(await page.getByTestId('stage-screen').count(), 'экран этапа открылся').toBeGreaterThan(0);
  console.log('С4 head:', flat(await page.getByTestId('stage-head').innerText({ timeout: 3000 }).catch(() => '')).slice(0, 250));
  await dumpTabs(page, 'С4cut');
  console.log('С4 errors:', JSON.stringify([...new Set(errors)]));
  await page.close();
});

test('С6: экран этапа «Ревью» — коммит и дифф артефакта', async ({ context, playwright, baseURL }) => {
  test.setTimeout(240_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 250)); });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await openStage(page, 'review');
  const tabs = await page.getByTestId('stage-screen').getByRole('tab').allInnerTexts();
  console.log('С6 вкладки:', JSON.stringify(tabs));
  const art = page.getByTestId('stage-screen').getByRole('tab', { name: /Артефакты/ });
  if (await art.count()) {
    await art.click(); await page.waitForTimeout(1500);
    const commits = await page.locator('[data-testid^="stage-commit-"]').evaluateAll(e => e.map(x => x.getAttribute('data-testid')));
    console.log('С6 блоки коммитов:', JSON.stringify([...new Set(commits.map(c => c!.split('-')[1]))]));
    const files = await page.locator('[data-testid^="stage-file-"]').evaluateAll(e => e.map(x => x.getAttribute('data-testid')));
    console.log('С6 файлы:', JSON.stringify(files.slice(0, 8)));
    const file = page.locator('[data-testid^="stage-file-"]').first();
    if (await file.count()) {
      await file.click().catch(() => undefined); await page.waitForTimeout(2500);
      const diff = await page.getByTestId('stage-file-diff-pane').innerText({ timeout: 3000 }).catch(() => '');
      console.log('С6 дифф:', flat(diff).slice(0, 700));
      await page.screenshot({ path: `${SHOTS}/С6-diff.png`, fullPage: true });
    }
  }
  console.log('С6 errors:', JSON.stringify([...new Set(errs)]));
  await page.close();
});

test('С5в: экран этапа «План» — вопрос человеку и ожидание', async ({ context, playwright, baseURL }) => {
  test.setTimeout(240_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 250)); });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await openStage(page, 'plan');
  const body = await page.locator('[data-testid="stage-screen-body"]').innerText({ timeout: 3000 }).catch(() => '');
  console.log('С5в summary:', flat(await page.getByTestId('stage-plates').innerText({ timeout: 3000 }).catch(() => '')).slice(0, 300));
  console.log('С5в вопросы:', flat(await page.getByTestId('stage-questions').innerText({ timeout: 3000 }).catch(() => 'НЕТ БЛОКА')).slice(0, 500));
  console.log('С5в текст тела:', flat(body).slice(0, 900));
  await page.screenshot({ path: `${SHOTS}/С5v-plan.png`, fullPage: true });
  console.log('С5в errors:', JSON.stringify([...new Set(errs)]));
  await page.close();
});
