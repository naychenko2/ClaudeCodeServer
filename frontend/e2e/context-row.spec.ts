import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { newWorld, openChat, primary, ref, w } from './contextRowMock';

// Строка контекста над полем ввода (ADR-023, 1ф-2). Бэкенд не нужен: API контекста и хабы — моки
// (живого API контекста на момент написания нет), dist раздаётся статикой. Запуск:
//   cd frontend; npm run build; npx vite preview --port 5322 --strictPort &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5322 CR_SHOTS_DIR=../.cc-attachments/context-row npx playwright test e2e/context-row.spec.ts

const SHOTS = process.env.CR_SHOTS_DIR || '';
test.use({ serviceWorkers: 'block' });

const row = (page: Page) => page.locator('[data-context-row]');
const chip = (page: Page, kind: string) => row(page).locator(`[data-chip="${kind}"]`);
const D = { width: 1440, height: 900 };
const M = { width: 360, height: 780 };

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name) });
}

// Строка и её чипы ни на пиксель не шире номиналов: не наезжают друг на друга
async function noOverlap(page: Page) {
  const boxes = await row(page).locator('[data-chip]').evaluateAll(els => els.map(e => { const r = e.getBoundingClientRect(); return [r.left, r.right]; }));
  for (let i = 1; i < boxes.length; i++) expect(boxes[i][0], 'чипы не наезжают').toBeGreaterThanOrEqual(boxes[i - 1][1] - 0.5);
}

test('1440: строка из ветки, объекта и референсов; страница не прокручивается вбок', async ({ page }) => {
  newWorld({ ctx: { primary: primary(), refs: [ref('r1', 'Аня'), ref('r2', 'palette.png')] } });
  await openChat(page, { vp: D });
  await expect(row(page)).toBeVisible({ timeout: 30_000 });
  await expect(chip(page, 'git')).toBeVisible();
  await expect(chip(page, 'primary')).toContainText('hero.png');
  await expect(chip(page, 'primary')).toContainText('v2');
  await expect(chip(page, 'ref')).toHaveCount(2);
  await expect(chip(page, 'exec'), '«Чем» в «Чате» не рисуется').toHaveCount(0);
  expect((await row(page).boundingBox())!.height).toBe(30);
  await noOverlap(page);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await shot(page, 'row-1440.png');
});

test('360: строка 32 px прокручивается, ветка иконкой с бейджем', async ({ page }) => {
  newWorld({ ctx: { primary: primary(), refs: [ref('r1', 'Аня'), ref('r2', 'palette.png')] } });
  await openChat(page, { vp: M });
  await expect(row(page)).toBeVisible({ timeout: 30_000 });
  expect((await row(page).boundingBox())!.height).toBe(32);
  await expect(row(page)).toHaveAttribute('data-ladder-scroll', '1');
  await expect(chip(page, 'git')).not.toContainText('feat/video-editor');
  await expect(chip(page, 'git')).toContainText('3');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await shot(page, 'row-360.png');
});

test('1440, тёмная тема: строка читается', async ({ page }) => {
  newWorld({ ctx: { primary: primary({ by: 'agent' }), refs: [ref('r1', 'Аня')] } });
  await openChat(page, { vp: D, theme: 'dark' });
  await expect(row(page)).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-agent-mark]').first()).toBeVisible();
  await shot(page, 'row-1440-dark.png');
});

test('✕ снимает объект, «Вернуть» возвращает; референсы не трогаются', async ({ page }) => {
  newWorld({ ctx: { primary: primary(), refs: [ref('r1', 'Аня')] } });
  await openChat(page, { vp: D });
  await expect(chip(page, 'primary')).toBeVisible({ timeout: 30_000 });
  await chip(page, 'primary').locator('[data-chip-x]').click();
  await expect(chip(page, 'primary')).toHaveCount(0);
  await expect(chip(page, 'ref')).toHaveCount(1);
  await expect(page.locator('[data-context-notice]')).toContainText('Выбор снят — поле снова «Чат»');
  await page.locator('[data-undo]').click();
  await expect(chip(page, 'primary')).toContainText('hero.png');
  await expect(page.locator('[data-context-notice]')).toHaveCount(0);
  const puts = w().mutations.filter(m => m.method === 'PUT');
  expect(puts.map(m => (m.body as { kind: string | null }).kind)).toEqual([null, 'image']);
});

test('«+N ›» открывает список подключённого, «Очистить контекст» снимает всё', async ({ page }) => {
  // 6 референсов не влезают в строку: часть уходит в «+N ›»
  const refs = ['Аня', 'palette.png', 'mood.jpg', 'logo.png', 'sky.png', 'sun.png'].map((n, i) => ref(`r${i}`, n, { role: i === 0 ? 'char' : 'style' }));
  newWorld({ ctx: { primary: primary(), refs } });
  await openChat(page, { vp: D });
  await expect(chip(page, 'more')).toBeVisible({ timeout: 30_000 });
  await expect(chip(page, 'more')).toContainText('+');
  await chip(page, 'more').click();
  await expect(page.getByText('Подключено к ходу')).toBeVisible();
  await expect(page.getByText('образец стиля').first()).toBeVisible();
  await shot(page, 'refs-list-1440.png');
  await page.getByText('Очистить контекст').click();
  await expect(chip(page, 'primary')).toHaveCount(0);
  await expect(chip(page, 'ref')).toHaveCount(0);
  expect(w().mutations.some(m => m.method === 'DELETE' && m.path === '/')).toBe(true);
  // ветка остаётся
  await expect(chip(page, 'git')).toBeVisible();
});

test('меню ветки: «Зафиксировать только этот чат» шлёт поручение со списком сохранённых файлов', async ({ page }) => {
  newWorld({ ctx: { primary: primary() }, savedFiles: [{ path: 'images/hero.v2.png', threadKind: 'image', savedAt: '2026-10-03T09:00:00Z' }] });
  await openChat(page, { vp: D });
  await chip(page, 'git').click();
  await expect(page.getByText('Зафиксировать всё дерево')).toBeVisible();
  await expect(page.getByText('Опубликовать 1', { exact: false })).toBeVisible();
  await shot(page, 'git-menu-1440.png');
  await page.getByText('Зафиксировать только этот чат').click();
  await expect.poll(() => w().invocations.filter(i => i.target === 'SendMessage').length).toBeGreaterThan(0);
  const sent = JSON.stringify(w().invocations.find(i => i.target === 'SendMessage')!.args);
  expect(sent).toContain('Зафиксируй');
  expect(sent).toContain('images/hero.v2.png');
});

test('руки видны пилюлей в губе поля, полосы «Руки» нет', async ({ page }) => {
  newWorld({ ctx: { primary: primary() } });
  await openChat(page, { vp: D });
  await expect(page.locator('[data-hands-pill]')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-composer-strip="hands"]')).toHaveCount(0);
  await expect(page.locator('[data-git-strip]')).toHaveCount(0);
});

test('без флага: старые полосы на месте, строки и пилюли нет', async ({ page }) => {
  newWorld({ flags: { 'composer-context-row': false }, ctx: { primary: primary() } });
  await openChat(page, { vp: D });
  await expect(page.locator('[data-git-strip], [data-composer-strip]').first()).toBeVisible({ timeout: 30_000 });
  await expect(row(page)).toHaveCount(0);
  await expect(page.locator('[data-hands-pill]')).toHaveCount(0);
});

test('личный чат без объекта: строки нет', async ({ page }) => {
  newWorld({ personal: true });
  await openChat(page, { vp: D });
  await expect(page.locator('textarea').last()).toBeVisible({ timeout: 30_000 });
  await expect(row(page)).toHaveCount(0);
});
