import { test, expect, type Locator, type Page } from '@playwright/test';
import fs from 'node:fs';
import {
  CAT, Counter, H, brush, catThread, heroThread, imageComposer, imageToggle, input, newWorld, openChat, panel, shot, strip, w,
  type Thread,
} from './imagePanelMock';

// Панель «Картинки» v5 (флаг image-panel-v5 включён, макет docs/mockups/image-panel-v5.html,
// вариант 1): все 9 сценариев макета со счётом кликов и переходов между зонами, личный чат,
// две вкладки одного чата, плашка «Вернуть» на 360 под кнопкой AI и кадры 360/1440 обеих тем.
// Бэкенд не нужен: собранный dist раздаётся статикой, /api/** и хаб — моки (imagePanelMock.ts).
//
//   (cd dist && python3 -m http.server 5232) &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5232 I4_SHOTS_DIR=../.cc-attachments/image-i4 \
//     npx playwright test e2e/image-panel-v5.spec.ts

const SHOTS = process.env.I4_SHOTS_DIR || '';
const TRACE = !!process.env.I4_TRACE;
const W = { width: 1440, height: 900 };
const M = { width: 360, height: 780 };

async function open(page: Page, vp: { width: number; height: number }, theme: 'light' | 'dark', focus: string | null, mode: 'edit' | 'create', threads: Thread[], personal = false) {
  newWorld({ focus, threads, v5: true, personal });
  await openChat(page, { vp, theme, mode, trace: TRACE });
}

const seg = (page: Page, i: 0 | 1) => strip(page).locator('[data-images-mode-switch] button').nth(i);
const sendBtn = (page: Page) => page.getByRole('button', { name: /^(Изменить|Сгенерировать)( отмеченное)? · / });
const summary = (page: Page) => page.locator('[data-images-settings-toggle] button');
// ✕ на чипе «Работаем с»; на телефоне — отдельная кнопка 40×40 рядом с миниатюрой
const chipX = (page: Page) => strip(page).getByRole('button', { name: 'Снять выбор картинки' })
  .or(strip(page).getByText('×', { exact: true })).first();
const release = (page: Page) => page.locator('[data-images-release]');

// Панель «Картинки»: на 360 полоса свёрнута в строку — её сводка открывает шторку
async function openPanel(page: Page) {
  await expect(page.locator('[data-composer-strip="images"]')).toBeVisible({ timeout: 30_000 });
  const mini = page.locator('[data-images-strip="mini"] [data-images-summary]');
  if (await mini.isVisible()) await mini.click();
  else await summary(page).click();
}

test.afterEach(async ({ page }, info) => {
  if (info.status !== info.expectedStatus) {
    fs.mkdirSync('/tmp/i4', { recursive: true });
    await page.screenshot({ path: `/tmp/i4/fail-${info.title.slice(0, 12).replace(/[^\p{L}\d]+/gu, '_')}.png` }).catch(() => {});
  }
});

test.describe('лента: черновик и кисть', () => {
  for (const vp of [{ name: '1440', v: W }, { name: '360', v: M }]) {
    test(`${vp.name}: черновик «Новая картинка» не рисуется карточкой, кисть «Отметить» — только в режиме «Картинка»`, async ({ page }) => {
      await open(page, vp.v, 'light', H, 'edit', [heroThread()]);
      await imageComposer(page);
      if (vp.v === W) await expect(page.locator('[data-image-brush]')).toBeVisible();
      await seg(page, 0).click().catch(async () => { await openPanel(page); await seg(page, 0).click(); });
      await expect.poll(() => w().threads.map(t => t.id)).toEqual([H, 'thread-draft']);
      // Черновик завёлся, но в ленте плашки нет
      await expect(page.locator('[data-image-draft]')).toHaveCount(0);
      await expect(page.getByText('Опишите её в поле ввода')).toHaveCount(0);
      await expect(page.getByText('Ещё не нарисована')).toHaveCount(0);
      await shot(page, SHOTS, `${vp.name}-no-draft-card`);
    });
  }

  test('1440: «Отметить» скрыта в режиме «Чат»', async ({ page }) => {
    await open(page, W, 'light', H, 'edit', [heroThread()]);
    await imageComposer(page);
    await expect(page.locator('[data-image-brush]')).toBeVisible();
    await page.getByRole('button', { name: 'Режим «Чат»' }).click();
    await expect(page.locator('[data-image-brush]')).toHaveCount(0);
    await shot(page, SHOTS, '1440-brush-chat-mode');
    await imageToggle(page).click();
    await expect(page.locator('[data-image-brush]')).toBeVisible();
  });
});

test.describe('сценарии v5 со счётом кликов', () => {
  test('1. нарисовать новую и ещё вариант — 3 / 0', async ({ page }) => {
    await open(page, W, 'light', H, 'edit', [heroThread()]);
    await imageComposer(page);
    const c = new Counter();
    await c.click('b', seg(page, 0));
    // Черновик «Новая картинка» в чипе, hero.png остаётся в ленте
    await expect(strip(page)).toContainText('Новая картинка');
    expect(w().threads.map(t => t.id)).toEqual([H, 'thread-draft']);
    await input(page).fill('маяк на закате, акварель');
    await c.click('b', sendBtn(page));
    await expect.poll(() => w().jobs.length).toBe(1);
    expect(w().jobs[0]).toMatchObject({ op: 'generate', threadId: 'thread-draft', prompt: 'маяк на закате, акварель' });
    // Режим остался «Создать», поле пустое — кнопка «↻ Ещё 2»
    await expect(input(page)).toHaveValue('');
    const again = page.locator('[data-composer-send="empty"] button');
    await expect(again).toContainText(/Ещё 2/);
    await c.click('b', again);
    await expect.poll(() => w().jobs.length).toBe(2);
    expect(w().jobs[1]).toMatchObject({ op: 'generate', threadId: 'thread-draft', prompt: 'маяк на закате, акварель' });
    expect([c.clicks, c.moves]).toEqual([3, 0]);
    // Та же кнопка — в низу панели
    await summary(page).click();
    await expect(panel(page).getByRole('button', { name: /^Ещё 2/ })).toBeVisible();
    await shot(page, SHOTS, 'w1440-light-again');
  });

  test('2. поправить картинку из ленты — 3 / 0', async ({ page }) => {
    await open(page, W, 'light', null, 'create', [heroThread(), catThread()]);
    await expect(strip(page)).toBeVisible({ timeout: 30_000 });
    const c = new Counter();
    // «Править» без картинки приглушён и спрашивает «Что править?»
    await c.click('b', seg(page, 1));
    await expect(page.getByText('Что править?')).toBeVisible();
    await expect(page.getByText('Или «Работать с этой» на карточке в ленте')).toBeVisible();
    await expect(page.getByRole('button', { name: /^Из файлов проекта/ })).toBeVisible();
    await expect(page.getByRole('button', { name: /^С компьютера/ })).toBeVisible();
    await shot(page, SHOTS, 'w1440-light-pick');
    await c.click('b', page.getByRole('button', { name: /^hero\.png/ }));
    // Выбор открыл панель колонкой — в тесной полосе чип без приставки «Работаем с:»
    await expect(strip(page).locator('[data-images-chip="focus"]')).toContainText('hero.png');
    await input(page).fill('вечер, тёплый свет из окна');
    await c.click('b', sendBtn(page));
    await expect.poll(() => w().jobs.length).toBe(1);
    expect(w().jobs[0]).toMatchObject({ op: 'edit', threadId: H });
    expect([c.clicks, c.moves]).toEqual([3, 0]);
  });

  test('3. убрать лишнее по отмеченному — 4 / 2', async ({ page }) => {
    await open(page, W, 'light', H, 'edit', [heroThread()]);
    await imageComposer(page);
    const c = new Counter();
    await c.click('e', page.locator('[data-image-brush]'));
    await expect(page.getByRole('heading', { name: 'Редактор · hero.png' })).toBeVisible();
    const pic = page.locator('svg[viewBox="0 0 320 240"]');
    await expect(pic).toBeVisible();
    await brush(page, pic);
    c.step('e');
    await c.click('b', page.getByRole('button', { name: 'Готово' }));
    await expect(page.getByRole('heading', { name: 'Редактор · hero.png' })).toHaveCount(0);
    await input(page).fill('убери человека справа');
    await c.click('b', sendBtn(page));
    await expect.poll(() => w().jobs.length).toBe(1);
    expect(w().quotes.at(-1)).toMatchObject({ op: 'inpaint', hasMask: true });
    expect(w().jobs[0].fields).toContain('mask');
    expect([c.clicks, c.moves]).toEqual([4, 2]);
  });

  test('4. дорисовать за края до 9:16 — 5 / 1', async ({ page }) => {
    await open(page, W, 'light', H, 'edit', [heroThread()]);
    await imageComposer(page);
    const c = new Counter();
    await c.click('p', summary(page));
    await c.select('p', panel(page).locator('[data-image-op-select] select'), 'outpaint');
    await c.click('p', panel(page).getByRole('button', { name: '9:16', exact: true }));
    await c.click('p', panel(page).getByRole('button', { name: /^Дорисовать/ }));
    await expect.poll(() => w().jobs.length).toBe(1);
    expect(w().jobs[0]).toMatchObject({ op: 'outpaint', aspectRatio: '9:16' });
    expect([c.clicks, c.moves]).toEqual([5, 1]);
  });

  test('5. убрать фон, потом улучшить — 7 / 1', async ({ page }) => {
    await open(page, W, 'light', H, 'edit', [heroThread()]);
    await imageComposer(page);
    const c = new Counter();
    await c.click('p', summary(page));
    const op = panel(page).locator('[data-image-op-select] select');
    await c.select('p', op, 'removeBackground');
    await expect(panel(page).locator('[data-image-no-samples]')).toBeVisible();
    await c.click('p', panel(page).getByRole('button', { name: /^Убрать фон/ }));
    await expect.poll(() => w().jobs.length).toBe(1);
    await c.select('p', op, 'upscale');
    await c.click('p', panel(page).getByRole('button', { name: /^Улучшить/ }));
    await expect.poll(() => w().jobs.length).toBe(2);
    expect(w().jobs.map(j => j.op)).toEqual(['removeBackground', 'upscale']);
    expect([c.clicks, c.moves]).toEqual([7, 1]);
  });

  test('6. Аня в стиле palette.png — 8 / 2', async ({ page }) => {
    await open(page, W, 'light', null, 'edit', [heroThread()]);
    await expect(strip(page)).toBeVisible({ timeout: 30_000 });
    const c = new Counter();
    // «Создать» в полосе: черновик заведёт первая отправка из поля ввода
    await c.click('b', seg(page, 0));
    await c.click('p', summary(page));
    await expect(panel(page).locator('[data-image-body="create"]')).toBeVisible();
    await c.click('p', panel(page).locator('[data-image-character-pick]'));
    await c.click('p', page.getByRole('button', { name: /^Аня/ }));
    await c.click('p', panel(page).getByText('Образец', { exact: true }));
    await c.click('p', page.getByRole('button', { name: /^Из файлов проекта/ }));
    await c.click('p', page.getByRole('button', { name: /palette\.png/ }));
    await expect(panel(page).locator('[data-sample="palette.png"]')).toBeVisible();
    await expect(panel(page).locator('[data-image-character-pick]')).toContainText('Аня');
    await input(page).fill('Аня у окна с чашкой какао');
    await c.click('b', sendBtn(page));
    await expect.poll(() => w().jobs.length).toBe(1);
    expect(w().jobs[0].op).toBe('generate');
    expect(w().jobs[0].fields).toEqual(expect.arrayContaining(['characterSlug', 'referencePaths']));
    expect([c.clicks, c.moves]).toEqual([8, 2]);
  });

  test('7. агент нарисовал — правлю — 2 / 0', async ({ page }) => {
    await open(page, W, 'light', CAT, 'create', [heroThread(), catThread()]);
    await expect(strip(page)).toBeVisible({ timeout: 30_000 });
    const c = new Counter();
    // Кот уже в чипе — «Править» включается без меню, поле ввода — «Картинка»
    await c.click('b', seg(page, 1));
    await expect(page.getByText('Что править?')).toHaveCount(0);
    await input(page).fill('сделай вечер');
    await c.click('b', sendBtn(page));
    await expect.poll(() => w().jobs.length).toBe(1);
    expect(w().jobs[0]).toMatchObject({ op: 'edit', threadId: CAT });
    expect([c.clicks, c.moves]).toEqual([2, 0]);
  });

  test('8. телефон 360: правка из ленты — 3 / 0, шторка не поднимается', async ({ page }) => {
    await open(page, M, 'light', null, 'create', [heroThread(), catThread()]);
    await expect(strip(page)).toBeVisible({ timeout: 30_000 });
    const c = new Counter();
    await c.click('b', seg(page, 1));
    await expect(page.getByText('Что править?')).toBeVisible();
    await shot(page, SHOTS, 'm360-light-pick');
    await c.click('b', page.getByRole('button', { name: /^hero\.png/ }));
    await expect(seg(page, 1)).toBeVisible();
    await page.waitForTimeout(300);
    await expect(page.locator('[data-gen-sheet="sheet"]')).toHaveCount(0);
    await input(page).fill('вечер, тёплый свет из окна');
    await c.click('b', sendBtn(page));
    await expect.poll(() => w().jobs.length).toBe(1);
    expect(w().jobs[0]).toMatchObject({ op: 'edit', threadId: H });
    expect([c.clicks, c.moves]).toEqual([3, 0]);
  });

  test('снять выбор — 1 клик: «Создать», поле остаётся «Картинкой», «Вернуть» возвращает', async ({ page }) => {
    await open(page, W, 'light', H, 'edit', [heroThread(), catThread()]);
    await imageComposer(page);
    const c = new Counter();
    await c.click('b', chipX(page));
    await expect(release(page)).toContainText('Картинка снята — дальше рисуем новую');
    await expect(page.getByText('Картинка больше не выбрана')).toHaveCount(0);
    await expect(input(page)).toHaveAttribute('placeholder', /Опишите новую/);
    await expect(strip(page)).toContainText('Новая картинка');
    expect(c.clicks).toBe(1);
    await shot(page, SHOTS, 'w1440-light-release');
    await release(page).getByRole('button', { name: 'Вернуть' }).click();
    await expect(strip(page)).toContainText('Работаем с:');
    await expect(input(page)).toHaveAttribute('placeholder', /Что изменить/);
  });
});

test('личный чат: в «Что править?» нет «Из файлов проекта…» и «С компьютера…»', async ({ page }) => {
  await open(page, W, 'light', null, 'create', [catThread()], true);
  await expect(strip(page)).toBeVisible({ timeout: 30_000 });
  await seg(page, 1).click();
  await expect(page.getByText('Что править?')).toBeVisible();
  // Картинки чата в меню есть, источников проекта — нет
  await expect(page.getByRole('button', { name: /Новая картинка|кот/ }).first()).toBeVisible();
  await expect(page.getByRole('button', { name: /^Из файлов проекта/ })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /^С компьютера/ })).toHaveCount(0);
  await shot(page, SHOTS, 'w1440-light-personal-pick');
});

test('две вкладки одного чата: режим «Создать / Править» синхронизируется', async ({ page, context }) => {
  await open(page, W, 'light', H, 'edit', [heroThread()]);
  await imageComposer(page);
  const other = await context.newPage();
  await openChat(other, { vp: W, mode: 'edit', trace: TRACE });
  await imageComposer(other);
  await expect(seg(other, 1)).toHaveAttribute('aria-pressed', 'true');

  // Первая вкладка: «Создать» — вторая переключается сама
  await seg(page, 0).click();
  await expect(seg(page, 0)).toHaveAttribute('aria-pressed', 'true');
  await expect(seg(other, 0)).toHaveAttribute('aria-pressed', 'true');
  await expect(input(other)).toHaveAttribute('placeholder', /Опишите новую/);

  // Вторая вкладка: «Править» → hero.png — первая возвращается в «Править»
  await seg(other, 1).click();
  await other.getByRole('button', { name: /^hero\.png/ }).click();
  await expect(seg(other, 1)).toHaveAttribute('aria-pressed', 'true');
  await expect(seg(page, 1)).toHaveAttribute('aria-pressed', 'true');
  await expect(input(page)).toHaveAttribute('placeholder', /Что изменить/);
  await other.close();
});

// Плашка «Вернуть» на 360: кнопка AI поднимается над ней и «Вернуть» нажимается; после
// плашки кнопка возвращается на прежнее место
test('360: «Вернуть» не под кнопкой AI', async ({ page }) => {
  await open(page, M, 'light', H, 'edit', [heroThread(), catThread()]);
  await imageComposer(page);
  const fab = page.locator('button.cc-fab').first();
  await expect(fab).toBeVisible();
  // Положение круга — после его собственной анимации подъёма над полем ввода
  const settledY = async () => {
    let prev = -1;
    for (;;) {
      const y = Math.round((await fab.boundingBox())!.y);
      if (y === prev) return y;
      prev = y;
      await page.waitForTimeout(400);
    }
  };
  const before = await settledY();
  await chipX(page).click();
  const undo = release(page).getByRole('button', { name: 'Вернуть' });
  await expect(undo).toBeVisible();
  // В центре «Вернуть» — сама кнопка, а не круг AI поверх
  await expect.poll(async () => undo.evaluate(el => {
    const r = el.getBoundingClientRect();
    const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return !!hit && el.contains(hit);
  })).toBe(true);
  // Круг AI целиком над плашкой — после анимации подъёма
  await expect.poll(async () => {
    const raised = (await fab.boundingBox())!;
    const nb = (await release(page).boundingBox())!;
    return raised.y + raised.height <= nb.y + 1;
  }, { message: 'круг AI над плашкой' }).toBe(true);
  await shot(page, SHOTS, 'm360-light-release-fab');
  await undo.click();
  await expect(seg(page, 1)).toHaveAttribute('aria-pressed', 'true');
  await expect(release(page)).toHaveCount(0);
  await expect.poll(settledY).toBe(before);
});

// Кадры обеих тем: полоса (1440 — подписи и сводка, 360 — иконки и «▴»), плашка «Вернуть»,
// шторка 360 и колонка 1440 обоих режимов
for (const theme of ['light', 'dark'] as const) {
  test(`кадры полосы, тема ${theme}`, async ({ page }) => {
    for (const vp of [{ ...W, tag: 'w1440' }, { ...M, tag: 'm360' }]) {
      await open(page, vp, theme, H, 'edit', [heroThread(), catThread()]);
      await imageComposer(page);
      await expect(strip(page).locator('[data-images-mode-switch]')).toBeVisible();
      if (vp.width === 360) await expect(summary(page)).toHaveAttribute('aria-label', /Открыть настройки/);
      await shot(page, SHOTS, `${vp.tag}-${theme}-strip-edit`);
      await chipX(page).click();
      await expect(release(page)).toBeVisible();
      await page.waitForTimeout(300);
      await shot(page, SHOTS, `${vp.tag}-${theme}-release`);
      await page.goto('about:blank');
    }
  });

  // Шторка 360: поля «Создать» и «Изменить» и строка «Чем» — в первом экране, без прокрутки
  test(`шторка 360 и колонка 1440, тема ${theme}`, async ({ page }) => {
    for (const [mode, focus] of [['edit', H], ['create', null]] as const) {
      await open(page, M, theme, focus, mode, [heroThread()]);
      await openPanel(page);
      const sheet = page.locator('[data-gen-sheet="sheet"]');
      await expect(sheet).toBeVisible();
      const body = sheet.locator(`[data-image-body="${mode}"]`);
      await expect(body).toBeVisible();
      const exec = body.locator('[data-image-executor]');
      const sb = (await sheet.boundingBox())!;
      const eb = (await exec.boundingBox())!;
      expect(eb.y + eb.height, `${mode}: «Чем» в первом экране шторки`).toBeLessThanOrEqual(sb.y + sb.height);
      await shot(page, SHOTS, `m360-${theme}-panel-${mode}`);
      if (mode === 'edit') {
        await expect(body.locator('[data-image-where]')).toContainText('отметок на телефоне нет');
        await expect(body.getByRole('button', { name: 'Отметить в редакторе' })).toHaveCount(0);
      }
      await page.goto('about:blank');
      await open(page, W, theme, focus, mode, [heroThread()]);
      await openPanel(page);
      await expect(panel(page).locator(`[data-image-body="${mode}"]`)).toBeVisible();
      await panel(page).locator('[data-image-executor] button').first().click();
      await panel(page).getByRole('button', { name: /Ещё настройки/ }).click();
      await shot(page, SHOTS, `w1440-${theme}-panel-${mode}`);
      await page.goto('about:blank');
    }
  });
}

// Дизайн-проверка Майи (карточка 64c67e83): тач-цели 360, подпись кнопки запуска на 360,
// имя в чипе «Работаем с» на 1440 при открытой панели
const box = async (l: Locator) => (await l.boundingBox())!;
for (const theme of ['light', 'dark'] as const) {
  test(`360: тач-цели панели и полосы, подпись запуска, тема ${theme}`, async ({ page }) => {
    // Крестик чипа «Работаем с» в полосе
    await open(page, M, theme, H, 'edit', [heroThread(), catThread()]);
    await imageComposer(page);
    const x = await box(chipX(page));
    expect(x.width, 'крестик чипа: ширина').toBeGreaterThanOrEqual(40);
    expect(x.height, 'крестик чипа: высота').toBeGreaterThanOrEqual(40);
    const sb = await box(strip(page));
    expect(x.x + x.width, 'крестик внутри полосы').toBeLessThanOrEqual(sb.x + sb.width);
    await shot(page, SHOTS, `m360-${theme}-strip-chip-x`);
    await page.goto('about:blank');

    // «Создать» на пустом поле: подпись кнопки целиком, цена без времени
    await open(page, M, theme, null, 'create', [heroThread()]);
    await imageComposer(page);
    await input(page).fill('маяк на закате');
    const send = sendBtn(page);
    await expect(send).toHaveText(/Сгенерировать · Бесплатно$/);
    const clip = await send.evaluate(btn => {
      const b = btn.getBoundingClientRect();
      const label = [...btn.querySelectorAll('span')].find(s => s.textContent?.includes('Сгенерировать'))!;
      const l = label.getBoundingClientRect();
      return { overflow: label.scrollWidth - label.clientWidth, labelRight: l.right, btnRight: b.right, vw: window.innerWidth };
    });
    expect(clip.overflow, 'подпись не обрезана').toBeLessThanOrEqual(0);
    expect(clip.labelRight, 'подпись внутри кнопки').toBeLessThanOrEqual(clip.btnRight + 0.5);
    expect(clip.btnRight, 'кнопка в экране').toBeLessThanOrEqual(clip.vw);
    await shot(page, SHOTS, `m360-${theme}-send`);

    // Шторка «Создать»: персонаж, образец, «Ещё настройки», пропорции
    await openPanel(page);
    const body = page.locator('[data-gen-sheet="sheet"] [data-image-body="create"]');
    await expect(body).toBeVisible();
    for (const [name, l] of [
      ['«Персонаж ▾»', body.locator('[data-image-character-pick]')],
      ['«Образец»', body.locator('[data-sample-add]')],
      ['«Ещё настройки»', body.locator('[data-image-more] > button')],
    ] as const) expect((await box(l)).height, `${name}: высота`).toBeGreaterThanOrEqual(40);
    await body.locator('[data-image-more] > button').click();
    const ratios = body.locator('[data-image-more-body] button');
    await expect(ratios).toHaveCount(4);
    for (const r of ['1:1', '16:9', '9:16']) {
      const b = await box(ratios.filter({ hasText: r }));
      expect(b.width, `пропорция ${r}: ширина`).toBeGreaterThanOrEqual(40);
      expect(b.height, `пропорция ${r}: высота`).toBeGreaterThanOrEqual(40);
    }
    await shot(page, SHOTS, `m360-${theme}-panel-create-more`);
  });
}

test('1440: при открытой панели имя в чипе «Работаем с» читаемо', async ({ page }) => {
  await open(page, W, 'light', H, 'edit', [heroThread(), catThread()]);
  await imageComposer(page);
  await summary(page).click();
  await expect(panel(page)).toBeVisible();
  const chip = strip(page).locator('[data-images-chip="focus"]');
  const name = chip.locator('b');
  await expect(name).toHaveText(/^hero\.png/);
  // Полоса тесна — приставка «Работаем с:» ушла, имя осталось
  await expect(chip).not.toContainText('Работаем с');
  // Имя целиком на виду (не обрезано многоточием), чип не уже 96
  await expect.poll(() => name.evaluate(b => {
    const r = b.getBoundingClientRect();
    const cut = b.parentElement!.getBoundingClientRect();
    return Math.round(r.right - Math.min(r.right, cut.right));
  }), { message: 'обрезанная часть имени' }).toBe(0);
  expect((await box(chip)).width, 'ширина чипа').toBeGreaterThanOrEqual(96);
  await shot(page, SHOTS, 'w1440-light-chip-panel-open');
});
