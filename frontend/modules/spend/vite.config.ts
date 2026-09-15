// Module Federation build для подсистемы «Расходы».
//
// Пилот динамических модулей: spend собирается в отдельный remote-чанк,
// хост (aihome_shell) грузит его по URL в рантайме (registerRemotes + loadRemote).
// Remote экспортирует ./subsystem — полный SubsystemManifest (manifest + tab + slots).
//
// Dev-сервер запускается на порту 5175 (vite dev), хост проксирует /spend-remote/**
// сюда. В прод remoteEntry.js сервиcится статически под /spend-remote/ в wwwroot.

import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { federation } from '@module-federation/vite';

export default defineConfig(({ mode }) => ({
  // Dev: префикс прокси хоста — MF-рантайм резолвит root-absolute импорты
  // относительно base, поэтому в dev все URL должны начинаться с /spend-remote/.
  // Prod: относительный — remoteEntry.js сервиcится статически под /spend-remote/.
  base: mode === 'development' ? '/spend-remote/' : './',
  plugins: [
    react(),
    federation({
      name: 'aihome_spend',
      filename: 'remoteEntry.js',
      // Единственный экспорт: полный манифест подсистемы.
      // Хост вызывает registerSubsystem(loaded.subsystem) после loadRemote.
      exposes: {
        './subsystem': './subsystem.tsx',
      },
      // Runtime-кит хоста: aihome_shell/kit → remoteEntry.js хоста (MF runtime
      // подтягивает expose-чанк кита как отдельный ESM-модуль; общий ESM-граф
      // браузера гарантирует один инстанс модульного состояния).
      // Алиас aihome_shell/kit на локальный путь НЕ ставим: именно он вернул бы копию.
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
    rollupOptions: {
      // MF remote — library-сборка без index.html.
      // entry: manifest.tsx содержит весь код подсистемы.
    },
  },
  server: {
    port: 5175,
    // Dev-сервер — цель прокси /spend-remote хоста: браузер идёт same-origin
    // (через прокси), поэтому CORS для него формально не нужен; cors:true оставлен
    // страховкой на случай прямого кросс-оригин доступа к remoteEntry.js.
    cors: true,
  },
}));
