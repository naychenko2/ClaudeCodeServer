import { test, expect } from '@playwright/test';
import { login, api } from './helpers';

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';
const flat = (s: string) => s.replace(/\n/g, ' | ');

test('П7: что говорит API и страница про обновлённый шаблон', async ({ context, playwright, baseURL }) => {
  test.setTimeout(150_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  const c = api(req, token);
  const wf = await c.get<any>(`/api/tasks/${EPIC}/workflow`);
  console.log('П7 confirmed:', JSON.stringify({ v: wf.confirmed?.confirmedVersion, dismissed: wf.confirmed?.dismissedUpdateVersion }));
  console.log('П7 templateUpdate:', JSON.stringify(wf.templateUpdate), 'updateAvailable:', wf.updateAvailable);
  await req.dispose();

  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(5000);
  const line = await page.getByTestId('feature-workflow-confirm-line').innerText({ timeout: 3000 }).catch(() => 'НЕТ СТРОКИ');
  console.log('П7 строка маршрута:', flat(line).slice(0, 300));
  const block = await page.getByTestId('feature-workflow-confirm').innerText({ timeout: 3000 }).catch(() => 'НЕТ БЛОКА');
  console.log('П7 блок:', flat(block).slice(0, 600));
  const upd = await page.getByTestId('feature-workflow-confirm-confirmed').innerText({ timeout: 3000 }).catch(() => 'НЕТ');
  console.log('П7 confirmed-блок:', flat(upd).slice(0, 400));
  console.log('П7 кнопки update:', await page.getByTestId('feature-workflow-confirm-update-apply').count(), await page.getByTestId('feature-workflow-confirm-update-keep').count());
  const body = await page.locator('body').innerText();
  console.log('П7 строки с «шаблон»: ', JSON.stringify(flat(body).split('|').filter(s => /шаблон/i.test(s)).slice(0, 6)));
  await page.screenshot({ path: '.cc-attachments/qa-wf-e2e/p7-update.png', fullPage: true });
  expect(true).toBeTruthy();
});
