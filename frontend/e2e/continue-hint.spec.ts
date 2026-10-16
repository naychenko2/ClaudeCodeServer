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

// ── Поведение: клики и совместное стояние полос (ревью варианта C) ──

// Диалог «Продолжить в новом чате»: у Modal нет role="dialog", карточку ищем по заголовку
const dialog = (page: Page) =>
  page.locator('.cc-modal-card').filter({ has: page.getByRole('heading', { name: 'Продолжить в новом чате' }) });

test('клик «Продолжить» открывает диалог', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: D });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  await hint(page).locator('[data-testid="continue-hint-open"]').click();
  await expect(dialog(page), 'диалог «Продолжить в новом чате» не открылся').toBeVisible();
  await shot(page, 'hint-1440-dialog.png');
});

test('клик ✕ «Не сейчас» — полоса исчезает', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: D });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  await hint(page).locator('[data-testid="continue-hint-snooze"]').click();
  await expect(hint(page), 'полоса не исчезла после ✕').toHaveCount(0);
});

test('без git и объекта: хост строки рисуется с подсказкой', async ({ page }) => {
  newWorld({ noGit: true, ctx: { primary: null }, feed: [bigResult] });
  await openChat(page, { vp: D });
  // Губы (строки) нет — ни ветки, ни объекта; но хост держит подсказку
  await expect(page.locator('[data-context-row]'), 'губы (строки) быть не должно').toHaveCount(0);
  await expect(
    page.locator('[data-context-row-host] [data-testid="continue-hint"]'),
    'подсказка в хосте строки',
  ).toBeVisible({ timeout: 30_000 });
  await shot(page, 'hint-nogit-1440.png');
});

test('«Вернуть» и подсказка вместе: обе полосы в одном хосте', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: D });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  // Снять объект (✕ на чипе) → «Вернуть» (UndoNotice) встаёт рядом с подсказкой
  await page.locator('[data-context-row] [data-chip="primary"] [data-chip-x]').click();
  await expect(page.locator('[data-context-notice]'), '«Вернуть» не появился').toBeVisible();
  await expect(hint(page), 'подсказка исчезла вместе с объектом').toBeVisible();
  // Обе полосы — в одном хосте, одна над другой
  await expect(page.locator('[data-context-row-host] [data-context-notice]'), '«Вернуть» в хосте').toHaveCount(1);
  await expect(page.locator('[data-context-row-host] [data-testid="continue-hint"]'), 'подсказка в хосте').toHaveCount(1);
  await shot(page, 'hint-undo-1440.png');
});

test('строка контекста прилегает к полю ввода (геометрия, не только видимость)', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: D });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-context-row]')).toBeVisible();
  // Губа строки заезжает под поле (COMPOSER_LIP.overlap = 12 px): низ строки лежит НИЖЕ верха поля,
  // а не висит над ним зазором. Измеряем перекрытие, а не только наличие.
  // boundingBox() = {x, y, width, height}: низ = y + height, верх = y.
  const rowBox = (await page.locator('[data-context-row]').boundingBox())!;
  const cardBox = (await page.locator('[data-composer-card]').boundingBox())!;
  const rowBottom = rowBox.y + rowBox.height;
  const cardTop = cardBox.y;
  const overlap = rowBottom - cardTop;
  expect(overlap, `перекрытие строки и поля = ${overlap.toFixed(1)} px (ожидалось ~12, без зазора)`)
    .toBeGreaterThanOrEqual(8);
  expect(overlap).toBeLessThanOrEqual(16);
  await shot(page, 'hint-1440-geometry.png');
});
