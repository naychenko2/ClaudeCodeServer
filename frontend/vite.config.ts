import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import babel from '@rolldown/plugin-babel';
import { VitePWA } from 'vite-plugin-pwa';
import { federation } from '@module-federation/vite';
import { viteStaticCopy } from 'vite-plugin-static-copy';
import { fileURLToPath } from 'node:url';

// Плагин UI-инспектора (data-cc-src на host-элементах JSX). plugin-react v6 babel не
// запускает (JSX-трансформ делает oxc) — официальная связка из его README: отдельный
// @rolldown/plugin-babel; babel-трансформ идёт ДО нативного oxc, поэтому плагин видит
// исходный JSX. Путь строкой, а не import: tsconfig.node.json без allowJs уронил бы
// tsc -b на импорте .mjs (TS2307). Babel резолвит строку-путь сам.
const ccSrcPlugin = fileURLToPath(new URL('./scripts/babel-cc-src.mjs', import.meta.url));

// Порт бэкенда для прокси /api и /hubs (по умолчанию 5000; переопределяется BACKEND_PORT)
const backendPort = process.env.BACKEND_PORT || '5000';
// Именно 127.0.0.1, а НЕ localhost: профиль запуска бэкенда слушает 0.0.0.0 (IPv4-wildcard,
// нужен для захода с телефона), а Node резолвит localhost в ::1 первым — прокси упирался в
// ECONNREFUSED и отдавал Bad Gateway на /api/auth/login.
const backendUrl = `http://127.0.0.1:${backendPort}`;

export default defineConfig({
  plugins: [
    react(),
    // Инъекция data-cc-src всегда (dev и prod) — решение по плану UI-инспектора.
    // include только .tsx: JSX живёт в них, остальным файлам babel-проход не нужен
    babel({ include: /\.tsx(?:$|\?)/, plugins: [ccSrcPlugin] }),
    // Host Module Federation (контракт §7, ТЗ R5): устанавливает shared-scope с
    // singleton react/react-dom для внешних модулей. Remotes регистрируются в рантайме
    // (registerRemotes по списку GET /api/modules) — статических remotes нет.
    // Спайк R5a подтвердил: Vite 8/Rolldown + MF + PWA injectManifest собираются,
    // React-инстанс один, remote-чанки не попадают в precache (живут под /api/modules/**/ui/).
    federation({
      name: 'aihome_shell',
      // Design-kit ядра для внешних модулей (контракт §7.1, R14–R16): модули берут
      // токены и примитивы через loadRemote('aihome_shell/design-kit') — entry кита
      // реэкспортирует только leaf-файлы (design.ts, breakpoints.ts, components/ui/*).
      filename: 'remoteEntry.js',
      exposes: {
        './design-kit': './src/lib/design-kit/index.ts',
      },
      remotes: {},
      // dts (#TYPE-001): дефолтный tsConfigPath плагина — корневой tsconfig.json,
      // это solution-файл (только references, без compilerOptions) без `jsx` —
      // генератор типов валился на TS6142 на каждом реэкспорте компонента кита
      // (.tsx). tsconfig.app.json — тот же конфиг, что реально собирает src/.
      dts: { tsConfigPath: './tsconfig.app.json' },
      shared: {
        // eager: react/react-dom бандлятся в основной синхронный chunk, а не в
        // async loadShare-обёртку. Без eager MF выносит shared react в отдельный
        // чанк с top-level-await — и при «офлайн → reconnect → re-render» соседние
        // проходы дерева ловят разные снапшоты React-диспетчера → React error #300
        // («Rendered fewer hooks than expected») → краш всего UI. Host здесь —
        // ПОСТАВЩИК singleton-инстанса (remotes пустые), для него eager штатен и
        // не мешает модулям-потребителям.
        react: { singleton: true, eager: true, requiredVersion: '^19.2.0' },
        'react-dom': { singleton: true, eager: true, requiredVersion: '^19.2.0' },
      },
    }),
    // Ассеты барж-ина (lib/bargeVad) — со СВОЕГО хоста под /vad/: CDN у vad-web/onnxruntime
    // дефолтный, а у пользователей DPI-блокировки — фича молча не работала бы. Копируются
    // модель Silero v5, аудио-ворклет и однопоточный WASM onnxruntime (loader .mjs + бинарь);
    // jsep/jspi/asyncify-варианты не нужны (ortConfig ставит numThreads=1)
    viteStaticCopy({
      // stripBase:true — файлы кладутся ПЛОСКО в dist/vad/ (иначе плагин
      // воспроизводит весь путь node_modules/... внутри dest)
      targets: [
        { src: 'node_modules/@ricky0123/vad-web/dist/silero_vad_v5.onnx', dest: 'vad', rename: { stripBase: true } },
        { src: 'node_modules/@ricky0123/vad-web/dist/vad.worklet.bundle.min.js', dest: 'vad', rename: { stripBase: true } },
        { src: 'node_modules/onnxruntime-web/dist/ort-wasm-simd-threaded.wasm', dest: 'vad', rename: { stripBase: true } },
        { src: 'node_modules/onnxruntime-web/dist/ort-wasm-simd-threaded.mjs', dest: 'vad', rename: { stripBase: true } },
      ],
    }),
    VitePWA({
      registerType: 'prompt',
      // Свой sw (src/sw.ts): прежний precache/SPA-fallback + обработчики web push.
      // В DEV service worker ОТКЛЮЧЁН: под Vite 8/Rolldown dev-обёртка падает
      // («Cannot use import statement outside a module»), а NavigationRoute на
      // непрекэшированный index.html ломает навигацию — при остановке dev-сервера
      // SW отдавал пустоту (белый экран). Офлайн в dev всё равно не работает
      // (нечего прекэшировать); PWA/офлайн тестируем в preview/prod-сборке.
      devOptions: { enabled: false, type: 'module' },
      strategies: 'injectManifest',
      srcDir: 'src',
      filename: 'sw.ts',
      // .mjs включён в precache — иначе pdf.worker.min.mjs выпадает и PDF не работает офлайн
      injectManifest: {
        globPatterns: ['**/*.{js,mjs,css,html,ico,png,svg,webmanifest}'],
        // Ассеты барж-ина офлайн не нужны (петля разговора в офлайне гаснет),
        // а worklet и ort-loader попали бы в precache по маске выше
        globIgnores: ['vad/**'],
        // Основной бандл перевалил дефолтный лимит precache (2 MiB); с инъекцией
        // data-cc-src (UI-инспектор) вырос до ~4.4 MiB — держим лимит с запасом
        maximumFileSizeToCacheInBytes: 6 * 1024 * 1024,
      },
      manifest: {
        name: 'Home AI',
        short_name: 'HomeAI',
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
      // Раздел «Телеметрия»: бэкенд форвардит /telemetry-proxy/* на SigNoz. Без этой строки
      // Vite отдал бы свой index.html (SPA-fallback), и в iframe грузился бы сам CCS.
      '/telemetry-proxy': { target: backendUrl, changeOrigin: true, ws: true },
    },
  },
  preview: {
    port: 4173,
    allowedHosts: ['naychenko.me'],
    proxy: {
      '/api': { target: backendUrl, changeOrigin: true },
      '/hubs': { target: backendUrl, changeOrigin: true, ws: true },
      '/drawio': { target: backendUrl, changeOrigin: true },
      '/telemetry-proxy': { target: backendUrl, changeOrigin: true, ws: true },
    },
  },
});
