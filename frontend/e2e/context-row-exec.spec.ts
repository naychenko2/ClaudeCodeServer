import { test, expect } from '@playwright/test';

// «Чем» при выбранном run-действии и серые референсы на витрине строки (#/ui-kit, dev-сервер Vite:
// в production-сборке витрины нет). В живом чате без вклада вертикали run-действий ещё нет, поэтому
// эти состояния проверяются на витрине, которая рисует тот же ContextRowView. Запуск:
//   cd frontend; npx vite --port 5322 --strictPort --host 127.0.0.1 &
//   PLAYWRIGHT_BASE_URL=http://127.0.0.1:5322 npx playwright test e2e/context-row-exec.spec.ts

test.use({ serviceWorkers: 'block', viewport: { width: 1440, height: 900 } });

async function openKit(page: import('@playwright/test').Page) {
  await page.addInitScript(() => { localStorage.setItem('theme-mode', 'light'); });
  await page.goto('/#/ui-kit');
  const head = page.getByText('Строка контекста', { exact: true }).first();
  const found = await head.waitFor({ timeout: 20_000 }).then(() => true, () => false);
  test.skip(!found, 'витрина #/ui-kit есть только на dev-сервере Vite');
  await head.scrollIntoViewIfNeeded();
}

test('«Чем» при выбранном действии: чип с ценой из полей, меню с заголовком по действию и группой «Облако»', async ({ page }) => {
  await openKit(page);
  const exec = page.locator('[data-context-row] [data-chip="exec"]').first();
  await expect(exec).toBeVisible();
  await expect(exec).toContainText('Qwen-Image Edit');
  await expect(exec).toContainText('бесплатно');
  await exec.click();
  // Меню без роли: берём контейнер заголовка
  const head = page.getByText('Чем выполнить «Изменить»');
  const menu = head.locator('xpath=..');
  await expect(head).toBeVisible();
  await expect(menu.getByText('Облако', { exact: true })).toBeVisible();
  await expect(menu.getByText('$0.04')).toBeVisible();
});

test('в «Чате» чипа «Чем» нет, а серые референсы бывают только при выбранном действии', async ({ page }) => {
  await openKit(page);
  // Серый референс — пунктирный, с причиной в тултипе; обычные такого признака не имеют
  const gray = page.locator('[data-context-row] [data-chip="ref"][data-gray]');
  await expect(gray.first()).toBeVisible();
  await expect(gray.first()).toHaveAttribute('title', /не берёт|Стемы/);
  const all = page.locator('[data-context-row] [data-chip="ref"]');
  expect(await all.count()).toBeGreaterThan(await gray.count());
  // Ступени «Чата» витрины: нет ни «Чем», ни серых — число серых не больше, чем у ступеней с действием
  const chat = page.locator('[data-context-row]').filter({ hasNot: page.locator('[data-chip="exec"]') });
  await expect(chat.locator('[data-chip="ref"][data-gray]')).toHaveCount(0);
});
