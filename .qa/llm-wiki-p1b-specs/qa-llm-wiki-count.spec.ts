import { test, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// QA-П1b · LLM-wiki — п.11: согласование чисел в диалоге отката (2 записи) на живых данных.
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

test('п.11 — «2 применённые записи» в диалоге отката', async ({ page, context, playwright, baseURL }) => {
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
  const journal = page.getByText('Журнал', { exact: true }).first();
  if (await journal.count()) { await journal.click(); await page.waitForTimeout(3000); }

  const rows = page.locator('[data-testid="wiki-journal-row"]');
  console.log('[cnt] строк журнала:', await rows.count());
  // Ищем записи источника, у которого 2 применённые записи
  let found = false;
  for (let i = 0; i < Math.min(await rows.count(), 15); i++) {
    const txt = await rows.nth(i).innerText();
    if (!txt.includes('QA-лимит-3')) continue;
    await rows.nth(i).click();
    await page.waitForTimeout(1500);
    const rb = page.locator('[data-testid="wiki-journal-retract"]');
    if (!(await rb.count())) continue;
    await rb.click();
    await page.waitForTimeout(1200);
    const dlg = page.locator('.cc-overlay').last();
    const text = await dlg.innerText();
    console.log('[cnt] ДИАЛОГ:', JSON.stringify(text.slice(0, 400)));
    console.log('[cnt] есть «2 применённые записи»:', text.includes('2 применённые записи'));
    console.log('[cnt] есть «2 применённых записей» (неверно):', text.includes('2 применённых записей'));
    await shot(page, 'cnt-dialog-two.png');
    found = true;
    break;
  }
  console.log('[cnt] диалог для источника с 2 записями найден:', found);
});
