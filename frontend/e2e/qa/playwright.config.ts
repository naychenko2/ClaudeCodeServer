import { defineConfig, devices } from '@playwright/test';

// Конфиг QA-спеков (docs/operations/qa-stand.md). Основной конфиг их игнорирует
// (`testIgnore: ['**/qa/**']`), а явный запуск файла из-под игнора даёт «0 tests» —
// поэтому запуск идёт отсюда: target=frontend/e2e/qa. Стенд — PLAYWRIGHT_BASE_URL.
export default defineConfig({
  testDir: '.',
  testMatch: '**/*.qa.spec.ts',
  // Шаблон не входит в прогон каталога, но явно названный файл запускается: testIgnore отсекает
  // и явный файл, поэтому игнор снимается, когда шаблон назван в аргументах CLI
  testIgnore: process.argv.some(a => a.includes('_template.qa.spec')) ? [] : ['**/_template.qa.spec.ts'],
  // 180 с: значок панели «Локальные модели» появляется после лёгкого 60-секундного опроса
  // configured, и на загруженной машине ожидание знака упирается в потолок теста.
  timeout: 180_000,
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
