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
])
