import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { newWorld, openChat, primary, P, S } from './contextRowMock';

// Чипы действий РЕАЛЬНОГО вида «картинка» (ADR-023, шаг 2к-1). Бэкенд не нужен: контекст чата и хабы — моки
// (contextRowMock), API картинок — встроенный мок модуля (ключ localStorage cc-image-editor-mock), вклад
// `context-kind` регистрируется из страницы тем же модулем, что подключает манифест. Запуск:
//   cd frontend; npx vite --port 5322 --strictPort --host 127.0.0.1 &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5322 CA_SHOTS_DIR=../.cc-attachments/composer-actions npx playwright test e2e/image-context-actions.spec.ts

const SHOTS = process.env.CA_SHOTS_DIR || '';
test.use({ serviceWorkers: 'block' });
const D = { width: 1440, height: 900 };
const M = { width: 360, height: 780 };

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.locator('[data-composer-actions]').first().scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(SHOTS, name) });
}

// draft — у нити нет картинки; marked — на холсте есть кисть
async function registerImage(page: Page, state: 'file' | 'draft' | 'marked') {
  await page.evaluate(async ({ state, P, S }) => {
    const find = (part: string) => performance.getEntriesByType('resource').map(e => e.name).find(n => n.includes(part));
    const core = await import(/* @vite-ignore */ find('/src/lib/subsystems/registryCore.ts') ?? '/src/lib/subsystems/registryCore.ts');
    const kind = await import(/* @vite-ignore */ '/src/features/imageEditor/context/kind.tsx');
    const store = await import(/* @vite-ignore */ '/src/features/imageEditor/thread/threadStore.ts');
    const t = {
      id: 'thread-hero', file: state === 'draft' ? null : 'images/hero.png', lineage: [], draftFolder: state === 'draft' ? '' : null, stacks: [],
      currentStackId: null, currentStepId: null, settings: null, pendingJobId: null, createdAt: '2026-10-03T10:00:00Z',
    };
    store.__applyThreads(S, P, { focus: t.id, revision: 1, threads: [t] });
    if (state === 'marked') store.setThreadMarks(t.id, [{ type: 'mask', points: [[1, 1], [9, 9]], width: 4 }], { w: 100, h: 100 });
    core.registerSubsystem({ key: 'e2e-image', title: 'e2e', order: 1, noPill: true, core: true, slots: { 'context-kind': [{ name: 'image', action: kind.imageKindApi }] } });
  }, { state, P, S });
}

const actions = (page: Page) => page.locator('[data-composer-actions]');
const ids = (page: Page) => actions(page).locator('[data-action-chip]').evaluateAll(els => els.map(e => e.getAttribute('data-action-chip')));

async function open(page: Page, vp: { width: number; height: number }, o: { by?: 'human' | 'agent' } = {}) {
  newWorld({ ctx: { primary: primary({ by: o.by ?? 'human' }) } });
  await page.addInitScript(() => localStorage.setItem('cc-image-editor-mock', 'all'));
  await openChat(page, { vp });
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
}

test('картинка с файлом, 1440: чипы вместо «Чат | Картинка», «Изменить» выбрано, «Чем» и цена на месте', async ({ page }) => {
  await open(page, D);
  await registerImage(page, 'file');
  await expect(actions(page)).toBeVisible();
  expect(await ids(page)).toEqual(['__chat', 'edit', 'removeBg', 'upscale', 'outpaint', 'mark']);
  await expect(page.locator('[data-composer-modes]')).toHaveCount(0);
  await expect(actions(page).locator('[data-action-chip="edit"]')).toHaveAttribute('aria-checked', 'true');
  await expect(page.locator('[data-composer-mode-bar]')).toContainText('✦ Изменить', { timeout: 10_000 });
  await expect(page.locator('[data-context-row] [data-chip="exec"]')).toBeVisible();
  // «Дорисовать» — вопрос «Пропорции» под чипами, первое значение предвыбрано
  await actions(page).locator('[data-action-chip="outpaint"]').click();
  await expect(actions(page).getByText('Пропорции')).toBeVisible();
  await actions(page).locator('[data-action-chip="edit"]').click();
  await shot(page, 'image-chips-1440.png');
});

test('картинка с файлом, 360: чипы прокручиваются, страница не уезжает вбок', async ({ page }) => {
  await open(page, M);
  await registerImage(page, 'file');
  await expect(actions(page)).toBeVisible();
  expect(await ids(page)).toEqual(['__chat', 'edit', 'removeBg', 'upscale', 'outpaint', 'mark']);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await shot(page, 'image-chips-360.png');
});

test('с отметками: «Изменить отмеченное»; черновик: только «Нарисовать»', async ({ page }) => {
  await open(page, D);
  await registerImage(page, 'marked');
  await expect(actions(page).locator('[data-action-chip="edit"]')).toContainText('Изменить отмеченное');
  await page.reload();
  await open(page, D);
  await registerImage(page, 'draft');
  expect(await ids(page)).toEqual(['__chat', 'draw']);
  await expect(actions(page).locator('[data-action-chip="draw"]')).toHaveAttribute('aria-checked', 'true');
  await shot(page, 'image-chips-draft-1440.png');
});

test('объект агента встаёт на «Чат»', async ({ page }) => {
  await open(page, D, { by: 'agent' });
  await registerImage(page, 'file');
  await expect(actions(page).locator('[data-action-chip="__chat"]')).toHaveAttribute('aria-checked', 'true');
});
