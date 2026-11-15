import { test } from '@playwright/test';
import { login } from './helpers';

// Проба: чем открыть модалку «Вид списка» на /#/chats @1280 и на панели чатов проекта.
let token: string;
let request: import('@playwright/test').APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
});
test.afterAll(async () => { await request?.dispose(); });

test('probe10: кнопка «Вид списка» на /#/chats @1280', async ({ browser }) => {
  test.setTimeout(90_000);
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  page.setViewportSize({ width: 1280, height: 900 });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto('/#/chats', { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  const btns = await page.evaluate(() => {
    const out: any[] = [];
    for (const e of [...document.querySelectorAll('button')]) {
      const el = e as HTMLElement;
      if (el.offsetParent === null) continue;
      const t = (el.getAttribute('title') || '') + '|' + (el.getAttribute('aria-label') || '') + '|' + (el.innerText || '').slice(0, 20);
      if (/вид|групп|сортир|архив/i.test(t)) out.push({ t: t.slice(0, 80), w: Math.round(el.getBoundingClientRect().width), h: Math.round(el.getBoundingClientRect().height) });
    }
    return out;
  });
  console.log('probe10 кнопки:', JSON.stringify(btns).slice(0, 1200));
  await page.screenshot({ path: '.cc-attachments/qa-wf-e2e/qa3-probe10-chats.png', fullPage: true });
  await page.close();
  await ctx.close();
});
