import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { hubSend, newWorld, openChat, primary, P, S, w } from './contextRowMock';

// «Формат» для картинок (ADR-025, макет docs/mockups/media-format-v1, сценарии 1–4 и 7–9). Бэкенд не нужен:
// контекст чата и хабы — моки (contextRowMock), API картинок — встроенный мок модуля (cc-image-editor-mock),
// префы проекта и обрезка — маршруты этого файла. Запуск:
//   cd frontend; npx vite --port 5322 --strictPort --host 127.0.0.1 &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5322 MF_SHOTS_DIR=../.cc-attachments/media-format npx playwright test e2e/media-format-image.spec.ts

const SHOTS = process.env.MF_SHOTS_DIR || '';
test.use({ serviceWorkers: 'block' });

const VIEWPORTS = [
  { name: '1440', vp: { width: 1440, height: 900 } },
  { name: '360', vp: { width: 360, height: 780 } },
] as const;

const FLUX = 'fal-ai/flux-pro/kontext';
const NANO = 'fal-ai/nano-banana-2/edit';
const NOW = '2026-10-03T10:00:00Z';

const prefs = (model: string, extra: Record<string, unknown> = {}) => ({
  provider: model === 'soul_2' ? 'higgsfield' : 'fal', model, count: 2, matchSourceSize: true, characterSlug: null,
  create: { provider: model === 'soul_2' ? 'higgsfield' : 'fal', model, count: null }, edit: null, ...extra,
});

const version = (id: string, number: number, base: string | null) => ({
  id, number, jobId: number ? `crop-${number}` : null, variant: null, baseVersionId: base, baseStepId: null, steps: [], currentStepId: null, createdAt: NOW,
});
const heroThread = () => ({
  id: 'thread-hero', file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null, currentStepId: null,
  settings: null, pendingJobId: null, createdAt: NOW, currentVersionId: 'origin', launches: [], versions: [version('origin', 0, null)],
});
const draftThread = () => ({
  id: 'thread-draft', file: null, lineage: [], draftFolder: '', stacks: [], currentStackId: null, currentStepId: null,
  settings: null, pendingJobId: null, createdAt: NOW, currentVersionId: null, launches: [], versions: [],
});

// Картинка нити: у файла из мока маршрута нет, а размер исходника «Формату» нужен — svg 400×300 (4:3)
const HERO_SVG = '<svg xmlns="http://www.w3.org/2000/svg" width="400" height="300"><rect width="400" height="300" fill="#7aa"/></svg>';

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name) });
}

interface Setup { kind: 'draft' | 'hero'; model: string; by?: 'human' | 'agent' }

async function open(page: Page, vp: { width: number; height: number }, theme: 'light' | 'dark', s: Setup) {
  const thread = s.kind === 'draft' ? draftThread() : heroThread();
  newWorld({
    threads: [thread], flags: { 'image-editor': true, 'media-format': true }, imagePrefs: prefs(s.model),
    ctx: { primary: primary({ by: s.by ?? 'human', label: s.kind === 'draft' ? 'Новая картинка' : 'hero.png', ref: { threadId: thread.id, ...(s.kind === 'hero' ? { versionId: 'origin' } : null) } }) },
  });
  await page.addInitScript(() => localStorage.setItem('cc-image-editor-mock', 'all'));
  await openChat(page, { vp, theme });
  await page.route(u => decodeURIComponent(u.href).includes('hero.png'), r => r.fulfill({ contentType: 'image/svg+xml', body: HERO_SVG }));
  // Обрезка: dryRun — размер результата, настоящая — новая версия нити (как ImageThreadService.CropToAspectAsync)
  const crops: { aspect: string; anchor: string; dryRun?: boolean }[] = [];
  await page.route(u => /\/threads\/[^/]+\/crop$/.test(u.pathname), async r => {
    const body = r.request().postDataJSON() as { aspect: string; anchor: string; dryRun?: boolean };
    crops.push(body);
    const t = w().threads![0] as ReturnType<typeof heroThread>;
    if (!body.dryRun) {
      t.versions.push({ ...version('v1', 1, 'origin'), steps: ['step-crop'], currentStepId: 'step-crop' });
      t.currentVersionId = 'v1';
      w().threadsRevision++;
    }
    await r.fulfill({ json: { width: 300, height: 300, aspect: body.aspect, versionId: body.dryRun ? null : 'v1', state: { focus: null, revision: w().threadsRevision, threads: w().threads } } });
  });
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
  await page.evaluate(async () => {
    const find = (part: string) => performance.getEntriesByType('resource').map(e => e.name).find(n => n.includes(part));
    const core = await import(/* @vite-ignore */ find('/src/lib/subsystems/registryCore.ts') ?? '/src/lib/subsystems/registryCore.ts');
    const mod = await import(/* @vite-ignore */ '/src/features/imageEditor/manifest.tsx');
    core.registerSubsystem({ ...mod.manifest, key: 'e2e-image', core: true, tab: undefined });
  });
  return crops;
}

const chip = (page: Page) => page.locator('[data-context-row] [data-chip="format"]');
const chipText = async (page: Page) => (await chip(page).innerText()).replace(/\s+/g, ' ').trim();
const aspect = (page: Page, a: string) => page.locator(`[data-fmt-aspect="${a}"]`);
const tier = (page: Page, t: string) => page.locator(`[data-fmt-tier="${t}"]`);
const body = (page: Page) => page.locator('[data-fmt-body]').last();
const execChip = (page: Page) => page.locator('[data-context-row] [data-chip="exec"]');
const runBtn = (page: Page) => page.locator('[data-composer-mode-bar] button').last();
const noOverflow = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);

// Строка модели в меню «Чем» — по точному имени с поставщиком: подсказки «сейчас: …» под «Авто» тоже называют модели
async function pickModel(page: Page, name: string) {
  await execChip(page).click();
  await page.getByText(name, { exact: true }).first().click();
}

for (const { name, vp } of VIEWPORTS) {
  for (const theme of ['light', 'dark'] as const) {
    test.describe(`«Формат» картинок · ${name} · ${theme}`, () => {
      test('1 · новая картинка: 16:9 → «Высокое» (1792×1024 · $0.08) → кнопка поля берёт цену ступени', async ({ page }) => {
        await open(page, vp, theme, { kind: 'draft', model: NANO });
        await expect(chip(page)).toBeVisible({ timeout: 15_000 });
        await chip(page).click();
        await expect(body(page)).toBeVisible();
        await aspect(page, '16:9').click();
        await tier(page, 'high').click();
        await expect(tier(page, 'high')).toContainText('1792×1024');
        await expect(tier(page, 'high')).toContainText('$0.08');
        await expect(tier(page, 'high')).toHaveAttribute('aria-pressed', 'true');
        // Свёрнутый чип: «16:9 · 1792×1024», на телефоне хвост короче — «16:9 · 1792»
        await expect.poll(() => chipText(page)).toContain(name === '360' ? '16:9 · 1792' : '16:9 · 1792×1024');
        if (name === '360') expect(await chipText(page)).not.toContain('1792×1024');
        // Желание ушло в префы проекта человеком, а не подставлено
        await expect.poll(() => (w().imagePrefs as { format?: { aspect?: string; tier?: string; by?: string } }).format).toMatchObject({ aspect: '16:9', tier: 'high', by: 'human' });
        // Котировка пошла с ступенью: на кнопке поля цена «High» (вдвое дороже Standard: $0.04 → $0.08)
        await page.keyboard.press('Escape');
        await expect(runBtn(page)).toContainText('✦ Нарисовать');
        await expect(runBtn(page)).toContainText('$0.08', { timeout: 10_000 });
        expect(await noOverflow(page)).toBe(true);
        await shot(page, `s1-${name}-${theme}.png`);
      });

      test('2 · «Чем» → GPT Image: «16:9 нет у GPT Image — взяли 3:2»; назад к FLUX — снова 16:9 (желание помнится)', async ({ page }) => {
        await open(page, vp, theme, { kind: 'draft', model: FLUX });
        await expect(chip(page)).toBeVisible({ timeout: 15_000 });
        await chip(page).click();
        await aspect(page, '16:9').click();
        await expect.poll(() => chipText(page)).toContain('16:9');
        await page.keyboard.press('Escape');
        await pickModel(page, 'Higgsfield · GPT Image 2.5');
        await expect(page.locator('[data-format-note]')).toContainText('16:9 нет у GPT Image — взяли 3:2.', { timeout: 10_000 });
        await expect.poll(() => chipText(page)).toContain('3:2');
        await shot(page, `s2-${name}-${theme}.png`);
        // Желание в префах осталось 16:9: подмена не записана
        expect((w().imagePrefs as { format?: { aspect?: string } }).format?.aspect).toBe('16:9');
        await pickModel(page, 'fal · FLUX Kontext');
        await expect.poll(() => chipText(page), { timeout: 10_000 }).toContain('16:9');
        await expect(page.locator('[data-format-note]')).toHaveCount(0);
      });

      test('3 · «Точно…»: 1920×1080 → «кратно 64: 1920×1088»; 2600×1400 → «до 2048 px»; «Вернуться к пропорциям»', async ({ page }) => {
        await open(page, vp, theme, { kind: 'draft', model: NANO });
        await expect(chip(page)).toBeVisible({ timeout: 15_000 });
        await chip(page).click();
        await page.locator('[data-fmt-exact-open]').click();
        await page.locator('[data-fmt-preset="1920x1080"]').click();
        await expect(page.locator('[data-fmt-note="rounded"]')).toContainText('Модель рисует кратно 64: 1920×1088.', { timeout: 10_000 });
        await expect(page.locator('[data-fmt-out]')).toContainText('1920×1088');
        await expect.poll(() => chipText(page)).toContain('1920×1088 · точно');
        // Поля ширина × высота: набранное уходит с паузой и тоже округляется на сервере
        const w2 = page.locator('[data-fmt-exact] input').first();
        const h2 = page.locator('[data-fmt-exact] input').last();
        await w2.fill('2600');
        await h2.fill('1400');
        await expect(page.locator('[data-fmt-note="rounded"]')).toContainText('до 2048 px по длинной стороне', { timeout: 10_000 });
        if (name === '360') expect(await w2.evaluate(el => parseFloat(getComputedStyle(el).fontSize))).toBeGreaterThanOrEqual(16);
        await shot(page, `s3-${name}-${theme}.png`);
        await page.locator('[data-fmt-exact-close]').click();
        await expect.poll(() => chipText(page)).not.toContain('точно');
        await expect(page.locator('[data-fmt-exact]')).toHaveCount(0);
      });

      test('4 · правка 4:3 → 1:1: «Дорисовать края» (outpaint) и «Обрезать» (новая версия, рамка «Слева»)', async ({ page }) => {
        const crops = await open(page, vp, theme, { kind: 'hero', model: NANO });
        await expect(chip(page)).toBeVisible({ timeout: 15_000 });
        // По умолчанию — «Как у исходника · 4:3»
        await expect.poll(() => chipText(page)).toContain('Как у исходника · 4:3');
        await chip(page).click();
        await expect(page.locator('[data-fmt-source]')).toContainText('400×300');
        await aspect(page, '1:1').click();
        const ask = page.locator('[data-fmt-ask]');
        await expect(ask).toContainText('4:3 → 1:1. Как получить 1:1?');
        await expect(ask).toContainText('Дорисовать края');
        await expect(ask).toContainText('Без ИИ · бесплатно');
        // Пока способ не выбран, запуск «Изменить» заблокирован: модель сама не тянет и не режет
        await shot(page, `s4-ask-${name}-${theme}.png`);
        await page.locator('[data-fmt-how="outpaint"]').click();
        await expect(runBtn(page)).toContainText('✦ Дорисовать', { timeout: 10_000 });
        await expect.poll(() => chipText(page)).toContain('1:1 · дорисовать');
        // Дорисовка ведёт в то же действие, а вопроса «Пропорции» под чипами больше нет
        await expect(page.getByText('Пропорции', { exact: true })).toHaveCount(0);
        // Второй путь: назад к исходнику → 1:1 → «Обрезать» с рамкой «Слева»
        await page.locator('[data-fmt-source]').click();
        await expect.poll(() => chipText(page)).toContain('Как у исходника · 4:3');
        await aspect(page, '1:1').click();
        await page.getByRole('button', { name: 'Слева' }).first().click();
        await expect.poll(() => crops.some(c => c.dryRun && c.aspect === '1:1' && c.anchor === 'left')).toBe(true);
        await page.locator('[data-fmt-how="crop"]').click();
        await expect.poll(() => crops.filter(c => !c.dryRun).map(c => `${c.aspect}/${c.anchor}`)).toEqual(['1:1/left']);
        // Обрезка дала новую версию и вернула «Как у исходника»
        await expect.poll(() => (w().threads![0] as ReturnType<typeof heroThread>).versions.length).toBe(2);
        await expect.poll(() => chipText(page)).toContain('Как у исходника');
        expect(await noOverflow(page)).toBe(true);
      });

      test('7 · формат поставил агент: ✦ на чипе, ручная правка снимает метку', async ({ page }) => {
        await open(page, vp, theme, { kind: 'draft', model: NANO });
        await expect(chip(page)).toBeVisible({ timeout: 15_000 });
        await expect(chip(page).locator('[data-agent-mark]')).toHaveCount(0);
        // Агент сделал image_generate(aspect "9:16", quality "high"): сервер пишет желание с by=agent и шлёт событие префов
        const agent = { mode: 'aspect', aspect: '9:16', tier: 'high', by: 'agent', at: NOW };
        w().imagePrefs = { ...w().imagePrefs, format: agent };
        hubSend({ type: 'image_prefs_changed', projectId: P, prefs: w().imagePrefs });
        await expect(chip(page).locator('[data-agent-mark]')).toBeVisible({ timeout: 10_000 });
        await expect.poll(() => chipText(page)).toContain('9:16');
        await chip(page).click();
        await expect(page.locator('[data-fmt-foot]').last()).toContainText('поставил Claude ✦');
        await shot(page, `s7-${name}-${theme}.png`);
        // Ручная «Макс» снимает ✦; несвязанная правка префов до этого формат агента не перештамповывала
        expect(w().imagePrefsPuts.some(p => 'format' in p)).toBe(false);
        await tier(page, 'max').click();
        await expect(chip(page).locator('[data-agent-mark]')).toHaveCount(0, { timeout: 10_000 });
        expect((w().imagePrefs as { format?: { by?: string; tier?: string } }).format).toMatchObject({ by: 'human', tier: 'max' });
      });

      test('8 · мобильная раскладка и пустое состояние: строка не уводит страницу вбок, «Как у модели» с причиной (Soul)', async ({ page }) => {
        await open(page, vp, theme, { kind: 'draft', model: 'soul_2' });
        await expect(chip(page)).toBeVisible({ timeout: 15_000 });
        expect(await noOverflow(page)).toBe(true);
        await chip(page).click();
        // Сценарий 9: размеры модель не сообщила — только «Как у модели» с причиной и ссылка на «Чем»
        await expect(page.locator('[data-fmt-nocaps]')).toContainText('Как у модели');
        await expect(page.locator('[data-fmt-nocaps]')).toContainText('не сообщил, какие размеры умеет');
        await expect(page.locator('[data-fmt-note="nocaps"]')).toContainText('Пропорции и качество появятся');
        await expect(page.locator('[data-fmt-aspect]')).toHaveCount(0);
        if (name === '360') {
          // Шторка телефона: тело в модальном окне, а не всплывашка над строкой
          await expect(page.getByText('Формат новой картинки', { exact: true })).toBeVisible();
        }
        await shot(page, `s9-${name}-${theme}.png`);
        await page.getByRole('button', { name: 'Сменить модель в «Чем»' }).click();
        await expect(page.locator('[data-fmt-body]')).toHaveCount(0);
        await expect(page.getByText(/GPT Image/).first()).toBeVisible();
      });
    });
  }
}

// Два клика подряд, пока ответ ручки ещё не пришёл: ступень не должна затереть выбранные пропорции (и наоборот)
test('быстрые клики подряд: пропорции и ступень не теряют друг друга', async ({ page }) => {
  await open(page, VIEWPORTS[0].vp, 'light', { kind: 'draft', model: NANO });
  await expect(chip(page)).toBeVisible({ timeout: 15_000 });
  await chip(page).click();
  await expect(body(page)).toBeVisible();
  await page.evaluate(() => {
    (document.querySelector('[data-fmt-aspect="9:16"]') as HTMLElement).click();
    (document.querySelector('[data-fmt-tier="high"]') as HTMLElement).click();
  });
  await expect.poll(() => chipText(page)).toContain('9:16 · 1024×1792');
  await expect.poll(() => (w().imagePrefs as { format?: { aspect?: string; tier?: string } }).format).toMatchObject({ aspect: '9:16', tier: 'high' });
});

// Лента: обрезка без ИИ — запись image_launch_versions с model «crop» (та же карточка версии) и тихая строка image_format
for (const { name, vp } of VIEWPORTS) {
  test(`лента · ${name}: обрезка человеком и агентом, смена формата агентом`, async ({ page }) => {
    const hero = heroThread();
    const crop = (jobId: string, initiator: 'human' | 'agent', text: string, ts: number) => ({
      kind: 'module_record', module: 'imageeditor', recordType: 'image_launch_versions', timestamp: ts, fallback: text,
      data: { threadId: hero.id, jobId, provider: 'none', model: 'crop', count: 1, initiator, baseVersionId: 'origin', crop: { aspect: '1:1', anchor: 'left', width: 1200, height: 1200 } },
    });
    hero.versions.push({ ...version('v1', 1, 'origin'), jobId: 'crop-1', steps: ['s1'], currentStepId: 's1' } as never,
      { ...version('v2', 2, 'origin'), jobId: 'crop-2', steps: ['s2'], currentStepId: 's2' } as never);
    (hero as { launches: unknown[] }).launches = ['crop-1', 'crop-2'].map((jobId, i) => ({
      jobId, baseVersionId: 'origin', baseStepId: null, at: NOW, status: 'done', initiator: i ? 'agent' : 'human', prompt: null,
    }));
    newWorld({
      threads: [hero], flags: { 'image-editor': true, 'media-format': true }, imagePrefs: prefs(NANO),
      feed: [
        crop('crop-1', 'human', 'Вы обрезали без ИИ: 1200×1200 · 1:1 · бесплатно · версия 1', Date.parse(NOW) - 30_000),
        crop('crop-2', 'agent', '✦ Claude обрезал без ИИ: 1200×1200 · 1:1 · бесплатно · версия 2', Date.parse(NOW) - 20_000),
        { kind: 'module_record', module: 'imageeditor', recordType: 'image_format', timestamp: Date.parse(NOW) - 10_000, fallback: '✦ Claude сменил формат картинок проекта: 16:9 → 9:16', data: { by: 'agent', before: '16:9', after: '9:16' } },
      ],
      ctx: { primary: null },
    });
    await page.addInitScript(() => localStorage.setItem('cc-image-editor-mock', 'all'));
    await openChat(page, { vp });
    await expect(page.locator('textarea').last()).toBeVisible({ timeout: 30_000 });
    await page.evaluate(async () => {
      const find = (part: string) => performance.getEntriesByType('resource').map(e => e.name).find(n => n.includes(part));
      const core = await import(/* @vite-ignore */ find('/src/lib/subsystems/registryCore.ts') ?? '/src/lib/subsystems/registryCore.ts');
      const mod = await import(/* @vite-ignore */ '/src/features/imageEditor/manifest.tsx');
      core.registerSubsystem({ ...mod.manifest, key: 'e2e-image', core: true, tab: undefined });
    });
    await expect(page.getByText('Вы обрезали без ИИ: 1200×1200 · 1:1 · бесплатно · версия 1')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText('✦ Claude обрезал без ИИ: 1200×1200 · 1:1 · бесплатно · версия 2')).toBeVisible();
    // Карточка версии та же, подпись — размер и «обрезано без ИИ», у агента с пометкой
    await expect(page.locator('[data-image-version="1"]')).toContainText('1200×1200 · 1:1 · обрезано без ИИ');
    await expect(page.locator('[data-image-version="2"]')).toContainText('обрезано без ИИ · ✦ Claude');
    await expect(page.getByText('✦ Claude сменил формат картинок проекта: 16:9 → 9:16')).toBeVisible();
    expect(await noOverflow(page)).toBe(true);
  });
}

test('флаг media-format выключен: чипа «Формат» нет, у «Дорисовать» прежний вопрос «Пропорции»', async ({ page }) => {
  newWorld({
    threads: [heroThread()], flags: { 'image-editor': true }, imagePrefs: prefs(NANO),
    ctx: { primary: primary({ ref: { threadId: 'thread-hero', versionId: 'origin' } }) },
  });
  await page.addInitScript(() => localStorage.setItem('cc-image-editor-mock', 'all'));
  await openChat(page, { vp: VIEWPORTS[0].vp });
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
  await page.evaluate(async () => {
    const find = (part: string) => performance.getEntriesByType('resource').map(e => e.name).find(n => n.includes(part));
    const core = await import(/* @vite-ignore */ find('/src/lib/subsystems/registryCore.ts') ?? '/src/lib/subsystems/registryCore.ts');
    const mod = await import(/* @vite-ignore */ '/src/features/imageEditor/manifest.tsx');
    core.registerSubsystem({ ...mod.manifest, key: 'e2e-image', core: true, tab: undefined });
  });
  await expect(page.locator('[data-composer-actions]')).toBeVisible({ timeout: 15_000 });
  await expect(chip(page)).toHaveCount(0);
  await page.locator('[data-composer-actions] [data-action-chip="outpaint"]').click();
  await expect(page.locator('[data-composer-actions]').getByText('Пропорции')).toBeVisible();
});
