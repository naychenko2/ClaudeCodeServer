import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { newWorld, openChat, primary, P, S, w } from './contextRowMock';

// «Чем» у «Изменить» и плашка «Вернуть» — на моке, который повторяет сервер: каталог с локальным поставщиком
// по умолчанию (флаг local-media-default) и fal-моделями, живой API картинок (без cc-image-editor-mock).
// Запуск (dev-сервер): cd frontend; npx vite --port 5322 --strictPort --host 127.0.0.1 &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5322 npx playwright test e2e/context-exec-cloud.spec.ts

test.use({ serviceWorkers: 'block' });
const D = { width: 1440, height: 900 };
const HERO = 'thread-hero';

// Снимок настоящего ответа сервера: fal (9 моделей) перед local, умолчание — local (флаг local-media-default)
const CATALOG = JSON.parse(fs.readFileSync(path.join(process.cwd(), 'e2e/fixtures/image-catalog-server.json'), 'utf8'));

const heroThread = () => ({
  id: HERO, file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null, currentStepId: null,
  settings: null, pendingJobId: null, createdAt: '2026-10-03T09:00:00Z', currentVersionId: 'origin', launches: [],
  versions: [{ id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, baseStepId: null, steps: [], currentStepId: null, createdAt: '2026-10-03T09:00:00Z' }],
});

// Против собранного dist (`npm run build`, статика на свободном порту, CTX_PROD=1): настоящий remote картинок, как на бою.
// На dev-сервере remote не поднят, поэтому манифест регистрируется из исходников — путь тот же, что у остальных e2e контекста
const PROD = process.env.CTX_PROD === '1';

async function register(page: Page) {
  if (PROD) return;
  await page.evaluate(async () => {
    const find = (part: string) => performance.getEntriesByType('resource').map(e => e.name).find(n => n.includes(part));
    const core = await import(/* @vite-ignore */ find('/src/lib/subsystems/registryCore.ts') ?? '/src/lib/subsystems/registryCore.ts');
    const mod = await import(/* @vite-ignore */ '/src/features/imageEditor/manifest.tsx');
    core.registerSubsystem({ ...mod.manifest, key: 'e2e-image', core: true, tab: undefined });
  });
}

async function openWithCatalog(page: Page, o: { noGit?: boolean } = {}) {
  newWorld({
    imageCatalog: CATALOG, imageRemote: PROD, noGit: o.noGit, threads: [heroThread()], flags: { 'image-editor': true, 'local-media-default': true },
    ctx: { primary: primary({ ref: { threadId: HERO, versionId: 'origin' } }) },
  });
  await openChat(page, { vp: D });
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
  await register(page);
}

const runBtn = (page: Page) => page.locator('[data-composer-mode-bar] button').last();
const execChip = (page: Page) => page.locator('[data-context-row] [data-chip="exec"]');

test('«Чем» в меню строки: секция «Облако» с fal-моделями и ценой справа', async ({ page }) => {
  await openWithCatalog(page);
  await expect(execChip(page)).toBeVisible({ timeout: 15_000 });
  await execChip(page).click();
  await expect(page.getByText('Облако', { exact: true })).toBeVisible();
  await expect(page.getByText('FLUX Kontext').first()).toBeVisible();
  await expect(page.getByText('$0.04').first()).toBeVisible();
});

test('панель «Контекст»: выбрал fal без текста — кнопка с $; «Авто» — «бесплатно»; после перезагрузки чип «Чем» помнит выбор', async ({ page }) => {
  await openWithCatalog(page);
  await page.locator('[data-context-row] [data-chip="primary"]').click();
  const panel = page.locator('[data-context-panel]');
  await expect(panel).toBeVisible({ timeout: 10_000 });
  // «Чем» в покое — одна строка; список с группами открывается меню поверх панели
  const by = panel.locator('[data-ctx-exec]');
  const pick = async (name: string | RegExp) => {
    await by.getByRole('button').click();
    await expect(page.getByText('Облако', { exact: true })).toBeVisible();
    await page.getByRole('radio', { name }).first().click();
    await expect(page.getByRole('radiogroup', { name: 'Исполнитель' })).toHaveCount(0);
  };
  await expect(by).toContainText('Авто');
  await expect(by).toContainText('бесплатно');
  await expect(runBtn(page)).toContainText('бесплатно', { timeout: 10_000 });
  await pick(/FLUX Kontext/);
  await expect(runBtn(page)).toContainText('$', { timeout: 10_000 });
  await expect(by).toContainText('FLUX Kontext');
  await expect(by).toContainText('$');
  await expect(page.locator('[data-ctx-price]')).toContainText('$');
  await expect(execChip(page)).toContainText('FLUX Kontext');
  await pick(/^Авто/);
  await expect(runBtn(page)).toContainText('бесплатно', { timeout: 10_000 });
  await expect(by).toContainText('бесплатно');
  await pick(/FLUX Kontext/);
  await expect(runBtn(page)).toContainText('$', { timeout: 10_000 });
  await page.reload();
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
  await register(page);
  await expect(execChip(page)).toContainText('FLUX Kontext', { timeout: 15_000 });
});

for (const noGit of [false, true]) {
  test(`✕ на основном объекте (${noGit ? 'проект без git' : 'с git'}): плашка «Вернуть», «Вернуть» возвращает объект`, async ({ page }) => {
    await openWithCatalog(page, { noGit });
    const chip = page.locator('[data-context-row] [data-chip="primary"]');
    await expect(chip).toBeVisible();
    await chip.locator('[data-chip-x]').click();
    await expect(chip).toHaveCount(0);
    await expect(page.locator('[data-context-notice]')).toBeVisible({ timeout: 2000 });
    await page.locator('[data-undo]').click();
    await expect(chip).toBeVisible();
    await expect(page.locator('[data-context-notice]')).toHaveCount(0);
    void w; void S;
  });
}
