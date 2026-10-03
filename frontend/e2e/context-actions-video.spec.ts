import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { FILM, newWorld, mockApi, standardFilm, standardScenes, w, P, S } from './videoPanelMock';
import { cw, mockContext, newCtxWorld, primaryOf, primaryPuts } from './contextVideoMock';

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

async function registerVideo(page: Page) {
  await page.evaluate(async () => {
    const find = (part: string) => performance.getEntriesByType('resource').map(e => e.name).find(n => n.includes(part));
    const core = await import(/* @vite-ignore */ find('/src/lib/subsystems/registryCore.ts') ?? '/src/lib/subsystems/registryCore.ts');
    const mod = await import(/* @vite-ignore */ '/src/features/videoEditor/manifest.tsx');
    core.registerSubsystem({ ...mod.manifest, key: 'e2e-video', core: true, tab: undefined });
  });
}

async function open(page: Page, vp: { width: number; height: number }, primary: () => ReturnType<typeof primaryOf>) {
  newWorld({ scenes: standardScenes(), films: [standardFilm()], focus: {} });
  newCtxWorld(primary());
  await page.setViewportSize(vp);
  await page.addInitScript(() => {
    localStorage.setItem('cc_token', 'e2e-token');
    localStorage.setItem('cc_user_id', 'u1');
    localStorage.setItem('theme-mode', 'light');
  });
  await mockApi(page);
  await mockContext(page);
  await page.goto(`/#/project/${P}/chat/${S}`);
  await expect(page.locator('textarea').last()).toBeVisible({ timeout: 30_000 });
  await registerVideo(page);
  await expect(page.locator('[data-composer-actions]')).toBeVisible({ timeout: 15_000 });
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
}
