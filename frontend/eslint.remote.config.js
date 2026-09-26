// Конфиг блокирующего шага CI «Lint remote guard» — сторож импортов MF-модуля
// «Редактор картинок» отдельно от общего линта. Общий `npm run lint` в CI информационный
// из-за легаси-долга, и нарушение границы remote в его шуме не заметить. Правило общее с
// eslint.config.js (импортируется, не дублируется).
import { defineConfig, globalIgnores } from 'eslint/config'
import tseslint from 'typescript-eslint'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import { imageEditorImportGuard } from './eslint.config.js'

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
    },
  },
  imageEditorImportGuard,
])
