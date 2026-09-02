import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'
import noRawColor from './eslint-rules/no-raw-color.js'

// Файлы, которым сырой hex положен по природе (см. docs/design/guidelines.md,
// раздел «Железные правила» — там же перечислены легальные исключения):
//  - темы сторонних редакторов/вьюверов: они принимают только конкретные значения,
//    CSS-переменные хоста до них не доходят;
//  - палитры-данные (цвета агентов/проектов/расширений) — это не оформление, а набор
//    значений, из которого оформление выбирает;
//  - рендер на canvas и SVG-маски, где var(--…) не работает.
// Точечное отклонение в обычном файле оформляется построчно:
//   // eslint-disable-next-line design/no-raw-color -- причина
export const RAW_COLOR_ALLOWED = [
  // Темы сторонних движков
  'src/components/OfficeViewer.tsx',      // customization.theme OnlyOffice
  'src/components/MermaidDiagram.tsx',    // themeVariables Mermaid (обе темы)
  'src/components/CodeEditor.tsx',        // HighlightStyle CodeMirror
  'src/features/notes/NoteEditor.tsx',    // HighlightStyle CodeMirror
  'src/lib/xtermTheme.ts',                // ANSI-палитра xterm.js (терминал + логи сервисов)
  'src/components/DrawioViewer.tsx',      // параметры темы iframe drawio
  'src/lib/widgetHtml.ts',                // тема виджета в sandbox-iframe (переменные хоста туда не доходят)
  // Палитры-данные
  'src/lib/design.ts',                    // GROUP_COLORS
  'src/lib/tasks.ts',                     // палитра проектов (main/soft/softDark)
  'src/components/AgentSelector.tsx',     // AGENT_COLORS
  'src/components/ui/FileTypeTile.tsx',   // EXT_META — палитра типов файлов
  'src/components/Composer.tsx',          // fileColor() — цвета языков во вложениях
  // Canvas и SVG-маски
  'src/components/ui/CanvasBackdrop.tsx',        // stroke в SVG-тайле маски (значима только альфа)
  'src/features/personas/PersonaFace.tsx',       // градиент для mask-image
  'src/features/notes/graph/GraphCanvas.tsx',    // стартовые значения темы canvas-графа
  'src/features/notes/graph/useThemeColors.ts',  // фолбэк, если CSS-переменная пуста
  'src/features/notes/graph/useForceSimulation.ts',
]

// Щиток дизайн-системы. Вынесен отдельно, потому что подключается дважды: здесь (в общий
// линт) и в eslint.design.config.js — под `npm run lint:design`, который проверяет ТОЛЬКО
// дизайн-правила и потому держится зелёным, в отличие от общего линта с его легаси-долгом.
export const designSystem = [
  {
    files: ['src/**/*.{ts,tsx}'],
    plugins: { design: { rules: { 'no-raw-color': noRawColor } } },
    rules: { 'design/no-raw-color': 'error' },
  },
  {
    files: RAW_COLOR_ALLOWED,
    rules: { 'design/no-raw-color': 'off' },
  },
]

export default defineConfig([
  globalIgnores(['dist', 'dev-dist']),   // dev-dist — сгенерированный workbox PWA
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      globals: globals.browser,
    },
    rules: {
      // Префикс `_` = «намеренно не используется»: destructure-to-omit
      // (`const { [key]: _drop, ...rest }`), catch без разбора ошибки, параметр,
      // оставленный в сигнатуре ради совместимости или задела на будущее. Соглашение
      // уже разлито по коду — правилу надо про него сказать, иначе каждое такое место
      // требует ритуального eslint-disable не глядя.
      '@typescript-eslint/no-unused-vars': ['error', {
        argsIgnorePattern: '^_',
        varsIgnorePattern: '^_',
        caughtErrorsIgnorePattern: '^_',
        destructuredArrayIgnorePattern: '^_',
      }],
      // Понижено до warn по итогам задачи 3/5: точность правила на нашей базе — 1
      // находка из 116 (все остальные — доминирующие легитимные идиомы фронта: fetch
      // со сбросом состояния, сброс при смене сущности, подписки matchMedia/ResizeObserver).
      // На уровне error каждый новый такой эффект упирался бы в ритуальное подавление
      // не глядя — 115 точечных eslint-disable уже расставлены и не снимаются, они
      // документируют, почему в этих местах так и надо.
      'react-hooks/set-state-in-effect': 'warn',
      // Отключено по итогам задачи 4б/5 (продолжение ba351c1b). Проверка HMR на живом
      // dev-сервере показала: для констант и хелперов правило охраняет проблему,
      // которой у нас нет — Vite предупреждение в консоли есть, перезагрузки и потери
      // состояния нет. Опции не снимают шум, не снимая защиту: allowConstantExport
      // (уже включён в пресете vite) покрывает только скалярные литералы, а наш
      // доминирующий паттерн — константы-объекты/массивы и функции-хелперы рядом с
      // компонентом (83 находки в 37 файлах остаются и с ним; allowExportNames
      // потребовал бы глобального белого списка из ~75 имён). Единственная категория
      // с настоящим риском — экспортируемые хуки: они вынесены из компонентных файлов
      // в отдельные модули (useChatDrag, useIsMobileModal, usePanelWidth,
      // useBindingLabels, useTaskHover; useDocAnnotations снят с экспорта), то есть
      // риск закрыт структурно, а не линтом. Новые хуки рядом с компонентами не
      // экспортируем — держим это соглашение вместо правила.
      'react-refresh/only-export-components': 'off',
      // Отключено по итогам задачи 5/5 (серия lint-debt). Правило — сигнал для React
      // Compiler («ручную мемоизацию не удалось сохранить → компонент пропущен»), но
      // компилятор в сборке не подключён (vite.config.ts: @vitejs/plugin-react без
      // babel-plugin-react-compiler, пакета нет в node_modules). Находки (23 в
      // FileExplorer/WorkspacePage/useInView) не меняют рантайм-поведение: ручные
      // useCallback/useMemo продолжают работать как написаны, а выравнивание кода под
      // инференс компилятора — рискованная переделка тяжёлых обработчиков без выигрыша.
      // Если компилятор подключим — правило вернуть и разобрать места по-настоящему.
      'react-hooks/preserve-manual-memoization': 'off',
    },
  },
  ...designSystem,
])
