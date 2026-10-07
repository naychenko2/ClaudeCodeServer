import { test, expect, type Page } from '@playwright/test';
import { openVideoChat, primaryOf } from './contextVideoMock';
import { applyFormatCatalog, mockFormat, newFmtWorld } from './mediaFormatVideoMock';
import { newWorld, openChat, primary } from './contextRowMock';

// Сужение окна 1280 → 360 при открытом и закрытом «Формате»: граница ошибок не срабатывает, шторка открывается.
test.use({ serviceWorkers: 'block' });

const NOW = '2026-10-03T10:00:00Z';
const heroThread = () => ({
  id: 'thread-hero', file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null, currentStepId: null,
  settings: null, pendingJobId: null, createdAt: NOW, currentVersionId: 'origin', launches: [],
  versions: [{ id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, baseStepId: null, steps: [], currentStepId: null, createdAt: NOW }],
});
const HERO_SVG = '<svg xmlns="http://www.w3.org/2000/svg" width="400" height="300"><rect width="400" height="300" fill="#7aa"/></svg>';
const prefs = {
  provider: 'fal', model: 'fal-ai/flux-pro/kontext', count: 2, matchSourceSize: true, characterSlug: null,
  create: { provider: 'fal', model: 'fal-ai/flux-pro/kontext', count: null }, edit: null,
};

async function setup(page: Page, errors: string[], width = 1280) {
  page.on('pageerror', e => errors.push(`pageerror: ${e.message}\n${e.stack ?? ''}`));
  page.on('console', m => { if (m.type() === 'error') errors.push(`console: ${m.text()}`); });
  const t = heroThread();
  newWorld({
    threads: [t], flags: {}, imagePrefs: prefs,
    ctx: { primary: primary({ by: 'human', label: 'hero.png', ref: { threadId: t.id, versionId: 'origin' } }) },
  });
  await page.addInitScript(() => {
    localStorage.setItem('cc-image-editor-mock', 'all');
    // Чат завершён: в списке телефона он виден, только если «Завершённые» не скрыты
    localStorage.setItem('cc_chat_filters:proj-ctx', JSON.stringify({ statuses: ['active', 'waiting', 'done', 'error'] }));
  });
  await openChat(page, { vp: { width, height: width === 360 ? 720 : 800 }, theme: 'light' });
  await page.route(u => decodeURIComponent(u.href).includes('hero.png'), r => r.fulfill({ contentType: 'image/svg+xml', body: HERO_SVG }));
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
  await page.evaluate(async () => {
    const find = (part: string) => performance.getEntriesByType('resource').map(e => e.name).find(n => n.includes(part));
    const core = await import(/* @vite-ignore */ find('/src/lib/subsystems/registryCore.ts') ?? '/src/lib/subsystems/registryCore.ts');
    const mod = await import(/* @vite-ignore */ '/src/features/imageEditor/manifest.tsx');
    core.registerSubsystem({ ...mod.manifest, key: 'e2e-image', core: true, tab: undefined });
  });
}

for (const opened of [false, true]) {
  test(`сужение 1280→360, «Формат» ${opened ? 'открыт' : 'закрыт'}: нет границы ошибок, шторка открывается`, async ({ page }) => {
    const errors: string[] = [];
    await setup(page, errors);
    const chip = page.locator('[data-context-row] [data-chip="format"]');
    await expect(chip).toBeVisible({ timeout: 15_000 });
    if (opened) { await chip.click(); await expect(page.locator('[data-fmt-body]').last()).toBeVisible(); }
    await page.setViewportSize({ width: 360, height: 720 });
    await page.waitForTimeout(500);
    await chip.evaluate(el => (el as HTMLElement).click());
    await expect(page.locator('[data-fmt-body]').last()).toBeVisible();
    await expect(page.getByText('Что-то пошло не так')).toHaveCount(0);
    if (errors.length) console.log(errors.join('\n---\n'));
  });
}

test('шторка открыта на телефоне, окно растянули до десктопа: нет границы ошибок, всплывашка открывается', async ({ page }) => {
  const errors: string[] = [];
  await setup(page, errors, 360);
  const chip = page.locator('[data-context-row] [data-chip="format"]');
  await expect(chip).toBeVisible({ timeout: 15_000 });
  await chip.click();
  await expect(page.locator('[data-fmt-body]').last()).toBeVisible();
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.waitForTimeout(500);
  await expect(page.getByText('Что-то пошло не так')).toHaveCount(0);
  await expect(chip).toBeVisible();
  await chip.click();
  await expect(page.locator('[data-fmt-body]').last()).toBeVisible();
  await expect(page.getByText('Что-то пошло не так')).toHaveCount(0);
  if (errors.length) console.log(errors.join('\n---\n'));
});

// Видео-чип сцены: та же смена раскладки при открытой и закрытой всплывашке
for (const opened of [false, true]) {
  test(`видео: сужение 1280→360, «Формат» ${opened ? 'открыт' : 'закрыт'}: нет границы ошибок`, async ({ page }) => {
    const errors: string[] = [];
    page.on('pageerror', e => errors.push(`pageerror: ${e.message}\n${e.stack ?? ''}`));
    applyFormatCatalog();
    newFmtWorld();
    await page.addInitScript(() => localStorage.setItem('cc_chat_filters:proj-ctx', JSON.stringify({ statuses: ['active', 'waiting', 'done', 'error'] })));
    await openVideoChat(page, () => primaryOf('video-scene', { sceneId: 'scene-5' }), {
      focus: { sceneId: 'scene-5' }, vp: { width: 1280, height: 800 }, theme: 'light', frames: true,
      flags: {}, autoFinish: false, extra: mockFormat,
    });
    const chip = page.locator('[data-context-row] [data-chip="format"]');
    await expect(chip).toBeVisible({ timeout: 30_000 });
    if (opened) { await chip.click(); await expect(page.locator('[data-fmt-body]').last()).toBeVisible(); }
    await page.setViewportSize({ width: 360, height: 720 });
    await page.waitForTimeout(500);
    await chip.evaluate(el => (el as HTMLElement).click());
    await expect(page.locator('[data-fmt-body]').last()).toBeVisible();
    await expect(page.getByText('Что-то пошло не так')).toHaveCount(0);
    expect(errors).toEqual([]);
  });
}
