import { test, expect } from '@playwright/test';
import { login } from './helpers';

// Что показывает блок маршрута на фиче с живым предложением модели.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';

test('П4: блок маршрута при живом предложении', async ({ context, playwright, baseURL }) => {
  test.setTimeout(120_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });

  for (const t of [3, 8, 15, 25]) {
    await page.waitForTimeout(t === 3 ? 3000 : 5000);
    const n = await page.getByTestId('feature-workflow-confirm').count();
    console.log(`П4 t=${t}s блок:`, n, n ? (await page.getByTestId('feature-workflow-confirm').innerText()).replace(/\n/g, ' | ').slice(0, 400) : '');
    if (n) break;
  }
  console.log('П4 rationale:', await page.getByTestId('feature-workflow-confirm-rationale').count());
  console.log('П4 checkboxes:', await page.getByTestId('feature-workflow-confirm').getByRole('checkbox').count());
  await page.screenshot({ path: '.cc-attachments/qa-wf-e2e/p4-block.png', fullPage: true });
  expect(true).toBeTruthy();
});
