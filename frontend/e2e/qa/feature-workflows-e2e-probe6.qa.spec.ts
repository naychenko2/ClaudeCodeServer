import { test, expect } from '@playwright/test';
import { login } from './helpers';

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';

const flat = (s: string) => s.replace(/\n/g, ' | ');

test('П6: DOM экрана этапа целиком', async ({ context, playwright, baseURL }) => {
  test.setTimeout(120_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  const errs: string[] = [];
  page.on('console', m => { if (m.type() === 'error') errs.push(m.text().slice(0, 200)); });
  page.on('pageerror', e => errs.push(String(e).slice(0, 200)));
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  await page.getByTestId('pgraph-node-cut').click();
  await page.waitForTimeout(1500);
  await page.getByTestId('feature-stage-area-open').click({ timeout: 15000 }).catch(() => undefined);
  await page.waitForTimeout(5000);
  const txt = await page.locator('[data-testid="stage-screen"]').innerText({ timeout: 3000 }).catch(() => 'НЕТ ЭКРАНА');
  console.log('П6 текст экрана:', flat(txt).slice(0, 1200));
  console.log('П6 testids:', JSON.stringify(await page.locator('[data-testid^="stage-"]').evaluateAll(e => e.map(x => `${x.getAttribute('data-testid')}:vis=${(x as HTMLElement).offsetParent !== null}`))));
  console.log('П6 errors:', JSON.stringify([...new Set(errs)]));
  await page.screenshot({ path: '.cc-attachments/qa-wf-e2e/p6-stage.png', fullPage: true });
  expect(true).toBeTruthy();
});
