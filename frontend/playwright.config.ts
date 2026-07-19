import { defineConfig, devices } from '@playwright/test';

// E2E-тесты офлайн-режима. Гоняются против УЖЕ ЗАПУЩЕННОГО приложения
// (по умолчанию Dev на http://localhost:5000; переопределяется PLAYWRIGHT_BASE_URL).
// Офлайн-режим включён безусловно (фич-флаги удалены) — включать ничего не нужно.
// Учётка — E2E_USER / E2E_PASS (по умолчанию admin / 12345). Пароль 12345 работает
// только когда сервер в Development и задан Auth:DevPassword=12345; иначе передайте
// E2E_PASS реального admin (случайный пароль печатается в лог при первом старте).
export default defineConfig({
  testDir: './e2e',
  timeout: 90_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL || 'http://localhost:5000',
    headless: true,
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
