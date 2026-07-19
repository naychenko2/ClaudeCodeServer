import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { VitePWA } from 'vite-plugin-pwa';

// Порт бэкенда для прокси /api и /hubs (по умолчанию 5000; переопределяется BACKEND_PORT)
const backendPort = process.env.BACKEND_PORT || '5000';
// Именно 127.0.0.1, а НЕ localhost: профиль запуска бэкенда слушает 0.0.0.0 (IPv4-wildcard,
// нужен для захода с телефона), а Node резолвит localhost в ::1 первым — прокси упирался в
// ECONNREFUSED и отдавал Bad Gateway на /api/auth/login.
const backendUrl = `http://127.0.0.1:${backendPort}`;

export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      registerType: 'prompt',
      // Свой sw (src/sw.ts): прежний precache/SPA-fallback + обработчики web push.
      // В dev SW подключается как module (требование injectManifest dev-режима)
      devOptions: { enabled: true, type: 'module' },
      strategies: 'injectManifest',
      srcDir: 'src',
      filename: 'sw.ts',
      // .mjs включён в precache — иначе pdf.worker.min.mjs выпадает и PDF не работает офлайн
      injectManifest: {
        globPatterns: ['**/*.{js,mjs,css,html,ico,png,svg,webmanifest}'],
        // Основной бандл перевалил дефолтный лимит precache (2 MiB) — поднимаем до 4 MiB
        maximumFileSizeToCacheInBytes: 4 * 1024 * 1024,
      },
      manifest: {
        name: 'AI Home',
        short_name: 'AIHome',
        description: 'Веб-интерфейс для AI-ассистентов',
        theme_color: '#D97757',
        background_color: '#F4F0E8',
        display: 'standalone',
        start_url: '/',
        icons: [
          { src: 'pwa-64x64.png', sizes: '64x64', type: 'image/png' },
          { src: 'pwa-192x192.png', sizes: '192x192', type: 'image/png' },
          { src: 'pwa-512x512.png', sizes: '512x512', type: 'image/png' },
          { src: 'maskable-icon-512x512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
      },
    }),
  ],
  server: {
    host: true,
    port: 5173,
    // Разрешаем заход через внешний домен (реверс-прокси/туннель) — иначе Vite режет чужой Host
    allowedHosts: ['naychenko.me'],
    proxy: {
      '/api': { target: backendUrl, changeOrigin: true },
      '/hubs': { target: backendUrl, changeOrigin: true, ws: true },
      // Self-hosted draw.io: бэкенд (YARP) проксирует /drawio/* в контейнер jgraph/drawio
      '/drawio': { target: backendUrl, changeOrigin: true },
    },
  },
  preview: {
    port: 4173,
    allowedHosts: ['naychenko.me'],
    proxy: {
      '/api': { target: backendUrl, changeOrigin: true },
      '/hubs': { target: backendUrl, changeOrigin: true, ws: true },
      '/drawio': { target: backendUrl, changeOrigin: true },
    },
  },
});
