import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { newWorld, openChat, primary, P, S, w } from './contextRowMock';

// Чипы действий и наполнение контекста РЕАЛЬНОГО вида «картинка» (ADR-023, шаги 2к-1 и 2к-2). Бэкенд не нужен: контекст чата и хабы — моки
// (contextRowMock), API картинок — встроенный мок модуля (ключ localStorage cc-image-editor-mock), вклад
// `context-kind` регистрируется из страницы тем же модулем, что подключает манифест. Запуск:
//   cd frontend; npx vite --port 5322 --strictPort --host 127.0.0.1 &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5322 CA_SHOTS_DIR=../.cc-attachments/composer-actions npx playwright test e2e/context-actions-image.spec.ts

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

// ── 2к-2: наполнение контекста (макет composer-actions-v1, сценарии 1, 2, 3, 6, 7) ──
// Лента: якорь нити hero.png → карточка версии. Весь манифест вертикали регистрируется из страницы
// (карточки ленты, панель «Персонажи», вид контекста), мок API картинок — встроенный, нити — мок маршрута.

const VIEWPORTS = [
  { name: '1440', vp: D },
  { name: '1024', vp: { width: 1024, height: 800 } },
  { name: '360', vp: M },
] as const;

const HERO = 'thread-hero';
const anchor = (id: string) => ({
  kind: 'module_record', module: 'imageeditor', recordType: 'image_thread', data: { threadId: id, versionId: 'origin' }, fallback: '',
  timestamp: Date.parse('2026-10-03T10:00:00Z') - 30_000,
});
const heroThread = () => ({
  id: HERO, file: 'images/hero.png', lineage: [], draftFolder: null, stacks: [], currentStackId: null, currentStepId: null,
  settings: null, pendingJobId: null, createdAt: '2026-10-03T09:00:00Z', currentVersionId: 'origin', launches: [],
  versions: [{ id: 'origin', number: 0, jobId: null, variant: null, baseVersionId: null, baseStepId: null, steps: [], currentStepId: null, createdAt: '2026-10-03T09:00:00Z' }],
});

async function registerFull(page: Page, o: { characters?: boolean } = {}) {
  await page.evaluate(async ({ P, o }) => {
    const find = (part: string) => performance.getEntriesByType('resource').map(e => e.name).find(n => n.includes(part));
    const core = await import(/* @vite-ignore */ find('/src/lib/subsystems/registryCore.ts') ?? '/src/lib/subsystems/registryCore.ts');
    const mod = await import(/* @vite-ignore */ '/src/features/imageEditor/manifest.tsx');
    if (o.characters) {
      const api = await import(/* @vite-ignore */ '/src/features/imageEditor/api.ts');
      await api.imageEditorApi().createCharacter(P, { name: 'Аня', photos: [new Blob(['x'], { type: 'image/jpeg' })] });
    }
    core.registerSubsystem({ ...mod.manifest, key: 'e2e-image', core: true, tab: undefined });
  }, { P, o });
}

const card = (page: Page) => page.locator('[data-image-version]').first();
const panelCtx = (page: Page) => page.locator('[data-context-panel]');
const runBtn = (page: Page) => page.locator('[data-composer-mode-bar] button').last();

async function openFeed(page: Page, vp: { width: number; height: number }, theme: 'light' | 'dark', o: { primary?: ReturnType<typeof primary> | null; characters?: boolean } = {}) {
  newWorld({ feed: [anchor(HERO)], threads: [heroThread()], flags: { 'image-editor': true }, ctx: { primary: o.primary ?? null } });
  await page.addInitScript(() => localStorage.setItem('cc-image-editor-mock', 'all'));
  await openChat(page, { vp, theme });
  await expect(page.locator('textarea').last()).toBeVisible({ timeout: 30_000 });
  await registerFull(page, { characters: o.characters });
}

for (const { name, vp } of VIEWPORTS) {
  for (const theme of ['light', 'dark'] as const) {
    test.describe(`наполнение контекста · ${name} · ${theme}`, () => {
      test(`1 · правка из ленты: «Работать с этой» → чипы, «Изменить» выбран → текст → запуск`, async ({ page }) => {
        await openFeed(page, vp, theme);
        await expect(card(page)).toBeVisible({ timeout: 15_000 });
        // До выбора объекта в контексте пусто: чипов нет
        await expect(actions(page)).toHaveCount(0);
        await card(page).getByRole('button', { name: 'Работать с этой' }).click();
        await expect(actions(page)).toBeVisible();
        expect(await ids(page)).toEqual(['__chat', 'edit', 'removeBg', 'upscale', 'outpaint', 'mark']);
        await expect(actions(page).locator('[data-action-chip="edit"]')).toHaveAttribute('aria-checked', 'true');
        await expect(card(page)).toContainText('В работе');
        // На телефоне «Работать с этой» панель не поднимает: шторка закрыла бы поле ввода
        if (name === '360') await expect(panelCtx(page)).toHaveCount(0);
        // Кнопка серая, пока нет текста
        await expect(page.locator('[data-composer-mode-bar]')).toContainText('✦ Изменить');
        await expect(runBtn(page)).toBeDisabled();
        await page.locator('[data-composer-input] textarea').fill('сделай небо закатным');
        await expect(runBtn(page)).toBeEnabled();
        await expect(runBtn(page)).toContainText('✦ Изменить');
        await runBtn(page).click();
        // Запуск принят: кнопка показывает ход или итог
        await expect(page.locator('[data-composer-mode-bar]')).toContainText(/Изменя|Готово|✦ Изменить/, { timeout: 10_000 });
        expect(w().mutations.some(m => m.method === 'PUT' && m.path === '/primary')).toBe(true);
        await shot(page, `s1-${name}-${theme}.png`);
      });

      test(`2 · убрать фон: чип → «Текст не нужен» → запуск`, async ({ page }) => {
        await openFeed(page, vp, theme);
        await expect(card(page)).toBeVisible({ timeout: 15_000 });
        await card(page).getByRole('button', { name: 'Работать с этой' }).click();
        await actions(page).locator('[data-action-chip="removeBg"]').click();
        await expect(actions(page).locator('[data-action-chip="removeBg"]')).toHaveAttribute('aria-checked', 'true');
        await expect(page.locator('[data-composer-input] textarea')).toHaveAttribute('placeholder', /Текст не нужен/);
        await expect(runBtn(page)).toContainText('✦ Убрать фон');
        await expect(runBtn(page)).toBeEnabled();
        await runBtn(page).click();
        await expect(page.locator('[data-composer-mode-bar]')).toContainText(/Убира|Готово|✦ Убрать фон/, { timeout: 10_000 });
        await shot(page, `s2-${name}-${theme}.png`);
      });

      test(`3 · дорисовать: чип → «Пропорции» под чипами → 9:16 → запуск`, async ({ page }) => {
        await openFeed(page, vp, theme);
        await expect(card(page)).toBeVisible({ timeout: 15_000 });
        await card(page).getByRole('button', { name: 'Работать с этой' }).click();
        await actions(page).locator('[data-action-chip="outpaint"]').click();
        await expect(actions(page).getByText('Пропорции')).toBeVisible();
        await actions(page).getByRole('radio', { name: '9:16' }).or(actions(page).getByText('9:16', { exact: true })).first().click();
        await expect(runBtn(page)).toContainText('✦ Дорисовать');
        await expect(runBtn(page)).toBeEnabled();
        await runBtn(page).click();
        await expect(page.locator('[data-composer-mode-bar]')).toContainText(/Дорисов|Готово/, { timeout: 10_000 });
        await shot(page, `s3-${name}-${theme}.png`);
      });

      test(`6 · объект агента: чипы есть, выбран «Чат», «Увеличить» → «Чем» в строке → запуск`, async ({ page }) => {
        await openFeed(page, vp, theme, { primary: primary({ by: 'agent', ref: { threadId: HERO, versionId: 'origin' } }) });
        await expect(actions(page)).toBeVisible({ timeout: 15_000 });
        await expect(actions(page).locator('[data-action-chip="__chat"]')).toHaveAttribute('aria-checked', 'true');
        await expect(page.locator('[data-context-row] [data-chip="exec"]'), 'в «Чате» исполнителя нет').toHaveCount(0);
        // Карточка объекта агента помечена ✦, панель от действий агента не двигается
        await expect(card(page)).toContainText('В работе ✦');
        await expect(panelCtx(page)).toHaveCount(0);
        await actions(page).locator('[data-action-chip="upscale"]').click();
        await expect(page.locator('[data-context-row] [data-chip="exec"]')).toBeVisible({ timeout: 10_000 });
        await expect(runBtn(page)).toContainText('✦ Увеличить');
        await runBtn(page).click();
        await expect(page.locator('[data-composer-mode-bar]')).toContainText(/Увелич|Готово/, { timeout: 10_000 });
        await shot(page, `s6-${name}-${theme}.png`);
      });

      test(`7 · панель: чип объекта → панель → «Чем» → «Добавить из…» → «Персонажей» → «В контекст» у Ани → 3 варианта на кнопке`, async ({ page }) => {
        await openFeed(page, vp, theme, { primary: primary({ ref: { threadId: HERO, versionId: 'origin' } }), characters: true });
        await expect(actions(page)).toBeVisible({ timeout: 15_000 });
        await expect(actions(page).locator('[data-action-chip="edit"]')).toHaveAttribute('aria-checked', 'true');
        const open = async () => {
          await page.locator('[data-context-row] [data-chip="primary"]').click();
          await expect(panelCtx(page)).toBeVisible({ timeout: 10_000 });
        };
        await open();
        // «Чем»: список раскрыт сразу (макет), сворачивать нечего — выбираем FLUX Kontext
        await panelCtx(page).getByText('FLUX Kontext').first().click();
        if (name !== '360') {
          // Библиотека «Персонажи» открывается отдельной панелью, Аня встаёт референсом с ролью «персонаж».
          // На телефоне панелей зоны нет, сценарий макета 9 до них не доходит
          await panelCtx(page).getByRole('button', { name: /Добавить из/ }).click();
          await page.getByText('Из «Персонажей»').click();
          const chars = page.locator('[data-character="anya"]');
          await expect(chars).toBeVisible({ timeout: 10_000 });
          await chars.locator('[data-context-add="add"] button').click();
          await expect(chars.locator('[data-context-add="in"]')).toBeVisible();
          expect(w().mutations.some(m => m.method === 'POST' && m.path === '/refs'
            && (m.body as { kind?: string; role?: string }).kind === 'image-character' && (m.body as { role?: string }).role === 'character')).toBe(true);
          // Назад на «Контекст»: Аня в «Плюс»
          await open();
          await expect(panelCtx(page).locator('[data-ctx-ref="on"]')).toContainText('Аня');
        }
        // «Вариантов +»: умолчание 1 (макет) → 3, на кнопке поля и в низу панели «3 вар.» (на 360 — «×3»)
        await panelCtx(page).getByRole('button', { name: 'Больше' }).click();
        await panelCtx(page).getByRole('button', { name: 'Больше' }).click();
        const three = /3 вар\.|×3/;
        await expect(runBtn(page)).toContainText(three);
        await expect(page.locator('[data-ctx-run]')).toContainText(three);
        await shot(page, `s7-${name}-${theme}.png`);
      });
    });
  }
}

// ── 2к-3: отметки, образцы с диска и персонаж как референсы (макет composer-actions-v1, сценарий 4) ──

// Картинка нити: у файла из мока маршрута нет, а размер холста редактору нужен — отдаём svg с натуральным размером
const HERO_SVG = '<svg xmlns="http://www.w3.org/2000/svg" width="400" height="300"><rect width="400" height="300" fill="#7aa"/></svg>';
async function routeHero(page: Page) {
  await page.route(u => decodeURIComponent(u.href).includes('hero.png'), r => r.fulfill({ contentType: 'image/svg+xml', body: HERO_SVG }));
}

const MARKS_VIEWPORTS = [VIEWPORTS[0], VIEWPORTS[2]] as const;

for (const { name, vp } of MARKS_VIEWPORTS) {
  for (const theme of ['light', 'dark'] as const) {
    test(`4 · отметки: «Отметить» → редактор → две отметки → «Готово» → «Изменить отмеченное» → текст → запуск · ${name} · ${theme}`, async ({ page }) => {
      await openFeed(page, vp, theme, { primary: primary({ ref: { threadId: HERO, versionId: 'origin' } }) });
      await routeHero(page);
      await expect(actions(page)).toBeVisible({ timeout: 15_000 });
      await expect(actions(page).locator('[data-action-chip="edit"]')).toContainText('Изменить');
      await expect(page.locator('[data-composer-note]')).toHaveCount(0);

      await actions(page).locator('[data-action-chip="mark"]').click();
      const canvas = page.locator('svg[viewBox="0 0 400 300"]').last();
      await expect(canvas).toBeVisible({ timeout: 15_000 });
      const box = (await canvas.boundingBox())!;
      for (const fy of [0.3, 0.6]) {
        await page.mouse.move(box.x + box.width * 0.3, box.y + box.height * fy);
        await page.mouse.down();
        await page.mouse.move(box.x + box.width * 0.5, box.y + box.height * fy, { steps: 4 });
        await page.mouse.move(box.x + box.width * 0.7, box.y + box.height * fy, { steps: 4 });
        await page.mouse.up();
      }
      await page.getByRole('button', { name: 'Готово' }).click();

      await expect(actions(page).locator('[data-action-chip="edit"]')).toContainText('Изменить отмеченное', { timeout: 10_000 });
      // Метка «Отмечено: N ✕» — только на компьютере: на телефоне место нужно кнопке запуска
      if (name === '360') await expect(page.locator('[data-composer-note]')).toHaveCount(0);
      else await expect(page.locator('[data-composer-note]')).toContainText('Отмечено: 2');
      await expect(page.locator('[data-composer-mode-bar]')).toContainText('✦ Изменить');
      await page.locator('[data-composer-input] textarea').fill('убери лишнее');
      await expect(runBtn(page)).toBeEnabled();
      await shot(page, `s4-${name}-${theme}.png`);
      await runBtn(page).click();
      await expect(page.locator('[data-composer-mode-bar]')).toContainText(/Изменя|Готово|✦ Изменить/, { timeout: 10_000 });
      // Запуск снимает отметки: чип снова «Изменить», метки нет
      await expect(actions(page).locator('[data-action-chip="edit"]')).not.toContainText('отмеченное', { timeout: 10_000 });
      await expect(page.locator('[data-composer-note]')).toHaveCount(0);
    });
  }
}

test('метка «Отмечено: N ✕»: ✕ снимает отметки, чип возвращается к «Изменить»', async ({ page }) => {
  await openFeed(page, D, 'light', { primary: primary({ ref: { threadId: HERO, versionId: 'origin' } }) });
  await expect(actions(page)).toBeVisible({ timeout: 15_000 });
  await page.evaluate(async () => {
    const store = await import(/* @vite-ignore */ '/src/features/imageEditor/thread/threadStore.ts');
    const m = { type: 'mask', points: [[1, 1], [9, 9]], width: 4 };
    store.setThreadMarks('thread-hero', [m, m], { w: 100, h: 100 });
  });
  await expect(page.locator('[data-composer-note]')).toContainText('Отмечено: 2');
  await page.locator('[data-composer-note]').getByText('×').click();
  await expect(page.locator('[data-composer-note]')).toHaveCount(0);
  await expect(actions(page).locator('[data-action-chip="edit"]')).not.toContainText('отмеченное');
});

for (const { name, vp } of MARKS_VIEWPORTS) {
  test(`образец с компьютера: «Добавить из…» → «С компьютера» → роль → референс в «Плюс» → переживает перезагрузку · ${name}`, async ({ page }) => {
    const openPanel = async () => {
      await page.locator('[data-context-row] [data-chip="primary"]').click();
      await expect(panelCtx(page)).toBeVisible({ timeout: 10_000 });
    };
    await openFeed(page, vp, 'light', { primary: primary({ ref: { threadId: HERO, versionId: 'origin' } }) });
    await expect(actions(page)).toBeVisible({ timeout: 15_000 });
    await openPanel();
    await panelCtx(page).getByRole('button', { name: /Добавить из/ }).click();
    await page.getByText('С компьютера').click();
    await page.locator('input[data-ctx-upload]').setInputFiles({ name: 'cat.png', mimeType: 'image/png', buffer: Buffer.from('x') });
    await page.getByRole('menuitem', { name: 'Как образец стиля' }).or(page.getByText('Как образец стиля', { exact: true })).first().click();
    await expect(panelCtx(page).locator('[data-ctx-ref="on"]')).toContainText('образец', { timeout: 10_000 });
    const post = w().mutations.find(m => m.method === 'POST' && m.path === '/refs');
    expect(post?.body).toMatchObject({ kind: 'image', role: 'style', ref: { upload: expect.stringMatching(/^up/) } });

    // Перезагрузка: образец лежит в контексте на сервере, а не в памяти вкладки
    await page.reload();
    await expect(page.locator('textarea').last()).toBeVisible({ timeout: 30_000 });
    await registerFull(page);
    await expect(actions(page)).toBeVisible({ timeout: 15_000 });
    await openPanel();
    await expect(panelCtx(page).locator('[data-ctx-ref="on"]')).toContainText('образец', { timeout: 10_000 });
  });
}
