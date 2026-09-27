// Module Federation build для подсистемы «Архитектура».
//
// Динамический модуль: architecture собирается в отдельный remote-чанк,
// хост (aihome_shell) грузит его по URL в рантайме (registerRemotes + loadRemote).
// Remote экспортирует ./subsystem — полный SubsystemManifest (manifest + slots, без tab).
//
// Dev-сервер запускается на порту 5175 (vite dev), хост проксирует /architecture-remote/**
// сюда. В прод remoteEntry.js сервиcится статически под /architecture-remote/ в wwwroot.

import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { federation } from '@module-federation/vite';

export default defineConfig({
  plugins: [
    react(),
    federation({
      name: 'aihome_architecture',
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
      // entry: subsystem.tsx — async-граница: ждёт кит хоста, затем грузит манифест фичи.
    },
  },
  server: {
    port: 5175,
    // Dev-сервер — цель прокси /architecture-remote хоста: браузер идёт same-origin
    // (через прокси), поэтому CORS для него формально не нужен; cors:true оставлен
    // страховкой на случай прямого кросс-оригин доступа к remoteEntry.js.
    cors: true,
  },
});
