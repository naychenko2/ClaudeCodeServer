import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'
import noRawColor from './eslint-rules/no-raw-color.js'

// Файлы, которым сырой hex положен по природе (см. docs/design-guidelines.md,
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
  'src/components/terminal/TerminalView.tsx', // ANSI-палитра xterm.js
  'src/components/DrawioViewer.tsx',      // параметры темы iframe drawio
  'src/lib/widgetHtml.ts',                // тема виджета в sandbox-iframe (переменные хоста туда не доходят)
  // Палитры-данные
  'src/lib/design.ts',                    // GROUP_COLORS
  'src/lib/tasks.ts',                     // палитра проектов (main/soft/softDark)
  'src/components/AgentSelector.tsx',     // AGENT_COLORS
  'src/components/FileExplorer.tsx',      // EXT_META — бейджи расширений
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
  globalIgnores(['dist']),
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
  },
  ...designSystem,
])
