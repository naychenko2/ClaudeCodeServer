import { test, expect, type Page } from '@playwright/test';
import { agentFocus, FILM, finishJob, hubSend, pushRecord, pushThreads, record, S, P, shot as saveShot, standardFilm, w } from './videoPanelMock';
import { cw, openVideoChat, primaryOf, primaryPuts } from './contextVideoMock';

// Фича «Видео» (ADR-022) на пути «Контекст + чипы» (ADR-023): сцены и фильм ведут чипы действий, текст сцены правит
// редактор «Сцена», порядок и сборку — редактор «Монтаж», карточки в ленте остались прежними. Сценарии 1–9
// прежнего макета перенесены сюда, прежняя панель «Видео», полоса и режим поля «Сцена» удалены вместе с флагом
// composer-context-row. Бэкенд не нужен: контекст хода — contextVideoMock, видео-API и хабы — videoPanelMock.
//
//   cd frontend; BACKEND_PORT=5370 npx vite --port 5371 --strictPort --host 127.0.0.1 &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5371 VE_SHOTS_DIR=../.cc-attachments/video-e2e \
//     npx playwright test e2e/video-editor.spec.ts

const SHOTS = process.env.VE_SHOTS_DIR || '';
const DESK = { width: 1440, height: 900 };
const PHONE = { width: 360, height: 780 };

test.use({ serviceWorkers: 'block' });

const feed = () => [1, 2, 3, 4, 5].map(n => record('video_scene', { sceneId: `scene-${n}` }, `Видео: Сцена ${n}`, Date.parse('2026-10-02T10:00:00Z') + n * 1000));

const filmPrimary = () => primaryOf('video-film', { filmPath: FILM });
const scenePrimary = (n = 5) => () => primaryOf('video-scene', { sceneId: `scene-${n}` });

const actions = (page: Page) => page.locator('[data-composer-actions]');
const chip = (page: Page, id: string) => actions(page).locator(`[data-action-chip="${id}"]`);
const montage = (page: Page) => page.locator('[data-video-montage]');
const sceneEditor = (page: Page) => page.locator('[data-video-scene-editor]');
const card = (page: Page, n: number) => page.locator(`[data-video-card="scene"][data-scene="scene-${n}"]`);
const modeBar = (page: Page) => page.locator('[data-composer-mode-bar]');
// Кнопка запуска — последняя в строке режима: ей предшествует метка вида; подпись начинается со значка «✦»
const runBtn = (page: Page) => modeBar(page).locator('button').last();
const isPhone = (page: Page) => (page.viewportSize()?.width ?? 1440) < 800;

async function openMontage(page: Page) {
  await chip(page, 'montage').click();
  await expect(montage(page)).toBeVisible({ timeout: 10_000 });
}

// Кнопка сборки в низу окна «Монтаж»
const buildButton = (page: Page, name: string | RegExp) => page.getByRole('button', { name, exact: typeof name === 'string' });

for (const theme of ['light', 'dark'] as const) {
  for (const [vpName, vp] of [['1440', DESK], ['360', PHONE]] as const) {
    test.describe(`${vpName} · ${theme}`, () => {
      const shotName = (s: string) => `${s}-${vpName}-${theme}`;
      const shot = (page: Page, s: string) => saveShot(page, SHOTS, shotName(s));

      test('1. сцена: текст в редакторе → «Снять» → варианты в карточке → «Сохранить сцену»', async ({ page }) => {
        await openVideoChat(page, scenePrimary(), { vp, theme, feed: feed(), films: [standardFilm(true)], frames: true, focus: { sceneId: 'scene-5', filmPath: FILM } });
        // Текст сцены правит редактор «Сцена» из панели «С чем»
        await page.locator('[data-context-row] [data-chip="primary"]').click();
        await page.locator('[data-context-panel]').getByRole('button', { name: 'Редактор сцены' }).click();
        await expect(sceneEditor(page)).toBeVisible({ timeout: 10_000 });
        await sceneEditor(page).locator('textarea').fill('Туман над озером. Камера: плавный подъём.');
        await expect.poll(() => w().scenes[4].settings.text, { timeout: 10_000 }).toContain('Камера: плавный подъём');
        await shot(page, 's1-editor');
        await page.getByRole('button', { name: 'Готово' }).click();
        await expect(sceneEditor(page)).toHaveCount(0);
        // На телефоне панель «Контекст» — шторка поверх поля ввода: закрываем её, чипы остаются над полем
        if (isPhone(page) && await page.locator('[data-gen-sheet="sheet"]').isVisible()) {
          await page.getByTitle('Закрыть панель — сводка останется в полосе').click();
        }
        // Чип «Снять» выбран, кнопка запуска несёт цену котировки; запуск уходит с текстом сцены
        await expect(chip(page, 'shoot')).toHaveAttribute('aria-checked', 'true');
        await expect(modeBar(page)).toContainText('≈ $3.20', { timeout: 10_000 });
        await shot(page, 's1-chips');
        await runBtn(page).click();
        await expect.poll(() => w().jobs.length).toBe(1);
        // Запуск по ревизии контекста идёт без sceneId; текст сцены уже сохранён редактором
        expect(w().jobs[0]).toMatchObject({ sceneId: '', contextRevision: expect.any(Number) });
        expect(w().scenes[4].settings.text).toContain('Камера: плавный подъём');
        // Одна съёмка — одна полная карточка: плеер и варианты у сцены, запуск — компактная строка
        await expect(card(page, 5).locator('[data-video-player]')).toBeVisible({ timeout: 10_000 });
        await expect(card(page, 5).getByText(/^\d из 2$/)).toBeVisible();
        await expect(page.locator('[data-video-card="launch"][data-scene="scene-5"] [data-video-player]')).toHaveCount(0);
        await shot(page, 's1-card');
        await card(page, 5).getByRole('button', { name: 'Сохранить сцену' }).click();
        await expect(card(page, 5).getByText(/В проекте: scene-05\.mp4/)).toBeVisible();
        expect(w().saves.at(-1)).toMatchObject({ filmPath: FILM });
      });

      test('2. сцену 3 переснимают из карточки: версия добавлена, «Сохранить сцену» уходит с путём фильма', async ({ page }) => {
        await openVideoChat(page, scenePrimary(3), { vp, theme, feed: feed(), films: [standardFilm(true)], frames: true, focus: { sceneId: 'scene-3', filmPath: FILM } });
        await expect(chip(page, 'shoot')).toContainText(/^(Снять|Переснять)/);
        await runBtn(page).click();
        await expect.poll(() => w().scenes[2].versions.length).toBe(3);
        // Полная карточка одна — у сцены; запуск рисуется компактной строкой без кнопок
        await card(page, 3).getByRole('button', { name: 'Сохранить сцену' }).click();
        // Уходит путь фильма, в котором сцена стоит (он старше папки сцены)
        await expect.poll(() => w().saves.at(-1)).toMatchObject({ filmPath: FILM });
        await shot(page, 's2-saved');
      });

      test('3. фильм не собран: порядок, затемнение 2 с, подрезка, «Собрать» → «Собрано», строки ленты человека', async ({ page }) => {
        await openVideoChat(page, filmPrimary, { vp, theme, feed: feed() });
        await openMontage(page);
        const rows = montage(page).locator('[data-video-film-row]');
        await expect(rows).toHaveCount(4);
        if (!isPhone(page)) {
          await rows.nth(0).locator('[data-video-drag]').dragTo(rows.nth(2));
        } else {
          await rows.nth(0).getByRole('button', { name: 'Действия со сценой' }).click();
          await page.getByRole('button', { name: 'Позже', exact: true }).last().click();
        }
        await expect.poll(() => w().patches.some(p => JSON.stringify(p).includes('"move"'))).toBe(true);
        const cut = montage(page).locator('[data-video-cut="1"]');
        await cut.locator('button').first().click();
        await cut.getByRole('button', { name: 'затемнение' }).click();
        await cut.getByRole('button', { name: '2 с' }).click();
        await expect(cut.locator('button').first()).toHaveText('■ затемнение 2 с');
        // Подрезка в строке: конец − 0,5 с
        await rows.nth(1).getByRole('button', { name: /✂/ }).click();
        await montage(page).locator('[data-video-trim]').getByTitle('Конец − 0,5 с').click();
        await expect(montage(page).locator('[data-video-trim]')).toContainText('остаётся 7,5 с из 8 с');
        await shot(page, 's3-trim');
        await montage(page).locator('[data-video-trim]').getByRole('button', { name: 'Готово' }).click();
        await expect(rows.nth(1)).toContainText('✂ 7,5 с из 8 с');
        await expect(montage(page).locator('[data-video-music]')).toContainText('утро.mp3');
        // Сборка — из низа окна: ход, потом «Собрано»
        await buildButton(page, 'Собрать').click();
        await expect(page.locator('[data-video-montage-foot]')).toBeVisible();
        await shot(page, 's3-building');
        await expect(buildButton(page, 'Собрано')).toBeVisible({ timeout: 15_000 });
        await shot(page, 's3-built');
        // Лента человека: строки правки и сборки есть, а ручки фильма звали с sessionId
        expect(w().filmNoSession).toEqual([]);
        await page.getByRole('button', { name: 'Готово', exact: true }).click();
        await expect(montage(page)).toHaveCount(0);
        await expect(page.locator('[data-video-quiet="video_note"]').filter({ hasText: /^Вы (поправили фильм: |переставили|подрезали|поменяли|добавили|убрали)/ }).first()).toBeVisible();
        await expect(page.locator('[data-video-quiet="video_film_built"]').last()).toHaveText('Вы собрали фильм утро-в-горах: video/утро-в-горах/film.mp4');
        await shot(page, 's3-human-feed');
      });

      test('4. «Сочинить под фильм…» → чип «Песня» с заготовкой стиля и «↩ К фильму»', async ({ page }) => {
        await openVideoChat(page, filmPrimary, { vp, theme, feed: feed() });
        await openMontage(page);
        await montage(page).getByRole('button', { name: 'Сочинить под фильм…' }).click();
        await expect(montage(page)).toHaveCount(0);
        await expect(chip(page, 'song')).toHaveAttribute('aria-checked', 'true');
        await expect(page.locator('textarea').last()).toHaveValue(/Инструментальная музыка под фильм «утро-в-горах»/);
        await shot(page, 's4-song');
        await page.locator('[data-context-row] [data-chip="primary"]').click();
        const ret = page.locator('[data-ctx-return]');
        await expect(ret).toContainText('К фильму «утро-в-горах»');
        await ret.click();
        await expect.poll(() => primaryPuts().at(-1)).toMatchObject({ kind: 'video-film', ref: { filmPath: FILM } });
        // Черновик звука заведён сервером под этот фильм; его первая версия встаёт музыкой сама
        expect(w().musicFor).toBe(FILM);
      });

      test('5. «Править кадр» → «Картинки» → «К сцене»: кадр открыт в контексте, возврат ставит сцену', async ({ page }) => {
        await openVideoChat(page, scenePrimary(3), { vp, theme, feed: feed(), frames: true });
        await chip(page, 'frameB').click();
        await page.getByText('Править в «Картинках»').click();
        await expect.poll(() => primaryPuts().length).toBe(1);
        expect(primaryPuts()[0]).toMatchObject({ kind: 'image', ref: { threadId: 'img-1' } });
        // Нить заведена по файлу кадра B сцены 3, а не черновиком
        expect(w().imageThreads[0]).toMatchObject({ file: 'video/утро-в-горах/кадры/кадр-4.png' });
        await expect(chip(page, 'edit')).toBeVisible();
        await shot(page, 's5-images');
        await page.locator('[data-context-row] [data-chip="primary"]').click();
        const ret = page.locator('[data-ctx-return]');
        await expect(ret).toContainText('К сцене «Сцена 3»');
        await ret.click();
        await expect.poll(() => primaryPuts().at(-1)).toMatchObject({ kind: 'video-scene', ref: { sceneId: 'scene-3' } });
        await expect(chip(page, 'shoot')).toBeVisible();
        await shot(page, 's5-back');
      });

      test('6. просьба в поле ввода уходит параметром запуска и снимает по котировке', async ({ page }) => {
        await openVideoChat(page, scenePrimary(), { vp, theme, feed: feed(), frames: true });
        await expect(chip(page, 'shoot')).toHaveAttribute('aria-checked', 'true');
        const input = page.locator('textarea').last();
        await input.fill('медленнее, закат теплее');
        await shot(page, 's6-composer');
        await runBtn(page).click();
        await expect.poll(() => w().jobs.length).toBe(1);
        // Просьба уходит параметром запуска `request`, сервер добавляет её к тексту сцены
        expect(w().jobs[0]).toMatchObject({ params: { request: 'медленнее, закат теплее' } });
      });

      test('7. телефон и десктоп: строка чипов не даёт горизонтальной прокрутки, цена на кнопке цела', async ({ page }) => {
        await openVideoChat(page, scenePrimary(), { vp, theme, feed: feed(), frames: true });
        const over = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
        expect(over).toBeLessThanOrEqual(1);
        await expect(runBtn(page)).toContainText('$3.20', { timeout: 10_000 });
        const whole = await runBtn(page).evaluate(n => n.scrollWidth <= n.clientWidth + 1);
        expect(whole).toBe(true);
        await shot(page, 's7-bar');
      });

      test('10. съёмка: одна карточка сцены с ходом, строка запуска без полосы, процент ровно от поставщика, цена из «Чем»', async ({ page }) => {
        await openVideoChat(page, scenePrimary(), { vp, theme, feed: feed(), films: [standardFilm(true)], frames: true, focus: { sceneId: 'scene-5', filmPath: FILM }, autoFinish: false });
        if (isPhone(page) && await page.locator('[data-gen-sheet="sheet"]').isVisible()) {
          await page.getByTitle('Закрыть панель — сводка останется в полосе').click();
        }
        await expect(modeBar(page)).toContainText('≈ $3.20', { timeout: 10_000 });
        await runBtn(page).click();
        await expect.poll(() => w().jobs.length).toBe(1);
        const jobId = String(w().jobs[0].jobId);
        // Поставщик шлёт Percent варианта: 0,1 → 0,4 → 0,8, полоса показывает ровно его
        const progress = (percent: number) => hubSend({
          type: 'video_edit_progress', sessionId: S, scopeKey: P, jobId, sceneId: 'scene-5', stage: 'running', variant: 1, count: 1, initiator: 'human', percent,
        });
        progress(0.1);
        const bar = card(page, 5).locator('[data-video-card-progress]');
        const pct = async () => Number(/(\d+) %/.exec(await bar.innerText())?.[1] ?? NaN);
        // Ровно одна полная карточка сцены, полоса хода только в ней, строка запуска без полосы
        await expect(page.locator('[data-video-card="scene"][data-scene="scene-5"]')).toHaveCount(1);
        await expect(page.locator('[data-video-card-progress]')).toHaveCount(1);
        await expect(page.locator('[data-video-card="launch"][data-scene="scene-5"]')).toHaveCount(1);
        await expect(bar).toContainText('снимаем');
        // Процент и на карточке, и на кнопке ровно такой, какой прислал поставщик
        for (const [p, text] of [[0.1, '10 %'], [0.4, '40 %'], [0.8, '80 %']] as const) {
          progress(p);
          await expect.poll(pct, { timeout: 8_000 }).toBe(p * 100);
          await expect(runBtn(page)).toContainText(text);
        }
        // Отставшее событие не откатывает полосу назад
        progress(0.3);
        await page.waitForTimeout(1200);
        expect(await pct()).toBe(80);
        await shot(page, 's10-progress');
        finishJob(jobId);
        await expect(bar).toHaveCount(0);
        await expect(card(page, 5).locator('[data-video-player]')).toBeVisible({ timeout: 10_000 });
        // После съёмки кнопка снова несёт цену выбранного в «Чем» исполнителя
        await expect(modeBar(page)).toContainText('≈ $3.20', { timeout: 10_000 });
      });

      test('8. пустой чат: «Видео» из «＋» заводит сцену, кадры A и B берутся «Из проекта» как референсы контекста', async ({ page }) => {
        // Лента пуста: якоря сцен 1–5 из feed() дали бы вторую карточку «Сцена 1» рядом с созданной сценой
        await openVideoChat(page, null, { vp, theme, scenes: [], feed: [] });
        await page.getByRole('button', { name: /^Прикрепить файл, / }).click();
        await page.getByRole('menuitem', { name: /^Видео/ }).or(page.getByRole('button', { name: /^Видео/ })).first().click();
        await expect.poll(() => w().scenes.length).toBe(1);
        // Создание сцены — ровно одна карточка в ленте (ни второго якоря, ни строки запуска)
        await expect(page.locator('[data-video-card]')).toHaveCount(1);
        // Бэкенд зеркалит фокус сцен в контекст: новая сцена — основной объект, чипы на месте, «Снять» серое без кадров
        await expect(actions(page)).toBeVisible({ timeout: 10_000 });
        await expect(chip(page, 'shoot')).toHaveAttribute('aria-disabled', 'true');
        await shot(page, 's8-empty-scene');
        // Кадры A и B — из проекта: выбор стартует с корня, папки video/ может не быть
        for (const [slot, role, name] of [['frameA', 'frame-a', 'кадр-1.png'], ['frameB', 'frame-b', 'кадр-2.png']] as const) {
          await chip(page, slot).click();
          await page.getByText('Из проекта', { exact: true }).click();
          const picker = page.locator('[data-video-project-picker]');
          if (slot === 'frameA') {
            await picker.getByRole('button', { name: 'video', exact: true }).click();
            await picker.getByRole('button', { name: 'утро-в-горах', exact: true }).click();
            await picker.getByRole('button', { name: 'кадры', exact: true }).click();
          }
          await picker.getByRole('button', { name }).click();
          await expect.poll(() => cw().mutations.some(m => m.method === 'POST' && JSON.stringify(m.body).includes(role) && JSON.stringify(m.body).includes(name))).toBe(true);
        }
        await shot(page, 's8-frames');
      });

      test('8б. фильм: «Сцена из проекта» в монтаже добавляет сцену в фильм', async ({ page }) => {
        await openVideoChat(page, filmPrimary, { vp, theme, feed: feed() });
        await openMontage(page);
        await expect(montage(page).locator('[data-video-film-row]')).toHaveCount(4);
        await montage(page).getByRole('button', { name: 'Сцена из проекта' }).click();
        // Выбор стартует с папки фильма: сцена-файл лежит рядом
        const add = montage(page).locator('[data-video-add-scene]');
        await add.getByRole('button', { name: 'scene-06.mp4' }).click();
        await expect(montage(page).locator('[data-video-film-row]')).toHaveCount(5);
        await shot(page, 's8b-added');
      });

      test('9. агент основной объект не двигает, человек берёт сцену карточкой («Переснять»)', async ({ page }) => {
        await openVideoChat(page, scenePrimary(), { vp, theme, feed: feed(), frames: true, focus: { sceneId: 'scene-5' } });
        // Агент берёт сцену 2 в работу: фокус нитей меняется событием, основной объект человека остаётся
        agentFocus('scene-2');
        await expect(page.locator('[data-context-row] [data-chip="primary"]')).toContainText('Сцена 5');
        expect(primaryPuts()).toHaveLength(0);
        // Человек выбирает сцену 2 на её карточке в ленте: фокус сцен уходит на сервер (он же зеркалит его в контекст)
        await card(page, 2).getByRole('button', { name: 'Переснять' }).click();
        await expect.poll(() => w().focus.sceneId).toBe('scene-2');
        await shot(page, 's9-card');
      });
    });
  }
}

test('агент снимает: та же карточка запуска с ценой и ходом плюс «✦ Claude»', async ({ page }) => {
  await openVideoChat(page, filmPrimary, { vp: DESK, feed: feed() });
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
  // Полоса хода — в полной карточке сцены, строка запуска её не дублирует
  await expect(card(page, 2).locator('[data-video-card-progress]')).toContainText('снимаем 2 варианта');
  await expect(c.locator('[data-video-card-progress]')).toHaveCount(0);
  await expect(c.locator('[data-by-claude]')).toBeVisible();
  finishJob('job-agent');
  // Готовые варианты — в одной полной карточке сцены; у запуска остаётся компактная строка с итогом
  await expect(card(page, 2).locator('[data-video-player]')).toBeVisible();
  await expect(card(page, 2).getByRole('button', { name: 'Сохранить сцену' })).toBeVisible();
  await expect(c.locator('[data-video-player]')).toHaveCount(0);
  await expect(c.locator('[data-video-launch-outcome]')).toContainText('Готово: 2 варианта');
  // Агент основной объект человека не двигает: фильм остаётся «С чем»
  expect(primaryPuts()).toHaveLength(0);
  await saveShot(page, SHOTS, 'agent-launch-card-1440-light');
});

test('агент: тихие строки без слова «Claude» в тексте, лицо даёт одна метка «✦ Claude»', async ({ page }) => {
  await openVideoChat(page, filmPrimary, { vp: DESK, feed: feed() });
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
  await saveShot(page, SHOTS, 'agent-quiet-lines-1440-light');
});

test('503 dsp_unavailable: «Собрать» серое с причиной, «Проверить снова» оживляет', async ({ page }) => {
  await openVideoChat(page, filmPrimary, { vp: DESK, feed: feed(), dsp: false });
  await openMontage(page);
  await buildButton(page, 'Собрать').click();
  await expect(page.locator('[data-video-montage-foot]')).toContainText('Сборка фильмов на этом сервере выключена — обратитесь к администратору');
  await expect(buildButton(page, 'Собрать')).toBeDisabled();
  await saveShot(page, SHOTS, 'dsp-unavailable-1440-light');
  w().dsp = true;
  await montage(page).getByRole('button', { name: 'Проверить снова' }).click();
  await expect(buildButton(page, 'Собрать')).toBeEnabled();
});

test('409 revision_conflict: фильм перечитан и показан свежим', async ({ page }) => {
  await openVideoChat(page, filmPrimary, { vp: DESK, feed: feed() });
  await openMontage(page);
  w().films.get(FILM)!.revision = 'r9';
  w().films.get(FILM)!.document.items.pop();
  await montage(page).locator('[data-video-film-row="0"]').getByRole('button', { name: 'Действия со сценой' }).click();
  await page.getByRole('button', { name: 'Позже', exact: true }).last().click();
  await expect(page.getByText('Фильм только что поменяли в другом месте — показываем свежий')).toBeVisible();
  await expect(montage(page).locator('[data-video-film-row]')).toHaveCount(3);
});

test('клик по .film в дереве открывает панель «Контекст» с этим фильмом, film.mp4 проигрывается', async ({ page }) => {
  await openVideoChat(page, null, { vp: DESK, feed: feed(), focus: { sceneId: 'scene-5' } });
  await page.getByRole('button', { name: 'Файлы', exact: true }).first().click();
  for (const name of ['video', 'утро-в-горах']) await page.getByText(name, { exact: true }).first().click();
  await page.getByText('утро-в-горах.film', { exact: true }).first().click();
  await expect(page.locator('[data-context-panel]')).toBeVisible({ timeout: 20_000 });
  await saveShot(page, SHOTS, 'film-from-tree-1440-light');
  await page.getByText('film.mp4', { exact: true }).first().click();
  // Просмотр файлов ставит адрес в <source>, а не в src самого <video>
  const video = page.locator('video:has(source[src*="film.mp4"])').first();
  await expect(video).toBeVisible({ timeout: 20_000 });
  await expect.poll(() => video.evaluate(v => (v as HTMLVideoElement).readyState), { timeout: 15_000 }).toBeGreaterThanOrEqual(1);
  expect(await video.evaluate(v => (v as HTMLVideoElement).duration)).toBeGreaterThan(3);
  await video.evaluate(v => { (v as HTMLVideoElement).muted = true; return (v as HTMLVideoElement).play(); });
  await expect.poll(() => video.evaluate(v => (v as HTMLVideoElement).currentTime)).toBeGreaterThan(0);
  await saveShot(page, SHOTS, 'film-mp4-plays-1440-light');
});

for (const width of [800, 1024]) {
  test(`раскладка ${width}: чипы и строка контекста без переполнения, редактор «Монтаж» открывается`, async ({ page }) => {
    await openVideoChat(page, filmPrimary, { vp: { width, height: 800 }, feed: feed() });
    const over = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
    expect(over).toBeLessThanOrEqual(1);
    await saveShot(page, SHOTS, `layout-${width}-light`);
    await openMontage(page);
    await saveShot(page, SHOTS, `layout-${width}-montage-light`);
  });
}
