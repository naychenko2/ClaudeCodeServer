import { test, expect } from '@playwright/test';
import { login } from './helpers';

// Почему экран этапа пустой: что отдаёт ручка /stage и что в DOM.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';
const CUT = '8549c3e1-2c42-45da-9c04-831b78e57ed5';

const flat = (s: string) => s.replace(/\n/g, ' | ');

test('П5: ручка /stage и DOM экрана этапа', async ({ context, playwright, baseURL }) => {
  test.setTimeout(120_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  const st = await req.get(`/api/tasks/${CUT}/stage`, { headers: { Authorization: `Bearer ${token}` } });
  const body = await st.text();
  console.log('П5 /stage статус:', st.status(), 'длина:', body.length);
  console.log('П5 /stage начало:', body.slice(0, 500));
  const j = body ? JSON.parse(body) : null;
  console.log('П5 ключи:', Object.keys(j ?? {}).slice(0, 12));
  console.log('П5 artifacts:', JSON.stringify(j?.artifacts)?.slice(0, 400));
  console.log('П5 metrics:', JSON.stringify(j?.metrics)?.slice(0, 400));
  console.log('П5 questions:', JSON.stringify(j?.questions)?.slice(0, 300));
  console.log('П5 review:', JSON.stringify(j?.review)?.slice(0, 400));
  await req.dispose();

  const page = await context.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  await page.getByTestId('pgraph-node-cut').click();
  await page.waitForTimeout(1500);
  await page.getByTestId('feature-stage-area-open').click({ timeout: 15000 }).catch(() => undefined);
  await page.waitForTimeout(4000);
  const sub = await page.locator('[data-testid="stage-screen"]').evaluateAll(els => els.map(e => ({
    html: (e as HTMLElement).innerHTML.slice(0, 300),
  })));
  console.log('П5 stage-screen html:', JSON.stringify(sub).slice(0, 600));
  for (const t of ['stage-screen-loading', 'stage-screen-error', 'stage-screen-empty', 'stage-screen-picker', 'stage-screen-tabs']) {
    console.log('П5', t, await page.getByTestId(t).count(), flat(await page.getByTestId(t).innerText({ timeout: 3000 }).catch(() => '')).slice(0, 200));
  }
  await page.screenshot({ path: '.cc-attachments/qa-wf-e2e/p5-stage.png', fullPage: true });
  expect(true).toBeTruthy();
});
