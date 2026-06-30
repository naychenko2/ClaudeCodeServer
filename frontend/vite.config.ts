import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { VitePWA } from 'vite-plugin-pwa';

// Порт backend для dev/preview-прокси. По умолчанию 5000; переопределяется через
// BACKEND_PORT (напр. при работе из git worktree на отдельном порту).
const backendPort = process.env.BACKEND_PORT ?? '5000';
const backendTarget = `http://localhost:${backendPort}`;

export default defineConfig({
  plugins: [
    react(),
    VitePWA({
      registerType: 'prompt',
      devOptions: { enabled: true },
      // .mjs включён в precache — иначе pdf.worker.min.mjs выпадает и PDF не работает офлайн
      workbox: {
        globPatterns: ['**/*.{js,mjs,css,html,ico,png,svg,webmanifest}'],
        // /api/* и OnlyOffice-пути не должны перехватываться SW.
        // OO использует версионированные пути /X.Y.Z-hash/... и статику /web-apps/, /sdkjs/ и т.д.
        navigateFallbackDenylist: [/^\/api\//, /^\/\d/, /^\/web-apps\//, /^\/sdkjs\//, /^\/doceditor\//, /^\/doc\//, /^\/coauthoring\//, /^\/cache\//],
      },
      manifest: {
        name: 'Claude Home Server',
        short_name: 'ClaudeHome',
        description: 'Веб-интерфейс для Claude Code CLI',
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
    proxy: {
      '/api': { target: backendTarget, changeOrigin: true },
      '/hubs': { target: backendTarget, changeOrigin: true, ws: true },
    },
  },
  preview: {
    port: 4173,
    proxy: {
      '/api': { target: backendTarget, changeOrigin: true },
      '/hubs': { target: backendTarget, changeOrigin: true, ws: true },
    },
  },
});
