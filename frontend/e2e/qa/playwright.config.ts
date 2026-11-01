import { defineConfig, devices } from '@playwright/test';

// Конфиг QA-спеков (docs/operations/qa-stand.md). Основной конфиг их игнорирует
// (`testIgnore: ['**/qa/**']`), а явный запуск файла из-под игнора даёт «0 tests» —
// поэтому запуск идёт отсюда: target=frontend/e2e/qa. Стенд — PLAYWRIGHT_BASE_URL.
export default defineConfig({
  testDir: '.',
  testMatch: '**/*.qa.spec.ts',
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
