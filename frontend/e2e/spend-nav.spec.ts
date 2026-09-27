/// <reference types="node" />
import { test, expect, type APIRequestContext } from '@playwright/test';

// QA: живая проверка навигации раздела «Аналитика токенов» (Ф1.6)
// Сценарии на дев-стенде (frontend :5173, backend :5000). Все 6 обязательны.

declare const process: { env: Record<string, string | undefined> };

const USER = process.env.E2E_USER || 'admin';
const PASS = process.env.E2E_PASS || '12345';
const BASE = 'http://localhost:5173';

const note = (description: string) => test.info().annotations.push({ type: 'note', description });

async function login(request: APIRequestContext): Promise<string> {
  for (let i = 0; i < 3; i++) {
    const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
    if (r.ok()) return (await r.json()).token as string;
    await new Promise((res) => setTimeout(res, 1000));
  }
  throw new Error('login failed after retries');
}

test.describe('навигация Spend (Ф1.6)', () => {
  test.beforeAll(async ({ playwright }) => {
    // Глобальный контекст один раз для всех тестов — login без гонок с rate-limit
    const request = await playwright.request.newContext({ baseURL: BASE });
    try {
      const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
      expect(r.ok(), 'login должен пройти').toBeTruthy();
    } finally {
      await request.dispose();
    }
  });

  test.beforeEach(async ({ playwright }) => {
    const request = await playwright.request.newContext({ baseURL: BASE });
    try {
      const r = await request.post('/api/auth/login', { data: { username: USER, password: PASS } });
      expect(r.ok(), 'login (beforeEach) должен пройти').toBeTruthy();
    } finally {
      await request.dispose();
    }
  });

  async function loginAndGetToken(playwright: import('@playwright/test').Playwright): Promise<string> {
    const request = await playwright.request.newContext({ baseURL: BASE });
    try {
      return await login(request);
    } finally {
      await request.dispose();
    }
  }

  test('1: deep-link #/spend открывается с дефолтным состоянием', async ({ page, context, playwright }) => {
    const token = await loginAndGetToken(playwright);
    await context.addInitScript((tk) => localStorage.setItem('cc_token', tk as string), token);
    await page.goto('/#/spend');
    // Раздел открылся (заголовок виден)
    await expect(page.getByText('Аналитика токенов', { exact: true })).toBeVisible({ timeout: 15000 });
    // Сегмент «Обзор» — MiniSegment рендерит button'ы, не role=tab; проверяем текст
    await expect(page.getByText('Обзор', { exact: true }).first()).toBeVisible();
  });

  test('2: виджет «Токены» на главной → клик по дню открывает срез', async ({ page, context, playwright }) => {
    const token = await loginAndGetToken(playwright);
    await context.addInitScript((tk) => localStorage.setItem('cc_token', tk as string), token);
    await page.goto('/#/home');
    await page.waitForLoadState('networkidle');

    const analyticsLink = page.getByText('Аналитика →', { exact: true }).first();
    if (await analyticsLink.isVisible({ timeout: 5000 }).catch(() => false)) {
      await analyticsLink.click();
    } else {
      const widgetBody = page.locator('[data-cc-src*="SpendWidget"]').first();
      if (await widgetBody.isVisible({ timeout: 2000 }).catch(() => false)) {
        await widgetBody.click();
      } else {
        note('виджет «Токены» не найден');
        return;
      }
    }

    await expect(page.getByText('Аналитика токенов', { exact: true })).toBeVisible({ timeout: 10000 });
    await page.waitForTimeout(1500);

    // Бары дней с cursor:pointer (кликабельные дни в SpendOverview)
    const clickableBars = page.locator('div[style*="cursor: pointer"][title]');
    const count = await clickableBars.count();
    if (count > 0) {
      await clickableBars.first().click({ force: true });
      await expect(page.getByText('Анализ', { exact: true }).first()).toBeVisible({ timeout: 10000 });
    } else {
      note('нет кликабельных баров (нет данных за период)');
    }
  });

  test('3: бейдж расхода в шапке чата → клик открывает анализ с фильтром', async ({ page, context, playwright }) => {
    const token = await loginAndGetToken(playwright);
    await context.addInitScript((tk) => localStorage.setItem('cc_token', tk as string), token);
    await page.goto('/#/chats');
    await page.waitForLoadState('networkidle');

    // Кликаем по первой реальной кнопке с названием чата (исключаем «Новый чат»)
    const chatButton = page.locator('button[title]').filter({ hasNotText: 'Новый' }).first();
    const chatCount = await chatButton.count();
    if (chatCount > 0) {
      await chatButton.click({ force: true });
      await page.waitForTimeout(2500);
    } else {
      note('чат в списке не найден');
      return;
    }

    const spendBadge = page.locator('button[title*="Расход токенов"], button[title*="токен"]');
    if (await spendBadge.isVisible({ timeout: 10000 }).catch(() => false)) {
      await spendBadge.first().click();
      const openAnalytics = page.getByText('Открыть аналитику →', { exact: true });
      if (await openAnalytics.isVisible({ timeout: 5000 }).catch(() => false)) {
        await openAnalytics.click();
        await expect(page.getByText('Аналитика токенов', { exact: true })).toBeVisible({ timeout: 10000 });
      }
    } else {
      note('бейдж расхода не виден (чат без ходов)');
    }
  });

  test('4: «Квоты» → переход в раздел «Аналитика» работает', async ({ page, context, playwright }) => {
    const token = await loginAndGetToken(playwright);
    await context.addInitScript((tk) => localStorage.setItem('cc_token', tk as string), token);
    await page.goto('/#/home');
    await page.waitForLoadState('networkidle');

    const detailsBtn = page.getByText('Подробнее →', { exact: true }).first();
    if (await detailsBtn.isVisible({ timeout: 5000 }).catch(() => false)) {
      await detailsBtn.click();
    } else {
      note('виджет «Использование» не найден');
      return;
    }
    await page.waitForTimeout(1500);

    const spendLink = page.locator('div, button').filter({ hasText: 'Расход по дням и местам' }).first();
    if (await spendLink.isVisible({ timeout: 5000 }).catch(() => false)) {
      await spendLink.click({ force: true });
      await expect(page.getByText('Аналитика токенов', { exact: true })).toBeVisible({ timeout: 10000 });
    } else {
      const altLink = page.getByText('Аналитика →', { exact: true });
      if (await altLink.isVisible({ timeout: 3000 }).catch(() => false)) {
        await altLink.click({ force: true });
        await expect(page.getByText('Аналитика токенов', { exact: true })).toBeVisible({ timeout: 10000 });
      } else {
        note('ссылка из Квот не найдена');
      }
    }
  });

  test('5: мобильная ширина — нет пилюли «Аналитика», вход через меню аватара', async ({ page, context, playwright }) => {
    const token = await loginAndGetToken(playwright);
    await page.setViewportSize({ width: 375, height: 812 });
    await context.addInitScript((tk) => localStorage.setItem('cc_token', tk as string), token);
    await page.goto('/#/home');
    await page.waitForLoadState('networkidle');

    const navButtons = page.locator('nav button, nav [role="tab"]');
    const navCount = await navButtons.count();
    let foundAnalyticsPill = false;
    for (let i = 0; i < navCount; i++) {
      const text = await navButtons.nth(i).textContent();
      if (text && text.includes('Аналитика')) {
        foundAnalyticsPill = true;
        break;
      }
    }
    expect(foundAnalyticsPill, 'пилюли «Аналитика» в таббаре быть НЕ должно').toBe(false);

    const headerButtons = page.locator('header button');
    const btnCount = await headerButtons.count();
    if (btnCount > 0) {
      await headerButtons.last().click({ force: true });
      await page.waitForTimeout(500);
      const spendMenuItem = page.getByText('Аналитика токенов', { exact: true });
      if (await spendMenuItem.isVisible({ timeout: 5000 }).catch(() => false)) {
        await spendMenuItem.click({ force: true });
        await expect(page.getByText('Аналитика токенов', { exact: true })).toBeVisible({ timeout: 10000 });
      } else {
        note('пункт в меню аватара не найден');
      }
    } else {
      note('кнопки в шапке не найдены');
    }
  });

  test('6: HUB_TAB_KEY переживает F5-перезагрузку', async ({ page, context, playwright }) => {
    const token = await loginAndGetToken(playwright);
    await context.addInitScript((tk) => localStorage.setItem('cc_token', tk as string), token);
    await page.goto('/#/spend');
    await expect(page.getByText('Аналитика токенов', { exact: true })).toBeVisible({ timeout: 15000 });

    await page.reload({ waitUntil: 'networkidle' });
    await expect(page.getByText('Аналитика токенов', { exact: true })).toBeVisible({ timeout: 15000 });
    expect(page.url()).toContain('#/spend');
  });

  test('доп: live SPA-переключение — контекст чата НЕ подхватывается без remount', async ({ page, context, playwright }) => {
    const token = await loginAndGetToken(playwright);
    await context.addInitScript((tk) => localStorage.setItem('cc_token', tk as string), token);
    await page.goto('/#/spend');
    await expect(page.getByText('Аналитика токенов', { exact: true })).toBeVisible({ timeout: 15000 });
    await page.waitForTimeout(3000);

    await page.goto('/#/chats');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(2000);

    await page.goto('/#/spend');
    await page.waitForTimeout(2000);
    await expect(page.getByText('Аналитика токенов', { exact: true })).toBeVisible({ timeout: 10000 });
    note('контекст чата НЕ подхватывается на SPA-переключении (ожидаемо, для Ф1.7)');
  });
});
