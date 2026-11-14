import { test, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login } from './helpers';

// Часть E2: «Артефакты с версиями и диффом» на этапе с коммитом (С7) и
// «вопросы человеку» на этапе, который сам остановился с вопросом (С5).

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';
const SHOTS = '.cc-attachments/qa-wf-e2e';
const flat = (s: string) => s.replace(/\n/g, ' | ');

async function openStage(page: Page, node: string) {
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  await page.getByTestId(`pgraph-node-${node}`).click();
  await page.waitForTimeout(1500);
  await page.getByTestId('feature-stage-area-open').click({ timeout: 15000 }).catch(e => console.log('open', node, String(e).slice(0, 80)));
  await page.waitForTimeout(3000);
  await page.getByTestId('stage-screen-tabs').waitFor({ timeout: 20000 }).catch(() => console.log('нет табов у', node));
}

test('С7в: артефакты этапа с версией и диффом', async ({ context, playwright, baseURL }) => {
  test.setTimeout(240_000);
  mkdirSync(SHOTS, { recursive: true });
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = []; const failed: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  page.on('requestfailed', r => failed.push(`${r.url()} ${r.failure()?.errorText}`));
  page.on('response', r => { if (r.status() >= 400) failed.push(`${r.url()} ${r.status()}`); });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openStage(page, 'step');

  await page.getByTestId('stage-screen').getByRole('tab', { name: /Артефакты/ }).click();
  await page.waitForTimeout(1500);
  const art = await page.getByTestId('stage-artifacts').innerText({ timeout: 3000 }).catch(() => 'НЕТ БЛОКА');
  console.log('С7в артефакты:', flat(art).slice(0, 700));
  const files = await page.locator('[data-testid^="stage-file-"]').evaluateAll(e => e.map(x => x.getAttribute('data-testid')));
  console.log('С7в файлы:', JSON.stringify(files.slice(0, 6)));
  const commits = await page.locator('[data-testid^="stage-commit-"]').evaluateAll(e => e.map(x => x.getAttribute('data-testid')));
  const uniq = [...new Set(commits.map(c => c!.replace('stage-commit-', '').replace('stage-commit-toggle-', '').replace('stage-commit-files-', '')))].slice(0, 6);
  console.log('С7в коммиты:', JSON.stringify(uniq));
  await page.locator('[data-testid^="stage-file-"]').first().click({ timeout: 5000 }).catch(() => undefined);
  await page.waitForTimeout(2500);
  const diff = await page.getByTestId('stage-file-diff').innerText({ timeout: 3000 }).catch(() => '');
  const pane = await page.getByTestId('stage-file-diff-pane').innerText({ timeout: 3000 }).catch(() => '');
  console.log('С7в дифф:', flat(diff).slice(0, 500) || flat(pane).slice(0, 500));
  await page.screenshot({ path: `${SHOTS}/С7v-artifacts.png`, fullPage: true });
  console.log('С7в errors:', JSON.stringify([...new Set(errs)]), 'failed:', JSON.stringify([...new Set(failed)].slice(0, 6)));
  await page.close();
});

test('С5в: вопрос человеку на экране этапа', async ({ context, playwright, baseURL }) => {
  test.setTimeout(240_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await openStage(page, 'plan');

  console.log('С5в kpis:', flat(await page.getByTestId('stage-plates').innerText({ timeout: 3000 }).catch(() => '')).slice(0, 300));
  const q = await page.getByTestId('stage-screen').getByRole('tab', { name: /Ревью и вопросы/ }).click().then(() => page.getByTestId('stage-questions').innerText({ timeout: 3000 }).catch(() => 'НЕТ БЛОКА'));
  console.log('С5в вопросы:', flat(q).slice(0, 600));
  await page.waitForTimeout(1000);
  await page.screenshot({ path: `${SHOTS}/С5v-questions.png`, fullPage: true });
  console.log('С5в errors:', JSON.stringify([...new Set(errs)]));
  await page.close();
});
