import { test } from '@playwright/test';
import { login, api, type Api } from './helpers';

// Диагностика QA-5: что рендерит страница фичи с предложением на 1280 и на 360 —
// compact-строка или блок. Одноразовая, утверждений о продукте не делает.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'cedc2dcb-0612-4f9e-b9d3-35e9e9610a8e';
const IDS = [
  'feature-page-workflow-confirm', 'feature-workflow-confirm', 'feature-page-route-proposal',
  'feature-page-route-proposal-action', 'feature-workflow-confirm-mobile-row',
  'feature-workflow-confirm-mobile-open', 'feature-workflow-confirm-plan-hint',
  'feature-workflow-confirm-submit', 'pgraph-legend-proposed',
];

let client: Api;
let request: import('@playwright/test').APIRequestContext;
let token: string;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});

test.afterAll(async () => { await request?.dispose(); });

test('P1: что на странице предложения @1280 и @360', async ({ browser }) => {
  test.setTimeout(150_000);
  const run = await client.get<any>(`/api/tasks/features/${EPIC}`);
  console.log('P1 api: routeRequired=', JSON.stringify(run?.epic?.routeRequired), 'wp=', Boolean(run?.epic?.workflowProposal), 'wf=', Boolean(run?.epic?.workflow));
  const ctx = await browser.newContext();
  for (const w of [1280, 360]) {
    const page = await ctx.newPage();
    await ctx.addInitScript(([tk]: string[]) => { localStorage.setItem('cc_token', tk); localStorage.setItem('theme-mode', 'light'); }, [token]);
    await page.setViewportSize({ width: w, height: 900 });
    await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(6000);
    const counts: Record<string, number> = {};
    for (const id of IDS) counts[id] = await page.locator(`[data-testid="${id}"]`).count();
    console.log(`P1 @${w}:`, JSON.stringify(counts));
    if (w === 1280) {
      const act = page.locator('[data-testid="feature-page-route-proposal-action"]');
      if (await act.count()) {
        await act.first().click();
        await page.waitForTimeout(3000);
        const after: Record<string, number> = {};
        for (const id of IDS) after[id] = await page.locator(`[data-testid="${id}"]`).count();
        console.log('P1 @1280 после раскрытия:', JSON.stringify(after));
      }
    }
    await page.screenshot({ path: `.cc-attachments/qa-wf-e2e/qa5-probe-${w}.png`, fullPage: true });
    await page.close();
  }
  await ctx.close();
});
