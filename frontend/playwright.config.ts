import { defineConfig, devices } from '@playwright/test';

// E2E-тесты офлайн-режима. Гоняются против УЖЕ ЗАПУЩЕННОГО приложения
// (по умолчанию Dev на http://localhost:5000; переопределяется PLAYWRIGHT_BASE_URL).
// Требуют включённых фич-флагов — тест включает их сам через API на старте.
// Учётка — E2E_USER / E2E_PASS (по умолчанию admin / 12345).
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
