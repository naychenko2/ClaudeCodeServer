import { test, expect } from '@playwright/test';
import { login } from './helpers';

// С11v3: кто именно на странице фичи даёт цели <40px и обрезанный текст на 360.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';

test('С11v3: виновники замеров на 360', async ({ context, playwright, baseURL }) => {
  test.setTimeout(150_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  await req.dispose();
  const page = await context.newPage();
  page.setViewportSize({ width: 360, height: 800 });
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto(`/#/project/${PID}/feature/${EPIC}`, { waitUntil: 'domcontentloaded' });
  await page.waitForTimeout(5000);
  const out = await page.evaluate(() => {
    const small = [...document.querySelectorAll('button, [role="tab"], select, a[href]')]
      .filter(e => (e as HTMLElement).offsetParent !== null)
      .map(e => { const r = (e as HTMLElement).getBoundingClientRect(); return { w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10, id: e.getAttribute('data-testid') ?? '', aria: e.getAttribute('aria-label') ?? '', text: ((e as HTMLElement).innerText || e.getAttribute('title') || '').slice(0, 40) }; })
      .filter(x => x.w > 0 && x.h > 0 && (x.w < 40 || x.h < 40));
    const clipped = [...document.querySelectorAll('*')]
      .filter(e => { const el = e as HTMLElement; return el.offsetParent !== null && el.scrollHeight > el.clientHeight + 4 && el.clientHeight > 0 && getComputedStyle(el).overflowY !== 'auto' && getComputedStyle(el).textOverflow !== 'ellipsis' && (el.textContent ?? '').trim().length > 12; })
      .slice(0, 6).map(e => { const el = e as HTMLElement; return { id: el.getAttribute('data-testid') ?? '', cls: String(el.className).slice(0, 40), sh: el.scrollHeight, ch: el.clientHeight, text: (el.textContent ?? '').slice(0, 80) }; });
    return { small, clipped };
  });
  console.log('С11v3 small:', JSON.stringify(out.small));
  console.log('С11v3 clipped:', JSON.stringify(out.clipped).slice(0, 1200));
  await page.screenshot({ path: '.cc-attachments/qa-wf-e2e/С11v3-360.png', fullPage: true });
  expect(true).toBeTruthy();
});
