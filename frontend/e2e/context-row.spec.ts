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

test('1440: ветка при свободном месте без многоточия, имя целиком', async ({ page }) => {
  newWorld({ ctx: { primary: primary(), refs: [ref('r1', 'Аня')] } });
  await openChat(page, { vp: D });
  await expect(row(page)).toBeVisible({ timeout: 30_000 });
  const cut = await chip(page, 'git').locator('span').first().evaluate(() => {
    const el = document.querySelector('[data-chip="git"] span') as HTMLElement | null;
    return el ? el.scrollWidth > el.clientWidth + 1 : false;
  });
  expect(cut, 'имя ветки не режется').toBe(false);
  await expect(chip(page, 'git')).toContainText('feat/video-editor');
  await shot(page, 'git-1440.png');
});

test('360: губа строками «Где» / «С чем» / «Подключено», без прокрутки, ветка с именем', async ({ page }) => {
  newWorld({ ctx: { primary: primary(), refs: [ref('r1', 'Аня'), ref('r2', 'palette.png')] } });
  await openChat(page, { vp: M });
  await expect(row(page)).toBeVisible({ timeout: 30_000 });
  await expect(row(page)).toHaveAttribute('data-ladder-scroll', '0');
  const lines = await row(page).locator('[data-row-line]').evaluateAll(els => els.map(e => e.getAttribute('data-row-line')));
  expect(lines).toEqual(['where', 'what', 'refs']);
  for (const l of await row(page).locator('[data-row-line]').all())
    expect(await l.evaluate(el => el.scrollWidth <= el.clientWidth + 1), 'строка без боковой прокрутки').toBe(true);
  await expect(chip(page, 'git')).toContainText('feat/video');
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

test('чипа «Руки» в поле ввода нет, полосы «Руки» тоже', async ({ page }) => {
  newWorld({ ctx: { primary: primary() } });
  await openChat(page, { vp: D });
  await expect(page.locator('textarea').last()).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-composer-strip="hands"]')).toHaveCount(0);
  await expect(page.locator('[data-git-strip]')).toHaveCount(0);
});

test('личный чат без объекта: строки нет', async ({ page }) => {
  newWorld({ personal: true });
  await openChat(page, { vp: D });
  await expect(page.locator('textarea').last()).toBeVisible({ timeout: 30_000 });
  await expect(row(page)).toHaveCount(0);
});

// ── Панель «Контекст» (1ф-3) ──

const panel = (page: Page) => page.locator('[data-context-panel]');
const SHOTS_PANEL = process.env.CP_SHOTS_DIR || '';
async function shotPanel(page: Page, name: string) {
  if (!SHOTS_PANEL) return;
  fs.mkdirSync(SHOTS_PANEL, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS_PANEL, name) });
}

test('чип объекта открывает панель «Контекст», повторный клик мигает «С чем»; кнопка панели одна', async ({ page }) => {
  newWorld({ ctx: { primary: primary(), refs: [ref('r1', 'Аня', { role: 'char' }), ref('r2', 'palette.png')] } });
  await openChat(page, { vp: D });
  await expect(chip(page, 'primary')).toBeVisible({ timeout: 30_000 });
  await expect(panel(page)).toHaveCount(0);
  await chip(page, 'primary').click();
  await expect(panel(page)).toBeVisible();
  await expect(page.locator('[data-ctx-section="where"]')).toContainText('feat/video-editor');
  await expect(page.locator('[data-ctx-section="with"]')).toContainText('hero.png');
  await expect(page.locator('[data-ctx-section="with"]')).toContainText('v2');
  // «Чат»: «Чем» и «Параметры» — пустые состояния
  await expect(page.locator('[data-ctx-section="by"]')).toContainText('Исполнитель появится, когда в поле выбрано действие');
  await expect(page.locator('[data-ctx-section="plus"] [data-ctx-ref]')).toHaveCount(2);
  await expect(page.locator('[data-ctx-card]')).toHaveAttribute('data-ctx-flash', '0');
  await shotPanel(page, 'panel-1440.png');
  // повторный клик по чипу при открытой панели мигает карточкой
  await chip(page, 'primary').click();
  await expect(page.locator('[data-ctx-card]')).toHaveAttribute('data-ctx-flash', '1');
  await expect(page.locator('[data-ctx-card]')).toHaveAttribute('data-ctx-flash', '0', { timeout: 5_000 });
  // в рельсе одна кнопка панели генерации; отсутствие «Картинок» и «Звука» при включённых вертикалях
  // держит юнит contextPanelHost.test.ts (в моке вертикали не загружаются)
  await expect(page.getByRole('button', { name: /«Контекст»/ })).toHaveCount(1);
});

test('панель: ✕ у карточки снимает объект, ✕ у пилюли отключает референс, «Очистить контекст»', async ({ page }) => {
  newWorld({ ctx: { primary: primary(), refs: [ref('r1', 'Аня', { role: 'char' }), ref('r2', 'palette.png')] } });
  await openChat(page, { vp: D });
  await chip(page, 'primary').click();
  await expect(panel(page)).toBeVisible();
  await page.locator('[data-ctx-section="plus"] [data-ctx-ref]').first().getByRole('button', { name: /Отключить/ }).click();
  await expect(page.locator('[data-ctx-section="plus"] [data-ctx-ref]')).toHaveCount(1);
  await page.locator('[data-ctx-clear]').click();
  await expect(page.locator('[data-ctx-section="with"]')).toContainText('Ничего не выбрано');
  expect(w().mutations.some(m => m.method === 'DELETE' && m.path === '/')).toBe(true);
});

test('панель на 360: шторка по просьбе, секции читаются, страница не уезжает вбок', async ({ page }) => {
  newWorld({ ctx: { primary: primary(), refs: [ref('r1', 'Аня', { role: 'char' })] } });
  await openChat(page, { vp: M });
  await expect(chip(page, 'primary')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-gen-sheet]')).toHaveCount(0);
  await chip(page, 'primary').click();
  await expect(page.locator('[data-gen-sheet]')).toBeVisible();
  await expect(page.locator('[data-ctx-section="with"]')).toContainText('hero.png');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await shotPanel(page, 'panel-360.png');
});

