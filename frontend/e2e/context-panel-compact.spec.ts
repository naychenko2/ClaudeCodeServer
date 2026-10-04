import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { newWorld, openChat, primary } from './contextRowMock';
import { openVideoChat, primaryOf } from './contextVideoMock';
import { CATALOG as VIDEO_CATALOG } from './videoPanelMock';

// Компактная панель «Контекст»: «Чем» одной строкой, параметры без расползания, подвал виден без прокрутки.
// Каталог — настоящий снимок ответа сервера. Запуск (dev-сервер):
//   cd frontend; npx vite --port 5322 --strictPort --host 127.0.0.1 &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5322 CP_SHOTS_DIR=../.cc-attachments/context-panel-compact npx playwright test e2e/context-panel-compact.spec.ts

test.use({ serviceWorkers: 'block' });
const SHOTS = process.env.CP_SHOTS_DIR || '';
const VPS = [{ name: '1440', vp: { width: 1440, height: 900 } }, { name: '360', vp: { width: 360, height: 780 } }] as const;
const HERO = 'thread-hero';
const CATALOG = JSON.parse(fs.readFileSync(path.join(process.cwd(), 'e2e/fixtures/image-catalog-server.json'), 'utf8'));

const heroThread = () => ({
  id: HERO, file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null, currentStepId: null,
  settings: null, pendingJobId: null, createdAt: '2026-10-03T09:00:00Z', currentVersionId: 'origin', launches: [],
  versions: [{ id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, baseStepId: null, steps: [], currentStepId: null, createdAt: '2026-10-03T09:00:00Z' }],
});

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name) });
}

async function registerImage(page: Page) {
  await page.evaluate(async () => {
    const find = (part: string) => performance.getEntriesByType('resource').map(e => e.name).find(n => n.includes(part));
    const core = await import(/* @vite-ignore */ find('/src/lib/subsystems/registryCore.ts') ?? '/src/lib/subsystems/registryCore.ts');
    const mod = await import(/* @vite-ignore */ '/src/features/imageEditor/manifest.tsx');
    core.registerSubsystem({ ...mod.manifest, key: 'e2e-image', core: true, tab: undefined });
  });
}

// Тело панели не прокручивается: высота содержимого равна видимой, а подвал с ценой и кнопкой в окне
async function expectFits(page: Page, vp: { width: number; height: number }) {
  const body = await page.locator('[data-context-panel]').evaluate(el => {
    const sc = el.parentElement as HTMLElement;
    return { scroll: sc.scrollHeight, client: sc.clientHeight };
  });
  expect(body.scroll, `тело панели: ${body.scroll} против видимых ${body.client}`).toBeLessThanOrEqual(body.client + 1);
  for (const sel of ['[data-ctx-price]', '[data-ctx-run] button']) {
    const box = await page.locator(sel).boundingBox();
    expect(box, sel).not.toBeNull();
    expect(box!.y + box!.height, sel).toBeLessThanOrEqual(vp.height);
  }
}

for (const { name, vp } of VPS) {
  for (const theme of ['light', 'dark'] as const) {
    test(`«Изменить» с облаком · ${name} · ${theme}: «Чем» одной строкой, меню с группами, всё влезает без прокрутки`, async ({ page }) => {
      newWorld({
        imageCatalog: CATALOG, threads: [heroThread()], flags: { 'image-editor': true, 'local-media-default': true },
        ctx: { primary: primary({ ref: { threadId: HERO, versionId: 'origin' } }) },
      });
      await openChat(page, { vp, theme });
      await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
      await registerImage(page);
      await page.locator('[data-context-row] [data-chip="primary"]').click();
      const panel = page.locator('[data-context-panel]');
      await expect(panel).toBeVisible({ timeout: 10_000 });
      const by = panel.locator('[data-ctx-exec]');
      await expect(by).toContainText('Авто', { timeout: 10_000 });
      // В покое списка нет, а строка занимает одну строку, не полторы сотни пикселей
      await expect(page.getByRole('radiogroup', { name: 'Исполнитель' })).toHaveCount(0);
      expect((await by.boundingBox())!.height).toBeLessThanOrEqual(44);
      const order = await panel.locator('[data-ctx-section]').evaluateAll(els => els.map(e => e.getAttribute('data-ctx-section')));
      expect(order).toEqual(['with', 'by', 'params', 'plus', 'where']);
      await expectFits(page, vp);
      await shot(page, `image-${name}-${theme}.png`);

      await by.getByRole('button').click();
      await expect(page.getByText('Облако', { exact: true })).toBeVisible();
      const menu = page.getByRole('radiogroup', { name: 'Исполнитель' });
      if (name === '360') expect((await menu.boundingBox())!.width).toBeGreaterThan(300);
      await shot(page, `image-menu-${name}-${theme}.png`);
      await page.getByRole('radio', { name: /FLUX Kontext/ }).first().click();
      await expect(page.getByRole('radiogroup', { name: 'Исполнитель' })).toHaveCount(0);
      await expect(by).toContainText('FLUX Kontext');
      await expect(by).toContainText('$');
      await expect(page.locator('[data-ctx-price]')).toContainText('$');
      await expect(page.locator('[data-ctx-run] button')).toContainText('$');
      await expectFits(page, vp);
    });
  }

  test(`«Снять» · ${name}: длительность больше четырёх значений — через меню, ширина параметров не растёт`, async ({ page }) => {
    // У Veo в моке три длительности (сегмент); на этот тест расширяем до пяти, потом возвращаем
    const veo = VIDEO_CATALOG.providers[1].models[0];
    const saved = veo.durations;
    veo.durations = [4, 6, 8, 10, 12];
    try {
      await openVideoChat(page, () => primaryOf('video-scene', { sceneId: 'scene-5' }), { vp, frames: true });
      await page.locator('[data-context-row] [data-chip="primary"]').click();
      const panel = page.locator('[data-context-panel]');
      await expect(panel).toBeVisible({ timeout: 10_000 });
      const dur = panel.locator('[data-ctx-param="duration"]');
      await expect(dur).toBeVisible({ timeout: 10_000 });
      const chip = dur.locator('[data-ctx-options]');
      await expect(chip).toBeVisible();
      await expect(dur.locator('button')).toHaveCount(1);
      expect((await dur.boundingBox())!.width).toBeLessThanOrEqual(vp.width - 24);
      const before = (await chip.boundingBox())!.width;
      await chip.getByRole('button').click();
      // Пять пунктов меню и сам чип с текущим значением
      await expect(page.getByRole('button', { name: /^\d+ с$/ })).toHaveCount(6);
      await shot(page, `video-duration-menu-${name}.png`);
      await page.getByRole('button', { name: '12 с', exact: true }).click();
      await expect(chip).toContainText('12 с');
      expect((await chip.boundingBox())!.width).toBeLessThan(before + 40);
      await expectFits(page, vp);
      await shot(page, `video-${name}.png`);
    } finally {
      veo.durations = saved;
    }
  });
}
