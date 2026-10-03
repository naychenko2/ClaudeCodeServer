import { test, expect, type Page } from '@playwright/test';
import {
  agentFocus, audioVersionReady, FILM, finishJob, hubSend, pushRecord, pushThreads, newWorld, openChat, P, panel, record, S, shot, standardFilm, standardScenes, stripReady, w,
} from './videoPanelMock';

// Фича «Видео» (ADR-022, макет docs/mockups/video-editor-v7.html): сценарии 1–9 макета, личный чат,
// 503 dsp_unavailable, клик по .film в дереве и проигрывание film.mp4 — на 1440 и 360 в обеих темах,
// плюс раскладка 800 и 1024. Бэкенд не нужен: собранный dist раздаётся статикой, /api/** и хаб — моки
// по контрактам (videoPanelMock.ts).
//
//   (cd dist && python3 -m http.server 5240) &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5240 VE_SHOTS_DIR=../.cc-attachments/video-e2e \
//     npx playwright test e2e/video-editor.spec.ts

const SHOTS = process.env.VE_SHOTS_DIR || '';
const TRACE = !!process.env.VE_TRACE;
const DESK = { width: 1440, height: 900 };
const PHONE = { width: 360, height: 780 };

test.use({ serviceWorkers: 'block' });

const feed = () => [1, 2, 3, 4, 5].map(n => record('video_scene', { sceneId: `scene-${n}` }, `Видео: Сцена ${n}`, Date.parse('2026-10-02T10:00:00Z') + n * 1000));

async function start(page: Page, o: { vp: typeof DESK; theme: 'light' | 'dark'; built?: boolean; focus?: { sceneId?: string; filmPath?: string }; personal?: boolean; dsp?: boolean; autoFinish?: boolean; scenes?: ReturnType<typeof standardScenes>; emptyFeed?: boolean }) {
  newWorld({
    scenes: o.scenes ?? standardScenes(), films: o.personal ? [] : [standardFilm(!!o.built)], feed: o.emptyFeed ? [] : feed(),
    focus: o.focus ?? (o.personal ? { sceneId: 'scene-5' } : { sceneId: 'scene-5', filmPath: FILM }), personal: o.personal, dsp: o.dsp,
    autoFinish: o.autoFinish,
  });
  await openChat(page, { vp: o.vp, theme: o.theme, trace: TRACE });
  if (o.focus && !o.focus.sceneId && !o.focus.filmPath) {
    await expect(page.getByRole('button', { name: /^Прикрепить файл, / })).toBeVisible({ timeout: 30_000 });
    return;
  }
  await stripReady(page);
}

// Ярлык «Видео» из «＋» поля ввода: полоса и панель на «Сцене» — вход, когда сцены ещё нет
async function openByShortcut(page: Page) {
  await page.getByRole('button', { name: /^Прикрепить файл, / }).click();
  await page.getByRole('menuitem', { name: 'Видео сцена и фильм' }).or(page.getByRole('button', { name: 'Видео сцена и фильм' })).first().click();
  await expect(panel(page)).toBeVisible();
}

const isPhone = (page: Page) => (page.viewportSize()?.width ?? 1440) < 800;
const sceneChip = (page: Page) => page.locator('[data-video-chip="scene"] button').first();
const filmChip = (page: Page) => page.locator('[data-video-chip="film"] button').first();
const card = (page: Page, n: number) => page.locator(`[data-video-card="scene"][data-scene="scene-${n}"]`);
const foot = (page: Page) => panel(page);
const tab = (page: Page, name: 'Сцена' | 'Фильм') => panel(page).getByRole('tab', { name: new RegExp(`^${name}`) });

async function openScene(page: Page) {
  if (isPhone(page) && await page.locator('[data-gen-sheet="sheet"]').isVisible()) await tab(page, 'Сцена').click();
  else await sceneChip(page).click();
  await expect(panel(page)).toBeVisible();
  await expect(tab(page, 'Сцена')).toHaveAttribute('aria-selected', 'true');
}

// Открытая шторка телефона закрывает полосу: вкладку меняем в самой панели
async function openFilm(page: Page) {
  if (isPhone(page) && await page.locator('[data-gen-sheet="sheet"]').isVisible()) await tab(page, 'Фильм').click();
  else await filmChip(page).click();
  await expect(panel(page)).toBeVisible();
  await expect(tab(page, 'Фильм')).toHaveAttribute('aria-selected', 'true');
}

// На телефоне панель — шторка поверх ленты: опустить до цены, чтобы добраться до карточек
async function peek(page: Page) {
  if (!isPhone(page)) return;
  const sheet = page.locator('[data-gen-sheet="sheet"]');
  if (await sheet.isVisible()) await sheet.getByTitle('Опустить до цены — лента станет доступна').click();
  await expect(page.locator('[data-gen-sheet="peek"]')).toBeVisible();
}

async function raise(page: Page) {
  if (!isPhone(page)) return;
  const peeked = page.locator('[data-gen-sheet="peek"]');
  if (await peeked.isVisible()) await peeked.getByTitle('Поднять шторку').click();
  await expect(page.locator('[data-gen-sheet="sheet"]')).toBeVisible();
}

// Закрыть панель: шторку — её ✕, колонку — пунктом рельса «Видео»
async function closePanel(page: Page) {
  if (isPhone(page)) await panel(page).getByTitle('Закрыть панель — сводка останется в полосе').click();
  else await page.getByRole('button', { name: 'Скрыть «Видео»' }).click();
}

async function noHorizontalScroll(page: Page) {
  const over = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(over).toBeLessThanOrEqual(1);
}

for (const theme of ['light', 'dark'] as const) {
  for (const [vpName, vp] of [['1440', DESK], ['360', PHONE]] as const) {
    test.describe(`${vpName} · ${theme}`, () => {
      const shotName = (s: string) => `${s}-${vpName}-${theme}`;

      test('1. сцена: «Развернуть» → «Снять» → варианты → «Сохранить сцену» → фильм с «Пересобрать»', async ({ page }) => {
        await start(page, { vp, theme, built: true });
        await openScene(page);
        await panel(page).getByRole('button', { name: 'Развернуть' }).click();
        await expect(page.locator('[data-video-text-expanded]')).toBeVisible();
        await panel(page).getByRole('button', { name: 'Камера:' }).click();
        await shot(page, SHOTS, shotName('s1-expanded'));
        await panel(page).getByRole('button', { name: 'Все настройки' }).click();
        await expect(page.locator('[data-gen-draft]')).toBeVisible();
        await expect(foot(page).getByText('≈ $3.20')).toBeVisible();
        await foot(page).getByRole('button', { name: 'Снять', exact: true }).click();
        await expect.poll(() => w().jobs.length).toBe(1);
        // Запуск уходит с текстом сцены, сохранённым до котировки
        expect(w().scenes[4].settings.text).toContain('Камера:');
        await expect(page.locator('[data-gen-draft]')).toHaveCount(0);
        await peek(page);
        // Одна съёмка — одна полная карточка: плеер и варианты у сцены, запуск — компактная строка
        const c = card(page, 5);
        await expect(c.locator('[data-video-player]')).toBeVisible({ timeout: 10_000 });
        await expect(c.getByText(/^\d из 2$/)).toBeVisible();
        await expect(page.locator('[data-video-card="launch"][data-scene="scene-5"] [data-video-player]')).toHaveCount(0);
        await shot(page, SHOTS, shotName('s1-card'));
        await card(page, 5).getByRole('button', { name: 'Сохранить сцену' }).click();
        await expect(card(page, 5).getByText(/В проекте: scene-05\.mp4/)).toBeVisible();
        await raise(page);
        await openFilm(page);
        const rows = panel(page).locator('[data-video-film-row]');
        await expect(rows).toHaveCount(5);
        await expect(rows.last()).toContainText('scene-05.mp4');
        await expect(rows.last().locator('[data-video-mark="updated"]')).toBeVisible();
        await expect(foot(page).getByRole('button', { name: 'Пересобрать' })).toBeVisible();
        await shot(page, SHOTS, shotName('s1-film'));
      });

      test('2. пересъёмка сцены 3 из карточки: «обновлена» и film.v2.mp4', async ({ page }) => {
        await start(page, { vp, theme, built: true });
        await openScene(page);
        await peek(page);
        // Клик по карточке при открытой панели — панель следует за выбором
        await card(page, 3).locator('b', { hasText: 'Сцена 3' }).click();
        await raise(page);
        await expect(panel(page).locator('[data-video-context]')).toContainText('Сцена 3');
        await foot(page).getByRole('button', { name: 'Переснять', exact: true }).click();
        await expect.poll(() => w().scenes[2].versions.length).toBe(3);
        await peek(page);
        // Полная карточка одна — у сцены; запуск рисуется компактной строкой без кнопок
        await card(page, 3).getByRole('button', { name: 'Сохранить сцену' }).click();
        // Уходит путь открытого фильма (он старше папки сцены)
        await expect.poll(() => w().saves.at(-1)).toMatchObject({ filmPath: 'video/утро-в-горах/утро-в-горах.film' });
        await raise(page);
        await openFilm(page);
        const row3 = panel(page).locator('[data-video-film-row="2"]');
        await expect(row3).toContainText('scene-03.v2.mp4');
        await expect(row3.locator('[data-video-mark="updated"]')).toBeVisible();
        await expect(page.locator('[data-video-film-dot]')).toBeVisible();
        await expect(foot(page).locator('[data-gen-foot-stale]')).toContainText('сцена 3 обновлена');
        await shot(page, SHOTS, shotName('s2-updated'));
        await foot(page).getByRole('button', { name: 'Пересобрать' }).click();
        await expect(foot(page).locator('[data-gen-foot-result]')).toContainText('film.v2.mp4', { timeout: 10_000 });
        await expect(panel(page).locator('[data-video-mark="updated"]')).toHaveCount(0);
        await shot(page, SHOTS, shotName('s2-rebuilt'));
      });

      test('3. фильм не собран: порядок, затемнение 2 с, «Собрать», «Показать в дереве»', async ({ page }) => {
        await start(page, { vp, theme });
        await openFilm(page);
        const rows = panel(page).locator('[data-video-film-row]');
        if (!isPhone(page)) {
          await rows.nth(0).locator('[data-video-drag]').dragTo(rows.nth(2));
        } else {
          await rows.nth(0).getByRole('button', { name: 'Действия со сценой' }).click();
          await page.getByRole('button', { name: 'Позже', exact: true }).last().click();
        }
        await expect.poll(() => w().patches.some(p => JSON.stringify(p).includes('"move"'))).toBe(true);
        await panel(page).locator('[data-video-cut="1"] button').first().click();
        await panel(page).locator('[data-video-cut="1"]').getByRole('button', { name: 'затемнение' }).click();
        await panel(page).locator('[data-video-cut="1"]').getByRole('button', { name: '2 с' }).click();
        await expect(panel(page).locator('[data-video-cut="1"] button').first()).toHaveText('■ затемнение 2 с');
        // Подрезка в строке: конец − 0,5 с
        await rows.nth(1).getByRole('button', { name: /✂/ }).click();
        await panel(page).locator('[data-video-trim]').getByTitle('Конец − 0,5 с').click();
        await expect(panel(page).locator('[data-video-trim]')).toContainText('остаётся 7,5 с из 8 с');
        await shot(page, SHOTS, shotName('s3-trim'));
        await panel(page).locator('[data-video-trim]').getByRole('button', { name: 'Готово' }).click();
        await expect(rows.nth(1)).toContainText('✂ 7,5 с из 8 с');
        await expect(panel(page).locator('[data-video-music]')).toContainText('утро.mp3');
        await foot(page).getByRole('button', { name: 'Собрать' }).click();
        await expect(foot(page).locator('[data-gen-foot-progress]')).toBeVisible();
        await shot(page, SHOTS, shotName('s3-building'));
        await expect(foot(page).locator('[data-gen-foot-result]')).toContainText('film.mp4', { timeout: 10_000 });
        await expect(foot(page).getByRole('button', { name: 'Собрано' })).toBeVisible();
        await shot(page, SHOTS, shotName('s3-built'));
        // Лента человека: строки правки и сборки есть, а ручки фильма звали с sessionId
        expect(w().filmNoSession).toEqual([]);
        await peek(page);
        await expect(page.locator('[data-video-quiet="video_note"]').filter({ hasText: /^Вы (поправили фильм: |переставили|подрезали|поменяли|добавили|убрали)/ }).first()).toBeVisible();
        await expect(page.locator('[data-video-quiet="video_film_built"]').last()).toHaveText('Вы собрали фильм утро-в-горах: video/утро-в-горах/film.mp4');
        await shot(page, SHOTS, shotName('s3-human-feed'));
        await raise(page);
        await foot(page).locator('[data-gen-foot-result]').getByRole('button', { name: 'Показать в дереве' }).click();
        await expect.poll(() => decodeURIComponent(page.url())).toContain('/file/video/утро-в-горах/film.mp4');
      });

      test('4. «Сочинить под фильм…» → «Звук» с заготовкой и «↩ К фильму»', async ({ page }) => {
        await start(page, { vp, theme });
        await openFilm(page);
        await panel(page).getByRole('button', { name: 'Сочинить под фильм…' }).click();
        const sound = page.getByRole('complementary', { name: 'Звук' }).or(page.getByRole('dialog', { name: 'Звук' })).first();
        await expect(sound).toBeVisible();
        await expect(sound.locator('[data-gen-return]')).toContainText('К фильму «утро-в-горах»');
        // Решение v7 №9: полоса над полем ввода остаётся на «Видео», на «Звук» не уходит
        await expect(page.locator('[data-composer-strip="video"]')).toBeVisible();
        await expect(page.locator('[data-composer-strip="sound"]')).toHaveCount(0);
        await expect(sound.getByText(/заготовка из «Видео»/)).toBeVisible();
        await shot(page, SHOTS, shotName('s4-sound'));
        await sound.locator('[data-gen-return] button').click();
        await expect(page.locator('[data-composer-strip="video"]')).toBeVisible();
        await expect(tab(page, 'Фильм')).toHaveAttribute('aria-selected', 'true');
        // Черновик звука заведён сервером под этот фильм; его первая версия встаёт музыкой сама
        expect(w().musicFor).toBe(FILM);
        audioVersionReady('music/утро-в-горах.mp3');
        await expect(panel(page).locator('[data-video-music]')).toContainText('утро-в-горах.mp3');
        await expect(panel(page).getByText('из «Звука»')).toBeVisible();
      });

      test('5. «Править кадр» → «Картинки» → новая версия встаёт кадром B, клип «переснять»', async ({ page }) => {
        await start(page, { vp, theme, focus: { sceneId: 'scene-3', filmPath: FILM } });
        await openScene(page);
        await panel(page).locator('[data-video-frame="B"] button').click();
        await panel(page).locator('[data-video-frame-menu="B"]').getByRole('button', { name: /Править кадр/ }).click();
        const images = page.getByRole('complementary', { name: 'Картинки' }).or(page.getByRole('dialog', { name: 'Картинки' })).first();
        await expect(images).toBeVisible();
        await expect(images.locator('[data-gen-return]')).toContainText('К сцене «Сцена 3»');
        await shot(page, SHOTS, shotName('s5-images'));
        // M3: открытие кадра без правки не меняет кадр — ни ссылки, ни пометки «изменён»
        await page.waitForTimeout(500);
        expect(JSON.stringify(w().scenes[2].settings.frameB)).toContain('"kind":"file"');
        // Правка в «Картинках» дала версию 1 — она сама становится кадром B
        const t = w().imageThreads[0] as { id: string; versions: Record<string, unknown>[]; currentVersionId: string; file: string };
        t.versions.push({ id: 'v1', number: 1, jobId: 'ij1', variant: 0, baseVersionId: 'origin', baseStepId: null, steps: ['st1'], currentStepId: 'st1', createdAt: '2026-10-02T10:05:00Z' });
        t.currentVersionId = 'v1';
        hubSend({ type: 'image_thread_changed', sessionId: S, projectId: P, revision: 9, state: { focus: t.id, revision: 9, threads: w().imageThreads } });
        await expect.poll(() => JSON.stringify(w().scenes[2].settings.frameB)).toContain('"kind":"image"');
        await images.locator('[data-gen-return] button').click();
        // B10: после «↩ К сцене» полоса над полем ввода снова «Видео»
        await expect(page.locator('[data-composer-strip="video"]')).toBeVisible();
        await expect(page.locator('[data-composer-strip="images"]')).toHaveCount(0);
        await expect(panel(page).locator('[data-video-stale]')).toContainText('Кадр B изменён после съёмки — переснимите');
        await shot(page, SHOTS, shotName('s5-stale'));
      });

      test('6. композер «Чат | Сцена»: просьба добавляется к тексту сцены и снимает по котировке', async ({ page }) => {
        await start(page, { vp, theme });
        await page.getByRole('button', { name: 'Режим «Сцена»' }).click();
        const input = page.locator('textarea').last();
        await expect(input).toHaveAttribute('placeholder', /Просьба к съёмке/);
        await input.fill('медленнее, закат теплее');
        await shot(page, SHOTS, shotName('s6-composer'));
        await page.getByRole('button', { name: /^Снять/ }).last().click();
        await expect.poll(() => w().jobs.length).toBe(1);
        expect(w().scenes[4].settings.text).toContain('медленнее, закат теплее');
      });

      test('7. телефон: полоса в одну строку, шторка опускается до цены и поднимается', async ({ page }) => {
        test.skip(vp.width >= 800, 'сценарий телефона');
        await start(page, { vp, theme });
        const bar = page.locator('[data-video-strip="full"]');
        const box = await bar.boundingBox();
        // Одна строка: тач-цель 44 px плюс рамка
        expect(box!.height).toBeLessThanOrEqual(52);
        expect((await sceneChip(page).boundingBox())!.height).toBeGreaterThanOrEqual(44);
        expect((await filmChip(page).boundingBox())!.height).toBeGreaterThanOrEqual(44);
        await noHorizontalScroll(page);
        await shot(page, SHOTS, shotName('s7-strip'));
        await openScene(page);
        await expect(page.locator('[data-gen-sheet="sheet"]')).toBeVisible();
        await shot(page, SHOTS, shotName('s7-sheet'));
        await peek(page);
        await expect(page.locator('[data-gen-sheet="peek"]').getByRole('button', { name: 'Снять', exact: true })).toBeVisible();
        await shot(page, SHOTS, shotName('s7-peek'));
        await raise(page);
        await noHorizontalScroll(page);
      });

      test('8. пустая сцена и пустой фильм: кадры и текст оживляют «Снять», «Сцена из проекта» наполняет фильм', async ({ page }) => {
        // Лента пуста: якоря сцен 1–5 из feed() дали бы вторую карточку «Сцена 1» рядом с созданной сценой
        await start(page, { vp, theme, focus: {}, scenes: [], emptyFeed: true });
        await openByShortcut(page);
        await expect(panel(page).locator('[data-video-empty="scene"]')).toBeVisible();
        await expect(foot(page).getByText('Нужны оба кадра: выберите кадр A и кадр B выше')).toBeVisible();
        await shot(page, SHOTS, shotName('s8-empty-scene'));
        for (const [slot, name] of [['A', 'кадр-1.png'], ['B', 'кадр-2.png']] as const) {
          await panel(page).locator(`[data-video-frame="${slot}"] button`).click();
          await panel(page).getByRole('button', { name: /^Из проекта/ }).click();
          const picker = panel(page).locator('[data-video-project-picker]');
          // Выбор стартует с корня проекта: папки video/ в проекте может не быть
          if (slot === 'A') {
            await picker.getByRole('button', { name: 'video', exact: true }).click();
            await picker.getByRole('button', { name: 'утро-в-горах', exact: true }).click();
            await picker.getByRole('button', { name: 'кадры', exact: true }).click();
          }
          await picker.getByRole('button', { name }).click();
        }
        await expect.poll(() => w().scenes.length).toBe(1);
        await expect(page.locator('[data-video-card="scene"][data-scene="scene-1"]')).toHaveCount(1);
        await panel(page).locator('[data-video-text] textarea').fill('Туман рассеивается над озером');
        await expect(foot(page).getByRole('button', { name: 'Снять', exact: true })).toBeEnabled({ timeout: 10_000 });
        await shot(page, SHOTS, shotName('s8-ready'));
        await tab(page, 'Фильм').click();
        await panel(page).getByRole('button', { name: 'Новый фильм' }).click();
        await panel(page).locator('[data-video-new-film] input').fill('вечер');
        await panel(page).getByRole('button', { name: 'Завести' }).click();
        await expect.poll(() => w().filmCreates).toEqual(['video/вечер/вечер.film']);
        await expect(panel(page).locator('[data-video-empty="film"]')).toContainText('В фильме пока нет сцен');
        await expect(foot(page).getByText('Добавьте в фильм хотя бы одну сцену')).toBeVisible();
        await shot(page, SHOTS, shotName('s8-empty-film'));
        await panel(page).getByRole('button', { name: 'Сцена из проекта' }).click();
        const add = panel(page).locator('[data-video-add-scene]');
        // Папки нового фильма на диске ещё нет — выбор откроется с корня проекта, а не «Not Found»
        await add.getByRole('button', { name: 'video', exact: true }).click();
        await add.getByRole('button', { name: 'утро-в-горах', exact: true }).click();
        await add.getByRole('button', { name: 'scene-06.mp4' }).click();
        await expect(panel(page).locator('[data-video-film-row]')).toHaveCount(1);
        await expect(foot(page).getByRole('button', { name: 'Собрать' })).toBeEnabled();
      });

      test('9. панель следует за выбором: черновик, «Картинки», агент не двигает панель, закрытая не открывается', async ({ page }) => {
        await start(page, { vp, theme });
        await openScene(page);
        await panel(page).locator('[data-video-text] textarea').fill('Солнце поднимается, туман уходит вниз');
        await expect(page.locator('[data-gen-draft]')).toBeVisible();
        // Уходим в «Картинки» (кадр B) и возвращаемся кликом по карточке сцены 5
        await panel(page).locator('[data-video-frame="B"] button').click();
        await panel(page).locator('[data-video-frame-menu="B"]').getByRole('button', { name: /Править кадр/ }).click();
        const images = page.getByRole('complementary', { name: 'Картинки' }).or(page.getByRole('dialog', { name: 'Картинки' })).first();
        await expect(images).toBeVisible();
        // Агент берёт сцену 2: панель «Картинки» остаётся, в ней подсказка
        agentFocus('scene-2');
        await expect(images.locator('[data-gen-agent-pick]')).toContainText('Claude взял в работу: Сцена 2');
        await expect(images).toBeVisible();
        await shot(page, SHOTS, shotName('s9-agent'));
        await images.locator('[data-gen-agent-pick]').getByRole('button', { name: 'Открыть' }).click();
        await raise(page);
        await expect(panel(page).locator('[data-video-context]')).toContainText('Сцена 2');
        await peek(page);
        await card(page, 5).locator('b', { hasText: 'Сцена 5' }).click();
        await raise(page);
        await expect(panel(page).locator('[data-video-context]')).toContainText('Сцена 5');
        await expect(panel(page).locator('[data-video-text] textarea')).toHaveValue('Солнце поднимается, туман уходит вниз');
        await expect(page.locator('[data-gen-draft]')).toBeVisible();
        await shot(page, SHOTS, shotName('s9-back'));
        // Закрытая панель по клику не открывается — меняется только сводка полосы
        await closePanel(page);
        await expect(panel(page)).toHaveCount(0);
        await card(page, 1).locator('b', { hasText: 'Сцена 1' }).click();
        await expect(sceneChip(page)).toContainText(isPhone(page) ? 'Сц. 1' : 'Сцена 1');
        await expect(panel(page)).toHaveCount(0);
        await shot(page, SHOTS, shotName('s9-closed'));
      });

      test('личный чат: фильмов нет, local под замком, «Скачать» вместо «Сохранить»', async ({ page }) => {
        const scenes = standardScenes().map(s => ({ ...s, savedFiles: [], filmRef: undefined, folder: '' }));
        await start(page, { vp, theme, personal: true, scenes });
        await expect(filmChip(page)).toHaveCount(0);
        await openScene(page);
        await panel(page).locator('[data-video-executor] button').first().click();
        const local = panel(page).getByRole('radio', { name: /MiniMax H3/ });
        await expect(local).toBeDisabled();
        await expect(local).toContainText('Локальные модели работают только в чате проекта');
        await panel(page).locator('[data-video-frame="A"] button').click();
        await expect(panel(page).getByRole('button', { name: /Из проекта/ })).toHaveCount(0);
        await expect(panel(page).getByRole('button', { name: /С компьютера/ })).toBeEnabled();
        await shot(page, SHOTS, shotName('personal-scene'));
        // «С компьютера»: новая ручка чата, путь из ответа кладётся в настройки сцены как есть
        const png = { name: 'кадр.png', mimeType: 'image/png', buffer: Buffer.from('x') };
        const put = page.waitForRequest(r => r.method() === 'PUT' && /\/scenes\/[^/]+\/settings/.test(r.url()) && r.postData()!.includes('frames/0123456789abcdef'));
        await panel(page).locator('[data-video-frame-menu="A"] input[type="file"]').setInputFiles(png);
        await put;
        // Превью — по ручке кадра чата (не файлы проекта), подпись — имя файла, а не путь рабочей папки
        await expect.poll(() => w().frameGets).toContain(`/video-editor/chats/${S}/frames/0123456789abcdef0123456789abcdef.png`);
        await expect(panel(page).locator('[data-video-frame="A"]')).toContainText('кадр.png');
        await expect(panel(page).locator('[data-video-frame="A"]')).not.toContainText('0123456789abcdef');
        await shot(page, SHOTS, shotName('personal-upload'));
        await panel(page).locator('[data-video-frame="A"] button').click();
        w().uploadFail = 'notImage';
        await panel(page).locator('[data-video-frame-menu="A"] input[type="file"]').setInputFiles(png);
        await expect(page.getByText('Это не картинка')).toBeVisible();
        w().uploadFail = 'tooBig';
        await panel(page).locator('[data-video-frame-menu="A"] input[type="file"]').setInputFiles(png);
        await expect(page.getByText('Файл больше 20 МБ')).toBeVisible();
        await tab(page, 'Фильм').click();
        await expect(panel(page).locator('[data-video-empty="film"]')).toContainText('Фильмы живут в проекте');
        await shot(page, SHOTS, shotName('personal-film'));
        await peek(page);
        await expect(card(page, 1).getByRole('button', { name: 'Скачать' })).toBeVisible();
        await expect(card(page, 1).getByRole('button', { name: 'В фильм →' })).toHaveCount(0);
      });
    });
  }
}

test('агент снимает: та же карточка запуска с ценой и ходом плюс «✦ Claude», панель не двигается', async ({ page }) => {
  await start(page, { vp: DESK, theme: 'light' });
  await openFilm(page);
  const s2 = w().scenes[1];
  s2.launches.push({ jobId: 'job-agent', at: '2026-10-02T10:10:00Z', status: 'running', interrupted: false, initiator: 'agent', provider: 'fal', model: 'veo-3.1', count: 2 });
  w().revision++;
  pushThreads();
  pushRecord(record('video_launch_versions', {
    sceneId: 'scene-2', jobId: 'job-agent', provider: 'fal', model: 'Veo 3.1', count: 2, durationSec: 8,
    price: { amount: 3.2, unit: 'usd', approx: true, source: 'pricing' }, initiator: 'agent',
  }, 'Claude запустил: съёмка · veo-3.1', Date.now()));
  hubSend({ type: 'video_edit_progress', sessionId: S, scopeKey: P, jobId: 'job-agent', sceneId: 'scene-2', stage: 'running', variant: 1, count: 2, initiator: 'agent' });
  const c = page.locator('[data-video-card="launch"][data-scene="scene-2"]');
  await expect(c.locator('[data-video-launch-line]')).toHaveText('Veo 3.1 · 2 вар. · ≈ $3.20');
  await expect(c.locator('[data-video-card-progress]')).toContainText('снимаем 2 варианта');
  await expect(c.locator('[data-by-claude]')).toBeVisible();
  finishJob('job-agent');
  // Готовые варианты — в одной полной карточке сцены; у запуска остаётся компактная строка с итогом
  await expect(card(page, 2).locator('[data-video-player]')).toBeVisible();
  await expect(card(page, 2).getByRole('button', { name: 'Сохранить сцену' })).toBeVisible();
  await expect(c.locator('[data-video-player]')).toHaveCount(0);
  await expect(c.locator('[data-video-launch-outcome]')).toContainText('Готово: 2 варианта');
  await expect(tab(page, 'Фильм')).toHaveAttribute('aria-selected', 'true');
  await shot(page, SHOTS, 'agent-launch-card-1440-light');
});

test('агент: тихие строки без слова «Claude» в тексте, лицо даёт одна метка «✦ Claude»', async ({ page }) => {
  await start(page, { vp: DESK, theme: 'light' });
  pushRecord(record('video_saved', { sceneId: 'scene-2', path: 'video/утро-в-горах/scene-02.mp4', initiator: 'agent' }, 'Сохранил сцену «Сцена 2» в проект: video/утро-в-горах/scene-02.mp4', Date.now()));
  pushRecord(record('video_film_built', { path: FILM, file: 'video/утро-в-горах/film.mp4', initiator: 'agent' }, 'Собрал фильм утро-в-горах: video/утро-в-горах/film.mp4', Date.now()));
  const saved = page.locator('[data-video-quiet="video_saved"]');
  await expect(saved).toHaveText('Сохранил сцену «Сцена 2» в проект: video/утро-в-горах/scene-02.mp4');
  const line = saved.locator('xpath=..');
  await expect(line.locator('[data-by-claude]')).toHaveCount(1);
  for (const t of ['video_saved', 'video_film_built']) {
    const row = page.locator(`[data-video-quiet="${t}"]`).locator('xpath=..');
    expect((await row.innerText()).match(/Claude/g)?.length ?? 0).toBeLessThanOrEqual(1);
  }
  await shot(page, SHOTS, 'agent-quiet-lines-1440-light');
});

test('503 dsp_unavailable: «Собрать» серое с причиной, «Проверить снова» оживляет', async ({ page }) => {
  await start(page, { vp: DESK, theme: 'light', dsp: false });
  await openFilm(page);
  await foot(page).getByRole('button', { name: 'Собрать' }).click();
  await expect(foot(page).getByText('Сборка фильмов на этом сервере выключена — обратитесь к администратору').first()).toBeVisible();
  await expect(foot(page).getByRole('button', { name: 'Собрать' })).toBeDisabled();
  await shot(page, SHOTS, 'dsp-unavailable-1440-light');
  w().dsp = true;
  await panel(page).getByRole('button', { name: 'Проверить снова' }).click();
  await expect(foot(page).getByRole('button', { name: 'Собрать' })).toBeEnabled();
});

test('409 revision_conflict: фильм перечитан и показан свежим', async ({ page }) => {
  await start(page, { vp: DESK, theme: 'light' });
  await openFilm(page);
  w().films.get(FILM)!.revision = 'r9';
  w().films.get(FILM)!.document.items.pop();
  await panel(page).locator('[data-video-film-row="0"]').getByRole('button', { name: 'Действия со сценой' }).click();
  await page.getByRole('button', { name: 'Позже', exact: true }).last().click();
  await expect(page.getByText('Фильм только что поменяли в другом месте — показываем свежий')).toBeVisible();
  await expect(panel(page).locator('[data-video-film-row]')).toHaveCount(3);
});

test('клик по .film в дереве открывает «Фильм», film.mp4 проигрывается', async ({ page }) => {
  await start(page, { vp: DESK, theme: 'light', focus: { sceneId: 'scene-5' } });
  await page.getByRole('button', { name: 'Файлы', exact: true }).first().click();
  const tree = page.getByRole('complementary', { name: 'Файлы' }).or(page.locator('[data-panel-key="files"]')).first();
  for (const name of ['video', 'утро-в-горах']) await page.getByText(name, { exact: true }).first().click();
  await page.getByText('утро-в-горах.film', { exact: true }).first().click();
  await expect(tab(page, 'Фильм')).toHaveAttribute('aria-selected', 'true', { timeout: 20_000 });
  await expect(panel(page).locator('[data-video-film-row]')).toHaveCount(4);
  await shot(page, SHOTS, 'film-from-tree-1440-light');
  await page.getByText('film.mp4', { exact: true }).first().click();
  // Просмотр файлов ставит адрес в <source>, а не в src самого <video>
  const video = page.locator('video:has(source[src*="film.mp4"])').first();
  await expect(video).toBeVisible({ timeout: 20_000 });
  await expect.poll(() => video.evaluate(v => (v as HTMLVideoElement).readyState), { timeout: 15_000 }).toBeGreaterThanOrEqual(1);
  expect(await video.evaluate(v => (v as HTMLVideoElement).duration)).toBeGreaterThan(3);
  await video.evaluate(v => { (v as HTMLVideoElement).muted = true; return (v as HTMLVideoElement).play(); });
  await expect.poll(() => video.evaluate(v => (v as HTMLVideoElement).currentTime)).toBeGreaterThan(0);
  await shot(page, SHOTS, 'film-mp4-plays-1440-light');
  void tree;
});

for (const width of [800, 1024]) {
  test(`раскладка ${width}: полоса без переполнения, панель на месте`, async ({ page }) => {
    await start(page, { vp: { width, height: 800 }, theme: 'light' });
    await openScene(page);
    await noHorizontalScroll(page);
    await shot(page, SHOTS, `layout-${width}-light`);
    await openFilm(page);
    await shot(page, SHOTS, `layout-${width}-film-light`);
  });
}


// Майя, major 3: в узкой колонке режется только имя; цена и длительность остаются целыми
for (const width of [800, 1024, 1440]) {
  test(`полоса ${width}: цена и «8 с · 2 вар.» не режутся`, async ({ page }) => {
    await start(page, { vp: { width, height: 800 }, theme: 'light' });
    // Цена приходит из котировки панели: открываем и закрываем «Сцену», чтобы она легла в стор
    await openScene(page);
    await expect(page.locator('[data-video-chip="scene"] [data-video-chip-price]')).toContainText('$3.20', { timeout: 10_000 });
    const whole = async (sel: string) => {
      const el = page.locator(sel).first();
      if (!await el.count()) return;
      expect(await el.evaluate(n => n.scrollWidth <= n.clientWidth + 1), sel).toBe(true);
    };
    await whole('[data-video-chip="scene"] [data-video-chip-price]');
    await whole('[data-video-chip="scene"] [data-video-chip-meta]');
  });
}
