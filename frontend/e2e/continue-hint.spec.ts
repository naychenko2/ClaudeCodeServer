import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { newWorld, openChat, primary } from './contextRowMock';

// Подсказка «Продолжить в новом чате» — вариант 2 · Ушко (LipEar): закладка у правого края
// верхней кромки, растёт из губы (есть строка) или из поля ввода (нет ни ветки, ни объекта).
// Бэкенд не нужен: dist раздаётся статикой, API и хабы — моки. Запуск:
//   cd frontend; npm run build; npx vite preview --port 5322 --strictPort &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5322 CH_SHOTS_DIR=../.cc-attachments/continue-hint-ear npx playwright test e2e/continue-hint.spec.ts

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

test('1440: ушко 23px, стоит у правого края над строкой, строка прилегает к полю', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: D });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  const h = Math.round((await hint(page).boundingBox())!.height);
  expect(h, 'высота ушка (десктоп) = 23').toBe(23);
  // ушко над строкой: строка (губа) под ним, не оторвана от поля
  await expect(page.locator('[data-context-row]')).toBeVisible();
  await shot(page, 'ear-1440-light.png');
});

test('1440, тёмная тема: ушко читается', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: D, theme: 'dark' });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  await shot(page, 'ear-1440-dark.png');
});

test('390: ушко 31px, короче («В новый чат»)', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: M });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  const h = Math.round((await hint(page).boundingBox())!.height);
  expect(h, 'высота ушка (390) = 31').toBe(31);
  await expect(hint(page)).toContainText('В новый чат');
  await shot(page, 'ear-390-light.png');
});

test('390, тёмная тема: ушко читается', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: M, theme: 'dark' });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  await shot(page, 'ear-390-dark.png');
});

// ── Поведение: клики и совместное стояние ушка с полосой «Вернуть» ──

// Диалог «Продолжить в новом чате»: у Modal нет role="dialog", карточку ищем по заголовку
const dialog = (page: Page) =>
  page.locator('.cc-modal-card').filter({ has: page.getByRole('heading', { name: 'Продолжить в новом чате' }) });

test('клик «Продолжить» открывает диалог', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: D });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  await hint(page).locator('[data-testid="continue-hint-open"]').click();
  await expect(dialog(page), 'диалог «Продолжить в новом чате» не открылся').toBeVisible();
  await shot(page, 'ear-1440-dialog.png');
});

test('клик ✕ «Не сейчас» — ушко исчезает', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: D });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  await hint(page).locator('[data-testid="continue-hint-snooze"]').click();
  await expect(hint(page), 'ушко не исчезло после ✕').toHaveCount(0);
});

test('без git и объекта: ушко растёт из кромки поля ввода', async ({ page }) => {
  newWorld({ noGit: true, ctx: { primary: null }, feed: [bigResult] });
  await openChat(page, { vp: D });
  // Губы (строки) нет — ни ветки, ни объекта; ушко (base = field) держится хостом
  await expect(page.locator('[data-context-row]'), 'губы (строки) быть не должно').toHaveCount(0);
  await expect(
    page.locator('[data-context-row-host] [data-testid="continue-hint"]'),
    'ушко в хосте строки',
  ).toBeVisible({ timeout: 30_000 });
  // Стык: низ ушка прилегает к верху поля ввода (карточки), зазор ≤ 5 px
  const earBox = (await page.locator('[data-context-row-host] [data-testid="continue-hint"]').boundingBox())!;
  const cardBox = (await page.locator('[data-composer-card]').boundingBox())!;
  const gap = cardBox.y - (earBox.y + earBox.height);
  expect(gap, `зазор низ-ушка → верх-поля = ${gap.toFixed(1)} px (ожидалось ≤ 5, без отлипания)`)
    .toBeGreaterThanOrEqual(-2);
  expect(gap).toBeLessThanOrEqual(5);
  await shot(page, 'ear-norow-1440.png');
});

// Скриншоты «без губы» (ушко на кромке поля) в оставшихся комбинациях. Каждый — отдельный
// тест со свежей страницей (Playwright даёт на тест новую page; переиспользование page в цикле
// даёт ложный «hidden», т.к. предыдущая раскладка не сбрасывается).
test('без губы, 1440 тёмная: ушко на кромке поля', async ({ page }) => {
  newWorld({ noGit: true, ctx: { primary: null }, feed: [bigResult] });
  await openChat(page, { vp: D, theme: 'dark' });
  await expect(page.locator('[data-context-row-host] [data-testid="continue-hint"]'), 'ушко (без губы) 1440-dark')
    .toBeVisible({ timeout: 30_000 });
  await shot(page, 'ear-norow-1440-dark.png');
});

test('без губы, 390 светлая: ушко на кромке поля', async ({ page }) => {
  newWorld({ noGit: true, ctx: { primary: null }, feed: [bigResult] });
  await openChat(page, { vp: M });
  await expect(page.locator('[data-context-row-host] [data-testid="continue-hint"]'), 'ушко (без губы) 390-light')
    .toBeVisible({ timeout: 30_000 });
  await shot(page, 'ear-norow-390-light.png');
});

test('без губы, 390 тёмная: ушко на кромке поля', async ({ page }) => {
  newWorld({ noGit: true, ctx: { primary: null }, feed: [bigResult] });
  await openChat(page, { vp: M, theme: 'dark' });
  await expect(page.locator('[data-context-row-host] [data-testid="continue-hint"]'), 'ушко (без губы) 390-dark')
    .toBeVisible({ timeout: 30_000 });
  await shot(page, 'ear-norow-390-dark.png');
});

test('«Вернуть» и ушко вместе: полоса над ушком в одном хосте', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: D });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  // Снять объект (✕ на чипе) → «Вернуть» (UndoNotice) встаёт рядом с подсказкой
  await page.locator('[data-context-row] [data-chip="primary"] [data-chip-x]').click();
  await expect(page.locator('[data-context-notice]'), '«Вернуть» не появился').toBeVisible();
  await expect(hint(page), 'ушко исчезло вместе с объектом').toBeVisible();
  // Полоса «Вернуть» и ушко — в одном хосте, одна над другим
  await expect(page.locator('[data-context-row-host] [data-context-notice]'), '«Вернуть» в хосте').toHaveCount(1);
  await expect(page.locator('[data-context-row-host] [data-testid="continue-hint"]'), 'ушко в хосте').toHaveCount(1);
  await shot(page, 'ear-undo-1440.png');
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
  await shot(page, 'ear-1440-geometry.png');
});

// Стык ушка с губой: низ ушка (margin-bottom: -1px + полоска стыка) прилегает к верху губы,
// а не висит над ней зазором. Ушко — alignSelf: flex-end, поэтому правый край у правого края губы.
test('ушко слито с верхней кромкой губы (стык без зазора, правый край у края)', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, feed: [bigResult] });
  await openChat(page, { vp: D });
  await expect(hint(page)).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-context-row]')).toBeVisible();
  const earBox = (await hint(page).boundingBox())!;
  const rowBox = (await page.locator('[data-context-row]').boundingBox())!;
  // Низ ушка прилегает к верху губы: зазор ≤ 5 px (полоска стыка закрывает рамку)
  const gap = rowBox.y - (earBox.y + earBox.height);
  expect(gap, `зазор низ-ушка → верх-губы = ${gap.toFixed(1)} px (ожидалось ≤ 5, слитый стык)`)
    .toBeGreaterThanOrEqual(-2);
  expect(gap).toBeLessThanOrEqual(5);
  // Ушко у правого края: правая кромка ушка в пределах 24 px от правого края губы
  const rightGap = (rowBox.x + rowBox.width) - (earBox.x + earBox.width);
  expect(rightGap, `отступ правого края ушка от края губы = ${rightGap.toFixed(1)} px (ожидалось ≤ 24)`)
    .toBeGreaterThanOrEqual(0);
  expect(rightGap).toBeLessThanOrEqual(24);
  await shot(page, 'ear-1440-seam.png');
});
