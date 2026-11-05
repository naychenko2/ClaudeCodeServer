import { test, expect } from '@playwright/test';
import { collectConsoleErrors, login } from './helpers';

// Тост уведомления не должен перекрывать интерфейс (дефект: закреплённое уведомление
// сторожа claude CLI лежало поверх кнопок шапки раздела, и сценарий «Перенести в вики»
// падал по таймауту 90 с).
//
// Здесь проверяется свойство, а не конкретный экран: под тостом нажимается то, что
// под ним. Тост — локальный (то же окно 'cc-local-toast' и тот же слой, что и у
// серверного уведомления), чтобы прогон не зависел от того, есть ли на стенде новая
// версия CLI. Кликабельность самой карточки и её контролов — юнит-проверка
// NotificationToasts.noBlock.test.ts; здесь — что нажатие сквозь тост доходит до
// интерфейса и что крестик тоста по-прежнему нажимается.

test.describe.configure({ mode: 'serial' });

let token: string;

test.beforeAll(async ({ playwright, baseURL }) => {
  const request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  await request.dispose();
});

test('тост висит над интерфейсом, но клики не перехватывает', async ({ page, context }) => {
  const errors = collectConsoleErrors(page);
  await context.addInitScript(tk => localStorage.setItem('cc_token', tk as string), token);
  await page.goto('/');
  await expect(page.locator('#root')).not.toBeEmpty();

  await page.evaluate(() => {
    window.dispatchEvent(new window.CustomEvent('cc-local-toast', {
      detail: { title: 'Проверка слоя', body: 'Тост не должен мешать нажатиям под ним.' },
    }));
  });

  const toasts = page.getByTestId('toast');
  const card = toasts.first();
  await expect(card).toBeVisible();

  // Центр карточки — середина её текста. Слой и карточка прозрачны для курсора,
  // поэтому под этой точкой должен быть элемент страницы, а не сам тост.
  const box = await card.boundingBox();
  expect(box, 'карточка тоста имеет размеры').not.toBeNull();
  const hit = await page.evaluate(({ x, y }) => {
    const el = document.elementFromPoint(x, y);
    if (!el) return 'none';
    return el.closest('[data-testid="toast"]') ? 'inside-toast' : 'interface';
  }, { x: box!.x + box!.width / 2, y: box!.y + box!.height / 2 });
  expect(hit, 'нажатие в центре тоста должно проходить к интерфейсу под ним').toBe('interface');

  // Сам тост всё ещё закрывается своей кнопкой: клик по крестику доходит до неё.
  // Крестик берётся внутри карточки — title="Закрыть" есть и у других панелей приложения
  await card.getByTitle('Закрыть').click();
  await expect(toasts).toHaveCount(0);

  expect(errors, 'в консоли не должно быть ошибок').toEqual([]);
});
