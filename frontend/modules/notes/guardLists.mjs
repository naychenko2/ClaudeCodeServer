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

// Модули с состоянием из src/pages/ (путь внутри src/pages/, без расширения): относительный
// импорт значений из модуля соберёт вторую копию notesPanels со своими подписчиками, и обе
// будут писать в cc_notes_zones (разрез ac431663, раздел 3). `import type` разрешён.
export const PAGES_FORBIDDEN = ['workspace/panelStackState', 'workspace/panelCatalog'];

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
  forbiddenPages: PAGES_FORBIDDEN,
};

export const NOTES_WIKI_GUARD = {
  name: 'notes-wiki',
  dir: 'src/features/notes/wiki',
  // единственный файл вики-части вне каталога — действия «Вики» в шапке чата
  extraFiles: ['src/features/notes/WikiChatActions.tsx'],
  forbidden: WIKI_FORBIDDEN,
  forbiddenPages: PAGES_FORBIDDEN,
};

export const GUARD_AREAS = [NOTES_MODULE_GUARD, NOTES_WIKI_GUARD];

// Поимённые исключения «легаси, вне LLM-wiki»: файлы, которым прямой импорт запретного
// разрешён сразу в обоих барьерах (список попадает в ignores ESLint-правил и в плагин
// сборки). Сейчас пуст: прямых импортов сторов ядра вне тестов нет (проверено grep
// по features/notes/**, 01.11.2026). Новое легаси-исключение добавляется сюда с
// комментарием, почему оно вне LLM-wiki, — а не чинится массово.
export const LEGACY_ALLOWED = [];
