import { test, type BrowserContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { login } from './helpers';

// Приёмка «Процессы фичи», четвёртый спек: чип «← к фиче» (сценарий 9).
const SHOTS = '.cc-attachments/qa-wf-final';
const PID = '32527f34-1907-4202-8c5c-9587b89d6366';
const D = 'fa5b0598-cbea-4d2f-94ef-8ecbacadabff';
const url = (f: string) => `/#/project/${PID}/feature/${f}`;
let token: string;

test.beforeAll(async ({ playwright, baseURL }) => {
  const req = await playwright.request.newContext({ baseURL });
  token = await login(req);
  await req.dispose();
  mkdirSync(SHOTS, { recursive: true });
});

async function open(context: BrowserContext, path: string, w = 1280): Promise<Page> {
  const page = await context.newPage();
  await page.setViewportSize({ width: w, height: 900 });
  await context.addInitScript(tk => { localStorage.setItem('cc_token', tk as string); }, token);
  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(3000);
  return page;
}

for (const [id, label] of [[D, 'Ход']] as const) {
  test(`С9: чип возврата — ${label}`, async ({ context }) => {
    const p = await open(context, url(id));
    console.log(label, 'вкладки:', JSON.stringify(await p.getByTestId('feature-page-tabs').getByRole('tab').allInnerTexts()));
    // 1) через «Ответить» плашки «от тебя» (чат тревоги)
    const ans = p.getByTestId('feature-page').getByRole('button', { name: /^Задача$/ });
    console.log(label, 'Задача:', await ans.count());
    // 2) через открытие задачи этапа (если есть)
    if (await ans.count()) {
      await ans.first().click();
    } else {
      const btns = await p.getByTestId('feature-page').getByRole('button').allInnerTexts();
      console.log(label, 'кнопки страницы:', JSON.stringify(btns.slice(0, 40)));
      const rows = p.locator('[data-testid^="timeline-stage-"], [data-testid^="feature-task-"], [data-testid*="step-row"]');
      console.log(label, 'строки хода:', await rows.count());
      const task = p.getByTestId('feature-page').getByText(/qa-wf/).first();
      console.log(label, 'qa-wf текстов:', await task.count());
      if (await task.count()) await task.click();
    }
    await p.waitForTimeout(3000);
    console.log(label, 'URL:', p.url());
    const body = await p.locator('body').innerText();
    const m = body.match(/[^\n]*к фиче[^\n]*/gi);
    console.log(label, 'строки с «к фиче»:', JSON.stringify(m));
    await p.screenshot({ path: `${SHOTS}/c9-${label}.png` });
    await p.close();
  });
}
