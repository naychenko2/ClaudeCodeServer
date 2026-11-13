// QA · ночь вики на нескольких исполнителях: пустой список у владельца в интерфейсе.
// Когда исполнители не назначены, в блоке ночи нет перечня — остаётся подсказка, где их
// назначают (признак «Исполнители: …» живёт ровно при непустом списке стора).

import { test, expect, type APIRequestContext, type Page } from '@playwright/test';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5000';
test.setTimeout(180_000);

let request: APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
});

test.afterAll(async () => {
  await request?.dispose();
});

test('пустой список: у владельца подсказка вместо перечня, разбивки нет', async ({ browser }) => {
  const user = process.env.E2E_USER || 'admin';
  const pass = process.env.E2E_PASS || '12345';
  const r = await request.post('/api/auth/login', { data: { username: user, password: pass } });
  expect(r.ok(), `вход админа (${r.status()})`).toBeTruthy();
  const token = (await r.json()).token as string;

  // Флаг llm-wiki у админа: без него вкладки вики и блока ночи нет вовсе.
  const flag = await request.put('/api/feature-flags/llm-wiki', {
    headers: { Authorization: `Bearer ${token}` }, data: { enabled: true },
  });
  expect(flag.ok(), `флаг llm-wiki включён (${flag.status()})`).toBeTruthy();

  const settings = await request.get('/api/wiki/settings', { headers: { Authorization: `Bearer ${token}` } });
  expect(settings.ok(), `настройки вики читаются (${settings.status()})`).toBeTruthy();
  const s = await settings.json() as { nightly: { executors: unknown[] } };
  expect(s.nightly.executors, 'список исполнителей пуст').toEqual([]);

  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  try {
    const page: Page = await ctx.newPage();
    await page.goto(`${BASE}/#/home`);
    await page.evaluate(async (t) => {
      localStorage.setItem('cc_token', t);
      const me = await fetch('/api/auth/me', { headers: { Authorization: `Bearer ${t}` } });
      const m = await me.json() as { userId?: string; role?: string };
      localStorage.setItem('cc_user_id', m.userId || '');
      if (m.role) localStorage.setItem('cc_role', m.role);
    }, token);
    await page.reload();
    await expect(page.locator('#root')).not.toBeEmpty();

    await page.getByRole('button', { name: 'Заметки', exact: true }).first().click({ timeout: 30_000 });
    await page.getByRole('button', { name: 'Настройки', exact: true }).first().click({ timeout: 30_000 });
    await atEmpty(page);
  } finally {
    await ctx.close();
  }
});

async function atEmpty(page: Page) {
  // При непустом списке строка несёт testid wiki-nightly-executors-note и перечень;
  // при пустом testid нет вовсе — остаётся подсказка, где назначают исполнителей.
  const fallback = page.getByText('Назначает администратор в «Поставщиках моделей».');
  await expect(fallback, 'подсказка о месте назначения').toBeVisible({ timeout: 20_000 });
  await expect(page.getByText(/^Исполнители: /), 'перечня исполнителей нет').toHaveCount(0);
  await expect(page.locator('[data-testid="wiki-nightly-executors-note"]'), 'строки с перечнем нет').toHaveCount(0);
  await expect(page.locator('[data-testid="wiki-nightly-executors"]'), 'разбивки последней ночи нет').toHaveCount(0);
}
