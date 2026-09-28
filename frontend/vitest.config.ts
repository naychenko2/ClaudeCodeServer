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
    // Потолок воркеров для ЛЮБОГО прогона — и npm test, и npx vitest run <путь>,
    // которые агенты запускают своим Bash: их не видит ни BuildConcurrencyGate,
    // ни -maxcpucount из backend/Directory.Build.rsp. Без потолка vitest берёт
    // «ядра − 1»: на 24 ядрах 23 воркера и ~3,3 GB на прогон, а 3–4 агента
    // параллельно дали 13 из 19 OOM в ccs-agents.slice 27–28.09. Замер 28.09:
    // при 4 воркерах ~1,1–1,2 GB и 18–22 с против 10–12 с; 5–6 воркеров почти не
    // ускоряют прогон, а память растёт на ~0,1 GB с каждым.
    maxWorkers: 4,
    include: ['src/**/*.test.ts'],
    exclude: ['**/node_modules/**', '**/dist/**', '**/dev-dist/**', '**/e2e/**'],
  },
});
