// Конфиг для `npm run lint:design` — щиток дизайн-системы отдельно от общего линта.
//
// Зачем отдельно: общий `npm run lint` тянет react-hooks/typescript-eslint и на текущей
// кодовой базе даёт сотни замечаний легаси-долга. Нарушение дизайн-системы в этом шуме
// не заметить, поэтому дизайн-правила вынесены в собственный прогон — он обязан быть
// зелёным. Набор правил общий с eslint.config.js (импортируется, не дублируется).
import { defineConfig, globalIgnores } from 'eslint/config'
import tseslint from 'typescript-eslint'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import { designSystem } from './eslint.config.js'
import noCrossFeatureImport from './eslint-rules/no-cross-feature-import.js'

// Файлы, в которых пока остаются прямые импорты из внутренних модулей
// `features/notes/*`. Каждый — реальное существующее нарушение на момент
// фиксации правила (см. задачу e55dcabf). Цель волны 4: по мере выноса
// заметок в подсистему импорты переводятся на `import { ... } from
// '@/features/notes'` (через index.ts) и запись отсюда снимается. Разовое
// отклонение в обычном файле оформляется построчно с причиной:
//   // eslint-disable-next-line module/no-cross-feature-import -- причина
export const CROSS_FEATURE_IMPORT_ALLOWED = [
  'src/App.tsx',
  'src/pages/WorkspacePage.tsx',
  'src/lib/ai/actions.tsx',
  'src/components/FileViewer.tsx',
  'src/components/FileExplorer.tsx',
  'src/components/chat/ChatHeaderBar.tsx',
  'src/components/chat/ChatItemView.tsx',
  'src/components/chat/PlanReviewView.tsx',
  'src/components/artifacts/PlanSection.tsx',
  'src/components/GlobalSearch.tsx',
]

// Фичи, у которых уже есть публичный `index.ts` и для которых линт-сторож
// уже действует. Подключение новой фичи = добавить её в этот список и
// завести allow-list стартовых нарушений (если они есть). Пока в проекте
// только «notes» (пилот волны выноса подсистем).
export const CROSS_FEATURE_IMPORT_ENABLED = ['notes']

export default defineConfig([
  globalIgnores(['dist', 'dev-dist']),   // dev-dist — сгенерированный workbox PWA
  {
    files: ['**/*.{ts,tsx}'],
    // Директивы на выключенные здесь правила — не повод шуметь: их смысл виден общему линту
    linterOptions: { reportUnusedDisableDirectives: 'off' },
    // Только парсер TS/JSX, без правил typescript-eslint: этот прогон судит исключительно
    // дизайн-систему, всё остальное — забота общего `npm run lint`.
    languageOptions: { parser: tseslint.parser },
    // Плагины зарегистрированы, но их правила НЕ включены. Нужны, чтобы уже расставленные
    // в коде `eslint-disable` на их правила не падали с «Definition for rule was not found»
    plugins: {
      '@typescript-eslint': tseslint.plugin,
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
    },
  },
  ...designSystem,
  // Модульность фич: импорт снаружи — только через `index.ts`. Регистрируем
  // под отдельным ключом `module` — ESLint 10 запрещает переопределять уже
  // зарегистрированный в `designSystem` плагин `design`, а конфликтовать с
  // чужим ключом нельзя.
  {
    files: ['src/**/*.{ts,tsx}'],
    plugins: { module: { rules: { 'no-cross-feature-import': noCrossFeatureImport } } },
    rules: {
      'module/no-cross-feature-import': ['error', {
        features: CROSS_FEATURE_IMPORT_ENABLED,
      }],
    },
  },
  {
    files: CROSS_FEATURE_IMPORT_ALLOWED,
    rules: { 'module/no-cross-feature-import': 'off' },
  },
])