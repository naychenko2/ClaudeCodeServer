import { test, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// QA-П1b · LLM-wiki — «Разобрать в базу» у заметки: личная заметка (понятный отказ).
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

test('п.7 — «Разобрать в базу» у личной заметки: понятный текст отказа', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  const reqs: string[] = [];
  page.on('request', r => { if (r.url().includes('/api/wiki')) reqs.push(`${r.method()} ${r.url()}`); });

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/#/chats');
  await page.waitForTimeout(4000);
  const notesTab = page.getByText('Заметки', { exact: true }).first();
  if (await notesTab.count()) { await notesTab.click(); await page.waitForTimeout(4000); }
  await shot(page, 'notes-personal.png');

  // Личная заметка: «QA-заметка для разбора»
  const note = page.getByText('QA-заметка для разбора', { exact: false }).first();
  console.log('[note] личная заметка найдена:', await note.count());
  if (await note.count()) {
    await note.click();
    await page.waitForTimeout(3000);
    await shot(page, 'note-view.png');
    const btn = page.getByTitle('Разобрать в базу: Хранитель соберёт пакет правок вики');
    console.log('[note] кнопка «Разобрать в базу»:', await btn.count());
    if (await btn.count()) {
      await btn.click();
      // Тост живёт несколько секунд — ловим по заголовку «Разобрать в базу»
      const toast = page.getByText('Разобрать в базу', { exact: true }).first();
      await toast.waitFor({ state: 'visible', timeout: 15000 }).catch(() => {});
      await page.waitForTimeout(500);
      await shot(page, 'note-ingest-refusal.png');
      const body = await page.locator('body').innerText();
      const i = body.indexOf('Разобрать в базу');
      console.log('[note] ТЕКСТ ТОСТА:', JSON.stringify(i >= 0 ? body.slice(i, i + 400) : '(тост не найден)'));
      console.log('[note] есть «packages_limit» в тексте:', body.includes('packages_limit'));
      console.log('[note] есть «открытых пакетов уже 20»:', body.includes('открытых пакетов уже 20'));
      console.log('[note] есть «личный раздел» в тексте:', body.includes('личный раздел'));
      console.log('[note] есть «Личный раздел пока не разбирается»:', body.includes('Личный раздел пока не разбирается'));
      console.log('[note] есть «Ф2» в тексте:', body.includes('Ф2'));
    }
  }
  console.log('[note] запросы /api/wiki:', JSON.stringify(reqs));
});
