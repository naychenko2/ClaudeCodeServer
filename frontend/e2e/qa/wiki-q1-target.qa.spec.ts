import { test, expect } from '@playwright/test';
import { login } from './helpers';
import type { APIRequestContext } from '@playwright/test';

const PROJECT_ID = '69b68a50-e030-4981-ab8a-c3edac4ffeeb';
const CHATS = ['8d1390ce-2eb4-413c-8a03-5ac8d8ceeee5', 'cd21502b-3268-41be-a6f1-3107570130f5'];

let request: APIRequestContext;
let token: string;

test.setTimeout(300_000);

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
});
test.afterAll(async () => { await request?.dispose(); });

test('маршрут чата: какой чат открылся и какой разбор предлагает меню «Вики»', async ({ page, context }) => {
  page.setDefaultTimeout(20000);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  for (const chatId of CHATS) {
    await page.goto(`/#/project/${PROJECT_ID}/chat/${chatId}`);
    await page.waitForTimeout(7000);
    const url = page.url();
    const head = await page.locator('header, [class*=header]').first().innerText().catch(() => '');
    const trigger = page.locator('[data-testid="wiki-chat-menu"] button[title="Действия «Вики» в этом чате"]');
    const n = await trigger.count();
    let menuTarget = null;
    if (n) {
      await trigger.first().click();
      await page.waitForTimeout(1500);
      const posts: string[] = [];
      const handler = (r: any) => { if (r.url().includes('/api/wiki/chats/')) posts.push(`${r.method()} ${r.url()}`); };
      page.on('request', handler);
      const item = page.getByRole('menuitem', { name: /Разобрать/ });
      const itemText = (await item.count()) ? (await item.innerText()).split('\n')[0] : null;
      await item.click().catch(() => undefined);
      await page.waitForTimeout(4000);
      page.off('request', handler);
      menuTarget = { itemText, posts: [...new Set(posts)] };
      await page.keyboard.press('Escape');
    }
    test.info().annotations.push({ type: 'note', description: `чат ${chatId}: URL ${url}; шапка «${head.replace(/\n/g, ' ').slice(0, 80)}»; триггеров ${n}; меню ${JSON.stringify(menuTarget)}` });
  }
  expect(true).toBe(true);
});
