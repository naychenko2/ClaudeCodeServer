import { test, expect } from '@playwright/test';
import { login } from './helpers';

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';
const flat = (s: string) => s.replace(/\n/g, ' | ');

test('П8: что в области маршрута на странице фичи', async ({ context, playwright, baseURL }) => {
  test.setTimeout(120_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(6000);
  const ids = await page.locator('[data-testid^="feature-workflow"], [data-testid^="feature-page-workflow"], [data-testid^="feature-page-route"]').evaluateAll(e => e.map(x => x.getAttribute('data-testid')));
  console.log('П8 testids:', JSON.stringify([...new Set(ids)]));
  const wrap = await page.getByTestId('feature-page-workflow-confirm').innerText({ timeout: 3000 }).catch(() => 'НЕТ');
  console.log('П8 область маршрута:', flat(wrap).slice(0, 700));
  const btns = await page.getByRole('button').allInnerTexts();
  console.log('П8 кнопки страницы:', JSON.stringify(btns.filter(b => /сменить|обновить|почему|маршрут/i.test(b)).slice(0, 10)));
  await page.screenshot({ path: '.cc-attachments/qa-wf-e2e/p8-route.png', fullPage: true });
  expect(true).toBeTruthy();
});
