// Module Federation build для MF-модуля «Видео» (ADR-022, по образцу ADR-018 §10.3).
//
// Весь код фичи живёт в src/features/audioEditor, здесь только обёртка: remote
// экспортирует ./subsystem (полный SubsystemManifest), ядро берёт через aihome_shell/kit.
//
// Dev-сервер на порту 5179 (vite dev), хост проксирует /video-editor-remote/** сюда.
// В прод remoteEntry.js раздаётся статически под /video-editor-remote/.

import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { federation } from '@module-federation/vite';
import { hostReactShared } from '../hostReactShared';

export default defineConfig(({ mode }) => ({
  // Dev: префикс прокси хоста — MF-рантайм резолвит root-absolute импорты относительно base.
  // Prod: относительный — remoteEntry.js раздаётся статически под /video-editor-remote/.
  base: mode === 'development' ? '/video-editor-remote/' : './',
  plugins: [
    react(),
    federation({
      name: 'aihome_video_editor',
      filename: 'remoteEntry.js',
      exposes: {
        './subsystem': './subsystem.tsx',
      },
      // Кит хоста — через его remoteEntry.js: общий ESM-граф даёт один инстанс
      // модульного состояния (api, signalr, сторы). Алиас на локальный путь НЕ ставим —
      // именно он вернул бы копию.
      remotes: {
        aihome_shell: { type: 'module', name: 'aihome_shell', entry: '/remoteEntry.js' },
      },
      // React — только singleton ядра, своей копии модуль не везёт (см. hostReactShared).
      shared: hostReactShared,
    }),
  ],
  build: {
    outDir: 'dist',
  },
  server: {
    port: 5179,
    cors: true,
  },
}));
