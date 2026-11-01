import { test, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const SHOTS = process.env.QA_WIKI_SHOTS || path.join(process.cwd(), '..', '.cc-attachments', 'qa-llm-wiki-p1b');
const PID = '047a15f0-1bda-4567-8bbb-ed39f55c376e';
const CHAT_E = 'd09ee44e-7acb-4581-b1cd-121d93b4c075';

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

test('диагностика: какой чат открыт по диплинку и что делает «Разобрать этот чат»', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const reqs: string[] = [];
  page.on('request', r => { if (r.url().includes('/api/wiki')) reqs.push(`${r.method()} ${r.url().split('/api/wiki')[1]}`); });

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(`/#/project/${PID}/chat/${CHAT_E}`);
  await page.waitForTimeout(7000);
  console.log('[diag] URL:', page.url());
  console.log('[diag] запросы wiki:', JSON.stringify(reqs));
  const body = await page.locator('body').innerText();
  console.log('[diag] есть «QA-чат-E» в экране:', body.includes('QA-чат-E'));
  console.log('[diag] есть «QA-чат-5» в экране:', body.includes('QA-чат-5'));
  // Заголовок открытого чата
  const h = page.locator('h1, h2, [data-testid="chat-title"]').allInnerTexts().catch(() => []);
  console.log('[diag] заголовки:', JSON.stringify(h.filter(Boolean).slice(0, 5)));
  await shot(page, 'diag-chat-e.png');
});

test('диагностика 360: переключатель «Заметки | Вики | Граф»', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize({ width: 360, height: 780 });
  await page.goto('/#/chats');
  await page.waitForTimeout(4000);
  const notesTab = page.getByText('Заметки', { exact: true }).first();
  console.log('[diag360] вкладка «Заметки»:', await notesTab.count());
  if (await notesTab.count()) { await notesTab.click(); await page.waitForTimeout(4500); }
  await shot(page, 'diag360-notes.png');
  const text = await page.locator('body').innerText();
  console.log('[diag360] текст (1200):', JSON.stringify(text.slice(0, 1200)));
  const wikiExact = page.getByText('Вики', { exact: true });
  console.log('[diag360] «Вики» exact:', await wikiExact.count());
  const wikiAny = page.getByText('Вики');
  console.log('[diag360] «Вики» any:', await wikiAny.count());
  const btns = await page.locator('button').allInnerTexts();
  console.log('[diag360] кнопки:', JSON.stringify(btns.filter(Boolean).slice(0, 40)));
});
