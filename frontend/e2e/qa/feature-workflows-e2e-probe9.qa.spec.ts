import { test } from '@playwright/test';
import { login } from './helpers';

// Проба: что за элемент «Чаты» 32×32 на 700 в шапке проекта — ряд PillSwitch или нет.
const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
let token: string;
let request: import('@playwright/test').APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
});
test.afterAll(async () => { await request?.dispose(); });

test('probe9: контекст цели «Чаты» <40 @700', async ({ browser }) => {
  test.setTimeout(120_000);
  const ctx = await browser.newContext();
  const page = await ctx.newPage();
  page.setViewportSize({ width: 700, height: 900 });
  await ctx.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PID}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(4000);
  const info = await page.evaluate(() => {
    const out: any[] = [];
    for (const e of [...document.querySelectorAll('button')]) {
      const el = e as HTMLElement;
      if (el.offsetParent === null) continue;
      const r = el.getBoundingClientRect();
      if (r.width >= 40 && r.height >= 40) continue;
      const aria = el.getAttribute('aria-label') || '';
      const text = (el.innerText || '').trim();
      if (!aria.includes('Чаты') && !text.includes('Чаты')) continue;
      const chain: string[] = [];
      let p: HTMLElement | null = el;
      for (let i = 0; i < 5 && p; i++) { chain.push(`${p.tagName}${p.getAttribute('data-testid') ? '[' + p.getAttribute('data-testid') + ']' : ''}`); p = p.parentElement; }
      out.push({
        w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10,
        top: Math.round(r.top), left: Math.round(r.left),
        aria, text: text.slice(0, 20),
        style: (el.getAttribute('style') ?? '').slice(0, 300),
        chain,
      });
    }
    return out;
  });
  console.log('probe9:', JSON.stringify(info, null, 1).slice(0, 2500));
  await page.close();
  await ctx.close();
});
