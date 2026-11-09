// Конфиг блокиющего шага CI «Lint remote guard» — сторож импортов MF-модулей
// «Редактор картинок», «Звук», «Видео» и «Заметки» (сторы ядра, LLM-wiki QA B1) плюс хостов
// контекста хода отдельно от общего линта. Общий `npm run lint` в CI информационный
// из-за легаси-долга, и нарушение границы remote в его шуме не заметить. Правило общее с
// eslint.config.js (импортируется, не дублируется).
// Важно: шаг CI линит только перечисленные в ci.yml директории — чтобы сторож «Заметок»
// работал в CI, в его команду нужно добавить src/features/notes (правка ci.yml, вне этой задачи).
import { defineConfig, globalIgnores } from 'eslint/config'
import tseslint from 'typescript-eslint'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import { audioEditorImportGuard, contextHostImportGuard, designPlugin, imageEditorImportGuard, notesStoreImportGuard, notesWikiImportGuard, videoEditorImportGuard } from './eslint.config.js'

export default defineConfig([
  globalIgnores(['dist', 'dev-dist']),
  {
    files: ['**/*.{ts,tsx}'],
    linterOptions: { reportUnusedDisableDirectives: 'off' },
    languageOptions: { parser: tseslint.parser },
    // Плагины без правил — чтобы расставленные в коде `eslint-disable` не падали
    // с «Definition for rule was not found»
    plugins: {
      '@typescript-eslint': tseslint.plugin,
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
      design: designPlugin,
    },
  },
  imageEditorImportGuard,
  audioEditorImportGuard,
  videoEditorImportGuard,
  // Модуль «Заметки» (LLM-wiki QA B1): сторы ядра не должны приходить в remote второй копией;
  // широкое правило на вики-часть идёт после узкого и для вики-файлов заменяет его собой
  notesStoreImportGuard,
  notesWikiImportGuard,
  // Хосты контекста хода (ADR-023 §Д6): тоже жёсткий гейт, в общем линте он тонул бы в легаси-долге
  contextHostImportGuard,
])
