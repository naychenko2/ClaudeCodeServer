import { defineConfig } from 'vitest/config';
import { fileURLToPath } from 'node:url';

// Отдельный конфиг для vitest: не трогаем vite.config.ts (там PWA-плагин,
// который в тестовом прогоне не нужен). Целевые тесты — чистые функции,
// поэтому окружение node, без jsdom.
export default defineConfig({
  resolve: {
    alias: {
      'aihome_shell/kit': fileURLToPath(new URL('./src/lib/shell-kit/index.ts', import.meta.url)),
    },
  },
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
    exclude: ['**/node_modules/**', '**/dist/**', '**/dev-dist/**', '**/e2e/**'],
  },
});
