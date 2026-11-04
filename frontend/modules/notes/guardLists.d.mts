// Типы для guardLists.mjs — сам список намеренно обычный JS (ESLint-конфиги не могут
// импортировать .ts), поэтому типы вынесены в этот спутниковый декларативный файл:
// их видит только конфиг сборки (vite.config.ts), проверямый tsc.

export interface NotesGuardArea {
  /** короткое имя области — для сообщений обоих барьеров */
  name: string;
  /** каталог области импортёров относительно frontend/, без завершающего '/' */
  dir: string;
  /** точечные файлы области вне каталога (относительно frontend/) */
  extraFiles?: readonly string[];
  /** запретные модули — имена внутри src/lib/ (без префикса «lib/») */
  forbidden: readonly string[];
  /** запретные модули внутри src/pages/ (без расширения), например 'workspace/panelStackState' */
  forbiddenPages: readonly string[];
}

export declare const NARROW_FORBIDDEN: readonly string[];
export declare const PAGES_FORBIDDEN: readonly string[];
export declare const WIKI_FORBIDDEN: readonly string[];
export declare const NOTES_MODULE_GUARD: NotesGuardArea;
export declare const NOTES_WIKI_GUARD: NotesGuardArea;
export declare const GUARD_AREAS: readonly NotesGuardArea[];
export declare const LEGACY_ALLOWED: readonly string[];
