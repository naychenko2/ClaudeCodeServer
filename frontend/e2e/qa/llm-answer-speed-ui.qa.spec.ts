import { test, expect } from '@playwright/test';
import { api, login, seed } from './helpers';
const PREFIX = 'qa-llmspeed-ui-';
const SHOTS = '/home/an/Sources/ClaudeCodeServer-wt/llm-answer-speed/.cc-attachments/qa-llm-speed/shots';
let request: any; let token: string; let client: any; let seeded: any; let projectId: string; let projectName: string;

test.describe.configure({ mode: 'serial' });
test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  seeded = seed(client);
  const stamp = Date.now();
  const p = await seeded.project(`${PREFIX}p-${stamp}`, `/tmp/${PREFIX}${stamp}`);
  projectId = p.id; projectName = p.name;
});
test.afterAll(async () => { await seeded?.cleanup(); await request?.dispose(); });

async function openPanel(page: any, theme: 'light' | 'dark' = 'light') {
  await page.addInitScript(([tk, th]: any) => {
    localStorage.setItem('cc_token', tk);
    localStorage.setItem('cc_role', 'admin');
    localStorage.setItem('cc_username', 'admin');
    localStorage.setItem('cc_local_model_tab', 'text');
    localStorage.setItem('theme-mode', th);
  }, [token, theme]);
  await page.goto(`/#/project/${projectId}`);
  const rail = page.locator('button[aria-label^="Ферма"]');
  const wide = (page.viewportSize()?.width ?? 1280) >= 700;
  if (wide) {
    await expect(rail.first(), 'значок панели в рельсе').toBeVisible({ timeout: 120_000 });
    await rail.first().click();
  } else {
    const more = page.locator('button[aria-label="Ещё"]');
    await expect(more.first(), 'меню «Ещё»').toBeVisible({ timeout: 120_000 });
    await more.first().click();
    const item = page.getByText('Ферма', { exact: true }).first();
    await expect(item, 'пункт «Ферма»').toBeVisible({ timeout: 15_000 });
    await item.click();
  }
  const panel = page.locator('[role="complementary"][aria-label="Ферма"]');
  await expect(panel, 'панель «Ферма» открылась').toBeVisible();
  return panel;
}

test('подписи: «скорость генерации (пока пишет)», «Скорость генерации, ток/с», старой подписи нет', async ({ page }) => {
  const panel = await openPanel(page);
  await expect(panel.getByText('скорость генерации (пока пишет), ток/с, за последнюю минуту'),
    'подпись в карточке «Скорость»').toBeVisible();
  await expect(panel.getByText('Скорость генерации, ток/с', { exact: true }),
    'подпись главной линии графика').toBeVisible();
  const panelText = await panel.innerText();
  expect(panelText, 'старой подписи «на запрос, ток/с» в панели нет').not.toContain('на запрос, ток/с');
});

test('«Кто сейчас на модели»: подсказка о сквозной скорости у строки чата', async ({ page }) => {
  const chatName = `${PREFIX}who-${Date.now()}`;
  await client.post(`/api/projects/${projectId}/sessions`, { name: chatName, model: 'qwen3.8-27b' });
  const { execFileSync } = await import('node:child_process');
  const gen = '/home/an/Sources/ClaudeCodeServer-wt/llm-answer-speed/.cc-attachments/qa-llm-speed/gen.mjs';
  execFileSync('node', [gen], {
    env: { ...process.env, QA_PROJECT: projectName, QA_CHAT: chatName, QA_COUNT: '1', QA_SLOW: '1' },
    stdio: 'ignore',
  });
  const panel = await openPanel(page);
  const who = panel.locator('section').filter({ hasText: /сейчас на модели/i }).first();
  await expect(who, 'карточка «Кто сейчас на модели»').toBeVisible({ timeout: 60_000 });
  await expect(who.locator('[title*="сквозная скорость"]'),
    'подсказка у скорости строки чата').toHaveCount(1, { timeout: 90_000 });
});

test('второй стенд: свои числа, чужие запросы не подмешаны', async ({ page }) => {
  const main = await client.get('/api/local-model/stats');
  const qa2 = await client.get('/api/local-model/stats?engine=qa2');
  const m = main.answerSpeed.windows.find((w: any) => w.window === '1h');
  const q = qa2.answerSpeed.windows.find((w: any) => w.window === '1h');
  expect(m.requests, 'у основного стенда есть запросы').toBeGreaterThan(0);
  expect(q.requests, 'у второго стенда есть свои запросы').toBeGreaterThan(0);
  expect(m.requests, 'счётчики стендов разные').not.toBe(q.requests);
  const panel = await openPanel(page);
  const seg = panel.getByRole('button', { name: /Второй стенд/ });
  await expect(seg, 'сегмент второго стенда').toBeVisible({ timeout: 30_000 });
  await seg.click();
  const card = panel.locator('section').filter({ hasText: /^Скорость ответа/ }).first();
  await expect(card.getByText(`запросов в окне: ${q.requests}`, { exact: true }),
    'число запросов второго стенда — своё').toBeVisible({ timeout: 30_000 });
});
