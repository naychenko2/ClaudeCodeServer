// Временный конфиг для smoke-теста параллельного стека (:5199 → бэкенд :5099).
// VitePWA нужен ради virtual:pwa-register, но dev-SW выключен — без кэша на тестовом origin.
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { VitePWA } from 'vite-plugin-pwa';

export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      registerType: 'prompt',
      devOptions: { enabled: false },
      strategies: 'injectManifest',
      srcDir: 'src',
      filename: 'sw.ts',
    }),
  ],
  server: {
    host: '127.0.0.1',
    port: 5199,
    proxy: {
      '/api': { target: 'http://127.0.0.1:5099', changeOrigin: true },
      '/hubs': { target: 'http://127.0.0.1:5099', changeOrigin: true, ws: true },
      '/drawio': { target: 'http://127.0.0.1:5099', changeOrigin: true },
    },
  },
});
