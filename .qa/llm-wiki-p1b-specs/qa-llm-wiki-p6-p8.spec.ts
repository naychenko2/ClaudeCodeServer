import { test, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// QA-П1b · LLM-wiki — п.6 («Здоровье»: «Проверить сейчас», смена раздела), п.8 (смена пользователя
// без перезагрузки), п.11 (тексты UI).
const SHOTS = process.env.QA_WIKI_SHOTS || path.join(process.cwd(), '..', '.cc-attachments', 'qa-llm-wiki-p1b');

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name), fullPage: false });
}
async function loginToken(user = 'admin'): Promise<string> {
  const r = await fetch(`${process.env.PLAYWRIGHT_BASE_URL}/api/auth/login`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: user, password: '12345' }),
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

test('п.6 — «Здоровье»: «Проверить сейчас», смена раздела не тащит находки прежнего', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken();
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize({ width: 1280, height: 900 });
  await openWiki(page);
  const health = page.getByText('Здоровье', { exact: true }).first();
  console.log('[p6] подвкладка «Здоровье»:', await health.count());
  if (!(await health.count())) return;
  await health.click();
  await page.waitForTimeout(3000);
  await shot(page, 'p6-health-before.png');
  console.log('[p6] находок до проверки:', await page.locator('[data-testid="wiki-health-row"]').count());
  console.log('[p6] счётчики:', JSON.stringify((await page.locator('[data-testid="wiki-health-counters"]').innerText().catch(() => '(нет)')).slice(0, 300)));

  const btn = page.locator('[data-testid="wiki-health-run"]').first();
  console.log('[p6] кнопка «Проверить сейчас»:', await btn.count());
  if (await btn.count()) {
    await btn.click();
    await page.waitForTimeout(6000);
    await shot(page, 'p6-health-checked.png');
    console.log('[p6] находок после проверки:', await page.locator('[data-testid="wiki-health-row"]').count());
    console.log('[p6] счётчики после:', JSON.stringify((await page.locator('[data-testid="wiki-health-counters"]').innerText().catch(() => '(нет)')).slice(0, 300)));
  }

  // Смена раздела: находки прежнего раздела не должны остаться
  const common = page.getByText('Общее', { exact: true }).first();
  console.log('[p6] раздел «Общее» в дереве:', await common.count());
  if (await common.count()) {
    await common.click();
    await page.waitForTimeout(4000);
    await shot(page, 'p6-health-other.png');
    const body = await page.locator('body').innerText();
    console.log('[p6] после смены раздела находок:', await page.locator('[data-testid="wiki-health-row"]').count());
    const m = body.match(/[^\n]*(проверк[аи][^\n]*|Здоровье раздела[^\n]*)/i);
    console.log('[p6] строка о проверке:', JSON.stringify(m?.[0] ?? '(нет)'));
  }
});

test('п.8 — смена пользователя без перезагрузки: «Открыть хронику» не открывает чужое и не залипает', async ({ page, context, playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  const token = await loginToken('admin');
  await request.dispose();
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);

  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto('/#/project/047a15f0-1bda-4567-8bbb-ed39f55c376e');
  await page.waitForTimeout(6000);
  const chat = page.getByText('QA-чат-G', { exact: true }).first();
  if (await chat.count()) { await chat.click(); await page.waitForTimeout(5000); }
  const chronicle = page.locator('[data-testid="wiki-chat-chronicle"]');
  console.log('[p8] кнопка «Открыть хронику» (admin):', await chronicle.count());
  if (!(await chronicle.count())) return;

  // Меняем пользователя на qa2 БЕЗ перезагрузки страницы
  const token2 = await loginToken('qa2');
  await page.evaluate(tk => localStorage.setItem('cc_token', tk as string), token2);
  await page.waitForTimeout(2500);
  console.log('[p8] после смены пользователя кнопка:', await page.locator('[data-testid="wiki-chat-chronicle"]').count());
  await shot(page, 'p8-after-user-switch.png');

  // Пробуем «Открыть хронику» под новым пользователем (кнопки может не быть)
  const c2 = page.locator('[data-testid="wiki-chat-chronicle"]');
  if (await c2.count()) {
    await c2.click();
    await page.waitForTimeout(3000);
    console.log('[p8] URL после клика:', page.url());
    const body = await page.locator('body').innerText();
    console.log('[p8] есть «QA-вики» (чужой раздел) в тексте:', body.includes('QA-вики'));
    console.log('[p8] текст (500):', JSON.stringify(body.slice(0, 500)));
    await shot(page, 'p8-chronicle-after-switch.png');
  } else {
    console.log('[p8] кнопки после смены пользователя нет — запрос «Открыть хронику» не отправлен');
  }
});
