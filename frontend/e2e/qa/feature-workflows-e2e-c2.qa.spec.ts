import { test, expect } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login } from './helpers';

// Часть C2: экран этапа — четыре блока (артефакты с диффом, вход и транскрипт,
// метрики по трём ролям, отчёт ревью и вопросы человеку).

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';
const SHOTS = '.cc-attachments/qa-wf-e2e';
const errors: string[] = [];

const flat = (s: string) => s.replace(/\n/g, ' | ');

test('С4в: экран этапа по всем четырём блокам', async ({ context, playwright, baseURL }) => {
  test.setTimeout(300_000);
  mkdirSync(SHOTS, { recursive: true });
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();

  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  page.on('console', m => { if (m.type() === 'error') errors.push(`[error] ${m.text()}`.slice(0, 300)); });
  page.on('pageerror', e => errors.push(`[pageerror] ${e}`.slice(0, 300)));
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);

  await page.getByTestId('pgraph-node-cut').click();
  await page.waitForTimeout(1500);
  console.log('С4в area title:', flat(await page.getByTestId('feature-stage-area-title').innerText({ timeout: 3000 }).catch(() => '')));
  await page.getByTestId('feature-stage-area-open').click({ timeout: 15000 }).catch(e => console.log('С4в открыть этап:', String(e).slice(0, 100)));
  await page.waitForTimeout(2500);

  const screen = page.getByTestId('stage-screen');
  expect.soft(await screen.count(), 'экран этапа открылся').toBeGreaterThan(0);
  console.log('С4в head:', flat(await page.getByTestId('stage-head').innerText({ timeout: 3000 }).catch(() => '')).slice(0, 400));
  console.log('С4в kpis:', flat(await page.getByTestId('stage-screen-kpis').innerText({ timeout: 3000 }).catch(() => '')).slice(0, 400));
  console.log('С4в summary:', flat(await page.getByTestId('stage-screen-summary').innerText({ timeout: 3000 }).catch(() => '')).slice(0, 500));
  const tabs = await screen.getByRole('tab').allInnerTexts();
  console.log('С4в вкладки:', JSON.stringify(tabs));

  const seen: Record<string, string[]> = {};
  for (const name of ['Ревью и вопросы', 'Артефакты', 'Вход и транскрипт', 'Метрики']) {
    const t = screen.getByRole('tab', { name: new RegExp(name.replace('и вопросы', '')) });
    if (await t.count() === 0) { console.log('С4в НЕТ вкладки', name); continue; }
    await t.click(); await page.waitForTimeout(1200);
    const blocks = await page.locator('[data-testid^="stage-"]').evaluateAll(els => els
      .filter(e => (e as HTMLElement).offsetParent !== null)
      .map(e => `${e.getAttribute('data-testid')}`));
    seen[name] = blocks;
    const body = await page.locator('[data-testid="stage-screen-body"]').innerText({ timeout: 3000 }).catch(() => '');
    console.log(`С4в [${name}] блоки:`, JSON.stringify([...new Set(blocks)].slice(0, 25)));
    console.log(`С4в [${name}] текст:`, flat(body).slice(0, 1500));
    await page.screenshot({ path: `${SHOTS}/s4v-tab-${name.replace(/\s/g, '')}.png`, fullPage: true });
  }

  // Дифф коммита: открыть первый коммит списка
  const artTab = screen.getByRole('tab', { name: /Артефакты/ });
  if (await artTab.count()) {
    await artTab.click(); await page.waitForTimeout(1000);
    const commits = page.getByTestId('stage-commits').locator('button, [role="button"]');
    console.log('С4в элементов коммитов:', await commits.count());
    if (await commits.count()) {
      await commits.first().click().catch(() => undefined);
      await page.waitForTimeout(2000);
      const diff = await page.getByTestId('stage-file-diff').innerText({ timeout: 3000 }).catch(() => '');
      console.log('С4в дифф:', flat(diff).slice(0, 700));
      await page.screenshot({ path: `${SHOTS}/s4v-diff.png`, fullPage: true });
    }
  }
  console.log('С4в CONSOLE ERRORS:', JSON.stringify([...new Set(errors)].slice(0, 10)));
  await page.close();
});
