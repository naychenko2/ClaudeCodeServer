import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { FILM, record, scene, standardFilm, standardScenes, w, type Scene } from './videoPanelMock';
import { openVideoChat, primaryOf } from './contextVideoMock';
import { applyFormatCatalog, fmt, mockFormat, newFmtWorld, progress, upscaleDone } from './mediaFormatVideoMock';

// «Формат» для видео (ADR-025, макет docs/mockups/media-format-v1, сценарии 5, 6, 7, 8 и 10). Бэкенд не нужен:
// контекст чата — contextVideoMock, видео-API — videoPanelMock, формат, котировка, замок фильма и увеличение «Макс»
// — mediaFormatVideoMock. Запуск:
//   cd frontend; npx vite --port 5331 --strictPort --host 127.0.0.1 &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5331 MF_SHOTS_DIR=../.cc-attachments/media-format npx playwright test e2e/media-format-video.spec.ts

const SHOTS = process.env.MF_SHOTS_DIR || '';
test.use({ serviceWorkers: 'block' });

const VIEWPORTS = [
  { name: '1440', vp: { width: 1440, height: 900 } },
  { name: '360', vp: { width: 360, height: 780 } },
] as const;
const FLAG_ON = { 'media-format': true };

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name) });
}

interface Setup {
  vp: { width: number; height: number };
  theme: 'light' | 'dark';
  scenes?: Scene[];
  films?: ReturnType<typeof standardFilm>[];
  primary?: () => ReturnType<typeof primaryOf>;
  focus?: { sceneId?: string; filmPath?: string };
  feed?: Record<string, unknown>[];
  flags?: Record<string, boolean>;
  // Пропорции исходных кадров A и B для превью подрезки (по умолчанию обе 16:9)
  sources?: [string, string];
}

const scene5 = () => primaryOf('video-scene', { sceneId: 'scene-5' });
const scene2 = () => primaryOf('video-scene', { sceneId: 'scene-2' });
const filmPrimary = () => primaryOf('video-film', { filmPath: FILM });

async function open(page: Page, o: Setup) {
  applyFormatCatalog();
  newFmtWorld();
  if (o.sources) { fmt().sourceAspect = o.sources[0]; fmt().sourceAspectB = o.sources[1]; }
  // Как у бэкенда: основной объект «Видео» зеркалится в серверный фокус сцен (выбор «Чем» пишется в сцену фокуса)
  await openVideoChat(page, o.primary ?? scene5, {
    focus: o.focus ?? { sceneId: 'scene-5' },
    vp: o.vp, theme: o.theme, frames: true, flags: o.flags ?? FLAG_ON, scenes: o.scenes, films: o.films, feed: o.feed, autoFinish: false,
    extra: mockFormat,
  });
}

const chip = (page: Page) => page.locator('[data-context-row] [data-chip="format"]');
const chipText = async (page: Page) => (await chip(page).innerText()).replace(/\s+/g, ' ').trim();
const aspect = (page: Page, a: string) => page.locator(`[data-fmt-aspect="${a}"]`);
const tier = (page: Page, t: string) => page.locator(`[data-fmt-tier="${t}"]`);
const body = (page: Page) => page.locator('[data-fmt-body]').last();
const note = (page: Page, n: string) => page.locator(`[data-fmt-note="${n}"]`);
const actionChip = (page: Page, id: string) => page.locator(`[data-composer-actions] [data-action-chip="${id}"]`);
const execChip = (page: Page) => page.locator('[data-context-row] [data-chip="exec"]');
const runBtn = (page: Page) => page.locator('[data-composer-mode-bar] button').last();
const noOverflow = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);
const lastPut = () => fmt().settingsPuts.at(-1)!;

// Сцена на локальной модели H3 с «Макс»: пропорции 16:9, ступень «Макс» уже выбрана человеком
const h3Scene = (tierKey: string): Scene => {
  const s = scene(5);
  s.settings = {
    ...s.settings, provider: 'local', model: 'minimax-h3', durationSec: 5, sound: false, count: 1, aspect: '16:9',
    format: { mode: 'aspect', aspect: '16:9', tier: tierKey, by: 'human', at: '2026-10-07T09:00:00Z' },
  } as Scene['settings'];
  return s;
};

for (const { name, vp } of VIEWPORTS) {
  for (const theme of ['light', 'dark'] as const) {
    test.describe(`«Формат» видео · ${name} · ${theme}`, () => {
      test('5 · сцена: 16:9 · 720p → 9:16 → кадры будут подрезаны с превью и рамкой; MiniMax H3 — ступени 480p / 768p / 1440p', async ({ page }) => {
        await open(page, { vp, theme });
        await actionChip(page, 'shoot').click();
        await expect(chip(page)).toBeVisible({ timeout: 15_000 });
        await expect.poll(() => chipText(page), { timeout: 15_000 }).toContain('16:9 · 720p');
        expect(await noOverflow(page)).toBe(true);
        await chip(page).click();
        await expect(body(page)).toBeVisible();
        // У Veo одна ступень: «Высокое» и «Макс» серые, причина в подписи
        await expect(tier(page, 'standard')).toContainText('720p · 1280×720');
        await expect(tier(page, 'high')).toHaveAttribute('data-fmt-off', '');
        await expect(tier(page, 'max')).toContainText('нет у модели');
        await expect(note(page, 'frames')).toHaveCount(0);

        await aspect(page, '9:16').click();
        await expect.poll(() => chipText(page)).toContain('9:16 · 720p');
        // Кадры A и B — 16:9: предупреждение, превью и рамка; ⚠ на чипе
        await expect(note(page, 'frames')).toContainText('Кадры A и B — 16:9, сцена — 9:16: кадры будут подрезаны по бокам до модели.', { timeout: 10_000 });
        await expect(page.locator('[data-fmt-frames] [data-fmt-preview="crop"]')).toHaveCount(2);
        await expect(chip(page).locator('[aria-label="Предупреждение"]')).toBeVisible();
        expect(await noOverflow(page)).toBe(true);
        await shot(page, `s5-frames-${name}-${theme}.png`);
        await page.getByRole('button', { name: 'Слева', exact: true }).click();
        await expect.poll(() => lastPut().settings.frameFit).toEqual({ anchor: 'left' });
        await expect.poll(() => fmt().previews.at(-1)?.anchor).toBe('left');
        // Желание ушло в сцену человеком, с зеркалом aspect
        expect(lastPut().settings).toMatchObject({ aspect: '9:16', format: { mode: 'aspect', aspect: '9:16', by: 'human' } });

        // «Чем» → MiniMax H3: ступени 480p / 768p / 1440p, у «Макс» время обоих прогонов и пояснение
        await page.keyboard.press('Escape');
        await execChip(page).click();
        await page.getByText('MiniMax H3', { exact: true }).first().click();
        await expect.poll(() => chipText(page), { timeout: 15_000 }).toContain('9:16');
        await chip(page).click();
        await expect(tier(page, 'standard')).toContainText('480p', { timeout: 10_000 });
        await expect(tier(page, 'standard')).toContainText('480p · 480×864');
        await expect(tier(page, 'standard')).toContainText('~1,5 мин');
        await expect(tier(page, 'high')).toContainText('768p · 768×1344');
        await expect(tier(page, 'high')).toContainText('~5,5 мин');
        await expect(tier(page, 'max')).toContainText('1440p · 1440×2528');
        await expect(tier(page, 'max')).toContainText('бесплатно');
        await expect(tier(page, 'max')).toContainText('~21 мин');
        await tier(page, 'max').click();
        await expect(page.locator('[data-fmt-hint="tier-note"]')).toContainText('«Макс» — два прогона на вашей видеокарте: съёмка 768×1344 (~5,5 мин), затем увеличение до 1440×2528 (~15 мин). Увеличение можно отменить.');
        await expect.poll(() => chipText(page)).toContain('9:16 · 1440p');
        expect(await noOverflow(page)).toBe(true);
        await shot(page, `s5-h3-${name}-${theme}.png`);
        await page.keyboard.press('Escape');
        await expect(runBtn(page)).toContainText('бесплатно · ~21 мин', { timeout: 10_000 });
      });

      test('5 · подрезка по высоте: «Сверху · По центру · Снизу», кадр A не тронут', async ({ page }) => {
        await open(page, { vp, theme, sources: ['16:9', '9:16'] });
        await actionChip(page, 'shoot').click();
        await expect.poll(() => chipText(page), { timeout: 15_000 }).toContain('16:9 · 720p');
        await chip(page).click();
        await expect(note(page, 'frames')).toContainText('Кадр B — 9:16, сцена — 16:9: кадр будет подрезан сверху и снизу до модели.', { timeout: 10_000 });
        await expect(page.locator('[data-fmt-frames] [data-fmt-preview="crop"]')).toHaveCount(1);
        await expect(page.getByRole('button', { name: 'Сверху', exact: true })).toBeVisible();
        await expect(page.getByRole('button', { name: 'Снизу', exact: true })).toBeVisible();
        await expect(page.getByRole('button', { name: 'Слева', exact: true })).toHaveCount(0);
        expect(await noOverflow(page)).toBe(true);
        await shot(page, `s5-height-${name}-${theme}.png`);
        await page.getByRole('button', { name: 'Сверху', exact: true }).click();
        await expect.poll(() => lastPut().settings.frameFit).toEqual({ anchor: 'top' });
        await expect.poll(() => fmt().previews.at(-1)?.anchor).toBe('top');
      });

      test('5 · кадры режутся в разные стороны: общий текст и «Рамка — по центру», выбора нет', async ({ page }) => {
        await open(page, { vp, theme, sources: ['21:9', '9:16'] });
        await actionChip(page, 'shoot').click();
        await expect.poll(() => chipText(page), { timeout: 15_000 }).toContain('16:9 · 720p');
        await chip(page).click();
        await expect(note(page, 'frames')).toContainText('Сцена — 16:9: кадр A (21:9) будет подрезан по бокам, кадр B (9:16) — сверху и снизу до модели.', { timeout: 10_000 });
        await expect(page.locator('[data-fmt-anchors="mixed"]')).toHaveText('Рамка — по центру: кадры режутся в разные стороны.');
        await expect(page.getByRole('button', { name: 'Слева', exact: true })).toHaveCount(0);
        await expect(page.getByRole('button', { name: 'Сверху', exact: true })).toHaveCount(0);
        expect(await noOverflow(page)).toBe(true);
        await shot(page, `s5-mixed-${name}-${theme}.png`);
      });

      test('5 · котировка ступеней не выдаёт себя за автора желания: у запросов по ступеням нет by и at', async ({ page }) => {
        await open(page, { vp, theme, scenes: [...standardScenes().slice(0, 4), h3Scene('high')] });
        await actionChip(page, 'shoot').click();
        await expect.poll(() => chipText(page), { timeout: 15_000 }).toContain('16:9 · 768p');
        await chip(page).click();
        await expect(tier(page, 'max')).toContainText('~21 мин', { timeout: 10_000 });
        const asks = (w().quotes as { format?: { tier?: string; by?: string; at?: string } }[]).map(q => q.format).filter(f => f && !f.by && !f.at);
        // Ступень текущего желания делит ключ с его котировкой, поэтому «чужие» ступени обязаны быть среди запросов без автора
        expect(asks.map(f => f!.tier)).toEqual(expect.arrayContaining(['standard', 'max']));
      });

      test('6 · сцена фильма: 16:9 · 720p 🔒 → 9:16 → «Оставить 16:9 / Всё равно 9:16» → предупреждение → «Привести к 16:9»', async ({ page }) => {
        const scenes = standardScenes();
        scenes[1] = { ...scenes[1], lockedBy: { filmPath: FILM, aspect: '16:9' } } as Scene;
        await open(page, { vp, theme, scenes, primary: scene2, focus: { sceneId: 'scene-2' } });
        await actionChip(page, 'shoot').click();
        await expect.poll(() => chipText(page), { timeout: 15_000 }).toContain('16:9 · 720p');
        await expect(chip(page).locator('[aria-label="Как у фильма"]')).toBeVisible();
        await chip(page).click();
        await expect(note(page, 'locked')).toContainText('Формат фильма «утро-в-горах» — 16:9, его задала сцена 1. Остальные сцены следуют за ней, иначе при склейке пойдут чёрные полосы.');
        await expect(page.getByText('Формат сцены · фильм «утро-в-горах»').last()).toBeVisible();
        await expect(page.locator('[data-fmt-foot]').last()).toContainText('Один формат на все сцены фильма «утро-в-горах».');

        // Попытка сменить: сервер ответил 409 — вопрос, чип остался 16:9
        await aspect(page, '9:16').click();
        await expect(note(page, 'lock-ask')).toContainText('Фильм «утро-в-горах» — 16:9. Сцена в 9:16 даст чёрные полосы по бокам при склейке.', { timeout: 10_000 });
        expect(lastPut().force).toBeUndefined();
        await expect.poll(() => chipText(page)).toContain('16:9');
        await shot(page, `s6-ask-${name}-${theme}.png`);
        await page.locator('[data-fmt-lock="keep"]').click();
        await expect(note(page, 'lock-ask')).toHaveCount(0);
        await expect.poll(() => chipText(page)).toContain('16:9');

        // «Всё равно 9:16»: тот же запрос с force, замок снят для него, чип жёлтый с ⚠
        await aspect(page, '9:16').click();
        await page.locator('[data-fmt-lock="force"]').click();
        await expect.poll(() => lastPut().force).toBe(true);
        await expect.poll(() => chipText(page)).toContain('9:16 · 720p');
        await expect(note(page, 'mismatch')).toContainText('Сцена в 9:16, а фильм «утро-в-горах» — 16:9. При склейке пойдут чёрные полосы.', { timeout: 10_000 });
        await expect(chip(page).locator('[aria-label="Предупреждение"]')).toBeVisible();
        expect(await noOverflow(page)).toBe(true);
        await shot(page, `s6-mismatch-${name}-${theme}.png`);
        // «Привести к 16:9»: пропорции снова фильма, предупреждения нет
        await page.locator('[data-fmt-fix]').click();
        await expect.poll(() => chipText(page)).toContain('16:9 · 720p');
        await expect(note(page, 'mismatch')).toHaveCount(0);
        expect(lastPut().settings).toMatchObject({ aspect: '16:9', format: { aspect: '16:9', by: 'human' } });
      });

      test('6 · строка фильма: «9:16 в фильме 16:9 — при склейке будут чёрные полосы» и «Привести к 16:9»', async ({ page }) => {
        const scenes = standardScenes();
        scenes[1] = {
          ...scenes[1], lockedBy: { filmPath: FILM, aspect: '16:9' }, formatMismatch: true,
          settings: { ...scenes[1].settings, aspect: '9:16', format: { mode: 'aspect', aspect: '9:16', tier: 'standard', by: 'human', at: '2026-10-07T09:00:00Z' } },
        } as Scene;
        const film = standardFilm();
        film.marks = [{ index: 1, claude: false, updated: false, stale: false, formatMismatch: true } as never];
        await open(page, { vp, theme, scenes, films: [film], primary: filmPrimary, focus: { filmPath: FILM } });
        await actionChip(page, 'montage').click();
        const row = page.locator('[data-video-montage] [data-video-film-mismatch]');
        await expect(row).toContainText('9:16 в фильме 16:9 — при склейке будут чёрные полосы.', { timeout: 10_000 });
        await expect(page.locator('[data-video-montage] [data-video-film-row="1"] [data-video-mark="format"]')).toBeVisible();
        await expect(page.locator('[data-video-montage] [data-video-film-row="0"] [data-video-film-mismatch]')).toHaveCount(0);
        await shot(page, `s6-film-row-${name}-${theme}.png`);
        await page.locator('[data-video-film-fix]').click();
        await expect.poll(() => lastPut().settings).toMatchObject({ aspect: '16:9', format: { aspect: '16:9', by: 'human' } });
        await expect(row).toHaveCount(0, { timeout: 10_000 });
      });

      test('7 · формат поставил агент: ✦ на чипе, ручная правка снимает метку', async ({ page }) => {
        const s = scene(5);
        s.settings = { ...s.settings, aspect: '9:16', format: { mode: 'aspect', aspect: '9:16', tier: 'standard', by: 'agent', at: '2026-10-07T09:00:00Z' } } as Scene['settings'];
        await open(page, { vp, theme, scenes: [...standardScenes().slice(0, 4), s] });
        await actionChip(page, 'shoot').click();
        await expect.poll(() => chipText(page), { timeout: 15_000 }).toContain('9:16 · 720p');
        await expect(chip(page).locator('[data-agent-mark]')).toBeVisible();
        await chip(page).click();
        await expect(page.locator('[data-fmt-foot]').last()).toContainText('поставил Claude ✦');
        await aspect(page, '16:9').click();
        await expect(chip(page).locator('[data-agent-mark]')).toHaveCount(0);
        await expect.poll(() => chipText(page)).toContain('16:9 · 720p');
        await shot(page, `s7-${name}-${theme}.png`);
      });

      test('10 · «Макс» local H3: «Снимаем» → «Увеличиваем до 1440p…» → отмена увеличения → «Повторить» → версия 1440p', async ({ page }) => {
        await open(page, { vp, theme, scenes: [...standardScenes().slice(0, 4), h3Scene('max')], feed: [record('video_scene', { sceneId: 'scene-5' }, 'Видео: Сцена 5')] });
        await actionChip(page, 'shoot').click();
        await expect.poll(() => chipText(page), { timeout: 15_000 }).toContain('16:9 · 1440p');
        await expect(runBtn(page)).toContainText('бесплатно · ~21 мин', { timeout: 10_000 });
        await runBtn(page).click();
        await expect.poll(() => w().jobs.length).toBe(1);
        const jobId = (w().jobs[0] as { jobId: string }).jobId;
        const card = page.locator('[data-video-card="scene"]');
        // Стадия 1: съёмка со временем, отмена всего запуска; стадия 2 ждёт «следом»
        progress('scene-5', jobId, { etaSeconds: 330, upscaleEtaSeconds: 915, percent: 0.3 });
        await expect(card.locator('[data-video-stage="1"]')).toContainText('Снимаем · 1344×768…', { timeout: 10_000 });
        await expect(card.locator('[data-video-stage="1"]')).toContainText('~5,5 мин');
        await expect(card.locator('[data-video-stage-cancel="job"]')).toHaveText('Отменить');
        await expect(card.locator('[data-video-stage="2"]')).toContainText('Увеличение до 1440p · 2528×1440');
        await expect(card.locator('[data-video-stage="2"]')).toContainText('следом · ~15 мин');
        await shot(page, `s10-shoot-${name}-${theme}.png`);
        // Стадия 2: увеличение идёт, у него своя отмена
        progress('scene-5', jobId, { stage: 'upscaling', etaSeconds: 915, percent: 0.2 });
        await expect(card.locator('[data-video-stage="1"]')).toContainText('Съёмка · 1344×768');
        await expect(card.locator('[data-video-stage="1"]')).toContainText('готово');
        await expect(card.locator('[data-video-stage="2"]')).toContainText('Увеличиваем до 1440p…');
        await expect(card.locator('[data-video-stage="2"]')).toContainText('ещё ~15 мин');
        await expect(card.locator('[data-video-stage-cancel="upscale"]')).toHaveText('Отменить увеличение');
        expect(await noOverflow(page)).toBe(true);
        await shot(page, `s10-upscale-${name}-${theme}.png`);
        await card.locator('[data-video-stage-cancel="upscale"]').click();
        await expect.poll(() => fmt().cancelUpscales).toEqual([jobId]);
        // Отмена — не сбой: съёмка 768p сохранена версией, нейтральный текст и «Увеличить до 1440p» (та же ручка)
        await expect(card.locator('[data-video-upscale-fail]')).toContainText('Увеличение отменено. Съёмка 768p сохранена версией 1 — в 1440p её нет.', { timeout: 10_000 });
        await expect(card.locator('[data-video-upscale-fail]')).not.toContainText('не удалось');
        await expect(card.locator('[data-video-upscale-retry]')).toHaveText('Увеличить до 1440p');
        await shot(page, `s10-fail-${name}-${theme}.png`);
        await card.locator('[data-video-upscale-retry]').click();
        await expect.poll(() => fmt().retries).toEqual([{ sceneId: 'scene-5', versionId: `ver-${jobId}-1` }]);
        await expect(card.locator('[data-video-stage="2"]')).toContainText('Увеличиваем до 1440p…', { timeout: 10_000 });
        // Готово: новая версия 1440p, версия 768p остаётся
        upscaleDone('scene-5', 'job-retry-1');
        await expect(card.locator('[data-video-upscaled]')).toContainText('Версия 2 · 1440p · 2528×1440', { timeout: 10_000 });
        await expect(card.locator('[data-video-upscale-fail]')).toHaveCount(0);
        await shot(page, `s10-done-${name}-${theme}.png`);
        // Вернулись к версии 768p: 1440p той же съёмки уже есть — без кнопки и без ⚠, а повторного запроса нет
        await card.getByRole('button', { name: 'Прошлый вариант' }).click();
        await expect(card.locator('[data-video-upscale-fail]')).toContainText('Съёмка 768p сохранена версией 1, а в 1440p — версией 2.');
        await expect(card.locator('[data-video-upscale-retry]')).toHaveCount(0);
        expect(fmt().retries).toHaveLength(1);
      });

      test('10 · сбой увеличения (не отмена): ⚠ «не удалось» и кнопка «Повторить»', async ({ page }) => {
        const s = h3Scene('max');
        s.versions = [{
          versionId: 'ver-f1', number: 1, jobId: 'job-f', variant: 0, provider: 'local', model: 'minimax-h3', durationSec: 5, sizeBytes: 36026, hasSound: false,
          cost: { currency: 'local', amount: 0 }, initiator: 'human', inputs: { text: 'т' }, createdAt: '2026-10-07T10:00:00Z', width: 1344, height: 768,
          upscaleError: 'Увеличение до 1440p не удалось: ComfyUI завершил задачу ошибкой',
        }] as never;
        s.currentVersionId = 'ver-f1';
        await open(page, { vp, theme, scenes: [...standardScenes().slice(0, 4), s], feed: [record('video_scene', { sceneId: 'scene-5' }, 'Видео: Сцена 5')] });
        const card = page.locator('[data-video-card="scene"]');
        await expect(card.locator('[data-video-upscale-fail]')).toContainText('Увеличение до 1440p не удалось. Съёмка 768p сохранена версией 1 — в 1440p её нет.', { timeout: 10_000 });
        await expect(card.locator('[data-video-upscale-retry]')).toHaveText('Повторить');
        await card.locator('[data-video-upscale-retry]').click();
        await expect.poll(() => fmt().retries).toEqual([{ sceneId: 'scene-5', versionId: 'ver-f1' }]);
      });
    });
  }

  // С выключенным флагом видео-редактор прежний: чипа «Формат» нет, в редакторе сцены — пропорции сегментом
  test(`флаг выключен · ${name}: чипа «Формат» нет, редактор сцены — прежние «Пропорции»`, async ({ page }) => {
    await open(page, { vp, theme: 'light', flags: {} });
    await actionChip(page, 'shoot').click();
    await page.locator('[data-context-row] [data-chip="primary"]').click();
    const panel = page.locator('[data-context-panel]');
    await expect(panel).toBeVisible({ timeout: 10_000 });
    await expect(chip(page)).toHaveCount(0);
    await panel.getByRole('button', { name: 'Редактор сцены' }).click();
    const ed = page.locator('[data-video-scene-editor]');
    await expect(ed).toBeVisible({ timeout: 10_000 });
    await expect(ed.getByText('Пропорции', { exact: true })).toBeVisible();
    await expect(ed.getByRole('button', { name: '9:16' })).toBeVisible();
    expect(fmt().settingsPuts).toHaveLength(0);
  });

  test(`флаг включён · ${name}: в редакторе сцены нет прежних «Пропорций» — формат в чипе`, async ({ page }) => {
    await open(page, { vp, theme: 'light' });
    await actionChip(page, 'shoot').click();
    await page.locator('[data-context-row] [data-chip="primary"]').click();
    const panel = page.locator('[data-context-panel]');
    await expect(panel).toBeVisible({ timeout: 10_000 });
    await panel.getByRole('button', { name: 'Редактор сцены' }).click();
    const ed = page.locator('[data-video-scene-editor]');
    await expect(ed).toBeVisible({ timeout: 10_000 });
    await expect(ed.getByText('Пропорции', { exact: true })).toHaveCount(0);
    await expect(ed.getByText('Звук клипа')).toBeVisible();
  });
}
