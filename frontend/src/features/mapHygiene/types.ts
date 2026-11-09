// Типы домена «Уборка карты проекта» (CLAUDE.md; переехали из types/index.ts, S5-P3). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// ===== Уборка карты проекта (CLAUDE.md) =====
// Контракт ответа сканера (docs/research/project-map-hygiene-plan-2026-09.md, Р8):
// обе фазы живут на нём — в этой волне фронт использует только факты (suggestions приходят
// пустые к modelSays, поля ниже модели задействует волна 2). Имена полей сверены с
// бэковым MapHygieneReport, расхождение здесь и там ломает UI молча.

export type MapHygieneKind = 'dead-link' | 'dead-import' | 'root-relative' | 'long-section';
export type MapHygieneSeverity = 'high' | 'medium' | 'low';

// Одна находка по ссылке или импорту в отчёте. AnchorText — markdown-ссылка целиком или
// строка импорта; id предложения считается от этого якоря, чтобы человек мог отметить
// «починить ссылку на FeatureFlag.cs» без риска попасть не на ту.
export interface MapHygieneLinkFinding {
  path: string;            // карта, в которой находка (для вложенных)
  section: string | null;
  line: number;            // 0 у мёртвого импорта второго уровня
  target: string;
  reason: 'notFound' | 'rootRelativeOnly' | 'absolute' | 'outsideProject' | 'deadImport';
  candidates: string[];
  anchorText: string;
}

// Секция карты — кандидат на уборку. FirstLine, а не тело: у крупной секции тело не
// показывается целиком, а firstLine достаточно для глаза.
export interface MapHygieneSection {
  title: string;
  startLine: number;
  lines: number;
  docsRefs: number;
  firstLine: string;
}

// Применимость правки: null ⇒ кнопки «Применить» нет вовсе. После `apply != null` живёт
// формула из Р10а: единственный кандидат И уникальный вне кодовых заборов якорь. Здесь —
// только носитель формы; заполняется в волне 4 вместе с самим apply.
export interface MapHygieneApplyPatch {
  before: string;
  after: string;
  anchorText: string;
}

export interface MapHygieneSuggestionAnchor {
  line: number;
  heading: string | null;
}

export interface MapHygieneSuggestion {
  id: string;
  kind: MapHygieneKind;
  severity: MapHygieneSeverity;
  fact: string;
  modelSays: string | null;
  anchor: MapHygieneSuggestionAnchor;
  savingLines: number;
  apply: MapHygieneApplyPatch | null;
}

// Бюджет: ориентир Anthropic (≤200 строк) + сам факт превышения. Проценты НЕ рисуем
// (Р9 — у большого проекта карта объективно больше, и приговор в процентах обесценивает
// отчёт), здесь только бинарный ответ для шапки.
export interface MapHygieneBudget {
  recommendedLines: number;
  overBudget: boolean;
}

// Справочная строка о файле карты (корневая / вложенные / локальная).
export interface MapHygieneFileRef {
  path: string;
  lines: number;
  bytes: number;
}

// Полный отчёт сканера. Поле baseSha едет обратно в review/apply — по нему сервер
// видит, что человек смотрит на тот же файл, что и в момент скана.
export interface MapHygieneReport {
  path: string;
  exists: boolean;
  baseSha: string | null;

  lines: number;
  bytes: number;
  approxTokens: number;
  approxTokensNote: string;

  expandedLines: number;
  importCount: number;
  expansionTruncated: boolean;
  importDepthExceeded: boolean;

  budget: MapHygieneBudget;

  sectionCount: number;
  longSectionCount: number;
  sections: MapHygieneSection[];

  deadLinkCount: number;
  deadLinks: MapHygieneLinkFinding[];

  deadImportCount: number;
  deadImports: MapHygieneLinkFinding[];

  rootRelativeCount: number;
  rootRelativeLinks: MapHygieneLinkFinding[];

  // Пропущенные цели (абсолютные пути и т.п.). В UI НЕ показываются (Р8): «не проверяли»
  // — не находка и не действие, а строка, которую человек не может ни починить, ни осмыслить
  skippedCount: number;
  skipped: MapHygieneLinkFinding[];

  unclosedFence: boolean;

  // Список ДЕЙСТВИЙ поверх списков находок выше. В этой волне modelSays у всех null:
  // модель ещё не зовётся (Р11: фич-флаг выключен). Волна 2 привезёт суждения.
  suggestions: MapHygieneSuggestion[];

  // Текст, которым сервер объясняет неполноту разбора (модель не настроена, не дошла
  // и т.п.). В UI показывается дословно — по глухому «не удалось» человек не починит
  // «модель для разбора не настроена». Доступен после POST /map-hygiene/review.
  modelNote?: string | null;

  secondMap: MapHygieneFileRef | null;
  nestedMapCount: number;
  nestedMaps: MapHygieneFileRef[];

  // Компактная карта из docs/CLAUDE-local.md (BareMode): в счёт контекста не входит,
  // показывается справочно
  localMap: MapHygieneFileRef | null;

  walkTruncated: boolean;
  truncated: boolean;
}

// Причина, по которой правка не применилась. Закрытый белый список из четырёх значений
// (MapApplyReasons на бэке): фронт по нему не ветвится, но человеку на каждый показывается
// своя строка — «не удалось» без причины он не починит.
export type MapHygieneApplyFailureReason =
  | 'anchorNotFound'
  | 'ambiguousAnchor'
  | 'unknownId'
  | 'notApplicable';

export interface MapHygieneApplyFailure {
  id: string;
  reason: MapHygieneApplyFailureReason;
}

// Ответ POST /map-hygiene/apply: что применилось, что нет с причиной, новый sha карты
// (если правки легли) и полный свежий отчёт сканера — шапка перерисовывается из него
// без второго запроса (Р10.4 плана). Мнимой атомарности нет: applied[] и failed[] могут
// быть непустыми одновременно.
export interface MapHygieneApplyResult {
  applied: string[];
  failed: MapHygieneApplyFailure[];
  newSha: string | null;
  scan: MapHygieneReport;
}
