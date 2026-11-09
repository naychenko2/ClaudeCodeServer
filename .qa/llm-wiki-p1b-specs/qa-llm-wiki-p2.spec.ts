import { test, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// QA-П1b · LLM-wiki — п.2: статус разбора, двойное нажатие (один разбор), «идёт» после reload.
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

// Открыть чат кликом по списку проекта: диплинк на чат слетает (URL остаётся на проекте)
async function openChat(page: Page, chatName: string) {
  await page.goto('/#/chats');
  await page.waitForTimeout(4000);
  // Заходим в проект: его чаты в общем списке не видны
  await page.goto(`/#/project/${PID}`);
  await page.waitForTimeout(6000);
  const chat = page.getByText(chatName, { exact: true }).first();
  const n = await chat.count();
  console.log('[p2] чат «' + chatName + '» в списке:', n);
  if (!n) return false;
  await chat.click();
  await page.waitForTimeout(5000);
  return true;
}

test('п.2 — двойное нажатие: один разбор; статус «разбираю…»; «идёт» после reload', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const posts: string[] = [];
  page.on('request', r => { if (r.url().includes('/analyze') && r.method() === 'POST') posts.push(r.url()); });

  await page.setViewportSize({ width: 1280, height: 900 });
  const opened = await openChat(page, 'QA-чат-G');
  if (!opened) return;
  console.log('[p2] URL:', page.url());

  const btn = page.locator('[data-testid="wiki-chat-analyze"]');
  console.log('[p2] кнопка разбора:', await btn.count(), JSON.stringify(await btn.innerText()));
  if (!(await btn.count())) return;

  // Двойное нажатие подряд
  await btn.click({ force: true });
  await btn.click({ force: true }).catch(e => console.log('[p2] второе нажатие:', (e as Error).message.slice(0, 60)));

  // Быстрый опрос UI: ловим статус разбора
  let statusSeen = '';
  for (let i = 0; i < 25; i++) {
    const b = await page.locator('body').innerText();
    const m = b.match(/[^\n]*(разбираю|разбор идёт|идёт разбор|готов|нечего разобрать|не разбирать)[^\n]*/i);
    if (m && m[0].trim()) { statusSeen = m[0].trim(); break; }
    if (i === 3) await shot(page, 'p2-status-running.png');
    await page.waitForTimeout(300);
  }
  console.log('[p2] СТАТУС В UI:', JSON.stringify(statusSeen));
  console.log('[p2] текст кнопки во время разбора:', JSON.stringify(await btn.innerText()));
  await page.waitForTimeout(2500);
  console.log('[p2] POST analyze после двойного нажатия:', posts.length, JSON.stringify(posts));
  await shot(page, 'p2-status-after.png');
});

test('п.2 — reload во время разбора: статус «идёт» восстанавливается', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(`/#/project/${PID}`);
  await page.waitForTimeout(6000);
  const chat = page.getByText('QA-чат-H', { exact: true }).first();
  console.log('[p2r] чат «QA-чат-H»:', await chat.count());
  if (!(await chat.count())) return;
  await chat.click();
  await page.waitForTimeout(5000);
  const btn = page.locator('[data-testid="wiki-chat-analyze"]');
  if (!(await btn.count())) { console.log('[p2r] кнопки разбора нет'); return; }
  await btn.click({ force: true });
  await page.waitForTimeout(500);
  await shot(page, 'p2r-before-reload.png');
  // Перезагрузка во время разбора
  await page.reload();
  await page.waitForTimeout(1500);
  let st = '';
  for (let i = 0; i < 20; i++) {
    const b = await page.locator('body').innerText();
    const m = b.match(/[^\n]*(разбираю|идёт|готов|разбор)[^\n]*/i);
    if (m && m[0].trim()) { st = m[0].trim(); break; }
    await page.waitForTimeout(250);
  }
  console.log('[p2r] СТАТУС ПОСЛЕ RELOAD:', JSON.stringify(st));
  await shot(page, 'p2r-after-reload.png');
});
