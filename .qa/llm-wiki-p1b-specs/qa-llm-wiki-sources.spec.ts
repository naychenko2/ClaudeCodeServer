import { test, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// QA-П1b · LLM-wiki — страница вики: сноски, поповер с цитатой, блок источников.
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

test('страница вики: сноски и поповер с цитатой', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/#/chats');
  await page.waitForTimeout(4000);
  const notesTab = page.getByText('Заметки', { exact: true }).first();
  if (await notesTab.count()) { await notesTab.click(); await page.waitForTimeout(3500); }
  const wikiTab = page.getByText('Вики', { exact: true }).first();
  if (await wikiTab.count()) { await wikiTab.click(); await page.waitForTimeout(3500); }
  const sec = page.locator('[data-testid="wiki-section-row"]').filter({ hasText: 'QA-вики' }).first();
  if (await sec.count()) { await sec.click(); await page.waitForTimeout(2500); }
  const row = page.locator('[data-testid="wiki-page-row"]').filter({ hasText: 'Правила разбора' }).first();
  console.log('[src] страница найдена:', await row.count());
  if (!(await row.count())) return;
  await row.click();
  await page.waitForTimeout(2500);
  await shot(page, 'src-page.png');

  const pageBody = page.locator('[data-testid="wiki-page"]');
  const text = await pageBody.innerText();
  console.log('[src] длина страницы:', text.length);
  console.log('[src] блок источников wiki-sources:', await page.locator('[data-testid="wiki-sources"]').count());
  console.log('[src] число сносок в тексте:', (text.match(/\[\^s\d+\]/g) || []).length);
  console.log('[src] есть «Хроники» в тексте:', text.includes('Хроники'));

  // Сноска: ищем элемент-ссылку сноски и наводим
  const sup = page.locator('[data-testid="wiki-page"] sup, [data-testid="wiki-page"] sup a, [data-testid="wiki-page"] a[href^="#"]');
  console.log('[src] кандидатов-сносок:', await sup.count());
  if (await sup.count()) {
    await sup.first().hover();
    await page.waitForTimeout(1500);
    await shot(page, 'src-footnote-hover.png');
    const body = await page.locator('body').innerText();
    const i = body.indexOf('—');
    console.log('[src] поповер (хвост страницы):', JSON.stringify(body.slice(-500)));
  }
});
