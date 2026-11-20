import { test, expect, type APIRequestContext, type Page } from '@playwright/test';
import { api, login, seed, type Api } from './helpers';
import { mkdirSync } from 'node:fs';
import path from 'node:path';

// QA-приёмка фичи «Скорость ответа» (вкладка LLM панели «Ферма»).
// Стенд уже наполнен ходами на локальной модели (сборщик шлюза живёт в памяти).

const PREFIX = 'qa-llmspeed-';
// Корень проекта от файла спека: frontend/e2e/qa → корень (как в соседних qa-спеках).
// Свидетельства — в .cc-attachments (вне git).
const ROOT = path.resolve(new URL('../../..', import.meta.url).pathname);
const SHOTS = path.join(ROOT, '.cc-attachments', 'qa-llm-speed', 'shots');

let request: APIRequestContext;
let token: string;
let client: Api;
let seeded: ReturnType<typeof seed>;
let projectId: string;

interface Win { window: string; endToEndTps?: number | null; medianTps?: number | null;
  medianTtftSec?: number | null; p90TtftSec?: number | null; requests?: number }
interface Stats { answerSpeed?: { windows: Win[]; minTokensForMedian?: number } | null }

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
  seeded = seed(client);
  const stamp = Date.now();
  const p = await seeded.project(`${PREFIX}p-${stamp}`, `/tmp/${PREFIX}${stamp}`);
  projectId = p.id;
  mkdirSync(SHOTS, { recursive: true });
});

test.afterAll(async () => {
  await seeded?.cleanup();
  await request?.dispose();
});

async function openPanel(page: Page) {
  await page.addInitScript(([tk]) => {
    localStorage.setItem('cc_token', tk as string);
    localStorage.setItem('cc_role', 'admin');
    localStorage.setItem('cc_username', 'admin');
    localStorage.setItem('cc_local_model_tab', 'text');
  }, [token] as const);
  await page.goto(`/#/project/${projectId}`);
  const rail = page.locator('button[aria-label^="Ферма"]');
  await expect(rail.first(), 'значок панели в рельсе').toBeVisible({ timeout: 120_000 });
  await rail.first().click();
  const panel = page.locator('[role="complementary"][aria-label="Ферма"]');
  await expect(panel, 'панель «Ферма» открылась').toBeVisible();
  return panel;
}

const tps = (v?: number | null) => v == null ? null : `${Math.round(v)} ток/с`;

test('карточка «Скорость ответа»: числа совпадают с ответом /api/local-model/stats', async ({ page }) => {
  const stats = await client.get<Stats>('/api/local-model/stats');
  const w5 = stats.answerSpeed?.windows.find(w => w.window === '5m');
  const w1h = stats.answerSpeed?.windows.find(w => w.window === '1h');
  expect(w1h?.requests, 'в часовом окне есть запросы').toBeGreaterThan(0);

  const panel = await openPanel(page);
  const card = panel.locator('section').filter({ hasText: /^Скорость ответа/ }).first();
  await expect(card, 'карточка «Скорость ответа»').toBeVisible({ timeout: 60_000 });

  await expect(card.getByText(tps(w1h!.endToEndTps)!, { exact: true }),
    'сквозная скорость за час — как в ответе API').toBeVisible();
  await expect(card.getByText(`запросов в окне: ${w1h!.requests}`, { exact: true }),
    'число запросов в часовом окне — как в ответе API').toBeVisible();
  await expect(card.getByText('медиана по запросам, за час', { exact: true }),
    'есть медиана по запросам').toBeVisible();
  await expect(card.getByText('Время до первого токена', { exact: true }),
    'есть блок времени до первого токена').toBeVisible();
  expect(w5?.requests, 'в окне 5 минут есть запросы').toBeGreaterThan(0);
});
