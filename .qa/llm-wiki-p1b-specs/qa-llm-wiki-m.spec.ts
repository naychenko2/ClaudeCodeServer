import { test, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// QA-П1b · LLM-wiki — мобильный экран «Разделы» (360) и шапка чата с тремя кнопками.
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

test('мобильный 360 — экран «Разделы» вики и шапка чата', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize({ width: 360, height: 780 });
  await page.goto('/#/chats');
  await page.waitForTimeout(4500);

  // «Заметки» на узком экране — в меню «Ещё»
  const more = page.getByRole('button', { name: 'Ещё' }).first();
  console.log('[m] кнопка «Ещё»:', await more.count());
  await more.click();
  await page.waitForTimeout(900);
  await shot(page, 'm-more-menu.png');
  const notesItem = page.getByText('Заметки', { exact: true }).first();
  console.log('[m] пункт «Заметки» в меню:', await notesItem.count());
  await notesItem.click();
  await page.waitForTimeout(4500);
  await shot(page, 'm-notes.png');

  const wikiTab = page.getByText('Вики', { exact: true }).first();
  console.log('[m] вкладка «Вики» в переключателе:', await wikiTab.count());
  if (await wikiTab.count()) {
    await wikiTab.click();
    await page.waitForTimeout(3500);
    await shot(page, 'm-wiki-tab.png');
    const sw = page.locator('[data-testid="wiki-section-switch"]');
    console.log('[m] кнопка «Раздел ▸»:', await sw.count());
    if (await sw.count()) {
      await sw.click();
      await page.waitForTimeout(1800);
      await shot(page, 'm-wiki-sections.png');
      console.log('[m] строк разделов:', await page.locator('[data-testid="wiki-section-row"]').count());
      const back = page.getByText('Назад', { exact: true }).first();
      console.log('[m] кнопка «Назад»:', await back.count());
    }
  }

  // Шапка чата на 360: три кнопки и их переполнение
  await page.goto(`/#/project/${PID}`);
  await page.waitForTimeout(5000);
  const chat = page.getByText('QA-чат-G', { exact: true }).first();
  if (await chat.count()) { await chat.click(); await page.waitForTimeout(5000); }
  await shot(page, 'm-chat-header.png');
  const btns = page.locator('[data-testid^="wiki-chat-"]');
  console.log('[m] кнопок вики в шапке:', await btns.count());
  for (const b of await btns.all()) {
    const box = await b.boundingBox();
    const txt = (await b.innerText()).slice(0, 30);
    console.log('[m] кнопка', JSON.stringify(txt), 'box', JSON.stringify(box),
      'правый край за экраном:', box ? box.x + box.width > 360 : '?');
  }
  // Проверяем: есть ли горизонтальная прокрутка у контейнера шапки
  const ov = await page.evaluate(() => {
    const els = Array.from(document.querySelectorAll('*')) as HTMLElement[];
    const bad = els.filter(e => e.scrollWidth > e.clientWidth + 2 && e.clientWidth > 0)
      .map(e => ({ sel: e.tagName + '.' + (e.className || '').toString().slice(0, 40), sw: e.scrollWidth, cw: e.clientWidth, ox: getComputedStyle(e).overflowX }));
    return { doc: document.documentElement.scrollWidth > document.documentElement.clientWidth, bad: bad.slice(0, 10) };
  });
  console.log('[m] документ переполняется:', ov.doc);
  console.log('[m] элементы с прокруткой:', JSON.stringify(ov.bad));
});
