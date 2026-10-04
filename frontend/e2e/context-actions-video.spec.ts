import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { FILM, P, S, hubSend, w } from './videoPanelMock';
import { cw, openVideoChat, primaryOf, primaryPuts } from './contextVideoMock';

// 3ф-2: редакторы «Сцена» и «Монтаж» поверх панели «Контекст» (ADR-023). Чип «Монтаж» и «Открыть монтаж» в «С чем»
// открывают окно; «Работать со сценой» в списке сцен фильма делает сцену основной и ставит «К фильму «…»»;
// «Редактор сцены» правит текст основной сцены. Бэкенд не нужен: контекст — contextVideoMock, видео-API — videoPanelMock;
// манифест модуля регистрируется со страницы. Запуск:
//   cd frontend; npx vite --port 5327 --strictPort --host 127.0.0.1 &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5327 CA_SHOTS_DIR=../.cc-attachments/video-context npx playwright test e2e/context-actions-video.spec.ts

const SHOTS = process.env.CA_SHOTS_DIR || '';
test.use({ serviceWorkers: 'block' });
const D = { width: 1440, height: 900 };
const M = { width: 360, height: 780 };

async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, name) });
}

async function open(page: Page, vp: { width: number; height: number }, primary: () => ReturnType<typeof primaryOf>, frames = false) {
  await openVideoChat(page, primary, { vp, frames });
}

const actions = (page: Page) => page.locator('[data-composer-actions]');
const ids = (page: Page) => actions(page).locator('[data-action-chip]').evaluateAll(els => els.map(e => e.getAttribute('data-action-chip')));
const montage = (page: Page) => page.locator('[data-video-montage]');
const filmPrimary = () => primaryOf('video-film', { filmPath: FILM });
const scenePrimary = () => primaryOf('video-scene', { sceneId: 'scene-5' });

for (const { name, vp } of [{ name: '1440', vp: D }, { name: '360', vp: M }] as const) {
  test(`монтаж · ${name}: чип «Монтаж» → окно → «Работать со сценой» → «К фильму «…»» возвращает фильм`, async ({ page }) => {
    await open(page, vp, filmPrimary);
    // Фильм: «Чат · Собрать · Монтаж», основной объект чип не меняет
    expect(await ids(page)).toEqual(['__chat', 'build', 'montage']);
    await actions(page).locator('[data-action-chip="montage"]').click();
    await expect(montage(page)).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText(/^Монтаж · утро-в-горах/)).toBeVisible();
    await expect(montage(page).locator('[data-video-film-tab]')).toBeVisible();
    expect(primaryPuts()).toHaveLength(0);
    const done = page.getByRole('button', { name: 'Готово' });
    const db = await done.boundingBox();
    expect(db!.y + db!.height).toBeLessThanOrEqual(vp.height);
    expect(db!.x + db!.width).toBeLessThanOrEqual(vp.width);
    await shot(page, `montage-${name}.png`);

    // «Работать со сценой» из меню строки: сцена 2 становится основной, окно закрыто, ссылка назад поставлена
    await montage(page).getByRole('button', { name: 'Действия со сценой' }).nth(1).click();
    await page.getByText('Работать со сценой').click();
    await expect(montage(page)).toHaveCount(0);
    await expect.poll(() => primaryPuts().length).toBe(1);
    expect(primaryPuts()[0]).toMatchObject({ kind: 'video-scene', ref: { sceneId: 'scene-2' } });
    await expect.poll(() => ids(page)).toEqual(['__chat', 'shoot', 'frameA', 'frameB']);

    await page.locator('[data-context-row] [data-chip="primary"]').click();
    const ret = page.locator('[data-ctx-return]');
    await expect(ret).toBeVisible({ timeout: 10_000 });
    await expect(ret).toContainText('К фильму «утро-в-горах»');
    await shot(page, `return-${name}.png`);
    await ret.click();
    await expect.poll(() => primaryPuts().length).toBe(2);
    expect(primaryPuts()[1]).toMatchObject({ kind: 'video-film', ref: { filmPath: FILM } });
    await expect.poll(() => ids(page)).toEqual(['__chat', 'build', 'montage']);
    await expect(page.locator('[data-ctx-return]')).toHaveCount(0);
  });

  test(`монтаж · ${name}: «Открыть монтаж» в «С чем» и сборка из низа окна`, async ({ page }) => {
    await open(page, vp, filmPrimary);
    await page.locator('[data-context-row] [data-chip="primary"]').click();
    const panel = page.locator('[data-context-panel]');
    await expect(panel).toBeVisible({ timeout: 10_000 });
    await panel.getByRole('button', { name: 'Открыть монтаж' }).click();
    await expect(montage(page)).toBeVisible({ timeout: 10_000 });
    const foot = page.locator('[data-video-montage-foot]');
    await expect(foot).toContainText('4 сцены');
    await page.getByRole('button', { name: 'Собрать', exact: true }).click();
    // Сборка идёт без ИИ: низ окна показывает ход, потом результат — кнопка «Собрано»
    await expect(page.getByRole('button', { name: 'Собрано', exact: true })).toBeVisible({ timeout: 15_000 });
    await page.getByRole('button', { name: 'Готово' }).click();
    await expect(montage(page)).toHaveCount(0);
  });

  test(`редактор сцены · ${name}: «Редактор сцены» правит текст основной сцены`, async ({ page }) => {
    await open(page, vp, scenePrimary);
    await page.locator('[data-context-row] [data-chip="primary"]').click();
    const panel = page.locator('[data-context-panel]');
    await expect(panel).toBeVisible({ timeout: 10_000 });
    await panel.getByRole('button', { name: 'Редактор сцены' }).click();
    const ed = page.locator('[data-video-scene-editor]');
    await expect(ed).toBeVisible({ timeout: 10_000 });
    await expect(page.getByText('Сцена · Сцена 5')).toBeVisible();
    const text = ed.locator('textarea');
    await text.fill('Туман над озером, камера поднимается');
    await shot(page, `scene-editor-${name}.png`);
    // Правка уходит в основную сцену контекста (серверный фокус пуст)
    await expect.poll(() => w().scenes.find(s => s.sceneId === 'scene-5')!.settings.text, { timeout: 10_000 }).toBe('Туман над озером, камера поднимается');
    await page.getByRole('button', { name: 'Готово' }).click();
    await expect(ed).toHaveCount(0);
    expect(cw().mutations.filter(m => m.method === 'PUT')).toHaveLength(0);
  });

  // Сценарий 8 макета: «Нарисовать в «Картинках»» из меню кадра — черновик становится основным объектом,
  // чип «Нарисовать» выбран, «↩ К сцене» возвращает сцену; панели «Картинки» нет
  test(`кадр · ${name}: «Нарисовать в «Картинках»» → чип «Нарисовать» → «К сцене» возвращает сцену`, async ({ page }) => {
    await open(page, vp, scenePrimary);
    await actions(page).locator('[data-action-chip="frameA"]').click();
    await page.getByText('Нарисовать в «Картинках»').click();
    await expect.poll(() => primaryPuts().length).toBe(1);
    expect(primaryPuts()[0]).toMatchObject({ kind: 'image', ref: { threadId: 'img-1' } });
    await expect.poll(() => ids(page)).toEqual(['__chat', 'draw']);
    await expect(actions(page).locator('[data-action-chip="draw"]')).toHaveAttribute('aria-checked', 'true');
    await expect(page.locator('[data-composer-actions]')).toBeVisible();
    await shot(page, `draw-frame-${name}.png`);
    if (vp.width > 600) await expect(page.locator('[data-context-panel]')).toBeVisible({ timeout: 10_000 });
    else await expect(page.locator('[data-context-panel]')).toHaveCount(0);

    await page.locator('[data-context-row] [data-chip="primary"]').click();
    const ret = page.locator('[data-ctx-return]');
    await expect(ret).toContainText('К сцене «Сцена 5»');
    await ret.click();
    await expect.poll(() => primaryPuts().length).toBe(2);
    expect(primaryPuts()[1]).toMatchObject({ kind: 'video-scene', ref: { sceneId: 'scene-5' } });
    await expect.poll(() => ids(page)).toEqual(['__chat', 'shoot', 'frameA', 'frameB']);
  });

  // Приёмка Веры, Д4/Д5/Д9: новый проект без папки video/кадры — папка заводится сама (иначе 400), ссылка
  // «К сцене» на месте, а готовая картинка встаёт кадром именно в сцену (у основной картинки роль кадра — 400)
  test(`кадр · ${name}: «Нарисовать в «Картинках»» в новом проекте без папки → «К сцене» → готовая картинка встаёт в слот A`, async ({ page }) => {
    await open(page, vp, scenePrimary);
    await actions(page).locator('[data-action-chip="frameA"]').click();
    await page.getByText('Нарисовать в «Картинках»').click();
    await expect.poll(() => primaryPuts().length).toBe(1);
    expect(primaryPuts()[0]).toMatchObject({ kind: 'image', ref: { threadId: 'img-1' } });
    expect(w().imageThreads[0]).toMatchObject({ draftFolder: 'video/кадры' });
    await page.locator('[data-context-row] [data-chip="primary"]').click();
    await expect(page.locator('[data-ctx-return]')).toContainText('К сцене «Сцена 5»');

    // Первая версия готова, пока основной объект — картинка: сцена возвращается основной, кадр встаёт в слот
    w().imageThreads[0] = {
      ...w().imageThreads[0], currentVersionId: 'v1',
      versions: [{ id: 'v1', number: 1, jobId: 'j1', variant: 0, baseVersionId: null, baseStepId: null, steps: [], currentStepId: 'st1', createdAt: new Date().toISOString() }],
    };
    w().imageRevision++;
    hubSend({ type: 'image_thread_changed', sessionId: S, projectId: P, revision: w().imageRevision, state: { focus: 'img-1', revision: w().imageRevision, threads: w().imageThreads } });
    await expect.poll(() => primaryPuts().length).toBe(2);
    expect(primaryPuts()[1]).toMatchObject({ kind: 'video-scene', ref: { sceneId: 'scene-5' } });
    await expect.poll(() => cw().mutations.find(m => m.path === '/refs')?.body).toMatchObject({ kind: 'image', role: 'frame-a', ref: { threadId: 'img-1', versionId: 'v1' } });
    expect(cw().ctx.refs).toHaveLength(1);
    await expect(page.locator('[data-ctx-return]')).toHaveCount(0);
    await expect.poll(() => ids(page)).toEqual(['__chat', 'shoot', 'frameA', 'frameB']);
  });

  // Д9 (блокер Глеба): генерация идёт минуты, человек ушёл на фильм — готовый кадр не перебивает выбор:
  // основной остаётся фильмом, в тосте «Поставить в сцену», по нажатию сцена основная и кадр в слоте A
  test(`кадр · ${name}: ушёл на фильм → готовый кадр не перебивает выбор, «Поставить в сцену» ставит кадр`, async ({ page }) => {
    await open(page, vp, scenePrimary);
    await actions(page).locator('[data-action-chip="frameA"]').click();
    await page.getByText('Нарисовать в «Картинках»').click();
    await expect.poll(() => primaryPuts().length).toBe(1);
    // Человек ставит основным фильм
    cw().ctx.primary = filmPrimary();
    cw().ctx.revision++;
    hubSend({ type: 'chat_context_changed', sessionId: S, context: cw().ctx });
    await expect.poll(() => ids(page)).toEqual(['__chat', 'build', 'montage']);

    w().imageThreads[0] = {
      ...w().imageThreads[0], currentVersionId: 'v1',
      versions: [{ id: 'v1', number: 1, jobId: 'j1', variant: 0, baseVersionId: null, baseStepId: null, steps: [], currentStepId: 'st1', createdAt: new Date().toISOString() }],
    };
    w().imageRevision++;
    hubSend({ type: 'image_thread_changed', sessionId: S, projectId: P, revision: w().imageRevision, state: { focus: 'img-1', revision: w().imageRevision, threads: w().imageThreads } });
    const toast = page.getByText('Кадр A для «Сцена 5» готов');
    await expect(toast).toBeVisible({ timeout: 10_000 });
    expect(primaryPuts()).toHaveLength(1);
    expect(cw().mutations.some(m => m.path === '/refs')).toBe(false);
    expect(cw().ctx.primary).toMatchObject({ kind: 'video-film' });

    await page.getByRole('button', { name: 'Поставить в сцену' }).click();
    await expect.poll(() => primaryPuts().length).toBe(2);
    expect(primaryPuts()[1]).toMatchObject({ kind: 'video-scene', ref: { sceneId: 'scene-5' } });
    await expect.poll(() => cw().mutations.find(m => m.path === '/refs')?.body).toMatchObject({ kind: 'image', role: 'frame-a', ref: { threadId: 'img-1', versionId: 'v1' } });
    await expect.poll(() => ids(page)).toEqual(['__chat', 'shoot', 'frameA', 'frameB']);
  });

  // «Править кадр»: пункт меню кадра с файлом ведёт в «Картинки» — нить заводится по файлу кадра и становится
  // основным объектом (правка, а не рисование: предвыбора «Нарисовать» нет), «↩ К сцене» возвращает сцену
  test(`кадр · ${name}: «Править в «Картинках»» → картинка кадра в контексте → «К сцене» возвращает сцену`, async ({ page }) => {
    await open(page, vp, scenePrimary, true);
    await actions(page).locator('[data-action-chip="frameB"]').click();
    await page.getByText('Править в «Картинках»').click();
    await expect.poll(() => primaryPuts().length).toBe(1);
    expect(primaryPuts()[0]).toMatchObject({ kind: 'image', ref: { threadId: 'img-1' } });
    // Нить заведена по файлу кадра B, а не черновиком
    expect(w().imageThreads[0]).toMatchObject({ file: 'video/утро-в-горах/кадры/кадр-6.png' });
    await expect.poll(async () => (await ids(page)).includes('draw')).toBe(false);
    await expect(actions(page).locator('[data-action-chip="edit"]')).toBeVisible();
    await shot(page, `edit-frame-${name}.png`);
    if (vp.width > 600) await expect(page.locator('[data-context-panel]')).toBeVisible({ timeout: 10_000 });
    else await expect(page.locator('[data-context-panel]')).toHaveCount(0);

    await page.locator('[data-context-row] [data-chip="primary"]').click();
    const ret = page.locator('[data-ctx-return]');
    await expect(ret).toContainText('К сцене «Сцена 5»');
    await ret.click();
    await expect.poll(() => primaryPuts().length).toBe(2);
    expect(primaryPuts()[1]).toMatchObject({ kind: 'video-scene', ref: { sceneId: 'scene-5' } });
    await expect.poll(() => ids(page)).toEqual(['__chat', 'shoot', 'frameA', 'frameB']);
  });

  // «Сочинить под фильм…» из монтажа: окно закрывается, черновик звука — основной объект, чип «Песня» выбран и
  // заправлен описанием стиля, «↩ К фильму» возвращает фильм
  test(`монтаж · ${name}: «Сочинить под фильм…» → чип «Песня» с предвыбором → «К фильму» возвращает фильм`, async ({ page }) => {
    await open(page, vp, filmPrimary);
    await actions(page).locator('[data-action-chip="montage"]').click();
    await expect(montage(page)).toBeVisible({ timeout: 10_000 });
    await montage(page).getByRole('button', { name: 'Сочинить под фильм…' }).click();
    await expect(montage(page)).toHaveCount(0);
    await expect.poll(() => primaryPuts().length).toBe(1);
    expect(primaryPuts()[0]).toMatchObject({ kind: 'audio', ref: { threadId: 'audio-1' } });
    expect(w().musicFor).toBe(FILM);
    await expect.poll(() => ids(page)).toEqual(['__chat', 'speak', 'song', 'sfx']);
    await expect(actions(page).locator('[data-action-chip="song"]')).toHaveAttribute('aria-checked', 'true');
    await expect(page.locator('textarea').last()).toHaveValue(/Инструментальная музыка под фильм «утро-в-горах»/);
    await shot(page, `compose-song-${name}.png`);

    await page.locator('[data-context-row] [data-chip="primary"]').click();
    const ret = page.locator('[data-ctx-return]');
    await expect(ret).toContainText('К фильму «утро-в-горах»');
    await ret.click();
    await expect.poll(() => primaryPuts().length).toBe(2);
    expect(primaryPuts()[1]).toMatchObject({ kind: 'video-film', ref: { filmPath: FILM } });
  });
}
