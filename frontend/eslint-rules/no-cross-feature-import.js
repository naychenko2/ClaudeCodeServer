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
 * Правило действует только для фич, перечисленных в опции `features`, — тех,
 * что выносятся в подсистемы. Фичу можно включить в список до того, как у неё
 * заведётся `index.ts` (так включён пилот «notes»): стартовые нарушения
 * оформляются построчными `eslint-disable` с причиной, а `CROSS_FEATURE_IMPORT_ALLOWED`
 * остаётся точкой возврата для массовых стартовых нарушений.
 *
 * Относительные импорты между соседними фичами (`'../sessions/api'` из
 * features/chat) — такие же границы фич, как и абсолютные: они резолвятся
 * против директории текущего файла и судятся тем же правилом. В сырой строке у них
 * может не быть сегмента `features/` — без резолвинга соседняя фича проходила
 * мимо сторожа (замечание ревью R-S5-P6).
 *
 * Правило НЕ действует на файлы внутри самой фичи (`src/features/X/...`):
 * свои модули внутри X могут импортировать друг друга как угодно, включая
 * глубокие относительные пути. Запрет только на «снаружи».
 *
 * Легальные исключения (текущие нарушения, которые ещё не вычищены) —
 * allow-list `CROSS_FEATURE_IMPORT_ALLOWED` в eslint.design.config.js. Разовое
 * отклонение оформляется построчно с причиной:
 *   // eslint-disable-next-line module/no-cross-feature-import -- причина
 */

// В пути импорта ловим «features/<X>» независимо от префикса (`./`, `../`,
// `@/`, голый alias). Жадность на минимальную длину имени фичи — до первого
// разделителя.
const FEATURE_RE = /(?:^|[./])features\/([^/'"\\]+)/;

// Дальнейший путь после имени фичи — прямое обращение к внутреннему модулю,
// нарушение. Пустой tail — импорт самого `features/<X>`: в TS-резолве это
// `features/<X>/index.ts`, публичный фасад фичи, — легально.
// Ветка `.` + что-либо (суффикс расширения сразу после имени) с текущим
// FEATURE_RE недостижима: суффикс жадно уходит в имя фичи
// (`'@/features/notes.ts'` → имя `notes.ts`), tail с `.` не возникает.
const DEEP_PATH_RE = /^(\/.*|\..+)$/;

// Где живут фичи на диске. Поддерживаем и абсолютный путь от корня линта
// (на линукс-раннерах CI), и вариант с ведущим `src/`.
const OWN_FEATURE_RE = /(?:\/|^)(?:src\/)?features\/([^/]+)\//;

// Относительный путь импорта (`./`, `../`): в сырой строке у него может не быть
// сегмента `features/` (`'../sessions/api'` из features/chat) — такие пути
// резолвятся против директории текущего файла и проверяются тем же FEATURE_RE.
const RELATIVE_RE = /^\.{1,2}\//;

const resolveRelative = (dir, rel) => {
  const out = [];
  for (const seg of (dir + '/' + rel).split('/')) {
    if (!seg || seg === '.') continue;
    if (seg === '..') { out.pop(); continue; }
    out.push(seg);
  }
  return out.join('/');
};

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
        'eslint-disable-next-line module/no-cross-feature-import с причиной.',
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
    // Директория текущего файла — основа резолвинга относительных импортов.
    // Нужна только файлам на пути фичи: снаружи фичи относительный путь к фиче
    // всегда содержит сегмент `features/` и ловится FEATURE_RE по сырой строке.
    const ownDir = ownFeature ? filename.slice(0, filename.lastIndexOf('/')) : null;

    const checkSource = (sourceNode) => {
      if (!sourceNode || sourceNode.type !== 'Literal') return;
      const raw = sourceNode.value;
      if (typeof raw !== 'string') return;
      // Две формы одного пути: сырая строка (в ней есть сегмент `features/`)
      // и резолвятая — для относительных импортов без этого сегмента.
      const candidates = [raw];
      if (ownDir && RELATIVE_RE.test(raw)) candidates.push(resolveRelative(ownDir, raw));
      for (const src of candidates) {
        const m = FEATURE_RE.exec(src);
        if (!m) continue;
        const feature = m[1];
        if (!enabled.has(feature)) continue;
        // Свой файл внутри той же фичи — свои правила.
        if (ownFeature === feature) continue;
        const tail = src.slice(m.index + m[0].length);
        // Пустой tail — сам `features/<X>` (в TS-резолве `…/index.ts`): это
        // публичный фасад фичи, а не обращение к её внутреннему модулю.
        // Нарушение — только дальнейший сегмент пути: ловим и `/saveToNote`,
        // и `/saveToNote.ts`, и глубже (`/graph/useThemeColors`).
        if (!DEEP_PATH_RE.test(tail)) continue;
        const path = tail.startsWith('/') ? tail.slice(1) : tail;
        context.report({
          node: sourceNode,
          messageId: 'crossFeatureImport',
          data: { feature, path },
        });
        return; // одно нарушение на импорт: обе формы одного пути не двойят;
      }
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