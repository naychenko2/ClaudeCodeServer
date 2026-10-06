import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { newWorld, openChat, primary } from './contextRowMock';

// Мост поля ввода и чипы действий (ADR-023 §Д2). Идёт на dev-сервере Vite с моком API (как
// context-row.spec.ts): вклады видов регистрируются из страницы через registerSubsystem, потому что
// вертикали в моке не грузятся. Слота composer-mode и сегмента «Чат | Картинка» больше нет. Запуск:
//   cd frontend; npx vite --port 5322 --strictPort --host 127.0.0.1 &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5322 CA_SHOTS_DIR=../.cc-attachments/composer-actions npx playwright test e2e/composer-actions.spec.ts

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

// withActions=false — у вида `actions` пусты: чипов нет, поле — обычный «Чат»
async function registerKit(page: Page, withActions: boolean, priced = false) {
  await page.evaluate(async ({ withActions, priced }) => {
    // Тот же экземпляр модуля, что у приложения: dev-сервер мог дописать ?t= после HMR, свой URL дал бы второй реестр
    const url = performance.getEntriesByType('resource').map(e => e.name).find(n => n.includes('/src/lib/subsystems/registryCore.ts'))
      ?? '/src/lib/subsystems/registryCore.ts';
    const m = await import(/* @vite-ignore */ url);
    const act = (id: string, label: string, extra = {}) => ({ id, kind: 'run', label, hint: `Запуск: ${label}`, op: id, ...extra });
    const actions = priced
      ? [act('edit', 'Изменить отмеченное в выбранной области', { text: 'none' })]
      : withActions
      ? [act('edit', 'Изменить', { text: 'required', placeholder: 'Что изменить на картинке…' }), act('removeBg', 'Убрать фон', { text: 'none' }),
          act('upscale', 'Увеличить', { text: 'none' }), act('outpaint', 'Дорисовать', { text: 'required' }),
          { id: 'mark', kind: 'editor', label: 'Отметить', hint: 'Открыть редактор на кисти', open: () => {} }]
      : [];
    m.registerSubsystem({
      key: 'e2e-kit', title: 'e2e', order: 1, noPill: true, core: true,
      slots: {
        'context-kind': [{ name: 'e2e-image', action: {
          kinds: ['image'], icon: () => null, preview: () => null, actions: () => actions,
          ...(priced ? { params: () => [{ kind: 'variants', min: 1, max: 4, value: 3 }], quote: async () => ({ price: '$0.12' }) } : null),
        } }],
      },
    });
  }, { withActions, priced });
}

const actions = (page: Page) => page.locator('[data-composer-actions]');
const field = (page: Page) => page.locator('[data-composer-input] textarea');

test('вид без действий: чипов нет, поле — обычный «Чат» без режима и кнопки запуска (1440)', async ({ page }) => {
  newWorld({ ctx: { primary: primary() } });
  await openChat(page, { vp: D });
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
  await registerKit(page, false);
  await expect(actions(page)).toHaveCount(0);
  await expect(page.locator('[data-composer-modes]')).toHaveCount(0);
  await expect(page.locator('[data-composer-mode-bar]')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Режим «Картинка»' })).toHaveCount(0);
});

test('вид без действий на 360: чипов нет, страница не уезжает вбок', async ({ page }) => {
  newWorld({ ctx: { primary: primary() } });
  await openChat(page, { vp: M });
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
  await registerKit(page, false);
  await expect(actions(page)).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('вид с действиями: чипы действий вместо сегмента режимов (1440)', async ({ page }) => {
  newWorld({ ctx: { primary: primary() } });
  await openChat(page, { vp: D });
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
  await registerKit(page, true);
  await expect(actions(page)).toBeVisible();
  await expect(page.locator('[data-composer-modes]')).toHaveCount(0);
  // «Чат» первым, затем четыре действия-режима и вход в редактор
  const ids = await actions(page).locator('[data-action-chip]').evaluateAll(els => els.map(e => e.getAttribute('data-action-chip')));
  expect(ids).toEqual(['__chat', 'edit', 'removeBg', 'upscale', 'outpaint', 'mark']);
  // Объект человека: выбрано первое действие, поле — режим «Изменить»
  await expect(actions(page).locator('[data-action-chip="edit"]')).toHaveAttribute('aria-checked', 'true');
  await expect(field(page)).toHaveAttribute('placeholder', 'Что изменить на картинке…');
  await expect(page.locator('[data-composer-mode-bar]')).toContainText('✦ Изменить');
  await shot(page, 'chips-1440.png');
  // «Чем» в строке контекста нет: у вертикали нет исполнителей
  await expect(page.locator('[data-context-row] [data-chip="exec"]')).toHaveCount(0);
  // «Чат» — поле снова обычное, режима и кнопки запуска нет
  await actions(page).locator('[data-action-chip="__chat"]').click();
  await expect(actions(page).locator('[data-action-chip="__chat"]')).toHaveAttribute('aria-checked', 'true');
  await expect(page.locator('[data-composer-mode-bar]')).toHaveCount(0);
  await expect(field(page)).not.toHaveAttribute('placeholder', 'Что изменить на картинке…');
  // Выбор «Чата» переживает перемонтирование поля: тот же объект, ручной «Чат» держится
  await actions(page).locator('[data-action-chip="removeBg"]').click();
  await expect(field(page)).toHaveAttribute('placeholder', /Текст не нужен/);
  // Вход в редактор выбор не меняет
  await actions(page).locator('[data-action-chip="mark"]').click();
  await expect(actions(page).locator('[data-action-chip="removeBg"]')).toHaveAttribute('aria-checked', 'true');
});

test('вид с действиями на 360: строка чипов прокручивается, страница не уезжает вбок', async ({ page }) => {
  newWorld({ ctx: { primary: primary() } });
  await openChat(page, { vp: M });
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
  await registerKit(page, true);
  await expect(actions(page)).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await shot(page, 'chips-360.png');
});

test('объект агента (✦) встаёт на «Чат»: следующее сообщение не уйдёт генератору', async ({ page }) => {
  newWorld({ ctx: { primary: primary({ by: 'agent' }) } });
  await openChat(page, { vp: D });
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
  await registerKit(page, true);
  await expect(actions(page).locator('[data-action-chip="__chat"]')).toHaveAttribute('aria-checked', 'true');
  await expect(page.locator('[data-composer-mode-bar]')).toHaveCount(0);
});

test('360: подпись кнопки — имя ужимается многоточием, «· ×3 · $0.12» не режется (Р3), в поле и в панели', async ({ page }) => {
  newWorld({ ctx: { primary: primary() } });
  await openChat(page, { vp: M });
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
  await registerKit(page, true, true);
  const tail = page.locator('[data-composer-mode-bar] [data-run-label-tail]');
  await expect(tail).toHaveText(' · ×3 · $0.12', { timeout: 10_000 });
  const name = page.locator('[data-composer-mode-bar] [data-run-label-name]');
  const m = await page.evaluate(() => {
    const n = document.querySelector('[data-composer-mode-bar] [data-run-label-name]') as HTMLElement;
    const t = document.querySelector('[data-composer-mode-bar] [data-run-label-tail]') as HTMLElement;
    const b = t.closest('button') as HTMLElement;
    return { nameClipped: n.scrollWidth > n.clientWidth, tailClipped: t.scrollWidth > t.clientWidth, tailRight: t.getBoundingClientRect().right, btnRight: b.getBoundingClientRect().right, vw: window.innerWidth };
  });
  expect(m.nameClipped, 'имя действия ужато многоточием').toBe(true);
  expect(m.tailClipped, 'хвост с ценой цел').toBe(false);
  expect(m.tailRight).toBeLessThanOrEqual(m.btnRight + 0.5);
  expect(m.btnRight).toBeLessThanOrEqual(m.vw);
  await expect(name).toContainText('✦ Изменить');
  await shot(page, 'run-label-360.png');
});
