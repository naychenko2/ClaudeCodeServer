/**
 * Правило модульности фич: импорт снаружи — только через `index.ts` фичи.
 *
 * Цель — подготовить фронт к выносу фич в отключаемые подсистемы (пилот —
 * «notes»). Фича X публикует свой API через `src/features/X/index.ts` (реестр
 * вкладов): снаружи импортируют оттуда, а не из внутренних модулей. Это даёт
 * стабильный публичный адрес: рефакторинг внутренних модулей не ломает
 * потребителей, а при отключении подсистемы фронт знает ровно одну точку,
 * через которую вкрапления появляются.
 *
 * Правило действует только для фич, перечисленных в опции `features` (это
 * фичи, у которых уже есть `index.ts`). Для фич без `index.ts` нарушение не
 * фиксируется: они ещё не переведены на новую модель и стартовое состояние —
 * хаос, который не имеет смысла allow-list'ить. Когда у фичи заводится
 * `index.ts`, её добавляют в `features` и начинают следить.
 *
 * Правило НЕ действует на файлы внутри самой фичи (`src/features/X/...`):
 * свои модули внутри X могут импортировать друг друга как угодно, включая
 * глубокие относительные пути. Запрет только на «снаружи».
 *
 * Легальные исключения (текущие нарушения, которые ещё не вычищены) —
 * allow-list `CROSS_FEATURE_IMPORT_ALLOWED` в eslint.design.config.js. Разовое
 * отклонение оформляется построчно с причиной:
 *   // eslint-disable-next-line design/no-cross-feature-import -- причина
 */

// В пути импорта ловим «features/<X>» независимо от префикса (`./`, `../`,
// `@/`, голый alias). Жадность на минимальную длину имени фичи — до первого
// разделителя.
const FEATURE_RE = /(?:^|[./])features\/([^/'"\\]+)/;

// То, что идёт после имени фичи в пути импорта. Пустая строка или `.js`,
// `.ts`, `.tsx` без суффикса-пути — легальный импорт через `index.ts`
// (`from '@/features/notes'` резолвится в `.../notes/index.ts`). Любой
// дальнейший сегмент — прямое обращение к внутреннему модулю, нарушение.
const DEEP_PATH_RE = /^(\/.*|\..+)$/;

// Где живут фичи на диске. Поддерживаем и абсолютный путь от корня линта
// (на линукс-раннерах CI), и вариант с ведущим `src/`.
const OWN_FEATURE_RE = /(?:\/|^)(?:src\/)?features\/([^/]+)\//;

export default {
  meta: {
    type: 'problem',
    docs: {
      description:
        'Запрещает прямой импорт из внутренних модулей features/* — ' +
        'снаружи можно только через features/<X>/index.ts',
    },
    schema: [
      {
        type: 'object',
        properties: {
          features: {
            type: 'array',
            items: { type: 'string' },
            // Без списка правило выключено: тишина по умолчанию — никаких
            // ложных срабатываний на фичах, у которых ещё нет index.ts.
          },
        },
        additionalProperties: false,
      },
    ],
    messages: {
      crossFeatureImport:
        'Прямой импорт из «features/{{feature}}/{{path}}»: снаружи фичи можно только ' +
        '`import ... from \'@/features/{{feature}}\'` (через index.ts). Прямые импорты ' +
        'из внутренних модулей делают невозможным отключение подсистемы без точечных ' +
        'правок по всем потребителям. Осознанное исключение — ' +
        'eslint-disable-next-line design/no-cross-feature-import с причиной.',
    },
  },

  create(context) {
    const options = context.options[0] || {};
    const enabled = new Set(options.features || []);
    if (enabled.size === 0) return {};

    const filename = (context.filename || context.getFilename?.() || '')
      .replace(/\\/g, '/');
    const own = OWN_FEATURE_RE.exec(filename);
    const ownFeature = own ? own[1] : null;

    const checkSource = (sourceNode) => {
      if (!sourceNode || sourceNode.type !== 'Literal') return;
      const raw = sourceNode.value;
      if (typeof raw !== 'string') return;
      const m = FEATURE_RE.exec(raw);
      if (!m) return;
      const feature = m[1];
      if (!enabled.has(feature)) return;
      // Свой файл внутри той же фичи — свои правила.
      if (ownFeature === feature) return;
      // Идёт ли после имени фичи что-то ещё, кроме коротких суффиксов
      // расширения (`/notes.ts` — эквивалент `/notes/index.ts` в TS-резолве)?
      const tail = raw.slice(m.index + m[0].length);
      // Всё, что не «пусто или короткое расширение», — прямое обращение к
      // внутреннему модулю. Здесь ловим и `/saveToNote`, и `/saveToNote.ts`,
      // и глубже (`/graph/useThemeColors`).
      if (!DEEP_PATH_RE.test(tail) && tail !== '') return;
      const path = tail.startsWith('/') ? tail.slice(1) : tail;
      context.report({
        node: sourceNode,
        messageId: 'crossFeatureImport',
        data: { feature, path },
      });
    };

    return {
      ImportDeclaration: (node) => checkSource(node.source),
      // Реэкспорт из чужого модуля (`export { X } from 'features/...'`):
      // те же правила, что и для импорта, иначе подсистему можно вынести за
      // границу одной строки правок.
      ExportNamedDeclaration: (node) => {
        if (!node.source) return;
        checkSource(node.source);
      },
      ExportAllDeclaration: (node) => checkSource(node.source),
      // Динамический `import('features/...')` — реже, но ловим тем же
      // правилом: динамика не отменяет модульности.
      ImportExpression: (node) => checkSource(node.source),
    };
  },
};