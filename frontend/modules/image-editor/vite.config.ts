// Module Federation build для MF-модуля «Редактор картинок» (ADR-018 §10.3).
//
// Весь код фичи живёт в src/features/imageEditor, здесь только обёртка: remote
// экспортирует ./subsystem (полный SubsystemManifest), ядро берёт через aihome_shell/kit.
//
// Dev-сервер на порту 5176 (vite dev), хост проксирует /image-editor-remote/** сюда.
// В прод remoteEntry.js раздаётся статически под /image-editor-remote/.

import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { federation } from '@module-federation/vite';

export default defineConfig(({ mode }) => ({
  // Dev: префикс прокси хоста — MF-рантайм резолвит root-absolute импорты относительно base.
  // Prod: относительный — remoteEntry.js раздаётся статически под /image-editor-remote/.
  base: mode === 'development' ? '/image-editor-remote/' : './',
  plugins: [
    react(),
    federation({
      name: 'aihome_image_editor',
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
      shared: {
        react: { singleton: true, requiredVersion: '^19.2.0' },
        'react-dom': { singleton: true, requiredVersion: '^19.2.0' },
      },
    }),
  ],
  build: {
    outDir: 'dist',
  },
  server: {
    port: 5176,
    cors: true,
  },
}));
