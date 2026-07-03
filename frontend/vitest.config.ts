import { defineConfig } from 'vitest/config';

// Отдельный конфиг для vitest: не трогаем vite.config.ts (там PWA-плагин,
// который в тестовом прогоне не нужен). Целевые тесты — чистые функции,
// поэтому окружение node, без jsdom.
export default defineConfig({
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
    exclude: ['**/node_modules/**', '**/dist/**', '**/dev-dist/**', '**/e2e/**'],
  },
});
