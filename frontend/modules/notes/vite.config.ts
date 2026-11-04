// Module Federation build для подсистемы «Заметки».
//
// Пилот динамических модулей: notes собирается в отдельный remote-чанк,
// хост (aihome_shell) грузит его по URL в рантайме (registerRemotes + loadRemote).
// Remote экспортирует ./subsystem — полный SubsystemManifest (manifest + tab + slots).
//
// Dev-сервер запускается на порту 5174 (vite dev), хост проксирует /notes-remote/**
// сюда. В прод remoteEntry.js сервиcится статически под /notes-remote/ в wwwroot.

import { defineConfig, type Plugin } from 'vite';
import path from 'node:path';
import react from '@vitejs/plugin-react';
import { federation } from '@module-federation/vite';
import { hostReactShared } from '../hostReactShared';
import { findViolation } from './guardLists.mjs';

// Сторож графа сборки remote «Заметки» (LLM-wiki, QA B1 critical; доработка ревью 0c1052e1):
// сторы ядра хоста (featureFlags, offline, signalr, toast, wiki) не должны попасть в remote
// ВТОРОЙ копией через прямой относительный импорт — вторая копия стора означает пустые флаги,
// мёртвый realtime и отдельный экземпляр WikiError (`instanceof` не проходит). Это второй
// независимый барьер после ESLint-сторожа (notesStoreImportGuard/notesWikiImportGuard в
// eslint.config.js): ошибка падает на сборке, а не на ревью.
// Области импортёров и списки запретных здесь НЕ вторая правда: они читаются из guardLists.mjs
// (тот же модуль, что строит ESLint-правила) — для любой пары (импортёр, модуль) вердикты
// барьеров совпадают. Раньше плагин смотрел только вики-часть, а ESLint — весь модуль; теперь
// области одинаковые: весь модуль (узкий список) и его вики-часть (широкий). Поимённые
// легаси-исключения — LEGACY_ALLOWED там же. Импорт через 'aihome_shell/kit' не относительный
// и не помечается: MF резолвит его в кит хоста, второй копии нет. Транзитивные импорты
// (components/ui/* → lib/design) не помечаются: смотрим только прямые импорты файлов области —
// как ESLint-страж.
function toPosix(p: string) {
  return p.split(path.sep).join('/');
}

function notesStoreGraphGuard(): Plugin {
  // Нарушения собираются в resolveId и сообщаются одним this.error() в buildEnd: так текст
  // доходит до вывода целиком с именем плагина (проверено на живой сборке, 01.11.2026),
  // а цепочка resolveId при этом не рвётся — в ошибке виден именно дефект, а не UNRESOLVED_IMPORT.
  // Set, а не список: на один импорт resolveId в этой сборке вызывается дважды (наблюдено
  // на живой сборке), нарушение должно быть в сообщении один раз
  const violations = new Set<string>();
  return {
    name: 'aihome-notes-store-graph-guard',
    // 'pre' — чтобы hook получил каждый импорт раньше, чем его разрешит другой плагин
    // (в цепочке resolveId первый вернувший значение останавливает цепочку).
    enforce: 'pre',
    resolveId(source, importer) {
      if (!importer) return null;
      // Вердикт целиком в guardLists.mjs (findViolation): тот же матчер гоняет тест барьеров.
      const hit = findViolation(importer, source);
      if (!hit) return null;
      const importerPath = toPosix(importer);
      const rel = importerPath.slice(Math.max(importerPath.indexOf('/src/'), 0) + 1);
      violations.add(`${rel} → ${hit}`);
      return null;
    },
    buildEnd() {
      if (violations.size === 0) return;
      this.error(
        `Remote «Заметки» собирает вторую копию стора ядра через прямой импорт:\n` +
        [...violations].map(v => `  - ${v}`).join('\n') +
        `\nИмпортируй из 'aihome_shell/kit' (та же граница закрыта ESLint-сторожем notesStoreImportGuard/notesWikiImportGuard; списки и области — modules/notes/guardLists.mjs).`,
      );
    },
  };
}

export default defineConfig(({ mode }) => ({
  // Dev: префикс прокси хоста — MF-рантайм резолвит root-absolute импорты
  // относительно base, поэтому в dev все URL должны начинаться с /notes-remote/.
  // Prod: относительный — remoteEntry.js сервиcится статически под /notes-remote/.
  base: mode === 'development' ? '/notes-remote/' : './',
  plugins: [
    notesStoreGraphGuard(),
    react(),
    federation({
      name: 'aihome_notes',
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
      // React — только singleton ядра, своей копии модуль не везёт (см. hostReactShared).
      shared: hostReactShared,
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
    port: 5174,
    // Dev-сервер — цель прокси /notes-remote хоста: браузер идёт same-origin
    // (через прокси), поэтому CORS для него формально не нужен; cors:true оставлен
    // страховкой на случай прямого кросс-оригин доступа к remoteEntry.js.
    cors: true,
  },
}));
