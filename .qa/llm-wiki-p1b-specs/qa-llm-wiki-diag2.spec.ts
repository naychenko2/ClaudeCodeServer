import { test, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const SHOTS = process.env.QA_WIKI_SHOTS || path.join(process.cwd(), '..', '.cc-attachments', 'qa-llm-wiki-p1b');

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

test('диагностика 360: как открыть «Заметки»', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize({ width: 360, height: 780 });
  await page.goto('/#/chats');
  await page.waitForTimeout(5000);
  await shot(page, 'diag360-chats.png');

  // все интерактивные элементы с именами
  const items = await page.evaluate(() => Array.from(document.querySelectorAll('button,a[href],[role=button]'))
    .map(el => {
      const r = el.getBoundingClientRect();
      return { tag: el.tagName, name: (el.getAttribute('aria-label') || el.getAttribute('title') || el.textContent || '').trim().slice(0, 40), w: +r.width.toFixed(0), h: +r.height.toFixed(0), x: +r.x.toFixed(0), y: +r.y.toFixed(0) };
    }).filter(x => x.w > 0 && x.h > 0));
  console.log('[d360] элементы:', JSON.stringify(items.slice(0, 30)));

  // пробуем прямой deeplink на заметки с перезагрузкой
  await page.goto('/#/notes');
  await page.reload();
  await page.waitForTimeout(6000);
  console.log('[d360] URL после reload:', page.url());
  const text = await page.locator('body').innerText();
  console.log('[d360] есть «Вики»:', text.includes('Вики'));
  console.log('[d360] текст (600):', JSON.stringify(text.slice(0, 600)));
  await shot(page, 'diag360-notes-reload.png');
  const btns = await page.locator('button').allInnerTexts();
  console.log('[d360] кнопки:', JSON.stringify(btns.filter(Boolean).slice(0, 30)));
});
