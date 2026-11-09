import path from 'node:path';

// Единый источник истины границы модуля «Заметки» (LLM-wiki B1, доработка ревью 0c1052e1):
// одни и те же списки запретных модулей и области importer'ов читают оба барьера —
// ESLint-правила (eslint.config.js: notesStoreImportGuard/notesWikiImportGuard) и плагин
// сборки (vite.config.ts: aihome-notes-store-graph-guard). Инвариант: для любой пары
// (импортёр, запретный модуль) вердикты двух барьеров совпадают.
//
// Обычный JS (.mjs): ESLint-конфиг — .js ESM, а Node не импортирует .ts, поэтому список
// не может быть .ts; типы для конфига сборки (.ts) — в спутниковом guardLists.d.mts.

// Сторы ядра хоста: прямой относительный импорт из модуля соберётся ВТОРОЙ копией внутрь
// remote — флаги пусты, realtime мёртв, отдельный экземпляр WikiError (`instanceof` не
// проходит) — исходная болевая точка QA B1.
export const NARROW_FORBIDDEN = ['featureFlags', 'wiki', 'offline', 'signalr', 'toast'];

// Префиксы внутри src/ (без расширения и без завершающего «/»): значения из всего pages/**
// и из относительного lib/shell-kit. Страницы тянут panelStackState/panelCatalog и сторы ядра
// транзитивно (PanelZone, PanelSlot, zoneGestures и др.) — относительный импорт любой из них
// соберёт вторую копию notesPanels со своими подписчиками, и обе будут писать в cc_notes_zones
// (разрез ac431663, раздел 3). Баррель lib/shell-kit даёт ту же копию плюс сторы ядра.
// `import type` разрешён; кит хоста — через 'aihome_shell/kit' (не относительный путь).
export const SRC_FORBIDDEN = ['pages', 'lib/shell-kit'];

// Вики-часть запрещает дополнительно токены дизайна и брейкпоинты: они есть в runtime-ките
// и другим remote запрещены — следуем их правилу (ср. imageEditorImportGuard).
export const WIKI_FORBIDDEN = [...NARROW_FORBIDDEN, 'design', 'breakpoints'];

// Области импортёров — одинаковые для обоих барьеров (доработка 0c1052e1: раньше плагин
// смотрел только вики-часть, а ESLint — весь модуль). Порядок: общий первым, узкий вторым;
// для файла узкой области побеждает последний совпавший — та же семантика, что у идущего
// позже блока в ESLint flat-config. Узкая область — весь модуль: легаси «Заметок»
// (useSphereOf, personaZone, registryCore) тянет lib напрямую, но не сторы ядра
// (проверено grep по features/notes/**, прямые импорты сторов — только в тестах).
export const NOTES_MODULE_GUARD = {
  name: 'notes-module',
  dir: 'src/features/notes',
  forbidden: NARROW_FORBIDDEN,
  forbiddenSrc: SRC_FORBIDDEN,
};

export const NOTES_WIKI_GUARD = {
  name: 'notes-wiki',
  dir: 'src/features/notes/wiki',
  // единственный файл вики-части вне каталога — действия «Вики» в шапке чата
  extraFiles: ['src/features/notes/WikiChatActions.tsx'],
  forbidden: WIKI_FORBIDDEN,
  forbiddenSrc: SRC_FORBIDDEN,
};

export const GUARD_AREAS = [NOTES_MODULE_GUARD, NOTES_WIKI_GUARD];

// Поимённые исключения «легаси, вне LLM-wiki»: файлы, которым прямой импорт запретного
// разрешён сразу в обоих барьерах (список попадает в ignores ESLint-правил и в плагин
// сборки). Сейчас пуст: прямых импортов сторов ядра вне тестов нет (проверено grep
// по features/notes/**, 01.11.2026). Новое легаси-исключение добавляется сюда с
// комментарием, почему оно вне LLM-wiki, — а не чинится массово.
export const LEGACY_ALLOWED = [];

// Общий матчер обоих барьеров (ESLint строит свои паттерны из тех же списков, плагин сборки
// зовёт findViolation напрямую; тест гоняет через него те же пары, что и через ESLint).
const EXT = '(?:\\.[cm]?[jt]sx?)?';
const esc = (s) => s.replace(/[.*+?^${}()|[\]\\\/]/g, '\\$&');
const AREA_MATCHERS = GUARD_AREAS.map((area) => ({
  forbiddenRe: new RegExp(
    `(^|\\/)src\\/(?:lib\\/(${area.forbidden.map(esc).join('|')})${EXT}|(${area.forbiddenSrc.map(esc).join('|')})(?:\\/.*|${EXT}))$`,
  ),
  inArea: (p) => p.includes(`/${area.dir}/`) || (area.extraFiles ?? []).some((f) => p.includes(`/${f}`)),
}));

const toPosix = (p) => p.split(path.sep).join('/');

/**
 * Нарушение границы модуля для пары (импортёр, источник импорта): строка «src/…» запретного
 * модуля либо null. Тесты, не входящие в бандл, и LEGACY_ALLOWED не проверяются.
 */
export function findViolation(importer, source) {
  const importerPath = toPosix(importer);
  if (
    importerPath.includes('/__tests__/') ||
    /\.test\.[cm]?[jt]sx?$/.test(importerPath) ||
    LEGACY_ALLOWED.some((f) => importerPath.includes(`/${f}`))
  ) return null;
  // Последний совпавший выигрывает: узкая область идёт в GUARD_AREAS после общей —
  // та же семантика, что у идущего позже блока в ESLint.
  const matcher = AREA_MATCHERS.filter((m) => m.inArea(importerPath)).at(-1);
  if (!matcher) return null;
  // Относительный импорт резолвим относительно импортера; прочие ('aihome_shell/kit',
  // пакеты) — не относительные, второй копии они не собирают.
  const resolved = toPosix(source.startsWith('.') ? path.resolve(path.dirname(importer), source) : source);
  const hit = matcher.forbiddenRe.exec(resolved);
  if (!hit) return null;
  return hit[2] ? `src/lib/${hit[2]}` : `src/${hit[3]}`;
}
