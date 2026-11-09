import { test, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// QA-П1b · LLM-wiki — п.4: страница, созданная пакетом и удалённая откатом: экран не остаётся
// с содержимым удалённой страницы.
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

async function openWiki(page: Page) {
  await page.goto('/#/chats');
  await page.waitForTimeout(4000);
  const notesTab = page.getByText('Заметки', { exact: true }).first();
  if (await notesTab.count()) { await notesTab.click(); await page.waitForTimeout(3500); }
  const wikiTab = page.getByText('Вики', { exact: true }).first();
  if (await wikiTab.count()) { await wikiTab.click(); await page.waitForTimeout(3500); }
}

test('п.4 — страница, созданная пакетом, удалена откатом: экран не остаётся с её содержимым', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize({ width: 1280, height: 900 });
  await openWiki(page);
  const sec = page.locator('[data-testid="wiki-section-row"]').filter({ hasText: 'QA-вики' }).first();
  if (await sec.count()) { await sec.click(); await page.waitForTimeout(2500); }
  const row = page.locator('[data-testid="wiki-page-row"]').filter({ hasText: 'Правила очереди' }).first();
  console.log('[p4c] строка «Правила очереди» в каталоге:', await row.count());
  if (!(await row.count())) { console.log('[p4c] каталог:', JSON.stringify((await page.locator('body').innerText()).slice(0, 500))); return; }
  await row.click();
  await page.waitForTimeout(2500);
  await shot(page, 'p4c-page-created.png');
  const before = await page.locator('[data-testid="wiki-page"]').innerText();
  console.log('[p4c] страница открыта, длина:', before.length);
  console.log('[p4c] есть «packages_limit» в тексте:', before.includes('packages_limit'));

  // Откатываем пакет, создавший страницу (через REST — тот же вызов, что у кнопки «Откатить пакет»)
  const r = await fetch(`${process.env.PLAYWRIGHT_BASE_URL}/api/wiki/packages/a1b2c3d4e5f60718293a4b5c6d7e8f90/revert`, {
    method: 'POST', headers: { Authorization: `Bearer ${token}` },
  });
  console.log('[p4c] откат пакета:', r.status, JSON.stringify(await r.json().catch(() => null)));

  // Возвращаемся на экран вики и смотрим, что осталось
  await openWiki(page);
  const sec2 = page.locator('[data-testid="wiki-section-row"]').filter({ hasText: 'QA-вики' }).first();
  if (await sec2.count()) { await sec2.click(); await page.waitForTimeout(2500); }
  await shot(page, 'p4c-catalog-after.png');
  const cat = await page.locator('body').innerText();
  console.log('[p4c] «Правила очереди» в каталоге после отката:', cat.includes('Правила очереди'));
  const row2 = page.locator('[data-testid="wiki-page-row"]').filter({ hasText: 'Правила очереди' }).first();
  console.log('[p4c] строка страницы после отката:', await row2.count());
  if (await row2.count()) {
    await row2.click();
    await page.waitForTimeout(2500);
    await shot(page, 'p4c-page-after.png');
    const after = await page.locator('body').innerText();
    console.log('[p4c] содержимое удалённой страницы на экране:', after.includes('packages_limit'));
    console.log('[p4c] страница на экране:', await page.locator('[data-testid="wiki-page"]').count());
    console.log('[p4c] текст (400):', JSON.stringify(after.slice(0, 400)));
  }
});
