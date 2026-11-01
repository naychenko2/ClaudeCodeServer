import { test, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// QA-П1b · LLM-wiki — п.1: флаг llm-wiki ВЫКЛЮЧЕН у admin — ни кнопок в шапке, ни запросов /api/wiki.
const SHOTS = process.env.QA_WIKI_SHOTS || path.join(process.cwd(), '..', '.cc-attachments', 'qa-llm-wiki-p1b');
const PID = '047a15f0-1bda-4567-8bbb-ed39f55c376e';

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name), fullPage: false });
}
async function loginToken(): Promise<string> {
  const r = await fetch(`${process.env.PLAYWRIGHT_BASE_URL}/api/auth/login`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: 'admin', password: '12345' }),
  });
  return (await r.json()).token as string;
}
test.use({ serviceWorkers: 'block' });

test('п.1 — флаг выкл: ни кнопок вики в шапке, ни запросов /api/wiki', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const reqs: string[] = [];
  page.on('request', r => { if (r.url().includes('/api/wiki')) reqs.push(`${r.method()} ${r.url().split('/api/wiki')[1]}`); });

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(`/#/project/${PID}`);
  await page.waitForTimeout(6000);
  const chat = page.getByText('QA-чат-G', { exact: true }).first();
  console.log('[off] чат в списке:', await chat.count());
  if (await chat.count()) { await chat.click(); await page.waitForTimeout(5000); }
  await shot(page, 'flagoff-chat.png');
  console.log('[off] кнопок вики в шапке:', await page.locator('[data-testid^="wiki-chat-"]').count());
  console.log('[off] запросы /api/wiki на чате:', JSON.stringify(reqs));

  // «Заметки» → нет вкладки «Вики»
  const notesTab = page.getByText('Заметки', { exact: true }).first();
  if (await notesTab.count()) { await notesTab.click(); await page.waitForTimeout(4000); }
  await shot(page, 'flagoff-notes.png');
  const body = await page.locator('body').innerText();
  console.log('[off] есть вкладка «Вики» в «Заметках»:', await page.getByText('Вики', { exact: true }).count());
  console.log('[off] запросы /api/wiki всего:', JSON.stringify(reqs));
});
