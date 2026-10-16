import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { newWorld, openChat, primary } from './contextRowMock';

// Подсказка «Продолжить в новом чате» — вариант C: тонкая полоса RowNotice над строкой контекста.
// Бэкенд не нужен: dist раздаётся статикой, API и хабы — моки. Запуск:
//   cd frontend; npm run build; npx vite preview --port 5322 --strictPort &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5322 CH_SHOTS_DIR=../.cc-attachments/continue-hint npx playwright test e2e/continue-hint.spec.ts

const SHOTS = process.env.CH_SHOTS_DIR || '';

const hint = (page: Page) => page.locator('[data-testid="continue-hint"]');
const D = { width: 1440, height: 900 };
const M = { width: 390, height: 844 };

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name) });
}

// result-итог с большим контекстом: подсказка включается (150K >= 60% окна 200K)
const bigResult = { kind: 'result', subtype: 'success', durationMs: 1000, numTurns: 1, contextTokens: 150_000 };

test('1440: полоса 28px, стоит над строкой, строка прилегает к полю', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: D });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  const h = Math.round((await hint(page).boundingBox())!.height);
  expect(h, 'высота полосы (десктоп) = 28').toBe(28);
  // полоса над строкой: строка (губа) под ней, не оторвана от поля
  await expect(page.locator('[data-context-row]')).toBeVisible();
  await shot(page, 'hint-1440-light.png');
});

test('1440, тёмная тема: полоса читается', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: D, theme: 'dark' });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  await shot(page, 'hint-1440-dark.png');
});

test('390: полоса 32px, короче («В новый чат»)', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: M });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  const h = Math.round((await hint(page).boundingBox())!.height);
  expect(h, 'высота полосы (390) = 32').toBe(32);
  await expect(hint(page)).toContainText('В новый чат');
  await shot(page, 'hint-390-light.png');
});

test('390, тёмная тема: полоса читается', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: M, theme: 'dark' });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  await shot(page, 'hint-390-dark.png');
});
