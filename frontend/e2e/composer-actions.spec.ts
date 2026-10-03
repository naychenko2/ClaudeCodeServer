import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { newWorld, openChat, primary } from './contextRowMock';

// Мост поля ввода и чипы действий (ADR-023 §Д2, 1ф-4). Идёт на dev-сервере Vite с моком API (как
// context-row.spec.ts): вклады видов регистрируются из страницы через registerSubsystem, потому что
// вертикали в моке не грузятся, а у реальных видов `actions` появятся только в 2к/2з. Запуск:
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

// withActions=false — у вида `actions` пусты, живёт старый «Чат | Картинка» из слота composer-mode
async function registerKit(page: Page, withActions: boolean) {
  await page.evaluate(async withActions => {
    // Тот же экземпляр модуля, что у приложения: dev-сервер мог дописать ?t= после HMR, свой URL дал бы второй реестр
    const url = performance.getEntriesByType('resource').map(e => e.name).find(n => n.includes('/src/lib/subsystems/registryCore.ts'))
      ?? '/src/lib/subsystems/registryCore.ts';
    const m = await import(/* @vite-ignore */ url);
    const act = (id: string, label: string, extra = {}) => ({ id, kind: 'run', label, hint: `Запуск: ${label}`, op: id, ...extra });
    const actions = withActions
      ? [act('edit', 'Изменить', { text: 'required', placeholder: 'Что изменить на картинке…' }), act('removeBg', 'Убрать фон', { text: 'none' }),
          act('upscale', 'Увеличить', { text: 'none' }), act('outpaint', 'Дорисовать', { text: 'required' }),
          { id: 'mark', kind: 'editor', label: 'Отметить', hint: 'Открыть редактор на кисти', open: () => {} }]
      : [];
    m.registerSubsystem({
      key: 'e2e-kit', title: 'e2e', order: 1, noPill: true, core: true,
      slots: {
        'context-kind': [{ name: 'e2e-image', action: { kinds: ['image'], icon: () => null, preview: () => null, actions: () => actions } }],
        'composer-mode': [{
          name: 'e2e-image-mode',
          action: {
            title: 'Картинка', icon: null, isAvailable: () => true, placeholder: () => 'Опишите правку (старый режим)',
            onSubmit: () => {},
          },
        }],
      },
    });
  }, withActions);
}

const actions = (page: Page) => page.locator('[data-composer-actions]');
const legacySeg = (page: Page) => page.locator('[data-composer-modes]');
const field = (page: Page) => page.locator('[data-composer-input] textarea');

test('вид без действий: живой старый «Чат | Картинка» через мост, чипов нет (1440)', async ({ page }) => {
  newWorld({ ctx: { primary: primary() } });
  await openChat(page, { vp: D });
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
  await registerKit(page, false);
  await expect(legacySeg(page)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Режим «Картинка»' })).toBeVisible();
  await expect(actions(page)).toHaveCount(0);
  await page.getByRole('button', { name: 'Режим «Картинка»' }).click();
  await expect(field(page)).toHaveAttribute('placeholder', 'Опишите правку (старый режим)');
});

test('вид без действий: старый «Чат | Картинка» на 360', async ({ page }) => {
  newWorld({ ctx: { primary: primary() } });
  await openChat(page, { vp: M });
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
  await registerKit(page, false);
  await expect(page.getByRole('button', { name: 'Режим «Картинка»' })).toBeVisible();
  await expect(actions(page)).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('вид с действиями: чипы вместо «Чат | Картинка», слот composer-mode не читается (1440)', async ({ page }) => {
  newWorld({ ctx: { primary: primary() } });
  await openChat(page, { vp: D });
  await expect(page.locator('[data-context-row]')).toBeVisible({ timeout: 30_000 });
  await registerKit(page, true);
  await expect(actions(page)).toBeVisible();
  await expect(legacySeg(page)).toHaveCount(0);
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

test('без флага: старый путь даже при видe с действиями, чипов нет', async ({ page }) => {
  newWorld({ flags: { 'composer-context-row': false }, ctx: { primary: primary() } });
  await openChat(page, { vp: D });
  await expect(page.locator('textarea').last()).toBeVisible({ timeout: 30_000 });
  await registerKit(page, true);
  await expect(page.getByRole('button', { name: 'Режим «Картинка»' })).toBeVisible();
  await expect(actions(page)).toHaveCount(0);
});
