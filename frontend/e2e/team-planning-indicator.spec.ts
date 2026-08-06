// Индикатор паузы планирования в ленте: прогон смоделированных wire-событий
// на dev-странице #/team-plan-sim (события идут через настоящий chatReducer).
// Гоняется против VITE DEV-СЕРВЕРА (страница симуляции существует только в dev):
//   cd frontend; npm run dev
//   npx playwright test e2e/team-planning-indicator.spec.ts --config playwright.config.ts
// с PLAYWRIGHT_BASE_URL=http://localhost:5173 (дефолтный :5000 — прод-сборка, там
// симуляции нет). Авторизация не нужна — страница живёт вне навигации хаба.
import { test, expect } from '@playwright/test';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://localhost:5173';

test.describe('индикатор паузы планирования', () => {
  test('появляется на стадии планирования и уходит с карточкой плана', async ({ page }) => {
    await page.goto(`${BASE}/#/team-plan-sim`);
    await expect(page.getByTestId('sim-stage')).toHaveText('—');
    await expect(page.getByTestId('team-planning-indicator')).toHaveCount(0);

    // Интервью закончилось — штаб вошёл в планирование
    await page.getByTestId('sim-start-planning').click();
    await expect(page.getByTestId('sim-stage')).toHaveText('planning');
    const indicator = page.getByTestId('team-planning-indicator');
    await expect(indicator).toBeVisible();
    await expect(indicator).toContainText('Команда готовит план…');
    await expect(indicator).toContainText('может занять несколько минут');
    // Признак течения времени на месте с первой секунды
    await expect(page.getByTestId('team-planning-elapsed')).toContainText('меньше минуты');

    // План готов: стадия confirming + карточка плана — плашка обязана уйти
    await page.getByTestId('sim-plan-ready').click();
    await expect(page.getByTestId('sim-stage')).toHaveText('confirming');
    await expect(page.getByTestId('team-planning-indicator')).toHaveCount(0);
    await expect(page.getByTestId('sim-plan-card')).toBeVisible();
  });

  test('карточка отказа гасит плашку, повторное планирование возвращает', async ({ page }) => {
    await page.goto(`${BASE}/#/team-plan-sim`);
    await page.getByTestId('sim-start-planning').click();
    await expect(page.getByTestId('team-planning-indicator')).toBeVisible();

    // Планировщик не уложился: карточка отказа, стадия остаётся planning —
    // но двух сообщений об одном быть не должно, плашка гаснет
    await page.getByTestId('sim-plan-failed').click();
    await expect(page.getByTestId('sim-stage')).toHaveText('planning');
    await expect(page.getByTestId('team-planning-indicator')).toHaveCount(0);
    await expect(page.getByTestId('sim-escalation-card')).toBeVisible();

    // Человек нажал «Повторить планирование»: карточка погашена, плашка снова видна
    await page.getByTestId('sim-retry-planning').click();
    await expect(page.getByTestId('team-planning-indicator')).toBeVisible();
    await expect(page.getByTestId('sim-escalation-card')).toContainText('(решено)');
  });
});
